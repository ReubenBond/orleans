using System.Globalization;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32.SafeHandles;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the LedgerStore invariants: one store per node root multiplexes every ledger replica through one WAL, whose
/// create and drop records decide existence (LS-I1); a replica is created by its first write, recovery write, or seal unless its
/// ledger's tombstone refuses it, and a drop leaves that tombstone for the node's life (LS-I2, LR-I3, LR-I7); retention
/// follows the live first files and the older kept catalog checkpoint (LS-I4); damage is scoped by first file (LS-I5);
/// the node identity moves with the WAL (LS-I6); and the root's cluster claim precedes its store (LS-I12). The catalog
/// checkpoints' own rules are pinned by <see cref="LedgerStoreCatalogCheckpointTests"/>.
/// </summary>
public sealed class LedgerStoreTests : IDisposable
{
    private static readonly string WalPath = SimulatedNodeRoot.WalPath;
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    // 400-byte payloads fill one 512-byte page each, so an 8-page file holds 7 records.
    private static readonly LedgerStoreOptions SmallFiles = new()
    {
        DirectoryPath = SimulatedNodeRoot.Path,
        PageSize = 512,
        FileSize = 8 * 512,
        WriteWindowPages = 4,
        PreallocateFiles = false,
    };

    private static readonly LedgerStoreCheckpointOptions ExplicitOnly = new() { CheckpointInterval = TimeSpan.FromDays(1) };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task InterleavedLedgersRecoverEveryAcknowledgedWriteAtAnyCrashCut()
    {
        for (var seed = 0; seed < 24; seed++)
        {
            await RunAsync(seed);
        }

        static async Task RunAsync(int seed)
        {
            var random = new Random(seed);
            var root = NewRoot(new SimulatedWalStorage(seed));
            var store = await root.OpenAsync(create: true);
            var ledgers = LedgerReplicaTestData.Ledgers(1, 2, 3);
            var replicas = new List<LedgerStoreReplica>();
            foreach (var ledger in ledgers)
            {
                replicas.Add(await CreateAndOpenReplicaAsync(store, ledger));
            }

            root.Storage.HoldWrites = true;
            var writes = new List<(LedgerId Ledger, long Slot, Task<ReplicaWriteOutcome> Write)>();
            for (var slot = 0L; slot < 30; slot++)
            {
                for (var index = 0; index < replicas.Count; index++)
                {
                    writes.Add((ledgers[index], slot, LedgerStoreReplicaTests.WriteAsync(replicas[index], slot, Payload(ledgers[index], slot))));
                }
            }

            var completions = random.Next(0, 40);
            for (var completed = 0; completed < completions && !writes.All(static write => write.Write.IsCompleted);)
            {
                if (root.Storage.CompleteWrite())
                {
                    completed++;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken);
            var acknowledged = writes.Where(static write => write.Write.IsCompletedSuccessfully).ToArray();
            await root.CrashAsync(store, operatingSystem: random.Next(2) == 0);
            await ((Task)Task.WhenAll(writes.Select(static write => write.Write))).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            await using var reopened = await root.OpenAsync();
            foreach (var ledger in ledgers)
            {
                var replica = Replica(reopened, ledger);
                for (var slot = 0L; slot < 30; slot++)
                {
                    var value = await LedgerReplicaTestData.ReadBytesAsync(replica, slot, TestContext.Current.CancellationToken);
                    if (acknowledged.Any(write => write.Ledger == ledger && write.Slot == slot))
                    {
                        Assert.Equal(Payload(ledger, slot), value);
                    }
                    else if (value is not null)
                    {
                        Assert.Equal(Payload(ledger, slot), value);
                    }
                }

                _ = await replica.ReadTailAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>
    /// LS-I2, LR-I3: a drop is one WAL record which unlists the replica and leaves its ledger's tombstone. The dropped
    /// replica then refuses every operation, nothing creates it again, a second drop writes nothing, and all of it holds
    /// across a restart; the other replicas keep serving.
    /// </summary>
    [Fact]
    public async Task ADropIsOneWalRecordWhichUnlistsTheReplicaAndLeavesItsTombstone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var below = await CreateAndOpenReplicaAsync(store, new(3));
        var replica = await CreateAndOpenReplicaAsync(store, new(5));
        var above = await CreateAndOpenReplicaAsync(store, new(8));
        await WriteRangeAsync(below, new(3), 0, 3);
        await WriteRangeAsync(replica, new(5), 0, 3);
        await WriteRangeAsync(above, new(8), 0, 3);

        await store.DropReplicaAsync(new(5), cancellationToken);
        Assert.Equal(LedgerReplicaTestData.Ledgers(5), await DropRecordsAsync(store));
        await store.DropReplicaAsync(new(5), cancellationToken);
        Assert.Equal(LedgerReplicaTestData.Ledgers(5), await DropRecordsAsync(store));
        await AssertRefusedAsync(replica);
        await AssertRefusedAsync(store.OpenReplica(new(5), Window));
        DroppedAnswers.AssertDropped(await store.CreateReplicaAsync(new(5), cancellationToken));
        Assert.Equal(LedgerReplicaTestData.Ledgers(3, 8), store.Replicas);
        Assert.Equal([0u, 2u], store.Catalog.Replicas.Select(static replica => replica.Handle));
        Assert.Equal(LedgerReplicaTestData.Ledgers(5), store.Catalog.Tombstones);
        Assert.Equal(3u, store.Catalog.NextHandle);

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(3, 8), reopened.Replicas);
        Assert.Equal(LedgerReplicaTestData.Ledgers(5), reopened.Catalog.Tombstones);
        Assert.Equal(3u, reopened.Catalog.NextHandle);
        await AssertRefusedAsync(reopened.OpenReplica(new(5), Window));
        DroppedAnswers.AssertDropped(await reopened.CreateReplicaAsync(new(5), cancellationToken));
        await reopened.DropReplicaAsync(new(5), cancellationToken);
        Assert.Equal(LedgerReplicaTestData.Ledgers(5), await DropRecordsAsync(reopened));
        await AssertRangeAsync(Replica(reopened, new(3)), new(3), 0, 3);
        await AssertRangeAsync(Replica(reopened, new(8)), new(8), 0, 3);
    }

    /// <summary>
    /// ledger-replicas.md §2.3, LS-I2: a drop of a ledger the node does not hold writes a drop record all the same, which
    /// leaves the ledger's tombstone and takes no handle; every operation on the ledger then refuses and creates
    /// nothing, across restarts.
    /// </summary>
    [Fact]
    public async Task ADropOfALedgerTheNodeDoesNotHoldLeavesATombstone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.DropReplicaAsync(new(4), cancellationToken);
        Assert.Equal(LedgerReplicaTestData.Ledgers(4), await DropRecordsAsync(store));
        Assert.Equal(LedgerReplicaTestData.Ledgers(4), store.Catalog.Tombstones);
        Assert.Equal(0u, store.Catalog.NextHandle);

        for (var restart = 0; restart < 2; restart++)
        {
            await AssertRefusedAsync(store.OpenReplica(new(4), Window));
            DroppedAnswers.AssertDropped(await store.CreateReplicaAsync(new(4), cancellationToken));
            Assert.Empty(store.Replicas);
            Assert.Empty(await StoreRecordsAsync(store));
            Assert.Empty(await ListSlotsAsync(root));
            await root.CrashAsync(store, operatingSystem: true);
            store = await root.OpenAsync();
        }

        await using (store)
        {
            Assert.Equal(LedgerReplicaTestData.Ledgers(4), store.Catalog.Tombstones);
            await CreateAndOpenReplicaAsync(store, new(3));
            Assert.Equal(LedgerReplicaTestData.Ledgers(3), store.Replicas);
        }
    }

    /// <summary>
    /// Disposing a store closes the WAL's files, every replica's checkpoint files, and their directories, whether the
    /// store shut down cleanly or after a drop (test/Durable.Storage/docs/spec/verification.md §11.1).
    /// </summary>
    [Fact]
    public async Task DisposalClosesEveryFileAndDirectory()
    {
        var root = NewRoot();
        await using (var store = await root.OpenAsync(create: true))
        {
            var kept = await CreateAndOpenReplicaAsync(store, new(3));
            var dropped = await CreateAndOpenReplicaAsync(store, new(5));
            await WriteRangeAsync(kept, new(3), 0, 3);
            await WriteRangeAsync(dropped, new(5), 0, 3);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(kept);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(dropped);
            await DropAsync(store, new(5));
            Assert.True(root.Storage.OpenHandleCount > 0);
        }

        Assert.Equal(0, root.Storage.OpenHandleCount);
        await using (var reopened = await root.OpenAsync())
        {
            await AssertRangeAsync(Replica(reopened, new(3)), new(3), 0, 3);
        }

        Assert.Equal(0, root.Storage.OpenHandleCount);
    }

    /// <summary>
    /// LS-I2: "A drop first stops admitting operations to the replica and waits for those admitted, so no mutation of it
    /// completes after the drop returns"; a later one waits for the drop record and then answers dropped.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADropWaitsForAdmittedMutationsAndRefusesLaterOnes()
    {
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        var replica = await CreateAndOpenReplicaAsync(store, LedgerReplicaTestData.Ledger);
        root.Storage.HoldWrites = true;
        var writes = Enumerable.Range(0, 5).Select(slot => LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(LedgerReplicaTestData.Ledger, slot))).ToArray();
        await EventuallyAsync(() => root.Storage.PendingWriteCount != 0, "a write is in flight");

        var drop = store.DropReplicaAsync(LedgerReplicaTestData.Ledger, TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(drop.IsCompleted);
        var later = LedgerStoreReplicaTests.WriteAsync(replica, 9, Payload(LedgerReplicaTestData.Ledger, 9));

        root.Storage.HoldWrites = false;
        await EventuallyAsync(
            () =>
            {
                _ = root.Storage.CompleteWrite();
                return drop.IsCompleted;
            },
            "the drop completes");

        await drop;
        DroppedAnswers.AssertDropped(await later);
        Assert.All(await Task.WhenAll(writes), static result => Assert.Equal(ReplicaWriteCase.Written, result.Case));
        await AssertRefusedAsync(replica);
        Assert.Empty(store.Replicas);
    }

    /// <summary>
    /// Regression test, LS-I2 and conventions §13: a replica answers dropped only once its drop record is durable, since a
    /// crash before then keeps the replica as it was and the node serves it again, so a seal check which counted the
    /// answer as sealed would have counted a replica the crash brings back. A writer write, a recovery write, a seal, a
    /// read, and a contiguous-tail read which meet the admission the drop closed wait while its record is in flight, then
    /// answer dropped once
    /// it is durable, or, when the record fails, the drop's failure, as does every later operation of the replica, and
    /// the restarted store still lists the replica. They answered dropped as soon as the drop closed admission.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OperationsMeetingADropAnswerDroppedOnlyOnceItsRecordIsDurable(bool durable)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var replica = await CreateAndOpenReplicaAsync(store, LedgerReplicaTestData.Ledger);
        await WriteRangeAsync(replica, LedgerReplicaTestData.Ledger, 0, 2);
        root.Storage.HoldWrites = true;
        var drop = store.DropReplicaAsync(LedgerReplicaTestData.Ledger, cancellationToken).AsTask();
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the drop record is in flight");
        var seal = SealAsync();
        var read = ReadAsync();
        var tail = ReadTailAsync();
        var write = LedgerStoreReplicaTests.WriteAsync(replica, 9, Payload(LedgerReplicaTestData.Ledger, 9));
        var recoveryWrite = LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 9, Payload(LedgerReplicaTestData.Ledger, 9));
        Task[] operations = [write, recoveryWrite, seal, read, tail];
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
        Assert.All(operations, static operation => Assert.False(operation.IsCompleted));
        Assert.False(drop.IsCompleted);

        if (durable)
        {
            Assert.True(root.Storage.CompleteWrite());
            await drop;
            DroppedAnswers.AssertDropped(await write);
            DroppedAnswers.AssertDropped(await recoveryWrite);
            DroppedAnswers.AssertDropped(await seal);
            DroppedAnswers.AssertDropped(await read);
            DroppedAnswers.AssertDropped(await tail);

            await store.DisposeAsync();
            return;
        }

