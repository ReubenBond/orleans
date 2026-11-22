using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks <see cref="LedgerStoreReplica"/>, the ledger replica contract over one LedgerStore replica (ledgerstore.md §2.1),
/// over real node roots and over <see cref="SimulatedWalStorage"/>.
/// </summary>
public sealed class LedgerStoreReplicaTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private static readonly LedgerReplicaOptions Window64 = new() { WindowSize = 64 };

    private static readonly LedgerReplicaOptions Window4 = new() { WindowSize = 4 };

    /// <summary>
    /// Writes out of address order survive a reopen, which rebuilds the retained contiguous tail, kept in memory, to
    /// <c>max(0, h + 1 − K)</c> for the highest slot <c>h</c> and the window <c>K</c> the replica recorded (LR-I6).
    /// </summary>
    [Fact]
    public async Task OutOfOrderWritesSurviveReopenWhichRebuildsTheRetainedTail()
    {
        using var directory = new TemporaryDirectory();
        await using (var replica = await CreateAsync(directory.Path, windowSize: 8))
        {
            await Task.WhenAll(Enumerable.Range(0, 64).Reverse().Select(address => WriteAsync(replica, address, [(byte)address, 99])));
            Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(TestContext.Current.CancellationToken));
        }

        await using var reopened = await OpenAsync(directory.Path, new LedgerReplicaOptions { WindowSize = 8 });
        Assert.Equal(new LedgerReplicaTail(new Slot(56), IsSealed: false), await reopened.ReadTailAsync(TestContext.Current.CancellationToken));
        foreach (var address in new long[] { 0, 31, 63 })
        {
            Assert.Equal(new byte[] { (byte)address, 99 }, await LedgerReplicaTestData.ReadBytesAsync(reopened, address, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// LR-I8, LS-I11: a writer write racing a seal either precedes it, written and durable, or follows it, sealed and
    /// absent; once sealed, every writer write reports sealed, and both outcomes survive a reopen.
    /// </summary>
    [Fact]
    public async Task AWriterWriteRacingASealKeepsItsOutcomeAcrossReopen()
    {
        using var directory = new TemporaryDirectory();
        ReplicaWriteOutcome outcome;
        await using (var replica = await CreateAsync(directory.Path))
        {
            var write = WriteAsync(replica, 0, "winner"u8.ToArray());
            var seal = replica.SealAsync(TestContext.Current.CancellationToken).AsTask();
            await Task.WhenAll(write, seal);
            outcome = await write;
            Assert.True((await seal).Tail.IsSealed);
            Assert.Equal(ReplicaAnswers.Sealed, await WriteAsync(replica, 0, "winner"u8.ToArray()));
            Assert.Equal(ReplicaAnswers.Sealed, await WriteAsync(replica, 1, "late"u8.ToArray()));
        }

        await using var reopened = await OpenAsync(directory.Path);
        Assert.Equal(outcome is ReplicaWriteOutcome.Written ? "winner"u8.ToArray() : null, await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, TestContext.Current.CancellationToken));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, TestContext.Current.CancellationToken));
        Assert.True((await reopened.ReadTailAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
        Assert.Equal(ReplicaAnswers.Sealed, await WriteAsync(reopened, 2, "later"u8.ToArray()));
    }

    /// <summary>
    /// LS-I7, LR-I1: concurrent writer and recovery writes of one slot are linearizable and report its one durable
    /// outcome; after a seal, the writer writes report sealed and the recovery writes still create the slot's DATA
    /// (LR-I5, LR-I8).
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesOfOneSlotAreLinearizable()
    {
        using var directory = new TemporaryDirectory();
        await using var replica = await CreateAsync(directory.Path);
        for (var address = 0; address < 32; address++)
        {
            if (address == 16)
            {
                Assert.True((await replica.SealAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
            }

            var writes = Enumerable.Range(0, 4).Select(_ => WriteAsync(replica, address, [(byte)address])).ToArray();
            var recoveries = Enumerable.Range(0, 2).Select(_ => RecoveryWriteAsync(replica, address, [(byte)address])).ToArray();
            await Task.WhenAll([.. writes, .. recoveries]);

            Assert.Equal(new byte[] { (byte)address }, await LedgerReplicaTestData.ReadBytesAsync(replica, address, TestContext.Current.CancellationToken));
            Assert.All(await Task.WhenAll(recoveries), static result => Assert.Equal(ReplicaAnswers.Written, result));
            var expected = address < 16 ? ReplicaWriteCase.Written : ReplicaWriteCase.Sealed;
            foreach (var write in writes)
            {
                Assert.Equal(expected, (await write).Case);
            }
        }
    }

    /// <summary>
    /// LR-I5, LR-I8: a sealed replica refuses a writer write but takes a recovery write, which creates missing DATA and
    /// leaves existing DATA; the DATA and the seal survive a reopen.
    /// </summary>
    [Fact]
    public async Task ARecoveryWriteOnASealedReplicaCreatesItsDataOnce()
    {
        using var directory = new TemporaryDirectory();
        await using (var replica = await CreateAsync(directory.Path))
        {
            _ = await replica.SealAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ReplicaAnswers.Sealed, await WriteAsync(replica, 0, "refused"u8.ToArray()));
            Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 0, TestContext.Current.CancellationToken));
            Assert.Equal(ReplicaAnswers.Written, await RecoveryWriteAsync(replica, 0, "canonical"u8.ToArray()));
            Assert.Equal(ReplicaAnswers.Written, await RecoveryWriteAsync(replica, 0, "ignored"u8.ToArray()));
            Assert.Equal(ReplicaAnswers.Written, await RecoveryWriteAsync(replica, 200, "historical"u8.ToArray()));
            Assert.Equal("canonical"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, 0, TestContext.Current.CancellationToken));
        }

        await using var reopened = await OpenAsync(directory.Path);
        Assert.Equal("canonical"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, TestContext.Current.CancellationToken));
        Assert.Equal("historical"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(reopened, 200, TestContext.Current.CancellationToken));
        Assert.True((await reopened.ReadTailAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
    }

    /// <summary>
    /// ledger-replicas.md §3.2: an empty payload is never DATA, so every write rejects it, writing nothing.
    /// </summary>
    [Fact]
    public async Task AnEmptyPayloadIsRejectedByEveryWrite()
    {
        using var directory = new TemporaryDirectory();
        await using var replica = await CreateAsync(directory.Path);
        await Assert.ThrowsAsync<ArgumentException>(async () => await WriteAsync(replica, 0, []));
        await Assert.ThrowsAsync<ArgumentException>(async () => await RecoveryWriteAsync(replica, 0, []));
        Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken)).Case);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Every handle over a replica shares its slots, and every replica of a node shares the store's one WAL. Creating a
    /// live replica again leaves it unchanged, and disposing a replica releases only that replica: the store and the other
    /// replicas stay open (ledgerstore.md §2.1, §2.2).
    /// </summary>
    [Fact]
    public async Task ReplicasOpenedOverOneLedgerShareItsSlotsAndDisposingOneLeavesTheStoreOpen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await using var store = await OpenStoreAsync(directory.Path, create: true);
        await store.CreateReplicaAsync(LedgerReplicaTestData.Ledger, cancellationToken);
        await using var second = Assert.IsType<LedgerStoreReplica>(store.OpenReplica(LedgerReplicaTestData.Ledger, Window64));
        await using (var first = Assert.IsType<LedgerStoreReplica>(store.OpenReplica(LedgerReplicaTestData.Ledger, Window64)))
        {
            Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(first, 0, "shared"u8.ToArray())).Case);
            await first.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await first.ReadAsync(Slot.Zero, cancellationToken));
        }

        Assert.Same(store, second.Store);
        Assert.Equal("shared"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(second, 0, cancellationToken));
        await store.CreateReplicaAsync(LedgerReplicaTestData.Ledger, cancellationToken);
        Assert.Equal("shared"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(second, 0, cancellationToken));
        Assert.Equal([LedgerReplicaTestData.Ledger], store.Replicas);

        await store.CreateReplicaAsync(new(LedgerReplicaTestData.Ledger.Value + 1), cancellationToken);
        await using var other = Assert.IsType<LedgerStoreReplica>(store.OpenReplica(new(LedgerReplicaTestData.Ledger.Value + 1), Window64));
        Assert.Same(second.Store, other.Store);
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(other, 0, cancellationToken));
        Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(other, 0, "other"u8.ToArray())).Case);
        Assert.Equal("shared"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(second, 0, cancellationToken));
        Assert.NotEqual(second.Ledger.Handle, other.Ledger.Handle);
    }

    /// <summary>
    /// LR-I6: a replica records the window of the request which created it, durably, and refuses every operation of a
    /// handle with another window before it has any effect: it neither writes, seals, nor retains a contiguous tail, and
    /// the refusal survives a restart.
    /// </summary>
    [Fact]
    public async Task AReplicaRecordsItsWindowAndRefusesAnotherWithoutEffect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        await using (var replica = await root.CreateReplicaAsync(Window4))
        {
            using var payload = Payload([1]);
            Assert.Equal(ReplicaWriteCase.Written, (await replica.WriteAsync(Slot.Zero, payload, new Slot(1), recovery: false, cancellationToken)).Case);
            await using var wide = replica.Store.OpenReplica(LedgerReplicaTestData.Ledger, Window64);
            await AssertRefusesAnotherWindowAsync(wide);

            Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 1, cancellationToken));
            Assert.Equal(new LedgerReplicaTail(new Slot(1), IsSealed: false), await replica.ReadTailAsync(cancellationToken));
            Assert.Equal(4, replica.Ledger.Entry.Window);
        }

        await using var reopened = await root.OpenReplicaAsync(Window4);
        await using (var wide = reopened.Store.OpenReplica(LedgerReplicaTestData.Ledger, Window64))
        {
            await AssertRefusesAnotherWindowAsync(wide);
        }

        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await reopened.ReadTailAsync(cancellationToken));
    }

    /// <summary>
    /// LR-I6: after a restart a replica retains <c>max(0, h + 1 − K)</c> for its highest slot <c>h</c> and the window
    /// <c>K</c> it recorded, whatever it retained before: a replica created with <c>K = 4</c> and restarted refuses a replica
    /// with <c>K′ = 1</c>, and the replica of its own window reads the rebuilt tail. A replica which holds no DATA retains 0.
    /// </summary>
    [Fact]
    public async Task ARestartRebuildsTheRetainedTailFromTheHighestSlotAndTheRecordedWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        var empty = new LedgerId(LedgerReplicaTestData.Ledger.Value + 1);
        var replica = await root.CreateReplicaAsync(Window4);
        for (var address = 0L; address < 10; address++)
        {
            using var payload = Payload([(byte)address]);
            Assert.Equal(ReplicaWriteCase.Written, (await replica.WriteAsync(new Slot(address), payload, new Slot(address), recovery: false, cancellationToken)).Case);
        }

        await using (var sealer = replica.Store.OpenReplica(empty, Window4))
        {
            Assert.True((await sealer.ReadTailAsync(new Slot(5), seal: true, cancellationToken)).Tail.IsSealed);
        }

        Assert.Equal(new LedgerReplicaTail(new Slot(12), IsSealed: false), await replica.ReadTailAsync(new Slot(12), seal: false, cancellationToken));
        await root.CrashAsync(replica, operatingSystem: false);

        await using var reopened = await root.OpenReplicaAsync(Window4);
        await using (var narrow = reopened.Store.OpenReplica(LedgerReplicaTestData.Ledger, new LedgerReplicaOptions { WindowSize = 1 }))
        {
            _ = await Assert.ThrowsAsync<ArgumentException>(async () => await narrow.ReadTailAsync(Slot.Zero, seal: false, cancellationToken));
        }

        Assert.Equal(new LedgerReplicaTail(new Slot(6), IsSealed: false), await reopened.ReadTailAsync(cancellationToken));
        await using var sealedEmpty = reopened.Store.OpenReplica(empty, Window4);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), await sealedEmpty.ReadTailAsync(cancellationToken));
    }

    /// <summary>
    /// LR-I6, and through it LG-I4: a replica's window is durable in its create record and in its catalog entry
    /// (ledgerstore.md §7.2, §7.3, §8.3). A restart takes it from the create record when the newest catalog checkpoint is
    /// cut below that record, and from the catalog checkpoint's entry when it is cut past it. Either way a replica created
    /// with <c>K = 4</c> whose DATA lies at slots 4 to 7, so <c>h = 7</c>, refuses every operation of a replica with
    /// <c>K′ = 1</c> without effect: its retained contiguous tail, its slots, and the WAL are unchanged. The replica of its
    /// own window reads the tail rebuilt as <c>max(0, h + 1 − K) = 4</c>, never the 7 which <c>K′</c> would give and from
    /// which a recovery would pass over the DATA at 4 to 6.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplicasWindowSurvivesARestartFromItsCreateRecordOrItsCatalogEntry(bool catalogCheckpointed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window4);
        for (var address = 4L; address < 8; address++)
        {
            Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(replica, address, [(byte)address])).Case);
        }

        if (catalogCheckpointed)
        {
            await replica.Store.CheckpointCatalogPastWritesAsync();
        }

        var create = await CreateRecordPositionAsync(replica.Store, LedgerReplicaTestData.Ledger);
        await root.CrashAsync(replica, operatingSystem: false);

        // On the frozen clock no catalog checkpoint falls due, so without the requested one the newest is the empty
        // catalog's, written as the store was created.
        var newest = NewestCatalogCheckpoint(root.Storage);
        Assert.Equal(catalogCheckpointed, newest.Cut > create);
        Assert.Equal(catalogCheckpointed ? Window4.WindowSize : null, newest.Catalog.Find(LedgerReplicaTestData.Ledger)?.Window);

        await using var reopened = await root.OpenReplicaAsync(Window4);
        Assert.Equal(Window4.WindowSize, reopened.Ledger.Entry.Window);
        Assert.Equal(new Slot(4), reopened.Ledger.RetainedContiguousTail);
        var frontier = reopened.Store.Wal.DurableFrontier;
        await using (var narrow = reopened.Store.OpenReplica(LedgerReplicaTestData.Ledger, new LedgerReplicaOptions { WindowSize = 1 }))
        {
            await AssertRefusesAnotherWindowAsync(narrow);
        }

        Assert.Equal(new Slot(4), reopened.Ledger.RetainedContiguousTail);
        Assert.Equal(new LedgerReplicaTail(new Slot(4), IsSealed: false), await reopened.ReadTailAsync(Slot.Zero, seal: false, cancellationToken));
        Assert.Equal(frontier, reopened.Store.Wal.DurableFrontier);
        for (var address = 0L; address < 8; address++)
        {
            var value = await LedgerReplicaTestData.ReadBytesAsync(reopened, address, cancellationToken);
            if (address < 4)
            {
                Assert.Null(value);
            }
            else
            {
                Assert.NotNull(value);
                Assert.Equal([(byte)address], value);
            }
        }
    }

    /// <summary>
    /// LR-I7, LR-I8: a seal of a replica the store neither lists nor dropped creates it sealed, while a contiguous-tail
    /// read of it answers its argument, unsealed, and creates nothing; the created replica refuses writer writes and
    /// takes recovery writes.
    /// </summary>
    [Fact]
    public async Task ASealOfAnUnlistedReplicaCreatesItSealed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        await using (var store = await root.OpenAsync(create: true))
        {
            await using var replica = store.OpenReplica(LedgerReplicaTestData.Ledger, Window64);
            Assert.Equal(new LedgerReplicaTail(new Slot(3), IsSealed: false), await replica.ReadTailAsync(new Slot(3), seal: false, cancellationToken));
            Assert.Empty(store.Replicas);

            var sealedTail = (await replica.ReadTailAsync(new Slot(2), seal: true, cancellationToken)).Tail;
            Assert.Equal((new Slot(2), true), (sealedTail.ContiguousTail, sealedTail.IsSealed));
            Assert.Equal([LedgerReplicaTestData.Ledger], store.Replicas);
            Assert.True(replica.Ledger.IsSealed);
            Assert.Equal(ReplicaAnswers.Sealed, await WriteAsync(replica, 0, [1]));
            Assert.Equal(ReplicaAnswers.Written, await RecoveryWriteAsync(replica, 0, [2]));
        }

        await using var reopened = await root.OpenReplicaAsync(Window64);
        Assert.True((await reopened.ReadTailAsync(cancellationToken)).Tail.IsSealed);
        Assert.Equal([2], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
    }

    /// <summary>
    /// LS-I11: the seal is a record in the WAL, so it survives a crash, whether recovery replays its record or a slot-index
    /// checkpoint whose root records it (ledgerstore.md §7.4, §8.7); the recovered replica refuses writer writes.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ASealSurvivesACrash(bool checkpointed, bool operatingSystem)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(replica, 0, [1])).Case);
        Assert.True((await replica.SealAsync(cancellationToken)).Tail.IsSealed);
        if (checkpointed)
        {
            await SettledCheckpointAsync(replica);
            Assert.True(replica.Ledger.CheckpointsWritten > 0);
        }

        await root.CrashAsync(replica, operatingSystem);

        await using var reopened = await root.OpenReplicaAsync(Window64);
        Assert.Equal(checkpointed ? LedgerStoreReplayPath.Checkpoint : LedgerStoreReplayPath.FullScan, reopened.Ledger.Recovery.Path);
        Assert.True(reopened.Ledger.IsSealed);
        Assert.True((await reopened.ReadTailAsync(cancellationToken)).Tail.IsSealed);
        Assert.Equal(ReplicaAnswers.Sealed, await WriteAsync(reopened, 1, [2]));
        Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
    }

    /// <summary>
    /// Conventions.md §14: a failed WAL write is reported, which stops the process. The write which met it fails without
    /// an answer, and so does every later operation which reaches the stopped storage; the restarted process recovers
    /// every write answered before the failure from the WAL.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AFailedWalWriteIsReportedAndTheRestartedProcessRecoversEveryAnsweredWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(replica, 0, "acknowledged"u8.ToArray())).Case);
        var reported = FatalErrors.ReportedCount;
        root.Storage.WriteFault = static (_, _, _, _) => new IOException("injected write failure");

        var write = await Assert.ThrowsAnyAsync<IOException>(async () => await WriteAsync(replica, 1, [1]));
        Assert.Contains(Chain(write), static exception => exception.Message.Contains("injected write failure", StringComparison.Ordinal));
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleWriteGather, SimulatedNodeRoot.WalPath);

        // The WAL's failure stopped the store, which answers nothing more.
        await StoppedStore.AssertAnswersNothingAsync(replica.Store, replica.Replica, Assert.IsAssignableFrom<IOException>(replica.Store.Stopped));
        _ = await Assert.ThrowsAnyAsync<IOException>(async () => await WriteAsync(replica, 2, [2]));
        _ = await Assert.ThrowsAnyAsync<IOException>(async () => await replica.SealAsync(cancellationToken));

        await root.CrashAsync(replica, operatingSystem: false);
        root.Storage.WriteFault = null;
        await using var reopened = await root.OpenReplicaAsync(Window64);
        Assert.Equal("acknowledged"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
        Assert.False((await reopened.ReadTailAsync(cancellationToken)).Tail.IsSealed);
        Assert.Equal([LedgerReplicaTestData.Ledger], reopened.Store.Replicas);
    }

    /// <summary>
    /// Conventions.md §14: a durable slot record whose publication throws leaves the slot table behind the WAL, so the
    /// store reports the defect once, as <c>LedgerStoreSlotPublication</c>, which stops the process,
    /// before the write answers or releases its slot claim and admission; the WAL finds it reported and reports no second
    /// failure. The write answers the defect, the stopped store answers nothing more, and the restarted process reads the
    /// durable write back.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ASlotRecordWhosePublicationThrowsIsReportedOnceBeforeTheWriteAnswers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(replica, 0, [1])).Case);
        var defect = new InvalidOperationException("injected slot publication defect");
        replica.Store.SlotPublicationFault = (ledger, slot) => ledger == LedgerReplicaTestData.Ledger && slot == 1 ? defect : null;
        var reported = FatalErrors.ReportedCount;
        var reportedAtAnswer = -1L;
        var write = WriteAsync(replica, 1, [2]);
        var answered = write.ContinueWith(
            _ => reportedAtAnswer = FatalErrors.ReportedCount,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => write));
        await answered;
        Assert.Equal(reported + 1, reportedAtAnswer);
        _ = StorageFailures.TakeDefect(reported, "LedgerStoreSlotPublication", defect);
        await StoppedStore.AssertAnswersNothingAsync(replica.Store, replica.Replica, defect);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await replica.Store.DisposeAsync()));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var reopened = await root.OpenReplicaAsync(Window64);
        Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
        Assert.Equal([2], await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, cancellationToken));
    }

    /// <summary>
    /// A slot whose record lies in a damaged range of the replayed WAL reports corruption, never Missing, until a
    /// recovery write re-establishes it (LR-I2, LR-I5); so does every slot without a record, which the range may have
    /// held, and a writer write refuses it. Slots re-established after the damage read normally (LS-I5). The store lists
    /// the replica corrupt from its open, and after a restart, though recovery writes re-establish slots.
    /// </summary>
    [Fact]
    public async Task DamagedRecordsInTheReplayedSuffix_ReportCorruptionUntilRecoveredAndNeverMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated(Options(SimulatedNodeRoot.Path) with { PageSize = 512, WriteWindowPages = 4 });
        var replica = await root.CreateReplicaAsync(Window64);
        await WriteDamageScenarioAsync(replica);

        // A crash before any slot-index checkpoint leaves recovery the whole WAL to replay; the catalog checkpoint's cut
        // lies past the damage, so it faults the replica rather than refusing the open (LS-I5).
        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: false);

        // Damage the page holding slot 1's record, after the create record's page and slot 0's, which W pages of later
        // records prove durable.
        root.Storage.Damage(SimulatedNodeRoot.WalPath, 1, 512 * 3, LedgerReplicaTestData.CreatePayload(512, 77));
        var reopened = await root.OpenReplicaAsync(Window64);
        Assert.Equal(LedgerStoreReplayPath.FullScan, reopened.Ledger.Recovery.Path);
        Assert.Equal([reopened.LedgerId], reopened.Store.ListCorruptReplicas());

        // The recovered slot table holds DATA in the slots re-established after the damage and faults the rest.
        var (data, faulty) = await reopened.Ledger.SlotStatesAsync(10, cancellationToken);
        Assert.Equal([2L, 4, 5, 6, 7, 9], data);
        Assert.Equal([0L, 1, 3, 8], faulty);

        foreach (var slot in new long[] { 1, 0, 3, 42 })
        {
            await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(slot), cancellationToken));
        }

        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await WriteAsync(reopened, 1, [1]));
        Assert.Equal(LedgerReplicaTestData.CreatePayload(400, 2), await LedgerReplicaTestData.ReadBytesAsync(reopened, 2, cancellationToken));
        Assert.Equal([9], await LedgerReplicaTestData.ReadBytesAsync(reopened, 9, cancellationToken));

        Assert.Equal(ReplicaAnswers.Written, await RecoveryWriteAsync(reopened, 1, LedgerReplicaTestData.CreatePayload(400, 1)));
        Assert.Equal(LedgerReplicaTestData.CreatePayload(400, 1), await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, cancellationToken));

        // The taint survives a checkpoint and a restart until each slot is re-established.
        await SettledCheckpointAsync(reopened);
        await root.CrashAsync(reopened, operatingSystem: false);
        await using var third = await root.OpenReplicaAsync(Window64);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, third.Ledger.Recovery.Path);
        Assert.Equal([third.LedgerId], third.Store.ListCorruptReplicas());
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await third.ReadAsync(new Slot(0), cancellationToken));
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await third.ReadAsync(new Slot(3), cancellationToken));
        Assert.Equal(LedgerReplicaTestData.CreatePayload(400, 1), await LedgerReplicaTestData.ReadBytesAsync(third, 1, cancellationToken));
        Assert.Equal([9], await LedgerReplicaTestData.ReadBytesAsync(third, 9, cancellationToken));
    }

    /// <summary>
    /// Below a checkpoint the slot table knows which page holds each slot's record, so damage faults only the slots a
    /// read finds on a damaged page, which a writer write refuses and a recovery write re-establishes (LS-I5, LR-I5). The
    /// store lists the replica corrupt once a read finds the damage, and no longer once the replica is dropped.
    /// </summary>
    [Fact]
    public async Task DamagedRecordsBelowACheckpoint_FaultOnlyTheSlotsRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated(Options(SimulatedNodeRoot.Path) with { PageSize = 512, WriteWindowPages = 4 });
        await using (var replica = await root.CreateReplicaAsync(Window64))
        {
            await WriteDamageScenarioAsync(replica);
        }

        root.Storage.Damage(SimulatedNodeRoot.WalPath, 1, 512 * 3, LedgerReplicaTestData.CreatePayload(512, 77));
        await using var reopened = await root.OpenReplicaAsync(Window64);
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.Checkpoint, reopened.Ledger.CheckpointGeneration, 0), reopened.Ledger.Recovery);

        // The slot table, which reads no record, reports DATA in every written slot until a read finds the damage.
        var (data, faulty) = await reopened.Ledger.SlotStatesAsync(10, cancellationToken);
        Assert.Equal([0L, 1, 2, 4, 5, 6, 7, 9], data);
        Assert.Empty(faulty);
        Assert.Empty(reopened.Store.ListCorruptReplicas());

        Assert.Equal(LedgerReplicaTestData.CreatePayload(400, 0), await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 3, cancellationToken));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 42, cancellationToken));
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(1), cancellationToken));
        Assert.Equal([reopened.LedgerId], reopened.Store.ListCorruptReplicas());
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await WriteAsync(reopened, 1, [1]));
        (data, faulty) = await reopened.Ledger.SlotStatesAsync(10, cancellationToken);
        Assert.Equal([0L, 2, 4, 5, 6, 7, 9], data);
        Assert.Equal([1L], faulty);

        Assert.Equal(ReplicaAnswers.Written, await RecoveryWriteAsync(reopened, 1, LedgerReplicaTestData.CreatePayload(400, 1)));
        Assert.Equal(LedgerReplicaTestData.CreatePayload(400, 1), await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, cancellationToken));

        await reopened.Store.DropReplicaAsync(reopened.LedgerId, cancellationToken);
        Assert.Empty(reopened.Store.ListCorruptReplicas());
    }

    /// <summary>
    /// LS-I5: a fault a read found is listed again after a restart at once when a checkpoint kept it, and otherwise once a
    /// read meets the damaged page again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFaultAReadFoundIsListedAfterARestart(bool checkpointed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated(Options(SimulatedNodeRoot.Path) with { PageSize = 512, WriteWindowPages = 4 });
        await using (var replica = await root.CreateReplicaAsync(Window64))
        {
            await WriteDamageScenarioAsync(replica);
        }

        root.Storage.Damage(SimulatedNodeRoot.WalPath, 1, 512 * 3, LedgerReplicaTestData.CreatePayload(512, 77));
        var reopened = await root.OpenReplicaAsync(Window64);
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(1), cancellationToken));
        if (checkpointed)
        {
            await SettledCheckpointAsync(reopened);
        }

        await root.CrashAsync(reopened, operatingSystem: false);
        await using var restarted = await root.OpenReplicaAsync(Window64);
        Assert.Equal(checkpointed ? [restarted.LedgerId] : [], restarted.Store.ListCorruptReplicas());

        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await restarted.ReadAsync(new Slot(1), cancellationToken));
        Assert.Equal([restarted.LedgerId], restarted.Store.ListCorruptReplicas());
    }

    /// <summary>
    /// Conventions.md §14, LS-I5: a failed point read is no damage: it stops the store, which lists nothing, and the
    /// restarted process reads the slot and lists no replica corrupt.
    /// </summary>
    [Fact]
    public async Task AFailedPointReadStopsTheStoreAndIsNotListedCorrupt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated(Options(SimulatedNodeRoot.Path) with { PageSize = 512, WriteWindowPages = 4 });
        await using (var replica = await root.CreateReplicaAsync(Window64))
        {
            await WriteDamageScenarioAsync(replica);
        }

        var reopened = await root.OpenReplicaAsync(Window64);
        var reported = FatalErrors.ReportedCount;
        root.Storage.ReadFault = static (_, file, offset, length) =>
            file == 1 && offset <= 512 * 3 && 512 * 3 < offset + length ? new IOException("injected read failure") : null;

        var failure = await Assert.ThrowsAnyAsync<IOException>(async () => await reopened.ReadAsync(new Slot(1), cancellationToken));
        Assert.Contains(Chain(failure), static exception => exception.Message.Contains("injected read failure", StringComparison.Ordinal));
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleReadScatter, SimulatedNodeRoot.WalPath);
        await StoppedStore.AssertAnswersNothingAsync(reopened.Store, reopened.Replica, Assert.IsAssignableFrom<IOException>(reopened.Store.Stopped));

        await root.CrashAsync(reopened, operatingSystem: false);
        root.Storage.ReadFault = null;
        await using var restarted = await root.OpenReplicaAsync(Window64);
        Assert.Equal(LedgerReplicaTestData.CreatePayload(400, 1), await LedgerReplicaTestData.ReadBytesAsync(restarted, 1, cancellationToken));
        Assert.Empty(restarted.Store.ListCorruptReplicas());
    }

    /// <summary>
    /// A storage read failure while the store opens, whether on a WAL file header or during replay, is reported, which
    /// stops the process (conventions.md §14): the open fails and answers nothing, and the restarted process's open
    /// recovers every acknowledged write.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(512L * 4)]
    public async Task AReadFailureWhileOpening_FailsTheOpenAndALaterOpenRecoversEveryWrite(long faultOffset)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Simulated(Options(SimulatedNodeRoot.Path) with { PageSize = 512 });
        await using (var replica = await root.CreateReplicaAsync(Window64))
        {
            for (var address = 0; address < 20; address++)
            {
                _ = await WriteAsync(replica, address, LedgerReplicaTestData.CreatePayload(300, address));
            }
        }

        var reported = FatalErrors.ReportedCount;
        root.Storage.ReadFault = (_, file, offset, length) =>
            file == 1 && offset <= faultOffset && faultOffset < offset + length ? new IOException("injected read failure") : null;
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () => await root.OpenReplicaAsync(Window64));
        Assert.Contains(Chain(failure), static exception => exception.Message.Contains("injected read failure", StringComparison.Ordinal));
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleReadScatter, SimulatedNodeRoot.WalPath);

        root.Crash(operatingSystem: false);
        root.Storage.ReadFault = null;
        await using var reopened = await root.OpenReplicaAsync(Window64);
        for (var address = 0; address < 20; address++)
        {
            Assert.Equal(LedgerReplicaTestData.CreatePayload(300, address), await LedgerReplicaTestData.ReadBytesAsync(reopened, address, cancellationToken));
        }
    }

    /// <summary>
    /// Cancelling a write after its record is admitted stops only the caller's wait: the record still becomes durable,
    /// and a seal, which waits for every writer write admitted before it, answers only then (LS-I7, LS-I11;
    /// ledgerstore.md §6.6, §8.3).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CancellingAnAdmittedWrite_StopsOnlyTheWaitAndTheWriteStillBecomesDurable()
    {
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        root.Storage.HoldWrites = true;
        using var cancellation = new CancellationTokenSource();
        using var writer = new ArcBufferWriter();
        writer.Write("admitted"u8);
        using var payload = writer.PeekSlice(writer.Length);
        var write = WriteAdmittedAsync();
        await WaitForPendingWriteAsync(root.Storage);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        var seal = replica.SealAsync(TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(seal.IsCompleted);

        root.Storage.HoldWrites = false;
        while (!seal.IsCompleted)
        {
            _ = root.Storage.CompleteWrite();
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.True((await seal).Tail.IsSealed);
        Assert.Equal("admitted"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, 0, TestContext.Current.CancellationToken));
        await replica.DisposeAsync();

        root.Crash(operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window64);
        Assert.Equal("admitted"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, TestContext.Current.CancellationToken));
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), await reopened.ReadTailAsync(TestContext.Current.CancellationToken));

        async Task<ReplicaWriteOutcome> WriteAdmittedAsync() => await replica.WriteAsync(Slot.Zero, payload, cancellation.Token);
    }

    /// <summary>
    /// Stateless replica operations on <see cref="LedgerStore"/> (<see cref="LedgerStore.WriteReplicaAsync"/>,
    /// <see cref="LedgerStore.ReadReplicaAsync(LedgerId, int, Slot, Slot, CancellationToken)"/>,
    /// <see cref="LedgerStore.ReadReplicaAsync(LedgerId, int, Slot, int, int, Slot, CancellationToken)"/>, and
    /// <see cref="LedgerStore.ReadReplicaTailAsync"/>) match <see cref="LedgerStoreReplica"/> across live, sealed,
    /// recovery, absent, dropped, invalid-argument, and disposed-store states without opening a per-call handle.
    /// </summary>
    [Fact]
    public async Task StatelessReplicaOperationsMatchHandleSemanticsAcrossStates()
    {
        var token = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var ledger = LedgerReplicaTestData.Ledger;
        var absentWrite = new LedgerId(ledger.Value + 1);
        var absentSeal = new LedgerId(ledger.Value + 2);

        await using var store = await OpenStoreAsync(directory.Path, create: true);
        await store.CreateReplicaAsync(ledger, token);

        using var payload = ArcBuffer.Wrap("stateless-payload"u8.ToArray());
        Assert.Equal(ReplicaWriteCase.Written, (await store.WriteReplicaAsync(ledger, 64, Slot.Zero, payload, Slot.Zero, recovery: false, token)).Case);

        var whole = await store.ReadReplicaAsync(ledger, 64, Slot.Zero, Slot.Zero, token);
        Assert.True(whole.TryGetValue(out ArcBuffer wholeBytes));
        using (wholeBytes)
        {
            Assert.Equal("stateless-payload"u8.ToArray(), wholeBytes.ToArray());
        }

        var ranged = await store.ReadReplicaAsync(ledger, 64, Slot.Zero, offset: 10, length: 7, Slot.Zero, token);
        Assert.True(ranged.TryGetValue(out ArcBuffer rangedBytes));
        using (rangedBytes)
        {
            Assert.Equal("payload"u8.ToArray(), rangedBytes.ToArray());
        }

        Assert.Equal(ReplicaReadCase.Missing, (await store.ReadReplicaAsync(ledger, 64, new Slot(1), Slot.Zero, token)).Case);

        var sealedTail = await store.ReadReplicaTailAsync(ledger, 64, Slot.Zero, seal: true, token);
        Assert.True(sealedTail.Tail.IsSealed);
        Assert.Equal(ReplicaAnswers.Sealed, await store.WriteReplicaAsync(ledger, 64, new Slot(1), payload, Slot.Zero, recovery: false, token));
        Assert.Equal(ReplicaAnswers.Written, await store.WriteReplicaAsync(ledger, 64, new Slot(1), payload, Slot.Zero, recovery: true, token));

        // Absent ledger: reads and unsealed tail reads answer as empty without creating state; writes and seals create it lazily.
        Assert.Equal(ReplicaReadCase.Missing, (await store.ReadReplicaAsync(absentWrite, 64, Slot.Zero, Slot.Zero, token)).Case);
        Assert.Equal(ReplicaReadCase.Missing, (await store.ReadReplicaAsync(absentWrite, 64, Slot.Zero, offset: 0, length: 4, Slot.Zero, token)).Case);
        var absentTail = await store.ReadReplicaTailAsync(absentWrite, 64, Slot.Zero, seal: false, token);
        Assert.False(absentTail.Tail.IsSealed);
        Assert.Equal(Slot.Zero, absentTail.Tail.ContiguousTail);
        Assert.Equal(ReplicaWriteCase.Written, (await store.WriteReplicaAsync(absentWrite, 64, Slot.Zero, payload, Slot.Zero, recovery: false, token)).Case);

        var lazilySealed = await store.ReadReplicaTailAsync(absentSeal, 64, Slot.Zero, seal: true, token);
        Assert.True(lazilySealed.Tail.IsSealed);
        Assert.Equal(ReplicaAnswers.Sealed, await store.WriteReplicaAsync(absentSeal, 64, Slot.Zero, payload, Slot.Zero, recovery: false, token));

        // Invalid arguments are refused before touching the store.
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.WriteReplicaAsync(ledger, 0, Slot.Zero, payload, Slot.Zero, recovery: false, token).AsTask());
        _ = await Assert.ThrowsAsync<ArgumentException>(() => store.WriteReplicaAsync(default, 64, Slot.Zero, payload, Slot.Zero, recovery: false, token).AsTask());
        _ = await Assert.ThrowsAsync<ArgumentException>(() => store.WriteReplicaAsync(ledger, 64, Slot.Zero, default, Slot.Zero, recovery: false, token).AsTask());
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadReplicaAsync(ledger, 64, Slot.Zero, offset: -1, length: 1, Slot.Zero, token).AsTask());
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadReplicaAsync(ledger, 64, Slot.Zero, offset: 0, length: 0, Slot.Zero, token).AsTask());

        // Dropped ledger answers Dropped.
        await store.DropReplicaAsync(ledger, token);
        Assert.Equal(ReplicaAnswers.Dropped, await store.WriteReplicaAsync(ledger, 64, Slot.Zero, payload, Slot.Zero, recovery: false, token));
        Assert.Equal(ReplicaReadCase.Dropped, (await store.ReadReplicaAsync(ledger, 64, Slot.Zero, Slot.Zero, token)).Case);
        Assert.True((await store.ReadReplicaTailAsync(ledger, 64, Slot.Zero, seal: false, token)).Value is ReplicaWriteOutcome.Dropped);

        // Disposed store throws ObjectDisposedException.
        await store.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => store.WriteReplicaAsync(ledger, 64, Slot.Zero, payload, Slot.Zero, recovery: false, token).AsTask());
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadReplicaAsync(ledger, 64, Slot.Zero, Slot.Zero, token).AsTask());
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadReplicaTailAsync(ledger, 64, Slot.Zero, seal: false, token).AsTask());
    }

    /// <summary>
    /// Disposing the store waits for every admitted write to reach its durable outcome, and every one recovers.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Dispose_WaitsForAdmittedWritesAndTheyRecover()
    {
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        root.Storage.HoldWrites = true;
        var writes = Enumerable.Range(0, 3).Select(address => WriteAsync(replica, address, [(byte)(address + 1)])).ToArray();
        await WaitForPendingWriteAsync(root.Storage);
        var disposal = replica.DisposeAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(disposal.IsCompleted);

        root.Storage.HoldWrites = false;
        while (!disposal.IsCompleted)
        {
            _ = root.Storage.CompleteWrite();
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        await disposal;
        Assert.All(await Task.WhenAll(writes), static result => Assert.Equal(ReplicaWriteCase.Written, result.Case));
        root.Crash(operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window64);
        for (var address = 0; address < 3; address++)
        {
            Assert.Equal(new byte[] { (byte)(address + 1) }, await LedgerReplicaTestData.ReadBytesAsync(reopened, address, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// ledgerstore §8.10: operations concurrent with the store's disposal, each in flight when it begins, answer from a
    /// durable outcome or fail with <see cref="ObjectDisposedException"/> without effect, and one after it fails so; none
    /// reports a fatal error, so a silo whose late work reaches its closed store never stops its process for it. A write
    /// held in the WAL, a seal behind it, a drop and a read which waits for the drop's outcome, a creation behind the drop,
    /// an open, and a listing begin before the disposal; the reopened store then holds every answer that succeeded, and
    /// nothing of an operation which failed.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task OperationsConcurrentWithTheStoresDisposalAnswerDurablyOrFailDisposedWithoutAFatalReport()
    {
        var since = FatalErrors.ReportedCount;
        var token = TestContext.Current.CancellationToken;
        var (dropped, created, opened) = (new LedgerId(8), new LedgerId(9), new LedgerId(10));
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        var store = replica.Store;
        _ = await WriteAsync(replica, 0, [1]);
        _ = await store.CreateLiveAsync(dropped, Window64.WindowSize, token);
        root.Storage.HoldWrites = true;
        var write = WriteAsync(replica, 1, [2]);
        await WaitForPendingWriteAsync(root.Storage);
        var seal = replica.ReadTailAsync(new Slot(1), seal: true, token).AsTask();
        var drop = store.DropReplicaAsync(dropped, token).AsTask();
        await using var droppedReplica = store.OpenReplica(dropped, Window64);
        var read = droppedReplica.ReadAsync(Slot.Zero, Slot.Zero, token).AsTask();
        var create = store.CreateLiveAsync(created, Window64.WindowSize, token).AsTask();
        await using var openedReplica = store.OpenReplica(opened, Window64);
        var list = ListAsync();
        Assert.False(read.IsCompleted);
        Assert.False(create.IsCompleted);

        var disposal = store.DisposeAsync().AsTask();
        root.Storage.HoldWrites = false;
        while (!disposal.IsCompleted)
        {
            _ = root.Storage.CompleteWrite();
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, token);
        }

        await disposal;
        var (wrote, writeAnswer) = await AnswerAsync(write);
        var written = wrote && writeAnswer.Case == ReplicaWriteCase.Written;
        var (checkedSeal, sealAnswer) = await AnswerAsync(seal);
        var sealedAnswer = checkedSeal && sealAnswer.Value is LedgerReplicaTail { IsSealed: true };
        var dropDurable = Outcome(await Record.ExceptionAsync(() => drop));
        var (readAnswered, readAnswer) = await AnswerAsync(read);
        if (readAnswered)
        {
            Assert.Equal(ReplicaWriteOutcome.Dropped.Instance, readAnswer.Value);
            dropDurable = true;
        }

        var createDurable = Outcome(await Record.ExceptionAsync(() => create));
        var (listed, listing) = await AnswerAsync(list);
        if (listed)
        {
            Assert.Contains(LedgerReplicaTestData.Ledger, listing);
        }

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await openedReplica.ReadAsync(Slot.Zero, Slot.Zero, token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replica.ReadAsync(Slot.Zero, new Slot(1), token));
        using (var late = Payload([3]))
        {
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replica.WriteAsync(new Slot(2), late, new Slot(1), recovery: false, token));
        }

        Assert.Empty(FatalErrors.TakeMine(since));

        // Every answer which succeeded is durable, and an operation which failed left nothing: the reopened store shows each.
        await using var reopened = await root.OpenReplicaAsync(Window64);
        var replicas = await reopened.Store.ListReplicasAsync(token);
        Assert.Equal(written ? [2] : null, await LedgerReplicaTestData.ReadBytesAsync(reopened, 1, token));
        if (checkedSeal)
        {
            Assert.Equal(sealedAnswer, (await reopened.ReadTailAsync(new Slot(1), seal: false, token)).Value is LedgerReplicaTail { IsSealed: true });
        }
        else
        {
            Assert.True((await reopened.ReadTailAsync(new Slot(1), seal: false, token)).Value is LedgerReplicaTail { IsSealed: false });
        }

        Assert.Equal(!dropDurable, replicas.Contains(dropped));
        Assert.Equal(createDurable, replicas.Contains(created));
        Assert.DoesNotContain(opened, replicas);

        async Task<IReadOnlyList<LedgerId>> ListAsync() => await store.ListReplicasAsync(token);

        // Returns whether the operation answered, and its answer; a failure must be the disposal's, without effect.
        static async Task<(bool Answered, T Answer)> AnswerAsync<T>(Task<T> operation)
        {
            try
            {
                return (true, await operation);
            }
            catch (ObjectDisposedException)
            {
                return (false, default!);
            }
        }

        static bool Outcome(Exception? failure)
        {
            if (failure is null)
            {
                return true;
            }

            _ = Assert.IsType<ObjectDisposedException>(failure);
            return false;
        }

        static ArcBuffer Payload(byte[] bytes)
        {
            using var writer = new ArcBufferWriter();
            writer.Write(bytes);
            return writer.ConsumeSlice(bytes.Length);
        }
    }

    [Fact]
    public async Task APointReadOfADamagedRecordReportsCorruption()
    {
        var root = Simulated(Options(SimulatedNodeRoot.Path) with { PageSize = 512 });
        await using var replica = await root.CreateReplicaAsync(Window64);
        _ = await WriteAsync(replica, 0, LedgerReplicaTestData.CreatePayload(400, 0));

        // Slot 0's record follows the create record's page.
        root.Storage.Damage(SimulatedNodeRoot.WalPath, 1, 512 * 2, LedgerReplicaTestData.CreatePayload(512, 77));

        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await replica.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken));

        // A contiguous-tail read inspects no slot.
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A quorum of 3 over 5 replicas, two of them unavailable, writes, recovers, and seals over the three LedgerStore
    /// replicas (LP-I4, LP-I6, LP-I7); each replica keeps the DATA and the seal across a reopen, and rebuilds its
    /// retained contiguous tail (LR-I6).
    /// </summary>
    [Fact]
    public async Task ThreeOfFiveWithTwoUnavailableReplicas_PreservesDataAndSealAcrossReopen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var paths = new[] { "first", "second", "third" }.Select(name => Path.Combine(directory.Path, name)).ToArray();
        var payloads = Enumerable.Range(0, 3).Select(static index => LedgerReplicaTestData.CreatePayload(4_103 + index * 127, seed: 91 + index)).ToArray();
        await using (var first = await CreateAsync(paths[0]))
        await using (var second = await CreateAsync(paths[1]))
        await using (var third = await CreateAsync(paths[2]))
        await using (var quorum = LedgerTestData.Create([first.Replica, Unavailable(), second.Replica, Unavailable(), third.Replica], 3, Window64, new FakeTimeProvider()))
        {
            for (var address = 0; address < payloads.Length; address++)
            {
                using var payload = Payload(payloads[address]);
                Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(address), payload, new Slot(address), cancellationToken)).Case);
            }

            Assert.Equal(new Slot(3), (await quorum.RecoverAsync(Slot.Zero, cancellationToken)).End);
            Assert.True((await quorum.ReadTailAsync(cancellationToken)).Tail.IsSealed);
            using var late = Payload([1]);
            Assert.Equal(ReplicaWriteCase.Sealed, (await quorum.WriteAsync(new Slot(3), late, new Slot(3), cancellationToken)).Case);
        }

        foreach (var path in paths)
        {
            await using var reopened = await OpenAsync(path);
            for (var address = 0; address < payloads.Length; address++)
            {
                Assert.Equal(payloads[address], await LedgerReplicaTestData.ReadBytesAsync(reopened, address, cancellationToken));
            }

            Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 3, cancellationToken));
            Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), await reopened.ReadTailAsync(cancellationToken));
        }
    }

    /// <summary>
    /// LS-I7 (#462): concurrent writes to the same slot on distinct ledgers proceed concurrently into the WAL rather
    /// than serializing behind one another.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DistinctLedgersWritingTheSameSlotProceedConcurrently()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Simulated();
        await using var store = await root.OpenAsync(create: true);
        var firstId = new LedgerId(10);
        var secondId = new LedgerId(11);
        _ = await store.CreateLiveAsync(firstId, Window64.WindowSize, token);
        _ = await store.CreateLiveAsync(secondId, Window64.WindowSize, token);
        await using var first = store.OpenReplica(firstId, Window64);
        await using var second = store.OpenReplica(secondId, Window64);

        root.Storage.HoldWrites = true;
        var writeFirst = WriteAsync(first, 0, [1, 2]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => store.Wal.OutstandingRecords == 1, "first ledger's write is held in the WAL");

        var writeSecond = WriteAsync(second, 0, [3, 4]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => store.Wal.OutstandingRecords == 2, "second ledger's write proceeds concurrently into the WAL");

        root.Storage.HoldWrites = false;
        while (root.Storage.CompleteWrite())
        {
        }

        Assert.Equal(ReplicaWriteCase.Written, (await writeFirst).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await writeSecond).Case);
        Assert.Equal([1, 2], await LedgerReplicaTestData.ReadBytesAsync(first, 0, token));
        Assert.Equal([3, 4], await LedgerReplicaTestData.ReadBytesAsync(second, 0, token));
    }

    /// <summary>
    /// LS-I7 (#462): concurrent writes to slots 1,024 apart on the same ledger proceed concurrently into the WAL
    /// rather than colliding on a fixed-size stripe table.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SameLedgerWrites1024SlotsApartProceedConcurrently()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Simulated();
        await using var replica = await root.CreateReplicaAsync(Window64);

        root.Storage.HoldWrites = true;
        var writeLow = WriteAsync(replica, 0, [10]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.OutstandingRecords == 1, "slot 0's write is held in the WAL");

        var writeHigh = WriteAsync(replica, 1024, [20]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.OutstandingRecords == 2, "slot 1024's write proceeds concurrently into the WAL");

        root.Storage.HoldWrites = false;
        while (root.Storage.CompleteWrite())
        {
        }

        Assert.Equal(ReplicaWriteCase.Written, (await writeLow).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await writeHigh).Case);
        Assert.Equal([10], await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));
        Assert.Equal([20], await LedgerReplicaTestData.ReadBytesAsync(replica, 1024, token));
    }

    /// <summary>
    /// LS-I7: concurrent writer and recovery writes to the exact same <c>(ledger, slot)</c> serialize strictly; the
    /// contending writes wait until the first is durable and answer <see cref="ReplicaAnswers.Written"/> without
    /// appending a second WAL record.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SameSlotWritesSerializeWithoutDuplicateWalRecords()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Simulated();
        await using var replica = await root.CreateReplicaAsync(Window64);

        // Writer write held; duplicate writer write and recovery write wait and append no second record.
        root.Storage.HoldWrites = true;
        var owner = WriteAsync(replica, 0, [1]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.OutstandingRecords == 1, "owner write is held in the WAL");

        var duplicateWriter = WriteAsync(replica, 0, [2]);
        var contendingRecovery = RecoveryWriteAsync(replica, 0, [3]);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
        Assert.False(duplicateWriter.IsCompleted || contendingRecovery.IsCompleted, "Contending writes answered before the owning write was durable.");
        Assert.Equal(1, replica.Store.Wal.OutstandingRecords);
        Assert.Equal(1, root.Storage.PendingWriteCount);

        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.Equal(ReplicaWriteCase.Written, (await owner).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await duplicateWriter).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await contendingRecovery).Case);
        Assert.Equal([1], await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));

        // Recovery write held; writer write and another recovery write wait and append no second record.
        root.Storage.HoldWrites = true;
        var recoveryOwner = RecoveryWriteAsync(replica, 1, [4]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.OutstandingRecords == 1, "recovery owner write is held in the WAL");

        var racingWriter = WriteAsync(replica, 1, [5]);
        var racingRecovery = RecoveryWriteAsync(replica, 1, [6]);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
        Assert.False(racingWriter.IsCompleted || racingRecovery.IsCompleted, "Contending writes answered before the recovery write was durable.");
        Assert.Equal(1, replica.Store.Wal.OutstandingRecords);
        Assert.Equal(1, root.Storage.PendingWriteCount);

        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.Equal(ReplicaWriteCase.Written, (await recoveryOwner).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await racingWriter).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await racingRecovery).Case);
        Assert.Equal([4], await LedgerReplicaTestData.ReadBytesAsync(replica, 1, token));

        await replica.Store.WaitForFrontierAsync();
        var slotRecords = new List<long>();
        await foreach (var entry in replica.Store.Wal.ReadAllAsync(default, token))
        {
            if (entry.TryGetValue(out WalRecord record)
                && LedgerRecords.Classify(record.Payload, replica.Store.Catalog.NextHandle, out var handle, out var slot) == RecordClass.Slot
                && handle == replica.Ledger.Handle)
            {
                slotRecords.Add(slot);
            }
        }

        Assert.Equal([0L, 1L], slotRecords);
    }

    /// <summary>
    /// LS-I7: cancelling a caller awaiting an in-flight slot claim neither disrupts the owning write nor loses the
    /// wakeup for another concurrent waiter on the same slot.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CancellingAContendingSlotWaiterPreservesTheOwnerAndSiblingWaiters()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Simulated();
        await using var replica = await root.CreateReplicaAsync(Window64);

        root.Storage.HoldWrites = true;
        var owner = WriteAsync(replica, 0, [7]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.OutstandingRecords == 1, "owner write is held in the WAL");

        using var cancelWaiter = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelledWaiter = WriteWithTokenAsync(replica, 0, [8], cancelWaiter.Token);
        var siblingWaiter = WriteAsync(replica, 0, [9]);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
        Assert.False(cancelledWaiter.IsCompleted || siblingWaiter.IsCompleted);

        await cancelWaiter.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        Assert.False(owner.IsCompleted);
        Assert.False(siblingWaiter.IsCompleted);
        Assert.Equal(1, replica.Store.Wal.OutstandingRecords);

        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.Equal(ReplicaWriteCase.Written, (await owner).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await siblingWaiter).Case);
        Assert.Equal([7], await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));

        // Releasing the slot left no stale claim behind: a follow-up write to the same and another slot succeed.
        Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(replica, 0, [10])).Case);
        Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(replica, 1, [11])).Case);

        static async Task<ReplicaWriteOutcome> WriteWithTokenAsync(ILedgerReplica target, long address, byte[] bytes, CancellationToken cancellationToken)
        {
            using var payload = Payload(bytes);
            return await target.WriteAsync(new Slot(address), payload, cancellationToken);
        }
    }

    /// <summary>
    /// LS-I7, LS-I10, conventions.md §14: when the owning write of a slot fails in the WAL or stops on a publication
    /// defect, contending waiters on that slot are woken cleanly and observe the store's stop failure rather than
    /// hanging or appending a second record.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOwningWriteReleasesContendingSlotWaitersWithTheFailure(bool publicationDefect)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Simulated();
        var replica = await root.CreateReplicaAsync(Window64);
        var reported = FatalErrors.ReportedCount;
        var defect = new InvalidOperationException("injected contending slot publication defect");
        if (publicationDefect)
        {
            replica.Store.SlotPublicationFault = (ledger, slot) => ledger == LedgerReplicaTestData.Ledger && slot == 0 ? defect : null;
        }

        root.Storage.HoldWrites = true;
        var blocker = WriteAsync(replica, 1, [9]);
        await WaitForPendingWriteAsync(root.Storage);

        var owner = WriteAsync(replica, 0, [1]);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.OutstandingRecords == 2, "owner write is queued in the WAL behind the held flush");

        var waiter = WriteAsync(replica, 0, [2]);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, token);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(2, replica.Store.Wal.OutstandingRecords);

        if (!publicationDefect)
        {
            root.Storage.WriteFault = static (_, _, _, _) => new IOException("injected contending slot write failure");
        }

        root.Storage.HoldWrites = false;
        Assert.True(root.Storage.CompleteWrite());
        Assert.Equal(ReplicaWriteCase.Written, (await blocker).Case);

        if (publicationDefect)
        {
            Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => owner));
            Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => waiter));
            _ = StorageFailures.TakeDefect(reported, "LedgerStoreSlotPublication", defect);
            Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await replica.Store.DisposeAsync()));
        }
        else
        {
            var ownerFailure = await Assert.ThrowsAnyAsync<IOException>(() => owner);
            var waiterFailure = await Assert.ThrowsAnyAsync<IOException>(() => waiter);
            Assert.Same(ownerFailure, waiterFailure);
            _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleWriteGather, SimulatedNodeRoot.WalPath);
            _ = await Assert.ThrowsAnyAsync<IOException>(async () => await replica.Store.DisposeAsync());
        }
    }

    /// <summary>
    /// Returns the options of a node root at <paramref name="path"/> with 1 MiB WAL files, unpreallocated, so the WAL
    /// never paces file preparation by the store's clock.
    /// </summary>
    internal static LedgerStoreOptions Options(string path) => new() { DirectoryPath = path, FileSize = 1 << 20, PreallocateFiles = false };

    /// <summary>
    /// Returns a simulated node root with default checkpoint options.
    /// </summary>
    internal static SimulatedNodeRoot Simulated(LedgerStoreOptions? options = null, SimulatedWalStorage? storage = null) =>
        new(options ?? Options(SimulatedNodeRoot.Path), new LedgerStoreCheckpointOptions(), storage);

    /// <summary>
    /// Opens the store of the real node root at <paramref name="path"/>, which exists.
    /// </summary>
    internal static ValueTask<LedgerStore> OpenStoreAsync(string path, bool create, Guid? cluster = null) =>
        LedgerStore.OpenAsync(
            new PortableWalStorage(),
            Options(path),
            cluster ?? LedgerReplicaTestData.Cluster,
            create,
            new LedgerStoreCheckpointOptions(),
            new FakeTimeProvider(),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Creates a real node root at <paramref name="path"/> with a store and the replica of
    /// <see cref="LedgerReplicaTestData.Ledger"/>, and opens the replica, which owns the store.
    /// </summary>
    internal static async ValueTask<StoreOwningReplica> CreateAsync(string path, int windowSize = 64)
    {
        Directory.CreateDirectory(path);
        return await StoreOwningReplica.OpenAsync(await OpenStoreAsync(path, create: true), new LedgerReplicaOptions { WindowSize = windowSize }, create: true);
    }

    internal static Task SettledCheckpointAsync(StoreOwningReplica replica) => SettledCheckpointAsync(replica.Replica);

    /// <summary>
    /// Writes a checkpoint which reflects every write acknowledged before the call. A write is acknowledged when its
    /// record is published, just before the WAL's durable frontier passes it, so the frontier is awaited first: a
    /// checkpoint defers a leaf holding a record at or above its cut (LS-I8).
    /// </summary>
    internal static async Task SettledCheckpointAsync(LedgerStoreReplica replica)
    {
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.Wal.DurableFrontier > replica.Ledger.LastPublished, "the durable frontier passes the replica's acknowledged writes");

        await replica.CheckpointAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sends a writer write of <paramref name="bytes"/>, carrying no known contiguous tail.
    /// </summary>
    internal static async Task<ReplicaWriteOutcome> WriteAsync(ILedgerReplica replica, long address, byte[] bytes)
    {
        using var payload = Payload(bytes);
        return await replica.WriteAsync(new Slot(address), payload, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sends a recovery write of <paramref name="bytes"/>, carrying no known contiguous tail (LR-I5).
    /// </summary>
    internal static async Task<ReplicaWriteOutcome> RecoveryWriteAsync(ILedgerReplica replica, long address, byte[] bytes)
    {
        using var payload = Payload(bytes);
        return await replica.RecoveryWriteAsync(address, payload, TestContext.Current.CancellationToken);
    }

    private static ArcBuffer Payload(byte[] bytes) => LedgerReplicaTestData.Buffer(bytes);

    private static async ValueTask<StoreOwningReplica> OpenAsync(string path, LedgerReplicaOptions? window = null) =>
        await StoreOwningReplica.OpenAsync(await OpenStoreAsync(path, create: false), window ?? Window64, create: false);

    /// <summary>
    /// Asserts that <paramref name="replica"/>, opened with a window other than the one the replica records, refuses
    /// every operation with <see cref="ArgumentException"/> (LR-I6).
    /// </summary>
    internal static async Task AssertRefusesAnotherWindowAsync(ILedgerReplica replica)
    {
        var token = TestContext.Current.CancellationToken;
        var tail = new Slot(9);
        using var payload = Payload([7]);
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.WriteAsync(new Slot(1), payload, tail, recovery: false, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.WriteAsync(new Slot(1), payload, tail, recovery: true, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.ReadAsync(Slot.Zero, tail, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.ReadAsync(Slot.Zero, 0, 1, tail, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.ReadTailAsync(tail, seal: false, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.ReadTailAsync(tail, seal: true, token));
    }

    /// <summary>
    /// Writes one 400-byte record per 512-byte page to slots 0 through 7 except 3, then slot 9.
    /// </summary>
    private static async Task WriteDamageScenarioAsync(ILedgerReplica replica)
    {
        foreach (var address in Enumerable.Range(0, 8).Where(static address => address != 3))
        {
            _ = await WriteAsync(replica, address, LedgerReplicaTestData.CreatePayload(400, address));
        }

        _ = await WriteAsync(replica, 9, [9]);
    }

    /// <summary>
    /// Returns the position of the create record which lists <paramref name="ledger"/>, once the WAL's durable frontier
    /// has passed every record it admitted.
    /// </summary>
    internal static async Task<WalPosition> CreateRecordPositionAsync(LedgerStore store, LedgerId ledger)
    {
        await store.WaitForFrontierAsync();
        await foreach (var entry in store.Wal.ReadAllAsync(default, TestContext.Current.CancellationToken))
        {
            if (entry.TryGetValue(out WalRecord record)
                && StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed
                && parsed is CreateRecord created
                && created.Pairs.Any(pair => pair.Ledger == ledger))
            {
                return record.Position;
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The WAL holds no create record of ledger {ledger.Value}."));
    }

    /// <summary>
    /// Decodes the durable catalog checkpoint of <paramref name="storage"/>'s node root with the highest cut (ledgerstore.md
    /// §7.2).
    /// </summary>
    private static CatalogCheckpoint NewestCatalogCheckpoint(SimulatedWalStorage storage)
    {
        var cuts = new List<(string Name, WalPosition Cut)>();
        foreach (var name in storage.ListNamedFiles(SimulatedNodeRoot.WalPath))
        {
            if (CatalogCheckpoints.TryParseFileName(name, out var cut))
            {
                cuts.Add((name, cut));
            }
        }

        var (newest, newestCut) = cuts.MaxBy(static file => file.Cut);
        return ReplicaCatalog.Decode(storage.ReadDurableFile(SimulatedNodeRoot.WalPath, newest), newestCut).Checkpoint();
    }

    private static RecordingLedgerReplica Unavailable() => new()
    {
        WriteAsyncHandler = static (_, _, _, _) => ValueTask.FromException<ReplicaWriteOutcome>(new IOException("replica unavailable")),
        ReadAsyncHandler = static (_, _) => ValueTask.FromException<ReplicaRead>(new IOException("replica unavailable")),
        ReadTailAsyncHandler = static (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new IOException("replica unavailable")),
    };

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static Task WaitForPendingWriteAsync(SimulatedWalStorage storage) =>
        LedgerStoreTestExtensions.WaitUntilAsync(() => storage.PendingWriteCount != 0, "a write is in flight");
}
