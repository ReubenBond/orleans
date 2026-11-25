using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Recovers the LedgerStore from every crash state its cluster claim, node file, directories, create, seal, and drop
/// records, catalog checkpoints, slot-index checkpoints, and WAL writes allow under the file system's persistence model
/// (verification.md §7.5, ledgerstore.md §6.1). Each workload runs over the real directory implementation, the portable
/// storage's files, and the real claim on a <see cref="RecordingFileSystem"/> rooted at the node root, which exists
/// durably and above which no flush reaches (ledgerstore.md §3.3); every enumerated state must open with every
/// acknowledged creation listed under the handle it took and every acknowledged drop absent, refused, and held as a
/// tombstone (LS-I1, LS-I2), each replica's checkpoint holding exactly its WAL records below its cut, its seal record
/// included (LS-I8), the root in force at or above every acknowledged checkpoint with every file it references readable
/// (LS-I9), every acknowledged mutation and every DATA a read answered readable, every replica whose seal an answer
/// reported sealed, and every replica no seal had reached unsealed (LS-I11). In every state the first retained WAL file
/// lies at or below the cut of the older of the two newest valid catalog checkpoints, a released file 1 implies two valid
/// ones, the older kept one derives the same catalog when the newest is damaged (LS-I3, LS-I4), and with every catalog
/// checkpoint deleted the open refuses unless no create or drop record was ever written (LS-I5). A sample of the states
/// is recovered again from every crash state of its own recovery.
/// </summary>
public sealed partial class LedgerStoreCrashStateTests : IDisposable
{
    /// <summary>
    /// The most crash states one workload recovers; the seed samples beyond it.
    /// </summary>
    private const int Limit = 2_000;

    /// <summary>
    /// Gets a value indicating whether Stryker.NET runs this test application (verification.md §9).
    /// </summary>
    public static bool UnderMutationTesting => TestDiagnostics.UnderMutationTesting;

    /// <summary>
    /// The crash states of a workload whose own recovery is crashed and recovered again, chosen by the seed.
    /// </summary>
    private const int RecoveryCrashSample = 6;

    /// <summary>
    /// The most crash states recovered after each failed operation of a workload; the seed samples beyond it.
    /// </summary>
    private const int FailedOperationLimit = 200;

    /// <summary>
    /// A slot no workload writes, which every recovered replica reads as Missing.
    /// </summary>
    private const long Unwritten = 4_000;

    /// <summary>
    /// A slot no workload writes, below the window, to which the check of a replica a workload seals sends a writer
    /// write: one which a recovery keeps moves no rebuilt contiguous tail.
    /// </summary>
    private const long Probe = 63;

    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-node"));

    private static readonly string NodeRoot = Root;

    // Unpreallocated files of which no workload fills a quarter, so the WAL prepares no file in the background. Four
    // write groups may be in flight at once.
    private static readonly LedgerStoreOptions Options = new()
    {
        DirectoryPath = NodeRoot,
        PageSize = 4096,
        FileSize = 256 * 4096,
        PreallocateFiles = false,
        MinWriteGroupsInFlight = 4,
        MaxWriteGroupsInFlight = 4,
        WriteWindowPages = 8,
    };

    // Preallocated files of 15 data pages, as the store's default writes them: the WAL prepares the next file once a
    // quarter of the current one is written.
    private static readonly LedgerStoreOptions Preallocated = Options with { FileSize = 16 * 4096, PreallocateFiles = true };

    // Checkpoints only on request; a root referencing more than two generation files compacts the oldest. A close writes
    // its final checkpoints one at a time, so each trace replays from its seed.
    private static readonly LedgerStoreCheckpointOptions Checkpoints = new() { CheckpointInterval = TimeSpan.FromDays(1), MaxCheckpointFiles = 2, FinalCheckpointConcurrency = 1 };

    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    private static readonly LedgerId Unheld = new(5);
    private static readonly LedgerId S1 = new(10);
    private static readonly LedgerId S2 = new(20);
    private static readonly LedgerId S3 = new(30);
    private static readonly LedgerId S4 = new(40);
    private static readonly LedgerId S9 = new(90);

    private enum StepKind
    {
        Create,
        Drop,
        Write,
        Checkpoint,

        /// <summary>An answer which reports the replica sealed: a seal, or a contiguous-tail read which met one.</summary>
        Seal,

        /// <summary>A writer write which the seal refused, answering <see cref="ReplicaAnswers.Sealed"/>.</summary>
        SealedWrite,

        /// <summary>A read of a slot, which answered its DATA, or that it is missing when <c>Value</c> is null.</summary>
        Read,
    }

