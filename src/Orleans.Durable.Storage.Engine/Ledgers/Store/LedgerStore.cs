using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal;
using WalLog = Orleans.Durable.Storage.Engine.Wal.WriteAheadLog;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// One node's store of ledger replicas, which multiplexes every replica's writes and seals, and its own create
/// and drop records, through one WAL (ledgerstore.md).
/// </summary>
/// <remarks>
/// <para>
/// The store owns one node root: <c>{root}/cluster</c> is the root's cluster claim (LS-I12), <c>{root}/wal/</c> holds the
/// node file, the WAL files, and the catalog checkpoints, and <c>{root}/slots/{handle}/</c> holds one live replica's
/// slot-index checkpoints, from its first. The WAL decides existence (LS-I1): the catalog is the fold of its create and
/// drop records over the newest valid catalog checkpoint, which persists it with its tombstones. A replica's first write,
/// recovery write, or seal creates it unless the catalog holds its ledger's tombstone, and a drop removes it and leaves
/// the tombstone for the node's life (LS-I2, LR-I7).
/// </para>
/// <para>
/// Each WAL record is the 8-byte record key (<c>ulong</c>, little-endian) followed by its body (ledgerstore.md §7.3).
/// The key is <c>(handle &lt;&lt; 40) | slot</c>. A slot record's body is the slot's non-empty DATA value. A store record
/// has the store handle, 2<sup>24</sup> − 1, its kind in the slot field, and a body of 16-byte
/// <c>(ledger, handle, window)</c> pairs for a create, the replica's <c>(ledger, handle)</c> for a seal, or the dropped
/// ledger's ID for a drop.
/// </para>
/// </remarks>
public sealed partial class LedgerStore : ILedgerStore, IAsyncDisposable
{
    internal const string WalDirectoryName = "wal";

    internal const string SlotsDirectoryName = "slots";

    private readonly WalStorage _storage;
    private readonly WalDirectory _directory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private readonly long _leaseDurationTicks;
    private readonly long _bootTimestamp;
    private readonly ReplicaWriteOutcome.Written _writtenWithLease;
    private readonly int _pageSize;
    private readonly long _fileSize;
    [SuppressMessage("Usage", "CA2213", Justification = "Disposal waits for the gate rather than disposing it, so a later creation or drop acquires it, observes the disposal, and releases it.")]
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<uint, LiveReplica> _ledgers = [];
    private readonly Dictionary<LedgerId, LiveReplica> _byLedger = [];
    private readonly List<CreationRequest> _waitingCreations = [];

    // The drops in progress, which disposal waits for: each finishes its replica's files after releasing the catalog gate.
    private readonly HashSet<Task> _drops = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly CatalogCheckpoints _catalogCheckpoints;
    private ReplicaCatalog _catalog;
    private bool _creating;
    private WalPage _lastPage;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    // The reported failure which stopped the store, set once, under the lock; null while it serves (conventions.md §14).
    private ExceptionDispatchInfo? _stopped;

    private LedgerStore(
        WalStorage storage,
        WalDirectory directory,
        WalLog wal,
        NodeId nodeId,
        ReplicaCatalog catalog,
        CatalogCheckpoints catalogCheckpoints,
        Guid cluster,
        LedgerStoreOptions options,
        LedgerStoreCheckpointOptions checkpointOptions,
        TimeProvider timeProvider)
    {
        _storage = storage;
        NodeId = nodeId;
        Cluster = cluster;
        _directory = directory;
        Wal = wal;
        _catalog = catalog;
        _catalogCheckpoints = catalogCheckpoints;
        Options = options;
        CheckpointOptions = checkpointOptions;
        _timeProvider = timeProvider;
        _leaseDuration = options.LeaseDuration;
        _leaseDurationTicks = CeilingTimestampTicks(_leaseDuration, timeProvider.TimestampFrequency);
        _bootTimestamp = timeProvider.GetTimestamp();
        _writtenWithLease = ReplicaWriteOutcome.Written.FromGrant(_leaseDuration);
        _pageSize = options.PageSize;
        _fileSize = options.FileSize;
    }

    internal LedgerStoreOptions Options { get; }

    internal LedgerStoreCheckpointOptions CheckpointOptions { get; }

    internal TimeSpan LeaseDuration => _leaseDuration;

    internal ReplicaWriteOutcome.Written WrittenWithLease => _writtenWithLease;

    internal WalLog Wal { get; }

    /// <summary>
    /// Computes the RAM-only monotonic lease expiry timestamp (relative to <see cref="_bootTimestamp"/>) for an
    /// unsealed non-recovery write or heartbeat (LR-I10, ledgerstore.md §8.3).
    /// </summary>
    internal long ExtendLeaseTicks()
    {
        if (_leaseDurationTicks == 0)
        {
            return 0;
        }

        var elapsed = Math.Max(0L, _timeProvider.GetTimestamp() - _bootTimestamp);
        var sum = (UInt128)elapsed + (UInt128)_leaseDurationTicks;
        return sum > (UInt128)long.MaxValue ? long.MaxValue : (long)sum;
    }

    /// <summary>
    /// Computes the remaining duration of a replica's granted lease or the store's startup <c>bootLeaseHorizon</c>
    /// (<c>max(grantedLeaseUntil, bootLeaseHorizon) − now</c>), rounded up to preserve safety (LR-I10, ledgerstore.md §8.3).
    /// </summary>
    internal TimeSpan ComputeLeaseRemaining(long grantedLeaseUntilTicks)
    {
        if (_leaseDurationTicks == 0)
        {
            return TimeSpan.Zero;
        }

        var horizon = Math.Max(grantedLeaseUntilTicks, _leaseDurationTicks);
        var elapsed = Math.Max(0L, _timeProvider.GetTimestamp() - _bootTimestamp);
        var remainingTicks = horizon - elapsed;
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : CeilingTimestampTicksToTimeSpan(remainingTicks, _timeProvider.TimestampFrequency);
    }

