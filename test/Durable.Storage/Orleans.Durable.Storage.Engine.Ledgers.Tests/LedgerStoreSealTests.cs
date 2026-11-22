using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the LedgerStore's seal (LS-I11): one seal record in the node's WAL, which every writer write the replica accepted
/// before it precedes, and which answers, and refuses later writer writes, only once it is durable (LR-I8, LR-I4,
/// conventions.md §13). Runs over <see cref="SimulatedWalStorage"/>, which holds and fails WAL writes.
/// </summary>
public sealed class LedgerStoreSealTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    private static readonly LedgerReplicaOptions Window4 = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LS-I11, LR-I8, LR-I5, LR-I4: a writer write whose outcome published before the seal answers
    /// <see cref="ReplicaAnswers.Written"/> and its record precedes the seal record in the WAL; a writer write after the seal
    /// answers <see cref="ReplicaAnswers.Sealed"/> and appends nothing, whether its slot is missing or holds DATA; a recovery
    /// write on the sealed replica answers <see cref="ReplicaAnswers.Written"/> and appends its DATA after the seal record.
    /// Every answer survives an operating-system crash.
    /// </summary>
    [Fact]
    public async Task WriterWritesBeforeTheSealAreWrittenAndThoseAfterItAreSealedAndStoreNothing()
    {
        var root = LedgerStoreReplicaTests.Simulated();
        var replica = await root.CreateReplicaAsync(Window);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 0, [1])).Case);
        var sealedTail = (await replica.SealAsync(Token)).Tail;
        Assert.Equal((Slot.Zero, true), (sealedTail.ContiguousTail, sealedTail.IsSealed));
        Assert.True(sealedTail.LeaseRemaining > TimeSpan.Zero && sealedTail.LeaseRemaining <= LedgerLeaseOptions.DefaultLeaseDuration);
        Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(replica, 1, [2]));
        Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(replica, 0, [3]));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 1, Token));
        Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 2, [4]));
        Assert.Equal(["create 7 K64", "slot 0", "seal 7", "slot 2"], await RecordsAsync(replica.Store));
        await root.CrashAsync(replica, operatingSystem: true);

        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, Token));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, Token));
        Assert.Equal([4], await LedgerReplicaTestData.ReadBytesAsync(reopened, 2, Token));
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), await reopened.ReadTailAsync(Token));
        Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(reopened, 1, [5]));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, Token));
    }

    /// <summary>
    /// LS-I11, LR-I4: a seal waits for every writer write which checked the seal before it to publish its outcome, and
    /// only then submits its record: while such a write's WAL write is held, the WAL admits no seal record and the seal
    /// stays unanswered. Once the write is durable the seal answers, its record after the write's, so a read sent after
    /// the seal's answer finds the write's DATA and can never answer Missing for a writer write which lands later.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASealSubmitsItsRecordOnlyOnceEveryEarlierWriterWriteIsDurable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = LedgerStoreReplicaTests.Simulated();
        await using var replica = await root.CreateReplicaAsync(Window);
        root.Storage.HoldWrites = true;
        var write = LedgerStoreReplicaTests.WriteAsync(replica, 5, [5]);
        await LedgerStoreTestExtensions.WaitUntilAsync(
            () => root.Storage.PendingWriteCount == 1 && replica.Store.Wal.OutstandingRecords == 1,
            "the writer write is the WAL's only record in flight");

        var seal = replica.SealAsync(cancellationToken).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
        Assert.False(seal.IsCompleted, "The seal answered before an earlier writer write was durable.");
        Assert.False(write.IsCompleted);
        Assert.Equal(1, replica.Store.Wal.OutstandingRecords);
        Assert.Equal(1, root.Storage.PendingWriteCount);
        Assert.False(replica.Ledger.IsSealed);

        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.True((await seal).Tail.IsSealed);
        Assert.Equal([5], await LedgerReplicaTestData.ReadBytesAsync(replica, 5, cancellationToken));
        Assert.Equal(ReplicaWriteCase.Written, (await write).Case);
        Assert.Equal(["create 7 K64", "slot 5", "seal 7"], await RecordsAsync(replica.Store));
    }

    /// <summary>
    /// LS-I11, LR-I8, conventions.md §13: while a seal record is in flight, a repeated seal, a writer write, and a
    /// contiguous-tail read which meet the seal all wait for its record and answer nothing early. Once the record is
    /// durable, each answers sealed with the retained contiguous tail, the writer write <see cref="ReplicaAnswers.Sealed"/>
    /// without a record, and the repeated seal appends no second seal record.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task OperationsMeetingASealInFlightAnswerOnlyOnceItsRecordIsDurable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = LedgerStoreReplicaTests.Simulated();
        await using var replica = await root.CreateReplicaAsync(Window);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 0, [1])).Case);
        root.Storage.HoldWrites = true;
        var sealing = ReadTailAsync(replica, 1, seal: true);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => root.Storage.PendingWriteCount == 1, "the seal record is in flight");

        var again = ReadTailAsync(replica, 2, seal: true);
        var write = LedgerStoreReplicaTests.WriteAsync(replica, 1, [2]);
        var tail = ReadTailAsync(replica, 3, seal: false);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
        Task[] waiting = [sealing, again, write, tail];
        Assert.All(waiting, static operation => Assert.False(operation.IsCompleted, "An operation answered before the seal record was durable."));
        Assert.Equal(1, root.Storage.PendingWriteCount);
        Assert.False(replica.Ledger.IsSealed);

        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        var firstSeal = (await sealing).Tail;
        var secondSeal = (await again).Tail;
        Assert.Equal((new Slot(3), true), (firstSeal.ContiguousTail, firstSeal.IsSealed));
        Assert.True(firstSeal.LeaseRemaining > TimeSpan.Zero && firstSeal.LeaseRemaining <= LedgerLeaseOptions.DefaultLeaseDuration);
        Assert.Equal((new Slot(3), true), (secondSeal.ContiguousTail, secondSeal.IsSealed));
        Assert.True(secondSeal.LeaseRemaining > TimeSpan.Zero && secondSeal.LeaseRemaining <= LedgerLeaseOptions.DefaultLeaseDuration);
        Assert.Equal(new LedgerReplicaTail(new Slot(3), IsSealed: true), await tail);
        Assert.Equal(ReplicaAnswers.Sealed, await write);
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 1, cancellationToken));
        Assert.Equal(["create 7 K64", "slot 0", "seal 7"], await RecordsAsync(replica.Store));
    }

    /// <summary>
    /// LS-I11, conventions.md §13 and §14: a seal whose record's WAL write fails is reported, which stops the process, and
    /// every operation waiting on it fails with the seal's failure, never answering sealed: the seal, a writer write which
    /// met it, a repeated seal, and a contiguous-tail read. The earlier writer write the seal waited for keeps its answer,
    /// and the restarted process recovers it; the failed write stored no byte of the seal record, so the unanswered seal
    /// is lost and the restarted replica is unsealed.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASealWhoseRecordFailsFailsEveryOperationWaitingOnIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = LedgerStoreReplicaTests.Simulated();
        var replica = await root.CreateReplicaAsync(Window);
        root.Storage.HoldWrites = true;
        var earlier = LedgerStoreReplicaTests.WriteAsync(replica, 0, [1]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => root.Storage.PendingWriteCount == 1, "the earlier writer write is in flight");
        var sealing = ReadTailAsync(replica, 0, seal: true);
        var write = LedgerStoreReplicaTests.WriteAsync(replica, 1, [2]);
        var again = ReadTailAsync(replica, 0, seal: true);
        var tail = ReadTailAsync(replica, 0, seal: false);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
        Task[] waiting = [sealing, write, again, tail];
        Assert.All(waiting, static operation => Assert.False(operation.IsCompleted));

        // The earlier write completes and the seal then submits its record, whose write fails.
        var reported = FatalErrors.ReportedCount;
        root.Storage.WriteFault = static (_, _, _, _) => new IOException("injected seal record failure");
        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.Equal(ReplicaWriteCase.Written, (await earlier).Case);
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => sealing);
        Assert.Contains(Chain(failure), static exception => exception.Message.Contains("injected seal record failure", StringComparison.Ordinal));
        Task[] met = [write, again, tail];
        foreach (var operation in met)
        {
            Assert.Same(failure, await Assert.ThrowsAnyAsync<IOException>(() => operation));
        }

        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleWriteGather, SimulatedNodeRoot.WalPath);
        _ = await Assert.ThrowsAnyAsync<IOException>(async () => await replica.SealAsync(cancellationToken));
        _ = await Assert.ThrowsAnyAsync<IOException>(() => LedgerStoreReplicaTests.WriteAsync(replica, 2, [3]));
        _ = await Assert.ThrowsAnyAsync<IOException>(async () => await replica.ReadTailAsync(cancellationToken));

        await root.CrashAsync(replica, operatingSystem: false);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await reopened.ReadTailAsync(cancellationToken));
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(reopened, 1, [2])).Case);
    }

    /// <summary>
    /// LS-I11, conventions.md §14: a seal which fails with a defect before its record reaches the WAL leaves the replica
    /// marked sealed apart from its WAL, so the defect is reported once, as <c>LedgerStoreSeal</c>, which
    /// stops the process, before any operation waiting on the seal answers. The seal and a writer write which met it fail
    /// with the defect, never answering sealed; the stopped store answers nothing more; and the restarted replica is
    /// unsealed and holds every acknowledged write.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASealWhichFailsWithADefectIsReportedOnceAndTheRestartedReplicaIsUnsealed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = LedgerStoreReplicaTests.Simulated();
        var replica = await root.CreateReplicaAsync(Window);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 0, [1])).Case);
        root.Storage.HoldWrites = true;
        var earlier = LedgerStoreReplicaTests.WriteAsync(replica, 1, [2]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => root.Storage.PendingWriteCount == 1, "the earlier writer write is in flight");
        var defect = new InvalidOperationException("injected seal defect");
        replica.Store.StoreRecordFault = record => record is SealRecord ? defect : null;
        var sealing = ReadTailAsync(replica, 0, seal: true);
        var write = LedgerStoreReplicaTests.WriteAsync(replica, 2, [3]);
        var reported = FatalErrors.ReportedCount;
        var reportedAtAnswer = -1L;
        var answered = write.ContinueWith(
            _ => reportedAtAnswer = FatalErrors.ReportedCount,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
        Assert.False(sealing.IsCompleted || write.IsCompleted, "An operation answered before the seal's outcome.");

        // The earlier write completes, and the seal then fails before its record reaches the WAL.
        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.Equal(ReplicaWriteCase.Written, (await earlier).Case);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => sealing));
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => write));
        await answered;
        Assert.Equal(reported + 1, reportedAtAnswer);
        _ = StorageFailures.TakeDefect(reported, "LedgerStoreSeal", defect);
        await StoppedStore.AssertAnswersNothingAsync(replica.Store, replica.Replica, defect);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await replica.Store.DisposeAsync()));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await reopened.ReadTailAsync(cancellationToken));
        Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
        Assert.Equal([2], await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, cancellationToken));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 2, cancellationToken));
        Assert.Equal(["create 7 K64", "slot 0", "slot 1"], await RecordsAsync(reopened.Store));
    }

    /// <summary>
    /// LR-I7, LR-I6, LS-I1, LS-I11: a seal of a replica the node neither holds nor dropped creates it sealed, with one
    /// create record which records the sealing request's window, followed by its seal record. After a restart, which replays
    /// both, a request of another window is refused before any effect, and one of the recorded window finds the replica
    /// sealed, empty, and retaining 0, refuses its writer writes, and takes its recovery writes.
    /// </summary>
    [Fact]
    public async Task ASealOfAnUnlistedReplicaCreatesItSealedWithTheSealingRequestsWindow()
    {
        var root = LedgerStoreReplicaTests.Simulated();
        var store = await root.OpenAsync(create: true);
        await using (var sealer = store.OpenReplica(LedgerReplicaTestData.Ledger, Window4))
        {
            var sealedTail = (await sealer.ReadTailAsync(new Slot(2), seal: true, Token)).Tail;
            Assert.Equal((new Slot(2), true), (sealedTail.ContiguousTail, sealedTail.IsSealed));
            Assert.True(sealedTail.LeaseRemaining > TimeSpan.Zero && sealedTail.LeaseRemaining <= LedgerLeaseOptions.DefaultLeaseDuration);
        }

        Assert.Equal([LedgerReplicaTestData.Ledger], store.Replicas);
        Assert.Equal(Window4.WindowSize, store.Catalog.Find(LedgerReplicaTestData.Ledger)!.Window);
        Assert.True(store.FindLive(LedgerReplicaTestData.Ledger)!.IsSealed);
        Assert.Equal(["create 7 K4", "seal 7"], await RecordsAsync(store));
        await root.CrashAsync(store, operatingSystem: true);

        await using var reopened = await root.OpenAsync();
        await using (var wide = reopened.OpenReplica(LedgerReplicaTestData.Ledger, Window))
        {
            await LedgerStoreReplicaTests.AssertRefusesAnotherWindowAsync(wide);
        }

        await using var replica = reopened.OpenReplica(LedgerReplicaTestData.Ledger, Window4);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), await replica.ReadTailAsync(Token));
        Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(replica, 0, [1]));
        Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 0, [2]));
        Assert.Equal([2], await LedgerReplicaTestData.ReadBytesAsync(replica, 0, Token));
    }

    /// <summary>
    /// LS-I2, LR-I3, LP-I5: a drop of a sealed replica is one drop record, which removes the replica with its seal: the
    /// node no longer lists it, its tombstone answers every operation, a seal among them, <c>Dropped</c>, and nothing
    /// creates the replica again, across a restart.
    /// </summary>
    [Fact]
    public async Task ADropOfASealedReplicaRemovesItWithItsSeal()
    {
        var root = LedgerStoreReplicaTests.Simulated();
        var replica = await root.CreateReplicaAsync(Window);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 0, [1])).Case);
        Assert.True((await replica.SealAsync(Token)).Tail.IsSealed);
        await replica.Store.DropReplicaAsync(LedgerReplicaTestData.Ledger, Token);
        Assert.Empty(await replica.Store.ListReplicasAsync(Token));
        Assert.Null(replica.Store.Catalog.Find(LedgerReplicaTestData.Ledger));
        Assert.True(replica.Store.Catalog.IsDropped(LedgerReplicaTestData.Ledger));
        await AssertDroppedAsync(replica.Replica);
        Assert.Equal(["create 7 K64", "slot 0", "seal 7", "drop 7"], await RecordsAsync(replica.Store));
        await root.CrashAsync(replica, operatingSystem: true);

        await using var reopened = await root.OpenAsync();
        Assert.Empty(await reopened.ListReplicasAsync(Token));
        Assert.True(reopened.Catalog.IsDropped(LedgerReplicaTestData.Ledger));
        await using (var again = reopened.OpenReplica(LedgerReplicaTestData.Ledger, Window))
        {
            await AssertDroppedAsync(again);
        }

        DroppedAnswers.AssertDropped(await reopened.CreateReplicaAsync(LedgerReplicaTestData.Ledger, Token));
        Assert.Empty(await reopened.ListReplicasAsync(Token));
    }

    /// <summary>
    /// ledger-replicas.md §6.3, LR-I7, LS-I11: an operation whose cancellation precedes it touches no storage. A seal, a writer
    /// write, and a recovery write with a cancelled token fail with <see cref="OperationCanceledException"/> before any
    /// effect, on a replica the node does not hold and on one it holds: the WAL issues no write and holds no new record,
    /// the node still lists only the second replica, which stays unsealed, empty where the writes were sent, and retains
    /// no contiguous tail they carried, and both serve later operations.
    /// </summary>
    [Fact]
    public async Task AnOperationCancelledBeforeItStartsTouchesNoStorage()
    {
        var root = LedgerStoreReplicaTests.Simulated();
        await using var store = await root.OpenAsync(create: true);
        var held = new LedgerId(LedgerReplicaTestData.Ledger.Value + 1);
        await using var unlisted = store.OpenReplica(LedgerReplicaTestData.Ledger, Window);
        await using var listed = store.OpenReplica(held, Window);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(listed, 0, [1])).Case);
        var records = await RecordsAsync(store);
        var issued = root.Storage.IssuedWriteCount;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var tail = new Slot(3);
        LedgerStoreReplica[] replicas = [unlisted, listed];

        foreach (var replica in replicas)
        {
            using var payload = LedgerReplicaTestData.Buffer([2]);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replica.ReadTailAsync(tail, seal: true, cancelled.Token));
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replica.WriteAsync(new Slot(1), payload, tail, recovery: false, cancelled.Token));
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replica.WriteAsync(new Slot(1), payload, tail, recovery: true, cancelled.Token));
        }

        Assert.Equal(issued, root.Storage.IssuedWriteCount);
        Assert.Equal(records, await RecordsAsync(store));
        Assert.Equal([held], store.Replicas);
        Assert.False(listed.Ledger.IsSealed);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await listed.ReadTailAsync(Token));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(listed, 1, Token));
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await unlisted.ReadTailAsync(Token));
        Assert.Equal(["create 8 K64", "slot 0"], records);

        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(listed, 1, [3])).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(unlisted, 0, [4])).Case);
        Assert.Equal(["create 8 K64", "slot 0", "slot 1", "create 7 K64", "slot 0"], await RecordsAsync(store));
    }

    /// <summary>
    /// Describes every record of the store's WAL, in log order, once the durable frontier has passed every record
    /// admitted: <c>create {ledger} K{window}</c> per create record's pair, <c>seal {ledger}</c>, <c>drop {ledger}</c>,
    /// and <c>slot {slot}</c>.
    /// </summary>
    private static async Task<List<string>> RecordsAsync(LedgerStore store)
    {
        await store.WaitForFrontierAsync();
        var records = new List<string>();
        await foreach (var entry in store.Wal.ReadAllAsync(default, Token))
        {
            if (!entry.TryGetValue(out WalRecord record))
            {
                continue;
            }

            if (StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed)
            {
                records.Add(parsed switch
                {
                    CreateRecord create => string.Join(", ", create.Pairs.Select(static pair => Invariant($"create {pair.Ledger.Value} K{pair.Window}"))),
                    SealRecord seal => Invariant($"seal {seal.Ledger.Value}"),
                    DropRecord drop => Invariant($"drop {drop.Ledger.Value}"),
                    _ => throw new InvalidOperationException(Invariant($"An unknown store record {parsed}.")),
                });
            }
            else if (LedgerRecords.Classify(record.Payload, LedgerRecords.StoreHandle, out _, out var slot) == RecordClass.Slot)
            {
                records.Add(Invariant($"slot {slot}"));
            }
        }

        return records;
    }

    /// <summary>
    /// Asserts that every operation of <paramref name="replica"/> answers <c>Dropped</c>, and that a checkpoint has nothing
    /// to capture (LS-I2, LR-I3).
    /// </summary>
    private static async Task AssertDroppedAsync(LedgerStoreReplica replica)
    {
        DroppedAnswers.AssertDropped(await LedgerStoreReplicaTests.WriteAsync(replica, 1, [1]));
        DroppedAnswers.AssertDropped(await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 1, [1]));
        DroppedAnswers.AssertDropped(await replica.ReadAsync(Slot.Zero, Token));
        DroppedAnswers.AssertDropped(await replica.ReadAsync(Slot.Zero, 0, 1, Token));
        DroppedAnswers.AssertDropped(await replica.ReadTailAsync(Token));
        DroppedAnswers.AssertDropped(await replica.SealAsync(Token));
        await replica.CheckpointAsync(Token);
    }

    /// <summary>
    /// Sends a contiguous-tail read carrying <paramref name="contiguousTail"/>, sealing when <paramref name="seal"/> is set.
    /// </summary>
    private static async Task<ReplicaTailRead> ReadTailAsync(ILedgerReplica replica, long contiguousTail, bool seal) =>
        await replica.ReadTailAsync(new Slot(contiguousTail), seal, TestContext.Current.CancellationToken);

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}
