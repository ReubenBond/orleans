using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal;
using WalLog = Orleans.Durable.Storage.Engine.Wal.WriteAheadLog;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <content>
/// Opening and recovery (ledgerstore.md §8.7).
/// </content>
public sealed partial class LedgerStore
{
    /// <summary>
    /// Opens the store of a node root for <paramref name="cluster"/>: verifies the root's cluster claim, reads its node
    /// file, loads the newest valid catalog checkpoint and each replica's newest usable slot-index checkpoint, and
    /// replays the WAL after them, deriving the catalog and the slot tables together, before serving any operation
    /// (ledgerstore.md §8.7). With <paramref name="create"/>, a root without a claim or a store gets new ones.
    /// </summary>
    /// <param name="storage">The storage which writes the WAL and the checkpoints.</param>
    /// <param name="options">The node root and WAL settings.</param>
    /// <param name="cluster">The non-empty cluster the root belongs to.</param>
    /// <param name="create">Whether to create the claim and the store when the root holds none.</param>
    /// <param name="checkpointOptions">When the store checkpoints its catalog and each replica its slot table.</param>
    /// <param name="timeProvider">The clock which schedules checkpoints and paces the WAL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open store, which the caller owns.</returns>
    /// <exception cref="DirectoryNotFoundException">The node root does not exist: an operator creates it (ledgerstore.md §3.3).</exception>
    /// <exception cref="LedgerStoreClusterMismatchException">Another cluster claims the root (LS-I12).</exception>
    /// <exception cref="WalNotFoundException">The root holds no store and <paramref name="create"/> is not set.</exception>
    /// <exception cref="LedgerReplicaCorruptException">
    /// The root holds a store without a cluster claim; WAL files or catalog checkpoints without a node file which are more
    /// than an interrupted creation leaves, a damaged node file, catalog checkpoints or checkpoint directories without WAL
    /// files, a checkpoint directory whose handle the WAL never assigned (LS-I6); no valid catalog checkpoint once the WAL
    /// holds a record or damage or has released its file 1, a detected node loss; a catalog checkpoint whose cut lies
    /// outside the recovered WAL, or WAL damage which may hide a create or drop record (LS-I4, LS-I5); or a derived catalog
    /// which does not follow a valid catalog checkpoint the open read (LS-I2). The open deletes nothing before it refuses.
    /// </exception>
    /// <exception cref="IOException">
    /// A local storage operation of the open failed, which stops the process (conventions.md §14): reading a catalog
    /// checkpoint, which deletes nothing, writing the open's catalog checkpoint, or another.
    /// </exception>
    public static ValueTask<LedgerStore> OpenAsync(
        WalStorage storage,
        LedgerStoreOptions options,
        Guid cluster,
        bool create,
        LedgerStoreCheckpointOptions checkpointOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return OpenAsync(
            storage,
            options,
            cluster,
            create,
            checkpointOptions,
            timeProvider,
            FileNodeRootClaim.Claim(options.DirectoryPath, flushToDisk: true),
            cancellationToken);
    }

