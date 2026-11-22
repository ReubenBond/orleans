using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal.Simulation;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A simulated node root: the <see cref="SimulatedWalStorage"/> which each crash replaces, and the root's cluster claim,
/// which survives every crash as a published claim does (LS-I12).
/// </summary>
internal sealed class SimulatedNodeRoot
{
    internal const string Path = "node";

    internal SimulatedNodeRoot(
        LedgerStoreOptions options,
        LedgerStoreCheckpointOptions checkpointOptions,
        SimulatedWalStorage? storage = null)
    {
        Options = options;
        CheckpointOptions = checkpointOptions;
        Storage = storage ?? new SimulatedWalStorage();
    }

    internal static string WalPath { get; } = LedgerStore.WalPath(Path);

    internal SimulatedWalStorage Storage { get; private set; }

    internal InMemoryNodeRootClaim Claim { get; } = new();

    internal LedgerStoreOptions Options { get; }

    internal LedgerStoreCheckpointOptions CheckpointOptions { get; }

    internal ValueTask<LedgerStore> OpenAsync(bool create = false, LedgerStoreOptions? options = null, TimeProvider? timeProvider = null) =>
        LedgerStore.OpenAsync(
            Storage,
            options ?? Options,
            LedgerReplicaTestData.Cluster,
            create,
            CheckpointOptions,
            timeProvider ?? new FakeTimeProvider(),
            Claim,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Creates the root's store and its replica of <see cref="LedgerReplicaTestData.Ledger"/>, and opens the replica,
    /// which owns the store.
    /// </summary>
    internal async ValueTask<StoreOwningReplica> CreateReplicaAsync(LedgerReplicaOptions window, LedgerStoreOptions? options = null, TimeProvider? timeProvider = null) =>
        await StoreOwningReplica.OpenAsync(await OpenAsync(create: true, options, timeProvider), window, create: true);

    /// <summary>
    /// Opens the root's store and its replica of <see cref="LedgerReplicaTestData.Ledger"/>, which owns the store.
    /// </summary>
    internal async ValueTask<StoreOwningReplica> OpenReplicaAsync(LedgerReplicaOptions window, LedgerStoreOptions? options = null, TimeProvider? timeProvider = null) =>
        await StoreOwningReplica.OpenAsync(await OpenAsync(create: false, options, timeProvider), window, create: false);

    /// <summary>
    /// Crashes the process, or the operating system, leaving the storage a restart finds.
    /// </summary>
    internal void Crash(bool operatingSystem) => Storage = Storage.Crash(operatingSystem);

    /// <summary>
    /// Crashes the storage and releases the store, whose operations then fail; the released store holds no handle on
    /// the crashed storage.
    /// </summary>
    internal async Task CrashAsync(LedgerStore store, bool operatingSystem)
    {
        var crashed = Storage;
        Crash(operatingSystem);
        try
        {
            await store.DisposeAsync();
        }
        catch (IOException)
        {
            // The crashed storage fails maintenance's storage operations and the final checkpoints, which disposal
            // surfaces once it has released everything.
        }

        Assert.Equal(0, crashed.OpenHandleCount);
    }

    internal Task CrashAsync(StoreOwningReplica replica, bool operatingSystem) => CrashAsync(replica.Store, operatingSystem);
}

/// <summary>
/// A replica of one ledger which owns its store, so a test owns the two together: disposing it releases the replica and
/// disposes the store.
/// </summary>
internal sealed class StoreOwningReplica : ILedgerReplica
{
    private StoreOwningReplica(LedgerStore store, LedgerId ledger, LedgerStoreReplica replica)
    {
        Store = store;
        LedgerId = ledger;
        Replica = replica;
    }

    internal LedgerStore Store { get; }

    internal LedgerId LedgerId { get; }

    internal LedgerStoreReplica Replica { get; }

    /// <summary>
    /// Gets the live replica of the replica's ledger, which the store must list.
    /// </summary>
    internal LiveReplica Ledger => Replica.Ledger;

    /// <summary>
    /// Opens the replica of <paramref name="ledger"/>, or of <see cref="LedgerReplicaTestData.Ledger"/>,
    /// creating the replica first with the replica's window when <paramref name="create"/> is set, as its first write would;
    /// the result owns <paramref name="store"/>, which a failure disposes.
    /// </summary>
    internal static async ValueTask<StoreOwningReplica> OpenAsync(LedgerStore store, LedgerReplicaOptions window, bool create, LedgerId? ledger = null)
    {
        var id = ledger ?? LedgerReplicaTestData.Ledger;
        try
        {
            if (create)
            {
                _ = await store.CreateLiveAsync(id, window.WindowSize, TestContext.Current.CancellationToken);
            }

            return new StoreOwningReplica(store, id, store.OpenReplica(id, window));
        }
        catch
        {
            await store.DisposeAsync();
            throw;
        }
    }

