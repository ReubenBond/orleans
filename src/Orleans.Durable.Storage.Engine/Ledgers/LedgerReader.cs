using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;


namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Reads any live ledger (ledgers.md §2.5), and owns the process's one <see cref="Ledger"/> of each ledger it reads.
/// </summary>
/// <remarks>
/// <para>
/// A ledger is read only where it is decided: an open one below the known contiguous tail of the process's
/// <see cref="Ledger"/>, which its seal checks raise, and a closed one below the map's recorded <c>T</c>, which the reader
/// passes to the <see cref="Ledger"/> (LG-I8, LP-I11). A replica which reports the ledger dropped, or a seal check which
/// finds an open ledger sealed, makes the reader re-read the map, so its caller's next lookup starts again from a newer
/// map (ledgers.md §8.4).
/// </para>
/// <para>
/// The reader opens each ledger's <see cref="Ledger"/> from the map's <see cref="LedgerMetadata"/>, over exactly the
/// replicas it lists (LG-I7), and holds it while any operation or writer leases it. Once its map view no longer lists the
/// ledger, the reader releases it, and the last lease disposes it. A lookup takes no lock: each ledger's leases are
/// counted with interlocked operations, and only a map change, an opening, or a disposal does more.
/// </para>
/// </remarks>
public sealed class LedgerReader : IAsyncDisposable
{
    /// <summary>The default of <see cref="MaxReadAheadBytes"/>: 64 MiB.</summary>
    internal const long DefaultMaxReadAheadBytes = 64L << 20;
    private readonly ILedgerReplicaTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<LedgerId, Holding> _ledgers = new();

    // Every holding not yet disposed, the ones the map no longer lists among them, whose reads disposal stops, by the order
    // in which each opened: a key hashes by its value, so disposal stops them in an order a seeded simulation replays, which
    // an identity hash's would not be (verification.md §7.2).
    private readonly ConcurrentDictionary<long, Holding> _holdings = new();