    private enum Presence
    {
        Absent,
        Present,
        Either,
    }

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>Waits until <paramref name="condition"/> holds, which a step the test does not hold makes true, failing once 30 s pass.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30), $"Waited 30 s for {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
    }

    /// <summary>
    /// Creates the store, then creates and drops replicas, including a drop of a ledger the node never held, across a
    /// restart, so create and drop records are written, more than three catalog checkpoints are written with drops between
    /// them and the older ones deleted, checkpoint directories are created and deleted, and, once two catalog checkpoints
    /// cover them, the WAL file holding the first drop records is released. A read which meets a drop's closed admission
    /// while its record is in flight answers dropped only once the record is durable, so no crash state loses a drop an
    /// operation answered (LS-I2).
    /// </summary>
    [Fact]
    public Task ReplicaCreatesAndDropsRecoverFromEveryCrashState() => RunAsync(0x5e61, writeThrough: false, Options, async (recorder, _) =>
    {
        await recorder.DropAsync(Unheld);
        await recorder.CreateAsync(S1);
        await recorder.CreateAsync(S2);
        await recorder.WriteAsync(S1, 0, 1);
        await recorder.WriteAsync(S2, 0);
        await recorder.DropAnsweringAReadAsync(S2);
        await recorder.WriteAsync(S1, 2);
        await recorder.CloseAsync();
        await recorder.OpenAsync();
        await recorder.CreateAsync(S3);
        await recorder.WriteAsync(S3, 0);
        await recorder.DropAnsweringAReadAsync(S1);
        await recorder.CreateAsync(S4);
        await recorder.WriteAsync(S4, 0);
        await recorder.DropAsync(S3);
        await recorder.WriteAsync(S4, 1);
        await recorder.CloseAsync();
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.MakeDirectory && operation.Path == LedgerStore.WalDirectoryName, "the WAL directory's creation");
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Rename && operation.Target == NodeFile.Name, "the node file's publication");
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Unlink && operation.Path.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal), "an older catalog checkpoint deleted");
        var published = recorder.FileSystem.Operations.Count(static operation => operation.Kind == FileSystemOperationKind.Rename && operation.Target!.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal));
        Assert.True(published > 3, Invariant($"The workload published {published} catalog checkpoints, not more than three."));
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Link && operation.Name == LedgerStoreClusterClaims.Name, "the cluster claim's publication");
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.RemoveDirectory, "a checkpoint directory deleted");
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Unlink && operation.Path.EndsWith(".clfl", StringComparison.Ordinal), "a WAL file released");
    });

    /// <summary>
    /// ledgerstore.md §8.10, #411: a close whose caller's bound passes once its first final checkpoint has started lets that
    /// one finish and starts no other, so two replicas keep their checkpoints in force and the drained WAL above them, as
    /// after a crash once the WAL drained; every crash state of the close, and of the open which replays them, recovers every
    /// acknowledged write, and a later close checkpoints all three.
    /// </summary>
    [Fact]
    public Task ACloseWhoseBoundSkipsFinalCheckpointsRecoversFromEveryCrashState() => RunAsync(0x5e6d, writeThrough: true, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.CreateAsync(S2);
        await recorder.CreateAsync(S3);
        await recorder.WriteAsync(S1, 0, 1);
        await recorder.WriteAsync(S2, 0);
        await recorder.CheckpointAsync(S2);
        await recorder.WriteAsync(S2, 1);
        await recorder.WriteAsync(S3, 0);
        var closed = await recorder.CloseWithinABoundAsync(finals: 1);
        await recorder.OpenAsync();
        await recorder.WriteAsync(S3, 1);
        await recorder.CloseAsync();
        Assert.Equal(new LedgerStoreCloseResult(1, 2), closed);
    });

    /// <summary>
    /// ledgerstore.md §8.10, #411: a close writes its final checkpoints eight at a time, and once its caller's bound passes
    /// starts no more. Over three hundred replicas, each written past its create record, the test holds every final's root
    /// write until eight are held, which no ninth joins, then passes the bound and lets the eight finish: the close ends with
    /// eight finals written and the rest skipped, no clock involved, and the next open recovers every replica's write, the
    /// skipped ones by replaying the WAL above their replay starts.
    /// </summary>
    [Fact]
    public async Task ACloseWritesFinalCheckpointsEightAtATimeAndStartsNoneOnceItsBoundPasses()
    {
        const int Count = 300;
        const int Concurrency = 8;
        var checkpoints = Checkpoints with { FinalCheckpointConcurrency = Concurrency };
        LedgerId[] ledgers = [.. Enumerable.Range(1, Count).Select(static index => new LedgerId((ulong)(1_000 + index)))];
        var fileSystem = new RecordingFileSystem(Root, writeThrough: true);
        var store = await OpenAsync(fileSystem, create: true);
        foreach (var ledger in ledgers)
        {
            _ = await store.CreateLiveAsync(ledger, Window.WindowSize, Token);
            await using var replica = Assert.IsType<LedgerStoreReplica>(store.OpenReplica(ledger, Window));
            await LedgerStoreReplicaTests.WriteAsync(replica, 0, Value(ledger));
        }

        var replayStarts = ledgers.ToDictionary(ledger => ledger, ledger => store.FindLive(ledger)!.ReplayStart);
        using var bound = new CancellationTokenSource();
        fileSystem.HoldWrites = static path => path.EndsWith("/" + SlotCheckpoints.RootName, StringComparison.Ordinal);
        var closing = store.CloseAsync(bound.Token).AsTask();
        await WaitUntilAsync(() => fileSystem.HeldWrites.Count == Concurrency, "eight final checkpoints to hold their root writes");
        Assert.False(closing.IsCompleted);
        Assert.Equal(Concurrency, fileSystem.HeldWrites.Select(static held => held.Path).Distinct().Count());

        await bound.CancelAsync();
        fileSystem.HoldWrites = null;
        foreach (var held in fileSystem.HeldWrites)
        {
            held.Complete();
        }

        Assert.Equal(new LedgerStoreCloseResult(Concurrency, Count - Concurrency), await closing);

        await using var reopened = await OpenAsync(fileSystem, create: false);
        var advanced = 0;
        foreach (var ledger in ledgers)
        {
            advanced += reopened.FindLive(ledger)!.ReplayStart > replayStarts[ledger] ? 1 : 0;
            await using var replica = Assert.IsType<LedgerStoreReplica>(reopened.OpenReplica(ledger, Window));
            Assert.Equal(Value(ledger), await LedgerReplicaTestData.ReadBytesAsync(replica, 0, Token));
        }

        Assert.Equal(Concurrency, advanced);

        ValueTask<LedgerStore> OpenAsync(RecordingFileSystem files, bool create) =>
            LedgerStore.OpenAsync(files.Storage, Options, LedgerReplicaTestData.Cluster, create, checkpoints, new FakeTimeProvider(), FileNodeRootClaim.Claim(NodeRoot, flushToDisk: true, files), Token);

        static byte[] Value(LedgerId ledger) => LedgerReplicaTestData.CreatePayload(32, (int)ledger.Value);
    }

    /// <summary>
    /// Checkpoints one leaf after another until compaction copies the oldest generation's leaves forward and its file is
    /// reclaimed, then closes, reopens, and checkpoints again.
    /// </summary>
    [Fact]
    public Task CheckpointsAndCompactionRecoverFromEveryCrashState() => RunAsync(0x5e62, writeThrough: false, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        for (var leaf = 0L; leaf < 5; leaf++)
        {
            await recorder.WriteAsync(S1, leaf * 256);
            await recorder.CheckpointAsync(S1);
        }

        await recorder.CloseAsync();
        await recorder.OpenAsync();
        await recorder.WriteAsync(S1, 1);
        await recorder.CheckpointAsync(S1);
        await recorder.CloseAsync();
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Unlink && operation.Path.EndsWith(".clsi", StringComparison.Ordinal), "a generation file reclaimed");
    });

    /// <summary>
    /// With write-through files, a create record's write is held while later writes of a listed replica complete and
    /// become durable above it, so crash states hold the torn create beneath intact later records; the creation then
    /// completes, and a later one follows it (ledgerstore.md §8.5, §8.7).
    /// </summary>
    [Fact]
    public Task ACreateRecordTornBeneathLaterRecordsRecoversFromEveryCrashState() => RunAsync(0x5e67, writeThrough: true, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0);
        await recorder.RaceCreateAsync(S2, S1, [1, 2, 300]);
        await recorder.WriteAsync(S2, 0);
        await recorder.CreateAsync(S3);
        await recorder.DropAsync(S1);
        await recorder.CloseAsync();
    });

    /// <summary>
    /// With write-through files, a first write to a ledger the node does not hold issues its create record, which is
    /// held while later writes of a listed replica complete above it, so the first write waits on a create which crash
    /// states hold torn beneath intact later records (ledgerstore.md §8.5); the creation then completes, and the write
    /// follows it.
    /// </summary>
    [Fact]
    public Task AFirstWriteWhoseCreateIsHeldBeneathLaterRecordsRecoversFromEveryCrashState() => RunAsync(0x5e68, writeThrough: true, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0);
        await recorder.RaceFirstWriteAsync(S2, S1, [1, 2, 300]);
        await recorder.WriteAsync(S2, 1);
        await recorder.CloseAsync();
    });

    /// <summary>
    /// With write-through files, creations which wait behind a held create record share the next one, which holds all
    /// their pairs, with consecutive handles, so every crash state lists all of them or none (ledgerstore.md §7.3, §8.5).
    /// </summary>
    [Fact]
    public Task CreationsWaitingBehindAHeldCreateShareOneRecordInEveryCrashState() => RunAsync(0x5e69, writeThrough: true, Options, async (recorder, _) =>
    {
        await recorder.BatchCreateAsync(S1, [S2, S3, S4]);
        await recorder.WriteAsync(S3, 0);
        await recorder.CloseAsync();
        Assert.Equal(4u, recorder.Steps.Where(static step => step.Kind == StepKind.Create).Max(static step => step.Handle) + 1);
    });

    /// <summary>
    /// With write-through files, a held write keeps the durable frontier below a drop record which completes above it, so
    /// the catalog checkpoint captured then is cut strictly past the drop record rather than at the frontier
    /// (ledgerstore.md §8.8), and every crash state recovers the drop.
    /// </summary>
    [Fact]
    public Task ACatalogCheckpointCapturedAboveTheFrontierRecoversFromEveryCrashState() => RunAsync(0x5e6a, writeThrough: true, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.CreateAsync(S2);
        await recorder.WriteAsync(S1, 0);
        await recorder.DropAboveTheFrontierAsync(S2, S1, 1);
        await recorder.WriteAsync(S1, 2);
        await recorder.CloseAsync();
    });

    /// <summary>
    /// With write-through files and a one-page write window, a create record which begins in the last bytes of a page
    /// holding a durable write and ends in the next keeps the durable frontier where it begins while its last page is in
    /// flight, so the catalog checkpoint captured then is cut exactly at the create record, which it does not reflect:
    /// every crash state replays the create record from the cut (ledgerstore.md §8.7 step 6, §8.8).
    /// </summary>
    [Fact]
    public Task ACatalogCheckpointCutWhereACreateRecordBeginsRecoversFromEveryCrashState() => RunAsync(0x5e6b, writeThrough: true, Options with { WriteWindowPages = 1 }, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0);
        await recorder.CaptureWhereACreateRecordBeginsAsync(S1, held: 1, filler: 2, S2);
        await recorder.WriteAsync(S2, 0);
        await recorder.CloseAsync();
    });

    /// <summary>
    /// With the portable storage's files in their Windows shape, write-through WAL writes in flight complete in seeded
    /// order, each durable alone once the file's length covers it, and a checkpoint follows each completion while the
    /// others hold the durable frontier below records already published.
    /// </summary>
    [Fact]
    public Task OutOfOrderWalCompletionsRacingCheckpointsRecoverFromEveryCrashState() => RunAsync(0x5e63, writeThrough: true, Options, async (recorder, random) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0, 1);
        await recorder.CheckpointAsync(S1);
        await recorder.RaceAsync(S1, [2, 3, 300, 4], random);
        await recorder.CloseAsync();
    });

    /// <summary>
    /// With preallocated files, as the store's default writes them: records land in zero-filled space, the WAL prepares
    /// the next file once a quarter of the current one is written, and the log rolls into it. The preparation is held
    /// until a fixed step, so the trace follows from the workload and its seed.
    /// </summary>
    [Fact]
    public Task PreallocatedFilesAndAPreparedNextFileRecoverFromEveryCrashState() => RunAsync(0x5e65, writeThrough: false, Preallocated, async (recorder, _) =>
    {
        recorder.FileSystem.HoldCreations = static path => path.Contains("prealloc-", StringComparison.Ordinal) && path.EndsWith(WalFormat.TemporaryExtension, StringComparison.Ordinal);
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0, 1, 2, 3);
        await recorder.PrepareNextFileAsync();

        // The create record and these writes fill the first file and two pages of the next, short of the quarter at which
        // the WAL would prepare a third.
        await recorder.WriteAsync(S1, [.. Enumerable.Range(4, 12).Select(static slot => (long)slot)]);
        await recorder.CheckpointAsync(S1);
        await recorder.CloseAsync();
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Rename && operation.Target == "00000000000000000002.clfl", "a prepared file's publication");
        AssertTraceHolds(recorder, static operation => operation.Kind == FileSystemOperationKind.Write && operation.Path.EndsWith("00000000000000000002.clfl", StringComparison.Ordinal), "a record in the prepared file");
    });

    /// <summary>
    /// With write-through files, a checkpoint's root write reaches the device and then fails, as a device error reports
    /// it: the failure is reported, which stops the process (conventions.md §14), and its generation file and root slot,
    /// which name a generation no checkpoint took, stay where a crash may keep them. The restarted process loads the
    /// failed root when it is usable, and otherwise keeps the previous one and deletes the generation's file, then
    /// checkpoints again (ledgerstore.md §8.8, LS-I9).
    /// </summary>
    [Fact]
    public Task ACheckpointWhoseRootWriteFailsAfterReachingTheDeviceRecoversFromEveryCrashState() => RunAsync(0x5e66, writeThrough: true, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0, 1);
        await recorder.CheckpointAsync(S1);
        await recorder.WriteAsync(S1, 256);
        await recorder.FailCheckpointRootAsync(S1);
        await recorder.RestartAsync();
        await recorder.WriteAsync(S1, 257);
        await recorder.CheckpointAsync(S1);
        await recorder.CloseAsync();
        AssertTraceHolds(
            recorder,
            static operation => operation.Kind == FileSystemOperationKind.FailWrite && operation.Path.EndsWith("/" + SlotCheckpoints.RootName, StringComparison.Ordinal),
            "a root write which failed after reaching the device");
    });

    /// <summary>
    /// Writes two replicas, then seals one with its seal record's fsync held while a writer write and a contiguous-tail read
    /// meet the seal, so each answers, <see cref="ReplicaAnswers.Sealed"/> and sealed, only once the record is durable; takes a
    /// recovery write on the sealed replica; seals a ledger the node does not hold, which creates its replica sealed; and
    /// writes a catalog checkpoint and a slot checkpoint past the seal records, so crash states recover each seal from its
    /// record and from the root which records it. After a restart a writer write is refused, a recovery write taken, and the
    /// sealed replica dropped. In every crash state an answered seal holds, a replica no seal had reached is unsealed, a
    /// writer write the seal refused stored nothing, and the other replica stays unsealed (LS-I11, LS-I1, LS-I2,
    /// conventions.md §13).
    /// </summary>
    [Fact]
    public Task SealRecordsRecoverFromEveryCrashState() => RunAsync(0x5e6c, writeThrough: false, Options, async (recorder, _) =>
    {
        await recorder.CreateAsync(S1);
        await recorder.CreateAsync(S2);
        await recorder.WriteAsync(S1, 0, 1);
        await recorder.WriteAsync(S2, 0);
        await recorder.SealAnsweringAWriteAsync(S1, 2);
        await recorder.RecoveryWriteAsync(S1, 3);
        await recorder.SealAsync(S3);
        await recorder.CheckpointCatalogAsync();
        await recorder.CheckpointAsync(S1);
        await recorder.WriteAsync(S2, 1);
        await recorder.CloseAsync();
        await recorder.OpenAsync();
        await recorder.SealedWriteAsync(S1, 4);
        await recorder.RecoveryWriteAsync(S3, 0);
        await recorder.DropAsync(S1);
        await recorder.WriteAsync(S2, 2);
        await recorder.CloseAsync();
        Assert.Equal(2, recorder.Steps.Count(static step => step.Kind == StepKind.SealedWrite && step.Acked != int.MaxValue));
        Assert.Contains(recorder.Steps, static step => step.Kind == StepKind.Create && step.Ledger == S3 && step.Acked != int.MaxValue);
    });

    /// <summary>
    /// The process crashes after each operation of a workload in turn, a restarted process finishes it, and the
    /// operating system then crashes at any later point: the page cache survives the process crash, and only what is
    /// durable survives the operating-system crash, so the restarted process must make durable what the crashed one
    /// left unsynced before it acknowledges anything which depends on it. The traces replay from the seed.
    /// </summary>
    [Fact(
        Skip = "Mutation runs leave out the longest crash-state enumeration (verification.md §9).",
        SkipWhen = nameof(UnderMutationTesting))]
    public async Task AProcessCrashFollowedByAnOperatingSystemCrashRecoversFromEveryCrashState()
    {
        const int Seed = 0x5e64;
        await SeededSimulation.RunAndReplayAsync(Seed, async run =>
        {
            var workload = (await RecordAcrossAProcessCrashAsync(crashAfter: null)).FileSystem.OperationCount;
            for (var crashAfter = 1; crashAfter <= workload; crashAfter++)
            {
                var recorder = await RecordAcrossAProcessCrashAsync(crashAfter);
                foreach (var operation in Trace(recorder))
                {
                    run.Record(Invariant($"{crashAfter}: {operation}"));
                }
            }
        });

        await SeededSimulation.RunAsync(Seed, async run =>
        {
            var workload = (await RecordAcrossAProcessCrashAsync(crashAfter: null)).FileSystem.OperationCount;
            var points = 0;
            var sampledPoints = 0;
            var verified = 0;
            for (var crashAfter = 1; crashAfter <= workload; crashAfter++)
            {
                var recorder = await RecordAcrossAProcessCrashAsync(crashAfter);
                Assert.True(recorder.Steps.Count != 0, "The restarted process acknowledged nothing.");
                var states = await VerifyAsync(run, recorder, from: crashAfter, int.MaxValue, recoveryCrashes: 0);
                points += states.Points;
                sampledPoints += states.SampledPoints;
                verified += states.States.Count;
            }

            TestDiagnostics.WriteLine(Invariant($"{workload} process crashes, {points} crash points after them ({sampledPoints} sampled), {verified} crash states verified"));
        });
    }

    [GeneratedRegex(@"\.cluster\.[0-9a-f]{32}\.tmp", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex TemporaryClaim();

    /// <summary>
    /// Conventions.md §14: each recorded operation of the workload in turn fails as a device error fails it, recording
    /// nothing; the failure is reported, which stops the process there, and a restarted process finishes the workload.
    /// The stopped process's store answers nothing once the failure reached it: every operation fails with the failure,
    /// and a read sent after a write failed is refused. Every crash state from the failure on recovers with every answer
    /// the stopped process gave, before the failure or after it, and every answer the restarted process gave, intact. The
    /// seed samples at most <see cref="FailedOperationLimit"/> states of each failure, and each failure's trace replays
    /// from it.
    /// </summary>
    [Fact(
        Skip = "Mutation runs leave out the longest crash-state enumeration (verification.md §9).",
        SkipWhen = nameof(UnderMutationTesting))]
    public Task AProcessStoppedByAFailedOperationRecoversFromEveryCrashState() => SeededSimulation.RunAsync(0x5e6d, async run =>
    {
        var workload = (await RecordAcrossAProcessCrashAsync(crashAfter: null)).FileSystem.OperationCount;
        var points = 0;
        var verified = 0;
        for (var failed = 0; failed < workload; failed++)
        {
            var recorder = await RecordAcrossAProcessCrashAsync(crashAfter: null, failOperation: failed);
            Assert.True(recorder.Steps.Count != 0, "The restarted process acknowledged nothing.");
            SeededSimulation.AssertSameHistory(run.Seed, Trace(recorder), Trace(await RecordAcrossAProcessCrashAsync(crashAfter: null, failOperation: failed)));
            var states = await VerifyAsync(run, recorder, from: failed, FailedOperationLimit, recoveryCrashes: 0);
            points += states.Points;
            verified += states.States.Count;
        }

        TestDiagnostics.WriteLine(Invariant($"{workload} failed operations, {points} crash points after them, {verified} crash states verified"));
    });

    private static void AssertTraceHolds(Recorder recorder, Func<FileSystemOperation, bool> operation, string description) =>
        Assert.True(recorder.FileSystem.Operations.Any(operation), $"The workload's trace holds no {description}.");

    /// <summary>
    /// Records <paramref name="workload"/> twice from the seed, fails unless both recordings issue the same file-system
    /// operations, and recovers the first's crash states: a trace which thread timing decides would not replay from the
    /// seed which reports its failure.
    /// </summary>
    private static Task RunAsync(int seed, bool writeThrough, LedgerStoreOptions options, Func<Recorder, Random, Task> workload) => SeededSimulation.RunAsync(seed, async run =>
    {
        var recorder = await RecordAsync(writeThrough, options, workload, run.Random);
        var replay = await RecordAsync(writeThrough, options, workload, new Random(run.Seed));
        SeededSimulation.AssertSameHistory(run.Seed, Trace(recorder), Trace(replay));
        TestDiagnostics.WriteLine((await VerifyAsync(run, recorder, from: 0, Limit, RecoveryCrashSample)).ToString());
    });

    private static async Task<Recorder> RecordAsync(bool writeThrough, LedgerStoreOptions options, Func<Recorder, Random, Task> workload, Random random)
    {
        var recorder = new Recorder(writeThrough, options);
        await recorder.OpenAsync(create: true);
        await workload(recorder, random);
        return recorder;
    }

    /// <summary>
    /// Returns a recording's file-system operations, each cluster claim's temporary file named alike, since the claim
    /// draws its name at random.
    /// </summary>
    private static List<string> Trace(Recorder recorder) =>
        [.. recorder.FileSystem.Operations.Select(static operation => TemporaryClaim().Replace(operation.ToString(), ".cluster.tmp"))];

    /// <summary>
    /// Recovers every crash state at or after point <paramref name="from"/> of a recorded workload, at most
    /// <paramref name="limit"/>, and checks each against the history; then, for <paramref name="recoveryCrashes"/> of
    /// them chosen by the seed, crashes the recovery at each of its own crash states and checks those against the same
    /// history.
    /// </summary>
    private static async Task<CrashStateSet> VerifyAsync(SimulationRun run, Recorder recorder, int from, int limit, int recoveryCrashes)
    {
        var enumerator = CrashStateEnumerator.Of(recorder.FileSystem, run.Seed);
        var operations = enumerator.Operations;
        run.UseHistory(() => operations.Select((operation, index) => Invariant($"#{index} {operation}")));
        var states = enumerator.Enumerate(from, operations.Count, limit);
        var recoveries = new List<(CrashState State, RecordingFileSystem Recovery)>();
        foreach (var state in states.States)
        {
            var recovery = await CheckStateAsync(recorder, enumerator.Build(state), state.Point, () => enumerator.Describe(state));
            if (recovery is { OperationCount: not 0 })
            {
                recoveries.Add((state, recovery));
            }
        }

        var sample = recoveries.Select(recovery => (Recovery: recovery, Order: run.Random.Next())).OrderBy(static pair => pair.Order).Take(recoveryCrashes).ToList();
        var recovered = 0;
        var sampledPoints = 0;
        foreach (var ((state, recovery), _) in sample)
        {
            var nested = CrashStateEnumerator.Of(recovery, run.Seed);
            var crashes = nested.Enumerate(0, nested.Operations.Count, int.MaxValue);
            sampledPoints += crashes.SampledPoints;
            foreach (var crash in crashes.States)
            {
                _ = await CheckStateAsync(
                    recorder,
                    nested.Build(crash),
                    state.Point,
                    () => enumerator.Describe(state) + "Then the recovery crashed:" + Environment.NewLine + nested.Describe(crash));
                recovered++;
            }
        }

        if (sample.Count != 0)
        {
            TestDiagnostics.WriteLine(Invariant($"{sample.Count} recoveries crashed, {recovered} of their crash states verified ({sampledPoints} points sampled)"));
        }

        return states;
    }

    private static async Task<RecordingFileSystem?> CheckStateAsync(Recorder history, FileSystemImage image, int point, Func<string> describe)
    {
        try
        {
            return await CheckAsync(history, image, point);
        }
        catch (Exception exception)
        {
            Assert.Fail(describe() + Environment.NewLine + exception);
            return null;
        }
    }

    /// <summary>
    /// Records a workload which creates the store, drops an unheld ledger, creates, writes, and checkpoints a replica,
    /// writes a slot of it which a read meets in flight, and creates, writes, and drops another. With
    /// <paramref name="crashAfter"/>, the process crashes after that many operations; with <paramref name="failOperation"/>,
    /// the operation after that many fails as a device error fails it, which is reported and stops the process there
    /// (conventions.md §14), and the stopped process's store then answers nothing. Either way a restarted process
    /// checkpoints every live replica, which a recovery may have asked of it, and then creates, writes, and checkpoints a
    /// new replica.
    /// </summary>
    private static async Task<Recorder> RecordAcrossAProcessCrashAsync(int? crashAfter, int? failOperation = null)
    {
        var reported = FatalErrors.ReportedCount;
        var recorder = new Recorder(writeThrough: false, Options, crashAfter, failOperation);
        await recorder.OpenAsync(create: true);
        await recorder.DropAsync(Unheld);
        await recorder.CreateAsync(S1);
        await recorder.WriteAsync(S1, 0);
        await recorder.CheckpointAsync(S1);
        await recorder.ReadDuringWriteAsync(S1, 1);
        await recorder.CreateAsync(S2);
        await recorder.WriteAsync(S2, 0);
        await recorder.DropAsync(S2);
        await recorder.CloseAsync();
        if (crashAfter is not null || failOperation is not null)
        {
            if (failOperation is not null)
            {
                await recorder.AssertStoppedAsync();
            }

            await recorder.RestartAsync();
            if (failOperation is not null)
            {
                // The stopped process reported its one failure, and the restarted process none.
                var failure = Assert.Single(FatalErrors.TakeMine(reported));
                Assert.StartsWith(Root, failure.Path, StringComparison.Ordinal);
            }

            await recorder.CreateAsync(S9);
            await recorder.WriteAsync(S9, 0);
            await recorder.CheckpointAsync(S9);
            await recorder.CloseAsync();
        }

        return recorder;
    }

    /// <summary>
    /// Checks one crash state's image against the acknowledged history at its crash point, and returns the file system
    /// its recovery recorded, or <see langword="null"/> when no store opened.
    /// </summary>
    private static async Task<RecordingFileSystem?> CheckAsync(Recorder history, FileSystemImage image, int point)
    {
        var checkpoints = await ReadCheckpointsAsync(image);
        var recovery = new RecordingFileSystem(Root, image);
        LedgerStore store;
        try
        {
            store = await OpenStoreAsync(recovery, history.StoreOptions, create: false);
        }
        catch (WalNotFoundException) when (point < history.Created)
        {
            await CheckInterruptedCreationAsync(history, image);
            return null;
        }

        ReplicaCatalog catalog;
        try
        {
            if (point >= history.Created)
            {
                Assert.Equal(history.Node, store.NodeId);
            }

            catalog = store.Catalog;
            CheckCatalog(history, catalog, point);
            var (records, seals) = await ScanAsync(store);
            CheckRecordHandles(history, catalog, records, seals);
            foreach (var replica in catalog.Replicas)
            {
                var checkpoint = checkpoints.GetValueOrDefault(replica.Handle) ?? new Checkpoint(replica.Handle, 0, default, [], Sealed: false, FoundDamage: false);
                await CheckReplicaAsync(history, store, replica.Ledger, checkpoint, records, seals, point);
            }

            foreach (var dropped in history.Steps.Where(step => step.Kind == StepKind.Drop && step.Acked <= point))
            {
                DroppedAnswers.AssertDropped(await store.CreateReplicaAsync(dropped.Ledger, Token));
            }
        }
        finally
        {
            await store.DisposeAsync();
        }

        await CheckCatalogCheckpointsAsync(history, image, point, catalog);
        return recovery;
    }

    /// <summary>
    /// LS-I6, §8.7 step 3: the store publishes its node file last, so a crash during its creation leaves at most WAL files
    /// without a record and the empty catalog checkpoint, which an open with create completes into an empty store under a
    /// new identity.
    /// </summary>
    private static async Task CheckInterruptedCreationAsync(Recorder history, FileSystemImage image)
    {
        await using var store = await OpenStoreAsync(new RecordingFileSystem(Root, image), history.StoreOptions, create: true);
        Assert.True(store.Catalog.IsEmpty, "The completed creation's catalog is not empty.");
        Assert.NotEqual(history.Node, store.NodeId);
    }

    /// <summary>
    /// LS-I1, LS-I2: every acknowledged creation keeps the handle it took, below the next handle, and no handle is
    /// reused; each ledger is listed as its acknowledged creation and drop require; and the catalog holds the tombstone
    /// of every acknowledged drop, and of no ledger whose drop had not started.
    /// </summary>
    private static void CheckCatalog(Recorder history, ReplicaCatalog catalog, int point)
    {
        foreach (var create in history.Steps.Where(step => step.Kind == StepKind.Create && step.Acked <= point))
        {
            Assert.True(create.Handle < catalog.NextHandle, Invariant($"The next handle {catalog.NextHandle} does not follow the acknowledged handle {create.Handle}."));
            if (catalog.Find(create.Ledger) is { } replica)
            {
                Assert.Equal(create.Handle, replica.Handle);
            }
        }

        Assert.All(catalog.Replicas, replica => Assert.True(replica.Handle < catalog.NextHandle));
        foreach (var batch in history.Batches)
        {
            // §7.3: one create record holds the batch's pairs, with consecutive handles, so a crash keeps all or none.
            var listed = batch.Select(catalog.Find).ToArray();
            Assert.True(listed.All(static replica => replica is null) || listed.All(static replica => replica is not null), Invariant($"A crash kept part of the batch {string.Join(", ", batch)}."));
            if (listed[0] is { } first)
            {
                Assert.Equal(Enumerable.Range(0, batch.Length).Select(index => first.Handle + (uint)index), listed.Select(static replica => replica!.Handle));
            }
        }

        foreach (var ledger in history.Steps.Select(static step => step.Ledger).Distinct())
        {
            var presence = Expected(history, ledger, point);
            Assert.True(presence == Presence.Either || presence == Presence.Present == catalog.Find(ledger) is not null, Invariant($"Ledger {ledger.Value} should be {presence}."));
            var drop = history.Steps.FirstOrDefault(step => step.Kind == StepKind.Drop && step.Ledger == ledger);
            if (drop is not null && drop.Acked <= point)
            {
                Assert.True(catalog.IsDropped(ledger), Invariant($"The acknowledged drop of ledger {ledger.Value} left no tombstone."));
            }
            else if (drop is not null && drop.DroppedAnswered <= point)
            {
                // LS-I2: a replica answers dropped only once its drop record is durable.
                Assert.True(catalog.IsDropped(ledger), Invariant($"An operation on ledger {ledger.Value} answered dropped, but the crash lost its drop."));
            }
            else if (drop is null || drop.Started >= point)
            {
                Assert.False(catalog.IsDropped(ledger), Invariant($"Ledger {ledger.Value} has a tombstone no drop wrote."));
            }
        }
    }

    /// <summary>
    /// LS-I1: a replica admits no write or seal before its create record is durable, so every slot and seal record the
    /// recovered WAL holds names a handle below the next handle, of a replica the catalog lists or of one whose ledger it
    /// holds the tombstone of; and a seal record names its replica's ledger (ledgerstore.md §7.3).
    /// </summary>
    private static void CheckRecordHandles(Recorder history, ReplicaCatalog catalog, List<Record> records, List<SealAt> seals)
    {
        foreach (var (handle, position, kind) in records.Select(static record => (record.Handle, record.Position, "slot"))
            .Concat(seals.Select(static seal => (seal.Handle, seal.Position, "seal"))))
        {
            Assert.True(handle < catalog.NextHandle, Invariant($"The {kind} record at {position} names handle {handle}, at or above the next handle {catalog.NextHandle}."));
            var ledger = LedgerOf(history, catalog, handle);
            Assert.True(
                ledger is { } named && (catalog.Find(named) is not null || catalog.IsDropped(named)),
                Invariant($"The {kind} record at {position} names handle {handle}, of no listed or dropped replica."));
        }

        foreach (var seal in seals)
        {
            Assert.True(LedgerOf(history, catalog, seal.Handle) == seal.Ledger, Invariant($"The seal record at {seal.Position} names ledger {seal.Ledger.Value}, not its handle's."));
        }

        static LedgerId? LedgerOf(Recorder history, ReplicaCatalog catalog, uint handle) =>
            catalog.Replicas.FirstOrDefault(replica => replica.Handle == handle)?.Ledger
                ?? history.Steps.FirstOrDefault(step => step.Kind == StepKind.Create && step.Handle == handle)?.Ledger;
    }

    /// <summary>
    /// Checks a crash state's catalog checkpoints (LS-I3, LS-I4, LS-I5): the first retained WAL file lies at or below the
    /// cut's file of the older of the two newest valid ones, and a released file 1 implies two valid ones; with the newest
    /// valid one damaged, the older kept one derives the catalog the newest did; and with every catalog checkpoint
    /// deleted, the open refuses once a create or drop record may have been written, and opens only while none was.
    /// </summary>
    private static async Task CheckCatalogCheckpointsAsync(Recorder history, FileSystemImage image, int point, ReplicaCatalog recovered)
    {
        var walDirectory = image.Lookup(FileSystemImage.Root, LedgerStore.WalDirectoryName);
        if (walDirectory < 0)
        {
            return;
        }

        var names = image.Entries(walDirectory).Select(static entry => entry.Name).ToList();
        var files = names.Where(static name => name.EndsWith(".clfl", StringComparison.Ordinal))
            .Select(static name => ulong.Parse(name.AsSpan(0, 20), CultureInfo.InvariantCulture))
            .Order()
            .ToList();
        var catalogs = names.Where(static name => CatalogCheckpoints.TryParseFileName(name, out _)).ToList();
        var valid = catalogs
            .Select(name => (Name: name, Checkpoint: Decode(name)))
            .Where(static checkpoint => checkpoint.Checkpoint is not null)
            .OrderBy(static checkpoint => checkpoint.Checkpoint!.Cut)
            .ToList();
        if (files.Count != 0)
        {
            if (valid.Count >= 2)
            {
                Assert.True(files[0] <= valid[^2].Checkpoint!.Cut.File, Invariant($"File {files[0]} is retained first, above the older kept catalog checkpoint's cut {valid[^2].Checkpoint!.Cut}."));
            }

            Assert.True(files[0] == 1 || valid.Count >= 2, Invariant($"File {files[0]} is retained first with {valid.Count} valid catalog checkpoints."));
        }

        if (valid.Count >= 2)
        {
            var fallback = new RecordingFileSystem(Root, image);
            await DamageAsync(fallback, valid[^1].Name);
            await using var store = await OpenStoreAsync(fallback, history.StoreOptions, create: false);
            var catalog = store.Catalog;
            Assert.Equal(recovered.NextHandle, catalog.NextHandle);
            Assert.Equal(recovered.Replicas, catalog.Replicas);
            Assert.Equal(recovered.Tombstones, catalog.Tombstones);
        }

        if (point >= history.Created && files.Count != 0)
        {
            var lost = new RecordingFileSystem(Root, image);
            foreach (var name in catalogs)
            {
                lost.DeleteFile(Path.Combine(Root, LedgerStore.WalDirectoryName, name));
            }

            var written = history.Steps.Where(static step => step.Kind is StepKind.Create or StepKind.Drop).ToList();
            try
            {
                await using var store = await OpenStoreAsync(lost, history.StoreOptions, create: false);
                Assert.False(written.Any(step => step.Acked <= point), "The open served a node whose catalog was lost after a create or drop.");
            }
            catch (LedgerReplicaCorruptException)
            {
                Assert.True(written.Any(step => step.Started < point), "The open refused a node whose WAL held no create or drop record.");
            }
        }

        CatalogCheckpoint? Decode(string name)
        {
            _ = CatalogCheckpoints.TryParseFileName(name, out var cut);
            try
            {
                return ReplicaCatalog.Decode(image.ReadFile(LedgerStore.WalDirectoryName + "/" + name)!, cut).Checkpoint();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Damages the catalog checkpoint <paramref name="name"/> in place, so its CRC fails.
    /// </summary>
    private static async Task DamageAsync(RecordingFileSystem fileSystem, string name)
    {
        using var file = fileSystem.OpenFile(Path.Combine(Root, LedgerStore.WalDirectoryName, name), direct: false);
        var crc = new byte[4];
        _ = await file.ReadAsync(crc, ReplicaCatalog.BodyCrcOffset, Token);
        await file.WriteAsync(crc.Select(static value => (byte)(value ^ 0xFF)).ToArray(), ReplicaCatalog.BodyCrcOffset, Token);
    }

    /// <summary>
    /// Checks a recovered replica: its checkpoint against the WAL records below its cut, its root against the
    /// acknowledged checkpoints, its slots against the acknowledged writes, and its seal against the answers which
    /// reported it.
    /// </summary>
    private static async Task CheckReplicaAsync(
        Recorder history, LedgerStore store, LedgerId ledger, Checkpoint checkpoint, List<Record> records, List<SealAt> seals, int point)
    {
        await using var replica = Assert.IsType<LedgerStoreReplica>(store.OpenReplica(ledger, Window));
        var acknowledged = history.Steps
            .Where(step => step.Kind == StepKind.Checkpoint && step.Ledger == ledger && step.Acked <= point && step.Generation != 0)
            .MaxBy(static step => step.Generation);
        if (acknowledged is not null)
        {
            // LS-I9: the root in force never falls below an acknowledged root, and it reads whole.
            Assert.False(checkpoint.FoundDamage, "Opening the checkpoints found damage.");
            Assert.True(checkpoint.Generation >= acknowledged.Generation, Invariant($"Generation {checkpoint.Generation} is in force after generation {acknowledged.Generation} was acknowledged."));
            Assert.True(checkpoint.ReplayStart >= acknowledged.ReplayStart, Invariant($"Replay start {checkpoint.ReplayStart} precedes the acknowledged {acknowledged.ReplayStart}."));
            Assert.Equal(checkpoint.Generation, replica.Ledger.Recovery.Generation);
        }

        if (checkpoint is { Generation: not 0 })
        {
            // LS-I8: the checkpoint holds each slot's latest record below its cut, and nothing at or above it.
            var expected = new SortedDictionary<long, SlotEntry>();
            foreach (var record in records.Where(record => record.Handle == checkpoint.Handle && record.Position < checkpoint.ReplayStart))
            {
                expected[record.Slot] = new SlotEntry(record.Kind, record.Page);
            }

            Assert.Equal(expected, checkpoint.Entries);

            // LS-I8, LS-I11: its root records the seal exactly when the replica's seal record lies below its cut.
            var sealedBelowCut = seals.Any(seal => seal.Handle == checkpoint.Handle && seal.Position < checkpoint.ReplayStart);
            Assert.True(sealedBelowCut == checkpoint.Sealed, Invariant($"The root's seal flag is {checkpoint.Sealed}, but a seal record below its cut {checkpoint.ReplayStart} is {sealedBelowCut}."));
        }

        var highest = -1L;
        foreach (var write in history.Steps.Where(step => step.Kind == StepKind.Write && step.Ledger == ledger && step.Started < point))
        {
            var value = await LedgerReplicaTestData.ReadBytesAsync(replica, write.Slot, Token);
            if (value is not null)
            {
                highest = Math.Max(highest, write.Slot);
            }

            if (write.Acked <= point)
            {
                Assert.True(write.Value.AsSpan().SequenceEqual(value), Invariant($"The acknowledged write of slot {write.Slot} was lost."));
            }
            else
            {
                Assert.True(value is null || write.Value.AsSpan().SequenceEqual(value), Invariant($"Slot {write.Slot} holds a value never written."));
            }
        }

        foreach (var read in history.Steps.Where(step => step.Kind == StepKind.Read && step.Ledger == ledger && step.Acked <= point))
        {
            if (read.Value is not null)
            {
                // Conventions.md §13: a read which answered DATA reported a durable record, which survives every crash.
                var value = await LedgerReplicaTestData.ReadBytesAsync(replica, read.Slot, Token);
                Assert.True(read.Value.AsSpan().SequenceEqual(value), Invariant($"The DATA a read of slot {read.Slot} answered was lost."));
            }
            else
            {
                // LR-I4: a read which answered missing began before any write of its slot was acknowledged.
                Assert.DoesNotContain(
                    history.Steps,
                    step => step.Kind == StepKind.Write && step.Ledger == ledger && step.Slot == read.Slot && step.Acked < read.Started);
            }
        }

        foreach (var refused in history.Steps.Where(step => step.Kind == StepKind.SealedWrite && step.Ledger == ledger && step.Started < point))
        {
            // LR-I8: a writer write the seal refused stored nothing; only a write of its slot may have filled it.
            if (!history.Steps.Any(step => step.Kind == StepKind.Write && step.Ledger == ledger && step.Slot == refused.Slot && step.Started < point))
            {
                Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, refused.Slot, Token));
            }
        }

        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, Unwritten, Token));

        // LR-I6: the reopened replica retains max(0, h + 1 - K) for its highest slot h and the window K it recorded.
        var tail = await replica.ReadTailAsync(Token);
        Assert.Equal(new Slot(Math.Max(0, highest + 1 - Window.WindowSize)), tail.Tail.ContiguousTail);

        // LS-I11, conventions.md §13: a seal an answer reported survives, and a replica no seal reached is unsealed.
        if (ExpectedSeal(history, ledger, point) is { } expectedSeal)
        {
            Assert.True(expectedSeal == tail.Tail.IsSealed, Invariant($"Ledger {ledger.Value} should be {(expectedSeal ? "sealed" : "unsealed")}."));
        }

        if (history.Steps.Any(step => step.Kind == StepKind.Seal && step.Ledger == ledger))
        {
            // LR-I8: a writer write is refused exactly when the replica reports its seal.
            Assert.Equal(tail.Tail.IsSealed ? ReplicaWriteCase.Sealed : ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, Probe, [1])).Case);
        }
    }

    /// <summary>
    /// Returns whether a recovered replica must be sealed (LS-I11, conventions.md §13): sealed from the first answer which
    /// reported its seal, unsealed until a seal began, and either while one was in flight.
    /// </summary>
    private static bool? ExpectedSeal(Recorder history, LedgerId ledger, int point)
    {
        var seals = history.Steps.Where(step => step.Ledger == ledger && step.Kind is StepKind.Seal or StepKind.SealedWrite).ToList();
        if (seals.Any(step => step.Acked <= point))
        {
            return true;
        }

        return seals.Any(step => step.Started < point) ? null : false;
    }

    private static Presence Expected(Recorder history, LedgerId ledger, int point)
    {
        Step? Find(StepKind kind) => history.Steps.FirstOrDefault(step => step.Kind == kind && step.Ledger == ledger);
        if (Find(StepKind.Drop) is { } drop && drop.Started < point)
        {
            return drop.Acked <= point ? Presence.Absent : Presence.Either;
        }

        if (Find(StepKind.Create) is { } create && create.Started < point)
        {
            return create.Acked <= point ? Presence.Present : Presence.Either;
        }

        return Presence.Absent;
    }

    /// <summary>
    /// Loads each checkpoint directory's checkpoint as the crash left it, by handle, reading every leaf it references. A
    /// replica's first checkpoint creates its directory, so a replica never checkpointed has none (LS-I6).
    /// </summary>
    private static async Task<Dictionary<uint, Checkpoint>> ReadCheckpointsAsync(FileSystemImage image)
    {
        var fileSystem = new RecordingFileSystem(Root, image);
        var result = new Dictionary<uint, Checkpoint>();
        var slots = LedgerStore.SlotsPath(NodeRoot);
        if (!fileSystem.DirectoryExists(slots))
        {
            return result;
        }

        foreach (var name in await fileSystem.Storage.ListDirectoriesAsync(slots, Token))
        {
            var handle = uint.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
            var path = LedgerStore.SlotsPath(NodeRoot, handle);
            await using var directory = await fileSystem.Storage.OpenDirectoryAsync(path, Token);
            var (checkpoints, loaded) = await SlotCheckpoints.OpenAsync(directory, Token);
            await using (checkpoints)
            {
                var entries = new SortedDictionary<long, SlotEntry>();
                foreach (var (leaf, reference) in loaded?.Leaves ?? [])
                {
                    var leafEntries = new SlotEntry[SlotLeafCodec.Slots];
                    FrozenLeaf.Decode(await checkpoints.ReadLeafBytesAsync(reference, Token)).Leaf().DecodeAll(leafEntries);
                    for (var index = 0; index < leafEntries.Length; index++)
                    {
                        if (leafEntries[index].Kind != SlotKind.Missing)
                        {
                            entries[leaf * SlotLeafCodec.Slots + index] = leafEntries[index];
                        }
                    }
                }

                result[handle] = new Checkpoint(
                    handle, loaded?.Generation ?? 0, loaded?.ReplayStart ?? default, entries, loaded?.Sealed ?? false, checkpoints.FoundDamage);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns every slot record and every seal record the recovered WAL holds, whatever its handle, with its position.
    /// </summary>
    private static async Task<(List<Record> Records, List<SealAt> Seals)> ScanAsync(LedgerStore store)
    {
        var records = new List<Record>();
        var seals = new List<SealAt>();
        await foreach (var entry in store.Wal.ReadAllAsync(default, Token))
        {
            if (!entry.TryGetValue(out WalRecord record))
            {
                continue;
            }

            var kind = LedgerRecords.Classify(record.Payload, LedgerRecords.StoreHandle, out var handle, out var slot);
            if (kind == RecordClass.Slot)
            {
                records.Add(new Record(handle, slot, SlotKind.Data, record.Position, record.Page));
            }
            else if (kind == RecordClass.Store && StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed && parsed is SealRecord seal)
            {
                seals.Add(new SealAt(seal.Handle, seal.Ledger, record.Position));
            }
        }

        return (records, seals);
    }

    private static ValueTask<LedgerStore> OpenStoreAsync(RecordingFileSystem fileSystem, LedgerStoreOptions options, bool create) =>
        LedgerStore.OpenAsync(
            fileSystem.Storage,
            options,
            LedgerReplicaTestData.Cluster,
            create,
            Checkpoints,
            new FakeTimeProvider(),
            FileNodeRootClaim.Claim(NodeRoot, flushToDisk: true, fileSystem),
            Token);

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private sealed record Step(StepKind Kind, LedgerId Ledger, int Started, int Acked)
    {
        public long Slot { get; init; }

        public byte[]? Value { get; init; }

        public ulong Generation { get; init; }

        public WalPosition ReplayStart { get; init; }

        public uint Handle { get; init; }

        /// <summary>
        /// Gets the point at which an operation on a drop's replica answered dropped, from which the drop must survive.
        /// </summary>
        public int DroppedAnswered { get; init; } = int.MaxValue;
    }

    private sealed record Checkpoint(uint Handle, ulong Generation, WalPosition ReplayStart, SortedDictionary<long, SlotEntry> Entries, bool Sealed, bool FoundDamage);

    private sealed record Record(uint Handle, long Slot, SlotKind Kind, WalPosition Position, WalPage Page);

    private sealed record SealAt(uint Handle, LedgerId Ledger, WalPosition Position);

    /// <summary>
    /// Runs a workload over a recording file system and records, for each step, the operations before it started and
    /// after it was acknowledged: a crash at a point at or after a step's acknowledgment must preserve it. A step is
    /// acknowledged where its action returned, even once the process crashed or stopped, since an answer the stopped
    /// process gave must hold too; one which failed because the process crashed, or stopped at the operation
    /// <paramref name="failOperation"/> fails, is recorded unacknowledged, and every later step of the crashed process does
    /// nothing.
    /// </summary>
    private sealed class Recorder(bool writeThrough, LedgerStoreOptions options, int? crashAfter = null, int? failOperation = null)
    {
        public RecordingFileSystem FileSystem { get; private set; } = new(Root, writeThrough: writeThrough) { CrashAfter = crashAfter, FailOperation = failOperation };

        public LedgerStoreOptions StoreOptions { get; } = options;

        public List<Step> Steps { get; } = [];

        /// <summary>
        /// Gets the creations which waited behind a held create record, each in the order it was requested.
        /// </summary>
        public List<LedgerId[]> Batches { get; } = [];

        public NodeId Node { get; private set; }

        /// <summary>
        /// Gets the point at which the store's creation was first acknowledged.
        /// </summary>
        public int Created { get; private set; } = int.MaxValue;

        private LedgerStore? Store { get; set; }

        private int Count => FileSystem.OperationCount;

        private bool Crashed => FileSystem.Crashed;

        /// <summary>
        /// Opens the store, creating its claim and the store when <paramref name="create"/> is set; the
        /// store's first acknowledged creation fixes the node identity. An open which returned answered, whether or not
        /// the process crashed afterwards.
        /// </summary>
        public async Task OpenAsync(bool create = false)
        {
            if (Crashed)
            {
                return;
            }

            try
            {
                Store = await OpenStoreAsync(FileSystem, StoreOptions, create);
            }
            catch (Exception) when (Crashed)
            {
                return;
            }

            await SettleAsync();
            if (Created == int.MaxValue)
            {
                Node = Store.NodeId;
                Created = Count;
            }
            else
            {
                Assert.Equal(Node, Store.NodeId);
            }
        }

        /// <summary>
        /// Restarts after the process crashed. The crashed process's store is disposed first, so none of its work
        /// overlaps the new process, whose every call through the crashed view fails. The new process sees the same page
        /// cache, opens the store, creating it if the crashed process had not, and checkpoints every live replica, so a
        /// checkpoint its recovery asked for has run before the workload continues.
        /// </summary>
        public async Task RestartAsync()
        {
            if (Store is { } crashed)
            {
                await crashed.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            Store = null;
            FileSystem = FileSystem.Restart();
            await OpenAsync(create: true);
            foreach (var ledger in Store!.Replicas)
            {
                await CheckpointAsync(ledger);
            }
        }

        /// <summary>
        /// Disposes the store, whose final checkpoints are acknowledged when disposal returns, whether or not the process
        /// crashed afterwards.
        /// </summary>
        public async Task CloseAsync()
        {
            if (Crashed)
            {
                return;
            }

            var store = Store!;
            var live = store.Replicas.Select(ledger => store.FindLive(ledger)!).ToArray();
            var started = Count;
            try
            {
                await store.DisposeAsync();
            }
            catch (Exception) when (Crashed)
            {
                // The final checkpoints' outcome is unknown.
                return;
            }

            foreach (var ledger in live)
            {
                Steps.Add(new Step(StepKind.Checkpoint, ledger.Ledger, started, Count) { Generation = ledger.CheckpointGeneration, ReplayStart = ledger.ReplayStart });
            }
        }

        /// <summary>
        /// Closes the store with its caller's bound passing once <paramref name="finals"/> final checkpoints hold their root
        /// writes: those finish, and the close starts no other (ledgerstore.md §8.10). Each final it wrote is acknowledged when
        /// the close returns; a replica whose final it skipped keeps its checkpoint in force.
        /// </summary>
        public async Task<LedgerStoreCloseResult> CloseWithinABoundAsync(int finals)
        {
            if (Crashed)
            {
                return default;
            }

            var store = Store!;
            var live = store.Replicas.Select(ledger => store.FindLive(ledger)!).ToArray();
            var generations = live.ToDictionary(static ledger => ledger.Ledger, static ledger => ledger.CheckpointGeneration);
            var started = Count;
            using var bound = new CancellationTokenSource();
            FileSystem.HoldWrites = static path => path.EndsWith("/" + SlotCheckpoints.RootName, StringComparison.Ordinal);
            LedgerStoreCloseResult closed;
            try
            {
                var closing = store.CloseAsync(bound.Token).AsTask();
                await UntilAsync(() => FileSystem.HeldWrites.Count == finals || closing.IsCompleted, "The close did not start its final checkpoints.");
                await bound.CancelAsync();
                FileSystem.HoldWrites = null;
                await UntilAsync(
                    () =>
                    {
                        foreach (var held in FileSystem.HeldWrites)
                        {
                            held.Complete();
                        }

                        return closing.IsCompleted;
                    },
                    "The close did not end once its bound passed.");
                closed = await closing;
            }
            catch (Exception) when (Crashed)
            {
                // The final checkpoints' outcome is unknown.
                return default;
            }
            finally
            {
                FileSystem.HoldWrites = null;
            }

            foreach (var ledger in live.Where(ledger => ledger.CheckpointGeneration != generations[ledger.Ledger]))
            {
                Steps.Add(new Step(StepKind.Checkpoint, ledger.Ledger, started, Count) { Generation = ledger.CheckpointGeneration, ReplayStart = ledger.ReplayStart });
            }

            return closed;
        }

        public async Task CreateAsync(LedgerId ledger)
        {
            uint handle = 0;
            await StepAsync(
                new Step(StepKind.Create, ledger, Count, 0),
                async () => handle = (await Store!.CreateLiveAsync(ledger, Window.WindowSize, Token))!.Handle,
                step => step with { Handle = handle });
        }

        /// <summary>
        /// Drops <paramref name="ledger"/> and then checkpoints the catalog past its drop record, as
        /// <see cref="CheckpointCatalogAfterDropAsync"/> does. The drop is acknowledged where it returned.
        /// </summary>
        public async Task DropAsync(LedgerId ledger)
        {
            await StepAsync(new Step(StepKind.Drop, ledger, Count, 0), async () => await Store!.DropReplicaAsync(ledger, Token));
            await CheckpointCatalogAfterDropAsync();
        }

        /// <summary>
        /// Drops <paramref name="ledger"/> with the fsync of its drop record held, so the record is in the page cache and
        /// not durable, reads its replica once the drop has closed admission, and releases the fsync: the read answers
        /// dropped only once the record is durable (LS-I2), so the check holds every crash state from the answer on to the
        /// drop's tombstone. Then checkpoints the catalog past the drop record, as <see cref="DropAsync"/> does. The
        /// workload's files are not write-through, so each WAL write is durable at its fsync.
        /// </summary>
        public async Task DropAnsweringAReadAsync(LedgerId ledger)
        {
            if (Crashed)
            {
                return;
            }

            var replica = Store!.OpenReplica(ledger, Window);
            var started = Count;
            var answered = int.MaxValue;
            FileSystem.HoldFlushes = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var drop = Store.DropReplicaAsync(ledger, Token).AsTask();
                await UntilAsync(() => FileSystem.HeldFlushes.Count != 0 || drop.IsCompleted, "The WAL did not fsync the drop record.");
                Assert.True(FileSystem.HeldFlushes.Count == 1, Invariant($"held {FileSystem.HeldFlushes.Count}, drop {drop.Status} {drop.Exception}"));
                var read = replica.ReadAsync(Slot.Zero, Token).AsTask();
                var observed = read.ContinueWith(
                    task => answered = task.IsCompletedSuccessfully && task.Result.Case == ReplicaReadCase.Dropped ? Count : answered,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Assert.False(read.IsCompleted, "The read answered before the drop record was durable.");
                FileSystem.HoldFlushes = null;
                FileSystem.HeldFlushes[0].Release();
                await drop;
                await observed;
                DroppedAnswers.AssertDropped(await read);
                Assert.NotEqual(int.MaxValue, answered);
                Steps.Add(new Step(StepKind.Drop, ledger, started, Count) { DroppedAnswered = answered });
            }
            finally
            {
                FileSystem.HoldFlushes = null;
                await replica.DisposeAsync();
            }

            await CheckpointCatalogAfterDropAsync();
        }

        /// <summary>
        /// Seals <paramref name="ledger"/> with a sealing contiguous-tail read, which creates its replica, sealed, when the
        /// catalog does not list it (ledgerstore.md §8.3, §8.5); a creation is recorded with the seal, which answers only
        /// once its create and seal records are durable (LS-I1, LS-I11).
        /// </summary>
        public async Task SealAsync(LedgerId ledger)
        {
            if (Crashed)
            {
                return;
            }

            var store = Store!;
            var creates = store.FindLive(ledger) is null;
            var handle = store.Catalog.NextHandle;
            await using var replica = store.OpenReplica(ledger, Window);
            var started = Count;
            await StepAsync(
                new Step(StepKind.Seal, ledger, started, 0),
                async () => Assert.True((await replica.ReadTailAsync(Slot.Zero, seal: true, Token)).Tail.IsSealed));
            if (creates)
            {
                Steps.Add(new Step(StepKind.Create, ledger, started, Steps[^1].Acked) { Handle = handle });
            }
        }

        /// <summary>
        /// Seals the replica of <paramref name="ledger"/> with the fsync of its seal record held, so the record is in the
        /// page cache and not durable, sends a writer write of <paramref name="slot"/> and a contiguous-tail read which meet
        /// the seal, and releases the fsync: the write answers <see cref="ReplicaAnswers.Sealed"/>, and the read sealed, only
        /// once the record is durable (LS-I11, conventions.md §13), so the check holds every crash state from each answer
        /// on to the seal. The workload's files are not write-through, so each WAL write is durable at its fsync.
        /// </summary>
        public async Task SealAnsweringAWriteAsync(LedgerId ledger, long slot)
        {
            if (Crashed)
            {
                return;
            }

            var replica = Store!.OpenReplica(ledger, Window);
            var started = Count;
            FileSystem.HoldFlushes = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var sealing = replica.ReadTailAsync(Slot.Zero, seal: true, Token).AsTask();
                await UntilAsync(() => FileSystem.HeldFlushes.Count != 0 || sealing.IsCompleted, "The WAL did not fsync the seal record.");
                Assert.True(FileSystem.HeldFlushes.Count == 1, Invariant($"held {FileSystem.HeldFlushes.Count}, seal {sealing.Status} {sealing.Exception}"));
                var metStarted = Count;
                var write = LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot));
                var read = ReadTailAsync(replica);
                var writeAnswered = int.MaxValue;
                var readAnswered = int.MaxValue;
                var observedWrite = write.ContinueWith(
                    task => writeAnswered = task.IsCompletedSuccessfully && task.Result.Case == ReplicaWriteCase.Sealed ? Count : writeAnswered,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                var observedRead = read.ContinueWith(
                    task => readAnswered = task is { IsCompletedSuccessfully: true, Result.Tail.IsSealed: true } ? Count : readAnswered,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Assert.False(sealing.IsCompleted || write.IsCompleted || read.IsCompleted, "An operation answered before the seal record was durable.");
                FileSystem.HoldFlushes = null;
                FileSystem.HeldFlushes[0].Release();
                Assert.True((await sealing).Tail.IsSealed);
                Steps.Add(new Step(StepKind.Seal, ledger, started, Count));
                await observedWrite;
                await observedRead;
                Assert.Equal(ReplicaAnswers.Sealed, await write);
                Assert.True((await read).Tail.IsSealed);
                Steps.Add(new Step(StepKind.SealedWrite, ledger, metStarted, writeAnswered) { Slot = slot });
                Steps.Add(new Step(StepKind.Seal, ledger, metStarted, readAnswered));
            }
            finally
            {
                FileSystem.HoldFlushes = null;
                await replica.DisposeAsync();
            }

            await SettleAsync();

            static async Task<ReplicaTailRead> ReadTailAsync(ILedgerReplica replica) => await replica.ReadTailAsync(Slot.Zero, seal: false, Token);
        }

        /// <summary>
        /// Sends a writer write of <paramref name="slot"/> to the sealed replica of <paramref name="ledger"/>, which
        /// answers <see cref="ReplicaAnswers.Sealed"/> and stores nothing (LR-I8).
        /// </summary>
        public async Task SealedWriteAsync(LedgerId ledger, long slot)
        {
            if (Crashed)
            {
                return;
            }

            await using var replica = Store!.OpenReplica(ledger, Window);
            await StepAsync(
                new Step(StepKind.SealedWrite, ledger, Count, 0) { Slot = slot },
                async () => Assert.Equal(ReplicaAnswers.Sealed, await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot))));
        }

        /// <summary>
        /// Sends recovery writes of <paramref name="slots"/>, which a sealed replica takes (LR-I5); each is recorded as the
        /// write it is, which must survive once acknowledged.
        /// </summary>
        public async Task RecoveryWriteAsync(LedgerId ledger, params long[] slots)
        {
            if (Crashed)
            {
                return;
            }

            await using var replica = Store!.OpenReplica(ledger, Window);
            foreach (var slot in slots)
            {
                await StepAsync(
                    new Step(StepKind.Write, ledger, Count, 0) { Slot = slot, Value = Payload(ledger, slot) },
                    async () => Assert.Equal(ReplicaAnswers.Written, await LedgerStoreReplicaTests.RecoveryWriteAsync(replica, slot, Payload(ledger, slot))));
            }
        }

        /// <summary>
        /// Requests a catalog checkpoint once the WAL's durable frontier has passed every record a replica published, so its
        /// cut lies past them, the seal records included, and settles with it (ledgerstore.md §8.8).
        /// </summary>
        public async Task CheckpointCatalogAsync()
        {
            if (Crashed)
            {
                return;
            }

            var store = Store!;
            var last = store.Replicas.Max(ledger => store.FindLive(ledger)!.LastPublished);
            await UntilAsync(() => store.Wal.DurableFrontier > last, "The durable frontier did not pass the published records.");
            store.RequestCatalogCheckpoint();
            await SettleAsync();
            Assert.True(store.CatalogCut > last, Invariant($"The catalog checkpoint's cut {store.CatalogCut} does not lie past {last}."));
        }

        /// <summary>
        /// Holds the WAL write of <paramref name="created"/>'s create record, then writes <paramref name="slots"/> of
        /// <paramref name="written"/>, each in its own held write group above it, and completes them, last first, so each
        /// is durable and acknowledged while the create record is not; then completes the create record.
        /// </summary>
        public async Task RaceCreateAsync(LedgerId created, LedgerId written, long[] slots)
        {
            if (Crashed)
            {
                return;
            }

            var replica = Store!.OpenReplica(written, Window);
            FileSystem.HoldWrites = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var createStarted = Count;
                var creation = Store.CreateLiveAsync(created, Window.WindowSize, Token).AsTask();
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1 || creation.IsCompleted, "The WAL did not issue the held create record.");
                Assert.True(FileSystem.HeldWrites.Count == 1, Invariant($"held {FileSystem.HeldWrites.Count}, creation {creation.Status} {creation.Exception}"));
                var createWrite = FileSystem.HeldWrites[0];
                var pending = new List<(long Slot, int Started, Task Write, RecordingFileSystem.HeldWrite Held)>();
                foreach (var slot in slots)
                {
                    var started = Count;
                    var write = LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(written, slot));
                    await UntilAsync(() => FileSystem.HeldWrites.Count == pending.Count + 2, "The WAL did not issue the held write.");
                    pending.Add((slot, started, write, FileSystem.HeldWrites[^1]));
                }

                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    var (slot, started, write, held) = pending[index];
                    held.Complete();
                    await write;
                    Steps.Add(new Step(StepKind.Write, written, started, Count) { Slot = slot, Value = Payload(written, slot) });
                }

                Assert.False(creation.IsCompleted);
                createWrite.Complete();
                var handle = (await creation)!.Handle;
                Steps.Add(new Step(StepKind.Create, created, createStarted, Count) { Handle = handle });
            }
            finally
            {
                FileSystem.HoldWrites = null;
                await replica.DisposeAsync();
            }

            await SettleAsync();
        }

        /// <summary>
        /// Holds the create record which a first write to <paramref name="created"/> issues, then writes
        /// <paramref name="slots"/> of <paramref name="written"/>, each in its own held write group above it, and
        /// completes them, last first, so each is durable and acknowledged while the create record is not and the first
        /// write waits on it; then completes the create record, and the first write follows it.
        /// </summary>
        public async Task RaceFirstWriteAsync(LedgerId created, LedgerId written, long[] slots)
        {
            if (Crashed)
            {
                return;
            }

            var first = Store!.OpenReplica(created, Window);
            var replica = Store.OpenReplica(written, Window);
            FileSystem.HoldWrites = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var started = Count;
                var firstWrite = LedgerStoreReplicaTests.WriteAsync(first, 0, Payload(created, 0));
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the held create record.");
                var createWrite = FileSystem.HeldWrites[0];
                await CompleteLastFirstAsync(replica, written, slots, held: 1);
                Assert.False(firstWrite.IsCompleted);
                Assert.Equal([createWrite], FileSystem.HeldWrites);
                FileSystem.HoldWrites = null;
                createWrite.Complete();
                await firstWrite;
                var handle = Store.Catalog.Find(created)!.Handle;
                Steps.Add(new Step(StepKind.Create, created, started, Count) { Handle = handle });
                Steps.Add(new Step(StepKind.Write, created, started, Count) { Slot = 0, Value = Payload(created, 0) });
            }
            finally
            {
                FileSystem.HoldWrites = null;
                await first.DisposeAsync();
                await replica.DisposeAsync();
            }

            await SettleAsync();
        }

        /// <summary>
        /// Holds the create record of <paramref name="held"/>, requests the creations of <paramref name="batch"/>, which
        /// wait behind it, and completes it, so the next create record holds every pair of the batch.
        /// </summary>
        public async Task BatchCreateAsync(LedgerId held, LedgerId[] batch)
        {
            if (Crashed)
            {
                return;
            }

            FileSystem.HoldWrites = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var heldStarted = Count;
                var first = Store!.CreateLiveAsync(held, Window.WindowSize, Token).AsTask();
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the held create record.");
                var heldWrite = FileSystem.HeldWrites[0];
                var started = Count;
                var creations = batch.Select(ledger => Store.CreateLiveAsync(ledger, Window.WindowSize, Token).AsTask()).ToArray();
                Assert.DoesNotContain(creations, static creation => creation.IsCompleted);
                heldWrite.Complete();
                var handle = (await first)!.Handle;
                Steps.Add(new Step(StepKind.Create, held, heldStarted, Count) { Handle = handle });
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the batch's create record.");
                Assert.DoesNotContain(creations, static creation => creation.IsCompleted);
                Batches.Add(batch);
                FileSystem.HeldWrites[0].Complete();
                var replicas = await Task.WhenAll(creations);
                for (var index = 0; index < batch.Length; index++)
                {
                    Steps.Add(new Step(StepKind.Create, batch[index], started, Count) { Handle = replicas[index]!.Handle });
                }
            }
            finally
            {
                FileSystem.HoldWrites = null;
            }

            await SettleAsync();
        }

        /// <summary>
        /// Holds a write of slot <paramref name="slot"/> of <paramref name="written"/>, which keeps the durable frontier
        /// below it, then drops <paramref name="dropped"/> and completes only the drop record, above the frontier, and
        /// requests a catalog checkpoint: its cut lies strictly past the drop record. Then completes the write.
        /// </summary>
        public async Task DropAboveTheFrontierAsync(LedgerId dropped, LedgerId written, long slot)
        {
            if (Crashed)
            {
                return;
            }

            var replica = Store!.OpenReplica(written, Window);
            FileSystem.HoldWrites = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var writeStarted = Count;
                var write = LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(written, slot));
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the held write.");
                var heldWrite = FileSystem.HeldWrites[0];
                var dropStarted = Count;
                var drop = Store.DropReplicaAsync(dropped, Token).AsTask();
                await UntilAsync(() => FileSystem.HeldWrites.Count == 2, "The WAL did not issue the held drop record.");
                FileSystem.HeldWrites[1].Complete();
                await drop;
                Steps.Add(new Step(StepKind.Drop, dropped, dropStarted, Count));

                // Maintenance deletes the replica's directory before the request (#165), and writes the checkpoint before
                // the held write completes, so the trace orders them alike in every run.
                await SettleAsync();
                var frontier = Store.Wal.DurableFrontier;
                Store.RequestCatalogCheckpoint();
                await SettleAsync();
                var cut = Assert.NotNull(Store.CatalogCut);
                Assert.True(cut > frontier, Invariant($"The catalog checkpoint's cut {cut} does not lie past the frontier {frontier}."));
                Assert.False(write.IsCompleted);
                FileSystem.HoldWrites = null;
                heldWrite.Complete();
                await write;
                Steps.Add(new Step(StepKind.Write, written, writeStarted, Count) { Slot = slot, Value = Payload(written, slot) });

                // The WAL reads up to its durable frontier, which passes the drop record once the WAL retires the held
                // write, after answering it.
                await WaitForFrontierAsync();
                var record = await FindDropRecordAsync(dropped);
                Assert.True(cut > record, Invariant($"The catalog checkpoint's cut {cut} does not lie past the drop record at {record}."));
            }
            finally
            {
                FileSystem.HoldWrites = null;
                await replica.DisposeAsync();
            }

            await SettleAsync();
        }

        /// <summary>
        /// With a one-page write window, holds a write of slot <paramref name="held"/> of <paramref name="written"/>,
        /// and queues behind it a write of slot <paramref name="filler"/> which leaves 20 bytes of the next page and the
        /// creation of <paramref name="created"/>, whose 20-byte create record begins there and ends in the page after.
        /// Completing the held write and then the shared page leaves the filler durable and the create record in flight,
        /// so the durable frontier stops where the create record begins, and the catalog checkpoint captured then is cut
        /// exactly there (ledgerstore.md §8.8). Then completes the create record, which awaits the next catalog
        /// checkpoint.
        /// </summary>
        public async Task CaptureWhereACreateRecordBeginsAsync(LedgerId written, long held, long filler, LedgerId created)
        {
            if (Crashed)
            {
                return;
            }

            Assert.Equal(1, StoreOptions.WriteWindowPages);

            // The WAL retires the previous step's records, so it counts only this step's.
            await WaitForFrontierAsync();
            Assert.Equal(0, Store!.Wal.OutstandingRecords);
            var replica = Store.OpenReplica(written, Window);
            FileSystem.HoldWrites = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var heldStarted = Count;
                var heldWrite = LedgerStoreReplicaTests.WriteAsync(replica, held, Payload(written, held));
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the held write.");

                // A page holds its CRC, then the filler's frame word and key, then 20 bytes: the create record's first
                // frame header and 8 of its bytes.
                var fillerValue = LedgerReplicaTestData.CreatePayload(StoreOptions.PageSize - 36, (int)filler);
                var fillerStarted = Count;
                var fillerWrite = LedgerStoreReplicaTests.WriteAsync(replica, filler, fillerValue);
                var createStarted = Count;
                var creation = Store.CreateLiveAsync(created, Window.WindowSize, Token).AsTask();
                await UntilAsync(() => Store.Wal.OutstandingRecords == 3, "The WAL did not admit the filler and the create record.");
                FileSystem.HeldWrites[0].Complete();
                await heldWrite;
                Steps.Add(new Step(StepKind.Write, written, heldStarted, Count) { Slot = held, Value = Payload(written, held) });
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the page the filler shares with the create record.");
                FileSystem.HeldWrites[0].Complete();
                await fillerWrite;
                Steps.Add(new Step(StepKind.Write, written, fillerStarted, Count) { Slot = filler, Value = fillerValue });
                Assert.False(creation.IsCompleted);

                // The WAL advances its frontier after the outcome callbacks, as it retires the records. It issues the
                // create record's last page before the request, and maintenance writes the checkpoint before the page
                // completes, so the trace orders them alike in every run.
                await UntilAsync(() => Store.Wal.OutstandingRecords == 1, "The frontier did not pass the filler.");
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The WAL did not issue the create record's last page.");
                var frontier = Store.Wal.DurableFrontier;
                Store.RequestCatalogCheckpoint();
                await SettleAsync();
                Assert.True(Store.CatalogCut == frontier, Invariant($"The catalog checkpoint's cut {Store.CatalogCut} is not the frontier {frontier}."));
                FileSystem.HoldWrites = null;
                FileSystem.HeldWrites[0].Complete();
                var handle = (await creation)!.Handle;
                Steps.Add(new Step(StepKind.Create, created, createStarted, Count) { Handle = handle });

                // The WAL reads up to its durable frontier, which passes the create record once the WAL retires it.
                await WaitForFrontierAsync();
                Assert.Equal(frontier, await FindCreateRecordAsync(created));
                Assert.False(Store.CatalogCheckpointCurrent, "The create record at the catalog cut does not await a catalog checkpoint.");
            }
            finally
            {
                FileSystem.HoldWrites = null;
                await replica.DisposeAsync();
            }

            await SettleAsync();
        }

        public async Task WriteAsync(LedgerId ledger, params long[] slots)
        {
            if (Crashed)
            {
                return;
            }

            await using var replica = Assert.IsType<LedgerStoreReplica>(Store!.OpenReplica(ledger, Window));
            foreach (var slot in slots)
            {
                await StepAsync(
                    new Step(StepKind.Write, ledger, Count, 0) { Slot = slot, Value = Payload(ledger, slot) },
                    async () => await LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot)));
            }
        }

        /// <summary>
        /// Writes <paramref name="slot"/> of <paramref name="ledger"/> with the fsync of its record held, so the record is
        /// in the page cache and not durable, reads the slot while the write is in flight, and releases the fsync. Each
        /// answer is recorded where it was given, the read's DATA or missing included, so the check holds every crash state
        /// from it on to what it reported, an answer the stopped process gave after a failed operation as well
        /// (conventions.md §13, §14). A read sent once the write failed meets the stopped store, which answers nothing. The
        /// workload's files are not write-through, so each WAL write is durable at its fsync.
        /// </summary>
        public async Task ReadDuringWriteAsync(LedgerId ledger, long slot)
        {
            if (Crashed)
            {
                return;
            }

            var replica = Store!.OpenReplica(ledger, Window);
            FileSystem.HoldFlushes = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var writeStarted = Count;
                var write = LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot));
                var writeAnswered = AnswerPointAsync(write);
                await UntilAsync(() => FileSystem.HeldFlushes.Count != 0 || write.IsCompleted, "The WAL did not fsync the write.");
                var writeFailed = write.IsFaulted;
                var readStarted = Count;
                var read = LedgerReplicaTestData.ReadBytesAsync(replica, slot, Token);
                var readAnswered = AnswerPointAsync(read);
                FileSystem.HoldFlushes = null;
                foreach (var held in FileSystem.HeldFlushes)
                {
                    held.Release();
                }

                await Task.WhenAll(write, read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Assert.True(Crashed || write.IsCompletedSuccessfully && read.IsCompletedSuccessfully, Invariant($"write {write.Status} {write.Exception}, read {read.Status} {read.Exception}"));
                Assert.False(writeFailed && read.IsCompletedSuccessfully, "The stopped store answered a read sent after the write failed.");
                Steps.Add(new Step(StepKind.Write, ledger, writeStarted, await writeAnswered) { Slot = slot, Value = Payload(ledger, slot) });
                if (read.IsCompletedSuccessfully)
                {
                    Steps.Add(new Step(StepKind.Read, ledger, readStarted, await readAnswered) { Slot = slot, Value = read.Result });
                }
            }
            finally
            {
                FileSystem.HoldFlushes = null;
                await replica.DisposeAsync();
            }

            await SettleAsync();
        }

        /// <summary>
        /// Checks that an injected failure stopped the process, and that its store, once the failure reached it, answers
        /// nothing (conventions.md §14): every operation fails with the reported failure which stopped it. A failure
        /// before the store opened, or while it closed, leaves no store serving.
        /// </summary>
        public async Task AssertStoppedAsync()
        {
            Assert.True(Crashed, "The injected failure did not stop the process.");
            if (Store is not { } store || store.IsDisposed)
            {
                return;
            }

            await UntilAsync(() => store.Stopped is not null, "The store did not stop at the failed operation.");

            // A replica opened past the store's refusal, which the check covers, reaches each replica operation.
            await using var replica = new LedgerStoreReplica(store, S1, Window);
            await StoppedStore.AssertAnswersNothingAsync(store, replica, store.Stopped!);
        }

        /// <summary>
        /// Checkpoints a replica once the WAL's durable frontier has passed every acknowledged write; a checkpoint which
        /// returned is acknowledged, whether or not the process crashed afterwards.
        /// </summary>
        public async Task CheckpointAsync(LedgerId ledger)
        {
            if (Crashed)
            {
                return;
            }

            await using var replica = Assert.IsType<LedgerStoreReplica>(Store!.OpenReplica(ledger, Window));
            var started = Count;
            try
            {
                await LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
            }
            catch (Exception) when (Crashed)
            {
                return;
            }

            AddCheckpoint(replica, started);
        }

        /// <summary>
        /// Checkpoints a replica, once the WAL's durable frontier has passed every acknowledged write, whose root write
        /// reaches the device and then fails, as a device error reports it: the checkpoint fails, the failure is reported,
        /// which stops the process there (conventions.md §14), and the root's bytes stay in the page cache, unsynced,
        /// where the restarted process and a crash may find them.
        /// </summary>
        public async Task FailCheckpointRootAsync(LedgerId ledger)
        {
            await using var replica = Assert.IsType<LedgerStoreReplica>(Store!.OpenReplica(ledger, Window));
            FileSystem.HoldWrites = static path => path.EndsWith("/" + SlotCheckpoints.RootName, StringComparison.Ordinal);
            var reported = FatalErrors.ReportedCount;
            try
            {
                var checkpoint = LedgerStoreReplicaTests.SettledCheckpointAsync(replica);
                await UntilAsync(() => FileSystem.HeldWrites.Count == 1, "The checkpoint did not write its root.");
                FileSystem.HeldWrites[0].Fail();
                _ = await Assert.ThrowsAsync<IOException>(() => checkpoint);
            }
            finally
            {
                FileSystem.HoldWrites = null;
            }

            var failure = Assert.Single(FatalErrors.TakeMine(reported));
            Assert.EndsWith(SlotCheckpoints.RootName, failure.Path, StringComparison.Ordinal);
            Assert.True(Crashed, "The failed root write did not stop the process.");
        }

        /// <summary>
        /// Lets the WAL's held preparation of its next file run to completion at this point: its creation, header and
        /// zero-fill, flush, publication, and directory flush.
        /// </summary>
        public async Task PrepareNextFileAsync()
        {
            await UntilAsync(() => FileSystem.HeldCreations.Count == 1, "The WAL did not prepare its next file.");
            var prepared = FileSystem.HeldCreations[0];
            prepared.Release();
            await UntilAsync(
                () => FileSystem.Operations.Any(operation => operation.Kind == FileSystemOperationKind.Flush && operation.Path == prepared.Path),
                "The prepared file did not finish staging.");
        }

        /// <summary>
        /// Holds a WAL write per slot, each in its own write group, and checkpoints. Then completes the last write first,
        /// so a record is published above the durable frontier, and the others in seeded order, checkpointing after each
        /// completion once the WAL has retired the completed write and advanced its frontier: the WAL answers a record
        /// before it advances the frontier, so a checkpoint at the answer would cover the record or not, as thread timing
        /// decides.
        /// </summary>
        public async Task RaceAsync(LedgerId ledger, long[] slots, Random random)
        {
            // The WAL retires the previous step's records, so it counts only this step's.
            await WaitForFrontierAsync();
            Assert.Equal(0, Store!.Wal.OutstandingRecords);
            var replica = Assert.IsType<LedgerStoreReplica>(Store.OpenReplica(ledger, Window));
            FileSystem.HoldWrites = static path => path.EndsWith(".clfl", StringComparison.Ordinal);
            try
            {
                var pending = new List<(long Slot, int Started, Task Write, RecordingFileSystem.HeldWrite Held)>();
                foreach (var slot in slots)
                {
                    var started = Count;
                    var write = LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot));
                    await UntilAsync(() => FileSystem.HeldWrites.Count == pending.Count + 1, "The WAL did not issue the held write.");
                    pending.Add((slot, started, write, FileSystem.HeldWrites[^1]));
                }

                await CheckpointNowAsync(replica);
                while (pending.Count != 0)
                {
                    var index = pending.Count == slots.Length ? pending.Count - 1 : random.Next(pending.Count);
                    var (slot, started, write, held) = pending[index];
                    pending.RemoveAt(index);
                    held.Complete();
                    await write;
                    Steps.Add(new Step(StepKind.Write, ledger, started, Count) { Slot = slot, Value = Payload(ledger, slot) });

                    // The WAL advances its frontier as it retires a record, after its outcome callback.
                    await UntilAsync(() => Store.Wal.OutstandingRecords == pending.Count, "The WAL did not retire the completed write.");
                    await CheckpointNowAsync(replica);
                }
            }
            finally
            {
                FileSystem.HoldWrites = null;
                await replica.DisposeAsync();
            }
        }

        /// <summary>
        /// Writes <paramref name="slots"/> of <paramref name="ledger"/>, each in its own held write group above the
        /// <paramref name="held"/> writes already held, and completes them, last first.
        /// </summary>
        private async Task CompleteLastFirstAsync(LedgerStoreReplica replica, LedgerId ledger, long[] slots, int held)
        {
            var pending = new List<(long Slot, int Started, Task Write, RecordingFileSystem.HeldWrite Held)>();
            foreach (var slot in slots)
            {
                var started = Count;
                var write = LedgerStoreReplicaTests.WriteAsync(replica, slot, Payload(ledger, slot));
                await UntilAsync(() => FileSystem.HeldWrites.Count == pending.Count + held + 1, "The WAL did not issue the held write.");
                pending.Add((slot, started, write, FileSystem.HeldWrites[^1]));
            }

            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var (slot, started, write, heldWrite) = pending[index];
                heldWrite.Complete();
                await write;
                Steps.Add(new Step(StepKind.Write, ledger, started, Count) { Slot = slot, Value = Payload(ledger, slot) });
            }
        }

        /// <summary>
        /// Returns the position of the drop record of <paramref name="ledger"/> in the durable WAL.
        /// </summary>
        private async Task<WalPosition> FindDropRecordAsync(LedgerId ledger)
        {
            await foreach (var entry in Store!.Wal.ReadAllAsync(default, Token))
            {
                if (entry.TryGetValue(out WalRecord record)
                    && StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed
                    && parsed is DropRecord drop
                    && drop.Ledger == ledger)
                {
                    return record.Position;
                }
            }

            throw new InvalidOperationException(Invariant($"The WAL holds no drop record of ledger {ledger.Value}."));
        }

        /// <summary>
        /// Returns the position of the create record which lists <paramref name="ledger"/> in the durable WAL.
        /// </summary>
        private async Task<WalPosition> FindCreateRecordAsync(LedgerId ledger)
        {
            await foreach (var entry in Store!.Wal.ReadAllAsync(default, Token))
            {
                if (entry.TryGetValue(out WalRecord record)
                    && StoreRecordTestHelpers.Decode(record.Payload, out _) is { } parsed
                    && parsed is CreateRecord create
                    && create.Pairs.Any(pair => pair.Ledger == ledger))
                {
                    return record.Position;
                }
            }

            throw new InvalidOperationException(Invariant($"The WAL holds no create record of ledger {ledger.Value}."));
        }

        private static async Task UntilAsync(Func<bool> condition, string description)
        {
            var started = TimeProvider.System.GetTimestamp();
            while (!condition())
            {
                Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30), description);
                await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
            }
        }

        private static byte[] Payload(LedgerId ledger, long slot) =>
            LedgerReplicaTestData.CreatePayload(24 + (int)(slot % 40), (int)(ledger.Value % 1000) + (int)(slot % 1000));

        /// <summary>
        /// Runs a step unless the process has crashed, and records it: acknowledged where its action returned, since an
        /// answer the stopped process gave after a failed operation must hold as well, and unacknowledged when it fails
        /// because the process crashed.
        /// </summary>
        private async Task StepAsync(Step step, Func<Task> action, Func<Step, Step>? complete = null)
        {
            if (Crashed)
            {
                return;
            }

            var acked = int.MaxValue;
            try
            {
                await action();
                acked = Count;
            }
            catch (Exception) when (Crashed)
            {
                // The step's outcome is unknown.
            }

            Steps.Add((complete?.Invoke(step) ?? step) with { Acked = acked });
            await SettleAsync();
        }

        /// <summary>
        /// Waits until the store's maintenance has finished the work the step made due, its catalog checkpoint, the
        /// deletions behind it, and the release, so the trace follows from the workload and its seed.
        /// </summary>
        private async Task SettleAsync()
        {
            if (Store is not { } store)
            {
                return;
            }

            await UntilAsync(
                () => Crashed || store.MaintenanceCompletion is { IsCompleted: true } || store.MaintenanceIdle,
                "The store's maintenance did not settle.");
        }

        /// <summary>
        /// Waits until the WAL retires every record it accepted, which advances its durable frontier past them: the WAL
        /// answers a record from its outcome callback and advances the frontier afterwards, so a capture requested at the
        /// answer would cut at the frontier or just past the record, as thread timing decides, and the trace would not
        /// replay from the seed (#134).
        /// </summary>
        private Task WaitForFrontierAsync() =>
            UntilAsync(() => Crashed || Store!.Wal.OutstandingRecords == 0, "The WAL's durable frontier did not pass its records.");

        /// <summary>
        /// Requests the catalog checkpoint which maintenance writes for a drop once its interval passes (ledgerstore.md
        /// §8.8), and settles with it, its deletions, and its release. Maintenance first settles with the drop, which
        /// handed it the replica's checkpoint directory to delete: a pass which began before the drop returned could
        /// otherwise take the directory and the request in either order, as thread timing decides (#165). The request
        /// follows the durable frontier past the drop record, so the capture cuts at the frontier in every run (#134).
        /// </summary>
        private async Task CheckpointCatalogAfterDropAsync()
        {
            if (Crashed)
            {
                return;
            }

            await SettleAsync();
            await WaitForFrontierAsync();
            Store!.RequestCatalogCheckpoint();
            await SettleAsync();
        }

        private async Task CheckpointNowAsync(LedgerStoreReplica replica)
        {
            var started = Count;
            await replica.CheckpointAsync(Token);
            AddCheckpoint(replica, started);
        }

        private void AddCheckpoint(LedgerStoreReplica replica, int started) =>
            Steps.Add(new Step(StepKind.Checkpoint, replica.Ledger.Ledger, started, Count)
            {
                Generation = replica.Ledger.CheckpointGeneration,
                ReplayStart = replica.Ledger.ReplayStart,
            });

        /// <summary>
        /// Returns the point at which <paramref name="operation"/> answered, observed as it completes, or
        /// <see cref="int.MaxValue"/> when it failed without an answer.
        /// </summary>
        private Task<int> AnswerPointAsync(Task operation) =>
            operation.ContinueWith(
                completed => completed.IsCompletedSuccessfully ? Count : int.MaxValue,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }
}