    internal ValueTask CheckpointAsync(CancellationToken cancellationToken = default) => Replica.CheckpointAsync(cancellationToken);

    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
        Replica.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        Replica.ReadAsync(slot, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        Replica.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
        Replica.ReadTailAsync(contiguousTail, seal, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Replica.DisposeAsync();
        await Store.DisposeAsync();
    }
}

/// <summary>
/// Creates LedgerStore replicas in tests as their first write would.
/// </summary>
internal static class LedgerStoreTestExtensions
{
    /// <summary>
    /// The window a replica <see cref="CreateReplicaAsync"/> creates records, which the tests' handles over it use.
    /// </summary>
    internal const int DefaultWindow = 64;

    /// <summary>
    /// Creates the replica of <paramref name="ledger"/> as its first write would, with one create record which holds
    /// <paramref name="windowSize"/>, and returns once the catalog lists it durably; a listed replica is returned as it
    /// is (LS-I1, LR-I6), and <see langword="null"/> when the catalog holds the ledger's tombstone (LS-I2).
    /// </summary>
    internal static ValueTask<LiveReplica?> CreateReplicaAsync(
        this LedgerStore store, LedgerId ledger, CancellationToken cancellationToken = default, int windowSize = DefaultWindow) =>
        store.CreateLiveAsync(ledger, windowSize, cancellationToken);

    /// <summary>
    /// Waits until the WAL retires every record it accepted, which advances its durable frontier past them: the WAL answers
    /// a record before it advances the frontier, so a read of the WAL or a capture at an answer could miss the record
    /// (#134, #159). The caller keeps the WAL quiet while this waits: it has awaited every append it started and starts no
    /// other. Reading the frontier throws once the WAL stops (conventions.md §14), so a stopped WAL fails the wait at once.
    /// </summary>
    internal static Task WaitForFrontierAsync(this LedgerStore store) =>
        WaitUntilAsync(
            () =>
            {
                _ = store.Wal.DurableFrontier;
                return store.Wal.OutstandingRecords == 0;
            },
            "the WAL retires every record it accepted");

    /// <summary>
    /// Requests a catalog checkpoint and waits until maintenance has written it and deleted and released what it
    /// supersedes; the WAL must hold a record past the newest catalog checkpoint's cut, or none is written (§8.8). The
    /// request follows the durable frontier past every record, so its capture cuts past them rather than at the newest cut.
    /// </summary>
    internal static async Task WriteNewCatalogCheckpointAsync(this LedgerStore store)
    {
        await store.WaitForFrontierAsync();
        var written = store.CatalogCheckpointsWritten;
        store.RequestCatalogCheckpoint();
        await WaitUntilAsync(() => store.CatalogCheckpointsWritten != written && store.MaintenanceIdle, "maintenance writes the requested catalog checkpoint");
    }

    /// <summary>
    /// Writes a catalog checkpoint whose cut lies past every write acknowledged before the call, so WAL damage below it
    /// faults only the replicas it reaches rather than refusing the open (LS-I5). It returns with maintenance idle and no
    /// catalog checkpoint requested; that cut then stays the newest until something makes another due (§8.8): a request,
    /// WAL past the cut reaching the replay bound, or a create or drop record once the checkpoint interval passes.
    /// </summary>
    internal static async Task CheckpointCatalogPastWritesAsync(this LedgerStore store)
    {
        var last = store.Replicas.Select(ledger => store.FindLive(ledger)!.LastPublished).DefaultIfEmpty().Max();
        await WaitUntilAsync(() => store.Wal.DurableFrontier > last, "the durable frontier passes every acknowledged write");
        var frontier = store.Wal.DurableFrontier;

        // One request suffices: the capture which takes it reads the frontier no earlier than this, and the frontier never
        // decreases. A request left behind would cut a later checkpoint wherever the frontier has moved by then.
        store.RequestCatalogCheckpoint();
        await WaitUntilAsync(
            () => store.CatalogCut is { } cut && cut >= frontier && store.CatalogCheckpointCurrent && store.MaintenanceIdle,
            "maintenance writes a catalog checkpoint cut at or past the frontier");
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, failing the test once 30 seconds pass.
    /// </summary>
    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30), $"Timed out waiting until {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }
}