    // Cancelled by disposal: ends the map re-reads which leased operations make.
    private readonly CancellationTokenSource _disposing = new();
    private readonly ConcurrentQueue<Exception> _disposalFailures = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The ledgers and replica sets not yet disposed, plus one which the reader holds until its own disposal: the reader is
    // drained once it reaches zero, after which nothing opens again.
    private int _live = 1;
    private long _opened;
    private ulong _prunedEpoch;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerReader"/> class over the process's map view, reaching each
    /// ledger's replicas through <paramref name="transport"/>. Construction performs no I/O.
    /// </summary>
    /// <param name="map">The process's map view, which the reader re-reads when a replica reports a seal or a drop.</param>
    /// <param name="transport">Reaches the replicas the map lists.</param>
    /// <param name="timeProvider">The clock of each <see cref="Ledger"/>'s recovery backoff and read hedges.</param>
    /// <param name="localNode">
    /// The process's own node, whose replica of a ledger, when the map lists one, each read below the ledger's known
    /// contiguous tail asks first (ledger-protocol.md §8.4), and which the process's writer registry serves; or
    /// <see langword="null"/> for a process without a node, such as a client, whose reads rotate over the replicas.
    /// </param>
    public LedgerReader(LedgerMapView map, ILedgerReplicaTransport transport, TimeProvider timeProvider, NodeId? localNode)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Map = map;
        _transport = transport;
        _timeProvider = timeProvider;
        LocalNode = localNode;
    }

    /// <summary>
    /// Gets the bytes of replies a scan holds once they arrived and before it yields them, past which it issues no read
    /// (ledgers.md §2.5). The reads still in flight are not counted: each holds at most the payload limit, so a scan holds
    /// at most this bound plus <c>K</c> payload limits.
    /// </summary>
    internal long MaxReadAheadBytes { get; init; } = DefaultMaxReadAheadBytes;

    /// <summary>Gets or sets a test seam told, as a scan issues each read, the bytes of the replies it holds.</summary>
    internal Action<long>? ReadIssued { get; set; }

    /// <summary>
    /// Gets the process's map view, which the reader re-reads when a replica reports a seal or a drop. A process has one
    /// reader, which its writer registry and its history reader share, so a seal check through it in the process which
    /// serves a ledger's writer covers that writer's durable proposals (ledgers.md §2.5).
    /// </summary>
    public LedgerMapView Map { get; }

    /// <summary>
    /// Gets the process's own node, or <see langword="null"/> for a process without one: the one source of the node a
    /// process's reads prefer and its writer registry serves.
    /// </summary>
    public NodeId? LocalNode { get; }

    /// <summary>
    /// Gets the observer every recovery of a ledger this reader opens on nodes calls with each replica answer it handles,
    /// fixed when the reader is built, or <see langword="null"/>; deterministic simulation logs them for trace validation
    /// (docs/spec/verification.md §5.1).
    /// </summary>
    internal Action<RecoveryStep>? RecoveryObserver { get; init; }

    /// <summary>
    /// Gets the process's object-ledger store, which opens the storage of each ledger the map places in the object store
    /// (object-ledgers.md §2.2), or <see langword="null"/> for a process which reaches none. Prefer
    /// <see cref="ObjectStoreProvider"/> when access must be gated behind a startup identity check (OL-I1).
    /// </summary>
    public ObjectLedgerStore? Objects { get; init; }

    /// <summary>
    /// Gets a gated provider for the process's object-ledger store (OL-I1): invoked whenever an object-store ledger is
    /// opened, so a composition fails closed until the store's record is verified against the node registry.
    /// </summary>
    public Func<ObjectLedgerStore>? ObjectStoreProvider { get; init; }

    /// <summary>
    /// Gets the primary quorum lease configuration for replica-backed ledgers opened by this reader
    /// (ledger-protocol.md §8.6, ledgers.md §2.5). Defaults to <see cref="LedgerLeaseOptions.Default"/>.
    /// </summary>
    public LedgerLeaseOptions LeaseOptions { get; init; } = LedgerLeaseOptions.Default;

    /// <summary>Gets the replica transport used by this reader, shared with the process's writer registry for heartbeats.</summary>
    internal ILedgerReplicaTransport Transport => _transport;

    /// <summary>Gets a value indicating whether the reader was disposed.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Reads the decided batch at <paramref name="slot"/> of the ledger <paramref name="metadata"/> describes.
    /// </summary>
    /// <param name="metadata">The ledger, as a map value the caller read lists it.</param>
    /// <param name="slot">
    /// A decided slot: below a closed ledger's <c>T</c>, or below an open ledger's known contiguous tail in this process,
    /// which its seal checks (<see cref="ReadTailAsync"/>) raise.
    /// </param>
    /// <param name="cancellationToken">Stops the caller's wait.</param>
    /// <returns>
    /// <see cref="LedgerRead.Data"/>, which owns the batch's verified bytes and is the first batch a replica reports;
    /// <see cref="ReplicaWriteOutcome.Dropped"/>, once the reader re-read the map, also for a slot past the known
    /// contiguous tail of a ledger the re-read map revokes, whose <see cref="Ledger"/> the reader opened afresh; or
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>, when backpressure kept the read from deciding, which asserts nothing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not decided.</exception>
    /// <exception cref="InvalidDataException">The batch breaks a rule of its format (ledgers.md §7): corruption.</exception>
    /// <exception cref="NotSupportedException">
    /// The ledger is in the object store, and the reader has no <see cref="Objects"/>.
    /// </exception>
    public async ValueTask<LedgerRead> ReadAsync(LedgerMetadata metadata, Slot slot, CancellationToken cancellationToken = default)
    {
        using var lease = LeaseLedger(metadata);
        var decided = lease.Ledger.KnownContiguousTail;
        if (slot >= decided)
        {
            return await DroppedPastTailAsync(metadata, decided, nameof(slot), slot).ConfigureAwait(false);
        }

        return await ReadSlotAsync(lease.Ledger, slot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the contiguous tail of the ledger <paramref name="metadata"/> describes and whether it is sealed.
    /// </summary>
    /// <param name="metadata">The ledger, as a map value the caller read lists it.</param>
    /// <param name="cancellationToken">Stops the caller's wait.</param>
    /// <returns>
    /// For a closed ledger, the <see cref="LedgerTail"/> <c>(T, true)</c> from the map without I/O. For an open one, a
    /// seal check of <c>Q</c> replicas begun after the call (LP-I9): its <see cref="LedgerTail"/>, with the process's
    /// known contiguous tail afterwards, once the reader has re-read the map when the check found the ledger sealed; or
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>, when backpressure kept the check from deciding, which asserts
    /// nothing.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The ledger is open in the object store, and the reader has no <see cref="Objects"/>.
    /// </exception>
    public async ValueTask<LedgerTailRead> ReadTailAsync(LedgerMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.Status.Value is LedgerStatus.Closed closed)
        {
            return new LedgerTail(closed.T, IsSealed: true);
        }

        using var lease = LeaseLedger(metadata);
        var answer = await lease.Ledger.ReadTailAsync(cancellationToken).ConfigureAwait(false);
        if (answer.Value is LedgerTail { IsSealed: true })
        {
            await RereadMapAsync().ConfigureAwait(false);
        }

        return answer;
    }

    /// <summary>
    /// Reads the decided batches of the ledger <paramref name="metadata"/> describes with <c>from ≤ slot &lt; below</c>,
    /// in slot order, each with the scan's <see cref="LedgerSegmentView"/>, which resolves every key's rows (ledgers.md
    /// §2.5).
    /// </summary>
    /// <inheritdoc cref="ScanAsync(LedgerMetadata, Slot, Slot, ReadOnlyMemory{byte}, CancellationToken)"/>
    public IAsyncEnumerable<LedgerScanItem> ScanAsync(
        LedgerMetadata metadata,
        Slot from,
        Slot below,
        CancellationToken cancellationToken = default) =>
        ScanAsync(metadata, from, below, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>
    /// Reads the decided batches of the ledger <paramref name="metadata"/> describes with <c>from ≤ slot &lt; below</c>,
    /// in slot order, each with the scan's <see cref="LedgerSegmentView"/> advanced past it, which lends its resolved rows
    /// (ledgers.md §2.5). The scan first reads the batch at <paramref name="from"/> and rewinds by its segment offset to its
    /// segment's start, then reads forward from there, advancing its view over every batch, so it resolves each row from
    /// <paramref name="from"/> on. It keeps up to <c>K</c> reads in flight, and the replies it holds read ahead within
    /// <see cref="MaxReadAheadBytes"/>, beside the replica transport's per-node budget.
    /// </summary>
    /// <param name="metadata">The ledger, as a map value the caller read lists it.</param>
    /// <param name="from">The first slot.</param>
    /// <param name="below">
    /// The slot after the last: at most the ledger's known contiguous tail in this process, a tail the caller learned
    /// through this reader, or a closed ledger's <c>T</c>.
    /// </param>
    /// <param name="key">
    /// The key whose rows the view resolves, alone and in constant memory; or empty, for every key's.
    /// </param>
    /// <param name="cancellationToken">Stops the scan.</param>
    /// <returns>
    /// Each batch as a <see cref="LedgerScanItem"/>: its <see cref="LedgerRead.Data"/>, which its caller disposes once it
    /// has read the rows the view lends, and the view, which lends them until the scan advances; the scan owns the view
    /// and disposes it when the enumeration ends. A last item holds <see cref="ReplicaWriteOutcome.Dropped"/> when a
    /// replica reports the ledger dropped, once the reader re-read the map, also when <paramref name="below"/> is past the
    /// known contiguous tail of a ledger the re-read map revokes, whose <see cref="Ledger"/> the reader opened afresh; or
    /// <see cref="ReplicaWriteOutcome.Backpressure"/> when backpressure kept a read from deciding, after which the caller
    /// resumes the scan from that slot. The batches it read ahead of the last are released.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="below"/> is below <paramref name="from"/>, at once, or, once enumerated, above the known contiguous
    /// tail of a ledger the map still lists, so a slot it reads would not be decided.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A batch breaks a rule of its format or of its segment (ledgers.md §7): corruption.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The ledger is in the object store, and the reader has no <see cref="Objects"/>.
    /// </exception>
    public IAsyncEnumerable<LedgerScanItem> ScanAsync(
        LedgerMetadata metadata,
        Slot from,
        Slot below,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(below, from);
        if (!key.IsEmpty)
        {
            KeyRange.CheckKey(key.Span);
        }

        return ScanDecidedAsync(metadata, from, below, key, held: null, window: null, cancellationToken);
    }

    /// <summary>
    /// Scans as <see cref="ScanAsync(LedgerMetadata, Slot, Slot, ReadOnlyMemory{byte}, CancellationToken)"/> does, advancing
    /// <paramref name="view"/>, which the caller holds across scans and disposes: when the view last advanced past the slot
    /// before <paramref name="from"/>, as after a scan which backpressure ended there, the scan resumes from
    /// <paramref name="from"/> without rewinding; otherwise it rewinds to the segment start.
    /// </summary>
    /// <param name="metadata">The ledger, as a map value the caller read lists it.</param>
    /// <param name="from">The first slot.</param>
    /// <param name="below">The slot after the last.</param>
    /// <param name="view">The view of the ledger's segments, of every key or one, which the caller owns.</param>
    /// <param name="window">
    /// A window of reads in flight which the scan shares with others running beside it, or null for none: the scan takes
    /// a place in it for each read it issues in turn and frees the place once it has taken that read's answer, beside its
    /// own bound of <c>K</c>, and waits for a place only while it has no read in flight, so scans which share a window of
    /// <c>K</c> places keep at most <c>K</c> reads in flight together (archive.md §8.1).
    /// </param>
    /// <param name="cancellationToken">Stops the scan.</param>
    internal IAsyncEnumerable<LedgerScanItem> ScanAsync(
        LedgerMetadata metadata,
        Slot from,
        Slot below,
        LedgerSegmentView view,
        SemaphoreSlim? window = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(below, from);
        ArgumentNullException.ThrowIfNull(view);
        return ScanDecidedAsync(metadata, from, below, ReadOnlyMemory<byte>.Empty, view, window, cancellationToken);
    }

    /// <summary>
    /// Returns the known contiguous tail of the ledger <paramref name="metadata"/> describes in this process, which a scan
    /// may read below without a seal check: the tail the process's operations on the ledger learned (LP-I11).
    /// </summary>
    /// <param name="metadata">The ledger, as a map value the caller read lists it.</param>
    /// <returns>The known contiguous tail.</returns>
    /// <exception cref="NotSupportedException">
    /// The ledger is in the object store, whose storage the reader builds from row 7.3 (object-ledgers.md).
    /// </exception>
    internal Slot KnownContiguousTail(LedgerMetadata metadata)
    {
        using var lease = LeaseLedger(metadata);
        return lease.Ledger.KnownContiguousTail;
    }

    /// <summary>
    /// Learns a contiguous tail of the ledger <paramref name="metadata"/> describes from the replica on this process's node,
    /// without a seal check, and raises the ledger's known contiguous tail in this process to it (LP-I11, ledgers.md
    /// §8.5): a closed ledger's is its <c>T</c>, and a process with no replica of an open ledger learns nothing.
    /// </summary>
    /// <param name="metadata">The ledger, as a map value the caller read lists it.</param>
    /// <param name="cancellationToken">Stops the learn.</param>
    /// <returns>The ledger's known contiguous tail in this process after the call, which a scan may read below.</returns>
    /// <exception cref="NotSupportedException">
    /// The ledger is in the object store, whose storage the reader builds from row 7.3 (object-ledgers.md).
    /// </exception>
    public async ValueTask<Slot> LearnTailAsync(LedgerMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.Status.Value is LedgerStatus.Closed closed)
        {
            return closed.T;
        }

        using var lease = LeaseLedger(metadata);
        return await lease.Ledger.LearnTailAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Stops the reads of every <see cref="Ledger"/> the reader holds, leased or not, and its map re-reads; releases each;
    /// and completes once the last lease of each has ended and each has been disposed with the replicas it owns.
    /// </summary>
    /// <remarks>
    /// A leased read, seal check, or recovery fails with <see cref="ObjectDisposedException"/> once the replica calls it
    /// awaits end under its ledger's cancelled lifetime, and a ledger's disposal cancels the replica calls of the writes
    /// its lease admitted (ledger-protocol.md §6.3), so disposal waits only for replica calls a cancellation ends, never
    /// for a lease held across a slow replica or for a transport's timeout (ledgers.md §2.5). Two demands on its callers
    /// bound it: every served writer is disposed first, and a caller ends its lease, a scan's among them, once a read
    /// fails or its own work stops, so a lease held past that is the caller's defect, which holds the disposal.
    /// </remarks>
    /// <returns>A task which completes once every <see cref="Ledger"/> is disposed.</returns>
    /// <exception cref="AggregateException">Disposing a <see cref="Ledger"/> or its replicas failed.</exception>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // ledger-protocol.md §6.3: the writers' disposal comes first, since stopping the reads would end a served
            // writer's open check and the recovery it helps.
            Debug.Assert(!_holdings.Values.Any(static holding => holding.HasWriter), "A served writer is disposed before its reader.");
            await _disposing.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_holdings.Values.Select(static holding => holding.Ledger.StopReadsAsync())).ConfigureAwait(false);
            foreach (var (id, holding) in _ledgers)
            {
                if (_ledgers.TryRemove(KeyValuePair.Create(id, holding)))
                {
                    holding.Release();
                }
            }

            EndLive();
        }

        await _drained.Task.ConfigureAwait(false);
        _disposing.Dispose();
        if (!_disposalFailures.IsEmpty)
        {
            throw new AggregateException("Disposing a ledger's replicas failed.", _disposalFailures);
        }
    }

    /// <summary>
    /// Leases the process's <see cref="Ledger"/> of the ledger <paramref name="metadata"/> describes, opening it over the
    /// replicas the map lists when the reader holds none, and passing a closed ledger's recorded <c>T</c> to it
    /// (LP-I11). The reader keeps it until the lease is disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    internal LedgerLease LeaseLedger(LedgerMetadata metadata)
    {
        Holding holding;
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            PruneIfChanged();
            if (_ledgers.TryGetValue(metadata.Id, out var held))
            {
                if (held.TryEnterLease())
                {
                    holding = held;
                    break;
                }

                // Its disposal began: it leaves the dictionary, and the next pass opens the ledger again.
                _ = _ledgers.TryRemove(KeyValuePair.Create(metadata.Id, held));
                continue;
            }

            if (!TryEnterLive())
            {
                throw new ObjectDisposedException(nameof(LedgerReader));
            }

            Holding opened;
            try
            {
                opened = new Holding(this, Interlocked.Increment(ref _opened), metadata, Open(metadata));
            }
            catch
            {
                EndLive();
                throw;
            }

            _ = _holdings.TryAdd(opened.Opened, opened);
            if (Volatile.Read(ref _disposed) != 0)
            {
                // The reader's disposal may have stopped the reads before this holding joined them.
                _ = opened.Ledger.StopReadsAsync();
            }

            if (Map.Current.NoLongerLists(metadata.Id))
            {
                // A caller whose map predates the view's leases a ledger the view no longer lists: its Ledger serves
                // that lease alone.
                opened.Release();
                holding = opened;
                break;
            }

            if (_ledgers.TryAdd(metadata.Id, opened))
            {
                if (Volatile.Read(ref _disposed) != 0 && _ledgers.TryRemove(KeyValuePair.Create(metadata.Id, opened)))
                {
                    // The reader's disposal may have released the ledgers before this one joined them.
                    opened.Release();
                }

                holding = opened;
                break;
            }

            // Another caller opened the ledger first: this one is disposed, and the next pass leases that one.
            opened.Release();
            opened.EndLease();
        }

        if (metadata.Status.Value is LedgerStatus.Closed closed)
        {
            holding.ObserveRecordedTail(closed.T);
        }

        return new LedgerLease(holding);
    }

    /// <summary>
    /// Answers a read past the known contiguous tail: <see cref="ReplicaWriteOutcome.Dropped"/> when a map read begun
    /// after the call revokes the ledger, since the caller learned that tail from a <see cref="Ledger"/> the reader
    /// released once its view revoked the ledger, and the reader opened a fresh one for this lease; otherwise the slot
    /// is not decided, a caller's error. A re-read the reader's disposal cancels leaves no map read begun after the call,
    /// so the read then answers <see cref="ObjectDisposedException"/>, which asserts nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The map still lists the ledger.</exception>
    /// <exception cref="ObjectDisposedException">The reader's disposal began.</exception>
    private async ValueTask<LedgerRead> DroppedPastTailAsync(LedgerMetadata metadata, Slot decided, string parameter, Slot slot)
    {
        await RereadMapAsync().ConfigureAwait(false);
        if (Map.Current.NoLongerLists(metadata.Id))
        {
            return new LedgerRead(ReplicaWriteOutcome.Dropped.Instance);
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        throw new ArgumentOutOfRangeException(parameter, slot, Undecided(metadata, decided));
    }

    private static string Undecided(LedgerMetadata metadata, Slot decided) =>
        metadata.Status.Value is LedgerStatus.Closed closed
            ? string.Create(CultureInfo.InvariantCulture, $"Only the slots below the ledger's recorded T {closed.T} are decided.")
            : string.Create(CultureInfo.InvariantCulture, $"Only the slots below the ledger's known contiguous tail {decided} are decided; a seal check raises it.");

    /// <summary>
    /// Verifies a batch read from a replica and returns its row count; a batch which breaks a rule of its format is
    /// released and reported as corruption (conventions.md §4).
    /// </summary>
    private static int VerifiedRowCount(ArcBuffer batch)
    {
        if (BatchView.Decode(batch, out var view) is { } rejection)
        {
            batch.Dispose();
            throw rejection;
        }

        return view.Count;
    }

    private async IAsyncEnumerable<LedgerScanItem> ScanDecidedAsync(
        LedgerMetadata metadata,
        Slot from,
        Slot below,
        ReadOnlyMemory<byte> key,
        LedgerSegmentView? held,
        SemaphoreSlim? window,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = LeaseLedger(metadata);
        var ledger = lease.Ledger;
        var decided = ledger.KnownContiguousTail;
        if (below > decided)
        {
            yield return new LedgerScanItem(await DroppedPastTailAsync(metadata, decided, nameof(below), below).ConfigureAwait(false), view: null);
            yield break;
        }

        if (from == below)
        {
            yield break;
        }

        using var owned = held is null ? key.IsEmpty ? new LedgerSegmentView(metadata.Id) : new LedgerSegmentView(metadata.Id, key.Span) : null;
        var view = held ?? owned!;

        var scanFrom = from;
        if (from > Slot.Zero && !view.ResumesAt(from))
        {
            // Read slot from first to learn its segment offset before scanning.
            var firstRead = await ReadSlotAsync(ledger, from, cancellationToken).ConfigureAwait(false);
            if (!firstRead.TryGetValue(out LedgerRead.Data firstData))
            {
                yield return new LedgerScanItem(firstRead, view: null);
                yield break;
            }

            var firstOwned = true;
            try
            {
                var offset = firstData.View.LedgerSegmentOffset;
                if (offset > (ulong)from.Value)
                {
                    throw DecodeRejection.Create(
                        LedgerSegmentView.ReasonOffsetMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"The batch at slot {from} has segment offset {offset}, which reaches past slot 0."));
                }

                var start = from - (long)offset;
                if (start == from)
                {
                    if (Advance(view, firstData) is { } firstRejection)
                    {
                        throw firstRejection;
                    }

                    firstOwned = false;
                    yield return new LedgerScanItem(firstRead, view);
                    scanFrom = from + 1;
                }
                else if (from + 1 == below)
                {
                    // Single-slot scan with rewind: scan [start, from) then yield firstData without re-reading from.
                    await foreach (var rewindItem in ScanRangeAsync(ledger, start, from, yieldFrom: from, view, window, cancellationToken).ConfigureAwait(false))
                    {
                        yield return rewindItem;
                        yield break;
                    }

                    if (Advance(view, firstData) is { } firstRejection)
                    {
                        throw firstRejection;
                    }

                    firstOwned = false;
                    yield return new LedgerScanItem(firstRead, view);
                    yield break;
                }
                else
                {
                    // Dispose firstData and issue a single [start, below) range read so extent-backed storages
                    // do not perform two overlapping 8 MiB reads for [start, from) and [from + 1, below).
                    scanFrom = start;
                }
            }
            finally
            {
                if (firstOwned)
                {
                    firstData.Dispose();
                }
            }
        }

        if (scanFrom < below)
        {
            await foreach (var item in ScanRangeAsync(ledger, scanFrom, below, yieldFrom: from, view, window, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private async IAsyncEnumerable<LedgerScanItem> ScanRangeAsync(
        Ledger ledger,
        Slot scanFrom,
        Slot below,
        Slot yieldFrom,
        LedgerSegmentView view,
        SemaphoreSlim? window,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var slot = scanFrom;
        await foreach (var raw in ReadRangeFromLedger(ledger, scanFrom, below, window, cancellationToken).ConfigureAwait(false))
        {
            var read = await ConvertScanReadAsync(slot, raw).ConfigureAwait(false);
            if (!read.TryGetValue(out LedgerRead.Data data))
            {
                yield return new LedgerScanItem(read, view: null);
                yield break;
            }

            if (Advance(view, data) is { } rejection)
            {
                data.Dispose();
                throw rejection;
            }

            var currentSlot = slot;
            slot += 1;
            if (currentSlot < yieldFrom)
            {
                data.Dispose();
                continue;
            }

            yield return new LedgerScanItem(read, view);
        }
    }

    private IAsyncEnumerable<ReplicaRead> ReadRangeFromLedger(
        Ledger ledger,
        Slot from,
        Slot below,
        SemaphoreSlim? window,
        CancellationToken cancellationToken) =>
        MaxReadAheadBytes != DefaultMaxReadAheadBytes || ReadIssued is not null
            ? ledger.ReadRangePointWiseAsync(from, below, MaxReadAheadBytes, window, ReadIssued, cancellationToken)
            : ledger.ReadRangeAsync(from, below, window, cancellationToken);

    /// <summary>Advances <paramref name="view"/> past the batch <paramref name="data"/> holds, by the segment rules.</summary>
    private static InvalidDataException? Advance(LedgerSegmentView view, LedgerRead.Data data) => view.Advance(data.Slot, data.View);

    private async ValueTask<LedgerRead> ConvertScanReadAsync(Slot slot, ReplicaRead answer)
    {
        if (answer.TryGetValue(out ArcBuffer batch))
        {
            return new LedgerRead(new LedgerRead.Data(slot, batch, VerifiedRowCount(batch)));
        }

        if (answer.TryGetValue(out ReplicaWriteOutcome.Dropped? dropped))
        {
            await RereadMapAsync().ConfigureAwait(false);
            return new LedgerRead(dropped);
        }

        if (answer.TryGetValue(out ReplicaWriteOutcome.Backpressure? backpressure))
        {
            return new LedgerRead(backpressure);
        }

        throw new UnreachableException("LP-I10: a read below the known contiguous tail answers a batch, or fails, rather than Missing.");
    }

    /// <summary>
    /// Reads one decided slot through <paramref name="ledger"/>, verifying the batch it finds (ledgers.md §7).
    /// </summary>
    private async ValueTask<LedgerRead> ReadSlotAsync(Ledger ledger, Slot slot, CancellationToken cancellationToken) =>
        await ConvertScanReadAsync(slot, await ledger.ReadAsync(slot, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>
    /// Re-reads the map after a sealed or dropped answer (LG-I8), and releases the ledgers the newer map no longer lists.
    /// The answer stands whether or not the read succeeds: a failed read leaves the caller's next lookup to the view.
    /// </summary>
    private async Task RereadMapAsync()
    {
        try
        {
            _ = await Map.ReadAsync(_disposing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The answer which led to the read stands; the view decides the next lookup.
        }

        PruneIfChanged();
    }

    /// <summary>
    /// Once the map view has advanced, passes each closed ledger's recorded <c>T</c> to its <see cref="Ledger"/>, and
    /// releases each ledger the view no longer lists, whose last lease disposes it. One caller handles each epoch.
    /// </summary>
    private void PruneIfChanged()
    {
        var current = Map.Current;
        var pruned = Volatile.Read(ref _prunedEpoch);
        if (current.Epoch <= pruned || Interlocked.CompareExchange(ref _prunedEpoch, current.Epoch, pruned) != pruned)
        {
            return;
        }

        foreach (var (id, holding) in _ledgers)
        {
            if (current.Get(id)?.Status.Value is LedgerStatus.Closed closed)
            {
                holding.ObserveRecordedTail(closed.T);
            }
            else if (current.NoLongerLists(id) && _ledgers.TryRemove(KeyValuePair.Create(id, holding)))
            {
                holding.Release();
            }
        }
    }

    /// <summary>
    /// Opens the ledger over the storage its map entry's location names (ledger-protocol.md §2.7): the quorum over the
    /// replicas the map lists with it (LG-I7), or the process's object-ledger store (object-ledgers.md §2.1).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The ledger is in the object store, and the reader has no <see cref="Objects"/>.
    /// </exception>
    private Ledger Open(LedgerMetadata metadata) => metadata.Location switch
    {
        LedgerLocation.Nodes nodes => OpenQuorum(metadata, nodes),
        LedgerLocation.ObjectStore objectStore => OpenObjectStore(metadata, objectStore),
        null => throw new UnreachableException("conventions.md §15: a ledger's location holds a case."),
    };

    private Ledger OpenObjectStore(LedgerMetadata metadata, LedgerLocation.ObjectStore location)
    {
        var objects = ObjectStoreProvider is { } provider
            ? provider()
            : Objects ?? throw new NotSupportedException(
                string.Create(CultureInfo.InvariantCulture, $"Ledger {metadata.Id} is in the object store, which this process's reader does not reach."));
#pragma warning disable CA2000 // Ownership of the storage transfers to the ledger.
        return new Ledger(metadata.Id, objects.Open(metadata.Id, metadata.K), location);
#pragma warning restore CA2000
    }

    /// <summary>
    /// Returns whether a batch's bytes end in the CRC-32C of the bytes before them (ledgers.md §7): the check a read repair
    /// requires of the batch it copies to the reader's own replica (ledger-protocol.md §8.3).
    /// </summary>
    private static bool IsIntactBatch(ArcBuffer batch) => batch.Length >= BatchView.CrcLength && BatchView.CrcMatches(batch);

    private Ledger OpenQuorum(LedgerMetadata metadata, LedgerLocation.Nodes nodes)
    {
        // LG-I7: the Ledger reaches exactly the replicas the map lists with the ledger.
        var options = new LedgerReplicaOptions { WindowSize = metadata.K };
        var replicas = new ILedgerReplica[nodes.Replicas.Length];
        var opened = 0;
        try
        {
            for (; opened < replicas.Length; opened++)
            {
                replicas[opened] = _transport.Open(nodes.Replicas[opened], metadata.Id, options);
            }

#pragma warning disable CA2000 // Ownership of the quorum, and through it of the replicas, transfers to the ledger.
            return new Ledger(
                metadata.Id,
                LedgerQuorum.ForNodes(metadata.Id, nodes.Replicas, replicas, nodes.Quorum, options, _timeProvider, LocalNode, RecoveryObserver, IsIntactBatch),
                nodes,
                _timeProvider,
                LeaseOptions);
#pragma warning restore CA2000
        }
        catch
        {
            StartDisposal(replicas[..opened]);
            throw;
        }
    }

    private bool TryEnterLive()
    {
        var live = Volatile.Read(ref _live);
        while (live > 0)
        {
            var observed = Interlocked.CompareExchange(ref _live, live + 1, live);
            if (observed == live)
            {
                return true;
            }

            live = observed;
        }

        return false;
    }

    private void EndLive()
    {
        if (Interlocked.Decrement(ref _live) == 0)
        {
            _ = _drained.TrySetResult();
        }
    }

    /// <summary>
    /// Disposes a <see cref="Ledger"/> whose holding was released and whose last lease ended.
    /// </summary>
    private void StartDisposal(Holding holding)
    {
        _ = _holdings.TryRemove(holding.Opened, out _);
        _ = DisposeLiveAsync(holding.Ledger.DisposeAsync);
    }

    /// <summary>
    /// Disposes replicas which a failed opening left without a <see cref="Ledger"/>.
    /// </summary>
    private void StartDisposal(ILedgerReplica[] replicas)
    {
        var tracked = TryEnterLive();
        _ = DisposeReplicasAsync(replicas, tracked);
    }

    private async Task DisposeReplicasAsync(ILedgerReplica[] replicas, bool tracked)
    {
        foreach (var replica in replicas)
        {
            try
            {
                await replica.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _disposalFailures.Enqueue(exception);
            }
        }

        if (tracked)
        {
            EndLive();
        }
    }

    private async Task DisposeLiveAsync(Func<ValueTask> dispose)
    {
        try
        {
            await dispose().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _disposalFailures.Enqueue(exception);
        }
        finally
        {
            EndLive();
        }
    }

    /// <summary>
    /// A lease on the process's <see cref="Ledgers.Ledger"/> of one ledger, which the reader keeps until it is disposed.
    /// </summary>
    internal readonly struct LedgerLease : IDisposable
    {
        private readonly Holding _holding;
        private readonly bool _writer;

        internal LedgerLease(Holding holding)
            : this(holding, writer: false)
        {
        }

        private LedgerLease(Holding holding, bool writer)
        {
            _holding = holding;
            _writer = writer;
        }

        /// <summary>Gets the process's <see cref="Ledgers.Ledger"/> of the ledger.</summary>
        public Ledger Ledger => _holding.Ledger;

        /// <summary>
        /// Creates the ledger's one writer over <paramref name="lifetime"/>, which a transition's executor created before
        /// the map write which names it in the ledger's primary claim, and which the map lists the ledger under; the
        /// <see cref="Ledgers.Ledger"/> adopts it, and stops it at once when this process already recovered the ledger or
        /// found it sealed (LP-I14). The writer holds a lease of its own until it is disposed (ledgers.md §2.4, §2.5).
        /// </summary>
        /// <exception cref="ArgumentException">The lifetime's window is not the ledger's.</exception>
        /// <exception cref="InvalidOperationException">
        /// The ledger's primary claim names another lifetime (LP-I15), or the ledger already has a writer (LP-I1).
        /// </exception>
        public LedgerWriter CreateWriter(Sequencer lifetime, LedgerMapView map, LedgerWriterHost host, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(lifetime);
            ArgumentNullException.ThrowIfNull(map);
            ArgumentNullException.ThrowIfNull(host);
            if (_holding.Metadata.Primary.Sequencer != lifetime.InstanceId)
            {
                throw new InvalidOperationException(
                    "A ledger's writer serves the lifetime its primary claim names, once the map lists the claim (LP-I15).");
            }

            var entered = _holding.TryEnterLease();
            Debug.Assert(entered, "A held lease keeps its holding from disposal.");
            _holding.EnterWriter();
            var writerLease = new LedgerLease(_holding, writer: true);
            try
            {
                return Ledger.CreateWriter(lifetime, timeProvider, new LedgerWriter.Serving(_holding.Metadata.Range, map, host, writerLease));
            }
            catch
            {
                writerLease.Dispose();
                throw;
            }
        }

        /// <summary>Ends the lease.</summary>
        public void Dispose()
        {
            if (_writer)
            {
                _holding.EndWriter();
            }

            _holding?.EndLease();
        }
    }

    /// <summary>
    /// One <see cref="Ledgers.Ledger"/> the reader holds, with its leases, counted with interlocked operations: once the
    /// reader releases it and its last lease ends, it is disposed, and no lease begins afterwards.
    /// </summary>
    internal sealed class Holding(LedgerReader reader, long opened, LedgerMetadata metadata, Ledger ledger)
    {
        // The leases; int.MinValue once its disposal began. Its opener holds the first.
        private int _leases = 1;
        private int _released;
        private int _writers;

        /// <summary>Gets the holding's place in the order in which the reader opened its holdings, from 1.</summary>
        public long Opened { get; } = opened;

        public Ledger Ledger { get; } = ledger;

        /// <summary>Gets the entry the ledger was opened from, whose range and replicas are fixed for its life.</summary>
        public LedgerMetadata Metadata { get; } = metadata;

        /// <summary>Begins a lease, unless the holding's disposal began.</summary>
        public bool TryEnterLease()
        {
            var leases = Volatile.Read(ref _leases);
            while (leases >= 0)
            {
                var observed = Interlocked.CompareExchange(ref _leases, leases + 1, leases);
                if (observed == leases)
                {
                    return true;
                }

                leases = observed;
            }

            return false;
        }

        /// <summary>Gets whether a served writer of the ledger holds a lease, until the writer is disposed.</summary>
        public bool HasWriter => Volatile.Read(ref _writers) != 0;

        public void EnterWriter() => _ = Interlocked.Increment(ref _writers);

        public void EndWriter() => _ = Interlocked.Decrement(ref _writers);

        /// <summary>Ends a lease; the last one of a released holding disposes its <see cref="Ledgers.Ledger"/>.</summary>
        public void EndLease()
        {
            if (Interlocked.Decrement(ref _leases) == 0 && Volatile.Read(ref _released) != 0)
            {
                TryStartDisposal();
            }
        }

        /// <summary>Releases the holding: its last lease disposes it, or this call does when it has none.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                TryStartDisposal();
            }
        }

        /// <summary>
        /// Passes the ledger's recorded <c>T</c> to its <see cref="Ledgers.Ledger"/>, which raises its known contiguous
        /// tail; repeating it changes nothing (LP-I11).
        /// </summary>
        public void ObserveRecordedTail(Slot recordedTail) => Ledger.ObserveRecordedTail(recordedTail);

        private void TryStartDisposal()
        {
            // The exchange and the decrement each fence before reading the other's write, so of a release and the last
            // lease's end, at least one sees the other and starts the disposal, and the compare-exchange lets only one.
            if (Interlocked.CompareExchange(ref _leases, int.MinValue, 0) == 0)
            {
                reader.StartDisposal(this);
            }
        }
    }
}
