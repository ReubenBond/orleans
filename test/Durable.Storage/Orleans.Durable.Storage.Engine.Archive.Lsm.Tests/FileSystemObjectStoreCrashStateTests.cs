using System.Globalization;
using Microsoft.Accordant;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Crash-state enumeration of the file-system object store (verification.md §7.5, archive §9), judged by the Accordant
/// <see cref="ObjectStoreSpec"/>: two store instances, each its own process, share one directory, and in every state an
/// operating-system crash can leave, the objects recover to values the spec allows after every answer either instance
/// gave before the crash, so no crash revokes an answer (conventions.md §13). A call which threw, or whose process
/// crashed, has an unknown outcome which may still take effect once. The store publishes through both paths: a hard
/// link, as on Linux and macOS, and a no-replace rename, as on Windows.
/// </summary>
public sealed class FileSystemObjectStoreCrashStateTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-object-store"));

    /// <summary>
    /// Creates, an existing create, replaces of the manifest from absent, at its version, and at a stale one, ranged and
    /// versioned reads, listings, and a delete, from two processes in turn.
    /// </summary>
    private static readonly Step[] Workload =
    [
        new(First, new ObjectReadVersioned(0)),
        new(First, new ObjectReplace(0, null, 1)),
        new(Second, new ObjectCreate(1, 2)),
        new(First, new ObjectCreate(1, 3)),
        new(Second, new ObjectReplace(0, 1, 4)),
        new(First, new ObjectReplace(0, 1, 5)),
        new(First, new ObjectRead(1, 10, 100)),
        new(Second, new ObjectList(0)),
        new(Second, new ObjectDelete(1)),
        new(First, new ObjectRead(1, 0, 1)),
        new(First, new ObjectCreate(3, 6)),
        new(Second, new ObjectList(1)),
        new(Second, new ObjectReplace(0, 4, 7)),
        new(First, new ObjectReadVersioned(0)),
    ];

    /// <summary>
    /// The reads which judge a recovered store: every name's object, and a listing of every name.
    /// </summary>
    private static readonly object[] Recovery =
    [
        new ObjectReadVersioned(0),
        new ObjectReadVersioned(1),
        new ObjectReadVersioned(2),
        new ObjectReadVersioned(3),
        new ObjectList(0),
    ];

    /// <summary>
    /// Appendable objects, from two processes in turn: creates, an existing create, appends at the length and at a stale
    /// one, a seal and appends after it, one of which finds its bytes there, tombstones over an open and a sealed object,
    /// a seal of a tombstone, a delete of an object a tombstone replaced, an append to the absent object, and a create
    /// again.
    /// </summary>
    private static readonly Step[] AppendableWorkload =
    [
        new(First, new ObjectCreateAppendable(4)),
        new(Second, new ObjectCreateAppendable(4)),
        new(Second, new ObjectAppend(4, 0, 1)),
        new(First, new ObjectAppend(4, 0, 2)),
        new(First, new ObjectAppend(4, 1, 3)),
        new(Second, new ObjectCreateAppendable(5)),
        new(Second, new ObjectAppend(5, 0, 4)),
        new(First, new ObjectSeal(4)),
        new(Second, new ObjectAppend(4, 2, 5)),
        new(First, new ObjectAppend(4, 1, 3)),
        new(Second, new ObjectTombstone(5)),
        new(First, new ObjectAppend(5, 1, 6)),
        new(Second, new ObjectSeal(5)),
        new(First, new ObjectTombstone(4)),
        new(Second, new ObjectDelete(5)),
        new(Second, new ObjectAppend(5, 0, 8)),
        new(First, new ObjectCreateAppendable(5)),
        new(Second, new ObjectAppend(5, 0, 7)),
        new(First, new ObjectList(4)),
    ];

    /// <summary>
    /// The reads which judge a recovered store's appendable objects: their properties, their first two chunks, and a
    /// listing of them.
    /// </summary>
    private static readonly object[] AppendableRecovery =
    [
        new ObjectGetProperties(4),
        new ObjectGetProperties(5),
        new ObjectRead(4, 0, ObjectStoreSpec.ChunkLength),
        new ObjectRead(4, ObjectStoreSpec.ChunkLength, ObjectStoreSpec.ChunkLength),
        new ObjectRead(5, 0, ObjectStoreSpec.ChunkLength),
        new ObjectList(4),
    ];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private const int First = 0;
    private const int Second = 1;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Gets each flush mutant, with each publish path.
    /// </summary>
    public static TheoryData<string, bool> Mutants
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var mutant in MutantScenarios.Keys)
            {
                data.Add(mutant, false);
                data.Add(mutant, true);
            }

            return data;
        }
    }

    /// <summary>
    /// Gets each file-flush mutant, with each publish path.
    /// </summary>
    public static TheoryData<string, bool> FileFlushMutants
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var mutant in FileFlushScenarios.Keys)
            {
                data.Add(mutant, false);
                data.Add(mutant, true);
            }

            return data;
        }
    }

    /// <summary>
    /// Gets each file-flush mutant: the flushes of a write's temporary file it skips, by whether the process holds the
    /// store's lock, and the steps whose publication then names a file whose content a crash can lose: a create's, a
    /// replace's from no object, and a replace's rename over the object.
    /// </summary>
    private static Dictionary<string, FileFlushScenario> FileFlushScenarios { get; } = new(StringComparer.Ordinal)
    {
        ["skip-file-flush-before-a-create-publishes"] = new(static locked => !locked, [new(First, new ObjectCreate(1, 2))]),
        ["skip-file-flush-before-a-replace-publishes"] = new(static locked => locked, [new(First, new ObjectReplace(0, null, 1))]),
        ["skip-file-flush-before-a-replace-renames"] = new(
            static locked => locked,
            [new(Second, new ObjectCreate(0, 1)), new(First, new ObjectReplace(0, 1, 2))]),
    };

    /// <summary>
    /// Gets each flush mutant: a predicate which names, by the calls since the last directory flush, the flush it skips;
    /// the steps which expose it, the answer a skipped flush revokes, and the process which crashes and the call after
    /// which it does, or a step placed inside another, after one of its reads.
    /// </summary>
    private static Dictionary<string, MutantScenario> MutantScenarios { get; } = BuildMutantScenarios();

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EveryAnswerHoldsInEveryCrashState(bool linkByRenaming) => SeededSimulation.RunAsync(0x0b1e, async run =>
    {
        var history = new ObjectStoreHistory();
        var first = new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming };
        var processes = new[] { history.Start(first), history.Start(first.StartProcess()) };
        var answers = await history.RunAllAsync(processes, Workload);
        ObjectOutcomeKind[] kinds =
        [
            ObjectOutcomeKind.Created, ObjectOutcomeKind.Exists, ObjectOutcomeKind.Applied, ObjectOutcomeKind.Mismatch, ObjectOutcomeKind.Absent,
            ObjectOutcomeKind.Present, ObjectOutcomeKind.Deleted, ObjectOutcomeKind.Listed,
        ];
        Assert.Equal(kinds.Order(), answers.Select(static answer => answer.Kind).Distinct().Order());
        Assert.Empty(await history.JudgeEveryStateAsync(first, run));
    });

    /// <summary>
    /// Appendable objects' creates, appends, seals, tombstones, and deletes, each an atomic step whose rename, link, or
    /// unlink a crash keeps or loses, hold every answer in every crash state.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EveryAppendableAnswerHoldsInEveryCrashState(bool linkByRenaming) => SeededSimulation.RunAsync(0x0b20, async run =>
    {
        var history = new ObjectStoreHistory(AppendableRecovery);
        var first = new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming };
        var answers = await history.RunAllAsync([history.Start(first), history.Start(first.StartProcess())], AppendableWorkload);
        ObjectOutcomeKind[] kinds =
        [
            ObjectOutcomeKind.Created, ObjectOutcomeKind.Exists, ObjectOutcomeKind.Appended, ObjectOutcomeKind.LengthMismatch, ObjectOutcomeKind.Sealed,
            ObjectOutcomeKind.NotAppendable, ObjectOutcomeKind.Absent, ObjectOutcomeKind.Tombstoned, ObjectOutcomeKind.Deleted, ObjectOutcomeKind.Listed,
        ];
        Assert.Equal(kinds.Order(), answers.Select(static answer => answer.Kind).Distinct().Order());
        Assert.Empty(await history.JudgeEveryStateAsync(first, run));
    });

    /// <summary>
    /// One process crashes after each of its operations in turn, its creates, renames, links, and unlinks among them,
    /// perhaps holding the store's lock, and stops; the other keeps running the workload, of plain or appendable objects:
    /// an answer which meets what the crashed process left is durable before it leaves.
    /// </summary>
    [Theory]
    [InlineData(false, First, false)]
    [InlineData(true, First, false)]
    [InlineData(false, Second, false)]
    [InlineData(true, Second, false)]
    [InlineData(false, First, true)]
    [InlineData(true, Second, true)]
    public Task EveryAnswerHoldsWhenOneProcessCrashesAtEachOfItsOperations(bool linkByRenaming, int crashing, bool appendable) => SeededSimulation.RunAsync(0x0b1f, async run =>
    {
        var (workload, recovery) = appendable ? (AppendableWorkload, AppendableRecovery) : (Workload, Recovery);
        var complete = new ObjectStoreHistory(recovery);
        var recording = new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming };
        _ = await complete.RunAllAsync([complete.Start(recording), complete.Start(recording.StartProcess())], workload);
        var operations = complete.OperationsOf(crashing);
        Assert.NotEmpty(operations);
        foreach (var operation in operations)
        {
            var history = new ObjectStoreHistory(recovery);
            var first = new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming };
            var second = first.StartProcess();
            var crashed = crashing == First ? first : second;
            crashed.CrashAfter = operation + 1;
            _ = await history.RunAllAsync([history.Start(first), history.Start(second)], workload);
            Assert.True(crashed.Crashed);
            var violations = await history.JudgeEveryStateAsync(first, run);
            Assert.True(
                violations.Count == 0,
                string.Create(CultureInfo.InvariantCulture, $"Process {crashing} crashed after operation {operation}:\n") + string.Join('\n', violations));
        }
    });

    /// <summary>
    /// Each mutant skips one flush of the store, and some crash state revokes the answer the flush protects: judged with
    /// that answer as unknown, every crash state is allowed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Mutants))]
    public async Task ASkippedFlushRevokesTheAnswerItProtects(string mutant, bool linkByRenaming)
    {
        var (history, first) = await RunMutantAsync(MutantScenarios[mutant], linkByRenaming, skipFlush: true);

        var violations = await history.JudgeEveryStateAsync(first);
        Assert.NotEmpty(violations);
        history.Unanswered = MutantScenarios[mutant].Target.Request;
        var unexplained = await history.JudgeEveryStateAsync(first);
        Assert.True(
            unexplained.Count == 0,
            $"Crash states revoke an answer other than {MutantScenarios[mutant].Target.Request}'s:\n" + string.Join('\n', unexplained));
    }

    /// <summary>
    /// The same steps, with every flush, revoke no answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Mutants))]
    public async Task TheSameStepsWithEveryFlushRevokeNoAnswer(string mutant, bool linkByRenaming)
    {
        var (history, first) = await RunMutantAsync(MutantScenarios[mutant], linkByRenaming, skipFlush: false);

        Assert.Empty(await history.JudgeEveryStateAsync(first));
    }

    /// <summary>
    /// Each file-flush mutant leaves a crash state in which a published object's file lacks the content written to it,
    /// which the recovered store returns as bytes no write stores.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileFlushMutants))]
    public async Task ASkippedFileFlushLeavesAPublishedObjectWithoutItsContent(string mutant, bool linkByRenaming)
    {
        var (history, first) = await RunFileFlushMutantAsync(FileFlushScenarios[mutant], linkByRenaming, skip: true);

        Assert.Contains(await history.JudgeEveryStateAsync(first), static violation => violation.Contains("which no write stores", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same steps, with every file's flush, revoke no answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileFlushMutants))]
    public async Task TheSameStepsWithEveryFileFlushRevokeNoAnswer(string mutant, bool linkByRenaming)
    {
        var (history, first) = await RunFileFlushMutantAsync(FileFlushScenarios[mutant], linkByRenaming, skip: false);

        Assert.Empty(await history.JudgeEveryStateAsync(first));
    }

    /// <summary>
    /// A create whose directory flush fails answers nothing and stops its process (conventions.md §14); a read by the
    /// process which restarts in its place flushes the directory, so the object it returns is durable.
    /// </summary>
    [Fact]
    public async Task ACreateWhoseFlushFailsAnswersNothingAndARestartedReadMakesItDurable()
    {
        var history = new ObjectStoreHistory();
        var recording = new RecordingFileSystem(Root) { FailFlushes = static path => path.Length == 0 };
        var since = FatalErrors.ReportedCount;
        Assert.Equal(ObjectOutcome.Unknown, await history.RunAsync(history.Start(recording), new ObjectCreate(1, 2)));
        var reported = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((FatalErrorSite.FlushDirectory, Root), (reported.Site, reported.Path));

        var restarted = recording.Restart();
        restarted.FailFlushes = null;
        Assert.Equal(new ObjectOutcome(ObjectOutcomeKind.Present, 2), await history.RunAsync(history.Start(restarted), new ObjectReadVersioned(1)));
        Assert.Empty(await history.JudgeEveryStateAsync(restarted));
    }

    /// <summary>
    /// A crash after a create's hard link leaves its temporary file. The store which the restarted process opens past the
    /// file's age deletes it as it opens, flushing no directory, and every answer holds in every crash state, those after
    /// the unlink among them (archive §9).
    /// </summary>
    [Fact]
    public Task AReclamationAtOpenRevokesNoAnswer() => SeededSimulation.RunAsync(0x0b20, async run =>
    {
        var history = new ObjectStoreHistory();
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow() - TimeSpan.FromHours(2));
        var recording = new RecordingFileSystem(Root, timeProvider: clock);
        var crashing = history.Start(recording);
        crashing.View.CrashAfter = static call => call is { Kind: SeamCallKind.Link, Found: true };
        Assert.Equal(ObjectOutcome.Unknown, await history.RunAsync(crashing, new ObjectCreate(1, 2)));
        var restarted = recording.Restart();
        _ = Assert.Single(TemporaryFiles(restarted));

        var opened = history.Start(restarted);

        Assert.Empty(TemporaryFiles(restarted));
        Assert.Equal(0, opened.View.Flushes);
        Assert.Equal(new ObjectOutcome(ObjectOutcomeKind.Present, 2), await history.RunAsync(opened, new ObjectReadVersioned(1)));
        Assert.Empty(await history.JudgeEveryStateAsync(recording, run));

        static IEnumerable<string> TemporaryFiles(RecordingFileSystem recording) =>
            recording.EnumerateFiles(Root, "*").Where(static path => path.EndsWith(".tmp", StringComparison.Ordinal));
    });

    private static Dictionary<string, MutantScenario> BuildMutantScenarios()
    {
        var created = new Step(First, new ObjectCreate(1, 2));
        var exists = new Step(Second, new ObjectCreate(1, 3));
        var absentRead = new Step(Second, new ObjectRead(1, 0, 1));
        var listing = new Step(Second, new ObjectList(0));
        var deleted = new Step(First, new ObjectDelete(1));
        var versionCheck = new Step(First, new ObjectReplace(0, 1, 3));
        var presentVersioned = new Step(First, new ObjectReadVersioned(0));
        var absentVersioned = new Step(Second, new ObjectReadVersioned(1));
        var lostRace = new Step(First, new ObjectReplace(1, null, 4));
        static bool UnflushedRename(SeamCall call) => call.Kind == SeamCallKind.Move;
        static bool UnflushedUnlink(SeamCall call) => call is { Kind: SeamCallKind.Delete, IsTemporary: false };
        return new(StringComparer.Ordinal)
        {
            // The flush between a create's publication and Created.
            ["skip-flush-before-created"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Link, Found: true, UnderLock: false }),
                [created],
                created),

            // The flush before Exists, against another process's publication which it has not flushed.
            ["skip-flush-before-exists"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Link, Found: false, UnderLock: false }),
                [new(First, new ObjectCreate(1, 2)), exists],
                exists,
                new(First, static call => call is { Kind: SeamCallKind.Link, Found: true })),

            // The flush before a ranged read answers absent, after another process's unflushed unlink.
            ["skip-flush-before-an-absent-read"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Open, Found: false, UnderLock: false }),
                [new(Second, new ObjectCreate(1, 2)), new(First, new ObjectDelete(1)), absentRead],
                absentRead,
                new(First, UnflushedUnlink)),

            // The flush before a versioned read answers, after another process's unflushed rename over the object.
            ["skip-flush-before-a-versioned-read"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Read, Found: true, UnderLock: false }),
                [new(First, new ObjectReplace(0, null, 1)), new(Second, new ObjectReplace(0, 1, 2)), presentVersioned],
                presentVersioned,
                new(Second, UnflushedRename)),

            // The flush before a versioned read answers absent, after another process's unflushed unlink.
            ["skip-flush-before-an-absent-versioned-read"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Read, Found: false, UnderLock: false }),
                [new(Second, new ObjectCreate(1, 2)), new(First, new ObjectDelete(1)), absentVersioned],
                absentVersioned,
                new(First, UnflushedUnlink)),

            // The flush before a listing answers, after another process's unflushed unlink.
            ["skip-flush-before-a-listing"] = new(
                static calls => calls.Any(static call => call.Kind == SeamCallKind.List),
                [new(Second, new ObjectCreate(1, 2)), new(First, new ObjectDelete(1)), listing],
                listing,
                new(First, UnflushedUnlink)),

            // The flush before a delete completes.
            ["skip-flush-before-a-delete-completes"] = new(
                static calls => calls.Any(UnflushedUnlink),
                [new(First, new ObjectCreate(1, 2)), deleted],
                deleted),

            // The flush before a replace compares versions, after another process's unflushed rename over the object.
            ["skip-flush-before-a-version-check"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Open, UnderLock: true })
                    && !calls.Any(static call => call.Kind is SeamCallKind.Move or SeamCallKind.Link),
                [new(First, new ObjectReplace(0, null, 1)), new(Second, new ObjectReplace(0, 1, 2)), versionCheck],
                versionCheck,
                new(Second, UnflushedRename)),

            // The flush before Mismatch when a replace which expected no object loses its publication to a create which
            // another process published after the replace's check and has not flushed.
            ["skip-flush-before-a-lost-race-mismatch"] = new(
                static calls => calls.Any(static call => call is { Kind: SeamCallKind.Link, Found: false, UnderLock: true }),
                [lostRace],
                lostRace,
                new(Second, static call => call is { Kind: SeamCallKind.Link, Found: true }),
                new Interleave(First, new(Second, new ObjectCreate(1, 5)))),
        };
    }

    /// <summary>
    /// Runs a mutant's steps with its flush skipped or kept, and returns the history and the first process's recording.
    /// </summary>
    private static async Task<(ObjectStoreHistory History, RecordingFileSystem First)> RunMutantAsync(MutantScenario scenario, bool linkByRenaming, bool skipFlush)
    {
        var history = new ObjectStoreHistory();
        var first = new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming };
        var processes = new[] { history.Start(first), history.Start(first.StartProcess()) };
        foreach (var process in processes)
        {
            process.View.SkipFlush = skipFlush ? scenario.SkipFlush : null;
        }

        if (scenario.CrashAfter is { } crash)
        {
            processes[crash.Process].View.CrashAfter = crash.After;
        }

        if (scenario.Inside is { } inside)
        {
            var placed = false;
            processes[inside.Process].View.BeforeWrite = async locked =>
            {
                if (!placed && locked)
                {
                    placed = true;
                    _ = await history.RunAsync(processes[inside.Step.Process], inside.Step.Request);
                }
            };
        }

        _ = await history.RunAllAsync(processes, scenario.Steps);
        Assert.Equal(skipFlush, processes.Sum(static process => process.View.SkippedFlushes) > 0);
        return (history, first);
    }

    /// <summary>
    /// Runs a file-flush mutant's steps with its file flushes skipped or kept, and returns the history and the first
    /// process's recording.
    /// </summary>
    private static async Task<(ObjectStoreHistory History, RecordingFileSystem First)> RunFileFlushMutantAsync(FileFlushScenario scenario, bool linkByRenaming, bool skip)
    {
        var history = new ObjectStoreHistory();
        var first = new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming };
        var processes = new[] { history.Start(first), history.Start(first.StartProcess()) };
        foreach (var process in processes)
        {
            process.View.SkipFileFlush = skip ? scenario.SkipFileFlush : null;
        }

        _ = await history.RunAllAsync(processes, scenario.Steps);
        Assert.Equal(skip, processes.Sum(static process => process.View.SkippedFileFlushes) > 0);
        return (history, first);
    }

    /// <summary>
    /// One call of a workload, by the process it names.
    /// </summary>
    private sealed record Step(int Process, object Request);

    /// <summary>
    /// The process which crashes in a mutant's scenario, and the call after which it does.
    /// </summary>
    private sealed record Crash(int Process, Func<SeamCall, bool> After);

    /// <summary>
    /// A step another process runs inside a call of <paramref name="Process"/>: a replace's or delete's, before its first
    /// write under the store's lock, after the check it made.
    /// </summary>
    private sealed record Interleave(int Process, Step Step);

    /// <summary>
    /// A flush mutant: the flush it skips, the steps which expose it, the step whose answer the flush protects, and a
    /// crash or an interleaving which leaves another process's change unflushed when that answer is given.
    /// </summary>
    private sealed record MutantScenario(
        Func<IReadOnlyList<SeamCall>, bool> SkipFlush,
        Step[] Steps,
        Step Target,
        Crash? CrashAfter = null,
        Interleave? Inside = null);

    /// <summary>
    /// A file-flush mutant: the flushes of temporary files it skips, given whether the process holds the store's lock,
    /// and the steps which publish such a file.
    /// </summary>
    private sealed record FileFlushScenario(Func<bool, bool> SkipFileFlush, Step[] Steps);

    /// <summary>
    /// One process: its view of the recorded file system and the executor of its calls.
    /// </summary>
    private sealed record StoreProcess(int Index, ScriptedFileSystem View, ObjectStoreExecutor Executor);

    /// <summary>
    /// The calls made of the store across processes, each with the span of the trace it covers, which the spec judges
    /// at every crash point.
    /// </summary>
    private sealed class ObjectStoreHistory(object[]? recovery = null)
    {
        private readonly object[] _recovery = recovery ?? Recovery;
        private readonly Spec<ObjectStoreState> _spec = ObjectStoreSpec.Create(unknownOutcomes: true);
        private readonly List<Call> _calls = [];
        private readonly Dictionary<int, StateProfile> _profiles = [];
        private ObjectStoreExecutor? _knowledge;
        private int _processes;

        /// <summary>
        /// Gets or sets a request whose answer the judge treats as unknown, to show that a violation revokes that answer.
        /// </summary>
        public object? Unanswered
        {
            get;
            set
            {
                field = value;
                _profiles.Clear();
            }
        }

        /// <summary>
        /// Starts a store instance, its own process, over <paramref name="recording"/>, which knows every version an
        /// earlier process learned.
        /// </summary>
        public StoreProcess Start(RecordingFileSystem recording)
        {
            var view = new ScriptedFileSystem(recording);
            var store = new FileSystemObjectStore(Root, TimeProvider.System, view);
            _knowledge = _knowledge?.Over(store, store) ?? new ObjectStoreExecutor(store, store);
            return new StoreProcess(_processes++, view, _knowledge);
        }

        /// <summary>
        /// Runs <paramref name="steps"/> in turn, each by its process, skipping those of a process which has crashed.
        /// </summary>
        public async Task<List<ObjectOutcome>> RunAllAsync(IReadOnlyList<StoreProcess> processes, IEnumerable<Step> steps)
        {
            var outcomes = new List<ObjectOutcome>();
            foreach (var step in steps)
            {
                var process = processes[step.Process];
                if (!process.View.Inner.Crashed)
                {
                    outcomes.Add(await RunAsync(process, step.Request));
                }
            }

            return outcomes;
        }

        /// <summary>
        /// Runs one call, records its span and answer, and requires the spec to allow every answer so far. A call which
        /// throws <see cref="IOException"/>, as a crashed process's and an injected failure's calls do, answers nothing.
        /// </summary>
        public async Task<ObjectOutcome> RunAsync(StoreProcess process, object request)
        {
            var recording = process.View.Inner;
            var start = recording.OperationCount;
            (ObjectOutcome Outcome, int? End) answer;
            try
            {
                answer = (await process.Executor.RunAsync(request, Token), recording.OperationCount);
            }
            catch (IOException)
            {
                answer = (ObjectOutcome.Unknown, null);
            }

            _calls.Add(new Call(process.Index, start, answer.End, request, answer.Outcome));
            _profiles.Clear();
            _ = AllowedStates(recording.OperationCount);
            return answer.Outcome;
        }

        /// <summary>
        /// Returns the indexes of the operations which <paramref name="process"/>'s answered calls recorded.
        /// </summary>
        public List<int> OperationsOf(int process) =>
            [.. _calls.Where(call => call.Process == process && call.End is not null).SelectMany(static call => Enumerable.Range(call.Start, call.End!.Value - call.Start))];

        /// <summary>
        /// Recovers the store from every crash state of <paramref name="recording"/>'s trace through a fresh process, and
        /// returns each state whose objects the spec does not allow after every answer given by its crash point; no
        /// point is sampled.
        /// </summary>
        public async Task<List<string>> JudgeEveryStateAsync(RecordingFileSystem recording, SimulationRun? run = null)
        {
            var enumerator = CrashStateEnumerator.Of(recording, run?.Seed ?? 0);
            run?.UseHistory(() => enumerator.Operations.Select(static (operation, index) => string.Create(CultureInfo.InvariantCulture, $"#{index} {operation}")));
            var states = enumerator.Enumerate(0, enumerator.Operations.Count, int.MaxValue);
            Assert.Equal(0, states.SampledPoints);
            var violations = new List<string>();
            foreach (var state in states.States)
            {
                if (await JudgeAsync(enumerator, state) is { } violation)
                {
                    violations.Add(violation);
                }
            }

            TestDiagnostics.WriteLine(states.ToString());
            return violations;
        }

        private async Task<string?> JudgeAsync(CrashStateEnumerator enumerator, CrashState state)
        {
            var recovering = new RecordingFileSystem(Root, enumerator.Build(state));
            var recovered = new FileSystemObjectStore(Root, TimeProvider.System, recovering);
            var executor = _knowledge!.Over(recovered, recovered);
            var profile = AllowedStates(state.Point);
            foreach (var request in _recovery)
            {
                ObjectOutcome outcome;
                try
                {
                    outcome = await executor.RunAsync(request, Token);
                }
                catch (InvalidDataException exception)
                {
                    // The store holds content no write stored, which no answer allows.
                    return string.Create(
                        CultureInfo.InvariantCulture, $"At point {state.Point} the store recovered {request} as {exception.Message}\n{enumerator.Describe(state)}");
                }

                var (allowed, explanation, next) = _spec.Allows(_spec.GetOperation(ObjectStoreSpec.OperationOf(request)), request, outcome, profile);
                if (!allowed)
                {
                    return string.Create(
                        CultureInfo.InvariantCulture, $"At point {state.Point} the store recovered {request} → {outcome}: {explanation}\n{enumerator.Describe(state)}");
                }

                profile = next;
            }

            return null;
        }

        /// <summary>
        /// Returns the states the spec allows after the first <paramref name="point"/> operations, and requires it to
        /// allow every answer given by then: each call answered by then has its answer, and each begun but unanswered
        /// call an unknown outcome. A call which recorded no operation, as one whose flush a mutant skipped, counts at
        /// the point where it answered.
        /// </summary>
        private StateProfile AllowedStates(int point)
        {
            if (_profiles.TryGetValue(point, out var cached))
            {
                return cached;
            }

            var profile = new StateProfile(new ObjectStoreState());
            foreach (var call in _calls.Where(call => call.Start < point || call.Start == point && call.End == point))
            {
                var outcome = call.End <= point && !ReferenceEquals(call.Request, Unanswered) ? call.Outcome : ObjectOutcome.Unknown;
                var (allowed, explanation, next) = _spec.Allows(_spec.GetOperation(ObjectStoreSpec.OperationOf(call.Request)), call.Request, outcome, profile);
                if (!allowed)
                {
                    Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"At point {point}, {call.Request} → {outcome}: {explanation}"));
                }

                profile = next;
            }

            _profiles[point] = profile;
            return profile;
        }

        private sealed record Call(int Process, int Start, int? End, object Request, ObjectOutcome Outcome);
    }
}
