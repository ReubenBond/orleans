using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <content>
/// Catalog checkpoints, retention, and maintenance (ledgerstore.md §8.4, §8.8, §8.10).
/// </content>
public sealed partial class LedgerStore
{
    // The handles of dropped replicas whose checkpoint directories maintenance deletes.
    // Replaced under _sync, and read without it by AssertNotBeingDeleted, so a Debug build takes no lock a Release build
    // lacks and a simulated run schedules alike in both.
    private ImmutableArray<uint> _droppedDirectories = [];

    // Whether a catalog checkpoint is due at once: a caller asked for one.
    private bool _catalogCheckpointRequested;

    // The wake signal maintenance waits on while nothing is due, or null while it works.
    private Task? _idle;
    private long _lastCatalogCheckpointTimestamp;
    private long _catalogCheckpointsWritten;

    internal long CatalogCheckpointsWritten => Interlocked.Read(ref _catalogCheckpointsWritten);

    /// <summary>
    /// Gets the cut of the newest valid catalog checkpoint, or <see langword="null"/> before the first.
    /// </summary>
    internal WalPosition? CatalogCut => _catalogCheckpoints.Cut;

    /// <summary>
    /// Gets a value indicating whether maintenance waits with nothing due: a wake, or a timer it waits on, ends the wait.
    /// </summary>
    internal bool MaintenanceIdle
    {
        get
        {
            lock (_sync)
            {
                return _idle is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a valid catalog checkpoint reflects every create and drop record the store holds,
    /// and none is requested.
    /// </summary>
    internal bool CatalogCheckpointCurrent
    {
        get
        {
            lock (_sync)
            {
                return !_catalogCheckpointRequested && _catalogCheckpoints.Cut is not null && !_catalogCheckpoints.Pending;
            }
        }
    }

    /// <summary>
    /// Gets the cuts of the catalog checkpoints the store holds valid, ascending.
    /// </summary>
    internal IReadOnlyList<WalPosition> ValidCatalogCheckpoints => _catalogCheckpoints.Valid;

    /// <summary>
    /// Asks maintenance for a catalog checkpoint at once, outside its cadence (§8.8).
    /// </summary>
    internal void RequestCatalogCheckpoint()
    {
        lock (_sync)
        {
            _catalogCheckpointRequested = true;
            _ = _wake.TrySetResult();
        }
    }

    /// <summary>
    /// Returns what holds the WAL: the retention bound, below which every file is released (LS-I4), and the live replica
    /// with the lowest first file.
    /// </summary>
    public LedgerStoreRetention GetRetention()
    {
        lock (_sync)
        {
            ThrowIfStopped();
            var replicas = _catalog.Replicas;
            var oldest = replicas.Count == 0 ? null : replicas.MinBy(static replica => replica.FirstFile);
            return new LedgerStoreRetention(Wal.CurrentFile, _catalogCheckpoints.RetentionBound(Wal.DurableFrontier.File, _catalog), oldest?.Ledger, replicas.Count);
        }
    }

    /// <summary>
    /// Measures the disk the store uses (ledgerstore.md §7.1): the bytes of its files, its cluster claim's and those
    /// beneath <c>wal/</c> and <c>slots/</c>, and the space of the volume holding each of the two directories, queried for
    /// each, since each may be its own mount point. Below a directory, an entry a concurrent change removes, a replica's
    /// directory being deleted among them, or which the process cannot read is left out; a failed listing of a directory
    /// or query of its volume stops the process (conventions.md §14).
    /// </summary>
    public async ValueTask<LedgerStoreDiskUsage> MeasureDiskAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        var root = Options.DirectoryPath;
        var wal = await _storage.MeasureDirectoryAsync(WalPath(root), cancellationToken).ConfigureAwait(false);
        var slots = await _storage.MeasureDirectoryAsync(SlotsPath(root), cancellationToken).ConfigureAwait(false);
        return new LedgerStoreDiskUsage(
            LedgerStoreClusterClaims.Length + wal.UsedBytes + slots.UsedBytes,
            Math.Min(wal.FreeBytes, slots.FreeBytes),
            wal.TotalBytes,
            Math.Min(wal.TotalBytes, slots.TotalBytes));
    }

    /// <summary>
    /// Returns every live replica with its first file, oldest first: each pins the WAL from its first file (LS-I4).
    /// </summary>
    public IReadOnlyList<LedgerReplicaRetention> ListRetention()
    {
        ThrowIfStopped();
        return
        [
            .. Catalog.Replicas
                .OrderBy(static replica => replica.FirstFile)
                .Select(static replica => new LedgerReplicaRetention(replica.Ledger, replica.FirstFile)),
        ];
    }

    /// <summary>
    /// Returns every live replica in which the store found damage, in ledger order (LS-I5, LR-I2): each whose recovery met
    /// a damaged WAL range which faults its slots, each whose loaded slot table holds a fault a read found, and each which
    /// answered <see cref="LedgerReplicaCorruptException"/> to an operation since the store opened. Only damage which no
    /// checkpoint records, or which lies in a leaf not yet loaded, is known in memory alone: after a restart, the replica's
    /// next operation which meets it lists the replica again. A failed storage operation lists nothing: it stops the store.
    /// </summary>
    public ImmutableArray<LedgerId> ListCorruptReplicas()
    {
        ThrowIfStopped();
        return
        [
            .. LiveReplicas()
                .Where(static replica => replica.IsCorrupt)
                .Select(static replica => replica.Ledger)
                .OrderBy(static ledger => ledger.Value),
        ];
    }

    /// <summary>
    /// Captures the catalog and its cut under the lock which orders publications, and writes the catalog checkpoint unless
    /// one is in force at the cut (ledgerstore.md §8.8). It deletes no catalog checkpoint and releases no WAL. A failed
    /// write stops the process (conventions.md §14) and leaves the previous catalog checkpoints in force. A stopped store
    /// writes none: a durable record whose publication failed stopped it before the frontier passed the record, so no cut
    /// passes a record the catalog lacks.
    /// </summary>
    /// <param name="final">Whether disposal drained the WAL, so its durable frontier lies past every record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it wrote one rather than finding one in force at the cut.</returns>
    private async ValueTask<bool> WriteCatalogCheckpointAsync(bool final, CancellationToken cancellationToken)
    {
        WalPosition cut;
        ReplicaCatalog catalog;
        lock (_sync)
        {
            ThrowIfStopped();
            cut = _catalogCheckpoints.Capture(Wal.DurableFrontier, final);
            catalog = _catalog;
            _catalogCheckpointRequested = false;
        }

        var written = await _catalogCheckpoints.WriteAsync(catalog, cut, cancellationToken).ConfigureAwait(false);
        if (written)
        {
            _ = Interlocked.Increment(ref _catalogCheckpointsWritten);
        }

        return written;
    }

    /// <summary>
    /// Completes an open and starts maintenance (ledgerstore.md §8.7 step 8). The open writes a catalog checkpoint at its
    /// frontier when it holds no valid one or <paramref name="needsCheckpoint"/>; it deletes no catalog checkpoint and
    /// releases no WAL, which maintenance does after its next catalog checkpoint (§8.8).
    /// </summary>
    /// <param name="needsCheckpoint">
    /// Whether the open holds fewer than two valid catalog checkpoints, found the newest invalid, or replayed a record or
    /// range at or above the cut: a checkpoint at this open's frontier then lies above every recovered range, so damage
    /// found there later faults only the replicas it reaches (LS-I5).
    /// </param>
    /// <param name="publishNodeFile">
    /// Whether the open creates the store, whose node file it publishes after the first catalog checkpoint (LS-I6).
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="IOException">
    /// The open's catalog checkpoint write failed, which stops the process (conventions.md §14): the open fails, and the
    /// next one finds the catalog checkpoints this one found (ledgerstore.md §8.7 step 4).
    /// </exception>
    private async ValueTask FinishOpenAsync(bool needsCheckpoint, bool publishNodeFile, CancellationToken cancellationToken)
    {
        _lastCatalogCheckpointTimestamp = _timeProvider.GetTimestamp();

        // Without a valid catalog checkpoint the store cannot release or recover its WAL, so it does not serve.
        if (_catalogCheckpoints.Cut is null || needsCheckpoint)
        {
            _ = await WriteCatalogCheckpointAsync(final: false, cancellationToken).ConfigureAwait(false);
        }

        if (publishNodeFile)
        {
            // LS-I6: the node file follows WAL file 1 and the empty catalog checkpoint, both durable, so a root holding it
            // holds a store, and one without it holds at most an interrupted creation.
            await NodeFile.CreateAsync(_directory, NodeId, cancellationToken).ConfigureAwait(false);
        }

        MaintenanceCompletion = Task.Run(() => MaintainAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>
    /// Releases every WAL file below the retention bound, oldest first (LS-I4).
    /// </summary>
    private async ValueTask ReleaseRetainedPrefixAsync(CancellationToken cancellationToken)
    {
        ulong bound;
        lock (_sync)
        {
            bound = _catalogCheckpoints.RetentionBound(Wal.DurableFrontier.File, _catalog);
        }

        await Wal.ReleaseAsync(bound, cancellationToken).ConfigureAwait(false);
        _catalogCheckpoints.Released(bound);
    }

    /// <summary>
    /// Deletes the checkpoint directories of dropped replicas; one whose deletion answers that its lock is held stays for
    /// the next pass, or the next open, to delete.
    /// </summary>
    private async ValueTask DeleteDroppedDirectoriesAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<uint> handles;
        lock (_sync)
        {
            handles = _droppedDirectories;
        }

        foreach (var handle in handles)
        {
            if (!await TryDeleteReplicaDirectoryAsync(handle, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            lock (_sync)
            {
                _droppedDirectories = _droppedDirectories.Remove(handle);
            }
        }
    }

    /// <summary>
    /// Deletes the checkpoint directory of a replica no catalog entry lists, answering <see langword="false"/> when the
    /// deletion answers that the directory's lock is held: no owner holds such a directory, so the answer means another
    /// process's handle on its lock file, a scanner's or an indexer's on Windows, outlasted the contention bound
    /// (wal.md §9), and a later attempt deletes it.
    /// </summary>
    private async ValueTask<bool> TryDeleteReplicaDirectoryAsync(uint handle, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteDirectoryAsync(SlotsPath(Options.DirectoryPath, handle), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (WalLockHeldException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns whether a catalog checkpoint is due (§8.8): one was requested; the WAL bytes from the cut reached the replay
    /// bound; or a published create or drop record awaits one and the interval has passed since the last. Drops within an
    /// interval share one checkpoint, and none is written while the catalog and the WAL past the cut stay small.
    /// </summary>
    private bool CatalogCheckpointDue(TimeSpan interval, out TimeSpan remaining)
    {
        var elapsed = _timeProvider.GetElapsedTime(_lastCatalogCheckpointTimestamp);
        remaining = TimeSpan.MaxValue;
        bool requested;
        lock (_sync)
        {
            requested = _catalogCheckpointRequested;
        }

        if (requested || _catalogCheckpoints.Cut is not { } cut)
        {
            return true;
        }

        // A capture reaches only what lies below the frontier, so the replay bound waits for the frontier to pass the cut.
        if (Wal.DurableFrontier > cut && DistanceFrom(cut) >= CheckpointOptions.CheckpointReplayBytes)
        {
            return true;
        }

        if (!_catalogCheckpoints.Pending)
        {
            return false;
        }

        remaining = interval - elapsed;
        return remaining <= TimeSpan.Zero;
    }

    /// <summary>
    /// Deletes dropped replicas' checkpoint directories, writes catalog checkpoints and releases the WAL behind them,
    /// freezes settled leaves, and writes each replica's checkpoint whenever its interval passes or its replay distance
    /// reaches its bound, never for a replica whose checkpoint generations are exhausted, until disposal stops it. It
    /// retries nothing: a failure stops the store and ends maintenance, which disposal surfaces (conventions.md §14).
    /// </summary>
    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        var interval = CheckpointOptions.CheckpointInterval;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // A stopped store writes and deletes nothing more.
                ThrowIfStopped();

                // Taking the signal before checking for work means a wake raised after the check ends the wait below.
                Task wake;
                lock (_sync)
                {
                    if (_wake.Task.IsCompleted)
                    {
                        _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    wake = _wake.Task;
                }

                await DeleteDroppedDirectoriesAsync(cancellationToken).ConfigureAwait(false);
                var catalogRemaining = await MaintainCatalogAsync(interval, cancellationToken).ConfigureAwait(false);
                if (catalogRemaining is null)
                {
                    continue;
                }

                var replicasRemaining = await CheckpointReplicasAsync(interval, cancellationToken).ConfigureAwait(false);
                if (replicasRemaining is null)
                {
                    continue;
                }

                await WaitAsync(wake, catalogRemaining < replicasRemaining ? catalogRemaining.Value : replicasRemaining.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal stops maintenance; maintenance after the next open does what this would have.
        }
        catch (Exception exception)
        {
            // Dead maintenance would leave checkpoints unwritten and the WAL growing without bound, so its failure stops
            // the store, reporting it as a defect unless a seam or the WAL reported it.
            Stop("LedgerStoreMaintenance", exception);
            throw;
        }
    }

    /// <summary>
    /// Writes a catalog checkpoint when one is due, then deletes the superseded ones and releases the WAL behind them.
    /// </summary>
    /// <returns><see langword="null"/> when it worked; otherwise how long until a catalog checkpoint is due.</returns>
    private async ValueTask<TimeSpan?> MaintainCatalogAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (!CatalogCheckpointDue(interval, out var remaining))
        {
            return remaining;
        }

        _lastCatalogCheckpointTimestamp = _timeProvider.GetTimestamp();
        _ = await WriteCatalogCheckpointAsync(final: false, cancellationToken).ConfigureAwait(false);
        if (await _catalogCheckpoints.DeleteSupersededAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReleaseRetainedPrefixAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Freezes each live replica's settled leaves and writes the checkpoints which are due.
    /// </summary>
    /// <returns><see langword="null"/> when it wrote one; otherwise how long until one is due.</returns>
    private async ValueTask<TimeSpan?> CheckpointReplicasAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // With a live replica, maintenance looks again at least once an interval; with none, a creation wakes it.
        var ledgers = LiveReplicas();
        var next = ledgers.Length == 0 ? TimeSpan.MaxValue : interval;
        var checkpointed = false;
        foreach (var ledger in ledgers)
        {
            ledger.LastCheckpointTimestamp ??= _timeProvider.GetTimestamp();
            ledger.FreezeSettledLeaves();
            if (ledger.CheckpointsExhausted)
            {
                // Exhaustion lasts until an operator deletes the replica's checkpoints (§8.8): no checkpoint is scheduled.
                continue;
            }

            var remaining = interval - _timeProvider.GetElapsedTime(ledger.LastCheckpointTimestamp.Value);
            if (remaining <= TimeSpan.Zero || ledger.CheckpointDue())
            {
                ledger.LastCheckpointTimestamp = _timeProvider.GetTimestamp();
                await ledger.MaintainCheckpointAsync(cancellationToken).ConfigureAwait(false);
                checkpointed = true;
                continue;
            }

            next = remaining < next ? remaining : next;
        }

        return checkpointed ? null : next;
    }

    /// <summary>
    /// Waits, idle, for <paramref name="wake"/> or for <paramref name="next"/> to pass, whichever comes first. The timer is
    /// armed before maintenance reports itself idle, so time which passes once it is idle counts toward
    /// <paramref name="next"/>.
    /// </summary>
    private async ValueTask WaitAsync(Task wake, TimeSpan next, CancellationToken cancellationToken)
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? timer = null;
        if (next != TimeSpan.MaxValue)
        {
            // Timers count whole milliseconds, so a shorter delay completes at once; waiting at least the remaining
            // time rounded up keeps maintenance from spinning until a fraction of a millisecond has passed.
            if (next.Ticks % TimeSpan.TicksPerMillisecond is var fraction and > 0)
            {
                next += TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - fraction);
            }

            timer = Task.Delay(next, _timeProvider, delay.Token);
        }

        lock (_sync)
        {
            _idle = wake;
        }

        try
        {
            if (timer is null)
            {
                // Nothing to time; a creation, a drop, or a publication which makes work due wakes maintenance.
                await wake.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                return;
            }

            _ = await Task.WhenAny(wake, timer).ConfigureAwait(false);
            await delay.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _idle = null;
            }
        }
    }
}
