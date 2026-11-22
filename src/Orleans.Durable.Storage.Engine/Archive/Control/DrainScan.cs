using System.Diagnostics;

using Orleans.Durable.Storage.Engine.Ledgers;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// The rows a drain holds of one segment while it scans it (archive.md §8.1), whichever store it drains into, and the
/// drain memory they are charged to; the store's drain worker gives each batch's rows their form and charge.
/// </summary>
internal abstract class DrainRows(LedgerSegmentId id) : IDisposable
{
    /// <summary>Gets the segment, its ledger and start.</summary>
    public LedgerSegmentId Id { get; } = id;

    /// <summary>Gets the segment's end, once its scan reached it.</summary>
    public Slot End { get; internal set; }

    /// <summary>Gets a value indicating whether the drain holds the whole drain memory, alone.</summary>
    public bool Alone { get; internal set; }

    /// <summary>Gets the bytes the rows charged, which the drain memory holds for them.</summary>
    public long Charged { get; internal set; }

    /// <summary>Gets or sets the drain memory the rows hold, none before their first charge.</summary>
    internal ByteBudget.Lease? Lease { get; set; }

    /// <summary>Returns <paramref name="bytes"/> of the rows' charge, which rows they no longer hold took, to the drain memory.</summary>
    internal void Uncharge(long bytes)
    {
        Debug.Assert(bytes >= 0 && bytes <= Charged, "Rows return only bytes they charged.");
        Charged -= bytes;
        if (Lease is { } lease && !Alone)
        {
            lease.Shrink(Math.Min(bytes, lease.Bytes - 1));
        }
    }

    /// <summary>Releases the rows and the drain memory they hold.</summary>
    public void Dispose()
    {
        Clear();
        Lease?.Dispose();
        Lease = null;
        Charged = 0;
    }

    /// <summary>
    /// Adds a batch's resolved rows, retaining what they need, and returns their charge, less the charge of the rows they
    /// release, as an opener releases the bodies it supersedes; negative when they release more than they add.
    /// </summary>
    protected internal abstract long Add(LedgerScanItem item);

    /// <summary>Releases every row, as a drain which scans its segment again alone does first.</summary>
    protected internal abstract void Clear();
}

