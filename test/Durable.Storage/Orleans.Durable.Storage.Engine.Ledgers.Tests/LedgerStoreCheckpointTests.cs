using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the slot-table checkpoints (LS-I3, LS-I8, LS-I9) end to end over <see cref="SimulatedWalStorage"/>: recovery
/// from a checkpoint plus the WAL suffix equals the acknowledged state, the seal included (LS-I11), and a full-scan
/// rebuild, a restart after a failed checkpoint, damaged checkpoints, file reclamation, compaction, and triggers behave
/// as specified.
/// </summary>
public sealed class LedgerStoreCheckpointTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The offset in a data page of its first frame's word: after the page's CRC (wal.md §7).
    /// </summary>
    private const int FrameOffset = 4;

    /// <summary>
    /// The offset in a data page of its first record, after the frame word.
    /// </summary>
    private const int RecordOffset = FrameOffset + 4;

    /// <summary>
    /// A Full frame's word holds kind 1 in its top two bits and the record's length below them.
    /// </summary>
    private const uint FullFrame = 0x4000_0000;

    private static readonly string WalPath = SimulatedNodeRoot.WalPath;
    private static readonly string SlotsPath = LedgerStore.SlotsPath(SimulatedNodeRoot.Path, 0);
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    // Unpreallocated, so the WAL neither paces file preparation nor reads the clock which times checkpoints.
    private static readonly LedgerStoreOptions SmallFiles = new() { DirectoryPath = SimulatedNodeRoot.Path, PageSize = 512, FileSize = 64 * 512, WriteWindowPages = 8, PreallocateFiles = false };
    private static readonly LedgerStoreCheckpointOptions ExplicitOnly = new() { CheckpointInterval = TimeSpan.FromDays(1), MaxCheckpointFiles = 3 };

    private enum Operation
    {
        Mutate,
        Seal,
        Checkpoint,
        ProcessCrash,
        SystemCrash,
    }

    [Fact]
    public void RandomMutationsCheckpointsAndCrashesRecoverTheAcknowledgedTable() =>
        Gen.Select(Gen.Int, Gen.Int[4, 12]).Sample(
            (seed, rounds) => _leaks.InScope(() => RunModelAsync(seed, rounds).GetAwaiter().GetResult()),
            iter: 20,
            threads: 1);

    [Fact]
    public async Task ACheckpointLeavesRecoveryOnlyTheWalSuffix()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 600);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await WriteRangeAsync(replica, 600, 50);

        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, reopened.Ledger.Recovery.Path);
        Assert.Equal(50, reopened.Ledger.Recovery.ReplayedRecords);
        await AssertRangeAsync(reopened, 0, 650);

        // LR-I6: the retained contiguous tail is rebuilt from the highest slot, 649, and the window, 64.
        Assert.Equal(new LedgerReplicaTail(new Slot(586), IsSealed: false), await reopened.ReadTailAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// LS-I9, conventions.md §14: a checkpoint whose root write fails is reported, which stops the process, and leaves the
    /// previous root in force: the restarted process recovers from it and replays everything after it, and its next
    /// checkpoint takes the generation the failed one could not, whose file the open deleted.
    /// </summary>
    [Fact]
    public async Task AFailedCheckpointKeepsThePreviousRootAndLosesNoLeaf()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 300);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        var first = replica.Ledger.CheckpointGeneration;

        await WriteRangeAsync(replica, 300, 300);
        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedWriteFault = static (_, name, _, _) => name == "slots.root" ? new IOException("injected root failure") : null;
        var failure = await Assert.ThrowsAsync<IOException>(async () => await LedgerStoreReplicaTests.SettledCheckpointAsync(replica));
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.HandleWriteGather, SlotsPath);
        Assert.Equal(first, replica.Ledger.CheckpointGeneration);

        // The reported failure stopped the store, which answers nothing more.
        await StoppedStore.AssertAnswersNothingAsync(replica.Store, replica.Replica, failure);

        // The restarted process recovers from the previous root and replays everything after it.
        await root.CrashAsync(replica, operatingSystem: true);
        var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.Checkpoint, first, 300), reopened.Ledger.Recovery);
        await AssertRangeAsync(reopened, 0, 600);

        // Its next checkpoint takes the generation the failed write could not (LS-I9).
        await WriteRangeAsync(reopened, 600, 10);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(reopened);
        Assert.Equal(first + 1, reopened.Ledger.CheckpointGeneration);
        await root.CrashAsync(reopened, operatingSystem: true);
        await using var recovered = await root.OpenReplicaAsync(Window);
        Assert.Equal(0, recovered.Ledger.Recovery.ReplayedRecords);
        await AssertRangeAsync(recovered, 0, 610);
    }

    [Fact]
    public async Task ATornNewestRootFallsBackToTheOlderRootAndItsGenerationIsTakenAgain()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 300);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        var older = replica.Ledger.CheckpointGeneration;
        await WriteRangeAsync(replica, 300, 300);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        var newer = replica.Ledger.CheckpointGeneration;
        await root.CrashAsync(replica, operatingSystem: false);
        root.Storage.DamageFile(SlotsPath, "slots.root", RootSlot(root.Storage, newer) * 4096 + 20, [0xFF, 0xFF]);
        var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.Checkpoint, older, 300), reopened.Ledger.Recovery);
        await AssertRangeAsync(reopened, 0, 600);

        // The torn slot names nothing usable and open deleted the newer generation's file, so the next checkpoint takes
        // that generation again with nothing naming it (LS-I9).
        await WriteRangeAsync(reopened, 600, 1);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(reopened);
        Assert.Equal(older + 1, newer);
        Assert.Equal(newer, reopened.Ledger.CheckpointGeneration);
        await root.CrashAsync(reopened, operatingSystem: true);
        await using var recovered = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, recovered.Ledger.Recovery.Path);
        await AssertRangeAsync(recovered, 0, 601);
    }

    /// <summary>
    /// A damaged leaf object is found when its leaf is first needed, recomputed from the WAL, and written again by a
    /// checkpoint the background task writes at once.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADamagedLeafIsRecomputedFromTheWalAndCheckpointedAgain()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 2_000);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: false);

        // Leaves come first in a generation file, in leaf order, so offset 10 lies in leaf 0.
        root.Storage.DamageFile(SlotsPath, Assert.Single(CheckpointFiles(root.Storage)), 10, [0xAA, 0xBB, 0xCC]);
        using var time = new ObservedTime();
        var reopened = await root.OpenReplicaAsync(Window, timeProvider: time);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, reopened.Ledger.Recovery.Path);
        await time.NextTimerAsync(() => reopened.Ledger.CheckpointsWritten == 0);
        Assert.Equal(0, reopened.Ledger.RebuiltLeaves);

        // Concurrent reads of the damaged leaf share one recomputation.
        await Task.WhenAll(Enumerable.Range(0, 256).Select(slot => LedgerReplicaTestData.ReadBytesAsync(reopened, slot, TestContext.Current.CancellationToken)));
        await AssertRangeAsync(reopened, 0, 2_000);
        Assert.Equal(1, reopened.Ledger.RebuiltLeaves);
        await time.NextTimerAsync(() => reopened.Ledger.CheckpointsWritten == 1);
        Assert.Equal(1, reopened.Ledger.CheckpointsWritten);

        await root.CrashAsync(reopened, operatingSystem: true);
        await using var recovered = await root.OpenReplicaAsync(Window);
        await AssertRangeAsync(recovered, 0, 2_000);
        Assert.Equal(0, recovered.Ledger.RebuiltLeaves);
    }

    /// <summary>
    /// The replica classifies every leaf rejection vector as a leaf it recomputes from the WAL rather than loads (LS-I3,
    /// ledgerstore.md §7.4, conventions.md §15): with the vector as leaf 0's object, under a reference whose CRC its
    /// bytes match, reading the leaf's slots recomputes it, unless its length is one no leaf has, which makes the tree,
    /// and so the root, unusable, and recovery scans the WAL. Either way the replica answers what was written.
    /// </summary>
    [Fact]
    public async Task LeafRejectionsAreLeavesTheReplicaRecomputes()
    {
        foreach (var vector in SharedVectors.Cases("ledgerstore", "leaf-rejections.json"))
        {
            var bytes = SharedVectors.Hex(vector);
            var root = NewRoot();
            var replica = await root.CreateReplicaAsync(Window);
            await WriteRangeAsync(replica, 0, 300);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            await root.CrashAsync(replica, operatingSystem: false);
            await ReplaceLeafAsync(root.Storage, 0, bytes);

            await using var reopened = await root.OpenReplicaAsync(Window);
            var placed = bytes.Length >= SlotLeafCodec.MinimumLength && bytes.Length <= SlotLeafCodec.MaximumLength;
            Assert.Equal(placed ? LedgerStoreReplayPath.Checkpoint : LedgerStoreReplayPath.FullScan, reopened.Ledger.Recovery.Path);
            await AssertRangeAsync(reopened, 0, 300);
            Assert.True(
                reopened.Ledger.RebuiltLeaves == (placed ? 1 : 0),
                string.Create(CultureInfo.InvariantCulture, $"'{SharedVectors.Name(vector)}' recomputed {reopened.Ledger.RebuiltLeaves} leaves."));
        }
    }

    /// <summary>
    /// A leaf recomputed from a WAL with damage below the checkpoint faults every slot whose latest record precedes
    /// the damage, as a replay would, and reads the others.
    /// </summary>
    [Fact]
    public async Task ARecomputedLeafFaultsTheSlotsBeforeWalDamage()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        for (var slot = 0L; slot < 10; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);

        // Later records in another leaf keep the damaged page out of the crash region a reopen finalizes as torn.
        for (var slot = 256L; slot < 300; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: true);
        var (file, offset) = await FindRecordAsync(root.Storage, 3);
        root.Storage.Damage(WalPath, file, offset + 40, [1, 2, 3]);
        root.Storage.DamageFile(SlotsPath, Assert.Single(CheckpointFiles(root.Storage)), 10, [0xAA, 0xBB, 0xCC]);

        await using var reopened = await root.OpenReplicaAsync(Window);
        for (var slot = 0L; slot <= 3; slot++)
        {
            await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(slot), TestContext.Current.CancellationToken));
        }

        await AssertRangeAsync(reopened, 4, 6);
        await AssertRangeAsync(reopened, 256, 44);
        Assert.Equal(1, reopened.Ledger.RebuiltLeaves);
    }

    /// <summary>
    /// Compaction copies unloaded leaves forward without loading them, validating each on the way: a damaged one is
    /// recomputed, and the others stay unloaded and readable after their old file is reclaimed.
    /// </summary>
    [Fact]
    public async Task CompactionCopiesUnloadedLeavesAndRecomputesDamagedOnes()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 2_000);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: false);
        var oldest = Assert.Single(CheckpointFiles(root.Storage));
        root.Storage.DamageFile(SlotsPath, oldest, 10, [0xAA, 0xBB, 0xCC]);

        // The leaves covering the highest 1,024 slots, 3 through 7, load with recovery.
        replica = await root.OpenReplicaAsync(Window);
        Assert.Equal((5, 3), replica.Ledger.LeafCounts);

        // Each round's new leaf lands in its own file, so the referenced files pass the bound and the oldest is copied.
        const int Rounds = 2 * 3;
        Assert.Equal(3, ExplicitOnly.MaxCheckpointFiles);
        for (var round = 0; round < Rounds; round++)
        {
            await WriteRangeAsync(replica, 2_048 + round * 256, 1);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        }

        Assert.DoesNotContain(oldest, CheckpointFiles(root.Storage));
        Assert.Equal(1, replica.Ledger.RebuiltLeaves);
        Assert.Equal((6 + Rounds, 2), replica.Ledger.LeafCounts);
        await AssertAllAsync(replica);
        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        await AssertAllAsync(reopened);
        Assert.Equal(0, reopened.Ledger.RebuiltLeaves);

        static async Task AssertAllAsync(StoreOwningReplica replica)
        {
            await AssertRangeAsync(replica, 0, 2_000);
            for (var round = 0; round < Rounds; round++)
            {
                await AssertRangeAsync(replica, 2_048 + round * 256, 1);
            }
        }
    }

    /// <summary>
    /// A root which cannot be used is recomputed at once: recovery scans the WAL, and the background task writes a new
    /// checkpoint without waiting for the interval.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnUnusableRootIsRecomputedAtOnce()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 600);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        var generation = replica.Ledger.CheckpointGeneration;
        await root.CrashAsync(replica, operatingSystem: false);
        root.Storage.DamageFile(SlotsPath, "slots.root", RootSlot(root.Storage, generation) * 4096 + 20, [0xFF, 0xFF]);

        using var time = new ObservedTime();
        var reopened = await root.OpenReplicaAsync(Window, timeProvider: time);
        Assert.Equal(LedgerStoreReplayPath.FullScan, reopened.Ledger.Recovery.Path);
        await time.NextTimerAsync(() => reopened.Ledger.CheckpointsWritten == 1);
        Assert.Equal(1, reopened.Ledger.CheckpointsWritten);

        await root.CrashAsync(reopened, operatingSystem: true);
        await using var recovered = await root.OpenReplicaAsync(Window);
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.Checkpoint, recovered.Ledger.CheckpointGeneration, 0), recovered.Ledger.Recovery);
        await AssertRangeAsync(recovered, 0, 600);
        Assert.Equal(new LedgerReplicaTail(new Slot(536), IsSealed: false), await recovered.ReadTailAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Opening from a checkpoint loads only the leaves recovery needs: those the replayed suffix touches and those
    /// covering the highest 1,024 slots, near which appends and recovery reads land, which also give the highest slot the
    /// retained contiguous tail is rebuilt from (LR-I6). A contiguous-tail read loads none; every other leaf loads when
    /// an operation first needs it.
    /// </summary>
    [Fact]
    public async Task OpeningLoadsOnlyTheLeavesRecoveryNeeds()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 5_000);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await WriteRangeAsync(replica, 5_000, 10);
        await root.CrashAsync(replica, operatingSystem: true);

        // Slots 3,986 through 5,010 lie in leaves 15 through 19.
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal((5, 15), reopened.Ledger.LeafCounts);
        Assert.Equal(new LedgerReplicaTail(new Slot(4_946), IsSealed: false), await reopened.ReadTailAsync(TestContext.Current.CancellationToken));
        Assert.Equal((5, 15), reopened.Ledger.LeafCounts);

        await AssertRangeAsync(reopened, 0, 1);
        Assert.Equal((6, 14), reopened.Ledger.LeafCounts);
        await AssertRangeAsync(reopened, 0, 5_010);
        Assert.Equal((20, 0), reopened.Ledger.LeafCounts);
    }

    /// <summary>
    /// Reading the slot states, which a traced simulation logs when a node restarts, reads each unloaded leaf into a
    /// scratch copy, a damaged one recomputed from the WAL: the replica which reported them loads and recomputes the same
    /// leaves, at the same operations, as one which did not (test/Durable.Storage/docs/spec/verification.md §5.1).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingTheSlotStatesLoadsNoLeaf(bool damaged)
    {
        // The leaves covering the highest 1,024 slots load with recovery; leaf 0, damaged or not, loads at the first read.
        var rebuilt = damaged ? 1L : 0;
        List<((int Loaded, int Unloaded), long Rebuilt)> expected = [((5, 15), 0), ((5, 15), 0), ((6, 14), rebuilt), ((20, 0), rebuilt)];
        Assert.Equal(expected, await ObserveLoadsAsync(traced: false));
        Assert.Equal(expected, await ObserveLoadsAsync(traced: true));

        async Task<List<((int Loaded, int Unloaded), long Rebuilt)>> ObserveLoadsAsync(bool traced)
        {
            var root = NewRoot();
            var replica = await root.CreateReplicaAsync(Window);
            await WriteRangeAsync(replica, 0, 5_000);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            await WriteRangeAsync(replica, 5_000, 10);
            await root.CrashAsync(replica, operatingSystem: true);
            if (damaged)
            {
                // Leaves come first in a generation file, in leaf order, so offset 10 lies in leaf 0.
                root.Storage.DamageFile(SlotsPath, Assert.Single(CheckpointFiles(root.Storage)), 10, [0xAA, 0xBB, 0xCC]);
            }

            await using var reopened = await root.OpenReplicaAsync(Window);
            List<((int Loaded, int Unloaded), long Rebuilt)> loads = [Observe()];
            if (traced)
            {
                var (data, faulty) = await reopened.Ledger.SlotStatesAsync(5_010, TestContext.Current.CancellationToken);
                Assert.Equal(Enumerable.Range(0, 5_010).Select(static slot => (long)slot), data);
                Assert.Empty(faulty);
            }

            loads.Add(Observe());
            await AssertRangeAsync(reopened, 0, 1);
            loads.Add(Observe());
            await AssertRangeAsync(reopened, 0, 5_010);
            loads.Add(Observe());
            return loads;

            ((int, int), long) Observe() => (reopened.Ledger.LeafCounts, reopened.Ledger.RebuiltLeaves);
        }
    }

    /// <summary>
    /// Corruption is reported only for the faulty slots an operation inspects: once recovery writes re-establish slots,
    /// they read normally across restarts, while other slots, absent ones included, stay faulty (LR-I2, LR-I5).
    /// </summary>
    [Fact]
    public async Task ReEstablishedSlotsNoLongerReportCorruption()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 10);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        for (var slot = 10L; slot < 300; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: true);
        root.Storage.RemoveFile(WalPath, root.Storage.ListFiles(WalPath)[^3]);
        replica = await root.OpenReplicaAsync(Window);
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await replica.ReadAsync(new Slot(1_000), TestContext.Current.CancellationToken));

        // A recovery write writes only the slots which do not hold DATA.
        for (var slot = 0L; slot < 300; slot++)
        {
            Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, slot, Payload(slot, 0)));
        }

        await AssertReEstablishedAsync(replica);

        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        await AssertReEstablishedAsync(reopened);

        static async Task AssertReEstablishedAsync(StoreOwningReplica replica)
        {
            await AssertRangeAsync(replica, 0, 300);
            await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await replica.ReadAsync(new Slot(1_000), TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// A checkpoint reflects exactly the WAL records below its cut, so losing every WAL byte after the cut loses only
    /// records the checkpoint does not reflect: the replica recovers without corruption, across restarts, and the lost
    /// slots read as Missing until a recovery write restores them.
    /// </summary>
    [Fact]
    public async Task AWalTailLostAfterTheCutLeavesTheCheckpointIntact()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 40);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        for (var slot = 40L; slot < 200; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await root.CrashAsync(replica, operatingSystem: true);
        LoseWalAfter(root.Storage, InForceRoot(root.Storage).ReplayStart);
        for (var restart = 0; restart < 2; restart++)
        {
            replica = await root.OpenReplicaAsync(Window);
            Assert.Equal(LedgerStoreReplayPath.Checkpoint, replica.Ledger.Recovery.Path);
            await AssertRangeAsync(replica, 0, 40);
            Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 199, TestContext.Current.CancellationToken));
            Assert.False((await replica.ReadTailAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
            await root.CrashAsync(replica, operatingSystem: true);
        }

        await using var recovered = await root.OpenReplicaAsync(Window);
        Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(recovered, 199, Payload(199, 0)));
        await AssertRangeAsync(recovered, 199, 1);
    }

    /// <summary>
    /// §8.7 step 5: when the WAL loses the whole file holding a checkpoint's replay start, the open still starts its first
    /// file above that replay start, so no later record takes a position the checkpoint names: a write after the loss is
    /// replayed across the next restart, not taken as one the checkpoint holds.
    /// </summary>
    [Fact]
    public async Task AnOpenNeverReusesTheLostFileOfACheckpointsReplayStart()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        var written = 0;
        while (replica.Store.Wal.DurableFrontier.File < 2 || written % 8 != 0)
        {
            await WriteRangeAsync(replica, written++, 1);
        }

        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: true);
        var replayStart = InForceRoot(root.Storage).ReplayStart;
        Assert.True(replayStart.File > 1, "The checkpoint's replay start lies in WAL file 1.");
        foreach (var file in root.Storage.ListFiles(WalPath).Where(file => file >= replayStart.File))
        {
            root.Storage.RemoveFile(WalPath, file);
        }

        replica = await root.OpenReplicaAsync(Window);
        await WriteRangeAsync(replica, written, 1);
        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        await AssertRangeAsync(reopened, written, 1);
    }

    /// <summary>
    /// When a later write group completes first, its records are published while the frontier stays before the earlier
    /// group. A checkpoint taken then defers their leaves, so losing every WAL byte after its cut loses every record
    /// cleanly: the slots read as Missing, never as corruption.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACheckpointDefersLeavesWithRecordsAboveItsCut(bool settleBeforeCrash)
    {
        var root = NewRoot();

        // Two groups in flight, so the later one can complete first.
        var replica = await root.CreateReplicaAsync(Window, SmallFiles with { MinWriteGroupsInFlight = 2, MaxWriteGroupsInFlight = 2 });
        root.Storage.HoldWrites = true;
        var slots = Enumerable.Range(0, 8).Concat(Enumerable.Range(256, 8)).ToArray();
        var writes = new List<Task>();
        foreach (var slot in slots)
        {
            writes.Add(LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0)));
            if (writes.Count == 1)
            {
                // The first record's group holds the first page, so later records go to a later group.
                await WaitUntilAsync(() => root.Storage.PendingWriteCount == 1);
            }
        }

        await WaitUntilAsync(() => root.Storage.PendingWriteCount == 2);
        long last = -1;
        _ = root.Storage.CompleteWrite((_, _, offset) =>
        {
            last = Math.Max(last, offset);
            return false;
        });
        Assert.True(root.Storage.CompleteWrite((name, _, offset) => name is null && offset == last));
        await WaitUntilAsync(() => writes.Any(static write => write.IsCompleted));
        Assert.False(writes[0].IsCompleted);

        var checkpoint = replica.CheckpointAsync(TestContext.Current.CancellationToken).AsTask();
        while (!checkpoint.IsCompleted)
        {
            if (!root.Storage.CompleteWrite(static (name, _, _) => name is not null))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }

        await checkpoint;
        Assert.True(replica.Ledger.LeavesDeferred >= 1);
        Assert.Empty(CheckpointFiles(root.Storage));
        root.Storage.HoldWrites = false;
        await WaitUntilAsync(() =>
        {
            _ = root.Storage.CompleteWrite();
            return writes.All(static write => write.IsCompleted);
        });

        await Task.WhenAll(writes);
        if (settleBeforeCrash)
        {
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            Assert.Single(CheckpointFiles(root.Storage));
        }

        await root.CrashAsync(replica, operatingSystem: true);
        if (!settleBeforeCrash)
        {
            LoseWalAfter(root.Storage, InForceRoot(root.Storage).ReplayStart);
        }

        await using var reopened = await root.OpenReplicaAsync(Window);
        foreach (var slot in slots)
        {
            Assert.Equal(settleBeforeCrash ? Payload(slot, 0) : null, await LedgerReplicaTestData.ReadBytesAsync(reopened, slot, TestContext.Current.CancellationToken));
        }

        static async Task WaitUntilAsync(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>
    /// Writes complete in seeded random order while a checkpoint runs; losing every WAL byte after its cut never leaves
    /// a checkpointed entry naming a lost record: every slot reads its value or Missing, never corruption.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACheckpointNeverReflectsARecordAtOrAboveItsCut()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            await RunCutAsync(seed);
        }

        static async Task RunCutAsync(int seed)
        {
            var random = new Random(seed);
            var root = NewRoot(new SimulatedWalStorage(seed));
            var replica = await root.CreateReplicaAsync(Window);
            root.Storage.HoldWrites = true;
            var writes = Enumerable.Range(0, 200).Select(slot => LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0))).ToArray();
            var all = Task.WhenAll(writes);
            var trigger = random.Next(1, 30);
            Task? checkpoint = null;
            var completed = 0;
            while (!all.IsCompleted || checkpoint is not { IsCompleted: true })
            {
                if (checkpoint is null && (completed >= trigger || all.IsCompleted))
                {
                    checkpoint = replica.CheckpointAsync(TestContext.Current.CancellationToken).AsTask();
                }

                if (root.Storage.CompleteWrite())
                {
                    completed++;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
                }
            }

            root.Storage.HoldWrites = false;
            await all;
            await checkpoint;
            await root.CrashAsync(replica, operatingSystem: true);
            LoseWalAfter(root.Storage, InForceRoot(root.Storage).ReplayStart);

            await using var reopened = await root.OpenReplicaAsync(Window);
            for (var slot = 0L; slot < 200; slot++)
            {
                if (await LedgerReplicaTestData.ReadBytesAsync(reopened, slot, TestContext.Current.CancellationToken) is { } value)
                {
                    Assert.Equal(Payload(slot, 0), value);
                }
            }

            Assert.False((await reopened.ReadTailAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
        }
    }

    [Fact]
    public async Task CompactionBoundsTheCheckpointFilesAndEveryReferencedFileSurvives()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        var generations = new List<ulong>();
        for (var round = 0; round < 12; round++)
        {
            await WriteRangeAsync(replica, round * 300, 300);
            _ = await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, round * 300, Payload(round * 300, 0));
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            generations.Add(replica.Ledger.CheckpointGeneration);
            Assert.True(replica.Ledger.CheckpointFileCount <= ExplicitOnly.MaxCheckpointFiles + 1, string.Create(CultureInfo.InvariantCulture, $"{replica.Ledger.CheckpointFileCount} files after round {round}."));
            Assert.True(root.Storage.ListNamedFiles(SlotsPath).Count(static name => name.EndsWith(".clsi", StringComparison.Ordinal)) <= 2 * (ExplicitOnly.MaxCheckpointFiles + 1));
        }

        Assert.Equal(generations.Order(), generations);
        Assert.Equal(generations.Count, generations.Distinct().Count());
        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, reopened.Ledger.Recovery.Path);
        await AssertRangeAsync(reopened, 0, 12 * 300);
    }

    [Fact(Timeout = 30_000)]
    public async Task TimerObservationWaitsForPendingTimersAfterTheExpectedWork()
    {
        using var time = new ObservedTime();
        var interval = TimeSpan.FromSeconds(10);
        var cancelled = time.CreateTimer(static _ => { }, null, interval, Timeout.InfiniteTimeSpan);
        cancelled.Dispose();

        using var elapsed = time.CreateTimer(static _ => { }, null, interval, Timeout.InfiniteTimeSpan);
        time.Fake.Advance(interval);
        var waiting = time.NextTimerAsync();
        Assert.False(waiting.IsCompleted);

        using var pending = time.CreateTimer(static _ => { }, null, interval, Timeout.InfiniteTimeSpan);
        await waiting.WaitAsync(TestContext.Current.CancellationToken);

        var completed = false;
        using var beforeWork = time.CreateTimer(static _ => { }, null, interval, Timeout.InfiniteTimeSpan);
        var afterWork = time.NextTimerAsync(() => completed);
        Assert.False(afterWork.IsCompleted);
        completed = true;
        using var after = time.CreateTimer(static _ => { }, null, interval, Timeout.InfiniteTimeSpan);
        await afterWork;
    }

    [Fact]
    public void TheIntervalIsPositiveAndAtMostTheLongestTimerWait()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.Zero }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromMilliseconds(uint.MaxValue) }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.MaxValue }.Validate());
        new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1) }.Validate();
    }

    [Fact(Timeout = 30_000)]
    public async Task TheIntervalTriggersACheckpointWhenItPasses()
    {
        using var time = new ObservedTime();
        var root = NewRoot(checkpointOptions: new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromSeconds(10), CheckpointReplayBytes = 1L << 40 });
        var replica = await root.CreateReplicaAsync(Window, timeProvider: time);
        await WriteRangeAsync(replica, 0, 10);
        await time.NextTimerAsync(() => replica.Ledger.CheckpointsWritten == 0);

        time.Fake.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(0, replica.Ledger.CheckpointsWritten);
        time.Fake.Advance(TimeSpan.FromSeconds(0.2));
        await time.NextTimerAsync(() => replica.Ledger.CheckpointsWritten == 1);
        Assert.Equal(1, replica.Ledger.CheckpointsWritten);
        Assert.Equal(Payload(9, 0), await LedgerReplicaTestData.ReadBytesAsync(replica, 9, TestContext.Current.CancellationToken));
        await replica.DisposeAsync();
    }

    /// <summary>
    /// Records published before the background task first checks for work still trigger a replay-bound checkpoint:
    /// the task never discards a wake without checking whether a checkpoint is due.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheReplayBoundTriggersACheckpointPublishedBeforeTheTaskFirstRuns()
    {
        using var time = new ObservedTime();
        time.Gate.Reset();
        var root = NewRoot(checkpointOptions: new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromDays(1), CheckpointReplayBytes = 4 * SmallFiles.PageSize });
        var replica = await root.CreateReplicaAsync(Window, timeProvider: time);
        for (var slot = 0; slot < 20; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        Assert.Equal(0, replica.Ledger.CheckpointsWritten);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => time.HeldReads != 0, "maintenance waits at the gate to check for work");

        // The checkpoint cuts at the durable frontier, and the replay distance runs to the latest published page: with the
        // frontier short of the writes, which the WAL answers first, the first cut leaves the bound reached, and maintenance
        // writes a second checkpoint at once.
        await replica.Store.WaitForFrontierAsync();
        time.Gate.Set();
        await time.NextTimerAsync(() => replica.Ledger.CheckpointsWritten == 1);
        Assert.Equal(1, replica.Ledger.CheckpointsWritten);

        // The new replay start leaves a short distance, so later records wait for the bound again.
        await LedgerStoreReplicaTests.WriteAsync(replica, 20, Payload(20, 0));
        Assert.Equal(1, replica.Ledger.CheckpointsWritten);
        Assert.Equal(Payload(20, 0), await LedgerReplicaTestData.ReadBytesAsync(replica, 20, TestContext.Current.CancellationToken));
        await replica.DisposeAsync();
    }

    /// <summary>
    /// Conventions.md §14: a background checkpoint whose write fails is reported, which stops the process, and ends
    /// maintenance, which retries nothing. Disposal releases every file and buffer, writing no checkpoint over the stopped
    /// storage, and then surfaces maintenance's failure; the restarted process recovers every acknowledged write from the
    /// WAL and checkpoints again.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AFailedBackgroundCheckpointEndsMaintenanceAndDisposalSurfacesItOnceEverythingIsReleased()
    {
        using var time = new ObservedTime();
        var root = NewRoot(checkpointOptions: new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromSeconds(10), CheckpointReplayBytes = 1L << 40 });
        var replica = await root.CreateReplicaAsync(Window, timeProvider: time);
        await WriteRangeAsync(replica, 0, 10);
        await time.NextTimerAsync(() => replica.Ledger.CheckpointsWritten == 0);

        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedWriteFault = static (_, name, _, _) => name == "slots.root" ? new IOException("injected root failure") : null;
        time.Fake.Advance(TimeSpan.FromSeconds(10));
        var maintenance = replica.Store.MaintenanceCompletion;
        Assert.NotNull(maintenance);
        var failure = await Assert.ThrowsAsync<IOException>(() => maintenance.WaitAsync(TestContext.Current.CancellationToken));
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.CreateFile, SlotsPath);
        Assert.Equal(0, replica.Ledger.CheckpointsWritten);
        Assert.Equal(0, time.TimersCreatedSinceLastWait);

        var stopped = root.Storage;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await replica.DisposeAsync()));
        Assert.Equal(0, stopped.OpenHandleCount);

        root.Crash(operatingSystem: false);
        using var later = new ObservedTime();
        await using var recovered = await root.OpenReplicaAsync(Window, timeProvider: later);
        Assert.Equal(LedgerStoreReplayPath.FullScan, recovered.Ledger.Recovery.Path);
        await AssertRangeAsync(recovered, 0, 10);
        await later.NextTimerAsync(() => recovered.Ledger.CheckpointsWritten == 0);
        later.Fake.Advance(TimeSpan.FromSeconds(10));
        await later.NextTimerAsync(() => recovered.Ledger.CheckpointsWritten == 1);
        Assert.Equal(1UL, recovered.Ledger.CheckpointGeneration);
    }

    /// <summary>
    /// Conventions.md §14: maintenance which fails with a defect, here at its wait's timer, would leave checkpoints
    /// unwritten and the WAL growing without bound, so the defect is reported once, as
    /// <c>LedgerStoreMaintenance</c>, which stops the process: the stopped store answers nothing more,
    /// and disposal surfaces the defect once everything is released. The restarted process recovers every acknowledged
    /// write.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AMaintenanceDefectIsReportedOnceAndStopsTheStore()
    {
        var time = new FailingTimers();
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window, timeProvider: time);
        await WriteRangeAsync(replica, 0, 10);
        await LedgerStoreTestExtensions.WaitUntilAsync(() => replica.Store.MaintenanceIdle, "maintenance waits with nothing due");
        var defect = new InvalidOperationException("injected maintenance defect");
        var reported = FatalErrors.ReportedCount;
        time.Fault = defect;
        replica.Store.RequestCatalogCheckpoint();
        var maintenance = replica.Store.MaintenanceCompletion;
        Assert.NotNull(maintenance);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.WaitAsync(TestContext.Current.CancellationToken)));
        _ = StorageFailures.TakeDefect(reported, "LedgerStoreMaintenance", defect);
        await StoppedStore.AssertAnswersNothingAsync(replica.Store, replica.Replica, defect);
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(async () => await replica.Store.DisposeAsync()));
        Assert.Equal(0, root.Storage.OpenHandleCount);

        root.Crash(operatingSystem: false);
        await using var recovered = await root.OpenReplicaAsync(Window);
        await AssertRangeAsync(recovered, 0, 10);
    }

    /// <summary>
    /// A root at the last generation, 2<sup>63</sup> − 1, loads, and no checkpoint can follow it (LS-I9, §8.8): the
    /// replica records the failure, neither the replay bound nor the interval schedules a checkpoint,
    /// <c>CheckpointAsync</c> fails, and disposal writes no final checkpoint, while every operation keeps serving. Once an
    /// operator deletes the replica's <c>slots.root</c> and <c>.clsi</c> files, the next open replays the WAL from the
    /// replica's first file and checkpoints again from generation 1.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ExhaustedGenerationsStopCheckpointingUntilAnOperatorDeletesTheCheckpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot(checkpointOptions: new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromSeconds(10), CheckpointReplayBytes = 4 * SmallFiles.PageSize });
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 10);
        await replica.CheckpointAsync(cancellationToken);
        var start = replica.Ledger.Start;

        // The catalog checkpoint lists the replica, so the crafted root below is the replica's from its first file.
        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: false);

        // Slot 0 names the last generation, with no tree and the replica's start as its replay start; slot 1 is unused.
        var slots = new byte[2 * 4096];
        new CheckpointRoot(CheckpointRoot.GenerationLimit - 1, 0, start, null, Sealed: false, default).Write(slots.AsSpan(0, 4096));
        root.Storage.DamageFile(SlotsPath, "slots.root", 0, slots);

        using var time = new ObservedTime();
        var exhausted = await root.OpenReplicaAsync(Window, timeProvider: time);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, exhausted.Ledger.Recovery.Path);
        Assert.Equal(CheckpointRoot.GenerationLimit - 1, exhausted.Ledger.CheckpointGeneration);
        Assert.True(exhausted.Ledger.CheckpointsExhausted);
        Assert.IsType<InvalidDataException>(exhausted.Ledger.Exhaustion);
        await AssertRangeAsync(exhausted, 0, 10);

        await WriteRangeAsync(exhausted, 10, 40);
        time.Fake.Advance(TimeSpan.FromMinutes(1));
        await time.NextTimerAsync();
        Assert.False(exhausted.Ledger.CheckpointDue());
        Assert.Equal(0, exhausted.Ledger.CheckpointsWritten);
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await exhausted.CheckpointAsync(cancellationToken));
        await AssertRangeAsync(exhausted, 0, 50);
        await exhausted.DisposeAsync();
        Assert.Equal(0, exhausted.Ledger.CheckpointsWritten);

        // The operator procedure: with the store closed, delete the replica's root and generation files.
        await using (var directory = await root.Storage.OpenDirectoryAsync(SlotsPath, cancellationToken))
        {
            foreach (var name in root.Storage.ListNamedFiles(SlotsPath))
            {
                await directory.DeleteNamedFileAsync(name, cancellationToken);
            }
        }

        using var later = new ObservedTime();
        await using var recovered = await root.OpenReplicaAsync(Window, timeProvider: later);
        Assert.Equal(LedgerStoreReplayPath.FullScan, recovered.Ledger.Recovery.Path);
        Assert.False(recovered.Ledger.CheckpointsExhausted);
        await AssertRangeAsync(recovered, 0, 50);
        await later.NextTimerAsync(() => recovered.Ledger.CheckpointsWritten == 1);
        Assert.Equal(1UL, recovered.Ledger.CheckpointGeneration);
    }

    /// <summary>
    /// §8.7 step 6: a create record which recovery replays lists its replica with an empty slot table, so the replica adopts
    /// only a slot-index checkpoint whose replay start lies above the record. A checkpoint captured before the WAL's
    /// frontier passed the create record, which the WAL answers first, starts at or below it, and recovery scans the
    /// replica's WAL instead; once a catalog checkpoint reflects the create record, recovery skips the record and adopts
    /// the same checkpoint.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACheckpointStartingAtAReplayedCreateRecordIsAdoptedOnlyOnceACatalogCheckpointReflectsTheRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 10);
        await replica.CheckpointAsync(cancellationToken);
        var created = await LedgerStoreReplicaTests.CreateRecordPositionAsync(replica.Store, LedgerReplicaTestData.Ledger);
        Assert.True(replica.Store.CatalogCut <= created, "The catalog checkpoint reflects the create record.");
        await root.CrashAsync(replica, operatingSystem: false);

        // Slot 0 holds a root with no tree whose replay start is the create record; slot 1 is unused.
        var slots = new byte[2 * 4096];
        new CheckpointRoot(1, 0, created, null, Sealed: false, default).Write(slots.AsSpan(0, 4096));
        root.Storage.DamageFile(SlotsPath, "slots.root", 0, slots);

        var scanned = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.FullScan, scanned.Ledger.Recovery.Path);
        await AssertRangeAsync(scanned, 0, 10);
        await scanned.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(scanned, operatingSystem: false);

        await using var adopted = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, adopted.Ledger.Recovery.Path);
        Assert.Equal(1UL, adopted.Ledger.CheckpointGeneration);
        await AssertRangeAsync(adopted, 0, 10);
    }

    /// <summary>
    /// Exhaustion is checked again under the checkpoint gate: a maintenance checkpoint which waited at the gate while an
    /// explicit one took the last generation skips the replica, writing and failing nothing, so exhaustion is recorded
    /// once, by the checkpoint which took the last generation, and a later explicit checkpoint fails (§8.8).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ACheckpointWaitingWhileAnotherTakesTheLastGenerationRecordsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 10);
        await replica.CheckpointAsync(cancellationToken);
        var start = replica.Ledger.Start;

        // The catalog checkpoint lists the replica, so the crafted root below is the replica's from its first file.
        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: false);

        // Slot 0 names the generation before the last, with no tree and the replica's start as its replay start.
        var slots = new byte[2 * 4096];
        new CheckpointRoot(CheckpointRoot.GenerationLimit - 2, 0, start, null, Sealed: false, default).Write(slots.AsSpan(0, 4096));
        root.Storage.DamageFile(SlotsPath, "slots.root", 0, slots);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.False(reopened.Ledger.CheckpointsExhausted);
        Assert.Null(reopened.Ledger.Exhaustion);
        await WriteRangeAsync(reopened, 10, 5);

        // The explicit checkpoint holds the gate while its root write waits.
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        root.Storage.NamedWriteFault = (_, name, _, _) =>
        {
            if (name == "slots.root")
            {
                writing.Set();
                _ = release.Wait(TimeSpan.FromSeconds(20));
            }

            return null;
        };
        var taking = Task.Run(async () => await reopened.CheckpointAsync(cancellationToken), cancellationToken);
        Assert.True(writing.Wait(TimeSpan.FromSeconds(20), cancellationToken));
        var waiting = reopened.Ledger.MaintainCheckpointAsync(cancellationToken).AsTask();
        Assert.False(waiting.IsCompleted);
        var recorded = reopened.Ledger.Exhaustion;
        release.Set();
        await taking;
        await waiting;
        root.Storage.NamedWriteFault = null;

        Assert.Null(recorded);
        Assert.Equal(CheckpointRoot.GenerationLimit - 1, reopened.Ledger.CheckpointGeneration);
        Assert.Equal(1, reopened.Ledger.CheckpointsWritten);
        Assert.True(reopened.Ledger.CheckpointsExhausted);
        var exhaustion = Assert.IsType<InvalidDataException>(reopened.Ledger.Exhaustion);
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.CheckpointAsync(cancellationToken));
        Assert.Same(exhaustion, reopened.Ledger.Exhaustion);
    }

    /// <summary>
    /// Slots across the address space grow the tree to every height; each height recovers, interior nodes in the
    /// oldest file are compacted forward, and the older root still recovers exactly when the newest is damaged.
    /// </summary>
    [Fact]
    public async Task TallTreesRecoverCompactAndFallBackToTheOlderRoot()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        var model = new Dictionary<long, byte[]>();
        foreach (var (first, count) in new (long, int)[] { (0, 300), ((1L << 16) + 5, 1), ((1L << 32) + 7, 3), (LedgerRecords.SlotLimit - 2, 2) })
        {
            for (var slot = first; slot < first + count; slot++)
            {
                model[slot] = Value(slot, 0);
                await LedgerStoreReplicaTests.WriteAsync(replica, slot, model[slot]);
            }

            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            await root.CrashAsync(replica, operatingSystem: true);
            replica = await root.OpenReplicaAsync(Window);
            Assert.Equal(LedgerStoreReplayPath.Checkpoint, replica.Ledger.Recovery.Path);
            await AssertModelAsync(replica, model, isSealed: false);
        }

        // Writing only leaf 1 leaves every other interior node in older files until compaction copies one leaf beneath
        // it.
        var initial = CheckpointFiles(root.Storage);
        for (var round = 1; round <= 3 * ExplicitOnly.MaxCheckpointFiles; round++)
        {
            var slot = 300L + round;
            model[slot] = Value(slot, round);
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, model[slot]);
            await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            Assert.True(replica.Ledger.CheckpointFileCount <= ExplicitOnly.MaxCheckpointFiles + 1, string.Create(CultureInfo.InvariantCulture, $"{replica.Ledger.CheckpointFileCount} files after round {round}."));
        }

        Assert.Empty(CheckpointFiles(root.Storage).Intersect(initial, StringComparer.Ordinal));

        // Before an open deletes what the newest root does not reference, the older root still names complete files.
        var newer = replica.Ledger.CheckpointGeneration;
        await root.CrashAsync(replica, operatingSystem: true);
        root.Storage.DamageFile(SlotsPath, "slots.root", RootSlot(root.Storage, newer) * 4096 + 20, [0xFF, 0xFF]);
        await using var fallback = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, fallback.Ledger.Recovery.Path);
        Assert.True(fallback.Ledger.Recovery.Generation < newer);
        await AssertModelAsync(fallback, model, isSealed: false);
    }

    /// <summary>
    /// A WAL file lost from the replayed suffix of an open is damage: the slots it may have held are faulty, and every
    /// slot whose latest record follows it reads normally.
    /// </summary>
    [Fact]
    public async Task ALostWalFileInTheReplayedSuffixFaultsTheSlotsBeforeIt()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 10);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        for (var slot = 10L; slot < 300; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: true);
        var files = root.Storage.ListFiles(WalPath);
        Assert.True(files.Count >= 4, $"The WAL holds only {files.Count} files.");
        root.Storage.RemoveFile(WalPath, files[^3]);

        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(Payload(299, 0), await LedgerReplicaTestData.ReadBytesAsync(reopened, 299, TestContext.Current.CancellationToken));
        foreach (var slot in new long[] { 0, 9, 10, 1_000 })
        {
            await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(slot), TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// LS-I11: the seal is a record in the replica's log, so a seal recorded after a damaged range of the replayed
    /// suffix still seals the recovered replica, while the slots the range may have held are faulty.
    /// </summary>
    [Fact]
    public async Task ASealRecordedAfterDamageStillSealsTheReplica()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 10);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        for (var slot = 10L; slot < 299; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        Assert.True((await replica.SealAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
        await replica.Store.CheckpointCatalogPastWritesAsync();
        await root.CrashAsync(replica, operatingSystem: true);
        root.Storage.RemoveFile(WalPath, (await FindRecordAsync(root.Storage, 100)).File);

        // A WAL file holds 63 one-record pages, so the seal record, after slot 298's, follows the lost one.
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.True(reopened.Ledger.IsSealed);
        Assert.True((await reopened.ReadTailAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
        Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(reopened, 299, Payload(299, 0)));
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken));
        await AssertRangeAsync(reopened, 298, 1);
    }

    /// <summary>
    /// A CRC-valid page holding a record of a handle the catalog never allocated is damage (ledgerstore.md §6.2) which
    /// faults every slot whose latest record precedes the next page, and every absent slot, across later checkpoints and
    /// restarts.
    /// </summary>
    [Fact]
    public async Task AnInvalidRecordOnAValidPageFaultsTheSlotsBeforeIt()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        for (var slot = 0L; slot < 10; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await root.CrashAsync(replica, operatingSystem: true);

        // The key's top three bytes are the handle: handle 7 lies at or above the catalog's next handle, 1.
        _ = await RewriteRecordAsync(root.Storage, 5, static page => page[RecordOffset + 5] = 7);

        replica = await root.OpenReplicaAsync(Window);
        foreach (var slot in new long[] { 0, 4, 5, 1_000 })
        {
            await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await replica.ReadAsync(new Slot(slot), TestContext.Current.CancellationToken));
        }

        await AssertRangeAsync(replica, 6, 4);
        Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, 4, Payload(4, 0)));
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: true);

        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.Checkpoint, reopened.Ledger.Recovery.Path);
        await AssertRangeAsync(reopened, 4, 1);
        await AssertRangeAsync(reopened, 6, 4);
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(3), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(1_000), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A checkpointed entry whose page later holds an empty slot record, which is damage, is caught by the point read,
    /// which faults only that slot, durably, until a recovery write re-establishes it (LR-I2, LR-I5).
    /// </summary>
    [Fact]
    public async Task APointReadOfAMismatchedRecordFaultsOnlyItsSlot()
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        for (var slot = 0L; slot < 10; slot++)
        {
            await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0));
        }

        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: true);

        // The DATA record for slot 5 becomes an empty value under the same key, which the checkpoint does not replay.
        var original = await RewriteRecordAsync(root.Storage, 5, static page =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(FrameOffset), FullFrame | LedgerRecords.KeyLength);
            page.AsSpan(RecordOffset + LedgerRecords.KeyLength).Clear();
        });
        replica = await root.OpenReplicaAsync(Window);
        Assert.Equal(0, replica.Ledger.Recovery.ReplayedRecords);
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await replica.ReadAsync(new Slot(5), TestContext.Current.CancellationToken));

        // The fault is checkpointed, so it survives a restart without the page being read again.
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        await root.CrashAsync(replica, operatingSystem: true);
        var (file, offset) = await FindRecordAsync(root.Storage, 5);
        root.Storage.Damage(WalPath, file, offset, original);
        await using var reopened = await root.OpenReplicaAsync(Window);
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await reopened.ReadAsync(new Slot(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await LedgerStoreReplicaTests.WriteAsync(reopened, 5, Payload(5, 0)));
        await AssertRangeAsync(reopened, 0, 5);
        await AssertRangeAsync(reopened, 6, 4);

        // Re-establishing the slot ends its fault everywhere.
        Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(reopened, 5, Payload(5, 0)));
        await AssertRangeAsync(reopened, 0, 10);
    }

    /// <summary>
    /// A checkpoint failing at any write, with mutations in flight, is reported, which stops the process
    /// (conventions.md §14), and loses no acknowledged mutation after either crash. Open deletes the file it orphaned, so
    /// nothing names its generation, and the next checkpoint takes that generation again (LS-I9).
    /// </summary>
    [Theory]
    [InlineData(".clsi", false)]
    [InlineData(".clsi", true)]
    [InlineData("slots.root", false)]
    [InlineData("slots.root", true)]
    public async Task ACheckpointFailingAtAnyWriteLosesNoAcknowledgedMutation(string failing, bool operatingSystem)
    {
        var root = NewRoot();
        var replica = await root.CreateReplicaAsync(Window);
        await WriteRangeAsync(replica, 0, 300);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        var older = replica.Ledger.CheckpointGeneration;

        // Slots 300 through 399 change leaf 1 below the settled frontier, so the checkpoint writes that leaf whatever
        // the mutations in flight in leaves 2 and 3 defer.
        await WriteRangeAsync(replica, 300, 100);
        while (replica.Store.Wal.DurableFrontier <= replica.Ledger.LastPublished)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        var reported = FatalErrors.ReportedCount;
        root.Storage.NamedWriteFault = (_, name, _, _) => name.EndsWith(failing, StringComparison.Ordinal) ? new IOException("injected checkpoint failure") : null;
        var mutations = Enumerable.Range(512, 200).Select(slot => LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(slot, 0))).ToArray();
        await Assert.ThrowsAsync<IOException>(async () => await replica.CheckpointAsync(TestContext.Current.CancellationToken));

        // A generation file fails at its creation, and a root slot at its write to the existing root file.
        _ = StorageFailures.TakeOne(reported, failing == ".clsi" ? FatalErrorSite.CreateFile : FatalErrorSite.HandleWriteGather, SlotsPath);

        // The mutations the stopped process answered are acknowledged; the others fail without an answer.
        var acknowledged = new List<long>();
        for (var index = 0; index < mutations.Length; index++)
        {
            try
            {
                Assert.Equal(ReplicaWriteCase.Written, (await mutations[index]).Case);
                acknowledged.Add(512 + index);
            }
            catch (IOException)
            {
                // The stopped process gave this mutation no answer.
            }
        }

        await root.CrashAsync(replica, operatingSystem);
        replica = await root.OpenReplicaAsync(Window);
        Assert.Equal(new LedgerStoreReplay(LedgerStoreReplayPath.Checkpoint, older, replica.Ledger.Recovery.ReplayedRecords), replica.Ledger.Recovery);
        await AssertRangeAsync(replica, 0, 400);
        await AssertSlotsAsync(replica, acknowledged);
        Assert.Equal(
            [SlotCheckpoints.FileName(older)],
            root.Storage.ListNamedFiles(SlotsPath).Where(static name => name.EndsWith(".clsi", StringComparison.Ordinal)));

        await WriteRangeAsync(replica, 712, 1);
        await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
        Assert.Equal(older + 1, replica.Ledger.CheckpointGeneration);
        await root.CrashAsync(replica, operatingSystem: true);
        await using var recovered = await root.OpenReplicaAsync(Window);
        await AssertRangeAsync(recovered, 0, 400);
        await AssertSlotsAsync(recovered, [.. acknowledged, 712]);

        static async Task AssertSlotsAsync(ILedgerReplica replica, IEnumerable<long> slots)
        {
            foreach (var slot in slots)
            {
                Assert.Equal(Payload(slot, 0), await LedgerReplicaTestData.ReadBytesAsync(replica, slot, TestContext.Current.CancellationToken));
            }
        }
    }

    /// <summary>
    /// A node file, catalog checkpoints, and checkpoint files without WAL files show the WAL was lost, so the store
    /// refuses both to be created over them and to open, and leaves the files in place; checkpoint directories without
    /// the WAL directory show it too, so a create refuses them (LS-I6).
    /// </summary>
    [Fact]
    public async Task ARootWhoseWalWasLostRefusesToStart()
    {
        var root = NewRoot();
        await using (var replica = await root.CreateReplicaAsync(Window))
        {
            await WriteRangeAsync(replica, 0, 10);
        }

        var files = root.Storage.ListNamedFiles(SlotsPath);
        Assert.Contains("slots.root", files);
        var walNamed = root.Storage.ListNamedFiles(WalPath);
        Assert.Contains(NodeFile.Name, walNamed);
        Assert.Contains(walNamed, static name => CatalogCheckpoints.TryParseFileName(name, out _));
        foreach (var file in root.Storage.ListFiles(WalPath))
        {
            root.Storage.RemoveFile(WalPath, file);
        }

        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync(create: true));
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync());
        Assert.Equal(files, root.Storage.ListNamedFiles(SlotsPath));
        Assert.Equal(walNamed, root.Storage.ListNamedFiles(WalPath));

        // §8.7 step 2: a root which lost its WAL directory holds no store without create, and a create refuses its
        // checkpoint directories rather than creating a WAL beside them.
        await root.Storage.DeleteDirectoryAsync(WalPath, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WalNotFoundException>(async () => await root.OpenAsync());
        await Assert.ThrowsAsync<LedgerReplicaCorruptException>(async () => await root.OpenAsync(create: true));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await root.Storage.OpenDirectoryAsync(WalPath, TestContext.Current.CancellationToken));
        Assert.Equal(files, root.Storage.ListNamedFiles(SlotsPath));
        Assert.Equal(0, root.Storage.OpenHandleCount);
    }

    private static SimulatedNodeRoot NewRoot(SimulatedWalStorage? storage = null, LedgerStoreCheckpointOptions? checkpointOptions = null) =>
        new(SmallFiles, checkpointOptions ?? ExplicitOnly, storage);

    private static async Task RunModelAsync(int seed, int rounds)
    {
        var random = new Random(seed);
        var root = NewRoot(new SimulatedWalStorage(seed));
        var replica = await root.CreateReplicaAsync(Window);

        // A checkpoint captures the replica's replay start at the durable frontier, and recovery adopts one only from past
        // the replica's create record, which the WAL answers before its frontier passes it (§8.7 step 6). Past this wait,
        // every checkpoint starts above the record, so the expected path below is exact;
        // ACheckpointStartingAtAReplayedCreateRecordIsAdoptedOnlyOnceACatalogCheckpointReflectsTheRecord covers one which
        // does not.
        await replica.Store.WaitForFrontierAsync();
        var model = new Dictionary<long, byte[]>();
        var isSealed = false;
        var version = 0;
        for (var round = 0; round < rounds; round++)
        {
            var draw = random.Next(0, 10);
            var operation = draw switch { >= 8 => Operation.Checkpoint, 5 => Operation.ProcessCrash, 6 => Operation.SystemCrash, 7 => Operation.Seal, _ => Operation.Mutate };
            if (operation is Operation.ProcessCrash or Operation.SystemCrash)
            {
                var expected = replica.Ledger.CheckpointGeneration != 0 ? LedgerStoreReplayPath.Checkpoint : LedgerStoreReplayPath.FullScan;
                await root.CrashAsync(replica, operation == Operation.SystemCrash);
                replica = await root.OpenReplicaAsync(Window);
                Assert.Equal(expected, replica.Ledger.Recovery.Path);
                AssertRebuiltTail(replica, model);
                await AssertModelAsync(replica, model, isSealed);
                continue;
            }

            if (operation == Operation.Seal)
            {
                // LS-I11: the seal is one record, which every later batch's writer writes meet.
                Assert.True((await replica.SealAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);
                isSealed = true;
            }

            // One batch mutates distinct slots concurrently, sometimes racing a checkpoint, so the model is exact.
            var slots = Enumerable.Range(0, random.Next(1, 120)).Select(_ => (long)(random.Next(0, 6) == 0 ? random.Next(0, 4_000) : random.Next(0, 400))).Distinct().ToArray();
            var writes = new List<(Task<ReplicaWriteOutcome> Write, ReplicaWriteCase Expected)>();
            foreach (var slot in slots)
            {
                var payload = Payload(slot, ++version);
                var held = model.ContainsKey(slot);
                if (random.Next(0, 2) == 0)
                {
                    // LR-I8: a writer write on a sealed replica stores nothing.
                    writes.Add((LedgerStoreReplicaTests.WriteAsync(replica, slot, payload), isSealed ? ReplicaWriteCase.Sealed : ReplicaWriteCase.Written));
                    if (!held && !isSealed)
                    {
                        model[slot] = payload;
                    }
                }
                else
                {
                    // LR-I1, LR-I5: a recovery write creates missing DATA, sealed or not, and leaves existing DATA.
                    writes.Add((LedgerStoreReplicaTests.RecoveryWriteAsync(replica, slot, payload), ReplicaWriteCase.Written));
                    if (!held)
                    {
                        model[slot] = payload;
                    }
                }
            }

            var checkpoint = operation == Operation.Checkpoint ? replica.CheckpointAsync(TestContext.Current.CancellationToken).AsTask() : Task.CompletedTask;
            foreach (var (write, expected) in writes)
            {
                Assert.Equal(expected, (await write).Case);
            }

            await checkpoint;
        }

        await AssertModelAsync(replica, model, isSealed);
        await replica.DisposeAsync();

        // A full-scan rebuild of the same WAL holds the same table.
        await using (var directory = await root.Storage.OpenDirectoryAsync(SlotsPath, TestContext.Current.CancellationToken))
        {
            foreach (var name in await directory.ListNamedFilesAsync(".clsi", TestContext.Current.CancellationToken))
            {
                await directory.DeleteNamedFileAsync(name, TestContext.Current.CancellationToken);
            }

            await directory.DeleteNamedFileAsync("slots.root", TestContext.Current.CancellationToken);
        }

        await using var rebuilt = await root.OpenReplicaAsync(Window);
        Assert.Equal(LedgerStoreReplayPath.FullScan, rebuilt.Ledger.Recovery.Path);
        AssertRebuiltTail(rebuilt, model);
        await AssertModelAsync(rebuilt, model, isSealed);
    }

    /// <summary>
    /// Returns the newest valid root in the durable root file.
    /// </summary>
    private static CheckpointRoot InForceRoot(SimulatedWalStorage storage)
    {
        var bytes = storage.ReadDurableFile(SlotsPath, "slots.root")!;
        return new[] { 0, 1 }
            .Select(slot => CheckpointRoot.Decode(bytes.AsSpan(slot * 4096, 4096)).Value)
            .OfType<CheckpointRoot>()
            .MaxBy(static root => root.Generation)!;
    }

    /// <summary>
    /// Loses every WAL page after the one holding the byte before <paramref name="cut"/>, and every later file, as a
    /// lost tail would; pages are written whole, so the page holding the last record below the cut survives.
    /// </summary>
    private static void LoseWalAfter(SimulatedWalStorage storage, WalPosition cut)
    {
        var pageSize = SmallFiles.PageSize;
        storage.TruncateFile(WalPath, cut.File, ((long)cut.Offset + pageSize - 1) / pageSize * pageSize);
        foreach (var file in storage.ListFiles(WalPath).Where(file => file > cut.File))
        {
            storage.RemoveFile(WalPath, file);
        }
    }

    private static string[] CheckpointFiles(SimulatedWalStorage storage) =>
        [.. storage.ListNamedFiles(SlotsPath).Where(static name => name.EndsWith(".clsi", StringComparison.Ordinal))];

    /// <summary>
    /// Rewrites the replica's checkpoint as one generation with the root fields and leaves of the one in force, but with
    /// leaf <paramref name="leaf"/>'s object holding <paramref name="bytes"/>, under a reference whose CRC they match.
    /// </summary>
    private static async Task ReplaceLeafAsync(SimulatedWalStorage storage, long leaf, byte[] bytes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var directory = await storage.OpenDirectoryAsync(SlotsPath, cancellationToken);
        var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, cancellationToken);
        var leaves = new List<CheckpointLeaf>();
        await using (checkpoints)
        {
            Assert.NotNull(loaded);
            foreach (var (number, reference) in loaded.Leaves.OrderBy(static entry => entry.Key))
            {
                leaves.Add(new CheckpointLeaf(number, number == leaf ? bytes : await checkpoints.ReadLeafBytesAsync(reference, cancellationToken)));
            }
        }

        foreach (var name in storage.ListNamedFiles(SlotsPath))
        {
            await directory.DeleteNamedFileAsync(name, cancellationToken);
        }

        await using var rewritten = SlotCheckpoints.Create(directory);
        _ = await rewritten.WriteAsync(loaded.ReplayStart, loaded.CorruptionEnd, loaded.Sealed, leaves, cancellationToken);
    }

    /// <summary>
    /// Rewrites the page holding a slot's only record and seals the page's CRC again, as damage which preserves it,
    /// returning the page as it was.
    /// </summary>
    private static async Task<byte[]> RewriteRecordAsync(SimulatedWalStorage storage, long slot, Action<byte[]> mutate)
    {
        var (file, offset) = await FindRecordAsync(storage, slot);
        await using var directory = await storage.OpenDirectoryAsync(WalPath, TestContext.Current.CancellationToken);
        await using var handle = await directory.OpenFileAsync(file, TestContext.Current.CancellationToken);
        using var contents = await handle.ReadAsync(offset, SmallFiles.PageSize, TestContext.Current.CancellationToken);
        var original = contents.ToArray();
        var page = (byte[])original.Clone();
        mutate(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, page.AsSpan(FrameOffset)));
        storage.Damage(WalPath, file, offset, page);
        return original;
    }

    /// <summary>
    /// Finds the page starting with a Full frame of a slot's record, whose key of handle 0 is the slot.
    /// </summary>
    private static async Task<(ulong File, long Offset)> FindRecordAsync(SimulatedWalStorage storage, long slot)
    {
        var pageSize = SmallFiles.PageSize;
        await using var directory = await storage.OpenDirectoryAsync(WalPath, TestContext.Current.CancellationToken);
        foreach (var file in await directory.ListFilesAsync(TestContext.Current.CancellationToken))
        {
            await using var handle = await directory.OpenFileAsync(file, TestContext.Current.CancellationToken);
            using var contents = await handle.ReadAsync(0, (int)handle.Length, TestContext.Current.CancellationToken);
            var bytes = contents.ToArray();
            for (var offset = pageSize; offset + pageSize <= bytes.Length; offset += pageSize)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + FrameOffset)) >> 30 == FullFrame >> 30
                    && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + RecordOffset)) == slot)
                {
                    return (file, offset);
                }
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No page holds slot {slot}."));
    }

    /// <summary>
    /// Asserts that every slot the model names holds its value, that every other slot of the model's range is Missing,
    /// and that the replica is sealed exactly when the model is (LS-I11).
    /// </summary>
    private static async Task AssertModelAsync(StoreOwningReplica replica, Dictionary<long, byte[]> model, bool isSealed)
    {
        Assert.Equal(isSealed, (await replica.ReadTailAsync(TestContext.Current.CancellationToken)).Tail.IsSealed);

        foreach (var (slot, value) in model)
        {
            Assert.Equal(value, await LedgerReplicaTestData.ReadBytesAsync(replica, slot, TestContext.Current.CancellationToken));
        }

        var dense = model.Keys.Where(static slot => slot < 4_096).DefaultIfEmpty(-1).Max() + 1;
        for (var slot = 0L; slot < dense; slot++)
        {
            if (!model.ContainsKey(slot))
            {
                Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, slot, TestContext.Current.CancellationToken));
            }
        }
    }

    /// <summary>
    /// LR-I6: a reopened replica retains <c>max(0, h + 1 − K)</c> for its highest slot <c>h</c> and its window.
    /// </summary>
    private static void AssertRebuiltTail(StoreOwningReplica replica, Dictionary<long, byte[]> model) =>
        Assert.Equal(new Slot(Math.Max(0, model.Keys.DefaultIfEmpty(-1).Max() + 1 - Window.WindowSize)), replica.Ledger.RetainedContiguousTail);

    private static int RootSlot(SimulatedWalStorage storage, ulong generation)
    {
        var bytes = storage.ReadDurableFile(SlotsPath, "slots.root")!;
        for (var slot = 0; slot < 2; slot++)
        {
            if (BitConverter.ToUInt64(bytes, slot * 4096 + 16) == generation)
            {
                return slot;
            }
        }

        throw new InvalidOperationException($"No root slot holds generation {generation}.");
    }

    private static async Task WriteRangeAsync(ILedgerReplica replica, long first, int count) =>
        await Task.WhenAll(Enumerable.Range(0, count).Select(index => LedgerStoreReplicaTests.WriteAsync(replica, first + index, Payload(first + index, 0))));

    private static async Task AssertRangeAsync(ILedgerReplica replica, long first, int count)
    {
        for (var slot = first; slot < first + count; slot++)
        {
            Assert.Equal(Payload(slot, 0), await LedgerReplicaTestData.ReadBytesAsync(replica, slot, TestContext.Current.CancellationToken));
        }
    }

    private static byte[] Payload(long slot, int version) => LedgerReplicaTestData.CreatePayload(16 + (int)(slot % 97), (int)(slot * 31 + version));

    private static byte[] Value(long slot, int version) => LedgerReplicaTestData.CreatePayload(24, unchecked((int)slot ^ (int)(slot >> 32) ^ (version << 20)));

    /// <summary>
    /// A clock over <see cref="FakeTimeProvider"/> whose timers fail with <see cref="Fault"/> once it is set, as a defect
    /// in maintenance's wait would fail it; the WAL of unpreallocated files creates no timer.
    /// </summary>
    private sealed class FailingTimers : TimeProvider
    {
        private readonly FakeTimeProvider _fake = new();

        public Exception? Fault
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public override long TimestampFrequency => _fake.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

        public override long GetTimestamp() => _fake.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Fault is { } fault ? throw fault : _fake.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>
    /// A clock over <see cref="FakeTimeProvider"/> which observes pending maintenance waits and whose reads by maintenance
    /// can be held until a test releases them.
    /// </summary>
    private sealed class ObservedTime : TimeProvider, IDisposable
    {
        private readonly SemaphoreSlim _timers = new(0);
        private readonly ConcurrentQueue<Func<bool>> _created = new();
        private int _heldReads;

        public FakeTimeProvider Fake { get; } = new();

        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        /// <summary>Gets how many of maintenance's reads of the clock found the gate closed.</summary>
        public int HeldReads => Volatile.Read(ref _heldReads);

        public int TimersCreatedSinceLastWait => _timers.CurrentCount;

        public override long TimestampFrequency => Fake.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

        public override long GetTimestamp()
        {
            // The gate holds maintenance's check for work, which reads the clock under LedgerStore.MaintainAsync's state
            // machine, so a test can publish records before maintenance first checks; the open's and the WAL's reads pass
            // through, so the open does not wait out the gate.
            if (!Gate.IsSet && InMaintenance())
            {
                _ = Interlocked.Increment(ref _heldReads);
                _ = Gate.Wait(TimeSpan.FromSeconds(20));
            }

            return Fake.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ObservedTimer(Fake, callback, state, dueTime, period);
            _created.Enqueue(() => timer.Pending);
            _ = _timers.Release();
            return timer;
        }

        /// <summary>
        /// Waits for a pending timer after the expected work. Wakes cancel delays, so queued creation notifications
        /// alone do not establish that maintenance is waiting.
        /// </summary>
        public async Task NextTimerAsync(Func<bool>? completed = null)
        {
            while (true)
            {
                Assert.True(await _timers.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "The background task never waited after the expected work.");
                Assert.True(_created.TryDequeue(out var pending));
                if (pending() && (completed?.Invoke() ?? true))
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _timers.Dispose();
            Gate.Dispose();
        }

        private static bool InMaintenance() =>
            new StackTrace(2, fNeedFileInfo: false).GetFrames().Any(static frame =>
                frame.GetMethod()?.DeclaringType is { } type
                && type.DeclaringType == typeof(LedgerStore)
                && type.Name.StartsWith("<MaintainAsync>", StringComparison.Ordinal));

        private sealed class ObservedTimer : ITimer
        {
            private readonly ITimer _timer;
            private int _pending = 1;

            public ObservedTimer(FakeTimeProvider time, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _timer = time.CreateTimer(
                    value =>
                    {
                        Volatile.Write(ref _pending, 0);
                        callback(value);
                    },
                    state,
                    dueTime,
                    period);
            }

            public bool Pending => Volatile.Read(ref _pending) != 0;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Volatile.Write(ref _pending, dueTime == Timeout.InfiniteTimeSpan ? 0 : 1);
                return _timer.Change(dueTime, period);
            }

            public void Dispose()
            {
                Volatile.Write(ref _pending, 0);
                _timer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Volatile.Write(ref _pending, 0);
                return _timer.DisposeAsync();
            }
        }
    }
}