        var crashed = root.Storage;
        root.Crash(operatingSystem: true);
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => drop);
        foreach (var operation in operations)
        {
            Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(() => operation));
        }

        Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(() => ReadAsync()));

        try
        {
            await store.DisposeAsync();
        }
        catch (IOException)
        {
            // The crashed storage fails the final checkpoints.
        }

        Assert.Equal(0, crashed.OpenHandleCount);
        await using var reopened = await root.OpenAsync();
        Assert.Equal([LedgerReplicaTestData.Ledger], reopened.Replicas);

        async Task<ReplicaTailRead> SealAsync() => await replica.ReadTailAsync(new Slot(2), seal: true, cancellationToken);
        async Task<ReplicaRead> ReadAsync() => await replica.ReadAsync(Slot.Zero, cancellationToken);
        async Task<ReplicaTailRead> ReadTailAsync() => await replica.ReadTailAsync(new Slot(2), seal: false, cancellationToken);
    }

    /// <summary>
    /// LS-I1, LS-I2, LR-I7: in any sequence of creations and drops, a creation lists an unlisted ledger under the next
    /// handle unless the catalog holds its tombstone, and returns a listed one as it is; a drop removes exactly its own
    /// replica, if listed, and leaves its tombstone; handles are never reused; and the live replicas, the tombstones, and
    /// the next handle survive a restart.
    /// </summary>
    [Fact]
    public void CreationIsDecidedAgainstTheTombstonesUnderTheGate() =>
        Gen.Select(Gen.Int[0, 1], Gen.ULong[1, 8]).Array[1, 24].Sample(
            operations => _leaks.InScope(() => RunAsync(operations).GetAwaiter().GetResult()),
            iter: 30,
            threads: 1);

    /// <summary>
    /// LS-I1, LS-I2, LS-I11, LR-I3, LR-I7, LR-I8: in any sequence of writer writes, recovery writes, seals, drops,
    /// catalog checkpoints, replica checkpoints, the WAL releases they enable, and process or operating-system restarts,
    /// over three ledgers, the node matches a model in which each ledger is absent, live with its DATA and seal, or
    /// dropped. A writer write, recovery write, or seal creates an absent ledger's replica; a writer write on a sealed
    /// replica answers <see cref="ReplicaAnswers.Sealed"/> and stores nothing, while a recovery write stores on any live
    /// replica; a drop removes the replica and leaves its tombstone, which refuses every later operation; and a restart
    /// changes nothing. After every step the listing, the tombstones, each replica's DATA and seal, and each answer match
    /// the model.
    /// </summary>
    [Fact]
    public Task TheNodeMatchesItsModelOfAbsentLiveAndDroppedReplicas() =>
        NodeSteps.Array[1, 40].SampleAsync(steps => _leaks.InScopeAsync(() => RunModelAsync(steps)), iter: 100, threads: 1);

    [Fact]
    public async Task ADropRemovesOnlyItsOwnReplicaAndTheOthersKeepServing()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var low = await CreateAndOpenReplicaAsync(store, new(2));
        var dropped = await CreateAndOpenReplicaAsync(store, new(6));
        var high = await CreateAndOpenReplicaAsync(store, new(9));
        foreach (var (ledger, replica) in new[] { (new LedgerId(2), low), (new LedgerId(6), dropped), (new LedgerId(9), high) })
        {
            await WriteRangeAsync(replica, ledger, 0, 3);
        }

        await DropAsync(store, new(6));
        Assert.Equal(LedgerReplicaTestData.Ledgers(2, 9), store.Replicas);
        Assert.Null(store.Catalog.Find(new(6)));
        await AssertRefusedAsync(dropped);
        foreach (var (ledger, replica) in new[] { (new LedgerId(2), low), (new LedgerId(9), high) })
        {
            await AssertServesAsync(replica, ledger);
        }

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(2, 9), reopened.Replicas);
        await AssertRefusedAsync(reopened.OpenReplica(new(6), Window));
        foreach (var ledger in LedgerReplicaTestData.Ledgers(2, 9))
        {
            var replica = Replica(reopened, ledger);
            await AssertRangeAsync(replica, ledger, 0, 5);
            Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 5, Payload(ledger, 5))).Case);
        }

        static async Task AssertServesAsync(ILedgerReplica replica, LedgerId ledger)
        {
            var token = TestContext.Current.CancellationToken;
            await AssertRangeAsync(replica, ledger, 0, 3);
            Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 3, Payload(ledger, 3))).Case);
            Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 4, Payload(ledger, 4)));
            Assert.False((await replica.ReadTailAsync(token)).Tail.IsSealed);
            var range = await replica.ReadAsync(new Slot(4), 0, 1, token);
            Assert.Equal(ReplicaReadCase.Batch, range.Case);
            using var value = range.Batch;
            Assert.Equal(Payload(ledger, 4)[..1], value.ToArray());
        }
    }

    /// <summary>
    /// LS-I1, LR-I6: a creation is one create record which takes the next handle, with the record's file as the
    /// replica's first file and the creating request's window, and writes no file or directory; a repeated creation returns
    /// the listed replica, whatever its window. A handle is never reused, across drops and restarts.
    /// </summary>
    [Fact]
    public async Task ACreationTakesTheNextHandleAndWritesNoFileOrDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var file = store.Wal.CurrentFile;
        var live = (await store.CreateLiveAsync(new(5), Window.WindowSize, cancellationToken))!;
        Assert.Equal(new CatalogReplica(0, Window.WindowSize, file, new(5)), live.Entry);
        Assert.Same(live, await store.CreateLiveAsync(new(5), 8, cancellationToken));
        Assert.False(live.HasCheckpointDirectory);
        Assert.Empty(await ListSlotsAsync(root));
        await WriteRangeAsync(Replica(store, new(5)), new(5), 0, 3);
        Assert.Equal(1u, store.Catalog.NextHandle);

        await DropAsync(store, new(5));
        Assert.Equal(1u, store.Catalog.NextHandle);
        Assert.Equal(1u, (await store.CreateLiveAsync(new(6), Window.WindowSize, cancellationToken))!.Handle);
        await WriteRangeAsync(Replica(store, new(6)), new(6), 0, 2);

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(1u, (await reopened.CreateLiveAsync(new(6), Window.WindowSize, cancellationToken))!.Handle);
        Assert.Equal(2u, reopened.Catalog.NextHandle);
        Assert.Equal(2u, (await reopened.CreateLiveAsync(new(7), Window.WindowSize, cancellationToken))!.Handle);
        Assert.Equal(LedgerReplicaTestData.Ledgers(6, 7), reopened.Replicas);
        await AssertRangeAsync(Replica(reopened, new(6)), new(6), 0, 2);
    }

    /// <summary>
    /// LS-I1, §8.5: creations which wait at the catalog gate are decided together and written as one create record, in
    /// arrival order, so they become live together; a refused one fails alone, and a repeated one shares its replica.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WaitingCreationsAreWrittenAsOneCreateRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.DropReplicaAsync(new(9), cancellationToken);
        root.Storage.HoldWrites = true;
        var first = CreateAsync(store, new(1));
        await EventuallyAsync(() => root.Storage.PendingWriteCount != 0, "the first create record is in flight");

        var waiting = LedgerReplicaTestData.Ledgers(4, 2, 9, 3, 2).Select(ledger => CreateAsync(store, ledger)).ToArray();
        Assert.Empty(store.Replicas);
        root.Storage.HoldWrites = false;
        await EventuallyAsync(
            () =>
            {
                _ = root.Storage.CompleteWrite();
                return first.IsCompleted && waiting.All(static creation => creation.IsCompleted);
            },
            "every creation completes");

        Assert.Equal(0u, (await first).Handle);
        DroppedAnswers.AssertDropped(await waiting[2]);
        var created = await Task.WhenAll(waiting[0], waiting[1], waiting[3], waiting[4]);
        Assert.Equal([1u, 2u, 3u, 2u], created.Select(static live => live.Handle));
        Assert.Same(await waiting[1], await waiting[4]);
        var batchFile = (await waiting[0]).Entry.FirstFile;
        Assert.All(created, live => Assert.Equal(batchFile, live.Entry.FirstFile));

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1, 2, 3, 4), reopened.Replicas);
        Assert.Equal(
            [[new CatalogPair(new(1), 0, 64)], [new CatalogPair(new(4), 1, 64), new CatalogPair(new(2), 2, 64), new CatalogPair(new(3), 3, 64)]],
            await StoreRecordsAsync(reopened));

        static async Task<LiveReplica> CreateAsync(LedgerStore store, LedgerId ledger) =>
            (await store.CreateLiveAsync(ledger, Window.WindowSize, TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// LS-I1, LR-I7: a read of a replica the catalog neither lists nor holds a tombstone for answers Missing, a ranged
    /// read Missing, and a contiguous-tail read its argument, unsealed, as of an empty replica; none appends a record,
    /// creates, or retains anything, across restarts, and the first write creates the replica.
    /// </summary>
    [Fact]
    public async Task AReadOfAnUnlistedUndroppedReplicaReadsEmptyAndAppendsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        for (var restart = 0; restart < 2; restart++)
        {
            var frontier = store.Wal.DurableFrontier;
            var replica = store.OpenReplica(new(4), Window);
            Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(new Slot(3), cancellationToken)).Case);
            Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(new Slot(3), 0, 1, cancellationToken)).Case);
            Assert.Equal(new LedgerReplicaTail(new Slot(7), IsSealed: false), await replica.ReadTailAsync(new Slot(7), seal: false, cancellationToken));
            Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(cancellationToken));
            await replica.CheckpointAsync(cancellationToken);
            Assert.Equal(frontier, store.Wal.DurableFrontier);
            Assert.Empty(store.Replicas);
            Assert.Equal(0u, store.Catalog.NextHandle);
            Assert.Empty(await ListSlotsAsync(root));
            await root.CrashAsync(store, operatingSystem: true);
            store = await root.OpenAsync();
        }

        await using (store)
        {
            Assert.Empty(await StoreRecordsAsync(store));
            Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(new(4), Window), 0, Payload(new(4), 0))).Case);
            Assert.Equal(LedgerReplicaTestData.Ledgers(4), store.Replicas);
        }
    }

    /// <summary>
    /// LS-I2, §6.5: creations are decided under the catalog gate which orders drops, so a creation which waits at the gate
    /// behind a drop of its ledger is refused by the drop's tombstone and writes no create record; every creating path,
    /// a write, a recovery write, and a seal, and every read then answers as dropped.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task EveryCreatingPathAndEveryReadAnswersDroppedForATombstonedLedger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        root.Storage.HoldWrites = true;
        var drop = store.DropReplicaAsync(new(6), cancellationToken).AsTask();
        await EventuallyAsync(() => root.Storage.PendingWriteCount != 0, "the drop record is in flight");

        var creation = LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(new(6), Window), 0, Payload(new(6), 0));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, cancellationToken);
        Assert.False(creation.IsCompleted);
        root.Storage.HoldWrites = false;
        await EventuallyAsync(
            () =>
            {
                _ = root.Storage.CompleteWrite();
                return drop.IsCompleted && creation.IsCompleted;
            },
            "the drop and the creation complete");

        await drop;
        DroppedAnswers.AssertDropped(await creation);
        await AssertRefusedAsync(store.OpenReplica(new(6), Window));
        Assert.Empty(await StoreRecordsAsync(store));
        Assert.Equal(0u, store.Catalog.NextHandle);
    }

    /// <summary>
    /// LS-I1: a record names only constructed ledger IDs, so opening or dropping the default <see cref="LedgerId"/>
    /// fails before any record, and the store keeps serving and reopens unchanged.
    /// </summary>
    [Fact]
    public async Task TheDefaultLedgerIdNeverReachesTheWal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.CreateReplicaAsync(new(3), cancellationToken);
        _ = Assert.Throws<ArgumentException>(() => store.OpenReplica(default, Window));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await store.DropReplicaAsync(default, cancellationToken));
        await store.CreateReplicaAsync(new(4), cancellationToken);

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(3, 4), reopened.Replicas);
        Assert.Equal(2u, reopened.Catalog.NextHandle);
    }

    [Fact]
    public async Task ListReplicasListsTheLiveReplicasAscending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        Assert.Empty(store.Replicas);
        foreach (var ledger in LedgerReplicaTestData.Ledgers(9, 3, 7, 12))
        {
            await store.CreateReplicaAsync(ledger, cancellationToken);
        }

        Assert.Equal(LedgerReplicaTestData.Ledgers(9, 3, 7, 12), store.Catalog.Replicas.Select(static replica => replica.Ledger));
        Assert.Equal(LedgerReplicaTestData.Ledgers(3, 7, 9, 12), store.Replicas);
        await DropAsync(store, new(7));
        Assert.Equal(LedgerReplicaTestData.Ledgers(3, 9, 12), store.Replicas);

        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(3, 9, 12), reopened.Replicas);
    }

    /// <summary>
    /// LS-I4: while the store holds two valid catalog checkpoints, it releases WAL files only below each live replica's
    /// first file and the cut of the older one, so a dropped replica's files are released once two catalog checkpoints
    /// cover its drop, and its tombstone outlives the release of its drop record (LS-I2).
    /// </summary>
    [Fact]
    public async Task RetentionReleasesFilesOnlyBelowTheLowestLiveFirstFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var first = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(first, new(1), 0, 20);
        var second = await CreateAndOpenReplicaAsync(store, new(2));
        var secondFile = store.FindLive(new(2))!.Entry.FirstFile;
        Assert.True(secondFile >= 3, $"The second ledger starts in file {secondFile}.");
        await WriteRangeAsync(second, new(2), 0, 20);
        Assert.Equal(1ul, root.Storage.ListFiles(WalPath)[0]);

        // The first ledger pins file 1 across a restart.
        Assert.Equal(new LedgerStoreRetention(store.Wal.CurrentFile, 1, new LedgerId(1), 2), store.GetRetention());
        Assert.Equal([new LedgerReplicaRetention(new(1), 1), new LedgerReplicaRetention(new(2), secondFile)], store.ListRetention());
        await root.CrashAsync(store, operatingSystem: true);
        store = await root.OpenAsync();
        Assert.Equal(1ul, root.Storage.ListFiles(WalPath)[0]);
        await DropAsync(store, new(1));
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(() => root.Storage.ListFiles(WalPath)[0] == secondFile, "the WAL is released through the dropped replica's files");
        Assert.Equal(new LedgerStoreRetention(store.Wal.CurrentFile, secondFile, new LedgerId(2), 1), store.GetRetention());
        Assert.Equal([new LedgerReplicaRetention(new(2), secondFile)], store.ListRetention());
        await AssertRangeAsync(Replica(store, new(2)), new(2), 0, 20);

        await root.CrashAsync(store, operatingSystem: true);
        store = await root.OpenAsync();
        Assert.Equal(secondFile, root.Storage.ListFiles(WalPath)[0]);
        await AssertRangeAsync(Replica(store, new(2)), new(2), 0, 20);

        // With no live replica the store keeps the WAL from the older kept catalog checkpoint's cut, which precedes the
        // last drop.
        var dropFile = store.Wal.CurrentFile;
        await DropAsync(store, new(2));
        store.RequestCatalogCheckpoint();
        await EventuallyAsync(
            () => store.ValidCatalogCheckpoints.Count == 2 && root.Storage.ListFiles(WalPath)[0] == store.ValidCatalogCheckpoints[0].File,
            "the WAL is released through the older kept catalog checkpoint's cut");
        var kept = store.ValidCatalogCheckpoints[0].File;
        Assert.InRange(kept, secondFile + 1, dropFile - 1);
        Assert.Equal(new LedgerStoreRetention(store.Wal.CurrentFile, kept, null, 0), store.GetRetention());
        await root.CrashAsync(store, operatingSystem: false);
        store = await root.OpenAsync();
        Assert.Equal(kept, root.Storage.ListFiles(WalPath)[0]);
        Assert.Empty(store.Replicas);
        Assert.Empty(store.ListRetention());
        Assert.Equal(new LedgerStoreRetention(store.Wal.CurrentFile, kept, null, 0), store.GetRetention());

        // Once two newer catalog checkpoints cover a later write, every drop record's file is released, and the tombstones
        // still refuse both ledgers across a restart.
        var third = await CreateAndOpenReplicaAsync(store, new(3));
        var thirdFile = store.FindLive(new(3))!.Entry.FirstFile;
        for (var round = 0; round < 2; round++)
        {
            await WriteRangeAsync(third, new(3), round, 1);
            await store.WriteNewCatalogCheckpointAsync();
        }

        Assert.Equal(thirdFile, root.Storage.ListFiles(WalPath)[0]);
        Assert.True(thirdFile > dropFile);
        await root.CrashAsync(store, operatingSystem: true);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1, 2), reopened.Catalog.Tombstones);
        await AssertRefusedAsync(reopened.OpenReplica(new(1), Window));
        await AssertRefusedAsync(reopened.OpenReplica(new(2), Window));
        DroppedAnswers.AssertDropped(await reopened.CreateReplicaAsync(new(2), cancellationToken));
        await AssertRangeAsync(Replica(reopened, new(3)), new(3), 0, 2);
    }

    /// <summary>
    /// LS-I1, LS-I8: a replica's checkpoint directory is created by its first checkpoint, so a replica created and
    /// written but never checkpointed has none, and recovers from the WAL alone.
    /// </summary>
    [Fact]
    public async Task AReplicaWithoutACheckpointRecoversFromTheWal()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.CreateReplicaAsync(LedgerReplicaTestData.Ledger, TestContext.Current.CancellationToken);
        await store.CreateReplicaAsync(new(9), TestContext.Current.CancellationToken);
        await WriteRangeAsync(Replica(store, new(9)), new(9), 0, 3);
        var nodeId = store.NodeId;
        await root.CrashAsync(store, operatingSystem: true);
        Assert.Empty(await ListSlotsAsync(root));

        await using var reopened = await root.OpenAsync();
        Assert.Equal(nodeId, reopened.NodeId);
        Assert.Equal(LedgerReplicaTestData.Ledgers(LedgerReplicaTestData.Ledger.Value, 9), reopened.Replicas);
        var replica = Replica(reopened, LedgerReplicaTestData.Ledger);
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 0, TestContext.Current.CancellationToken));
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(TestContext.Current.CancellationToken));
        await AssertRangeAsync(Replica(reopened, new(9)), new(9), 0, 3);
        await WriteRangeAsync(replica, LedgerReplicaTestData.Ledger, 0, 2);
        await AssertRangeAsync(replica, LedgerReplicaTestData.Ledger, 0, 2);
    }

    /// <summary>
    /// LS-I6: a replica's checkpoint directory follows its durable create record, so a directory whose handle the WAL
    /// never assigned shows the WAL lost a create; the open refuses and leaves it in place.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(5u)]
    public async Task ACheckpointDirectoryWhoseHandleTheWalNeverAssignedIsCorrupt(uint handle)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await store.CreateReplicaAsync(LedgerReplicaTestData.Ledger, TestContext.Current.CancellationToken);
        Assert.Equal(1u, store.Catalog.NextHandle);
        await root.CrashAsync(store, operatingSystem: true);

        // Handle 1 is the next handle itself, the first the WAL never assigned.
        var name = handle.ToString(CultureInfo.InvariantCulture);
        await root.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, handle), TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync(create: true));
        Assert.Equal([name], await ListSlotsAsync(root));
    }

    /// <summary>
    /// Conventions.md §14: a create or drop record whose write fails is reported, which stops the process; the creation or
    /// drop fails without an answer, and so does every later operation which reaches the stopped storage, and every
    /// operation which meets the admission the failed drop closed. The restarted store holds the record whole or absent,
    /// never in between.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedCreateOrDropRecordIsReportedAndTheRestartedStoreHoldsItWholeOrAbsent(bool drop)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var kept = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(kept, new(1), 0, 3);
        var reported = FatalErrors.ReportedCount;
        root.Storage.WriteFault = static (_, _, _, _) => new IOException("injected WAL failure");
        if (drop)
        {
            var failure = await Assert.ThrowsAnyAsync<IOException>(async () => await store.DropReplicaAsync(new(1), cancellationToken));

            // The replica found its replica before the record failed, and a drop whose record failed answers its failure, not
            // the drop (LS-I2, conventions.md §13).
            Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(async () => await kept.ReadAsync(Slot.Zero, cancellationToken)));
        }
        else
        {
            _ = await Assert.ThrowsAnyAsync<IOException>(async () => await store.CreateReplicaAsync(new(2), cancellationToken));
        }

        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleWriteGather, WalPath);
        _ = await Assert.ThrowsAnyAsync<IOException>(async () => await store.CreateReplicaAsync(new(3), cancellationToken));
        _ = await Assert.ThrowsAnyAsync<IOException>(() => LedgerStoreReplicaTests.WriteAsync(kept, 3, [1]));

        await root.CrashAsync(store, operatingSystem: false);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), reopened.Replicas);
        Assert.Equal(1u, reopened.Catalog.NextHandle);
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 3);
        if (drop)
        {
            await reopened.DropReplicaAsync(new(1), cancellationToken);
            Assert.Empty(reopened.Replicas);
        }
        else
        {
            Assert.Equal(1u, (await reopened.CreateLiveAsync(new(2), Window.WindowSize, cancellationToken))!.Handle);
        }
    }

    /// <summary>
    /// LS-I2, conventions.md §13 and §14: a drop which closed its replica's admission and fails with a defect before its
    /// record reaches the WAL leaves the replica stopped while the catalog lists it, so the defect is reported once, as
    /// <c>LedgerStoreDrop</c>, which stops the process, before any operation which met the closed
    /// admission answers. The drop, and a read which met its closed admission, answer the defect, never dropped; the
    /// stopped store answers nothing more; and the restarted store keeps the replica with every acknowledged write.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADropWhichFailsWithADefectIsReportedOnceAndReadsAnswerTheFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var replica = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(replica, new(1), 0, 3);
        var defect = new InvalidOperationException("injected drop defect");
        Task<ReplicaRead>? read = null;
        Task? answered = null;
        var readAnsweredEarly = true;
        var reportedAtAnswer = -1L;
        store.StoreRecordFault = record =>
        {
            if (record is not DropRecord)
            {
                return null;
            }

            // The drop has closed admission, so a read now waits for its outcome (conventions.md §13).
            read = replica.ReadAsync(Slot.Zero, cancellationToken).AsTask();
            readAnsweredEarly = read.IsCompleted;
            answered = read.ContinueWith(
                _ => reportedAtAnswer = FatalErrors.ReportedCount,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return defect;
        };
        var reported = FatalErrors.ReportedCount;

        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.DropReplicaAsync(new(1), cancellationToken)));
        Assert.NotNull(read);
        Assert.False(readAnsweredEarly, "A read which met the closed admission answered before the drop's outcome.");
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => read));
        await answered!;
        Assert.Equal(reported + 1, reportedAtAnswer);
        _ = StorageFailures.TakeDefect(reported, "LedgerStoreDrop", defect);
        await StoppedStore.AssertAnswersNothingAsync(store, replica, defect);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.DisposeAsync()));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), reopened.Replicas);
        Assert.Empty(reopened.Catalog.Tombstones);
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 3);
    }

    /// <summary>
    /// LS-I1, conventions.md §14: a durable create record whose publication throws leaves the catalog behind the WAL, so
    /// the store reports the defect once, as <c>LedgerStoreRecordPublication</c>, which stops the
    /// process, before the creation answers; the WAL finds it reported and reports no second failure. The creation answers
    /// the defect, the stopped store answers nothing more, and the restarted store lists the replica the durable record
    /// created.
    /// </summary>
    [Fact]
    public async Task ACreateRecordWhosePublicationThrowsIsReportedOnceBeforeTheCreationAnswers()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var replica = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(replica, new(1), 0, 2);
        var defect = new InvalidOperationException("injected create publication defect");
        store.StoreRecordPublicationFault = record => record is CreateRecord ? defect : null;
        var reported = FatalErrors.ReportedCount;
        var reportedAtAnswer = -1L;
        var creation = CreateAsync(store, new(2));
        var answered = creation.ContinueWith(
            _ => reportedAtAnswer = FatalErrors.ReportedCount,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => creation));
        await answered;
        Assert.Equal(reported + 1, reportedAtAnswer);
        _ = StorageFailures.TakeDefect(reported, "LedgerStoreRecordPublication", defect);
        await StoppedStore.AssertAnswersNothingAsync(store, replica, defect);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.DisposeAsync()));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1, 2), reopened.Replicas);
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 2);
    }

    /// <summary>
    /// LS-I6: the node file holds the node identity as 32 lowercase hexadecimal digits, created with the store and kept
    /// across restarts.
    /// </summary>
    [Fact]
    public async Task TheNodeFileHoldsTheNodeIdentity()
    {
        var root = NewRoot();
        NodeId nodeId;
        await using (var store = await root.OpenAsync(create: true))
        {
            nodeId = store.NodeId;
            await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
        }

        var content = root.Storage.ReadDurableFile(WalPath, NodeFile.Name);
        Assert.Equal(nodeId.Value.ToString("N", CultureInfo.InvariantCulture), System.Text.Encoding.ASCII.GetString(content!));
        await using var reopened = await root.OpenAsync();
        Assert.Equal(nodeId, reopened.NodeId);
    }

    /// <summary>
    /// LS-I6, §7.1: a damaged node file cannot establish the node identity, so the open refuses and keeps the root.
    /// </summary>
    [Fact]
    public async Task ADamagedNodeFileRefusesToStart()
    {
        var root = NewRoot();
        await using (var store = await root.OpenAsync(create: true))
        {
            await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
        }

        root.Storage.DamageFile(WalPath, NodeFile.Name, 0, [(byte)'g']);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync(create: true));
        Assert.Contains(NodeFile.Name, root.Storage.ListNamedFiles(WalPath));
    }

    /// <summary>
    /// What a root without a node file holds beyond an interrupted creation.
    /// </summary>
    public enum Leftover
    {
        /// <summary>The WAL holds a create record and writes.</summary>
        Record,

        /// <summary>The only catalog checkpoint holds a tombstone.</summary>
        CatalogCheckpointOfATombstone,

        /// <summary>A second catalog checkpoint, of the empty catalog.</summary>
        SecondCatalogCheckpoint,

        /// <summary>A replica's checkpoint directory.</summary>
        CheckpointDirectory,

        /// <summary>The catalog checkpoint, with every WAL file deleted.</summary>
        CatalogCheckpointWithoutWalFiles,

        /// <summary>The only catalog checkpoint fails validation.</summary>
        DamagedCatalogCheckpoint,

        /// <summary>The only catalog checkpoint, of the empty catalog, has a cut past every WAL file.</summary>
        CatalogCheckpointPastTheWal,
    }

    /// <summary>
    /// LS-I6, §8.7 step 3: the store publishes its node file last, after WAL file 1 and the empty catalog checkpoint, so
    /// a creation which fails before it, at the checkpoint or at the node file itself, is reported and stops the process
    /// (conventions.md §14), and fails again in the restarted process, leaves no store: an open without create finds none,
    /// and one with create completes the creation, with at most the one empty catalog checkpoint, and the store it
    /// completes keeps its identity.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInterruptedCreationIsCompletedByTheNextOpenWithCreate(bool checkpointWritten)
    {
        var root = NewRoot();
        var failing = checkpointWritten ? (Func<string, bool>)(static name => name == NodeFile.Name) : IsCatalogCheckpoint;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reported = FatalErrors.ReportedCount;
            root.Storage.NamedWriteFault = (_, name, _, _) => failing(name) ? new IOException("The disk is full.") : null;
            _ = await Assert.ThrowsAnyAsync<IOException>(async () => await root.OpenAsync(create: true));
            _ = StorageFailures.TakeOne(reported, FatalErrorSite.CreateFile, WalPath);
            root.Crash(operatingSystem: false);
            Assert.DoesNotContain(NodeFile.Name, root.Storage.ListNamedFiles(WalPath));
            Assert.Equal(checkpointWritten ? 1 : 0, root.Storage.ListNamedFiles(WalPath).Count(IsCatalogCheckpoint));
            Assert.NotEmpty(root.Storage.ListFiles(WalPath));
            _ = await Assert.ThrowsAsync<WalNotFoundException>(async () => await root.OpenAsync());
        }

        NodeId nodeId;
        await using (var store = await root.OpenAsync(create: true))
        {
            nodeId = store.NodeId;
            Assert.True(store.Catalog.IsEmpty);
            Assert.Single(root.Storage.ListNamedFiles(WalPath), IsCatalogCheckpoint);
            Assert.Contains(NodeFile.Name, root.Storage.ListNamedFiles(WalPath));
            await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
        }

        await using var reopened = await root.OpenAsync();
        Assert.Equal(nodeId, reopened.NodeId);
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), reopened.Replicas);
    }

    /// <summary>
    /// LS-I6: without a node file, a root holding more than an interrupted creation leaves shows a lost identity, so the
    /// open refuses and starts no new node over it, deleting nothing and leaving no handle open: a WAL which holds a
    /// record, a catalog checkpoint of a catalog other than the empty one, an invalid one, one whose cut lies past the WAL,
    /// a second catalog checkpoint, a checkpoint directory, or catalog checkpoints without WAL files. The record and the
    /// checkpoint's contents and cut show only to an open which may complete the creation; an open without create finds no
    /// store.
    /// </summary>
    [Theory]
    [InlineData(Leftover.Record)]
    [InlineData(Leftover.CatalogCheckpointOfATombstone)]
    [InlineData(Leftover.SecondCatalogCheckpoint)]
    [InlineData(Leftover.CheckpointDirectory)]
    [InlineData(Leftover.CatalogCheckpointWithoutWalFiles)]
    [InlineData(Leftover.DamagedCatalogCheckpoint)]
    [InlineData(Leftover.CatalogCheckpointPastTheWal)]
    public async Task MoreThanAnInterruptedCreationLeavesWithoutANodeFileRefusesToStart(Leftover leftover)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        if (leftover == Leftover.Record)
        {
            await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
        }

        await root.CrashAsync(store, operatingSystem: false);
        var cut = root.Storage.ListNamedFiles(WalPath).Where(IsCatalogCheckpoint).Select(static name => CatalogCheckpoints.TryParseFileName(name, out var cut) ? cut : default).Single();
        await using (var directory = await root.Storage.OpenDirectoryAsync(WalPath, cancellationToken))
        {
            await directory.DeleteNamedFileAsync(NodeFile.Name, cancellationToken);
            switch (leftover)
            {
                case Leftover.CatalogCheckpointOfATombstone:
                    await directory.DeleteNamedFileAsync(CatalogCheckpoints.FileName(cut), cancellationToken);
                    _ = await new CatalogCheckpoints(directory, [], firstRetainedFile: 1).WriteAsync(new ReplicaCatalog(0, [], [new LedgerId(5)]), cut, cancellationToken);
                    break;
                case Leftover.SecondCatalogCheckpoint:
                    _ = await new CatalogCheckpoints(directory, [], firstRetainedFile: 1).WriteAsync(ReplicaCatalog.Empty, new WalPosition(1, 0), cancellationToken);
                    break;
                case Leftover.CheckpointDirectory:
                    await root.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 0), cancellationToken);
                    break;
                case Leftover.CatalogCheckpointWithoutWalFiles:
                    foreach (var file in root.Storage.ListFiles(WalPath))
                    {
                        await directory.DeleteFileAsync(file, cancellationToken);
                    }

                    break;
                case Leftover.DamagedCatalogCheckpoint:
                    var crc = root.Storage.ReadDurableFile(WalPath, CatalogCheckpoints.FileName(cut)).AsSpan(ReplicaCatalog.BodyCrcOffset, 4).ToArray();
                    root.Storage.DamageFile(WalPath, CatalogCheckpoints.FileName(cut), ReplicaCatalog.BodyCrcOffset, [.. crc.Select(static value => (byte)(value ^ 0xFF))]);
                    break;
                case Leftover.CatalogCheckpointPastTheWal:
                    await directory.DeleteNamedFileAsync(CatalogCheckpoints.FileName(cut), cancellationToken);
                    var past = new WalPosition(root.Storage.ListFiles(WalPath)[^1] + 1, cut.Offset);
                    _ = await new CatalogCheckpoints(directory, [], firstRetainedFile: 1).WriteAsync(ReplicaCatalog.Empty, past, cancellationToken);
                    break;
                case Leftover.Record:
                default:
                    break;
            }
        }

        var files = root.Storage.ListFiles(WalPath);
        var named = root.Storage.ListNamedFiles(WalPath);
        var showsWithoutCreate = leftover is not (Leftover.Record or Leftover.CatalogCheckpointOfATombstone or Leftover.DamagedCatalogCheckpoint or Leftover.CatalogCheckpointPastTheWal);
        if (showsWithoutCreate)
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        }
        else
        {
            _ = await Assert.ThrowsAsync<WalNotFoundException>(async () => await root.OpenAsync());
        }

        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync(create: true));
        Assert.Subset(root.Storage.ListFiles(WalPath).ToHashSet(), files.ToHashSet());
        Assert.Equal(named, root.Storage.ListNamedFiles(WalPath));
        Assert.Equal(0, root.Storage.OpenHandleCount);
    }

    /// <summary>
    /// LS-I6: catalog checkpoints or checkpoint directories without WAL files show the WAL was lost, so the open refuses,
    /// with or without create, and keeps them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogCheckpointsOrCheckpointDirectoriesWithoutWalFilesRefuseToStart(bool catalogCheckpoints)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        await using (var store = await root.OpenAsync(create: true))
        {
            var replica = await CreateAndOpenReplicaAsync(store, new(1));
            await WriteRangeAsync(replica, new(1), 0, 2);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        }

        await using (var directory = await root.Storage.OpenDirectoryAsync(WalPath, cancellationToken))
        {
            foreach (var file in root.Storage.ListFiles(WalPath))
            {
                await directory.DeleteFileAsync(file, cancellationToken);
            }

            if (!catalogCheckpoints)
            {
                foreach (var name in root.Storage.ListNamedFiles(WalPath).Where(static name => name.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal)))
                {
                    await directory.DeleteNamedFileAsync(name, cancellationToken);
                }
            }
            else
            {
                await root.Storage.DeleteDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 0), cancellationToken);
            }
        }

        var named = root.Storage.ListNamedFiles(WalPath);
        var slots = await ListSlotsAsync(root);
        Assert.Equal(catalogCheckpoints, slots.Count == 0);
        foreach (var create in new[] { false, true })
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync(create));
        }

        Assert.Equal(named, root.Storage.ListNamedFiles(WalPath));
        Assert.Equal(slots, await ListSlotsAsync(root));
    }

    /// <summary>
    /// LS-I6: the node file follows WAL file 1, so a node file without WAL files shows a lost WAL: the open refuses, with
    /// or without create, and keeps the node file.
    /// </summary>
    [Fact]
    public async Task ANodeFileWithoutWalFilesRefusesToStart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var started = NewRoot();
        await started.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path), cancellationToken);
        await started.Storage.CreateDirectoryAsync(WalPath, cancellationToken);
        Assert.True(await started.Claim.CreateIfAbsentAsync(LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster), cancellationToken));
        await using (var wal = await started.Storage.OpenDirectoryAsync(WalPath, cancellationToken))
        {
            await NodeFile.CreateAsync(wal, LedgerReplicaTestData.Node, cancellationToken);
        }

        foreach (var create in new[] { false, true })
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await started.OpenAsync(create));
        }

        Assert.Equal([NodeFile.Name], started.Storage.ListNamedFiles(WalPath));
    }

    /// <summary>
    /// LS-I1, §8.5, LR-I7: a replica admits no write before its create record is durable: a first write which creates it
    /// waits for the create, and the writes are issued and answered only once the create is durable. A read and a
    /// contiguous-tail read meanwhile are concurrent with the create and answer at once as an empty, unsealed replica.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AReplicaAdmitsNoWriteBeforeItsCreateIsDurableWhileReadsSeeItAbsent()
    {
        var token = TestContext.Current.CancellationToken;
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        var replica = store.OpenReplica(new(2), Window);
        root.Storage.HoldWrites = true;
        var write = LedgerStoreReplicaTests.WriteAsync(replica, 0, Payload(new(2), 0));
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the create record is in flight");
        var second = LedgerStoreReplicaTests.WriteAsync(replica, 1, Payload(new(2), 1));
        await AssertReadsSeeAnAbsentReplicaAsync(replica, token);
        Assert.Empty(store.Replicas);
        Assert.False(write.IsCompleted || second.IsCompleted);
        Assert.Equal(1, root.Storage.PendingWriteCount);

        Assert.True(root.Storage.CompleteWrite());
        await EventuallyAsync(() => store.Replicas.Count == 1 && root.Storage.PendingWriteCount != 0, "the writes follow the create record");
        root.Storage.HoldWrites = false;
        await EventuallyAsync(
            () =>
            {
                _ = root.Storage.CompleteWrite();
                return write.IsCompleted && second.IsCompleted;
            },
            "the writes complete");
        Assert.Equal(ReplicaWriteCase.Written, (await write).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await second).Case);
        await AssertRangeAsync(replica, new(2), 0, 2);
    }

    /// <summary>
    /// LS-I1, §8.3, LR-I7, LR-I8: a seal of a replica the node does not hold creates it, and answers sealed only once
    /// its create and seal records are durable. A read and a contiguous-tail read meanwhile are concurrent with the
    /// create and answer at once as an empty, unsealed replica.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASealWhichCreatesAReplicaAnswersOnceItsCreateIsDurableWhileReadsSeeItAbsent()
    {
        var token = TestContext.Current.CancellationToken;
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        var replica = store.OpenReplica(new(2), Window);
        root.Storage.HoldWrites = true;
        var seal = replica.ReadTailAsync(Slot.Zero, seal: true, token).AsTask();
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the create record is in flight");
        await AssertReadsSeeAnAbsentReplicaAsync(replica, token);
        Assert.Empty(store.Replicas);
        Assert.False(seal.IsCompleted);
        Assert.Equal(1, root.Storage.PendingWriteCount);

        Assert.True(root.Storage.CompleteWrite());
        root.Storage.HoldWrites = false;
        await EventuallyAsync(
            () =>
            {
                _ = root.Storage.CompleteWrite();
                return seal.IsCompleted;
            },
            "the seal completes");
        var sealedTail = (await seal).Tail;
        Assert.Equal((Slot.Zero, true), (sealedTail.ContiguousTail, sealedTail.IsSealed));
        Assert.Single(store.Replicas);
        Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(replica, 0, Payload(new(2), 0)));
    }

    /// <summary>
    /// A read and a contiguous-tail read of a replica whose create is in flight complete without waiting for it and answer
    /// as an empty, unsealed replica, the tail read with its argument (LR-I7).
    /// </summary>
    private static async Task AssertReadsSeeAnAbsentReplicaAsync(ILedgerReplica replica, CancellationToken token)
    {
        var read = replica.ReadAsync(Slot.Zero, token);
        var tail = replica.ReadTailAsync(new Slot(1), seal: false, token);
        Assert.True(read.IsCompleted, "A read concurrent with the create answers at once.");
        Assert.True(tail.IsCompleted, "A contiguous-tail read concurrent with the create answers at once.");
        Assert.Equal(ReplicaReadCase.Missing, (await read).Case);
        Assert.Equal(new LedgerReplicaTail(new Slot(1), IsSealed: false), await tail);
    }

    /// <summary>
    /// ledgerstore.md §6.1: a create or drop record whose bytes reached the page cache when the process crashed survives
    /// whole: the restarted process recovers it and makes it durable before it serves, so a later operating-system crash
    /// keeps it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACreateOrDropRecordInThePageCacheAtAProcessCrashSurvivesWhole(bool drop)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 3);
        root.Storage.HoldWrites = true;
        var record = drop ? DropAsync(store, new(1)) : CreateAsync(store, new(2));
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the record is in the page cache");
        await root.CrashAsync(store, operatingSystem: false);
        _ = await Assert.ThrowsAnyAsync<Exception>(() => record);

        for (var crash = 0; crash < 2; crash++)
        {
            await using var reopened = await root.OpenAsync();
            Assert.Equal(drop ? [] : LedgerReplicaTestData.Ledgers(1, 2), reopened.Replicas);
            Assert.Equal(drop, reopened.Catalog.IsDropped(new(1)));
            await root.CrashAsync(reopened, operatingSystem: true);
        }
    }

    /// <summary>
    /// §8.5: every creation waiting at the catalog gate completes when the store's WAL fails or the store is disposed,
    /// and none is left waiting: the one whose record failed with the failure, and those behind it with the WAL's
    /// failure or the disposal.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryWaitingCreationCompletesWhenTheStoreFailsOrIsDisposed(bool dispose)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        root.Storage.HoldWrites = true;
        var first = CreateAsync(store, new(1));
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the first create record is in flight");
        var waiting = LedgerReplicaTestData.Ledgers(2, 3, 4).Select(ledger => CreateAsync(store, ledger)).ToArray();
        if (dispose)
        {
            var disposal = store.DisposeAsync().AsTask();
            root.Storage.HoldWrites = false;
            await EventuallyAsync(
                () =>
                {
                    _ = root.Storage.CompleteWrite();
                    return disposal.IsCompleted;
                },
                "the store is disposed");
            await disposal;
        }
        else
        {
            await root.CrashAsync(store, operatingSystem: false);
        }

        foreach (var creation in waiting.Append(first))
        {
            try
            {
                _ = await creation;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The WAL failed or the store was disposed before the creation's record was durable.
            }
        }

        if (!dispose)
        {
            Assert.All(waiting.Append(first), static creation => Assert.True(creation.IsFaulted));
        }
    }

    /// <summary>
    /// §8.5, §8.6: a creation or a drop cancelled while it waits at the catalog gate writes nothing; one the gate has taken
    /// runs to its outcome.
    /// </summary>
    [Fact]
    public async Task ACreationOrDropCancelledWhileItWaitsWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        root.Storage.HoldWrites = true;
        var first = CreateAsync(store, new(1));
        await EventuallyAsync(() => root.Storage.PendingWriteCount == 1, "the first create record is in flight");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var creation = CreateAsync(store, new(2), cancellation.Token);
        var drop = store.DropReplicaAsync(new(3), cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drop);

        root.Storage.HoldWrites = false;
        await EventuallyAsync(
            () =>
            {
                _ = root.Storage.CompleteWrite();
                return first.IsCompleted;
            },
            "the first creation completes");
        _ = await first;
        await EventuallyAsync(() => store.MaintenanceIdle, "maintenance settles");
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), store.Replicas);
        Assert.Equal(1u, store.Catalog.NextHandle);
        Assert.Empty(store.Catalog.Tombstones);
    }

    /// <summary>
    /// §8.7: the open deletes the checkpoint directories no live replica owns only once its checks pass, so an open which
    /// refuses deletes none, even one whose replica the replay dropped.
    /// </summary>
    [Fact]
    public async Task CheckpointDirectoriesAreDeletedOnlyOnceTheOpenPassesItsChecks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
        var dropped = await CreateAndOpenReplicaAsync(store, new(2));
        await WriteRangeAsync(dropped, new(2), 0, 2);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(dropped);
        await DropAsync(store, new(2));
        await root.CrashAsync(store, operatingSystem: true);

        // The replay drops the replica whose directory a crash before maintenance's deletion leaves.
        await root.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 1), cancellationToken);
        await root.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 7), cancellationToken);
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        Assert.Equal(["1", "7"], await ListSlotsAsync(root));

        await root.Storage.DeleteDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 7), cancellationToken);
        await using var reopened = await root.OpenAsync();
        Assert.Empty(await ListSlotsAsync(root));
        await AssertRefusedAsync(reopened.OpenReplica(new(2), Window));
    }

    /// <summary>
    /// §8.7: a dropped replica's directory whose deletion answers that its lock is held, as when a scanner's handle on its
    /// lock file outlasts the contention bound on Windows, stays for a later pass: maintenance reports nothing and keeps
    /// running, and a pass after the lock is released deletes the directory (conventions.md §14, wal.md §9).
    /// </summary>
    [Fact]
    public async Task ADroppedDirectoryWhoseDeletionAnswersHeldIsDeletedByALaterPass()
    {
        var root = NewRoot();
        var slots = LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 0);
        var storage = new HeldDeletionStorage(root.Storage, slots, releasedAfterFirst: false);
        var time = new FakeTimeProvider();
        var since = FatalErrors.ReportedCount;
        await using var store = await LedgerStore.OpenAsync(
            storage, root.Options, LedgerReplicaTestData.Cluster, create: true, root.CheckpointOptions, time, root.Claim, TestContext.Current.CancellationToken);
        var dropped = await CreateAndOpenReplicaAsync(store, new(2));
        await WriteRangeAsync(dropped, new(2), 0, 2);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(dropped);
        Assert.Equal(["0"], await ListSlotsAsync(root));

        // A drop wakes maintenance as it publishes its record and again as it hands over the directory, so a pass which met
        // the held answer may be followed at once by another, which meets it too until the lock is released (#181).
        await DropAsync(store, new(2));
        await EventuallyAsync(() => storage.Deletions != 0 && store.MaintenanceIdle, "maintenance meets the held answer and waits");
        Assert.Equal(["0"], await ListSlotsAsync(root));
        Assert.Empty(FatalErrors.TakeMine(since));

        storage.Release();
        var attempts = storage.Deletions;
        time.Advance(root.CheckpointOptions.CheckpointInterval);
        await EventuallyAsync(() => storage.Deletions > attempts && store.MaintenanceIdle, "a later pass deletes the directory");
        Assert.Empty(await ListSlotsAsync(root));
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>
    /// §8.7: an open whose deletion of a dropped replica's directory answers that its lock is held, as when a scanner's
    /// handle on its lock file outlasts the contention bound on Windows, opens all the same, reporting nothing, and
    /// leaves the directory to maintenance, whose first pass deletes it.
    /// </summary>
    [Fact]
    public async Task AnOpenLeavesADirectoryWhoseDeletionAnswersHeldToMaintenance()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var kept = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(kept, new(1), 0, 2);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(kept);
        await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(2)), new(2), 0, 2);
        await DropAsync(store, new(2));
        await root.CrashAsync(store, operatingSystem: true);
        await root.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 1), TestContext.Current.CancellationToken);
        var storage = new HeldDeletionStorage(root.Storage, LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 1), releasedAfterFirst: true);
        var since = FatalErrors.ReportedCount;

        await using var reopened = await LedgerStore.OpenAsync(
            storage, root.Options, LedgerReplicaTestData.Cluster, create: false, root.CheckpointOptions, new FakeTimeProvider(), root.Claim, TestContext.Current.CancellationToken);

        await EventuallyAsync(() => storage.Deletions == 2 && reopened.MaintenanceIdle, "maintenance deletes the directory the open left");
        Assert.Equal(["0"], await ListSlotsAsync(root));
        Assert.Empty(FatalErrors.TakeMine(since));
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 2);
    }

    /// <summary>
    /// A drop's checkpoint directory is deleted after its drop record, so a crash between them leaves it; the next open
    /// deletes it and keeps the live replica's.
    /// </summary>
    [Fact]
    public async Task CheckpointDirectoriesOfDroppedReplicasAreDeletedOnOpen()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var kept = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(kept, new(1), 0, 2);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(kept);
        await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(2)), new(2), 0, 2);
        await DropAsync(store, new(2));
        await root.CrashAsync(store, operatingSystem: true);

        await root.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 1), TestContext.Current.CancellationToken);
        Assert.Equal(["0", "1"], await ListSlotsAsync(root));
        await using var reopened = await root.OpenAsync();
        Assert.Equal(["0"], await ListSlotsAsync(root));
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 2);
        await AssertRefusedAsync(reopened.OpenReplica(new(2), Window));
    }

    /// <summary>
    /// LS-I5: damage in file F faults every live ledger whose first file is at or below F, and no other, when it lies
    /// below the catalog checkpoint's cut.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamageFaultsOnlyTheLedgersWhoseFirstFileIsAtOrBelowIt(bool damageTheLaterFile)
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var early = await CreateAndOpenReplicaAsync(store, new(1));
        await WriteRangeAsync(early, new(1), 0, 20);
        var late = await CreateAndOpenReplicaAsync(store, new(2));
        var lateFile = store.FindLive(new(2))!.Entry.FirstFile;
        await WriteRangeAsync(late, new(2), 0, 20);
        await WriteRangeAsync(early, new(1), 20, 10);

        // A catalog checkpoint after the writes moves its cut past them.
        await store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(store, operatingSystem: false);

        // Every damaged page lies well before the crash region recovery finalizes, so it is damage, not a torn write.
        var file = damageTheLaterFile ? lateFile : 1;
        root.Storage.Damage(WalPath, file, 2 * SmallFiles.PageSize + 40, [1, 2, 3]);

        await using var reopened = await root.OpenAsync();
        var reopenedEarly = Replica(reopened, new(1));
        var reopenedLate = Replica(reopened, new(2));
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopenedEarly.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken));
        if (damageTheLaterFile)
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopenedLate.ReadAsync(new Slot(20), TestContext.Current.CancellationToken));
        }
        else
        {
            await AssertRangeAsync(reopenedLate, new(2), 0, 20);
            Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopenedLate, 20, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task EachLedgersCheckpointShortensOnlyItsOwnReplay()
    {
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        var checkpointed = await CreateAndOpenReplicaAsync(store, new(1));
        var scanned = await CreateAndOpenReplicaAsync(store, new(2));
        await WriteRangeAsync(checkpointed, new(1), 0, 30);
        await WriteRangeAsync(scanned, new(2), 0, 30);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(checkpointed);
        await WriteRangeAsync(checkpointed, new(1), 30, 5);
        await WriteRangeAsync(scanned, new(2), 30, 5);

        await root.CrashAsync(store, operatingSystem: true);
        store = await root.OpenAsync();
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.Checkpoint, 1, 5), store.FindLive(new(1))!.Recovery);
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.FullScan, 0, 35), store.FindLive(new(2))!.Recovery);
        await AssertRangeAsync(Replica(store, new(1)), new(1), 0, 35);
        await AssertRangeAsync(Replica(store, new(2)), new(2), 0, 35);

        // A clean shutdown checkpoints every ledger, so the next recovery replays nothing.
        await store.DisposeAsync();
        await using var reopened = await root.OpenAsync();
        Assert.All(reopened.Replicas, ledger => Assert.Equal(0, reopened.FindLive(ledger)!.Recovery.ReplayedRecords));
        await AssertRangeAsync(Replica(reopened, new(2)), new(2), 0, 35);
    }

    [Fact]
    public async Task SlotsBeyondTheKeySpaceAreRejected()
    {
        var root = NewRoot();
        await using var store = await root.OpenAsync(create: true);
        var replica = await CreateAndOpenReplicaAsync(store, LedgerReplicaTestData.Ledger);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LedgerStoreReplicaTests.WriteAsync(replica, LedgerRecords.SlotLimit, [1]));
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, LedgerRecords.SlotLimit - 1, [1])).Case);
        Assert.Equal((((ulong)LedgerRecords.StoreHandle - 1) << 40) | 5, LedgerRecords.Key(LedgerRecords.StoreHandle - 1, 5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LedgerRecords.Key(LedgerRecords.StoreHandle, 0));
    }

    /// <summary>
    /// LS-I12: a create publishes the root's claim, the cluster as 32 lowercase hexadecimal digits, before it creates the
    /// store, so a create whose claim cannot be published leaves no store; an open without create of an empty root
    /// publishes nothing; another cluster is refused and the claim is never replaced.
    /// </summary>
    [Fact]
    public async Task TheClusterClaimIsPublishedBeforeTheStoreAndAnotherClusterIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var claim = Path.Combine(directory.Path, "cluster");
        _ = await Assert.ThrowsAsync<WalNotFoundException>(async () => await LedgerStoreReplicaTests.OpenStoreAsync(directory.Path, create: false));
        Assert.False(File.Exists(claim));
        Assert.False(Directory.Exists(LedgerStore.WalPath(directory.Path)));

        NodeId nodeId;
        await using (var store = await LedgerStoreReplicaTests.OpenStoreAsync(directory.Path, create: true))
        {
            Assert.Equal(LedgerReplicaTestData.Cluster, store.Cluster);
            nodeId = store.NodeId;
            await store.CreateReplicaAsync(LedgerReplicaTestData.Ledger, cancellationToken);
        }

        var content = await File.ReadAllTextAsync(claim, cancellationToken);
        Assert.Equal(32, content.Length);
        Assert.All(content, static digit => Assert.True(char.IsAsciiHexDigitLower(digit)));
        Assert.Equal(LedgerReplicaTestData.Cluster.ToString("N", CultureInfo.InvariantCulture), content);

        foreach (var create in new[] { false, true })
        {
            var mismatch = await Assert.ThrowsAsync<LedgerStoreClusterMismatchException>(
                async () => await LedgerStoreReplicaTests.OpenStoreAsync(directory.Path, create, LedgerReplicaTestData.OtherCluster));
            Assert.Equal(content, mismatch.ClaimedCluster);
        }

        Assert.Equal(content, await File.ReadAllTextAsync(claim, cancellationToken));
        await using (var reopened = await LedgerStoreReplicaTests.OpenStoreAsync(directory.Path, create: false))
        {
            Assert.Equal(nodeId, reopened.NodeId);
            Assert.Equal([LedgerReplicaTestData.Ledger], reopened.Replicas);
        }

        var root = NewRoot();
        _ = await Assert.ThrowsAsync<IOException>(async () => await LedgerStore.OpenAsync(
            root.Storage, SmallFiles, LedgerReplicaTestData.Cluster, create: true, ExplicitOnly, new FakeTimeProvider(), new UnpublishableClaim(), cancellationToken));
        Assert.Empty(await root.Storage.ListDirectoriesAsync(SimulatedNodeRoot.Path, cancellationToken));
        await using var created = await root.OpenAsync(create: true);
        Assert.Equal(LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster), root.Claim.Content);
    }

    /// <summary>
    /// LS-I12, LS-I6: the claim precedes the store, so a root whose WAL directory holds a node file, catalog checkpoints, or
    /// WAL files without a claim is damage which neither an open nor a create repairs, and the refusal leaves no handle
    /// open; a root holding nothing has no store, and gains a claim only with a create (ledgerstore.md §8.7 step 1).
    /// </summary>
    [Fact]
    public async Task AStoreInARootWithoutAClaimIsCorrupt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var nothing = NewRoot();
        _ = await Assert.ThrowsAsync<WalNotFoundException>(async () => await nothing.OpenAsync());
        Assert.Null(nothing.Claim.Content);
        Assert.Empty(await nothing.Storage.ListDirectoriesAsync(SimulatedNodeRoot.Path, cancellationToken));

        // An empty WAL directory holds no store either.
        var emptyWal = NewRoot();
        await emptyWal.Storage.CreateDirectoryAsync(WalPath, cancellationToken);
        _ = await Assert.ThrowsAsync<WalNotFoundException>(async () => await emptyWal.OpenAsync());
        Assert.Null(emptyWal.Claim.Content);
        await (await emptyWal.OpenAsync(create: true)).DisposeAsync();
        Assert.Equal(LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster), emptyWal.Claim.Content);

        var root = NewRoot();
        await using (var store = await root.OpenAsync(create: true))
        {
            await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
        }

        var unclaimed = new SimulatedNodeRoot(SmallFiles, ExplicitOnly, root.Storage);
        foreach (var create in new[] { false, true })
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await unclaimed.OpenAsync(create));
        }

        Assert.Null(unclaimed.Claim.Content);
        Assert.NotEmpty(root.Storage.ListFiles(WalPath));
        Assert.Contains(NodeFile.Name, root.Storage.ListNamedFiles(WalPath));

        // A node file alone, which precedes the WAL's files, is a store too.
        var nodeOnly = NewRoot();
        await nodeOnly.Storage.CreateDirectoryAsync(WalPath, cancellationToken);
        await using (var wal = await nodeOnly.Storage.OpenDirectoryAsync(WalPath, cancellationToken))
        {
            await NodeFile.CreateAsync(wal, LedgerReplicaTestData.Node, cancellationToken);
        }

        foreach (var create in new[] { false, true })
        {
            _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await nodeOnly.OpenAsync(create));
        }

        Assert.Null(nodeOnly.Claim.Content);

        // WAL files alone, or a catalog checkpoint alone, are a store too, whatever else the root lacks.
        foreach (var keepFiles in new[] { true, false })
        {
            var partial = NewRoot();
            await using (var store = await partial.OpenAsync(create: true))
            {
                await WriteRangeAsync(await CreateAndOpenReplicaAsync(store, new(1)), new(1), 0, 2);
            }

            await using (var wal = await partial.Storage.OpenDirectoryAsync(WalPath, cancellationToken))
            {
                foreach (var name in partial.Storage.ListNamedFiles(WalPath).Where(name => keepFiles || name == NodeFile.Name))
                {
                    await wal.DeleteNamedFileAsync(name, cancellationToken);
                }

                foreach (var file in keepFiles ? [] : partial.Storage.ListFiles(WalPath))
                {
                    await wal.DeleteFileAsync(file, cancellationToken);
                }
            }

            var unclaimedPartial = new SimulatedNodeRoot(SmallFiles, ExplicitOnly, partial.Storage);
            foreach (var create in new[] { false, true })
            {
                _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await unclaimedPartial.OpenAsync(create));
            }

            Assert.Null(unclaimedPartial.Claim.Content);
            Assert.Equal(0, partial.Storage.OpenHandleCount);
        }

        // §8.7 step 1: only the WAL directory's contents show a store, so a root holding an empty one and checkpoint
        // directories holds none without create; with create, step 3 refuses the checkpoint directories, and publishes
        // no claim.
        var directoriesOnly = NewRoot();
        await directoriesOnly.Storage.CreateDirectoryAsync(WalPath, cancellationToken);
        await directoriesOnly.Storage.CreateDirectoryAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 0), cancellationToken);
        _ = await Assert.ThrowsAsync<WalNotFoundException>(async () => await directoriesOnly.OpenAsync());
        _ = await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await directoriesOnly.OpenAsync(create: true));
        Assert.Null(directoriesOnly.Claim.Content);
        Assert.Equal(0, directoriesOnly.Storage.OpenHandleCount);

        await using var reopened = await root.OpenAsync();
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 2);
    }

    /// <summary>
    /// §2.1, §2.2: the store checks a request before it reaches a replica: an address the contract excludes is refused
    /// for a ledger the store does not hold, a cancelled listing refuses, and once the store is disposed a read of a
    /// ledger it does not hold refuses rather than answering as empty; a replica the store creates live records that it
    /// was created rather than recovered.
    /// </summary>
    [Fact]
    public async Task TheStoreChecksARequestBeforeItReachesAReplica()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () => await LedgerStore.OpenAsync(
            root.Storage, null!, LedgerReplicaTestData.Cluster, create: true, ExplicitOnly, TimeProvider.System, cancellationToken));
        var store = await root.OpenAsync(create: true);
        var absent = Replica(store, new(7));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await absent.ReadAsync(new Slot(long.MaxValue), cancellationToken));
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.ListReplicasAsync(cancelled.Token));
        }

        _ = await CreateAsync(store, new(1));
        Assert.Equal(LedgerStoreReplayPath.Created, store.FindLive(new(1))!.Recovery.Path);
        await store.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await absent.ReadAsync(Slot.Zero, cancellationToken));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.CreateReplicaAsync(new(8), cancellationToken));
    }

    /// <summary>
    /// §7.3, §8.5: handles run below the reserved store handle, so once the catalog has assigned every other one, a
    /// creation is refused and writes nothing, and the store keeps serving the replicas it holds, across a restart.
    /// </summary>
    [Fact]
    public async Task ACreationIsRefusedOnceEveryHandleIsAssigned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        await root.CrashAsync(store, operatingSystem: false);
        var name = Assert.Single(root.Storage.ListNamedFiles(WalPath), IsCatalogCheckpoint);
        _ = CatalogCheckpoints.TryParseFileName(name, out var cut);
        await using (var directory = await root.Storage.OpenDirectoryAsync(WalPath, cancellationToken))
        {
            await directory.DeleteNamedFileAsync(name, cancellationToken);
            _ = await new CatalogCheckpoints(directory, [], firstRetainedFile: 1).WriteAsync(new ReplicaCatalog(LedgerRecords.StoreHandle - 1, [], []), cut, cancellationToken);
        }

        await using (var exhausted = await root.OpenAsync())
        {
            var last = await CreateAndOpenReplicaAsync(exhausted, new(1));
            Assert.Equal(LedgerRecords.StoreHandle - 1, exhausted.FindLive(new(1))!.Handle);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CreateAsync(exhausted, new(2)));
            Assert.Equal(LedgerReplicaTestData.Ledgers(1), exhausted.Replicas);
            Assert.Equal(new CatalogPair(new(1), LedgerRecords.StoreHandle - 1, 64), Assert.Single(Assert.Single(await StoreRecordsAsync(exhausted))));
            await WriteRangeAsync(last, new(1), 0, 2);
        }

        await using var reopened = await root.OpenAsync();
        Assert.Equal(LedgerReplicaTestData.Ledgers(1), reopened.Replicas);
        await AssertRangeAsync(Replica(reopened, new(1)), new(1), 0, 2);
    }

    /// <summary>
    /// Regression test for #106: a create or drop wrote the catalog by replacing a file, which Windows refuses while
    /// another process, such as an antivirus scanner, holds the file open without delete sharing. Creations and drops now
    /// write only WAL records, so they succeed while the named files in the WAL directory, the node file and the catalog
    /// checkpoint, are held open that way. The held catalog checkpoint only stalls its deletion, and with it the WAL's
    /// release: the file system waits the holder out, which is no failure (conventions.md §14), and once the holder lets
    /// go, maintenance deletes it and nothing is reported.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CreationsAndDropsSucceedWhileTheCatalogCheckpointsAreHeldOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete or replace a file another process holds open without delete sharing.");
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var walPath = LedgerStore.WalPath(directory.Path);
        var held = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        try
        {
            await using var store = await LedgerStoreReplicaTests.OpenStoreAsync(directory.Path, create: true);
            await EventuallyAsync(() => store.MaintenanceIdle, "maintenance settles after the open");

            // Read sharing without delete sharing, as an antivirus scanner opens a file.
            foreach (var path in Directory.GetFiles(walPath).Where(static path =>
                path.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal) || string.Equals(Path.GetFileName(path), NodeFile.Name, StringComparison.Ordinal)))
            {
                held.Add(path, File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }

            var nodeFile = Path.Combine(walPath, NodeFile.Name);
            Assert.Contains(nodeFile, held.Keys);
            Assert.Contains(held.Keys, static path => path.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal));
            for (var index = 1UL; index <= 40; index++)
            {
                var ledger = new LedgerId(index);
                Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(store.OpenReplica(ledger, Window), 0, [1])).Case);
                await store.DropReplicaAsync(ledger, cancellationToken);
                store.RequestCatalogCheckpoint();
            }

            // Each held file is held as the scanner holds it: Windows refuses its deletion.
            _ = Assert.ThrowsAny<IOException>(() => File.Delete(nodeFile));
            Assert.Empty(store.Replicas);
            Assert.Equal(40, store.Catalog.Tombstones.Count);
            Assert.Equal(40u, store.Catalog.NextHandle);

            Release(held);
            await EventuallyAsync(
                () => store.MaintenanceIdle
                    && store.CatalogCheckpointCurrent
                    && Directory.GetFiles(walPath, "*" + CatalogCheckpoints.Extension).Length == CatalogCheckpoints.Kept,
                "maintenance deletes the superseded catalog checkpoints once their holder lets go");
        }
        finally
        {
            Release(held);
        }

        static void Release(Dictionary<string, SafeFileHandle> held)
        {
            foreach (var handle in held.Values)
            {
                handle.Dispose();
            }

            held.Clear();
        }
    }

    /// <summary>
    /// The steps of <see cref="TheNodeMatchesItsModelOfAbsentLiveAndDroppedReplicas"/>, mostly writes.
    /// </summary>
    private static readonly Gen<NodeStep> NodeSteps = Gen.Select(
            Gen.Frequency(
                (6, Gen.Const(NodeStepKind.Write)),
                (2, Gen.Const(NodeStepKind.RecoveryWrite)),
                (2, Gen.Const(NodeStepKind.Seal)),
                (1, Gen.Const(NodeStepKind.Drop)),
                (1, Gen.Const(NodeStepKind.CatalogCheckpoint)),
                (1, Gen.Const(NodeStepKind.ReplicaCheckpoint)),
                (1, Gen.Const(NodeStepKind.Restart))),
            Gen.ULong[1, 3],
            Gen.Long[0, 5],
            Gen.Bool)
        .Select(static (kind, ledger, address, operatingSystem) => new NodeStep(kind, new LedgerId(ledger), address, operatingSystem));

    /// <summary>
    /// The kinds of <see cref="NodeStep"/>.
    /// </summary>
    private enum NodeStepKind
    {
        Write,
        RecoveryWrite,
        Seal,
        Drop,
        CatalogCheckpoint,
        ReplicaCheckpoint,
        Restart,
    }

    private static SimulatedNodeRoot NewRoot(SimulatedWalStorage? storage = null) => new(SmallFiles, ExplicitOnly, storage);

    /// <summary>
    /// Runs <paramref name="steps"/> against a fresh node and its model, checking the node against the model after each.
    /// </summary>
    private static async Task RunModelAsync(NodeStep[] steps)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        try
        {
            var live = new Dictionary<LedgerId, ReplicaModel>();
            var dropped = new SortedSet<LedgerId>();
            foreach (var step in steps)
            {
                var ledger = step.Ledger;
                switch (step.Kind)
                {
                    case NodeStepKind.Write:
                    case NodeStepKind.RecoveryWrite:
                        var recovery = step.Kind == NodeStepKind.RecoveryWrite;
                        var write = SendWriteAsync(store, ledger, step.Address, recovery);
                        if (dropped.Contains(ledger))
                        {
                            DroppedAnswers.AssertDropped(await write);
                            break;
                        }

                        var written = Live(live, ledger);
                        if (!recovery && written.Sealed)
                        {
                            Assert.Equal(ReplicaAnswers.Sealed, await write);
                            break;
                        }

                        Assert.Equal(ReplicaWriteCase.Written, (await write).Case);
                        _ = written.Data.Add(step.Address);
                        break;
                    case NodeStepKind.Seal:
                        var seal = SendSealAsync(store, ledger);
                        if (dropped.Contains(ledger))
                        {
                            DroppedAnswers.AssertDropped(await seal);
                            break;
                        }

                        var sealedTail = (await seal).Tail;
                        Assert.Equal((Slot.Zero, true), (sealedTail.ContiguousTail, sealedTail.IsSealed));
                        Live(live, ledger).Sealed = true;
                        break;
                    case NodeStepKind.Drop:
                        await store.DropReplicaAsync(ledger, cancellationToken);
                        _ = live.Remove(ledger);
                        _ = dropped.Add(ledger);
                        break;
                    case NodeStepKind.CatalogCheckpoint:
                        await store.CheckpointCatalogPastWritesAsync();
                        await EventuallyAsync(() => store.MaintenanceIdle, "maintenance releases what the catalog checkpoint allows");
                        break;
                    case NodeStepKind.ReplicaCheckpoint:
                        if (live.ContainsKey(ledger))
                        {
                            await LedgerStoreReplicaTests.SettledCheckpointAsync(Replica(store, ledger));
                            await EventuallyAsync(() => store.MaintenanceIdle, "maintenance releases what the replica checkpoint allows");
                        }

                        break;
                    case NodeStepKind.Restart:
                        await root.CrashAsync(store, step.OperatingSystem);
                        store = await root.OpenAsync();
                        break;
                    default:
                        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"An unknown step {step}."));
                }

                await AssertMatchesAsync(store, live, dropped);
            }
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    /// <summary>
    /// Asserts that the node lists exactly the model's live replicas and holds exactly its tombstones, that each live
    /// replica holds exactly the model's DATA and seal, that each dropped ledger refuses, and that each absent one reads
    /// as an empty, unsealed replica.
    /// </summary>
    private static async Task AssertMatchesAsync(LedgerStore store, Dictionary<LedgerId, ReplicaModel> live, SortedSet<LedgerId> dropped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listed = live.Keys.Order().ToArray();
        Assert.Equal(listed, store.Replicas);
        Assert.Equal(listed, await store.ListReplicasAsync(cancellationToken));
        Assert.Equal(dropped, store.Catalog.Tombstones);
        foreach (var ledger in LedgerReplicaTestData.Ledgers(1, 2, 3))
        {
            await using var replica = Replica(store, ledger);
            if (dropped.Contains(ledger))
            {
                DroppedAnswers.AssertDropped(await replica.ReadAsync(Slot.Zero, cancellationToken));
                DroppedAnswers.AssertDropped(await replica.ReadTailAsync(cancellationToken));
                continue;
            }

            var model = live.GetValueOrDefault(ledger);
            Assert.Equal(new LedgerReplicaTail(Slot.Zero, model?.Sealed ?? false), await replica.ReadTailAsync(cancellationToken));
            for (var address = 0L; address <= 5; address++)
            {
                Assert.Equal(
                    model?.Data.Contains(address) == true ? Payload(ledger, address) : null,
                    await LedgerReplicaTestData.ReadBytesAsync(replica, address, cancellationToken));
            }
        }
    }

    /// <summary>
    /// Returns the model of <paramref name="ledger"/>'s live replica, which a write, recovery write, or seal creates.
    /// </summary>
    private static ReplicaModel Live(Dictionary<LedgerId, ReplicaModel> live, LedgerId ledger)
    {
        if (!live.TryGetValue(ledger, out var replica))
        {
            replica = new ReplicaModel();
            live.Add(ledger, replica);
        }

        return replica;
    }

    /// <summary>
    /// Sends a writer write, or a recovery write, of the one value of <paramref name="ledger"/>'s address through a replica
    /// of its own, carrying no contiguous tail.
    /// </summary>
    private static async Task<ReplicaWriteOutcome> SendWriteAsync(LedgerStore store, LedgerId ledger, long address, bool recovery)
    {
        await using var replica = Replica(store, ledger);
        using var payload = LedgerReplicaTestData.Buffer(Payload(ledger, address));
        return await replica.WriteAsync(new Slot(address), payload, Slot.Zero, recovery, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seals <paramref name="ledger"/>'s replica through a replica of its own, carrying no contiguous tail.
    /// </summary>
    private static async Task<ReplicaTailRead> SendSealAsync(LedgerStore store, LedgerId ledger)
    {
        await using var replica = Replica(store, ledger);
        return await replica.SealAsync(TestContext.Current.CancellationToken);
    }

    private static async Task RunAsync((int Kind, ulong Ledger)[] operations)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var store = await root.OpenAsync(create: true);
        try
        {
            var live = new List<CatalogReplica>();
            var dropped = new SortedSet<LedgerId>();
            var nextHandle = 0u;
            foreach (var (kind, value) in operations)
            {
                var ledger = new LedgerId(value);
                switch (kind)
                {
                    case 0 when live.Any(replica => replica.Ledger == ledger):
                        Assert.Equal(live.Single(replica => replica.Ledger == ledger).Handle, (await store.CreateLiveAsync(ledger, Window.WindowSize, cancellationToken))!.Handle);
                        break;
                    case 0 when dropped.Contains(ledger):
                        DroppedAnswers.AssertDropped(await store.CreateReplicaAsync(ledger, cancellationToken));
                        break;
                    case 0:
                        var created = (await store.CreateLiveAsync(ledger, Window.WindowSize, cancellationToken))!;
                        Assert.Equal(nextHandle++, created.Handle);
                        live.Add(created.Entry);
                        break;
                    default:
                        await store.DropReplicaAsync(ledger, cancellationToken);
                        _ = live.RemoveAll(replica => replica.Ledger == ledger);
                        _ = dropped.Add(ledger);
                        break;
                }

                Assert.Equal(live, store.Catalog.Replicas);
                Assert.Equal(dropped, store.Catalog.Tombstones);
                Assert.Equal(nextHandle, store.Catalog.NextHandle);
            }

            await root.CrashAsync(store, operatingSystem: true);
            store = await root.OpenAsync();
            Assert.Equal(live.Select(static replica => replica.Ledger).Order(), store.Replicas);
            Assert.Equal(live.Select(static replica => replica.Handle), store.Catalog.Replicas.Select(static replica => replica.Handle));
            Assert.Equal(dropped, store.Catalog.Tombstones);
            Assert.Equal(nextHandle, store.Catalog.NextHandle);
            foreach (var ledger in dropped)
            {
                DroppedAnswers.AssertDropped(await store.CreateReplicaAsync(ledger, cancellationToken));
            }
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns the pairs of every create record the store's WAL holds, in log order.
    /// </summary>
    private static async Task<List<CatalogPair[]>> StoreRecordsAsync(LedgerStore store) =>
        [.. (await StoreRecordsOfAsync(store)).OfType<CreateRecord>().Select(static create => create.Pairs)];

    /// <summary>
    /// Returns the ledger of every drop record the store's WAL holds, in log order.
    /// </summary>
    private static async Task<List<LedgerId>> DropRecordsAsync(LedgerStore store) =>
        [.. (await StoreRecordsOfAsync(store)).OfType<DropRecord>().Select(static drop => drop.Ledger)];

    private static async Task<List<StoreRecord>> StoreRecordsOfAsync(LedgerStore store)
    {
        // The WAL reads up to its durable frontier, which passes a record once the WAL retires it, after answering it.
        await store.WaitForFrontierAsync();
        var records = new List<StoreRecord>();
        await foreach (var entry in store.Wal.ReadAllAsync(default, TestContext.Current.CancellationToken))
        {
            if (entry.TryGetValue(out WalRecord record) && StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed)
            {
                records.Add(parsed);
            }
        }

        return records;
    }

    private static byte[] Payload(LedgerId ledger, long slot) => LedgerReplicaTestData.CreatePayload(400, (int)ledger.Value * 1_000 + (int)slot);

    private static async Task<LedgerStoreReplica> CreateAndOpenReplicaAsync(LedgerStore store, LedgerId ledger)
    {
        await store.CreateReplicaAsync(ledger, TestContext.Current.CancellationToken);
        return Replica(store, ledger);
    }

    private static LedgerStoreReplica Replica(LedgerStore store, LedgerId ledger) => store.OpenReplica(ledger, Window);

    private static bool IsCatalogCheckpoint(string name) => name.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal);

    /// <summary>
    /// Drops <paramref name="ledger"/>, leaving its tombstone (LS-I2).
    /// </summary>
    private static Task DropAsync(LedgerStore store, LedgerId ledger) =>
        store.DropReplicaAsync(ledger, TestContext.Current.CancellationToken).AsTask();

    /// <summary>
    /// Creates <paramref name="ledger"/>'s replica as its first write would (LS-I1).
    /// </summary>
    private static async Task<LiveReplica> CreateAsync(LedgerStore store, LedgerId ledger, CancellationToken? cancellationToken = null) =>
        (await store.CreateLiveAsync(ledger, Window.WindowSize, cancellationToken ?? TestContext.Current.CancellationToken))!;

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = TimeProvider.System.GetTimestamp() + TimeSpan.FromSeconds(20).Ticks * TimeProvider.System.TimestampFrequency / TimeSpan.TicksPerSecond;
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetTimestamp() < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    private static Task<IReadOnlyList<string>> ListSlotsAsync(SimulatedNodeRoot root) =>
        root.Storage.ListDirectoriesAsync(LedgerStore.SlotsPath(SimulatedNodeRoot.Path), TestContext.Current.CancellationToken).AsTask();

    private static async Task WriteRangeAsync(ILedgerReplica replica, LedgerId ledger, long first, int count)
    {
        for (var slot = first; slot < first + count; slot++)
        {
            Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot))).Case);
        }
    }

    private static async Task AssertRangeAsync(ILedgerReplica replica, LedgerId ledger, long first, int count)
    {
        for (var slot = first; slot < first + count; slot++)
        {
            Assert.Equal(Payload(ledger, slot), await LedgerReplicaTestData.ReadBytesAsync(replica, slot, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// Asserts that a replica answers every operation <c>Dropped</c> (LS-I2, LR-I3).
    /// </summary>
    private static async Task AssertRefusedAsync(ILedgerReplica replica)
    {
        var token = TestContext.Current.CancellationToken;
        DroppedAnswers.AssertDropped(await LedgerStoreReplicaTests.WriteAsync(replica, 100, [1]));
        DroppedAnswers.AssertDropped(await replica.ReadAsync(Slot.Zero, token));
        DroppedAnswers.AssertDropped(await replica.ReadAsync(Slot.Zero, 0, 1, token));
        DroppedAnswers.AssertDropped(await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 0, [1]));
        DroppedAnswers.AssertDropped(await replica.ReadTailAsync(token));
        DroppedAnswers.AssertDropped(await replica.SealAsync(token));
        if (replica is LedgerStoreReplica storeReplica)
        {
            await storeReplica.CheckpointAsync(token);
        }
    }

    /// <summary>
    /// One step of <see cref="RunModelAsync"/>: a writer write, recovery write, or seal on <paramref name="Ledger"/>, at
    /// <paramref name="Address"/> for a write, a drop of the ledger or a checkpoint of its replica, a catalog checkpoint,
    /// or a restart, of the operating system when <paramref name="OperatingSystem"/> is set.
    /// </summary>
    private sealed record NodeStep(NodeStepKind Kind, LedgerId Ledger, long Address, bool OperatingSystem)
    {
        public override string ToString() => Kind switch
        {
            NodeStepKind.Write or NodeStepKind.RecoveryWrite => string.Create(CultureInfo.InvariantCulture, $"{Kind}(S{Ledger.Value}, {Address})"),
            NodeStepKind.Seal or NodeStepKind.Drop or NodeStepKind.ReplicaCheckpoint => string.Create(CultureInfo.InvariantCulture, $"{Kind}(S{Ledger.Value})"),
            NodeStepKind.CatalogCheckpoint => "CatalogCheckpoint",
            NodeStepKind.Restart => OperatingSystem ? "Restart(operating system)" : "Restart(process)",
            _ => Kind.ToString(),
        };
    }

    /// <summary>
    /// A live replica of the model: the addresses which hold DATA, and its seal.
    /// </summary>
    private sealed class ReplicaModel
    {
        internal HashSet<long> Data { get; } = [];

        internal bool Sealed { get; set; }
    }

    /// <summary>
    /// A claim whose publication always fails, as on a root whose claim cannot be written.
    /// </summary>
    private sealed class UnpublishableClaim : INodeRootClaim
    {
        public ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask<bool> CreateIfAbsentAsync(byte[] content, CancellationToken cancellationToken) =>
            ValueTask.FromException<bool>(new IOException("The claim cannot be published."));
    }

    /// <summary>
    /// Storage whose deletions of the directory <paramref name="held"/> answer that its lock is held, the first always and
    /// later ones until the lock is released, which it is from the start when <paramref name="releasedAfterFirst"/>; it
    /// otherwise passes every operation to <paramref name="inner"/>.
    /// </summary>
    private sealed class HeldDeletionStorage(WalStorage inner, string held, bool releasedAfterFirst) : WalStorage
    {
        private int _deletions;
        private bool _released = releasedAfterFirst;

        /// <summary>
        /// Gets the number of deletions of the held directory attempted.
        /// </summary>
        public int Deletions => Volatile.Read(ref _deletions);

        /// <summary>
        /// Releases the lock, so the next deletion of the held directory proceeds.
        /// </summary>
        public void Release() => Volatile.Write(ref _released, true);

        public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public override ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken) => inner.OpenDirectoryAsync(path, cancellationToken);

        public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            if (string.Equals(path, held, StringComparison.Ordinal)
                && (Interlocked.Increment(ref _deletions) == 1 || !Volatile.Read(ref _released)))
            {
                throw new WalLockHeldException($"Another handle held the lock of '{path}' past the contention bound.");
            }

            return inner.DeleteDirectoryAsync(path, cancellationToken);
        }

        public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken) =>
            inner.ListDirectoriesAsync(path, cancellationToken);

        public override ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken) =>
            inner.MeasureDirectoryAsync(path, cancellationToken);
    }

    [Fact]
    public async Task ReadRangeAsyncMatchesPointWiseReadsAcrossGapsAndByteCaps()
    {
        var token = TestContext.Current.CancellationToken;
        var root = NewRoot(new SimulatedWalStorage(seed: 19));
        await using var store = await root.OpenAsync(create: true);
        var ledger = new LedgerId(42);
        await using var replica = await CreateAndOpenReplicaAsync(store, ledger);

        // Write slots 0..9 and 12..15 (leaving a gap at 10..11).
        var payloads = new Dictionary<long, byte[]>();
        foreach (var slot in Enumerable.Range(0, 10).Concat(Enumerable.Range(12, 4)))
        {
            var data = Enumerable.Range(0, 32 + slot * 13).Select(i => unchecked((byte)(slot * 17 + i))).ToArray();
            payloads[slot] = data;
            _ = Assert.IsType<ReplicaWriteOutcome.Written>((await LedgerStoreReplicaTests.WriteAsync(replica, slot, data)).Value);
        }

        // Uncapped range from 0..16 stops at gap slot 10 and matches point-wise reads exactly.
        var firstRange = await replica.ReadRangeAsync(Slot.Zero, new Slot(16), new Slot(10), maxSlots: 128, maxBytes: 1 << 20, token);
        Assert.True(firstRange.TryGetValue(out ReplicaRangeBatch batch));
        using (batch)
        {
            Assert.Equal(Slot.Zero, batch.FirstSlot);
            Assert.Equal(10, batch.Count);
            Assert.Null(ReplicaRangeBatch.ValidatePacked(batch.Count, batch.Packed));
            var offset = 0;
            for (var s = 0; s < 10; s++)
            {
                using var slice = ReplicaRangeBatch.SliceNext(batch.Packed, ref offset);
                Assert.Equal(payloads[s], slice.ToArray());
            }

            Assert.Equal(batch.Packed.Length, offset);
        }

        // Range starting at gap slot 10 returns empty.
        var atGap = await replica.ReadRangeAsync(new Slot(10), new Slot(16), new Slot(10), maxSlots: 128, maxBytes: 1 << 20, token);
        Assert.True(atGap.TryGetValue(out ReplicaRangeBatch gapBatch));
        using (gapBatch)
        {
            Assert.Equal(0, gapBatch.Count);
            Assert.Equal(0, gapBatch.FirstSlotTooLargeLength);
        }

        // Byte-capped iteration from 0..10 yields all 10 slots with exact parity.
        var cursor = Slot.Zero;
        while (cursor < new Slot(10))
        {
            var chunk = await replica.ReadRangeAsync(cursor, new Slot(10), new Slot(10), maxSlots: 3, maxBytes: 256, token);
            Assert.True(chunk.TryGetValue(out ReplicaRangeBatch part));
            using (part)
            {
                Assert.InRange(part.Count, 1, 3);
                Assert.True(part.Packed.Length <= 256);
                var offset = 0;
                for (var i = 0; i < part.Count; i++)
                {
                    using var slice = ReplicaRangeBatch.SliceNext(part.Packed, ref offset);
                    Assert.Equal(payloads[cursor.Value + i], slice.ToArray());
                }

                cursor += part.Count;
            }
        }
    }
}