/// <summary>
/// The scan of one closed segment by a drain (archive.md §8.1, archive-lsm.md §8.1, archive-logs.md §8.1), which both
/// stores' drain workers share. A segment closes by evidence every process sees alike (ledgers.md §2.2): the next decided
/// batch with the segment-start flag, which the scan reads below a contiguous tail, or the closed ledger's <c>T</c>. The
/// scan resumes after backpressure from the slot it reached, keeping its segment view, under bounded jittered backoff.
/// </summary>
/// <remarks>
/// The drain memory, <c>DrainMemoryBytes</c>, is a hard bound for both stores: a scan charges each batch's rows as it
/// reads them; a scan holding nothing waits for its first charge; a scan whose next charge would pass what the budget has
/// free releases what it holds, waits for the whole budget, and scans the segment again alone; and a segment whose rows
/// pass the whole budget alone fails its drain with <see cref="DrainMemoryExceededException"/>, which names the ledger and
/// the segment. A scan which waited while it held bytes could deadlock with another, so it never does.
/// </remarks>
internal sealed class DrainScan
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(1);

    private readonly LedgerReader _reader;
    private readonly ByteBudget _budget;
    private readonly ResiliencePipeline<ScanEnd> _retry;
    private long _drainedAlone;

    /// <summary>Initializes a new instance of the <see cref="DrainScan"/> class.</summary>
    /// <param name="reader">Reads the ledgers' decided batches.</param>
    /// <param name="budget">The process's drain memory.</param>
    /// <param name="timeProvider">The clock of the backoff after backpressure.</param>
    /// <param name="backpressureRetries">How many times a scan resumes after backpressure before it answers it; at least 1.</param>
    /// <param name="backpressureDelay">The first wait before a scan resumes, which doubles, jittered, up to a second.</param>
    public DrainScan(LedgerReader reader, ByteBudget budget, TimeProvider timeProvider, int backpressureRetries, TimeSpan backpressureDelay)
    {
        _reader = reader;
        _budget = budget;
        _retry = new ResiliencePipelineBuilder<ScanEnd> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<ScanEnd>
            {
                ShouldHandle = static arguments => ValueTask.FromResult(arguments.Outcome.Exception is null && arguments.Outcome.Result.Value is ReplicaWriteOutcome.Backpressure),
                MaxRetryAttempts = backpressureRetries,
                DelayGenerator = arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, backpressureDelay, MaximumDelay, Random.Shared.NextDouble())),
            })
            .Build();
    }

    /// <summary>Gets how many segments a scan drained alone, holding the whole drain memory, which the composition reports.</summary>
    public long DrainedAlone => Interlocked.Read(ref _drainedAlone);

    /// <summary>Gets the drain memory no drain holds; for tests.</summary>
    public long Available => _budget.Available;

    /// <summary>
    /// Scans the segment of <paramref name="ledger"/> which starts at <paramref name="rows"/>' start, below
    /// <paramref name="below"/>, adding each batch's rows to <paramref name="rows"/>, charged to the drain memory.
    /// </summary>
    /// <param name="ledger">The ledger, as a ledger map value the caller read lists it.</param>
    /// <param name="rows">The segment's rows, empty, which the caller owns and disposes.</param>
    /// <param name="below">The slot below which the scan may read.</param>
    /// <param name="boundIsEnd">Whether <paramref name="below"/> is the closed ledger's <c>T</c>, which closes the segment.</param>
    /// <param name="notAStart">The failure of a start whose batch does not start a segment, which the scan throws.</param>
    /// <param name="bySegment">Whether the scan stops at the next segment's start; false only for a mutant which drains inside segments.</param>
    /// <param name="cancellationToken">Stops the scan.</param>
    /// <returns>
    /// The segment's end, with <paramref name="rows"/> holding its rows; or why the scan reached none:
    /// <see cref="Open"/>, the segment does not close below <paramref name="below"/>; the ledger's
    /// <see cref="ReplicaWriteOutcome.Dropped"/>; or <see cref="ReplicaWriteOutcome.Backpressure"/> past the retries.
    /// </returns>
    /// <exception cref="DrainMemoryExceededException">The segment's rows pass the whole drain memory alone.</exception>
    /// <exception cref="Exception"><paramref name="notAStart"/>'s: the segment's start holds a batch which starts no segment.</exception>
    public async Task<DrainScanOutcome> ScanAsync(LedgerMetadata ledger, DrainRows rows, Slot below, bool boundIsEnd, Func<Exception> notAStart, bool bySegment, CancellationToken cancellationToken)
    {
        var end = await ScanWithRetriesAsync(ledger, rows, below, boundIsEnd, notAStart, bySegment, cancellationToken).ConfigureAwait(false);
        if (end.Value is OverBudget)
        {
            // A drain which waited while it held bytes could deadlock with another: it releases them, then holds them all.
            rows.Dispose();
            rows.Lease = await _budget.AcquireAsync(_budget.Capacity, cancellationToken).ConfigureAwait(false);
            rows.Alone = true;
            _ = Interlocked.Increment(ref _drainedAlone);
            end = await ScanWithRetriesAsync(ledger, rows, below, boundIsEnd, notAStart, bySegment, cancellationToken).ConfigureAwait(false);
        }

        switch (end.Value)
        {
            case Slot slot:
                rows.End = slot;
                return slot;
            case Open open:
                return open;
            case ReplicaWriteOutcome.Dropped dropped:
                return dropped;
            case ReplicaWriteOutcome.Backpressure backpressure:
                return backpressure;
            case OverBudget:
            case null:
            default:
                throw new UnreachableException("A scan holding the whole drain memory throws rather than passing it.");
        }
    }

    private async Task<ScanEnd> ScanWithRetriesAsync(LedgerMetadata ledger, DrainRows rows, Slot below, bool boundIsEnd, Func<Exception> notAStart, bool bySegment, CancellationToken cancellationToken)
    {
        using var scan = new ResumableScan(ledger.Id, rows.Id.Start);
        return await _retry.ExecuteAsync(
            static (state, token) => state.Scanner.ScanPassAsync(state.Ledger, state.Rows, state.Below, state.BoundIsEnd, state.NotAStart, state.BySegment, state.Scan, token),
            (Scanner: this, Ledger: ledger, Rows: rows, Below: below, BoundIsEnd: boundIsEnd, NotAStart: notAStart, BySegment: bySegment, Scan: scan),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans from where <paramref name="scan"/> reached, stopping at the next batch past the segment's start which starts a
    /// segment, the segment's end, and adds each batch's rows, charging them.
    /// </summary>
    private async ValueTask<ScanEnd> ScanPassAsync(LedgerMetadata ledger, DrainRows rows, Slot below, bool boundIsEnd, Func<Exception> notAStart, bool bySegment, ResumableScan scan, CancellationToken cancellationToken)
    {
        var start = rows.Id.Start;
        await foreach (var item in _reader.ScanAsync(ledger, scan.Next, below, scan.View, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var read = item.Read;
            if (read.TryGetValue(out LedgerRead.Data data))
            {
                using (data)
                {
                    var isStart = data.View.IsSegmentStart;
                    if (bySegment && data.Slot == start && !isStart)
                    {
                        throw notAStart();
                    }

                    if (bySegment && data.Slot > start && isStart)
                    {
                        return data.Slot;
                    }

                    // A batch whose rows supersede more than they add returns the difference to the drain memory.
                    var charge = rows.Add(item);
                    if (charge < 0)
                    {
                        rows.Uncharge(-charge);
                        charge = 0;
                    }

                    if (!await ChargeAsync(rows, charge, cancellationToken).ConfigureAwait(false))
                    {
                        return OverBudget.Instance;
                    }

                    scan.Next = data.Slot + 1;
                }

                continue;
            }

            if (read.TryGetValue(out ReplicaWriteOutcome.Dropped? dropped))
            {
                return dropped;
            }

            return ReplicaWriteOutcome.Backpressure.Instance;
        }

        return boundIsEnd || !bySegment ? below : Open.Instance;
    }

    /// <summary>
    /// Charges <paramref name="bytes"/> to the drain memory: holding none, the rows wait their turn; holding some, they grow
    /// without waiting, answering false when they cannot; holding the whole budget, they throw once they pass it.
    /// </summary>
    /// <exception cref="DrainMemoryExceededException">The rows pass the whole budget alone.</exception>
    private async ValueTask<bool> ChargeAsync(DrainRows rows, long bytes, CancellationToken cancellationToken)
    {
        rows.Charged += bytes;
        if (rows.Alone)
        {
            return rows.Charged <= _budget.Capacity ? true : throw new DrainMemoryExceededException(rows.Id, rows.Charged, _budget.Capacity);
        }

        if (bytes == 0)
        {
            return true;
        }

        if (rows.Lease is { } lease)
        {
            return lease.TryGrow(bytes);
        }

        if (bytes > _budget.Capacity)
        {
            return false;
        }

        rows.Lease = await _budget.AcquireAsync(bytes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The segment does not close below the scan's bound.</summary>
    public sealed record Open
    {
        private Open()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Open Instance { get; } = new();
    }

    /// <summary>A scan's rows would pass the drain memory beside the other drains.</summary>
    private sealed record OverBudget
    {
        private OverBudget()
        {
        }

        public static OverBudget Instance { get; } = new();
    }

    /// <summary>Where a pass of a segment's scan stopped: at the segment's end, short of it, at <c>Dropped</c> or <c>Backpressure</c>, or past the drain memory.</summary>
    private readonly union ScanEnd(Slot, Open, ReplicaWriteOutcome.Dropped, ReplicaWriteOutcome.Backpressure, OverBudget);

    /// <summary>A segment's scan across its passes: its segment view, held so a resumed pass rewinds nothing, and the next slot.</summary>
    private sealed class ResumableScan(LedgerId ledger, Slot start) : IDisposable
    {
        public LedgerSegmentView View { get; } = new(ledger);

        public Slot Next { get; set; } = start;


        public void Dispose() => View.Dispose();
    }
}

/// <summary>What a segment's scan gives (conventions.md §15): its end, or why it reached none.</summary>
internal readonly union DrainScanOutcome(Slot, DrainScan.Open, ReplicaWriteOutcome.Dropped, ReplicaWriteOutcome.Backpressure);