    private static long CeilingTimestampTicks(TimeSpan duration, long timestampFrequency)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        if (timestampFrequency == TimeSpan.TicksPerSecond)
        {
            return duration.Ticks;
        }

        var numerator = (UInt128)duration.Ticks * (UInt128)timestampFrequency + (UInt128)(TimeSpan.TicksPerSecond - 1);
        var result = numerator / (UInt128)TimeSpan.TicksPerSecond;
        return result > (UInt128)long.MaxValue ? long.MaxValue : (long)result;
    }

    private static TimeSpan CeilingTimestampTicksToTimeSpan(long timestampTicks, long timestampFrequency)
    {
        if (timestampTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        if (timestampFrequency == TimeSpan.TicksPerSecond)
        {
            return TimeSpan.FromTicks(timestampTicks);
        }

        var numerator = (UInt128)timestampTicks * (UInt128)TimeSpan.TicksPerSecond + (UInt128)(timestampFrequency - 1);
        var result = numerator / (UInt128)timestampFrequency;
        return TimeSpan.FromTicks(result > (UInt128)long.MaxValue ? long.MaxValue : (long)result);
    }

    /// <summary>
    /// Gets the maintenance task once recovery has completed.
    /// </summary>
    internal Task? MaintenanceCompletion { get; private set; }

    /// <summary>
    /// Gets or sets the defect a test injects into a create, seal, or drop record before the WAL takes it, or
    /// <see langword="null"/> for none; production leaves it unset.
    /// </summary>
    internal Func<StoreRecord, Exception?>? StoreRecordFault { get; set; }

    /// <summary>
    /// Gets or sets the defect a test injects into the publication of a durable create, seal, or drop record, or
    /// <see langword="null"/> for none; production leaves it unset.
    /// </summary>
    internal Func<StoreRecord, Exception?>? StoreRecordPublicationFault { get; set; }

    /// <summary>
    /// Gets or sets the defect a test injects into the publication of a durable slot record, by ledger and slot, or
    /// <see langword="null"/> for none; production leaves it unset.
    /// </summary>
    internal Func<LedgerId, long, Exception?>? SlotPublicationFault { get; set; }

    /// <summary>
    /// Gets the reported failure which stopped the store, or <see langword="null"/> while it serves (conventions.md §14).
    /// </summary>
    internal Exception? Stopped => Volatile.Read(ref _stopped)?.SourceException;

    /// <summary>
    /// Gets the node identity, a random UUID the node file holds, created with the store, so a replaced or wiped disk
    /// starts a new node (LS-I6, ledgerstore.md §2.3).
    /// </summary>
    public NodeId NodeId { get; }

    /// <summary>
    /// Gets the cluster the node root belongs to, which its cluster claim records (LS-I12).
    /// </summary>
    public Guid Cluster { get; }

    internal ReplicaCatalog Catalog
    {
        get
        {
            lock (_sync)
            {
                return _catalog;
            }
        }
    }

    internal static string WalPath(string root) => Path.Combine(root, WalDirectoryName);

    internal static string SlotsPath(string root) => Path.Combine(root, SlotsDirectoryName);

    internal static string SlotsPath(string root, uint handle) =>
        Path.Combine(root, SlotsDirectoryName, handle.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Opens a handle over the replica of <paramref name="ledger"/>, without I/O and without creating it.
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The replica's runtime options, with the ledger's window.</param>
    /// <returns>
    /// A replica the caller disposes; the store stays open. A replica the catalog lists serves every operation. One it does
    /// not list answers as LR-I7 requires: when the catalog holds the ledger's tombstone, every operation fails with
    /// <c>Dropped</c>; otherwise its first write, recovery write, or seal creates it with the
    /// replica's window, and a read or contiguous-tail read answers as an empty, unsealed replica (ledgerstore.md §2.2).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default, which names no ledger.</exception>
    public LedgerStoreReplica OpenReplica(LedgerId ledger, LedgerReplicaOptions options)
    {
        ThrowIfDefault(ledger);
        ArgumentNullException.ThrowIfNull(options);
        LedgerReplicaOperations.ValidateWindowSize(options.WindowSize);
        ThrowIfUnavailable();
        return new LedgerStoreReplica(this, ledger, options);
    }

    /// <summary>
    /// Writes a batch directly to the replica of <paramref name="ledger"/> without allocating a
    /// <see cref="LedgerStoreReplica"/> handle, with the same contract as
    /// <see cref="LedgerStoreReplica.WriteAsync"/>.
    /// </summary>
    public ValueTask<ReplicaWriteOutcome> WriteReplicaAsync(
        LedgerId ledger,
        int windowSize,
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDefault(ledger);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        var found = FindForRead(ledger);
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        cancellationToken.ThrowIfCancellationRequested();
        return found switch
        {
            LiveReplica live => live.WriteAsync(slot, payload, contiguousTail, recovery, windowSize, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(dropped),
            ReplicaLookup.Absent => WriteReplicaAfterCreateAsync(ledger, windowSize, slot, payload, contiguousTail, recovery, cancellationToken),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <summary>
    /// Reads a whole batch directly from the replica of <paramref name="ledger"/> without allocating a
    /// <see cref="LedgerStoreReplica"/> handle, with the same contract as
    /// <see cref="LedgerStoreReplica.ReadAsync(Slot, Slot, CancellationToken)"/>.
    /// </summary>
    public ValueTask<ReplicaRead> ReadReplicaAsync(
        LedgerId ledger,
        int windowSize,
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDefault(ledger);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        var found = FindForRead(ledger);
        LedgerReplicaOperations.ValidateSlot(slot);
        cancellationToken.ThrowIfCancellationRequested();
        return found switch
        {
            LiveReplica live => live.ReadAsync(slot, contiguousTail, windowSize, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaRead(ReplicaRead.Missing.Instance)),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <summary>
    /// Reads up to <paramref name="maxSlots"/> consecutive slots starting at <paramref name="from"/> below
    /// <paramref name="below"/> directly from the replica of <paramref name="ledger"/> without allocating a
    /// <see cref="LedgerStoreReplica"/> handle.
    /// </summary>
    public ValueTask<ReplicaRangeRead> ReadReplicaRangeAsync(
        LedgerId ledger,
        int windowSize,
        Slot from,
        Slot below,
        Slot contiguousTail,
        int maxSlots,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDefault(ledger);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        var found = FindForRead(ledger);
        LedgerReplicaOperations.ValidateReadRange(from, below, maxSlots, maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return found switch
        {
            LiveReplica live => live.ReadRangeAsync(from, below, contiguousTail, windowSize, maxSlots, maxBytes, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaRangeRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaRangeRead(ReplicaRangeBatch.Empty(from))),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <summary>
    /// Reads a byte range of a batch directly from the replica of <paramref name="ledger"/> without allocating a
    /// <see cref="LedgerStoreReplica"/> handle, with the same contract as
    /// <see cref="LedgerStoreReplica.ReadAsync(Slot, int, int, Slot, CancellationToken)"/>.
    /// </summary>
    public ValueTask<ReplicaRead> ReadReplicaAsync(
        LedgerId ledger,
        int windowSize,
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDefault(ledger);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        var found = FindForRead(ledger);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        LedgerReplicaOperations.ValidateSlot(slot);
        cancellationToken.ThrowIfCancellationRequested();
        return found switch
        {
            LiveReplica live => ReadReplicaRangeAsync(live, slot, offset, length, contiguousTail, windowSize, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaRead(dropped)),
            ReplicaLookup.Absent => new(new ReplicaRead(ReplicaRead.Missing.Instance)),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <summary>
    /// Reads the contiguous tail of, or seals, the replica of <paramref name="ledger"/> without allocating a
    /// <see cref="LedgerStoreReplica"/> handle, with the same contract as
    /// <see cref="LedgerStoreReplica.ReadTailAsync"/>.
    /// </summary>
    public ValueTask<ReplicaTailRead> ReadReplicaTailAsync(
        LedgerId ledger,
        int windowSize,
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDefault(ledger);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        var found = FindForRead(ledger);
        cancellationToken.ThrowIfCancellationRequested();
        return found switch
        {
            LiveReplica live => live.ReadTailAsync(contiguousTail, seal, windowSize, cancellationToken),
            ReplicaWriteOutcome.Dropped dropped => new(new ReplicaTailRead(dropped)),
            ReplicaLookup.Absent when seal => SealReplicaAfterCreateAsync(ledger, windowSize, contiguousTail, cancellationToken),
            ReplicaLookup.Absent => new(new ReplicaTailRead(new LedgerReplicaTail(contiguousTail, IsSealed: false))),
            null => throw new UnreachableException("A lookup holds a case."),
        };
    }

    /// <summary>
    /// Renews RAM-only leases for the requested live, unsealed replicas in one batch without creating replicas or
    /// touching the WAL (ledger-replicas.md §2.2, ledgerstore.md §8.3, LR-I10).
    /// </summary>
    /// <param name="ledgers">The ledger IDs whose leases to renew.</param>
    /// <param name="cancellationToken">Cancels the wait on any replica whose seal is in flight.</param>
    /// <returns>Per-ledger statuses in request order paired with this store's configured <see cref="LedgerStoreOptions.LeaseDuration"/>.</returns>
    public async ValueTask<ReplicaHeartbeatReply> HeartbeatReplicasAsync(
        ReadOnlyMemory<LedgerId> ledgers,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        if (ledgers.IsEmpty)
        {
            return new ReplicaHeartbeatReply(_leaseDuration, ReadOnlyMemory<ReplicaHeartbeatStatus>.Empty);
        }

        var outcomes = new ReplicaHeartbeatStatus[ledgers.Length];
        for (var i = 0; i < ledgers.Length; i++)
        {
            var ledger = ledgers.Span[i];
            ThrowIfDefault(ledger);
            var found = FindForRead(ledger);
            outcomes[i] = found switch
            {
                LiveReplica live => await live.HeartbeatAsync(cancellationToken).ConfigureAwait(false),
                ReplicaWriteOutcome.Dropped => ReplicaHeartbeatStatus.Dropped,
                ReplicaLookup.Absent => ReplicaHeartbeatStatus.Absent,
                null => throw new UnreachableException("A lookup holds a case."),
            };
        }

        return new ReplicaHeartbeatReply(_leaseDuration, outcomes);
    }

    private async ValueTask<ReplicaWriteOutcome> WriteReplicaAfterCreateAsync(
        LedgerId ledger,
        int windowSize,
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken) =>
        await CreateLiveAsync(ledger, windowSize, cancellationToken).ConfigureAwait(false) is { } live
            ? await live.WriteAsync(slot, payload, contiguousTail, recovery, windowSize, cancellationToken).ConfigureAwait(false)
            : ReplicaWriteOutcome.Dropped.Instance;

    private static async ValueTask<ReplicaRead> ReadReplicaRangeAsync(
        LiveReplica live,
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        int windowSize,
        CancellationToken cancellationToken)
    {
        var read = await live.ReadAsync(slot, contiguousTail, windowSize, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out ArcBuffer batch))
        {
            return read;
        }

        using (batch)
        {
            return LedgerReplicaOperations.RangeOf(batch, offset, length);
        }
    }

    private async ValueTask<ReplicaTailRead> SealReplicaAfterCreateAsync(
        LedgerId ledger,
        int windowSize,
        Slot contiguousTail,
        CancellationToken cancellationToken) =>
        await CreateLiveAsync(ledger, windowSize, cancellationToken).ConfigureAwait(false) is { } live
            ? await live.ReadTailAsync(contiguousTail, seal: true, windowSize, cancellationToken).ConfigureAwait(false)
            : new ReplicaTailRead(ReplicaWriteOutcome.Dropped.Instance);

    /// <summary>
    /// Drops <paramref name="ledger"/> with one drop record, which removes its replica, if live, and leaves its
    /// tombstone (ledgerstore.md §8.6).
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait for the catalog gate, which writes nothing; once the drop holds the gate, it runs to its record's
    /// outcome and only the caller's wait ends.
    /// </param>
    /// <returns>
    /// A task which completes once the drop record is durable and the replica's files are closed. Idempotent: a ledger
    /// already dropped writes nothing. A ledger the node does not hold gets a tombstone too.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default, which names no ledger.</exception>
    public async ValueTask DropReplicaAsync(LedgerId ledger, CancellationToken cancellationToken = default)
    {
        ThrowIfDefault(ledger);
        ThrowIfUnavailable();
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var drop = DropHoldingGateAsync(ledger);
        lock (_sync)
        {
            _ = _drops.Add(drop);
        }

        _ = drop.ContinueWith(
            static (task, state) =>
            {
                var store = (LedgerStore)state!;
                lock (store._sync)
                {
                    _ = store._drops.Remove(task);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await drop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the ledger ID of every live replica, ascending; dropped replicas are absent (LS-I2).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ledger IDs.</returns>
    /// <exception cref="ObjectDisposedException">The store is disposed.</exception>
    public ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Replicas);
    }

    /// <summary>
    /// Gets the ledger ID of every live replica, ascending, as <see cref="ListReplicasAsync"/> returns them.
    /// </summary>
    internal IReadOnlyList<LedgerId> Replicas
    {
        get
        {
            LedgerId[] replicas;
            lock (_sync)
            {
                ThrowIfUnavailableLocked();
                replicas = [.. _byLedger.Keys];
            }

            // Sorted outside the lock, which every replica's writes take.
            Array.Sort(replicas);
            return replicas;
        }
    }

    /// <summary>
    /// Releases the store as <see cref="CloseAsync"/> does, writing every replica's final checkpoint.
    /// </summary>
    public async ValueTask DisposeAsync() => _ = await CloseAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Releases the store (ledgerstore.md §8.10): stops maintenance, waits for the creations, drops, and leaf loads in
    /// progress, drains and releases the WAL, writes the replicas' final checkpoints, at most
    /// <see cref="LedgerStoreCheckpointOptions.FinalCheckpointConcurrency"/> at once, until <paramref name="finalCheckpoints"/> is cancelled, and a final
    /// catalog checkpoint, so recovery replays an empty suffix for each replica whose final checkpoint it wrote, and
    /// releases every file. Once <paramref name="finalCheckpoints"/> is cancelled it starts no more final checkpoint,
    /// and lets each one already started finish: a replica whose final it skipped keeps its checkpoint in force and the
    /// WAL above its replay start, which the drained WAL holds and the next open replays, as after a crash once the WAL
    /// drained. It releases everything whatever fails, then surfaces a failure of maintenance or of a final checkpoint; a
    /// failed local storage operation among them was reported where it failed, which stops the process (conventions.md
    /// §14). A stopped store writes no final checkpoint, since its memory may not follow its WAL, and surfaces the
    /// failure which stopped it.
    /// </summary>
    /// <param name="finalCheckpoints">The caller's bound on the final checkpoints: once cancelled, no more start.</param>
    /// <returns>The final checkpoints the close wrote and skipped; none of either once the store was disposed already.</returns>
    public async ValueTask<LedgerStoreCloseResult> CloseAsync(CancellationToken finalCheckpoints)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            LiveReplica[] ledgers;
            try
            {
                if (MaintenanceCompletion is { } maintenance)
                {
                    await maintenance.ConfigureAwait(false);
                }
            }
            finally
            {
                // A creation or drop in progress finishes first; a later one observes the disposal. A drop then closes its
                // replica's files outside the gate.
                await _catalogGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                _ = _catalogGate.Release();
                Task[] drops;
                lock (_sync)
                {
                    drops = [.. _drops];
                }

                await Task.WhenAll(drops).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                ledgers = LiveReplicas();
                foreach (var ledger in ledgers)
                {
                    await ledger.StopLoadsAsync().ConfigureAwait(false);
                }

                await Wal.DisposeAsync().ConfigureAwait(false);
            }

            // The WAL has drained, so final checkpoints leave recovery an empty suffix. A store which never finished
            // recovering writes none: its tables may lack records below the frontier. Nor does a stopped store, whose
            // memory may not follow its WAL: it surfaces the failure which stopped it (conventions.md §14).
            ThrowIfStopped();
            if (MaintenanceCompletion is not null)
            {
                var written = 0;
                var skipped = 0;
                await Parallel.ForEachAsync(
                    ledgers,
                    new ParallelOptions { MaxDegreeOfParallelism = CheckpointOptions.FinalCheckpointConcurrency },
                    async (ledger, cancellationToken) =>
                    {
                        // Each replica's final waits for its own checkpoint gate, so it never overlaps the replica's
                        // other checkpoints; one the bound skips closes the gate alone.
                        var final = !finalCheckpoints.IsCancellationRequested;
                        if (await ledger.CloseCheckpointsAsync(final).ConfigureAwait(false))
                        {
                            _ = Interlocked.Increment(ref written);
                        }
                        else if (!final && !ledger.CheckpointsExhausted)
                        {
                            _ = Interlocked.Increment(ref skipped);
                        }
                    }).ConfigureAwait(false);

                // The catalog checkpoint records the catalog alone at the drained frontier; recovery replays from the
                // lowest of its cut and every listed replica's replay start (§8.7 step 6), so a skipped final leaves that
                // replica's records to the replay.
                _ = await WriteCatalogCheckpointAsync(final: true, CancellationToken.None).ConfigureAwait(false);
                return new LedgerStoreCloseResult(written, skipped);
            }

            return default;
        }
        finally
        {
            foreach (var ledger in LiveReplicas())
            {
                await ledger.RetireAsync().ConfigureAwait(false);
            }

            await _directory.DisposeAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }
    }

    internal void RecordPublished(WalPage page)
    {
        lock (_sync)
        {
            if (page > _lastPage)
            {
                _lastPage = page;
            }
        }
    }

    /// <summary>
    /// Returns the WAL bytes from <paramref name="start"/> through the end of the latest published page.
    /// </summary>
    internal long DistanceFrom(WalPosition start)
    {
        WalPage last;
        lock (_sync)
        {
            last = _lastPage;
        }

        // Data page i of a file ends P * (2 + i) bytes into it, after the header page.
        var end = (Int128)last.File * _fileSize + (Int128)_pageSize * (2 + (long)last.Index);
        var from = (Int128)start.File * _fileSize + start.Offset;
        return (long)Int128.Clamp(end - from, 0, long.MaxValue);
    }

    internal void Wake()
    {
        lock (_sync)
        {
            _ = _wake.TrySetResult();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the store is disposed.
    /// </summary>
    internal bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// Throws the failure which stopped the store, or <see cref="ObjectDisposedException"/> once it is disposed.
    /// </summary>
    internal void ThrowIfUnavailable()
    {
        lock (_sync)
        {
            ThrowIfUnavailableLocked();
        }
    }

    /// <summary>
    /// Throws the failure which stopped the store, if any: a stopped store answers nothing and writes nothing more
    /// (conventions.md §14).
    /// </summary>
    internal void ThrowIfStopped() => Volatile.Read(ref _stopped)?.Throw();

    /// <summary>
    /// Stops the store at <paramref name="exception"/>: reports it as the defect <paramref name="defect"/>, unless a storage
    /// seam or the WAL already reported it, then records it, the first failure only. In production the report ends the
    /// process and never returns. After a report a test or simulation intercepts, the store answers nothing: every
    /// operation throws the recorded failure, and the store writes no checkpoint, since its memory may not follow its WAL
    /// (conventions.md §14).
    /// </summary>
    /// <remarks>
    /// The report precedes the record, so the failure every operation throws was reported. A caller stops the store before
    /// it completes an operation which waits on the failure, and before it releases a slot claim or an admission another
    /// operation could take.
    /// </remarks>
    internal void Stop(string defect, Exception exception)
    {
        FatalErrorHandler.StopOnDefect(defect, exception);
        RecordStop(exception);
    }

    /// <summary>
    /// Stops the store with the failure which stopped its WAL, which the WAL reported before it failed the record
    /// (wal.md §6): an <c>OnFailed</c> callback's failure.
    /// </summary>
    internal void StopOnWalFailure(Exception error) => RecordStop(error);

    /// <summary>
    /// Stops the store when <paramref name="exception"/> carries a failure a storage seam or the WAL reported, which
    /// stops the process (conventions.md §14); any other failure is its operation's own answer.
    /// </summary>
    internal void StopIfReported(Exception exception)
    {
        if (FatalErrorHandler.IsReported(exception))
        {
            RecordStop(exception);
        }
    }

    /// <summary>
    /// Creates a replica's checkpoint directory one level beneath <c>slots/</c>, which flushes <c>slots/</c> alone, and
    /// opens it; with its first checkpoint (ledgerstore.md §8.8 step 3).
    /// </summary>
    internal async ValueTask<WalDirectory> CreateReplicaDirectoryAsync(uint handle, CancellationToken cancellationToken)
    {
        AssertNotBeingDeleted(handle);
        var path = SlotsPath(Options.DirectoryPath, handle);
        await _storage.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        return await _storage.OpenDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts that WAL's callers never open a directory while it is being deleted (wal.md §3): maintenance deletes a
    /// replica's directory only once the replica retired, which waits for its running checkpoint, and no handle is reused.
    /// It reads the list without the lock, which a Release build never takes here: a stale list lacks only handles added
    /// since, and holds only handles never reused.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertNotBeingDeleted(uint handle) =>
        Debug.Assert(!_droppedDirectories.Contains(handle), "wal.md §3: a replica's directory is never opened while it is being deleted.");

    /// <summary>
    /// Returns the live replica of <paramref name="ledger"/> for a write, recovery write, or seal, creating it first, with
    /// <paramref name="windowSize"/> in its create record, when the catalog neither lists it nor holds its tombstone
    /// (ledgerstore.md §8.5, LR-I6). A live replica keeps the window it was created with.
    /// </summary>
    /// <returns>The live replica, or <see langword="null"/> when the catalog holds the ledger's tombstone, creating nothing.</returns>
    /// <exception cref="InvalidOperationException">The store has assigned every handle.</exception>
    internal async ValueTask<LiveReplica?> CreateLiveAsync(LedgerId ledger, int windowSize, CancellationToken cancellationToken)
    {
        CreationRequest request;
        var startPump = false;
        lock (_sync)
        {
            ThrowIfUnavailableLocked();
            if (_byLedger.TryGetValue(ledger, out var live))
            {
                return live;
            }

            // A tombstone is permanent, so one already published refuses without waiting for the gate (LS-I2).
            if (_catalog.IsDropped(ledger))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            request = new CreationRequest(this, ledger, windowSize);
            _waitingCreations.Add(request);
            if (!_creating)
            {
                _creating = true;
                startPump = true;
            }
        }

        if (startPump)
        {
            _ = Task.Run(CreateWaitingAsync, CancellationToken.None);
        }

        try
        {
            return await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A request cancelled while it waits writes nothing; one a batch took runs to its outcome, and only the wait
            // ends.
            request.Withdraw();
            throw;
        }
    }

    /// <summary>
    /// Returns what the store holds of <paramref name="ledger"/> for a read: its live replica, its tombstone, or nothing,
    /// when the read answers as an empty replica; it creates nothing (LS-I1, LS-I2, ledgerstore.md §8.5).
    /// </summary>
    internal ReplicaLookup FindForRead(LedgerId ledger)
    {
        lock (_sync)
        {
            ThrowIfUnavailableLocked();

            // A creation or drop publishes under this lock, so the read sees the replica live, dropped, or absent before it.
            if (_byLedger.TryGetValue(ledger, out var live))
            {
                return live;
            }

            return _catalog.IsDropped(ledger) ? ReplicaWriteOutcome.Dropped.Instance : ReplicaLookup.Absent.Instance;
        }
    }

    /// <summary>
    /// Returns the live replica of <paramref name="ledger"/> the catalog lists, or <see langword="null"/>; it creates
    /// nothing.
    /// </summary>
    internal LiveReplica? FindLive(LedgerId ledger)
    {
        lock (_sync)
        {
            return _byLedger.GetValueOrDefault(ledger);
        }
    }

    private static void ThrowIfDefault(LedgerId ledger)
    {
        // A record names only constructed ledger IDs (ledgerstore.md §7.3).
        if (ledger == default)
        {
            throw new ArgumentException("The default LedgerId names no ledger.", nameof(ledger));
        }
    }

    /// <summary>
    /// Writes the waiting creations, a batch per hold of the catalog gate, until none waits (ledgerstore.md §8.5). On an
    /// unexpected failure it fails every waiting creation, so none is left waiting, and the next creation starts a new
    /// writer.
    /// </summary>
    private async Task CreateWaitingAsync()
    {
        try
        {
            while (true)
            {
                await _catalogGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    List<CreationRequest> batch;
                    lock (_sync)
                    {
                        batch = [.. _waitingCreations];
                        _waitingCreations.Clear();
                        if (batch.Count == 0)
                        {
                            _creating = false;
                            return;
                        }
                    }

                    await CreateBatchAsync(batch).ConfigureAwait(false);
                }
                finally
                {
                    _ = _catalogGate.Release();
                }
            }
        }
#pragma warning disable CA1031 // Every waiting creation receives the failure; none is left waiting.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            List<CreationRequest> waiting;
            lock (_sync)
            {
                waiting = [.. _waitingCreations];
                _waitingCreations.Clear();
                _creating = false;
            }

            foreach (var request in waiting)
            {
                _ = request.TrySetException(exception);
            }
        }
    }

    /// <summary>
    /// Decides each creation of a batch under the catalog gate, against the catalog's tombstones, and writes the others as
    /// one create record, whose durable publication lists each replica, with the record's file as its first file, before
    /// any operation reaches it (LS-I1, LS-I2). Every request of the batch completes, whatever fails: a batch taken once
    /// the store is disposed or stopped writes nothing, and each request answers the refusal.
    /// </summary>
    private async ValueTask CreateBatchAsync(List<CreationRequest> batch)
    {
        try
        {
            ThrowIfUnavailable();
            var catalog = Catalog;
            var pairs = new List<CatalogPair>();
            var creating = new Dictionary<LedgerId, List<CreationRequest>>();
            foreach (var request in batch)
            {
                if (FindLive(request.Ledger) is { } live)
                {
                    _ = request.TrySetResult(live);
                    continue;
                }

                if (creating.TryGetValue(request.Ledger, out var sharing))
                {
                    sharing.Add(request);
                    continue;
                }

                if (catalog.IsDropped(request.Ledger))
                {
                    // LS-I2: a tombstone is permanent, so the request answers dropped and nothing is created.
                    _ = request.TrySetResult(null);
                    continue;
                }

                if (catalog.NextHandle + (uint)pairs.Count >= LedgerRecords.StoreHandle)
                {
                    _ = request.TrySetException(new InvalidOperationException("The LedgerStore has assigned every replica handle."));
                    continue;
                }

                // Requests for one ledger share its first request's pair, and so its window.
                pairs.Add(new CatalogPair(request.Ledger, catalog.NextHandle + (uint)pairs.Count, (uint)request.WindowSize));
                creating.Add(request.Ledger, [request]);
            }

            if (pairs.Count == 0)
            {
                return;
            }

            var record = new CreateRecord(pairs);
            var created = await WriteStoreRecordAsync(record, durable => PublishCreate(record, durable)).ConfigureAwait(false);
            created.EnsureDurable();
            foreach (var (ledger, requests) in creating)
            {
                // The gate orders drops after this batch, so each replica it published is live.
                var live = FindLive(ledger);
                Debug.Assert(live is not null, "LS-I1: a published create lists each replica it created.");
                foreach (var request in requests)
                {
                    _ = request.TrySetResult(live);
                }
            }
        }
#pragma warning disable CA1031 // Every request of the batch receives the failure; none is left waiting.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            foreach (var request in batch)
            {
                _ = request.TrySetException(exception);
            }
        }
    }

    /// <summary>
    /// Publishes a durable create record: the catalog lists each replica under its handle, with the record's file as its
    /// first file, and only then does each replica become live (LS-I1, conventions.md §13).
    /// </summary>
    private DurableCreate PublishCreate(CreateRecord record, WalDurableRecord durable)
    {
        durable.EnsureDurable();
        lock (_sync)
        {
            var contradiction = ApplyLocked(
                record,
                durable.Position,
                replica =>
                {
#pragma warning disable CA2000 // The store owns each replica and releases it when it is dropped or the store disposed.
                    var live = new LiveReplica(this, replica, directory: null, checkpoints: null, loaded: null);
#pragma warning restore CA2000
                    live.MarkCreated();
                    return live;
                },
                out _);

            // The store wrote the record from its own catalog under the catalog gate, so the catalog takes it.
            ThrowIfContradicted(contradiction);
        }

        return DurableCreate.FromWal(durable);
    }

    /// <summary>
    /// Drops <paramref name="ledger"/> under the catalog gate, which the caller took and this releases once the drop
    /// record is published: stops admitting operations to its replica, if live, and waits for those admitted and for its
    /// leaf loads, then writes the drop record which removes it and leaves the ledger's tombstone. An operation which
    /// meets the closed admission waits for the drop's outcome (conventions.md §13): it answers dropped once the record is
    /// durable and published, and the drop's failure otherwise, which stopped the store and the process first
    /// (conventions.md §14). Outside the gate, which does no file or directory I/O, it closes the replica's checkpoints and
    /// files and hands its directory to maintenance (ledgerstore.md §6.5, §8.6).
    /// </summary>
    /// <remarks>
    /// LS-I2: "A drop first stops admitting operations to the replica and waits for those admitted, so no mutation of it
    /// completes after the drop returns".
    /// </remarks>
    private async Task DropHoldingGateAsync(LedgerId ledger)
    {
        LiveReplica? live;
        try
        {
            ThrowIfUnavailable();
            if (Catalog.IsDropped(ledger))
            {
                return;
            }

            live = FindLive(ledger);
            DurableDrop? dropped = null;
            Exception? failure = null;
            try
            {
                if (live is not null)
                {
                    await live.StopAsync().ConfigureAwait(false);
                }

                dropped = await WriteStoreRecordAsync(new DropRecord(ledger), durable => PublishDrop(ledger, durable)).ConfigureAwait(false);
                dropped.Value.EnsureDurable();
            }
            catch (Exception exception) when (live is not null)
            {
                // Conventions §13, LS-I2: the closed admission answers dropped only once the record is published, so the
                // operations which met it answer this failure instead. A drop which closed admission and ends without its
                // record leaves the replica stopped while the catalog lists it, the store's memory apart from its WAL, so
                // the store stops, reporting the failure as a defect unless a seam or the WAL reported it, before any of
                // those operations answers (ledgerstore.md §6.4). The gate keeps disposal from releasing the WAL under the
                // drop, so no failure here is disposal's.
                failure = exception;
                Stop("LedgerStoreDrop", exception);
                throw;
            }
            finally
            {
                Debug.Assert(
                    live is null || dropped is { IsValid: true } && Catalog.IsDropped(ledger) || failure is not null,
                    "Conventions §13, LS-I2: a drop which closed admission ends with its durable tombstone published or its failure.");
                if (live is not null)
                {
                    if (dropped is { } durableDrop)
                    {
                        live.CompleteDrop(durableDrop);
                    }
                    else
                    {
                        live.FailDrop(failure ?? new InvalidOperationException("The drop ended without a durable drop record or failure."));
                    }
                }
            }
        }
        finally
        {
            _ = _catalogGate.Release();
        }

        if (live is null)
        {
            return;
        }

        await live.RetireAsync().ConfigureAwait(false);

        // Maintenance deletes the replica's checkpoint directory, if it has one.
        lock (_sync)
        {
            _droppedDirectories = _droppedDirectories.Add(live.Handle);
            _ = _wake.TrySetResult();
        }
    }

    /// <summary>
    /// Publishes a durable drop record: the catalog no longer lists the ledger's replica and holds its tombstone (LS-I2,
    /// conventions.md §13).
    /// </summary>
    private DurableDrop PublishDrop(LedgerId ledger, WalDurableRecord durable)
    {
        durable.EnsureDurable();
        lock (_sync)
        {
            ThrowIfContradicted(ApplyLocked(new DropRecord(ledger), durable.Position, static _ => throw new InvalidOperationException("A drop creates no replica."), out _));
        }

        return DurableDrop.FromWal(durable);
    }

    /// <summary>
    /// Applies a create or drop record at <paramref name="position"/> to the catalog and the live replicas, as its
    /// publication and recovery's replay do: a create lists each replica, which <paramref name="live"/> makes live, and a
    /// drop unlists its ledger's replica, if listed, and records the ledger's tombstone (LS-I1, LS-I2). The record then
    /// awaits a catalog checkpoint.
    /// </summary>
    /// <param name="record">The create or drop record.</param>
    /// <param name="position">The record's position.</param>
    /// <param name="live">Makes a created replica live.</param>
    /// <param name="dropped">The live replica a drop removed, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="null"/>, or the rejection naming how the record disagrees with the catalog (ledgerstore.md §7.3),
    /// when nothing changed.
    /// </returns>
    private InvalidDataException? ApplyLocked(StoreRecord record, WalPosition position, Func<CatalogReplica, LiveReplica> live, out LiveReplica? dropped)
    {
        dropped = null;
        if (_catalog.Apply(record, position.File, out var catalog) is { } contradiction)
        {
            return contradiction;
        }

        Debug.Assert(catalog is not null, "A record the catalog takes leaves a catalog.");
        _catalog = catalog;
        switch (record)
        {
            case CreateRecord create:
                foreach (var pair in create.Pairs)
                {
                    AddLocked(live(_catalog.Find(pair.Ledger)!));
                }

                break;
            case DropRecord drop:
                if (_byLedger.Remove(drop.Ledger, out dropped))
                {
                    _ = _ledgers.Remove(dropped.Handle);
                }

                break;
            default:
                throw new ArgumentException("A store record is a create or a drop.", nameof(record));
        }

        _catalogCheckpoints.Published(position);
        _ = _wake.TrySetResult();
        return null;
    }

    /// <summary>
    /// Throws when the catalog contradicts a record the store wrote from that catalog: a defect, which leaves the store's
    /// memory apart from its WAL, so its publication fails and stops the store.
    /// </summary>
    private static void ThrowIfContradicted(InvalidDataException? contradiction)
    {
        if (contradiction is not null)
        {
            throw new InvalidOperationException("The catalog contradicts a store record written from it.", contradiction);
        }
    }

    /// <summary>
    /// Writes a create, seal, or drop record and waits for its durable publication, which <paramref name="publish"/>
    /// performs on the WAL's completion path using the post-sync <see cref="WalDurableRecord"/> proof (conventions.md
    /// §13). The record fails with the WAL's failure, whose failed storage operation, or failed publication, stopped the
    /// store and the process (conventions.md §14); the next incarnation recovers from the WAL.
    /// </summary>
    internal async ValueTask<TProof> WriteStoreRecordAsync<TProof>(StoreRecord record, Func<WalDurableRecord, TProof> publish)
    {
        // A stopped store writes nothing more.
        ThrowIfStopped();
        if (StoreRecordFault?.Invoke(record) is { } fault)
        {
            throw fault;
        }

        var request = new StoreRecordRequest<TProof>(this, record, publish);
        try
        {
            await Wal.WriteAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A stopped WAL refuses the record with the failure it reported.
            StopIfReported(exception);
            request.Abandon();
            throw;
        }

        return await request.Completion.ConfigureAwait(false);
    }

    private void ThrowIfUnavailableLocked()
    {
        _stopped?.Throw();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Records the reported failure which stopped the store, the first only, and wakes maintenance, which ends at it.
    /// </summary>
    private void RecordStop(Exception exception)
    {
        lock (_sync)
        {
            _stopped ??= ExceptionDispatchInfo.Capture(exception);
            Debug.Assert(FatalErrorHandler.IsReported(_stopped.SourceException), "A stopped LedgerStore holds a reported failure (conventions.md §14).");
            _ = _wake.TrySetResult();
        }
    }

    private void AddLocked(LiveReplica ledger)
    {
        _ledgers.Add(ledger.Handle, ledger);
        _byLedger.Add(ledger.Ledger, ledger);
    }

    private LiveReplica[] LiveReplicas()
    {
        lock (_sync)
        {
            return [.. _ledgers.Values];
        }
    }

    /// <summary>
    /// A creation waiting at the catalog gate for its batch, with the window its create record records.
    /// </summary>
    private sealed class CreationRequest(LedgerStore store, LedgerId ledger, int windowSize)
        : TaskCompletionSource<LiveReplica?>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public LedgerId Ledger { get; } = ledger;

        public int WindowSize { get; } = windowSize;

        /// <summary>
        /// Withdraws the request while it waits, so no batch writes it; once a batch has taken it, it runs to its outcome.
        /// Only the cancelled caller awaited it, so it is left incomplete.
        /// </summary>
        public void Withdraw()
        {
            lock (store._sync)
            {
                _ = store._waitingCreations.Remove(this);
            }
        }
    }

    /// <summary>
    /// Zero-allocation proof that a create record is durable in the WAL and published to the catalog (LS-I1,
    /// conventions.md §13).
    /// </summary>
    internal readonly struct DurableCreate
    {
        private readonly bool _durable;

        private DurableCreate(bool durable) => _durable = durable;

        internal bool IsValid => _durable;

        internal static DurableCreate FromWal(WalDurableRecord record)
        {
            record.EnsureDurable();
            return new DurableCreate(durable: true);
        }

        internal void EnsureDurable()
        {
            if (!_durable)
            {
                throw new InvalidOperationException("A default DurableCreate does not witness a durable create record (conventions.md §13, LS-I1).");
            }
        }
    }

    /// <summary>
    /// Zero-allocation proof that a drop record is durable in the WAL and published as the ledger's tombstone (LS-I2,
    /// conventions.md §13).
    /// </summary>
    internal readonly struct DurableDrop
    {
        private readonly bool _durable;

        private DurableDrop(bool durable) => _durable = durable;

        internal bool IsValid => _durable;

        internal static DurableDrop FromWal(WalDurableRecord record)
        {
            record.EnsureDurable();
            return new DurableDrop(durable: true);
        }

        internal void EnsureDurable()
        {
            if (!_durable)
            {
                throw new InvalidOperationException("A default DurableDrop does not witness a durable drop record (conventions.md §13, LS-I2).");
            }
        }
    }

    /// <summary>
    /// One create, seal, or drop record, which publishes its outcome once durable and produces its proof token.
    /// </summary>
    private sealed class StoreRecordRequest<TProof> : WalWriteRequest
    {
        private readonly LedgerStore _store;
        private readonly StoreRecord _record;
        private readonly Func<WalDurableRecord, TProof> _publish;
        private readonly TaskCompletionSource<TProof> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StoreRecordRequest(LedgerStore store, StoreRecord record, Func<WalDurableRecord, TProof> publish)
        {
            _store = store;
            _record = record;
            _publish = publish;
            Header = LedgerRecords.RecordKey(record.Key);
            using var writer = new ArcBufferWriter(record.BodyLength);
            record.Encode(writer);
            Payload = writer.ConsumeSlice(writer.Length);
        }

        public Task<TProof> Completion => _completion.Task;

        public void Abandon()
        {
            Header.Dispose();
            Payload.Dispose();
        }

        /// <summary>
        /// Publishes the durable record, unless the store has stopped, which publishes and answers nothing more; the next
        /// incarnation recovers the record from the WAL (conventions.md §14).
        /// </summary>
        protected internal override void OnDurable(WalDurableRecord record)
        {
            try
            {
                record.EnsureDurable();
                if (_store.Stopped is { } stopped)
                {
                    _ = _completion.TrySetException(stopped);
                    return;
                }

                if (_store.StoreRecordPublicationFault?.Invoke(_record) is { } fault)
                {
                    throw fault;
                }

                var proof = _publish(record);
                _store.RecordPublished(record.Page);
                _ = _completion.TrySetResult(proof);
            }
            catch (Exception exception)
            {
                // A publication which throws leaves the catalog, or a seal, behind the WAL. The store stops, reporting it,
                // before the record's caller or any operation waiting on the catalog gate or the seal sees the failure;
                // the WAL then finds it reported (conventions.md §14).
                _store.Stop("LedgerStoreRecordPublication", exception);
                _ = _completion.TrySetException(exception);
                throw;
            }
            finally
            {
                Abandon();
            }
        }

        /// <summary>
        /// Fails the record with the failure which stopped the WAL, which the WAL reported (conventions.md §14): the store
        /// stops with it before the record's caller sees it.
        /// </summary>
        protected internal override void OnFailed(Exception error)
        {
            _store.StopOnWalFailure(error);
            Abandon();
            _ = _completion.TrySetException(error);
        }
    }
}
