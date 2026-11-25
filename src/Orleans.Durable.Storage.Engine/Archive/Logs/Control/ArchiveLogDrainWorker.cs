using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>
/// Drains a ledger's closed segments into archive logs (archive-logs.md §2.3, §8.1), one whole segment at a time and in
/// slot order: it scans the segment from its start, builds each key's write from the key's resolved rows, checks each
/// write's stored token against its key's chain, writes every key through <see cref="ArchiveLogWriter"/>, and, once every
/// write is held, creates the segment's drain marker <c>durable-storage/d/{S:D20}/{start:D20}</c>, which completes the segment
/// (LOG-I3).
/// </summary>
/// <remarks>
/// <para>
/// A drain of <c>S</c> runs only while the ledger map lists no ledger with a lower ID which overlaps <c>S</c>, the drop
/// rule's own condition (LG-I10): the older ledgers have then dropped, so each is wholly held (HI-I2), and a write which
/// the store classifies <c>PredecessorMissing</c> is a log which lost a write it held, which blocks the ledger with cause
/// <see cref="ArchiveLogDrainBlockedCause.Corrupt"/>. The map never lists a lower ID again, so the check holds for the rest
/// of the drain. A drain starts from the end this worker last reached for <c>S</c>, or else the content of the highest
/// marker of <c>S</c>, or else slot 0.
/// </para>
/// <para>
/// A segment closes by evidence every process sees alike (ledgers.md §2.2), and fixes each key's write, its commands and
/// the token its defining row stores, so racing drains, stragglers, and retries all make the same writes (archive-logs.md
/// §3), and the drain's content is the batch pages' own bytes. Before it writes a segment, the drain checks each key's
/// stored token against <c>Plan</c> of the key's write in the segment it drained before, in the same call (HI-I4): a
/// break would otherwise be classified held, its segment marked, and its ledger dropped with the key's commands lost, so
/// it blocks the ledger with cause <see cref="ArchiveLogDrainBlockedCause.Corrupt"/>. A segment with no rows holds no
/// write: the drain passes it without writing, and leaves no marker.
/// </para>
/// <para>
/// A drain holds its segment's rows within the process's drain memory, <see cref="ArchiveLogOptions.DrainMemoryBytes"/>,
/// a hard bound it shares with the LSM's drains through <see cref="DrainScan"/>: a segment whose rows pass the whole
/// budget fails with <see cref="DrainMemoryExceededException"/>. It writes at most
/// <see cref="ArchiveLogOptions.DrainConcurrency"/> keys at once over every drain of the worker, admitting each key's
/// planned requests within <see cref="RequestsPerSecond"/>, the node's share of the cluster's request budget, and charging
/// the requests a write issued beyond its plan as debt. A segment whose write fails stops admitting writes at once; the
/// writes of the segment which were held stay remembered, so the segment's next attempt writes only the others.
/// </para>
/// </remarks>
public sealed class ArchiveLogDrainWorker
{
    // A key's entry beyond its bytes: its KeyWrite, write builder, command and body lists with their first arrays, its
    // dictionary entry, and its key array's header, from the types' layouts, list growth included.
    private const long KeyEntryBytes = 512;

    // A row's command and body entries beyond its body, each list doubling, and a Delete's tombstone array.
    private const long CommandEntryBytes = 160;

    private readonly ArchiveLogWriter _writer;
    private readonly ArchiveLogLayout _layout;
    private readonly IObjectStore _objects;
    private readonly LedgerMapView _map;
    private readonly LedgerReader _reader;
    private readonly ImmutableArray<MessageFolds.RegisteredProcessor> _messageProcessors;
    private readonly TimeProvider _time;
    private readonly ArchiveLogNames _names;
    private readonly ByteBudget _memory;
    private readonly ByteBudget _writes;
    private readonly RequestPacer _pacer;
    private readonly Lock _sync = new();
    private readonly Dictionary<LedgerId, Slot> _resume = [];
    private readonly HashSet<LedgerId> _complete = [];
    private readonly Dictionary<LedgerSegmentId, HashSet<byte[]>> _held = [];

    // The ledgers whose drop this worker saw apply, until a map value no longer lists them: the view may lag the drop.
    private readonly HashSet<LedgerId> _dropped = [];

    // Each ledger's progress in this worker: the keys whose writes it saw held and the segments it completed.
    private readonly Dictionary<LedgerId, long> _progress = [];
    private long _segmentsWithoutRows;