    /// <summary>
    /// Opens the store of a node root whose cluster claim <paramref name="claim"/> reads and publishes: inspects what the
    /// root holds without changing it, decides once whether to recover the store or create what it lacks, then acts.
    /// </summary>
    internal static async ValueTask<LedgerStore> OpenAsync(
        WalStorage storage,
        LedgerStoreOptions options,
        Guid cluster,
        bool create,
        LedgerStoreCheckpointOptions checkpointOptions,
        TimeProvider timeProvider,
        INodeRootClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpointOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(claim);
        if (cluster == Guid.Empty)
        {
            throw new ArgumentException("The cluster identifier cannot be empty.", nameof(cluster));
        }

        options.Validate();

        // The store holds the values it validated, whatever its caller changes later (ledgerstore.md §8.11).
        checkpointOptions = checkpointOptions with { };
        checkpointOptions.Validate();

        // LS-I12: the claim precedes the store, so a store in an unclaimed root is damage, and only an empty root is
        // claimed. The claim's read flushes the root, so a name a crashed creator left unsynced in it, the WAL directory's
        // included, is durable before the store reads or acknowledges anything beneath it (WAL-I10, ledgerstore.md §8.7).
        var claims = new LedgerStoreClusterClaims();
        var root = options.DirectoryPath;
        var claimPath = Path.Combine(root, LedgerStoreClusterClaims.Name);
        var claimed = await claims.TryVerifyAsync(claimPath, cluster, claim.ReadAsync, cancellationToken).ConfigureAwait(false);
        var walPath = WalPath(root);
        WalDirectory? directory;
        try
        {
            directory = await storage.OpenDirectoryAsync(walPath, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException exception) when (!FatalErrorHandler.IsReported(exception))
        {
            // The WAL directory's absence, durable in the root, is an answer; a directory a seam found gone is a failure
            // which stops the process (conventions.md §14).
            directory = null;
        }

        IReadOnlyList<ulong> files = [];
        List<WalPosition> cuts = [];
        NodeId? nodeId = null;
        OpenAction action;
        try
        {
            if (directory is not null)
            {
                files = await directory.ListFilesAsync(cancellationToken).ConfigureAwait(false);
                cuts = await CatalogCheckpoints.ListAsync(directory, cancellationToken).ConfigureAwait(false);
                nodeId = await NodeFile.ReadAsync(directory, root, cancellationToken).ConfigureAwait(false);
            }

            var hasCheckpointDirectories = (await storage.ListDirectoriesAsync(SlotsPath(root), cancellationToken).ConfigureAwait(false)).Count != 0;
            action = DecideOpen(root, create, claimed, directory is not null, files.Count, cuts.Count, nodeId is not null, hasCheckpointDirectories);
        }
        catch
        {
            if (directory is not null)
            {
                await directory.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        // The store recovered or created takes the WAL directory, and disposes it if that fails.
        if (action == OpenAction.Recover)
        {
            return await RecoverStoreAsync(storage, directory!, nodeId!.Value, files, cuts, cluster, options, checkpointOptions, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        if (action == OpenAction.CompleteCreation)
        {
            return await CompleteCreationAsync(storage, directory!, files, cuts, cluster, options, checkpointOptions, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            if (!claimed)
            {
                await claims.EnsureAsync(claimPath, cluster, claim.CreateIfAbsentAsync, claim.ReadAsync, cancellationToken).ConfigureAwait(false);
            }

            if (directory is null)
            {
                // The slots directory precedes the WAL directory, so a root holding the WAL directory holds a durable slots
                // directory, beneath which every replica's checkpoint directory is created (LS-I6).
                await storage.CreateDirectoryAsync(SlotsPath(root), cancellationToken).ConfigureAwait(false);
                await storage.CreateDirectoryAsync(walPath, cancellationToken).ConfigureAwait(false);
                directory = await storage.OpenDirectoryAsync(walPath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (directory is not null)
            {
                await directory.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return await CreateStoreAsync(storage, directory, cluster, options, checkpointOptions, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What an open does with the root it inspected.
    /// </summary>
    private enum OpenAction
    {
        /// <summary>The root holds no store: the open creates one.</summary>
        Create,

        /// <summary>The root holds a creation a crash interrupted before its node file: the open completes it.</summary>
        CompleteCreation,

        /// <summary>The root holds a store: the open recovers it.</summary>
        Recover,
    }

    /// <summary>
    /// Decides, from what the root holds, whether the open recovers its store, completes an interrupted creation, or
    /// creates a store, and refuses a root whose contents contradict each other (ledgerstore.md §8.7 steps 1 to 4; LS-I12,
    /// LS-I6).
    /// </summary>
    private static OpenAction DecideOpen(
        string root, bool create, bool claimed, bool hasWalDirectory, int files, int checkpoints, bool hasNodeFile, bool hasCheckpointDirectories)
    {
        if (!hasWalDirectory)
        {
            if (!create)
            {
                throw new WalNotFoundException($"The LedgerStore at '{root}' holds no write-ahead log.");
            }

            return hasCheckpointDirectories ? throw LostWal(root, "no write-ahead log directory") : OpenAction.Create;
        }

        if (!claimed)
        {
            if (hasNodeFile || checkpoints != 0 || files != 0)
            {
                throw new LedgerReplicaCorruptException($"The LedgerStore at '{root}' holds a store but no cluster claim.");
            }

            if (!create)
            {
                throw new WalNotFoundException($"The LedgerStore at '{root}' holds no cluster claim.");
            }
        }

        if (!hasNodeFile)
        {
            // LS-I6: the store publishes its node file last, after WAL file 1 and the empty catalog checkpoint, so files
            // without it are a creation a crash interrupted only when they could be no more than that. Without create, the
            // root holds no store; with it, the open checks their contents before it completes the creation (§8.7 step 3).
            if (files == 0 && checkpoints != 0)
            {
                throw IdentityLost(root, "catalog checkpoints but no WAL files");
            }

            if (hasCheckpointDirectories)
            {
                throw files == 0 ? LostWal(root, "no node file") : IdentityLost(root, "slot-index checkpoint directories");
            }

            if (checkpoints > 1)
            {
                throw IdentityLost(root, "more than one catalog checkpoint");
            }

            if (!create)
            {
                throw new WalNotFoundException($"The LedgerStore at '{root}' holds no node file.");
            }

            return files == 0 ? OpenAction.Create : OpenAction.CompleteCreation;
        }

        // The node file follows WAL file 1, so a node file without WAL files shows a lost WAL.
        return files == 0
            ? throw new LedgerReplicaCorruptException($"The LedgerStore at '{root}' holds a node file but no WAL files: its WAL was lost.")
            : OpenAction.Recover;
    }

    private static LedgerReplicaCorruptException LostWal(string root, string missing) =>
        new($"The LedgerStore at '{root}' holds slot-index checkpoint directories but {missing}: its WAL was lost.");

    /// <summary>
    /// Reports a root without a node file which holds more than an interrupted creation leaves: a store existed, and its
    /// identity is lost (LS-I6).
    /// </summary>
    private static LedgerReplicaCorruptException IdentityLost(string root, string evidence) =>
        new($"The LedgerStore at '{root}' holds no node file but {evidence}, so a store existed and it cannot establish its node identity.");

    /// <summary>
    /// Creates the WAL of a store which holds none, writes its first catalog checkpoint, of the empty catalog, and then
    /// publishes its node file (ledgerstore.md §8.7 step 4).
    /// </summary>
    private static async ValueTask<LedgerStore> CreateStoreAsync(
        WalStorage storage,
        WalDirectory directory,
        Guid cluster,
        LedgerStoreOptions options,
        LedgerStoreCheckpointOptions checkpointOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        WalLog wal;
        try
        {
            wal = await WalLog.CreateAsync(directory, options.ToWalOptions(timeProvider), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await directory.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var store = new LedgerStore(
            storage, directory, wal, NodeId.New(), ReplicaCatalog.Empty, new CatalogCheckpoints(directory, [], wal.CurrentFile), cluster, options, checkpointOptions, timeProvider);
        try
        {
            await store.FinishOpenAsync(needsCheckpoint: true, publishNodeFile: true, cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch
        {
            await store.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }

    /// <summary>
    /// Completes a creation a crash interrupted before its node file: when the WAL retains file 1 and holds neither a
    /// record nor damage, and its only catalog checkpoint, if any, is the empty catalog's, writes the empty catalog
    /// checkpoint if it is missing and publishes the node file of a new identity; otherwise refuses, since a store existed
    /// (ledgerstore.md §8.7 step 3; LS-I6).
    /// </summary>
    private static async ValueTask<LedgerStore> CompleteCreationAsync(
        WalStorage storage,
        WalDirectory directory,
        IReadOnlyList<ulong> files,
        List<WalPosition> cuts,
        Guid cluster,
        LedgerStoreOptions options,
        LedgerStoreCheckpointOptions checkpointOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = options.DirectoryPath;
        WalLog? wal = null;
        LedgerStore? store = null;
        try
        {
            var checkpoints = await CatalogCheckpoints.ReadNewestValidAsync(directory, cuts, cancellationToken).ConfigureAwait(false);
            if (files[0] != 1 || checkpoints.Count != cuts.Count || checkpoints.Any(static checkpoint => !checkpoint.Catalog.IsEmpty))
            {
                throw IdentityLost(root, "WAL files and a catalog checkpoint which are more than an interrupted creation leaves");
            }

            wal = await WalLog.OpenAsync(directory, options.ToWalOptions(timeProvider), minimumFirstFile: 0, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfCutOutsideRecoveredWal(root, wal, cuts);
            if (await FindEntryAsync(wal, files[0], cancellationToken).ConfigureAwait(false) is { } evidence)
            {
                throw IdentityLost(root, "WAL files which " + evidence);
            }

            store = new LedgerStore(
                storage,
                directory,
                wal,
                NodeId.New(),
                ReplicaCatalog.Empty,
                new CatalogCheckpoints(directory, cuts, firstRetainedFile: 1),
                cluster,
                options,
                checkpointOptions,
                timeProvider);
            await store.FinishOpenAsync(needsCheckpoint: false, publishNodeFile: true, cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch
        {
            if (store is not null)
            {
                // The store owns the WAL and the directory.
                await store.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            else
            {
                if (wal is not null)
                {
                    await wal.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                await directory.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Recovers a store whose WAL holds files (ledgerstore.md §8.7 steps 5 to 8): checks its catalog checkpoints' cuts,
    /// loads the newest valid one and the replicas' checkpoint directories, opens the WAL, replays it, checks the derived
    /// catalog, and only then deletes the directories no live replica owns.
    /// </summary>
    private static async ValueTask<LedgerStore> RecoverStoreAsync(
        WalStorage storage,
        WalDirectory directory,
        NodeId nodeId,
        IReadOnlyList<ulong> files,
        List<WalPosition> cuts,
        Guid cluster,
        LedgerStoreOptions options,
        LedgerStoreCheckpointOptions checkpointOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = options.DirectoryPath;
        var loaded = new Dictionary<uint, LoadedDirectory>();
        var unlisted = new List<uint>();
        WalLog? wal = null;
        LedgerStore? store = null;
        try
        {
            // LS-I4, LS-I5: every catalog checkpoint's cut lies in the retained WAL; one below it shows a released prefix
            // the checkpoint needs, and one past it a lost tail the checkpoint reflects. The recovered WAL bounds it to the
            // byte once the WAL is open.
            if (cuts.Where(cut => cut.File < files[0] || cut.File > files[^1]).Select(static cut => (WalPosition?)cut).FirstOrDefault() is { } outside)
            {
                throw new LedgerReplicaCorruptException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The LedgerStore at '{root}' holds a catalog checkpoint whose cut {outside} lies outside its retained WAL files {files[0]} to {files[^1]}."));
            }

            var checkpoints = await CatalogCheckpoints.ReadNewestValidAsync(directory, cuts, cancellationToken).ConfigureAwait(false);
            if (checkpoints.Count == 0 && files[0] != 1)
            {
                throw CatalogLost(root, "has released its file 1");
            }

            var checkpoint = checkpoints.Count == 0 ? null : checkpoints[^1];
            var catalog = checkpoint?.Catalog ?? ReplicaCatalog.Empty;
            await LoadCheckpointDirectoriesAsync(storage, root, catalog, loaded, unlisted, cancellationToken).ConfigureAwait(false);
            wal = await WalLog.OpenAsync(directory, options.ToWalOptions(timeProvider), NextOpenFile(loaded), cancellationToken)
                .ConfigureAwait(false);
            ThrowIfCutOutsideRecoveredWal(root, wal, cuts);
            if (checkpoint is null)
            {
                await ThrowIfWalHoldsEntriesAsync(root, wal, files[0], cancellationToken).ConfigureAwait(false);
            }

            var catalogCheckpoints = new CatalogCheckpoints(directory, checkpoints.Select(static valid => valid.Cut), files[0]);
            store = new LedgerStore(storage, directory, wal, nodeId, catalog, catalogCheckpoints, cluster, options, checkpointOptions, timeProvider);
            lock (store._sync)
            {
                foreach (var replica in catalog.Replicas)
                {
                    store.AddLocked(Adopt(store, replica, loaded, created: null));
                }
            }

            var (pastCut, dropped) = checkpoint is null
                ? (false, new List<uint>())
                : await store.RecoverAsync(checkpoint.Cut, files[0], loaded, cancellationToken).ConfigureAwait(false);
            store.CheckRecoveredCatalog(root, files[0], checkpoints, loaded.Keys);
            await store.DeleteUnlistedDirectoriesAsync([.. unlisted, .. dropped], loaded, cancellationToken).ConfigureAwait(false);
            var needsCheckpoint = checkpoints.Count < CatalogCheckpoints.Kept || checkpoint!.Cut != cuts[^1] || pastCut;
            await store.FinishOpenAsync(needsCheckpoint, publishNodeFile: false, cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch
        {
            foreach (var replica in loaded.Values)
            {
                await replica.DisposeAsync().ConfigureAwait(false);
            }

            if (store is not null)
            {
                // The store owns the WAL and the directory.
                await store.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            else
            {
                if (wal is not null)
                {
                    await wal.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                await directory.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Returns the least first file of the new open: one above every loaded replay start's file, so no record of the open
    /// takes a position a slot-index checkpoint names, even when the WAL lost the file holding it (ledgerstore.md §8.7
    /// step 5). Every catalog checkpoint's cut lies in a retained file, or the open refuses, and a listed replica's first
    /// file lies at or below its checkpoint's cut, so the WAL's own next file already follows them.
    /// </summary>
    private static ulong NextOpenFile(Dictionary<uint, LoadedDirectory> loaded) =>
        loaded.Values.Select(static replica => replica.Checkpoint is { } slots ? checked(slots.ReplayStart.File + 1) : 0)
            .DefaultIfEmpty()
            .Max();

    /// <summary>
    /// Refuses a catalog checkpoint whose cut lies past the end recovery found in the cut's file: the WAL lost a tail the
    /// checkpoint reflects (LS-I5). Every cut a store writes lies at or below the end of a durable record.
    /// </summary>
    private static void ThrowIfCutOutsideRecoveredWal(string root, WalLog wal, IEnumerable<WalPosition> cuts)
    {
        foreach (var cut in cuts)
        {
            if (wal.ReplayedEnd(cut.File) is not { } end || cut > end)
            {
                throw new LedgerReplicaCorruptException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The LedgerStore at '{root}' holds a catalog checkpoint whose cut {cut} lies past the WAL recovery found in its file, so the WAL lost records the checkpoint reflects."));
            }
        }
    }

    /// <summary>
    /// Refuses a store without a valid catalog checkpoint whose WAL holds a record or damage: a catalog existed, and its
    /// tombstones are lost, a detected node loss (ledgerstore.md §6.2, §8.7 step 5; LS-I5). A torn range holds no
    /// acknowledged record.
    /// </summary>
    private static async ValueTask ThrowIfWalHoldsEntriesAsync(string root, WalLog wal, ulong firstRetainedFile, CancellationToken cancellationToken)
    {
        if (await FindEntryAsync(wal, firstRetainedFile, cancellationToken).ConfigureAwait(false) is { } evidence)
        {
            throw CatalogLost(root, evidence);
        }
    }

    /// <summary>
    /// Scans the WAL from <paramref name="firstRetainedFile"/> for a record or damage, either of which shows that the
    /// store wrote a record.
    /// </summary>
    /// <returns>What the scan found, or <see langword="null"/> when the WAL holds neither.</returns>
    private static async ValueTask<string?> FindEntryAsync(WalLog wal, ulong firstRetainedFile, CancellationToken cancellationToken)
    {
        await foreach (var entry in wal.ReadAllAsync(new WalPosition(firstRetainedFile, 0), cancellationToken).ConfigureAwait(false))
        {
            if (entry.TryGetValue(out WalRecord _))
            {
                return "holds a record";
            }

            if (entry.TryGetValue(out WalCorruption _))
            {
                return "holds damage";
            }
        }

        return null;
    }

    /// <summary>
    /// Reports a store which holds no valid catalog checkpoint although a catalog existed, a detected node loss: the
    /// tombstones its released drop records left are lost with the checkpoints (ledgerstore.md §6.2, LS-I5).
    /// </summary>
    private static LedgerReplicaCorruptException CatalogLost(string root, string evidence) =>
        new($"The LedgerStore at '{root}' holds no valid catalog checkpoint, but its WAL {evidence}, so a catalog existed: its tombstones are lost, and the node returns only as a new node.");

    /// <summary>
    /// Opens each checkpoint directory the catalog lists or which lies at or above its next handle, and loads its newest
    /// usable slot-index checkpoint; records every other one, which a drop the catalog reflects left, for deletion once
    /// the open's checks pass (ledgerstore.md §8.7 step 5).
    /// </summary>
    private static async ValueTask LoadCheckpointDirectoriesAsync(
        WalStorage storage, string root, ReplicaCatalog catalog, Dictionary<uint, LoadedDirectory> loaded, List<uint> unlisted, CancellationToken cancellationToken)
    {
        var listed = catalog.Replicas.Select(static replica => replica.Handle).ToHashSet();
        foreach (var name in await storage.ListDirectoriesAsync(SlotsPath(root), cancellationToken).ConfigureAwait(false))
        {
            if (!TryParseHandle(name, out var handle))
            {
                continue;
            }

            if (handle < catalog.NextHandle && !listed.Contains(handle))
            {
                unlisted.Add(handle);
                continue;
            }

            // The first checkpoint which created it made its name durable before writing beneath it, but a crashed process
            // may have left the name unsynced; repeating the creation makes it durable before the store relies on it.
            // Opening flushes the directory's own entries.
            await storage.CreateDirectoryAsync(SlotsPath(root, handle), cancellationToken).ConfigureAwait(false);
            var directory = await storage.OpenDirectoryAsync(SlotsPath(root, handle), cancellationToken).ConfigureAwait(false);
            try
            {
                var (checkpoints, checkpoint) = await SlotCheckpoints.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
                loaded.Add(handle, new LoadedDirectory(directory, checkpoints, checkpoint));
            }
            catch
            {
                await directory.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes the checkpoint directories no live replica owns, once the open's checks pass: those of dropped replicas,
    /// and those loaded which no replica adopted, whose replicas the replay created and dropped. One whose deletion
    /// answers that its lock is held is left for maintenance to delete, as a drop's directory is (§8.6).
    /// </summary>
    private async ValueTask DeleteUnlistedDirectoriesAsync(
        IEnumerable<uint> handles, Dictionary<uint, LoadedDirectory> loaded, CancellationToken cancellationToken)
    {
        foreach (var (handle, replica) in loaded)
        {
            await replica.DisposeAsync().ConfigureAwait(false);
            await DeleteOrLeaveForMaintenanceAsync(handle, cancellationToken).ConfigureAwait(false);
        }

        loaded.Clear();
        foreach (var handle in handles)
        {
            await DeleteOrLeaveForMaintenanceAsync(handle, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DeleteOrLeaveForMaintenanceAsync(uint handle, CancellationToken cancellationToken)
    {
        if (await TryDeleteReplicaDirectoryAsync(handle, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        lock (_sync)
        {
            _droppedDirectories = _droppedDirectories.Add(handle);
        }
    }

    private static bool TryParseHandle(string name, out uint handle) =>
        uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out handle)
        && string.Equals(name, handle.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>
    /// Builds the live replica of a catalog entry, adopting its loaded checkpoint directory. A replica created at
    /// <paramref name="created"/> adopts only a checkpoint whose replay start lies above its create record.
    /// </summary>
    private static LiveReplica Adopt(LedgerStore store, CatalogReplica replica, Dictionary<uint, LoadedDirectory> loaded, WalPosition? created)
    {
        if (!loaded.Remove(replica.Handle, out var directory))
        {
            return new LiveReplica(store, replica, directory: null, checkpoints: null, loaded: null);
        }

        var checkpoint = directory.Checkpoint is { } slots && (created is not { } position || slots.ReplayStart > position) ? slots : null;
        return new LiveReplica(store, replica, directory.Directory, directory.Checkpoints, checkpoint);
    }

    private static LedgerReplicaCorruptException DamagedStoreRecord(string root, WalPosition position, InvalidDataException reason) =>
        new($"The LedgerStore at '{root}' holds a damaged create, seal, or drop record at {position}, so it cannot establish its replicas.", reason);

    /// <summary>
    /// Replays the retained WAL once, from the lowest of the catalog cut and every listed replica's replay start, or from
    /// the first retained file when that lies above it, deriving the catalog and the slot tables together
    /// (ledgerstore.md §8.7 step 6).
    /// </summary>
    /// <returns>
    /// Whether the replay met a record or range at or above the catalog cut: a catalog checkpoint at this open's frontier
    /// then moves every recovered range below the cut, so later damage there faults only the replicas it reaches; and the
    /// handles of the replicas it dropped, whose directories the open deletes once its checks pass.
    /// </returns>
    /// <remarks>
    /// LS-I5: "A damaged WAL range in file <c>F</c> which ends at or below the cut of the catalog checkpoint recovery uses
    /// faults only live replicas whose first file is at or below <c>F</c> and whose replay start precedes the range's
    /// end." A <c>WalCorruption</c> which ends above the cut, and a damaged store record at or above it, may hide a create
    /// or drop, so the store refuses to open. A damaged range ends where the next record begins; with no record after it,
    /// it covers everything before this open's first file.
    /// </remarks>
    private async ValueTask<(bool PastCut, List<uint> Dropped)> RecoverAsync(
        WalPosition cut, ulong firstRetainedFile, Dictionary<uint, LoadedDirectory> loaded, CancellationToken cancellationToken)
    {
        var retained = new WalPosition(firstRetainedFile, 0);
        var lowest = LiveReplicas().Select(static ledger => ledger.ReplayStart).Append(cut).Min();
        var from = lowest > retained ? lowest : retained;
        var pastCut = false;
        List<uint> dropped = [];
        List<LiveReplica> damaged = [];
        await foreach (var entry in Wal.ReadAllAsync(from, cancellationToken).ConfigureAwait(false))
        {
            if (entry.TryGetValue(out WalRecord record))
            {
                pastCut |= record.Position >= cut;
                EndDamage(damaged, record.Page);
                switch (LedgerRecords.Classify(record.Payload, _catalog.NextHandle, out var handle, out var slot))
                {
                    case RecordClass.Store when LedgerRecords.StoreKindOf(record.Payload) == (ulong)StoreRecordKind.Seal:
                        ReplaySeal(record, cut);
                        break;
                    case RecordClass.Store when record.Position >= cut:
                        if (await ApplyRecoveredStoreRecordAsync(record, loaded).ConfigureAwait(false) is { } removed)
                        {
                            dropped.Add(removed);
                        }

                        break;
                    case RecordClass.Store:
                        // The catalog checkpoint reflects the create or drop.
                        break;
                    case RecordClass.Slot:
                        if (_ledgers.TryGetValue(handle, out var ledger) && record.Position >= ledger.ReplayStart)
                        {
                            await ledger.ReplayAsync(slot, record, cancellationToken).ConfigureAwait(false);
                        }

                        // A record of a dropped handle, or one its replica's checkpoint holds, is skipped.
                        break;
                    case RecordClass.Damaged:
                        // A valid page holding a record shorter than its key, a slot record with an empty value, or a slot
                        // record of an unassigned handle, is damage which preserved the CRC.
                        RaiseDamage(record);
                        break;
                    default:
                        throw new InvalidOperationException("A WAL record has no record class.");
                }
            }
            else if (entry.TryGetValue(out WalCorruption corruption))
            {
                if (corruption.End > cut)
                {
                    throw new LedgerReplicaCorruptException(
                        $"The LedgerStore at '{Options.DirectoryPath}' holds WAL damage, ending at {corruption.End}, which may hide a create or drop record, so it cannot establish its replicas.");
                }

                damaged.AddRange(LiveReplicas().Where(ledger => ledger.ReplayStart < corruption.End && !damaged.Contains(ledger)));
            }

            // A WalTorn range was left incomplete by a crash; its records were never acknowledged.
        }

        EndDamage(damaged, new WalPage(Wal.DurableFrontier.File, 0));
        foreach (var ledger in LiveReplicas())
        {
            await ledger.FinishRecoveryAsync(cancellationToken).ConfigureAwait(false);
        }

        return (pastCut, dropped);

        static void EndDamage(List<LiveReplica> damaged, WalPage end)
        {
            foreach (var ledger in damaged)
            {
                ledger.RaiseCorruptionEnd(end);
            }

            damaged.Clear();
        }
    }

    /// <summary>
    /// Replays a seal record (ledgerstore.md §7.3, §8.7 step 6): one of a listed replica at or above that replica's replay
    /// start seals it, one below it is reflected by the replica's checkpoint, and one of an unlisted handle below the next
    /// handle belongs to a dropped replica and is skipped. Any other is damage: at or above the catalog cut its kind cannot
    /// be trusted, so the store refuses to open; below the cut it raises <c>CorruptionEnd</c> past it (§6.2).
    /// </summary>
    private void ReplaySeal(WalRecord record, WalPosition cut)
    {
        var decoding = LedgerRecords.DecodeStore(record.Payload);
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        var rejection = decoding switch
        {
            SealRecord seal => Replay(seal),
            StoreRecord other => throw new UnreachableException($"A record keyed as a seal decoded as {other.Kind}."),
            InvalidDataException damaged => damaged,
            null => throw new UnreachableException(),
        };
        if (rejection is null)
        {
            return;
        }

        if (record.Position >= cut)
        {
            throw DamagedStoreRecord(Options.DirectoryPath, record.Position, rejection);
        }

        RaiseDamage(record);

        // Replays a well-formed seal record, or returns the rejection of one the catalog contradicts.
        InvalidDataException? Replay(SealRecord seal)
        {
            if (_ledgers.TryGetValue(seal.Handle, out var ledger))
            {
                if (ledger.Ledger != seal.Ledger)
                {
                    return DecodeRejection.Create("seal-ledger-mismatch", "The seal record names a ledger other than its handle's.");
                }

                if (record.Position >= ledger.ReplayStart)
                {
                    ledger.ReplaySeal(record);
                }

                return null;
            }

            if (seal.Handle >= _catalog.NextHandle)
            {
                return DecodeRejection.Create("seal-handle-unassigned", "The seal record names a handle the catalog never assigned.");
            }

            return null;
        }
    }

    /// <summary>
    /// Raises <c>CorruptionEnd</c> to the page after a damaged record for each replica whose replay start is at or below
    /// it (ledgerstore.md §6.2).
    /// </summary>
    private void RaiseDamage(WalRecord record)
    {
        var end = new WalPage(record.Page.File, checked(record.Page.Index + 1));
        foreach (var replica in LiveReplicas().Where(replica => replica.ReplayStart <= record.Position))
        {
            replica.RaiseCorruptionEnd(end);
        }
    }

    /// <summary>
    /// Applies a create or drop record at or above the catalog cut, checking it against the catalog (ledgerstore.md
    /// §7.3); a damaged one refuses the open. A drop discards the replica's slot table and closes its files.
    /// </summary>
    /// <returns>The handle of the replica a drop removed, or <see langword="null"/>.</returns>
    private async ValueTask<uint?> ApplyRecoveredStoreRecordAsync(WalRecord record, Dictionary<uint, LoadedDirectory> loaded)
    {
        var decoding = LedgerRecords.DecodeStore(record.Payload);
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        var parsed = decoding switch
        {
            StoreRecord valid => valid,
            InvalidDataException rejection => throw DamagedStoreRecord(Options.DirectoryPath, record.Position, rejection),
            null => throw new UnreachableException(),
        };

        LiveReplica? dropped;
        lock (_sync)
        {
            if (ApplyLocked(parsed, record.Position, replica => Adopt(this, replica, loaded, record.Position), out dropped) is { } contradiction)
            {
                throw DamagedStoreRecord(Options.DirectoryPath, record.Position, contradiction);
            }
        }

        if (dropped is null)
        {
            return null;
        }

        await dropped.StopAsync().ConfigureAwait(false);
        await dropped.RetireAsync().ConfigureAwait(false);
        return dropped.Handle;
    }

    /// <summary>
    /// Checks the recovered catalog against what bounds it (ledgerstore.md §8.7 step 8): no checkpoint directory's handle
    /// lies at or above its next handle (LS-I6), no live replica's first file was released (LS-I4), and it follows every
    /// valid catalog checkpoint the open read (LS-I2).
    /// </summary>
    /// <exception cref="LedgerReplicaCorruptException">A check fails; the store keeps its files.</exception>
    private void CheckRecoveredCatalog(string root, ulong firstRetainedFile, List<CatalogCheckpoint> checkpoints, IEnumerable<uint> directories)
    {
        // LS-I6: a replica's first checkpoint creates its directory after its create record is durable, so a directory
        // whose handle the catalog never assigned shows the WAL lost a create.
        var catalog = Catalog;
        var unassigned = directories.Where(handle => handle >= catalog.NextHandle).Order().ToList();
        if (unassigned.Count != 0)
        {
            throw new LedgerReplicaCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The LedgerStore at '{root}' holds a checkpoint directory for handle {unassigned[0]}, which its WAL never assigned: the WAL lost a create."));
        }

        // LS-I4: a listed replica whose first file was released is a dropped one, whose drop record the replay applied.
        if (catalog.Replicas.FirstOrDefault(replica => replica.FirstFile < firstRetainedFile) is { } released)
        {
            throw new LedgerReplicaCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The LedgerStore at '{root}' lists the replica of ledger {released.Ledger}, whose first file {released.FirstFile} lies below its first retained WAL file {firstRetainedFile}."));
        }

        // LS-I2: every tombstone, and every ledger listed or dropped, of a valid catalog checkpoint survives.
        if (checkpoints.FirstOrDefault(checkpoint => !catalog.Follows(checkpoint.Catalog)) is { } contradicted)
        {
            throw new LedgerReplicaCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The LedgerStore at '{root}' derived a catalog which lacks a tombstone, replica, or handle of its catalog checkpoint at {contradicted.Cut}."));
        }
    }

    /// <summary>
    /// The checkpoint directory of a replica, opened with its newest usable slot-index checkpoint.
    /// </summary>
    private sealed record LoadedDirectory(WalDirectory Directory, SlotCheckpoints Checkpoints, LoadedCheckpoint? Checkpoint)
    {
        public async ValueTask DisposeAsync()
        {
            await Checkpoints.DisposeAsync().ConfigureAwait(false);
            await Directory.DisposeAsync().ConfigureAwait(false);
        }
    }
}
