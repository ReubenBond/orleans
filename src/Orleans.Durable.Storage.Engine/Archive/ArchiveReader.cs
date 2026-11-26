using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// Reads a key's history across the live ledgers and the archive store they drain into, and the store's first-touch
/// answer (archive.md §2.2, §8.1, §8.2). One per process, over the process's
/// one <see cref="LedgerReader"/>, which its writer registry shares, and the cluster's archive store.
/// </summary>
/// <remarks>
/// <para>
/// A read takes the reader's cached map value, starts the store read with it, and meanwhile reads every live ledger the
/// value lists whose range contains the key, together, with the open ledger's <c>K</c> reads in flight in all: each closed
/// ledger to its recorded <c>T</c>, and the open ledger to its known contiguous tail in this process (or, when a
/// <see cref="ArchiveKeyIndex"/> is supplied, waits for each indexed ledger's catch-up and point-reads only the key's
/// indexed slots and their defining batches). Once the store has answered, it checks the open ledger's seal and finishes
/// the open ledger to the check's tail (LG-I8), so the prefix holds no command beyond the end the read reached, and joins
/// the ledgers' rows in ledger and slot order. When an unindexed store read has already answered
/// <see cref="ArchiveStoreRead.Absent"/> with per-ledger bounds, each unindexed scan starts at its ledger's bound rather
/// than slot 0. The map is read before the store, a ledger leaves the map only once the store covers it (HI-I2), and the
/// store's coverage only grows, so the store covers every ledger the value no longer lists (HI-I1). The merge marks the
/// rows the prefix holds by the store's <c>Holds</c> and checks the token chain and versions of the rows it does not
/// (HI-I4).
/// </para>
/// <para>
/// A ledger has one <c>Ledger</c> per process, which the writer registry's writer shares with the reader, so in the
/// process which serves the open ledger's writer the seal check's tail covers every proposal that writer answered
/// durable, and the read is <see cref="ArchiveRead.Linearizable"/>. Elsewhere it reads the open ledger below a contiguous
/// tail, which may lag the primary's answers.
/// </para>
/// <para>
/// A read starts again when a cache proves stale: when the store answers <see cref="ArchiveStoreRead.Stale"/>, after
/// reading the map again; when a ledger it scans is dropped or the open one is sealed, once the map was read again. One
/// whose map advanced starts again at once; others wait with bounded, jittered, exponential backoff, at least 20 ms and
/// at most 1 s, whose attempts count only those restarts. Backpressure on a replica read or the seal check delays that
/// read with the same backoff, and the scan resumes from its slot. Every other exception propagates.
/// </para>
/// </remarks>
public sealed class ArchiveReader
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(1);

    private readonly LedgerReader _reader;
    private readonly LedgerMapView _map;
    private readonly IArchiveStore _store;
    private readonly ArchiveKeyIndex? _keyIndex;
    private readonly ResiliencePipeline<ArchiveRead?> _readRestarts;
    private readonly ResiliencePipeline<ArchiveStoreAnswer?> _storeRestarts;
    private readonly ResiliencePipeline<LedgerTailRead> _tailRetry;
    private readonly ResiliencePipeline<ScanPass> _scanRetry;
    private readonly ResiliencePipeline<LedgerRead> _pointRetry;

    /// <summary>Initializes a new instance of the <see cref="ArchiveReader"/> class. Construction performs no I/O.</summary>
    /// <param name="reader">
    /// The process's one ledger reader, which its writer registry shares, so that a seal check here covers the proposals of
    /// the writers this process serves; the history reader takes its map view.
    /// </param>
    /// <param name="store">The cluster's archive store.</param>
    /// <param name="timeProvider">The clock of the backoff.</param>
    public ArchiveReader(LedgerReader reader, IArchiveStore store, TimeProvider timeProvider)
        : this(reader, store, timeProvider, keyIndex: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveReader"/> class using <paramref name="keyIndex"/> to point-read
    /// only a key's indexed slots in live ledgers (archive.md §8.1, journals.md §8.3). Construction performs no I/O.
    /// </summary>
    /// <param name="reader">
    /// The process's one ledger reader, which its writer registry shares, so that a seal check here covers the proposals of
    /// the writers this process serves; the history reader takes its map view.
    /// </param>
    /// <param name="store">The cluster's archive store.</param>
    /// <param name="timeProvider">The clock of the backoff.</param>
    /// <param name="keyIndex">The node's per-ledger key index, or <see langword="null"/> to scan live ledgers.</param>
    public ArchiveReader(LedgerReader reader, IArchiveStore store, TimeProvider timeProvider, ArchiveKeyIndex? keyIndex)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _reader = reader;
        _map = reader.Map;
        _store = store;
        _keyIndex = keyIndex;
        _readRestarts = Retry<ArchiveRead?>(timeProvider, static outcome => outcome is null, null);
        _storeRestarts = Retry<ArchiveStoreAnswer?>(timeProvider, static outcome => outcome is null, null);
        _tailRetry = Retry<LedgerTailRead>(timeProvider, static outcome => outcome.Value is ReplicaWriteOutcome.Backpressure, Counts.Backpressured);
        _scanRetry = Retry<ScanPass>(timeProvider, static outcome => outcome == ScanPass.Backpressured, Counts.Backpressured);
        _pointRetry = Retry<LedgerRead>(timeProvider, static outcome => outcome.Value is ReplicaWriteOutcome.Backpressure, Counts.Backpressured);
    }

    /// <summary>Gets how often reads started again, by cause, and resumed after backpressure; for tests.</summary>
    internal ArchiveReadCounts Counts { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the reader reads the store before it takes the map value: the mutant of
    /// archive.md §10's read-order test, which tests set to show the oracle catches it.
    /// </summary>
    internal bool MapAfterStoreMutant { get; set; }

    /// <summary>Gets or sets the mutant the reader's merges run as, which tests set to show the oracles catch it.</summary>
    internal ArchiveMerge.Mutant MergeMutant { get; set; }

    /// <summary>
    /// Reads <paramref name="key"/>'s history (archive.md §2.1, §8.1): the store's prefix, and every live row of the key
    /// from slot 0 of each live ledger to its decided end, each marked held when the prefix holds it, with the token chain
    /// and versions of the rows not held checked (HI-I1, HI-I4).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Stops the read, also while it waits for a sealed ledger to close in the map.</param>
    /// <returns>The read, which its caller disposes.</returns>
    /// <exception cref="ArchiveCorruptException">
    /// The store contradicts itself, or a stored token or version of a row not held breaks the key's chain.
    /// </exception>
    /// <exception cref="InvalidDataException">A batch read from a live ledger breaks its format: corruption.</exception>
    public ValueTask<ArchiveRead> ReadAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        ReadAsync(key, includeBodies: true, cancellationToken);

    /// <summary>
    /// Reads <paramref name="key"/>'s history as <see cref="ReadAsync(ReadOnlyMemory{byte}, CancellationToken)"/> does,
    /// optionally omitting live non-<c>Delete</c> row bodies when <paramref name="includeBodies"/> is <see langword="false"/>
    /// so metadata-only callers (such as Journals' warm-state load) avoid copying payloads they only measure.
    /// </summary>
    public ValueTask<ArchiveRead> ReadAsync(ReadOnlyMemory<byte> key, bool includeBodies, CancellationToken cancellationToken = default) =>
        includeBodies
            ? RunAsync(_readRestarts, static (reader, key, token) => reader.SettleAsync(key, token, static (reader, key, token) => reader.TryReadAsync(key, includeBodies: true, token)), key, cancellationToken)
            : RunAsync(_readRestarts, static (reader, key, token) => reader.SettleAsync(key, token, static (reader, key, token) => reader.TryReadAsync(key, includeBodies: false, token)), key, cancellationToken);

    /// <summary>
    /// Reads the store's answer for <paramref name="key"/>, from one read of the store with a map value read before it,
    /// which the answer pairs (archive.md §2.2, §8.2): a journal's first touch. It reads both again while the store answers
    /// <see cref="ArchiveStoreRead.Stale"/>.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The answer, which its caller disposes.</returns>
    /// <exception cref="ArchiveCorruptException">The store contradicts itself.</exception>
    public ValueTask<ArchiveStoreAnswer> ReadStoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        RunAsync(_storeRestarts, static (reader, key, token) => reader.SettleAsync(key, token, static (reader, key, token) => reader.TryReadStoreAsync(key, token)), key, cancellationToken);

    /// <summary>
    /// Repeats an operation with bounded, jittered, exponential backoff while <paramref name="repeats"/> holds for its
    /// answer, counting each repetition in <paramref name="counter"/> when one is given.
    /// </summary>
    internal static ResiliencePipeline<T> Retry<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(TimeProvider timeProvider, Func<T, bool> repeats, ArchiveReadCounts.Counter? counter) =>
        new ResiliencePipelineBuilder<T> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<T>
            {
                ShouldHandle = arguments => ValueTask.FromResult(arguments.Outcome.Exception is null && repeats(arguments.Outcome.Result!)),
                OnRetry = _ =>
                {
                    counter?.Increment();
                    return default;
                },
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, BaseDelay, MaximumDelay, Random.Shared.NextDouble())),
            })
            .Build();

    private static Slot ClosedEnd(LedgerMetadata ledger) =>
        ledger.Status.Value is LedgerStatus.Closed closed ? closed.T : throw new UnreachableException("A lookup's closed ledgers are closed.");

    /// <summary>Releases the answer a store read gives, once it settles, when the attempt no longer needs it.</summary>
    private static async ValueTask ReleaseAsync(Task<ArchiveStoreAnswer?> task)
    {
        try
        {
            (await task.ConfigureAwait(false))?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The attempt's own outcome stands.
        }
    }

    /// <summary>
    /// Adds the key's resolved rows of one scanned batch, in the view's segment, to <paramref name="scan"/>, copying their
    /// bodies into the scan's own buffer (or keeping only lengths for non-<c>Delete</c> rows when
    /// <paramref name="includeBodies"/> is <see langword="false"/>).
    /// </summary>
    private static void Collect(LedgerScanItem item, LedgerSegmentId segment, LedgerScan scan, bool includeBodies)
    {
        foreach (var row in item.Rows)
        {
            if (row.Kind == RowKind.Message)
            {
                // LG-I15: message rows are excluded from ArchiveReader journal folds.
                continue;
            }

            scan.AddLive(ArchiveRow.Of(row, segment, scan.GetBodiesWriter(), includeBodies));
        }
    }

    /// <summary>Runs <paramref name="attempt"/> under <paramref name="restarts"/>, which repeat it with backoff while it answers null.</summary>
    private async ValueTask<T> RunAsync<T>(
        ResiliencePipeline<T?> restarts,
        Func<ArchiveReader, ReadOnlyMemory<byte>, CancellationToken, ValueTask<T?>> attempt,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
        where T : class
    {
        if (await attempt(this, key, cancellationToken).ConfigureAwait(false) is { } first)
        {
            return first;
        }

        return await RunRetryAsync(restarts, attempt, key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> RunRetryAsync<T>(
        ResiliencePipeline<T?> restarts,
        Func<ArchiveReader, ReadOnlyMemory<byte>, CancellationToken, ValueTask<T?>> attempt,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
        where T : class
    {
        var firstAttempt = true;
        var value = await restarts.ExecuteAsync(
            (state, token) =>
            {
                if (firstAttempt)
                {
                    firstAttempt = false;
                    return ValueTask.FromResult<T?>(null);
                }

                return state.Attempt(state.Reader, state.Key, token);
            },
            (Reader: this, Key: key, Attempt: attempt),
            cancellationToken).ConfigureAwait(false);
        Debug.Assert(value is not null, "The restarts end only with an answer.");
        return value;
    }

    /// <summary>
    /// Repeats <paramref name="attempt"/> at once while it starts again after the map view advanced past the value it
    /// took, since every restart reads the map first, and answers its value, or null for a restart whose map did not
    /// advance, which waits with backoff.
    /// </summary>
    private async ValueTask<T?> SettleAsync<T>(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken,
        Func<ArchiveReader, ReadOnlyMemory<byte>, CancellationToken, ValueTask<T?>> attempt)
        where T : class
    {
        while (true)
        {
            var epoch = _map.Current.Epoch;
            if (await attempt(this, key, cancellationToken).ConfigureAwait(false) is { } value)
            {
                return value;
            }

            if (_map.Current.Epoch == epoch)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Reads the store with <paramref name="map"/>: its answer paired with the map value, or null when it answered
    /// <see cref="ArchiveStoreRead.Stale"/>. The one match on a store's answer.
    /// </summary>
    private async ValueTask<ArchiveStoreAnswer?> ReadStoreAnswerAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        var read = await _store.ReadAsync(key, map, cancellationToken).ConfigureAwait(false);
        Debug.Assert(read.Value is not null, "A store answers a union which holds a case.");
        return read switch
        {
            ArchiveStoreRead.Absent absent => new ArchiveStoreAnswer(_store, map, null, absent.Bounds, MergeMutant),
            ArchiveStoreRead.Read stored => new ArchiveStoreAnswer(_store, map, stored.Prefix, mutant: MergeMutant),
            ArchiveStoreRead.Stale => null,
            null => throw new UnreachableException(),
        };
    }

    private async ValueTask<ArchiveStoreAnswer?> TryReadStoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var map = _map.Current;
        var answer = await ReadStoreAnswerAsync(key, map, cancellationToken).ConfigureAwait(false);
        if (answer is not null)
        {
            return answer;
        }

        Counts.Stale.Increment();
        _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// One attempt of a history read (archive.md §8.1): the cached map value; the store read with it, while every live
    /// ledger's scan from slot 0 runs, together, with the open ledger's <c>K</c> reads in flight in all: each closed
    /// ledger's to its <c>T</c>, and the open ledger's to its known contiguous tail in this process; once the store has
    /// answered, the open ledger's seal check, and its scan extended to the check's tail; and the merge by <c>Holds</c> of
    /// the rows joined in ledger and slot order, which checks the chain of the rows not held; or a restart, when a cache
    /// proved stale.
    /// </summary>
    private async ValueTask<ArchiveRead?> TryReadAsync(ReadOnlyMemory<byte> key, bool includeBodies, CancellationToken cancellationToken)
    {
        using var early = MapAfterStoreMutant ? await ReadStoreAnswerAsync(key, _map.Current, cancellationToken).ConfigureAwait(false) : null;

        // 1. A ledger map value, which the store is read after (HI-I1). A key no ledger contains is answered only from a
        // value read after the call.
        var map = _map.Current;
        var lookup = map.Find(key.Span);
        if (lookup is null)
        {
            if (await ReadMapAsync(map, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var noLedgerAnswer = early ?? await ReadStoreAnswerAsync(key, map, cancellationToken).ConfigureAwait(false);
            using var ownedNoLedgerAnswer = early is null ? noLedgerAnswer : null;
            if (noLedgerAnswer is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Counts.Stale.Increment();
                _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await SettleAsync(noLedgerAnswer.MergeOwned([], EmptyLedgerThrough.Instance, openLedger: null, linearizable: false), map, EmptyLedgerThrough.Instance, cancellationToken).ConfigureAwait(false);
        }

        if (lookup.Value.Closed.Length == 0)
        {
            return await TryReadSingleOpenLedgerAsync(early, map, lookup.Value.Open, key, includeBodies, cancellationToken).ConfigureAwait(false);
        }

        // 2. Multi-ledger (closed + open): read the store with that value, and meanwhile run every live ledger's scan from
        // slot 0 together, bounded by the open ledger's K reads in flight.
        var listed = lookup.Value;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reading = early is not null ? Task.FromResult<ArchiveStoreAnswer?>(early) : ReadStoreAnswerAsync(key, map, attempt.Token).AsTask();
        var immediateAnswer = reading.IsCompletedSuccessfully ? reading.Result : null;
        var pendingStore = reading.IsCompletedSuccessfully ? null : reading;
        var settled = false;
        var through = new Dictionary<LedgerId, Slot>(listed.Closed.Length + 1);
        using var window = new SemaphoreSlim(listed.Open.K);
        var scans = new List<LedgerScan>(listed.Closed.Length + 1);
        try
        {
            var useIndex = _keyIndex is not null;
            foreach (var ledger in listed.Closed)
            {
                var closedEnd = ClosedEnd(ledger);
                through[ledger.Id] = closedEnd;
                var bound = immediateAnswer?.AbsentBound(ledger.Id) ?? Slot.Zero;
                scans.Add(new LedgerScan(this, ledger, closedEnd, key, window, bound, useIndex, includeBodies));
            }

            var openBound = immediateAnswer?.AbsentBound(listed.Open.Id) ?? Slot.Zero;
            var openBelow = useIndex ? Slot.Zero : _reader.KnownContiguousTail(listed.Open);
            scans.Add(new LedgerScan(this, listed.Open, openBelow, key, window, openBound, useIndex, includeBodies));

            var reached = await ScanTogetherAsync(scans, pendingStore, attempt).ConfigureAwait(false);
            settled = true;
            ArchiveStoreAnswer? storeAnswer;
            try
            {
                storeAnswer = await reading.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!reached && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            using var ownedAnswer = early is null ? storeAnswer : null;
            if (storeAnswer is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Counts.Stale.Increment();
                _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!reached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            // 3. The open ledger's decided end, from a seal check begun once the store answered, so the prefix holds no
            // command beyond it; and its rows finished to that tail (via key index or scan from its bound / last slot).
            var tail = await ReadTailAsync(listed.Open, cancellationToken).ConfigureAwait(false);
            if (tail.IsSealed)
            {
                Counts.Sealed.Increment();
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            through[listed.Open.Id] = tail.ContiguousTail;
            scans[^1].Below = tail.ContiguousTail;
            scans[^1].ApplyAbsentBound(storeAnswer.AbsentBound(listed.Open.Id));
            if (!await ScanAsync(scans[^1], reading: null, cancellationToken).ConfigureAwait(false))
            {
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            // 4. The rows joined in ledger and slot order, each marked held by Holds, and the chain of the rows not held
            // checked (HI-I4). When a newer ledger's indexed read stopped at a verified tokenless opener (LSM), older
            // ledgers cannot affect the key's state and are omitted so HI-I4 version continuity starts cleanly at the base.
            var firstScan = 0;
            for (var index = scans.Count - 1; index >= 0; index--)
            {
                if (scans[index].FoundCompleteBase)
                {
                    firstScan = index;
                    break;
                }
            }

            var totalRows = 0;
            for (var index = firstScan; index < scans.Count; index++)
            {
                totalRows += scans[index].LiveCount;
            }

            if (totalRows == 0)
            {
                return await SettleAsync(storeAnswer.MergeOwned([], through, listed.Open.Id, tail.ServedHere), map, through, cancellationToken).ConfigureAwait(false);
            }

            var live = new List<ArchiveRow>(totalRows);
            for (var index = firstScan; index < scans.Count; index++)
            {
                scans[index].CopyLiveTo(live);
            }

            return await SettleAsync(storeAnswer.MergeOwned(CollectionsMarshal.AsSpan(live), through, listed.Open.Id, tail.ServedHere), map, through, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var scan in scans)
            {
                scan.Dispose();
            }

            if (!settled)
            {
                await attempt.CancelAsync().ConfigureAwait(false);
                await ReleaseAsync(reading).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Single open-ledger path (no closed ledgers covering the key): avoids allocating a <see cref="Dictionary{TKey,TValue}"/>,
    /// <see cref="SemaphoreSlim"/>, <see cref="List{T}"/> of scans, and (when no step-2 scan runs or the store answers
    /// synchronously) <see cref="CancellationTokenSource"/> / <see cref="Task"/> boxing.
    /// </summary>
    private async ValueTask<ArchiveRead?> TryReadSingleOpenLedgerAsync(
        ArchiveStoreAnswer? early,
        LedgerMapValue map,
        LedgerMetadata open,
        ReadOnlyMemory<byte> key,
        bool includeBodies,
        CancellationToken cancellationToken)
    {
        var useIndex = _keyIndex is not null;
        var openBelow = useIndex ? Slot.Zero : _reader.KnownContiguousTail(open);
        if (openBelow == Slot.Zero)
        {
            var storeAnswer = early ?? await ReadStoreAnswerAsync(key, map, cancellationToken).ConfigureAwait(false);
            using var ownedAnswer = early is null ? storeAnswer : null;
            if (storeAnswer is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Counts.Stale.Increment();
                _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await FinishSingleOpenLedgerAsync(storeAnswer, map, open, key, openScan: null, useIndex, includeBodies, cancellationToken).ConfigureAwait(false);
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var storeValueTask = early is not null ? ValueTask.FromResult<ArchiveStoreAnswer?>(early) : ReadStoreAnswerAsync(key, map, attempt.Token);
        if (storeValueTask.IsCompletedSuccessfully)
        {
            var syncAnswer = await storeValueTask.ConfigureAwait(false);
            using var ownedSyncAnswer = early is null ? syncAnswer : null;
            if (syncAnswer is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Counts.Stale.Increment();
                _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await FinishSingleOpenLedgerAsync(syncAnswer, map, open, key, openScan: null, useIndex: false, includeBodies, cancellationToken).ConfigureAwait(false);
        }

        var reading = storeValueTask.AsTask();
        var settled = false;
        using var openScan = new LedgerScan(this, open, openBelow, key, window: null, Slot.Zero, useIndex: false, includeBodies);
        try
        {
            var reached = false;
            try
            {
                reached = await ScanAsync(openScan, reading, attempt.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!reached)
                {
                    await attempt.CancelAsync().ConfigureAwait(false);
                }
            }

            settled = true;
            ArchiveStoreAnswer? storeAnswer;
            try
            {
                storeAnswer = await reading.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!reached && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            using var ownedAnswer = early is null ? storeAnswer : null;
            if (storeAnswer is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Counts.Stale.Increment();
                _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!reached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await FinishSingleOpenLedgerAsync(storeAnswer, map, open, key, openScan, useIndex: false, includeBodies, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!settled)
            {
                await attempt.CancelAsync().ConfigureAwait(false);
                await ReleaseAsync(reading).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ArchiveRead?> FinishSingleOpenLedgerAsync(
        ArchiveStoreAnswer answer,
        LedgerMapValue map,
        LedgerMetadata open,
        ReadOnlyMemory<byte> key,
        LedgerScan? openScan,
        bool useIndex,
        bool includeBodies,
        CancellationToken cancellationToken)
    {
        var tail = await ReadTailAsync(open, cancellationToken).ConfigureAwait(false);
        if (tail.IsSealed)
        {
            Counts.Sealed.Increment();
            await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var through = new SingleLedgerThrough(open.Id, tail.ContiguousTail);
        var openBound = answer.AbsentBound(open.Id);
        if (openScan is null)
        {
            if (openBound >= tail.ContiguousTail)
            {
                return await SettleAsync(answer.MergeOwned([], through, open.Id, tail.ServedHere), map, through, cancellationToken).ConfigureAwait(false);
            }

            using var scan = new LedgerScan(this, open, tail.ContiguousTail, key, window: null, openBound, useIndex, includeBodies);
            if (!await ScanAsync(scan, reading: null, cancellationToken).ConfigureAwait(false))
            {
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await SettleAsync(answer.MergeOwned(scan.LiveSpan, through, open.Id, tail.ServedHere), map, through, cancellationToken).ConfigureAwait(false);
        }

        openScan.Below = tail.ContiguousTail;
        openScan.ApplyAbsentBound(openBound);
        if (!await ScanAsync(openScan, reading: null, cancellationToken).ConfigureAwait(false))
        {
            await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await SettleAsync(answer.MergeOwned(openScan.LiveSpan, through, open.Id, tail.ServedHere), map, through, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a merge's outcome: its read; a restart when the prefix reached past the scans' end (archive.md §8.1 step
    /// 4); and, for a break of the key's chain, a restart once a map read after it no longer lists a ledger the read
    /// scanned, which a legal rebase after that ledger's drop explains, or otherwise the corruption.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The chain broke while the map still lists every ledger the read scanned.</exception>
    private async ValueTask<ArchiveRead?> SettleAsync(ArchiveMergeOutcome outcome, LedgerMapValue map, IReadOnlyDictionary<LedgerId, Slot> through, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case ArchiveRead read:
                return read;
            case ArchiveMergeOutcome.PastEnd:
                Counts.PastEnd.Increment();
                await RereadMapUnlessAdvancedAsync(map, cancellationToken).ConfigureAwait(false);
                return null;
            case ArchiveCorruptException corruption:
                var current = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (through.Keys.Any(current.NoLongerLists))
                {
                    Counts.Dropped.Increment();
                    return null;
                }

                throw corruption;
            case null:
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>
    /// Runs one pass of every scan at once, and cancels the rest once one ends without reaching its end: answers whether
    /// every scan reached its end, or false when a ledger was dropped. Once every pass has ended, the first failure in
    /// ledger order propagates, and otherwise a cancellation.
    /// </summary>
    private async ValueTask<bool> ScanTogetherAsync(List<LedgerScan> scans, Task<ArchiveStoreAnswer?>? reading, CancellationTokenSource attempt)
    {
        var passes = new Task<bool>[scans.Count];
        for (var index = 0; index < passes.Length; index++)
        {
            passes[index] = PassAsync(scans[index], reading, attempt);
        }

        await ((Task)Task.WhenAll(passes)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (Array.Find(passes, static pass => pass.IsFaulted) is { } failed)
        {
            ExceptionDispatchInfo.Throw(failed.Exception!.InnerException!);
        }

        if (Array.Exists(passes, static pass => pass.IsCompletedSuccessfully && !pass.Result))
        {
            return false;
        }

        foreach (var pass in passes)
        {
            _ = await pass.ConfigureAwait(false);
        }

        return true;

        async Task<bool> PassAsync(LedgerScan scan, Task<ArchiveStoreAnswer?>? reading, CancellationTokenSource attempt)
        {
            var reached = false;
            try
            {
                reached = await ScanAsync(scan, reading, attempt.Token).ConfigureAwait(false);
                return reached;
            }
            finally
            {
                if (!reached)
                {
                    await attempt.CancelAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="scan"/>'s ledger to its end, collecting the key's rows: via the key index (point-reading only
    /// the key's indexed slots and their defining batches) when indexed, or by scanning from its start slot (resuming on
    /// backpressure after backoff) when unindexed.
    /// </summary>
    /// <returns>Whether the read reached its end; false when the ledger was dropped.</returns>
    private async ValueTask<bool> ScanAsync(LedgerScan scan, Task<ArchiveStoreAnswer?>? reading, CancellationToken cancellationToken)
    {
        if (scan.Next >= scan.Below)
        {
            return true;
        }

        if (scan.UseIndex)
        {
            var outcome = await scan.RunIndexedAsync(cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case ScanPass.Reached:
                    return true;
                case ScanPass.Dropped:
                    Counts.Dropped.Increment();
                    return false;
                case ScanPass.Unindexed:
                    if (reading is not null)
                    {
                        var store = await reading.ConfigureAwait(false);
                        if (store is null)
                        {
                            return false;
                        }

                        scan.ApplyAbsentBound(store.AbsentBound(scan.LedgerId));
                        if (scan.Next >= scan.Below)
                        {
                            return true;
                        }
                    }

                    break;
                case ScanPass.Backpressured:
                    throw new UnreachableException("An indexed read retries backpressure.");
                default:
                    throw new UnreachableException("An indexed read ends in one of its cases.");
            }
        }
        else if (reading is { IsCompletedSuccessfully: true, Result: { } completed })
        {
            scan.ApplyAbsentBound(completed.AbsentBound(scan.LedgerId));
            if (scan.Next >= scan.Below)
            {
                return true;
            }
        }

        var first = await scan.RunAsync(cancellationToken).ConfigureAwait(false);
        if (first == ScanPass.Reached)
        {
            return true;
        }

        if (first == ScanPass.Dropped)
        {
            Counts.Dropped.Increment();
            return false;
        }

        return await ScanRetryAsync(scan, first, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ScanRetryAsync(LedgerScan scan, ScanPass first, CancellationToken cancellationToken)
    {
        var useFirst = true;
        if (await _scanRetry.ExecuteAsync(
            (target, token) =>
            {
                if (useFirst)
                {
                    useFirst = false;
                    return ValueTask.FromResult(first);
                }

                return target.RunAsync(token);
            },
            scan,
            cancellationToken).ConfigureAwait(false) == ScanPass.Dropped)
        {
            Counts.Dropped.Increment();
            return false;
        }

        return true;
    }

    /// <summary>Reads one slot of <paramref name="ledger"/>, retrying backpressure with backoff and releasing <paramref name="window"/> across backoff sleeps.</summary>
    private async ValueTask<LedgerRead> ReadSlotAsync(LedgerMetadata ledger, Slot slot, SemaphoreSlim? window, CancellationToken cancellationToken)
    {
        Counts.PointReads.Increment();
        LedgerRead first;
        if (window is null)
        {
            first = await _reader.ReadAsync(ledger, slot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await window.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                first = await _reader.ReadAsync(ledger, slot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = window.Release();
            }
        }

        if (!first.TryGetValue(out ReplicaWriteOutcome.Backpressure? _))
        {
            return first;
        }

        return await ReadSlotRetryAsync(first, ledger, slot, window, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<LedgerRead> ReadSlotRetryAsync(
        LedgerRead first,
        LedgerMetadata ledger,
        Slot slot,
        SemaphoreSlim? window,
        CancellationToken cancellationToken)
    {
        var useFirst = true;
        return _pointRetry.ExecuteAsync(
            async (state, token) =>
            {
                if (useFirst)
                {
                    useFirst = false;
                    return first;
                }

                if (state.Window is not null)
                {
                    await state.Window.WaitAsync(token).ConfigureAwait(false);
                }

                try
                {
                    return await state.Reader.ReadAsync(state.Ledger, state.Slot, token).ConfigureAwait(false);
                }
                finally
                {
                    _ = state.Window?.Release();
                }
            },
            (Reader: _reader, Ledger: ledger, Slot: slot, Window: window),
            cancellationToken);
    }

    /// <summary>Checks the open ledger's seal, repeating the check with backoff while backpressure keeps it from deciding.</summary>
    private async ValueTask<LedgerTail> ReadTailAsync(LedgerMetadata open, CancellationToken cancellationToken)
    {
        var first = await _reader.ReadTailAsync(open, cancellationToken).ConfigureAwait(false);
        return first is LedgerTail fastTail
            ? fastTail
            : await ReadTailRetryAsync(first, open, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LedgerTail> ReadTailRetryAsync(LedgerTailRead first, LedgerMetadata open, CancellationToken cancellationToken)
    {
        var useFirst = true;
        var answer = await _tailRetry.ExecuteAsync(
            (state, token) =>
            {
                if (useFirst)
                {
                    useFirst = false;
                    return ValueTask.FromResult(first);
                }

                return state.Reader.ReadTailAsync(state.Open, token);
            },
            (Reader: _reader, Open: open),
            cancellationToken).ConfigureAwait(false);
        return answer switch
        {
            LedgerTail tail => tail,
            ReplicaWriteOutcome.Backpressure => throw new UnreachableException("A seal check repeats backpressure."),
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// After a dropped or sealed answer, which made the reader read the map again: reads it once more when that read did
    /// not advance the view past <paramref name="map"/>, as when the store showed a drain the seal check missed.
    /// </summary>
    private async ValueTask RereadMapUnlessAdvancedAsync(LedgerMapValue map, CancellationToken cancellationToken)
    {
        if (_map.Current.Epoch == map.Epoch)
        {
            _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the map, in a read begun after the call; returns whether it advanced past <paramref name="map"/>.</summary>
    private async ValueTask<bool> ReadMapAsync(LedgerMapValue map, CancellationToken cancellationToken)
    {
        _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        return _map.Current.Epoch > map.Epoch;
    }

    /// <summary>How one pass of a ledger's scan or indexed read ended.</summary>
    private enum ScanPass
    {
        Reached,
        Dropped,
        Backpressured,
        Unindexed,
    }

    /// <summary>
    /// One ledger's read for a key, which collects the key's rows into a list and a buffer of its own: via point reads of
    /// indexed slots when indexed, or by scanning from its start slot (with a segment view held across resumed passes so
    /// backpressure or open-ledger extensions rewind nothing). Disposal releases the rows it collected.
    /// </summary>
    private sealed class LedgerScan(ArchiveReader reader, LedgerMetadata ledger, Slot below, ReadOnlyMemory<byte> key, SemaphoreSlim? window, Slot startAt, bool useIndex, bool includeBodies) : IDisposable
    {
        private ArcBufferWriter? _bodies;
        private List<ArchiveRow>? _live;
        private LedgerSegmentView? _view;

        /// <summary>Gets the ledger this scan reads.</summary>
        public LedgerId LedgerId => ledger.Id;

        /// <summary>Gets the number of rows the read collected.</summary>
        public int LiveCount => _live?.Count ?? 0;

        /// <summary>Gets the key's rows the read collected, in slot order.</summary>
        public ReadOnlySpan<ArchiveRow> LiveSpan => _live is null ? [] : CollectionsMarshal.AsSpan(_live);

        /// <summary>Gets or sets the slot the read ends before.</summary>
        public Slot Below { get; set; } = below;

        /// <summary>Gets the next slot an unindexed scan reads, starting at slot 0 unless bounded by an Absent answer.</summary>
        public Slot Next { get; private set; } = startAt;

        /// <summary>Gets a value indicating whether this ledger tries the key index before scanning.</summary>
        public bool UseIndex { get; private set; } = useIndex;

        /// <summary>
        /// Gets a value indicating whether an indexed read in this ledger reached a verified tokenless opener
        /// (<c>Replace</c> or <c>Delete</c> with empty <see cref="RowToken"/>), making all earlier segments and ledgers
        /// superseded.
        /// </summary>
        public bool FoundCompleteBase { get; private set; }

        public ArcBufferWriter GetBodiesWriter() => _bodies ??= new ArcBufferWriter();

        public void AddLive(in ArchiveRow row) => (_live ??= []).Add(row);

        public void CopyLiveTo(List<ArchiveRow> destination)
        {
            if (_live is not null)
            {
                destination.AddRange(_live);
            }
        }

        /// <summary>Advances the scan's start slot to an <c>Absent</c> per-ledger bound when higher.</summary>
        public void ApplyAbsentBound(Slot bound)
        {
            if (bound > Next)
            {
                Next = bound;
            }
        }

        public void Dispose()
        {
            if (_live is not null)
            {
                ArchiveMerge.Release(CollectionsMarshal.AsSpan(_live));
            }

            _view?.Dispose();
            _bodies?.Dispose();
        }

        public async ValueTask<ScanPass> RunIndexedAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(reader._keyIndex is not null, "Indexed reads run only with a key index.");
            var lookup = await reader._keyIndex.ReadAsync(ledger, Below, key, cancellationToken).ConfigureAwait(false);
            Debug.Assert(lookup.Value is not null, "A key-index read answers a union which holds a case.");
            return lookup switch
            {
                KeyIndexRead.Dropped => ScanPass.Dropped,
                KeyIndexRead.Unindexed => MarkUnindexed(),
                KeyIndexRead.Indexed indexed => await ReadIndexedAsync(indexed, cancellationToken).ConfigureAwait(false),
                null => throw new UnreachableException("A key-index read answers a union which holds a case."),
            };

            ScanPass MarkUnindexed()
            {
                UseIndex = false;
                return ScanPass.Unindexed;
            }
        }

        private async ValueTask<ScanPass> ReadIndexedAsync(KeyIndexRead.Indexed indexed, CancellationToken cancellationToken)
        {
            reader.Counts.IndexedLedgers.Increment();
            var outcome = await FetchIndexedAsync(indexed.Entries, cancellationToken).ConfigureAwait(false);
            if (outcome == ScanPass.Reached)
            {
                Next = Below;
            }

            return outcome;
        }

        public async ValueTask<ScanPass> RunAsync(CancellationToken cancellationToken)
        {
            _view ??= new LedgerSegmentView(ledger.Id, key.Span);
            await foreach (var item in reader._reader.ScanAsync(ledger, Next, Below, _view, window, cancellationToken).ConfigureAwait(false))
            {
                if (item.Read.TryGetValue(out LedgerRead.Data data))
                {
                    using (data)
                    {
                        Collect(item, _view.Segment, this, includeBodies);
                    }

                    Next = data.Slot + 1;
                }
                else if (item.Read.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
                {
                    return ScanPass.Dropped;
                }
                else if (item.Read.TryGetValue(out ReplicaWriteOutcome.Backpressure? _))
                {
                    return ScanPass.Backpressured;
                }
                else
                {
                    throw new UnreachableException("A scan's item holds a batch, Dropped, or Backpressure.");
                }
            }

            return ScanPass.Reached;
        }

        /// <summary>
        /// Streams point reads of only the distinct defining and row slots named by <paramref name="entries"/> in
        /// ascending slot order with at most <c>K</c> reads in flight, resolving each defining batch by ordinal
        /// (<c>unknown-ordinal</c> for a stale entry), verifying the defining row's full key against hash collisions,
        /// reconstructing each matching <see cref="ArchiveRow"/>, and releasing each batch's buffer immediately.
        /// </summary>
        private ValueTask<ScanPass> FetchIndexedAsync(ImmutableArray<KeyIndexEntry> entries, CancellationToken cancellationToken)
        {
            if (entries.IsEmpty)
            {
                return ValueTask.FromResult(ScanPass.Reached);
            }

            if (entries.Length == 1 && entries[0].DefiningSlot == entries[0].Tag.Slot)
            {
                return FetchSingleSlotIndexedAsync(entries[0], cancellationToken);
            }

            return FetchMultiSlotIndexedAsync(entries, cancellationToken);
        }

        private async ValueTask<ScanPass> FetchSingleSlotIndexedAsync(KeyIndexEntry entry, CancellationToken cancellationToken)
        {
            var slot = entry.Tag.Slot;
            if (entry.Tag.Ledger != ledger.Id || slot >= Below)
            {
                throw DecodeRejection.Create(
                    LedgerSegmentView.ReasonOffsetMismatch,
                    string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} (defining slot {entry.DefiningSlot}) lies outside ledger {ledger.Id} below {Below}."));
            }

            var outcome = await reader.ReadSlotAsync(ledger, slot, window, cancellationToken).ConfigureAwait(false);
            if (outcome.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
            {
                return ScanPass.Dropped;
            }

            if (!outcome.TryGetValue(out LedgerRead.Data data))
            {
                throw new UnreachableException("A point read retries backpressure.");
            }

            using (data)
            {
                var batch = BatchView.OfVerified(data.Batch);
                if (batch.FindDefining(entry.Ordinal, out var defRow) is { } rejection)
                {
                    throw rejection;
                }

                var keySpan = key.Span;
                if (!defRow.Key.SequenceEqual(keySpan))
                {
                    return ScanPass.Reached;
                }

                if (batch.LedgerSegmentOffset > (ulong)slot.Value)
                {
                    throw DecodeRejection.Create(
                        LedgerSegmentView.ReasonOffsetMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Defining batch at slot {slot} has segment offset {batch.LedgerSegmentOffset}."));
                }

                if (entry.Tag.Index >= batch.Count)
                {
                    throw DecodeRejection.Create(
                        BatchView.ReasonUnknownOrdinal,
                        string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} lies past batch count {batch.Count} at slot {slot}."));
                }

                if (entry.Tag.Index != defRow.Index)
                {
                    if (!TryFindRow(batch, entry.Tag.Index, out var row))
                    {
                        throw DecodeRejection.Create(
                            BatchView.ReasonUnknownOrdinal,
                            string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} lies past batch count {batch.Count} at slot {slot}."));
                    }

                    throw DecodeRejection.Create(
                        row.IsDefining ? LedgerSegmentView.ReasonDuplicateKey : BatchView.ReasonUnknownOrdinal,
                        row.IsDefining
                            ? string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} does not match the defining row of ordinal {entry.Ordinal} at slot {slot}.")
                            : string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} does not reference ordinal {entry.Ordinal} at slot {slot}."));
                }

                var versionBefore = defRow.VersionBefore;
                if (versionBefore.Count == ulong.MaxValue)
                {
                    throw DecodeRejection.Create(
                        LedgerSegmentView.ReasonCountOverflow,
                        string.Create(CultureInfo.InvariantCulture, $"Row {defRow.Index} of the batch at slot {slot} would advance its key's count past the u64 maximum."));
                }

                if (defRow.Kind == RowKind.Message)
                {
                    // LG-I15: message rows are excluded from ArchiveReader journal folds.
                    return ScanPass.Reached;
                }

                var segment = new LedgerSegmentId(ledger.Id, new Slot(slot.Value - (long)batch.LedgerSegmentOffset));
                var version = new RowVersion(versionBefore.Base.IsEmpty ? default : new RowToken(versionBefore.Base.ToArray()), versionBefore.Count + 1);
                var token = defRow.Token.IsEmpty ? default : new RowToken(defRow.Token.ToArray());
                AddIndexedRow(entry.Tag, defRow.Kind, version, segment, token, defRow.Body, defRow.BodyLength, supersededBefore: 0);
                if (defRow.Kind is RowKind.Replace or RowKind.Delete && token.IsEmpty)
                {
                    FoundCompleteBase = true;
                }
            }

            return ScanPass.Reached;
        }

        private async ValueTask<ScanPass> FetchMultiSlotIndexedAsync(ImmutableArray<KeyIndexEntry> entries, CancellationToken cancellationToken)
        {
            // Validate entries and precompute each entry's 1-based offset within its (DefiningSlot, Ordinal) segment so
            // descending waves compute exact RowVersion counts and SupersededBefore counts without reading older slots.
            var entryCount = entries.Length;
            var offsetInSegment = new ulong[entryCount];
            var segmentHasDefiningEntry = new bool[entryCount];
            var segmentCounts = new Dictionary<(Slot DefiningSlot, ulong Ordinal), (ulong Count, bool HasDefining)>();
            for (var index = 0; index < entryCount; index++)
            {
                var entry = entries[index];
                if (entry.Tag.Ledger != ledger.Id || entry.Tag.Slot >= Below || entry.DefiningSlot > entry.Tag.Slot)
                {
                    throw DecodeRejection.Create(
                        LedgerSegmentView.ReasonOffsetMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} (defining slot {entry.DefiningSlot}) lies outside ledger {ledger.Id} below {Below}."));
                }

                if (index > 0 && entry.Tag <= entries[index - 1].Tag)
                {
                    throw DecodeRejection.Create(
                        LedgerSegmentView.ReasonOffsetMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Key-index entries for ledger {ledger.Id} are not in strictly increasing tag order at {entry.Tag}."));
                }

                var segKey = (entry.DefiningSlot, entry.Ordinal);
                ref var segInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(segmentCounts, segKey, out var existed);
                if (!existed)
                {
                    segInfo = (1UL, entry.Tag.Slot == entry.DefiningSlot);
                }
                else
                {
                    segInfo.Count++;
                }

                offsetInSegment[index] = segInfo.Count;
                segmentHasDefiningEntry[index] = segInfo.HasDefining;
            }

            var defining = new Dictionary<(Slot DefiningSlot, ulong Ordinal), DefiningState>(segmentCounts.Count);
            HashSet<(Slot DefiningSlot, ulong Ordinal)>? stoppedSegments = null;
            var descendingRows = new List<ArchiveRow>();
            var waveSize = Math.Clamp(ledger.K, 16, 32);
            var waveEntries = new List<int>(waveSize);
            var waveSlots = new List<Slot>(waveSize * 2);
            var waveSlotSet = new HashSet<Slot>(waveSize * 2);
            var waveDefinings = new List<(Slot DefiningSlot, ulong Ordinal)>(waveSize);
            var waveDefiningSet = new HashSet<(Slot DefiningSlot, ulong Ordinal)>(waveSize);
            var waveBatches = new Dictionary<Slot, LedgerRead.Data>(waveSize * 2);
            var cursor = entryCount - 1;
            var stoppedAll = false;

            try
            {
                while (cursor >= 0 && !stoppedAll)
                {
                    waveEntries.Clear();
                    waveSlots.Clear();
                    waveSlotSet.Clear();
                    waveDefinings.Clear();
                    waveDefiningSet.Clear();

                    while (cursor >= 0 && waveEntries.Count < waveSize)
                    {
                        var idx = cursor--;
                        var entry = entries[idx];
                        var segKey = (entry.DefiningSlot, entry.Ordinal);
                        if (stoppedSegments is not null && stoppedSegments.Contains(segKey))
                        {
                            continue;
                        }

                        waveEntries.Add(idx);
                        if (waveSlotSet.Add(entry.Tag.Slot))
                        {
                            waveSlots.Add(entry.Tag.Slot);
                        }

                        if (!defining.ContainsKey(segKey) && waveDefiningSet.Add(segKey))
                        {
                            waveDefinings.Add(segKey);
                            if (waveSlotSet.Add(entry.DefiningSlot))
                            {
                                waveSlots.Add(entry.DefiningSlot);
                            }
                        }
                    }

                    if (waveEntries.Count == 0)
                    {
                        break;
                    }

                    try
                    {
                        var fetched = await FetchWaveSlotsAsync(waveSlots, waveBatches, cancellationToken).ConfigureAwait(false);
                        if (fetched == ScanPass.Dropped)
                        {
                            return ScanPass.Dropped;
                        }

                        ResolveWaveDefinings(waveDefinings, waveBatches, defining);

                        var keySpan = key.Span;
                        foreach (var idx in waveEntries)
                        {
                            var entry = entries[idx];
                            var segKey = (entry.DefiningSlot, entry.Ordinal);
                            if (stoppedSegments is not null && stoppedSegments.Contains(segKey))
                            {
                                continue;
                            }

                            ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(defining, segKey);
                            Debug.Assert(!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref state), "Every wave entry's defining state was resolved.");
                            if (!state.Matched)
                            {
                                continue;
                            }

                            var slot = entry.Tag.Slot;
                            var batch = BatchView.OfVerified(waveBatches[slot].Batch);
                            if (slot < state.Segment.Start || batch.LedgerSegmentOffset != (ulong)(slot - state.Segment.Start))
                            {
                                throw DecodeRejection.Create(
                                    LedgerSegmentView.ReasonOffsetMismatch,
                                    string.Create(CultureInfo.InvariantCulture, $"Batch at slot {slot} has segment offset {batch.LedgerSegmentOffset}, and its segment starts at slot {state.Segment.Start}."));
                            }

                            if (entry.Tag.Index >= batch.Count || !TryFindRow(batch, entry.Tag.Index, out var row))
                            {
                                throw DecodeRejection.Create(
                                    BatchView.ReasonUnknownOrdinal,
                                    string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} lies past batch count {batch.Count} at slot {slot}."));
                            }

                            var offset = offsetInSegment[idx];
                            if (slot == entry.DefiningSlot && row.IsDefining)
                            {
                                if (offset != 1 || row.Index != state.DefiningRowIndex || !row.Key.SequenceEqual(keySpan))
                                {
                                    throw DecodeRejection.Create(
                                        LedgerSegmentView.ReasonDuplicateKey,
                                        string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} does not match the defining row of ordinal {entry.Ordinal} at slot {slot}."));
                                }
                            }
                            else if (offset == 1 || !segmentHasDefiningEntry[idx] || row.IsDefining || row.Ordinal != entry.Ordinal)
                            {
                                throw DecodeRejection.Create(
                                    BatchView.ReasonUnknownOrdinal,
                                    string.Create(CultureInfo.InvariantCulture, $"Key-index entry {entry.Tag} does not reference ordinal {entry.Ordinal} at slot {slot}."));
                            }

                            if (state.DefiningVersionBeforeCount > ulong.MaxValue - offset)
                            {
                                throw DecodeRejection.Create(
                                    LedgerSegmentView.ReasonCountOverflow,
                                    string.Create(CultureInfo.InvariantCulture, $"Row {row.Index} of the batch at slot {slot} would advance its key's count past the u64 maximum."));
                            }

                            if (row.Kind == RowKind.Message)
                            {
                                // LG-I15: message rows are excluded from ArchiveReader journal folds.
                                continue;
                            }

                            var isOpener = row.Kind is RowKind.Replace or RowKind.Delete;
                            var supersededBefore = isOpener ? checked((int)(offset - 1)) : 0;
                            var version = new RowVersion(state.VersionBase is null ? default : new RowToken(state.VersionBase), state.DefiningVersionBeforeCount + offset);
                            var token = state.Token is null ? default : new RowToken(state.Token);
                            descendingRows.Add(CreateIndexedRow(entry.Tag, row.Kind, version, state.Segment, token, row.Body, row.BodyLength, supersededBefore));

                            if (isOpener)
                            {
                                _ = (stoppedSegments ??= []).Add(segKey);
                                if (token.IsEmpty)
                                {
                                    FoundCompleteBase = true;
                                    stoppedAll = true;
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (var data in waveBatches.Values)
                        {
                            data.Dispose();
                        }

                        waveBatches.Clear();
                    }
                }

                for (var index = descendingRows.Count - 1; index >= 0; index--)
                {
                    AddLive(descendingRows[index]);
                }

                descendingRows.Clear();
                return ScanPass.Reached;
            }
            finally
            {
                if (descendingRows.Count > 0)
                {
                    ArchiveMerge.Release(CollectionsMarshal.AsSpan(descendingRows));
                }
            }
        }

        private async ValueTask<ScanPass> FetchWaveSlotsBatchedAsync(
            List<Slot> slots,
            Dictionary<Slot, LedgerRead.Data> results,
            CancellationToken cancellationToken)
        {
            slots.Sort();
            var slotArray = slots.ToArray();
            for (var i = 0; i < slotArray.Length; i++)
            {
                reader.Counts.PointReads.Increment();
            }

            var first = await TryReadSlotsOnceAsync(slotArray, results, cancellationToken).ConfigureAwait(false);
            if (first != ScanPass.Backpressured)
            {
                return first;
            }

            var useFirst = true;
            return await reader._scanRetry.ExecuteAsync(
                (state, token) =>
                {
                    if (useFirst)
                    {
                        useFirst = false;
                        return ValueTask.FromResult(first);
                    }

                    return state.Scan.TryReadSlotsOnceAsync(state.Slots, state.Results, token);
                },
                (Scan: this, Slots: slotArray, Results: results),
                cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<ScanPass> TryReadSlotsOnceAsync(
            Slot[] slotArray,
            Dictionary<Slot, LedgerRead.Data> results,
            CancellationToken cancellationToken)
        {
            object? status;
            if (window is null)
            {
                status = await reader._reader.ReadSlotsAsync(ledger, slotArray, results, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await window.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    status = await reader._reader.ReadSlotsAsync(ledger, slotArray, results, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _ = window.Release();
                }
            }

            return status switch
            {
                null => ScanPass.Reached,
                ReplicaWriteOutcome.Dropped => ScanPass.Dropped,
                ReplicaWriteOutcome.Backpressure => ScanPass.Backpressured,
                _ => throw new UnreachableException("ReadSlotsAsync returns null, Dropped, or Backpressure."),
            };
        }

        private async ValueTask<ScanPass> FetchWaveSlotsAsync(
            List<Slot> slots,
            Dictionary<Slot, LedgerRead.Data> results,
            CancellationToken cancellationToken)
        {
            if (slots.Count > 1 && reader._reader.SupportsReadSlots(ledger))
            {
                return await FetchWaveSlotsBatchedAsync(slots, results, cancellationToken).ConfigureAwait(false);
            }

            var maxInFlight = Math.Min(slots.Count, Math.Max(1, ledger.K));
            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var inFlight = new Queue<(Slot Slot, Task<LedgerRead> Task)>(maxInFlight);
            var nextToLaunch = 0;
            var dropped = false;
            Exception? failure = null;
            OperationCanceledException? unexpectedCancel = null;

            try
            {
                while (nextToLaunch < maxInFlight)
                {
                    var slot = slots[nextToLaunch++];
                    inFlight.Enqueue((slot, FetchSlotAsync(slot, fetchCts)));
                }

                while (inFlight.TryDequeue(out var current))
                {
                    LedgerRead outcome;
                    try
                    {
                        outcome = await current.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (outcome.TryGetValue(out LedgerRead.Data data))
                    {
                        if (nextToLaunch < slots.Count)
                        {
                            var nextSlot = slots[nextToLaunch++];
                            inFlight.Enqueue((nextSlot, FetchSlotAsync(nextSlot, fetchCts)));
                        }

                        results[current.Slot] = data;
                    }
                    else if (outcome.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
                    {
                        dropped = true;
                        break;
                    }
                    else
                    {
                        throw new UnreachableException("A point read retries backpressure.");
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                if (inFlight.Count > 0)
                {
                    await fetchCts.CancelAsync().ConfigureAwait(false);
                    while (inFlight.TryDequeue(out var pending))
                    {
                        try
                        {
                            var leftover = await pending.Task.ConfigureAwait(false);
                            if (leftover.TryGetValue(out LedgerRead.Data leftoverData))
                            {
                                leftoverData.Dispose();
                            }
                            else if (leftover.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
                            {
                                dropped = true;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            failure ??= exception;
                        }
                    }
                }
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            if (dropped)
            {
                return ScanPass.Dropped;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (unexpectedCancel is not null)
            {
                ExceptionDispatchInfo.Throw(unexpectedCancel);
            }

            return ScanPass.Reached;

            async Task<LedgerRead> FetchSlotAsync(Slot slot, CancellationTokenSource cts)
            {
                var reached = false;
                try
                {
                    var outcome = await reader.ReadSlotAsync(ledger, slot, window, cts.Token).ConfigureAwait(false);
                    reached = outcome.TryGetValue(out LedgerRead.Data _);
                    return outcome;
                }
                catch (OperationCanceledException oce) when (!cts.IsCancellationRequested)
                {
                    _ = Interlocked.CompareExchange(ref unexpectedCancel, oce, null);
                    throw;
                }
                finally
                {
                    if (!reached)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        private void ResolveWaveDefinings(
            List<(Slot DefiningSlot, ulong Ordinal)> waveDefinings,
            Dictionary<Slot, LedgerRead.Data> waveBatches,
            Dictionary<(Slot DefiningSlot, ulong Ordinal), DefiningState> defining)
        {
            var keySpan = key.Span;
            foreach (var id in waveDefinings)
            {
                var slot = id.DefiningSlot;
                var batch = BatchView.OfVerified(waveBatches[slot].Batch);
                if (batch.FindDefining(id.Ordinal, out var defRow) is { } rejection)
                {
                    throw rejection;
                }

                if (!defRow.Key.SequenceEqual(keySpan))
                {
                    defining[id] = new DefiningState { Matched = false };
                    continue;
                }

                if (batch.LedgerSegmentOffset > (ulong)slot.Value)
                {
                    throw DecodeRejection.Create(
                        LedgerSegmentView.ReasonOffsetMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Defining batch at slot {slot} has segment offset {batch.LedgerSegmentOffset}."));
                }

                var start = new Slot(slot.Value - (long)batch.LedgerSegmentOffset);
                var versionBefore = defRow.VersionBefore;
                defining[id] = new DefiningState
                {
                    Matched = true,
                    DefiningRowIndex = defRow.Index,
                    Segment = new LedgerSegmentId(ledger.Id, start),
                    VersionBase = versionBefore.Base.IsEmpty ? null : versionBefore.Base.ToArray(),
                    Token = defRow.Token.IsEmpty ? null : defRow.Token.ToArray(),
                    DefiningVersionBeforeCount = versionBefore.Count,
                };
            }
        }

        private void AddIndexedRow(
            Tag tag,
            RowKind kind,
            RowVersion version,
            LedgerSegmentId segment,
            RowToken token,
            System.Buffers.ReadOnlySequence<byte> bodyBytes,
            int bodyLength,
            int supersededBefore) =>
            AddLive(CreateIndexedRow(tag, kind, version, segment, token, bodyBytes, bodyLength, supersededBefore));

        private ArchiveRow CreateIndexedRow(
            Tag tag,
            RowKind kind,
            RowVersion version,
            LedgerSegmentId segment,
            RowToken token,
            System.Buffers.ReadOnlySequence<byte> bodyBytes,
            int bodyLength,
            int supersededBefore)
        {
            if (!includeBodies && kind != RowKind.Delete)
            {
                return ArchiveRow.LiveLengthOnly(tag, kind, version, segment, token, bodyLength, supersededBefore);
            }

            var bodies = GetBodiesWriter();
            Debug.Assert(bodies.Length == 0, "Each body is consumed as it is copied.");
            bodies.Write(bodyBytes);
            using var body = bodyLength == 0 ? default : bodies.ConsumeSlice(bodyLength);
            return ArchiveRow.LiveSuperseded(tag, kind, version, segment, token, body, supersededBefore);
        }

        private static bool TryFindRow(BatchView batch, int targetIndex, out BatchRow row)
        {
            foreach (var candidate in batch)
            {
                if (candidate.Index == targetIndex)
                {
                    row = candidate;
                    return true;
                }
            }

            row = default;
            return false;
        }

        private struct DefiningState
        {
            public bool Matched;
            public int DefiningRowIndex;
            public LedgerSegmentId Segment;
            public byte[]? VersionBase;
            public byte[]? Token;
            public ulong DefiningVersionBeforeCount;
        }
    }
}

/// <summary>How often a <see cref="ArchiveReader"/>'s reads started again, by cause, resumed after backpressure, and used key indexes.</summary>
internal sealed class ArchiveReadCounts
{
    public Counter Stale { get; } = new();

    public Counter Dropped { get; } = new();

    public Counter Sealed { get; } = new();

    public Counter Backpressured { get; } = new();

    /// <summary>Gets how often a read started again because its prefix held a write past the end its scans reached.</summary>
    public Counter PastEnd { get; } = new();

    /// <summary>Gets how many distinct live-ledger slot point reads the reader issued through key indexes.</summary>
    public Counter PointReads { get; } = new();

    /// <summary>Gets how many live-ledger reads were answered through key indexes.</summary>
    public Counter IndexedLedgers { get; } = new();

    /// <summary>A count which concurrent reads raise.</summary>
    internal sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);
    }
}