    // The epoch of the newest map value a drain read, to which the worker forgot every ledger it no longer lists.
    private ulong _prunedAt;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogDrainWorker"/> class.</summary>
    /// <param name="gate">
    /// The silo's archive gate, which binds the archive-log store and the object store of durable storage's objects under
    /// <c>durable-storage/</c>, which holds the drain markers: a drain writes only once the gate passed (archive-logs.md §7.5,
    /// orleans.md §5.13).
    /// </param>
    /// <param name="map">The process's ledger map view, which the lineage check reads.</param>
    /// <param name="reader">Reads the ledgers' decided batches.</param>
    /// <param name="options">The drain concurrency and memory, and the request rate; the logs' names are the gate's.</param>
    /// <param name="timeProvider">The clock of the request rate, the scans' backoff, and the writer's retries.</param>
    /// <param name="writerOptions">The writer's retries; by default <see cref="ArchiveLogWriterOptions"/>'s.</param>
    /// <param name="messageProcessors">Optional control-plane message processors validating and folding <see cref="RowKind.Message"/> rows during segment drains (LG-I15, LG-I16).</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public ArchiveLogDrainWorker(
        ArchiveLogGate gate,
        LedgerMapView map,
        LedgerReader reader,
        ArchiveLogOptions options,
        TimeProvider timeProvider,
        ArchiveLogWriterOptions? writerOptions = null,
        IEnumerable<IMessageProcessor>? messageProcessors = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _writer = new ArchiveLogWriter(gate.Logs, timeProvider, writerOptions);
        _layout = gate.Logs.Layout;
        _objects = gate.Objects;
        _map = map;
        _reader = reader;
        _messageProcessors = MessageFolds.ValidateProcessors(messageProcessors);
        _time = timeProvider;
        _names = gate.Names;
        _memory = new ByteBudget(options.DrainMemoryBytes);
        _writes = new ByteBudget(options.DrainConcurrency);
        _pacer = new RequestPacer(timeProvider, options.DrainRequestsPerSecond);
    }

    /// <summary>
    /// Gets or sets the requests a second the worker's drains make at most: the node's share of the cluster's request
    /// budget, which the composition's controller reassigns in proportion to each node's undrained keys (archive-logs.md
    /// §8.6); it applies to the writes admitted after it is set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
    public double RequestsPerSecond
    {
        get => _pacer.RequestsPerSecond;
        set => _pacer.RequestsPerSecond = value;
    }

    /// <summary>
    /// Gets the source of the requests a second the worker's drains make at most, read before each admission and charge in
    /// place of <see cref="RequestsPerSecond"/>, or <see langword="null"/>: the composition's share, whose deadline it
    /// checks where the rate is used (orleans.md §5.6, OR-I22). It must be cheap, safe from any thread, and answer a positive
    /// rate.
    /// </summary>
    public Func<double>? RequestsPerSecondSource
    {
        get => _pacer.Source;
        init => _pacer.Source = value;
    }

    /// <summary>
    /// Gets the observer of each segment with rows the worker scanned to its end and starts writing, with the number of keys
    /// it writes (archive-logs.md §8.1): the composition counts a segment's keys from it where no writer recorded them
    /// (orleans.md §5.6). Called on the drain's thread, so it must not block.
    /// </summary>
    public Action<LedgerSegmentKeys>? OnSegmentScanned { get; init; }

    /// <summary>Gets how many segments this worker drained alone, holding the whole drain memory, which the composition reports.</summary>
    public long SegmentsDrainedAlone => Scan.DrainedAlone;

    /// <summary>Gets how many times a scan resumes after backpressure before the drain answers its progress; at least 1.</summary>
    internal int BackpressureRetries { get; init; } = 8;

    /// <summary>Gets the first wait before a scan resumes after backpressure, which doubles, jittered, up to a second.</summary>
    internal TimeSpan BackpressureDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Gets how many segments without rows, a marker batch alone, the worker passed without a write; for tests.</summary>
    internal long SegmentsWithoutRows => Interlocked.Read(ref _segmentsWithoutRows);

    /// <summary>Gets the drain memory no drain holds; for tests.</summary>
    internal long AvailableMemoryBytes => _memory.Available;

    /// <summary>Gets the ledgers and segments of which the worker remembers anything; for tests.</summary>
    internal (int Resume, int Complete, int Held, int Dropped) Remembered
    {
        get
        {
            lock (_sync)
            {
                return (_resume.Count, _complete.Count, _held.Count, _dropped.Count);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the worker is the <c>ignores-lineage</c> mutant, which drains a ledger while an
    /// older overlapping ledger is listed; for tests.
    /// </summary>
    internal bool IgnoresLineageMutant { get; init; }

    /// <summary>
    /// Gets a value indicating whether the worker is the <c>marks-unheld</c> mutant, which marks a segment complete though a
    /// key's write threw, as if it were held; for tests.
    /// </summary>
    internal bool MarksUnheldMutant { get; init; }

    /// <summary>
    /// Gets a value indicating whether the worker skips committing segment message folds before creating the drain
    /// marker (LG-I16 mutant for tests).
    /// </summary>
    internal bool DropBeforeMessagesCommittedMutant { get; init; }

    /// <summary>
    /// Gets a value indicating whether the worker skips <see cref="RowKind.Message"/> rows for unregistered channels
    /// instead of failing closed with <see cref="ArchiveLogDrainBlockedException"/> (LG-I15 mutant for tests).
    /// </summary>
    internal bool SkipUnknownChannelMutant { get; init; }

    /// <summary>
    /// Gets the observer of each segment the worker scanned to its end and starts writing, which maps a simulated drain onto
    /// the model's actions (verification.md §5.1); for tests.
    /// </summary>
    internal Action<LedgerSegmentId, Slot>? StartedSegment { get; init; }

    /// <summary>Gets the observer of each segment the worker completed, or passed holding no row; for tests.</summary>
    internal Action<LedgerSegmentId, Slot>? CompletedSegment { get; init; }

    private DrainScan Scan => field ??= new DrainScan(_reader, _memory, _time, BackpressureRetries, BackpressureDelay);

    /// <summary>
    /// Returns the progress this worker made on <paramref name="ledger"/>: the keys whose writes it saw held and the segments
    /// it completed, a count which only grows while the worker remembers the ledger, so a drain which holds keys within one
    /// long segment shows progress before the segment completes (orleans.md §5.6, <c>ClosedDrainTimeout</c>).
    /// </summary>
    public long Progress(LedgerId ledger)
    {
        lock (_sync)
        {
            return _progress.GetValueOrDefault(ledger);
        }
    }
    /// <summary>
    /// Drains, in slot order, each segment of <paramref name="ledger"/> from its first undrained segment which closes
    /// below <paramref name="below"/> (archive-logs.md §2.3, §8.1).
    /// </summary>
    /// <param name="ledger">The ledger, as a ledger map value the caller read lists it.</param>
    /// <param name="below">
    /// The slot below which the drain may read: a contiguous tail of the ledger which the reader learned after the call, or,
    /// for a closed ledger, at most its <c>T</c>.
    /// </param>
    /// <param name="cancellationToken">Stops the drain; the writes which landed stay, and the segment gets no marker.</param>
    /// <returns>
    /// <see cref="ArchiveLogDrainOutcome.Complete"/> when the ledger is closed and every segment below its <c>T</c> is
    /// complete; <see cref="ArchiveLogDrainOutcome.Drained"/> at the first segment not yet complete, starting at or past
    /// <paramref name="below"/> or met by backpressure; <see cref="ArchiveLogDrainOutcome.OpenSegment"/> at it when it is
    /// open at <paramref name="below"/>; <see cref="ArchiveLogDrainOutcome.Waiting"/> when the map lists an
    /// older overlapping ledger; or <see cref="ArchiveLogDrainOutcome.Dropped"/> when a map value shows the ledger dropped, or this
    /// worker's drop of it applied.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="below"/> lies past a closed ledger's <c>T</c>.</exception>
    /// <exception cref="ArchiveLogDrainBlockedException">
    /// A key's write no retry can make succeed, a damaged segment, or a corrupt drain marker: the drain stops at its segment.
    /// </exception>
    /// <exception cref="DrainMemoryExceededException">A segment's rows pass the drain memory alone.</exception>
    /// <remarks>
    /// At the first key's write of a segment which throws, the drain admits no more of the segment's writes, lets those
    /// in flight settle, and throws that exception, its progress staying at the segment's start; a later drain writes the
    /// segment's other keys again, skipping those this worker saw held. A caller runs at most one drain of a ledger at a
    /// time, and submits its drop only after the drain which answered <c>Complete</c>: the worker's memory of a ledger,
    /// its resumption, completion, held keys, and drop, follows one drain of it at a time.
    /// </remarks>
    public async Task<ArchiveLogDrainOutcome> DrainAsync(LedgerMetadata ledger, Slot below, CancellationToken cancellationToken = default)
    {
        Slot? t = ledger.Status.Value is LedgerStatus.Closed closed ? closed.T : null;
        if (t is { } last)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(below, last);
        }

        if (await LineageAsync(ledger, cancellationToken).ConfigureAwait(false) is { } answer)
        {
            return answer;
        }

        if (await StartAsync(ledger, t, cancellationToken).ConfigureAwait(false) is not { } resumed)
        {
            return ArchiveLogDrainOutcome.Dropped.Instance;
        }

        var (start, marker) = resumed;
        Dictionary<byte[], RowToken>? chain = null;
        while (true)
        {
            Debug.Assert(t is not { } end || start <= end, "A start past T is a corrupt marker, which the resume refused.");
            if (start == t)
            {
                lock (_sync)
                {
                    _ = _complete.Add(ledger.Id);
                }

                return ArchiveLogDrainOutcome.Complete.Instance;
            }

            if (start >= below)
            {
                return new ArchiveLogDrainOutcome.Drained(start);
            }

            var drained = await DrainSegmentAsync(ledger, start, below, below == t, chain, marker, cancellationToken).ConfigureAwait(false);
            switch (drained.Value)
            {
                case DrainedSegment segment:
                    Advance(ledger.Id, segment.End);
                    (start, chain, marker) = (segment.End, segment.Chain, null);
                    break;
                case DrainScan.Open:
                    return new ArchiveLogDrainOutcome.OpenSegment(start);
                case ReplicaWriteOutcome.Backpressure:
                    return new ArchiveLogDrainOutcome.Drained(start);
                case ReplicaWriteOutcome.Dropped:
                    Forget(ledger.Id);
                    return ArchiveLogDrainOutcome.Dropped.Instance;
                case null:
                default:
                    throw new UnreachableException("conventions.md §15: a segment's drain answers a union which holds a case.");
            }
        }
    }

    /// <summary>
    /// Submits <c>Drop(S)</c> for a ledger this worker's drain answered <see cref="ArchiveLogDrainOutcome.Complete"/>
    /// (archive-logs.md §2.4): the held writes are the coverage HI-I2 asks for, and no listed ledger with a lower ID overlaps
    /// it. Once the map holds the drop, the worker forgets the ledger's drains, and remembers the drop until a map value no
    /// longer lists the ledger, so a drain of it answers <see cref="ArchiveLogDrainOutcome.Dropped"/> while the process's
    /// view lags.
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="commit">Submits a change once to the ledger map committer, as its <c>CommitAsync</c> does.</param>
    /// <param name="cancellationToken">Stops the caller's wait; the drop may still apply.</param>
    /// <returns>The committer's answer; a drop which finds the ledger already removed finds its goal.</returns>
    /// <exception cref="InvalidOperationException">No drain of this worker answered the ledger complete.</exception>
    public async Task<LedgerMapCommitOutcome> SubmitDropAsync(
        LedgerId ledger,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_sync)
        {
            if (!_complete.Contains(ledger))
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No drain of this worker answered ledger {ledger} complete (LOG-I3)."));
            }
        }

        var outcome = await commit(new LedgerMapChange.Drop(ledger), cancellationToken).ConfigureAwait(false);
        if (outcome.Value is LedgerMapCommitOutcome.Applied)
        {
            // One step, so no drain finds the ledger forgotten and not yet remembered dropped.
            lock (_sync)
            {
                ForgetLocked(ledger);
                _ = _dropped.Add(ledger);
            }
        }

        return outcome;
    }

    /// <summary>
    /// The lineage check (archive-logs.md §8.1, step 1): <c>Dropped</c> when this worker's drop of the ledger applied, which
    /// it checks before reading the map, or the map shows the ledger below its <c>NextLedgerId</c> and unlisted;
    /// <c>Waiting</c> when it lists an older overlapping ledger, <see cref="LedgerMapValue.OlderOverlapping"/>, which the
    /// value computes once for every ledger; otherwise none. A view which has not yet passed the ledger in
    /// <c>NextLedgerId</c> is older than it, and the map is read again. The worker forgets every ledger a map value no
    /// longer lists once per value, the first time a drain reads a value newer than any before it.
    /// </summary>
    private async ValueTask<ArchiveLogDrainOutcome?> LineageAsync(LedgerMetadata ledger, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_dropped.Contains(ledger.Id))
            {
                // Its drop applied, so the map no longer lists it, whatever the process's view shows.
                return ArchiveLogDrainOutcome.Dropped.Instance;
            }
        }

        var map = _map.Current;
        if (map.NextLedgerId <= ledger.Id)
        {
            map = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (map.NextLedgerId <= ledger.Id)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"No ledger map value has yet passed ledger {ledger.Id}."), nameof(ledger));
            }
        }

        Prune(map);
        if (map.Get(ledger.Id) is null)
        {
            return ArchiveLogDrainOutcome.Dropped.Instance;
        }

        if (!IgnoresLineageMutant && map.OlderOverlapping(ledger.Id) is { } older)
        {
            return new ArchiveLogDrainOutcome.Waiting(older);
        }

        return null;
    }
    /// <summary>
    /// Returns the start of the ledger's first segment not yet complete: the end this worker last reached, or else the
    /// content of the highest marker under <c>durable-storage/d/{S:D20}/</c>, with that marker's segment, or else slot 0; null when the
    /// highest marker vanished because the ledger dropped, which a map read after it shows.
    /// </summary>
    /// <exception cref="ArchiveLogDrainBlockedException">
    /// The highest marker breaks its format, names an end not past its start or past <c>T</c>, or vanished while the
    /// ledger is still listed: cause <see cref="ArchiveLogDrainBlockedCause.CorruptMarker"/>.
    /// </exception>
    private async ValueTask<(Slot Start, LedgerSegmentId? Marker)?> StartAsync(LedgerMetadata ledger, Slot? t, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_resume.TryGetValue(ledger.Id, out var reached))
            {
                return (reached, null);
            }
        }

        ObjectName? highest = null;
        Slot highestStart = default;

        // The listing is one of the drain's requests, so it takes its share of the budget before it is made.
        await _pacer.WaitAsync(1, cancellationToken).ConfigureAwait(false);
        await foreach (var listed in _objects.ListAsync(ArchiveLogNames.DrainMarkersOf(ledger.Id), cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (ArchiveLogNames.ParseDrainMarker(listed.Name.Value) is { } parsed && parsed.Ledger == ledger.Id && (highest is null || parsed.Start > highestStart))
            {
                (highest, highestStart) = (listed.Name, parsed.Start);
            }
        }

        if (highest is null)
        {
            return (Slot.Zero, null);
        }

        var marker = new LedgerSegmentId(ledger.Id, highestStart);
        await _pacer.WaitAsync(2, cancellationToken).ConfigureAwait(false);
        if (await _objects.ReadVersionedAsync(highest, cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            // Only a sweep of a dropped ledger deletes markers (LOG-I6), so a vanished marker is a drop this drain's lineage
            // check missed, which a map read after it shows, or corruption.
            if ((await _map.ReadAsync(cancellationToken).ConfigureAwait(false)).Get(ledger.Id) is null)
            {
                Forget(ledger.Id);
                return null;
            }

            throw CorruptMarker(marker, $"The drain marker {highest} was listed and then read as absent, while ledger {ledger.Id} is still listed; only a sweep of a dropped ledger deletes markers.");
        }

        using var content = stored.Content;
        var end = ArchiveLogNames.ParseDrainMarkerContent(content.ToArray())
            ?? throw CorruptMarker(marker, $"The drain marker {highest} breaks its format: its content is not 20 decimal digits.");
        if (end <= highestStart)
        {
            throw CorruptMarker(marker, $"The drain marker {highest} names its segment's end {end}, not past its start.");
        }

        if (end > t)
        {
            throw CorruptMarker(marker, $"The drain marker {highest} names its segment's end {end}, past ledger {ledger.Id}'s T {t}.");
        }

        Advance(ledger.Id, end);
        return (end, marker);
    }

    /// <summary>
    /// Drains the segment of <paramref name="ledger"/> which starts at <paramref name="start"/>, if it closes below
    /// <paramref name="below"/>: scans it, checks its writes' tokens against <paramref name="chain"/>, writes each key's
    /// write, and creates its marker once every write is held.
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="start">The segment's start.</param>
    /// <param name="below">The scan's bound.</param>
    /// <param name="boundIsEnd">Whether the bound is the closed ledger's <c>T</c>.</param>
    /// <param name="chain">The token each key of the segment drained before this one in the call plans next, or none.</param>
    /// <param name="marker">The marker the start came from, which a start that starts no segment shows corrupt; or none.</param>
    /// <param name="cancellationToken">Stops the drain.</param>
    /// <returns>The segment's end and its keys' next tokens; or why it is not drained: open at the bound, the ledger dropped, or backpressure.</returns>
    private async Task<DrainStep> DrainSegmentAsync(LedgerMetadata ledger, Slot start, Slot below, bool boundIsEnd, Dictionary<byte[], RowToken>? chain, LedgerSegmentId? marker, CancellationToken cancellationToken)
    {
        var id = new LedgerSegmentId(ledger.Id, start);
        using var segment = new Segment(id, ledger, _messageProcessors, SkipUnknownChannelMutant);
        DrainScanOutcome scanned;
        try
        {
            scanned = await Scan.ScanAsync(ledger, segment, below, boundIsEnd, () => NotAStart(id, marker), bySegment: true, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw ArchiveLogDrainBlockedException.ForDamagedSegment(id, exception);
        }
        catch (ArchiveCorruptException exception)
        {
            throw ArchiveLogDrainBlockedException.ForCorruptSegment(id, exception);
        }

        switch (scanned.Value)
        {
            case Slot:
                break;
            case DrainScan.Open open:
                return open;
            case ReplicaWriteOutcome.Dropped dropped:
                return dropped;
            case ReplicaWriteOutcome.Backpressure backpressure:
                return backpressure;
            case null:
            default:
                throw new UnreachableException("conventions.md §15: a scan answers a union which holds a case.");
        }

        StartedSegment?.Invoke(id, segment.End);
        var writes = segment.Writes.Select(write => (Key: write, Write: write.ToWrite())).ToArray();
        CheckChain(id, writes, chain);
        if (writes.Length > 0)
        {
            OnSegmentScanned?.Invoke(new LedgerSegmentKeys(id, writes.Length));
        }

        if (writes.Length == 0 && !segment.HasMessages)
        {
            // A segment with no archive rows and no messages (a marker batch alone) holds no write.
            _ = Interlocked.Increment(ref _segmentsWithoutRows);
            CompletedSegment?.Invoke(id, segment.End);
            return new DrainedSegment(segment.End, chain);
        }

        if (writes.Length > 0)
        {
            await WriteAsync(id, writes, cancellationToken).ConfigureAwait(false);
        }

        if (segment.HasMessages && !DropBeforeMessagesCommittedMutant)
        {
            try
            {
                await segment.CommitMessagesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveCorruptException exception)
            {
                throw ArchiveLogDrainBlockedException.ForCorruptSegment(id, exception);
            }
        }

        await _pacer.WaitAsync(1, cancellationToken).ConfigureAwait(false);
        using var content = ArcBuffer.Wrap(ArchiveLogNames.DrainMarkerContent(segment.End));
        var created = await _objects.CreateAsync(new ObjectName(ArchiveLogNames.DrainMarker(ledger.Id, start)), content, null, cancellationToken).ConfigureAwait(false);

        // A marker which exists is another drain's of the same segment, whose end is the same (LOG-I3).
        Debug.Assert(created.Value is ObjectCreateOutcome.Created or ObjectCreateOutcome.Exists, "A create answers Created or Exists.");
        lock (_sync)
        {
            _ = _held.Remove(id);
            AddProgressLocked(ledger.Id);
        }

        CompletedSegment?.Invoke(id, segment.End);
        var next = new Dictionary<byte[], RowToken>(writes.Length, LedgerSegmentDictionary.KeyComparer.Instance);
        foreach (var (key, write) in writes)
        {
            next[key.Key] = PlanOf(write);
        }

        return new DrainedSegment(segment.End, next);
    }

    /// <summary>
    /// Checks each write's stored token against <c>Plan</c> of its key's write in the segment drained before it in the
    /// call (HI-I4): a key the earlier segment wrote must start where that write ends.
    /// </summary>
    /// <exception cref="ArchiveLogDrainBlockedException">A token breaks its key's chain: cause <see cref="ArchiveLogDrainBlockedCause.Corrupt"/>.</exception>
    private void CheckChain(LedgerSegmentId id, (KeyWrite Key, ArchiveWrite Write)[] writes, Dictionary<byte[], RowToken>? chain)
    {
        if (chain is null)
        {
            return;
        }

        foreach (var (key, write) in writes)
        {
            if (chain.TryGetValue(key.Key, out var expected) && write.Token != expected)
            {
                throw ArchiveLogDrainBlockedException.ForKey(
                    key.Key,
                    _names.Of(key.Key),
                    id,
                    ArchiveLogDrainBlockedCause.Corrupt,
                    new ArchiveCorruptException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The write of segment ({id.Ledger}, {id.Start}) stores token {write.Token}, not {expected}, which its key's write in the segment before plans (HI-I4).")));
            }
        }
    }

    /// <summary>
    /// Writes each key's write of the segment which this worker has not seen held, at most the worker's concurrency at once
    /// and within its request rate; at the first which throws, admits no more, lets those in flight settle, and throws it,
    /// as <see cref="ArchiveLogDrainBlockedException"/> when no retry removes its cause.
    /// </summary>
    private async Task WriteAsync(LedgerSegmentId id, (KeyWrite Key, ArchiveWrite Write)[] writes, CancellationToken cancellationToken)
    {
        HashSet<byte[]> held;
        lock (_sync)
        {
            if (!_held.TryGetValue(id, out held!))
            {
                _held[id] = held = [with(LedgerSegmentDictionary.KeyComparer.Instance)];
            }
        }

        var failure = new FirstFailure();
        var pending = new List<Task>(writes.Length);
        try
        {
            foreach (var (key, write) in writes)
            {
                lock (_sync)
                {
                    if (held.Contains(key.Key))
                    {
                        continue;
                    }
                }

                var slot = await _writes.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
                if (failure.HasFailed)
                {
                    slot.Dispose();
                    break;
                }

                var planned = _writer.PlannedRequests(write);
                try
                {
                    await _pacer.WaitAsync(planned, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    slot.Dispose();
                    throw;
                }

                pending.Add(WriteKeyAsync(id, key, write, planned, held, slot, failure, cancellationToken));
            }
        }
        finally
        {
            // The writes already admitted settle before the drain answers, so their buffers stay valid while they run.
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        if (failure.Exception is { } thrown && !MarksUnheldMutant)
        {
            thrown.Throw();
        }
    }

    /// <summary>
    /// Writes one key's write, releasing its concurrency slot when it settles, charging the requests it issued past its
    /// plan to the request rate, and remembering it held or recording its failure.
    /// </summary>
    private async Task WriteKeyAsync(LedgerSegmentId id, KeyWrite key, ArchiveWrite write, int planned, HashSet<byte[]> held, ByteBudget.Lease slot, FirstFailure failure, CancellationToken cancellationToken)
    {
        using (slot)
        {
            // The receiving half of admission's check (AdmitsKey): a row's key is one the store's names name, since the
            // scheme, the set of keys it declines among it, is the record's (LOG-I5).
            Debug.Assert(_names.TryOf(key.Key) is not null, "A drained key is one its store's names admitted.");
            var name = _names.Of(key.Key);
            var issued = new RequestTally();
            try
            {
                var verdict = await _writer.WriteAsync(name, write, key.Content(), issued, cancellationToken).ConfigureAwait(false);
                if (verdict.Value is ArchiveLogConditionOutcome.PredecessorMissing)
                {
                    // Every older ledger overlapping the segment's dropped, and its earlier segments are complete, so the log
                    // lacks a write it held (HI-I1, HI-I2).
                    failure.Record(ArchiveLogDrainBlockedException.ForKey(
                        key.Key,
                        name,
                        id,
                        ArchiveLogDrainBlockedCause.Corrupt,
                        new ArchiveCorruptException($"The archive log '{name}' lacks the write before the segment's, though every older write of its key is held.")));
                    return;
                }

                lock (_sync)
                {
                    if (held.Add(key.Key))
                    {
                        AddProgressLocked(id.Ledger);
                    }
                }
            }
            catch (ArchiveCorruptException exception)
            {
                failure.Record(ArchiveLogDrainBlockedException.ForKey(key.Key, name, id, ArchiveLogDrainBlockedCause.Corrupt, exception));
            }
            catch (ArchiveLogRefusedException exception)
            {
                failure.Record(ArchiveLogDrainBlockedException.ForKey(key.Key, name, id, ArchiveLogDrainBlockedCause.Refused, exception));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure.Record(exception);
            }
            finally
            {
                _pacer.Charge(Math.Max(0, issued.Issued - planned));
            }
        }
    }

    /// <summary>Returns <c>Plan</c> of <paramref name="write"/>: the token its key's next write starts from.</summary>
    private RowToken PlanOf(ArchiveWrite write)
    {
        var plan = new ArchiveLogPlan(_layout, ArchiveLogTokens.PositionOf(write.Token));
        plan.Supersede(write.Superseded);
        foreach (var command in write.Commands)
        {
            plan.Add(command);
        }

        return plan.End();
    }

    private static ArchiveLogDrainBlockedException CorruptMarker(LedgerSegmentId marker, string message) =>
        ArchiveLogDrainBlockedException.ForCorruptMarker(marker, new ArchiveCorruptException(message));

    /// <summary>
    /// Returns the failure of a drain whose start holds a batch which starts no segment: a corrupt marker, when the start
    /// came from one; otherwise corruption of the ledger, whose earlier segment's scan read that start.
    /// </summary>
    private static Exception NotAStart(LedgerSegmentId id, LedgerSegmentId? marker)
    {
        var message = string.Create(CultureInfo.InvariantCulture, $"Ledger {id.Ledger}'s drain resumes at {id.Start}, whose batch does not start a segment (LOG-I3).");
        return marker is { } from ? CorruptMarker(from, message) : new ArchiveCorruptException(message);
    }

    private void Advance(LedgerId ledger, Slot end)
    {
        lock (_sync)
        {
            // Progress only moves forward: a straggler's earlier end never replaces a later one.
            if (!_resume.TryGetValue(ledger, out var reached) || reached < end)
            {
                _resume[ledger] = end;
            }
        }
    }

    private void Forget(LedgerId ledger)
    {
        lock (_sync)
        {
            ForgetLocked(ledger);
        }
    }

    /// <summary>Forgets what this worker's drains remembered of <paramref name="ledger"/>. Under the lock.</summary>
    private void ForgetLocked(LedgerId ledger)
    {
        _ = _resume.Remove(ledger);
        _ = _complete.Remove(ledger);
        RemoveHeld(ledger);
    }

    /// <summary>
    /// Forgets every ledger which <paramref name="map"/> no longer lists, below its <c>NextLedgerId</c>, its drop among
    /// them, when <paramref name="map"/> is newer than every value pruned at before; an older or equal value forgets nothing
    /// more.
    /// </summary>
    private void Prune(LedgerMapValue map)
    {
        lock (_sync)
        {
            if (map.Epoch <= _prunedAt)
            {
                return;
            }

            _prunedAt = map.Epoch;
            foreach (var ledger in _resume.Keys.Concat(_complete).Concat(_held.Keys.Select(static segment => segment.Ledger)).Concat(_dropped).Distinct().ToArray())
            {
                if (ledger < map.NextLedgerId && map.Get(ledger) is null)
                {
                    _ = _resume.Remove(ledger);
                    _ = _complete.Remove(ledger);
                    _ = _dropped.Remove(ledger);
                    RemoveHeld(ledger);
                }
            }
        }
    }

    private void AddProgressLocked(LedgerId ledger) => _progress[ledger] = _progress.GetValueOrDefault(ledger) + 1;

    /// <summary>Forgets the held writes and the progress of <paramref name="ledger"/>'s segments. Under the lock.</summary>
    private void RemoveHeld(LedgerId ledger)
    {
        _ = _progress.Remove(ledger);
        foreach (var segment in _held.Keys.Where(segment => segment.Ledger == ledger).ToArray())
        {
            _ = _held.Remove(segment);
        }
    }

    /// <summary>What a segment's drain gives: its end and its keys' next tokens once complete, or why it is not.</summary>
    private readonly union DrainStep(DrainedSegment, DrainScan.Open, ReplicaWriteOutcome.Dropped, ReplicaWriteOutcome.Backpressure);

    /// <summary>A segment drained or passed: its end, and the token each key it wrote plans next, which the next segment's check reads.</summary>
    private sealed record DrainedSegment(Slot End, Dictionary<byte[], RowToken>? Chain);

    /// <summary>The first failure among a segment's writes, which the drain throws once they settle.</summary>
    private sealed class FirstFailure
    {
        private readonly Lock _sync = new();

        public bool HasFailed
        {
            get
            {
                lock (_sync)
                {
                    return Exception is not null;
                }
            }
        }

        public ExceptionDispatchInfo? Exception { get; private set; }

        public void Record(Exception exception)
        {
            lock (_sync)
            {
                Exception ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    /// <summary>
    /// One key's write of a segment, as its rows resolve it (archive.md §2.1): the key, its write's builder, which counts
    /// the rows an opener supersedes, and each command's body retained over the batch's pages with the charge it took.
    /// </summary>
    private sealed class KeyWrite(byte[] key, LedgerSegmentId segment, RowToken token) : IDisposable
    {
        private readonly ArchiveWriteBuilder _builder = new(segment, token);
        private readonly List<(ArcBuffer Body, long Charge)> _bodies = [];

        public byte[] Key { get; } = key;

        public RowToken Token => _builder.Token;

        /// <summary>Adds a row's command and body, and returns the charge the bodies it superseded took, which it releases.</summary>
        public long Add(ArchiveCommand command, ArcBuffer body, long charge)
        {
            long released = 0;
            if (_builder.Add(command) > 0)
            {
                foreach (var (superseded, taken) in _bodies)
                {
                    superseded.Dispose();
                    released += taken;
                }

                _bodies.Clear();
            }

            _bodies.Add((body, charge));
            return released;
        }

        public ArchiveWrite ToWrite() => _builder.ToWrite();

        /// <summary>Returns the bodies of the write's commands, a <c>Delete</c>'s empty, chained over the retained pages.</summary>
        public ReadOnlySequence<byte> Content() => ContentChain.Of(_bodies.Select(static body => body.Body));

        public void Dispose()
        {
            foreach (var (body, _) in _bodies)
            {
                body.Dispose();
            }

            _bodies.Clear();
        }
    }

    /// <summary>A segment's key writes and folded messages, in the order their keys first appear.</summary>
    private sealed class Segment(
        LedgerSegmentId id,
        LedgerMetadata ledger,
        ImmutableArray<MessageFolds.RegisteredProcessor> messageProcessors,
        bool skipUnknownChannelMutant) : DrainRows(id)
    {
        private readonly MessageFolds _messages = new(ledger, id, messageProcessors, skipUnknownChannelMutant);
        private readonly Dictionary<byte[], KeyWrite> _byKey = [with(LedgerSegmentDictionary.KeyComparer.Instance)];

        public List<KeyWrite> Writes { get; } = [];

        public bool HasMessages => _messages.HasMessages;

        public Task CommitMessagesAsync(CancellationToken cancellationToken) => _messages.CommitAsync(cancellationToken);

        protected internal override long Add(LedgerScanItem item)
        {
            var lookup = _byKey.GetAlternateLookup<ReadOnlySpan<byte>>();
            long charge = 0;
            foreach (var row in item.Rows)
            {
                if (row.Kind == RowKind.Message)
                {
                    charge += _messages.Add(row.Tag.Slot, row.Tag.Index, row.Channel, row.Body);
                    continue;
                }

                if (!lookup.TryGetValue(row.Key, out var write))
                {
                    Debug.Assert(row.IsDefining, "A key's first row in its segment defines it (ledgers.md §2.2).");
                    write = new KeyWrite(row.Key.ToArray(), Id, row.Token);
                    _byKey.Add(write.Key, write);
                    Writes.Add(write);
                    charge += row.Key.Length + KeyEntryBytes;
                }

                Debug.Assert(row.Token == write.Token, "Every row of a key in a segment carries its defining row's token.");
                var taken = CommandEntryBytes + row.BodyLength;
                var command = row.Kind switch
                {
                    RowKind.Append => ArchiveCommand.Append(row.BodyLength),
                    RowKind.Replace => ArchiveCommand.Replace(row.BodyLength),
                    RowKind.Delete => ArchiveCommand.Delete(row.Body.ToArray()),
                    RowKind.Message => throw new UnreachableException("RowKind.Message is handled before command conversion."),
                    _ => throw new UnreachableException("A row's kind is defined."),
                };
                charge += taken - write.Add(command, row.Kind == RowKind.Delete ? default : row.RetainBody(), taken);
            }

            return charge;
        }

        protected internal override void Clear()
        {
            _messages.Clear();
            foreach (var write in Writes)
            {
                write.Dispose();
            }

            Writes.Clear();
            _byKey.Clear();
        }
    }
}
