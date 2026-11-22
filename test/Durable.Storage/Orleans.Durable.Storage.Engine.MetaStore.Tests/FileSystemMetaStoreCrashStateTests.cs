using System.Buffers;
using System.Globalization;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

/// <summary>
/// Crash-state enumeration of the file-system MetaStore (verification.md §7.5), judged by the Accordant
/// <see cref="ConditionalRegisterSpec"/>: in every state an operating-system crash can leave, the register recovers to
/// a value, at a version, which the spec allows after every answer given before the crash, so no crash revokes an
/// answer (conventions.md §13, MS-I1). A version an applied write returned before the crash names its value in every state
/// after it: the executor requires each recovered read to return a value at the version an earlier answer gave it. A call
/// which threw, or whose process crashed, has an unknown outcome which may still take effect once (MS-I3).
/// </summary>
public sealed class FileSystemMetaStoreCrashStateTests : IDisposable
{
    private const string Name = "l/orders";
    private static readonly Guid Cluster = new("0123456789abcdef0123456789abcdef");
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-metastore"));
    private static readonly string ClusterDirectory = Path.Combine(Root, Cluster.ToString("N"));

    /// <summary>
    /// A read of an absent register, a create, a mismatched create, updates, a stale update, and reads: the first write
    /// creates the cluster's directory, and later writes replace the register's file with values of one to three
    /// sectors.
    /// </summary>
    private static readonly object[] Workload =
    [
        RegisterRead.Instance,
        new RegisterWrite(null, 1),
        new RegisterWrite(null, 2),
        RegisterRead.Instance,
        new RegisterWrite(1, 63),
        new RegisterWrite(1, 4),
        RegisterRead.Instance,
        new RegisterWrite(63, 5),
        RegisterRead.Instance,
    ];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task EveryAnswerHoldsInEveryCrashState() => SeededSimulation.RunAsync(0x3e7a, async run =>
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var answers = await history.RunAllAsync(await history.StartAsync(recording), Workload);
        RegisterOutcomeKind[] kinds = [RegisterOutcomeKind.Applied, RegisterOutcomeKind.Mismatch, RegisterOutcomeKind.Absent, RegisterOutcomeKind.Present];
        Assert.Equal(kinds.Order(), answers.Select(static answer => answer.Kind).Distinct().Order());

        var recovered = await history.JudgeEveryStateAsync(recording, run);
        Assert.Superset(
            new HashSet<RegisterOutcome>
            {
                new(RegisterOutcomeKind.Absent),
                new(RegisterOutcomeKind.Present, 1),
                new(RegisterOutcomeKind.Present, 63),
                new(RegisterOutcomeKind.Present, 5),
            },
            recovered);
    });

    /// <summary>
    /// The process crashes after each operation of <see cref="Workload"/> in turn, and a restarted process creates,
    /// reads, and updates the register: a create meets the crashed writer's directory or its renamed value, and its
    /// answer is durable before it leaves.
    /// </summary>
    [Fact]
    public Task EveryAnswerHoldsAfterAProcessCrashAtEachOperation() => SeededSimulation.RunAsync(0x3e7b, async run =>
    {
        var complete = new RecordingFileSystem(Root);
        var completeHistory = new RegisterHistory();
        _ = await completeHistory.RunAllAsync(await completeHistory.StartAsync(complete), Workload);
        for (var crashAfter = 1; crashAfter <= complete.OperationCount; crashAfter++)
        {
            var crashed = new RecordingFileSystem(Root) { CrashAfter = crashAfter };
            var history = new RegisterHistory();
            _ = await history.RunAllAsync(await history.StartAsync(crashed), Workload);
            Assert.True(crashed.Crashed);

            var restarted = crashed.Restart();
            var process = await history.StartAsync(restarted);
            _ = await history.RunAsync(process, new RegisterWrite(null, 9));
            var read = await history.RunAsync(process, RegisterRead.Instance);
            Assert.Equal(RegisterOutcomeKind.Present, read.Kind);
            Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(process, new RegisterWrite(read.Value, 10)));
            Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 10), await history.RunAsync(process, RegisterRead.Instance));
            _ = await history.JudgeEveryStateAsync(restarted, run, string.Create(CultureInfo.InvariantCulture, $"The process crashed after operation {crashAfter}."));
        }
    });

    /// <summary>
    /// A writer in another process renames its value over the register just before a read, and has not flushed the
    /// directory when the read answers: the read flushes after reading, so the value it returns is durable.
    /// </summary>
    [Fact]
    public async Task AValueAReadReturnsIsDurableThoughItsLiveWriterHasNotFlushed()
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var reader = await history.StartAsync(recording);
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(reader, new RegisterWrite(null, 1)));
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 1), await history.RunAsync(reader, RegisterRead.Instance));

        reader.View.BeforeNextRead = async path =>
        {
            var writer = new RegisterWrite(1, 2);
            history.Begin(writer, recording.OperationCount);
            var temporary = Path.Combine(ClusterDirectory, "writer~tmp");
            using (var file = recording.CreateFile(temporary, replace: false, writeThrough: false, preallocationSize: 0))
            {
                await file.WriteAsync(FileSystemConditionalRegister.Encode(Guid.NewGuid(), new ReadOnlySequence<byte>(ConditionalRegisterSpec.Payload(writer.Value))), 0, Token);
                file.Flush();
            }

            recording.MoveFile(temporary, path, replace: true);
        };
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 2), await history.RunAsync(reader, RegisterRead.Instance));
        _ = await history.JudgeEveryStateAsync(recording);
    }

    /// <summary>
    /// The process fails at each operation of <see cref="Workload"/> in turn, which the file-system seam reports, and
    /// stops there (conventions.md §14); a restarted process creates, reads, and updates the register, and every answer
    /// either process gave holds in every crash state, the failed call's outcome unknown.
    /// </summary>
    [Fact]
    public Task EveryAnswerHoldsAfterAFailureOfEachOperation() => SeededSimulation.RunAsync(0x3e7c, async run =>
    {
        var complete = new RecordingFileSystem(Root);
        var completeHistory = new RegisterHistory();
        _ = await completeHistory.RunAllAsync(await completeHistory.StartAsync(complete), Workload);
        for (var failed = 0; failed < complete.OperationCount; failed++)
        {
            var since = FatalErrors.ReportedCount;
            var failing = new RecordingFileSystem(Root) { FailOperation = failed };
            var history = new RegisterHistory();
            var outcomes = await history.RunAllAsync(await history.StartAsync(failing), Workload);
            Assert.Equal(RegisterOutcome.Unknown, outcomes[^1]);
            Assert.True(failing.Crashed);
            var reported = Assert.Single(FatalErrors.TakeMine(since));
            Assert.StartsWith(Root, reported.Path, StringComparison.Ordinal);

            var restarted = failing.Restart();
            var process = await history.StartAsync(restarted);
            _ = await history.RunAsync(process, new RegisterWrite(null, 9));
            var read = await history.RunAsync(process, RegisterRead.Instance);
            Assert.Equal(RegisterOutcomeKind.Present, read.Kind);
            Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(process, new RegisterWrite(read.Value, 10)));
            _ = await history.JudgeEveryStateAsync(restarted, run, string.Create(CultureInfo.InvariantCulture, $"Operation {failed} failed."));
        }
    });

    /// <summary>
    /// A write whose flush after its rename fails answers nothing and stops the process; a restarted process's read of
    /// its value flushes it first, so the value it returns is durable.
    /// </summary>
    [Fact]
    public async Task AWriteWhoseLastFlushFailsAnswersNothingAndARestartedReadMakesItsValueDurable()
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var process = await history.StartAsync(recording);
        var since = FatalErrors.ReportedCount;
        process.View.FailDirectoryFlush = Fail(ClusterDirectory, times: 1);
        Assert.Equal(RegisterOutcome.Unknown, await history.RunAsync(process, new RegisterWrite(null, 1)));
        AssertReported(since, FatalErrorSite.FlushDirectory, ClusterDirectory);

        var restarted = recording.Restart();
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 1), await history.RunAsync(await history.StartAsync(restarted), RegisterRead.Instance));
        _ = await history.JudgeEveryStateAsync(restarted);
    }

    /// <summary>
    /// A read whose flush fails answers nothing, though it read a value: the value's rename may not be durable.
    /// </summary>
    [Fact]
    public async Task AReadWhoseFlushFailsAnswersNothing()
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var process = await history.StartAsync(recording);
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(process, new RegisterWrite(null, 1)));
        var since = FatalErrors.ReportedCount;
        process.View.FailDirectoryFlush = Fail(ClusterDirectory, times: 1);
        Assert.Equal(RegisterOutcome.Unknown, await history.RunAsync(process, RegisterRead.Instance));
        AssertReported(since, FatalErrorSite.FlushDirectory, ClusterDirectory);
        _ = await history.JudgeEveryStateAsync(recording);
    }

    /// <summary>
    /// A write whose flush of the root fails, after it created the cluster's directory, renames nothing, answers nothing,
    /// and stops the process; a restarted process's write in that directory makes its name durable.
    /// </summary>
    [Fact]
    public async Task AWriteWhoseRootFlushFailsPublishesNothing()
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var process = await history.StartAsync(recording);
        var since = FatalErrors.ReportedCount;
        process.View.FailDirectoryFlush = Fail(Root, times: 1);
        Assert.Equal(RegisterOutcome.Unknown, await history.RunAsync(process, new RegisterWrite(null, 1)));
        AssertReported(since, FatalErrorSite.FlushDirectory, Root);
        Assert.DoesNotContain(recording.Operations, static operation => operation.Kind == FileSystemOperationKind.Rename);

        var restarted = recording.Restart();
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(await history.StartAsync(restarted), new RegisterWrite(null, 2)));
        _ = await history.JudgeEveryStateAsync(restarted);
    }

    /// <summary>
    /// A write whose rename fails answers nothing and stops the process, leaving its temporary file, which no read opens;
    /// a restarted process's write applies.
    /// </summary>
    [Fact]
    public async Task AWriteWhoseRenameFailsStopsTheProcessAndARestartedWriteApplies()
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var process = await history.StartAsync(recording);
        var since = FatalErrors.ReportedCount;
        process.View.FailMoves = 1;
        Assert.Equal(RegisterOutcome.Unknown, await history.RunAsync(process, new RegisterWrite(null, 1)));
        _ = Assert.Single(FatalErrors.TakeMine(since), static reported => reported.Site == FatalErrorSite.MoveFile);

        var restarted = recording.Restart();
        var next = await history.StartAsync(restarted);
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(next, new RegisterWrite(null, 2)));
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 2), await history.RunAsync(next, RegisterRead.Instance));
        _ = await history.JudgeEveryStateAsync(restarted);
    }

    /// <summary>
    /// A lock which fails, rather than being held by another writer, is reported once and stops the process: the write
    /// neither waits nor tries the lock again. A restarted process's write applies.
    /// </summary>
    [Fact]
    public async Task AWriteWhoseLockFailsStopsTheProcessWithoutWaiting()
    {
        var recording = new RecordingFileSystem(Root);
        var history = new RegisterHistory();
        var process = await history.StartAsync(recording);
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(process, new RegisterWrite(null, 1)));
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 1), await history.RunAsync(process, RegisterRead.Instance));
        var since = FatalErrors.ReportedCount;
        process.View.FailLocks = 3;
        Assert.Equal(RegisterOutcome.Unknown, await history.RunAsync(process, new RegisterWrite(1, 2)));
        Assert.Equal(2, process.View.FailLocks);
        _ = Assert.Single(FatalErrors.TakeMine(since), static reported => reported.Site == FatalErrorSite.OpenLock);

        var restarted = recording.Restart();
        var next = await history.StartAsync(restarted);
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 1), await history.RunAsync(next, RegisterRead.Instance));
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await history.RunAsync(next, new RegisterWrite(1, 3)));
        _ = await history.JudgeEveryStateAsync(restarted);
    }

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Takes the one failure reported since <paramref name="since"/>, which must be <paramref name="operation"/> on
    /// <paramref name="path"/>.
    /// </summary>
    private static void AssertReported(long since, FatalErrorSite operation, string path)
    {
        var reported = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((operation, path), (reported.Site, reported.Path));
    }

    /// <summary>
    /// Returns a predicate which fails the first <paramref name="times"/> flushes of <paramref name="directory"/>.
    /// </summary>
    private static Func<string, bool> Fail(string directory, int times)
    {
        var remaining = times;
        return path =>
        {
            if (remaining == 0 || !string.Equals(path, directory, StringComparison.Ordinal))
            {
                return false;
            }

            remaining--;
            return true;
        };
    }


    /// <summary>
    /// One process: its view of the recorded file system and the executor of its calls.
    /// </summary>
    private sealed record RegisterProcess(RecordingFileSystem Recording, InterceptingFileSystem View, ConditionalRegisterExecutor Executor);

    /// <summary>
    /// The calls made of one register across processes, each with the span of the trace it covers, which the spec
    /// judges at every crash point.
    /// </summary>
    private sealed class RegisterHistory
    {
        private readonly Spec<ConditionalRegisterState> _spec = ConditionalRegisterSpec.Create(unknownOutcomes: true, appliedMismatch: false);
        private readonly List<Call> _calls = [];
        private readonly Dictionary<int, StateProfile> _profiles = [];
        private ConditionalRegisterExecutor? _knowledge;

        /// <summary>
        /// Starts a process over <paramref name="recording"/>, which knows every version an earlier process learned.
        /// </summary>
        public async Task<RegisterProcess> StartAsync(RecordingFileSystem recording)
        {
            var view = new InterceptingFileSystem(recording);
            var register = await RegisterAsync(view);
            _knowledge = _knowledge?.Over(register) ?? new ConditionalRegisterExecutor(register);
            return new RegisterProcess(recording, view, _knowledge);
        }

        /// <summary>
        /// Runs <paramref name="requests"/> in turn until one has an unknown outcome.
        /// </summary>
        public async Task<List<RegisterOutcome>> RunAllAsync(RegisterProcess process, IEnumerable<object> requests)
        {
            var outcomes = new List<RegisterOutcome>();
            foreach (var request in requests)
            {
                outcomes.Add(await RunAsync(process, request));
                if (outcomes[^1] == RegisterOutcome.Unknown)
                {
                    break;
                }
            }

            return outcomes;
        }

        /// <summary>
        /// Runs one call, records its span and answer, and requires the spec to allow every answer so far. A call which
        /// throws <see cref="IOException"/>, as a crashed process's and an injected failure's calls do, answers nothing.
        /// </summary>
        public async Task<RegisterOutcome> RunAsync(RegisterProcess process, object request)
        {
            var start = process.Recording.OperationCount;
            (RegisterOutcome Outcome, int? End) answer;
            try
            {
                var outcome = request switch
                {
                    RegisterWrite write => await process.Executor.WriteAsync(write, Token),
                    RegisterRead => await process.Executor.ReadAsync(Token),
                    _ => throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{request} is not a register request."), nameof(request)),
                };
                answer = (outcome, process.Recording.OperationCount);
            }
            catch (IOException)
            {
                answer = (RegisterOutcome.Unknown, null);
            }

            _calls.Add(new Call(start, answer.End, request, answer.Outcome));
            _profiles.Clear();
            _ = AllowedStates(process.Recording.OperationCount);
            return answer.Outcome;
        }

        /// <summary>
        /// Records a call another process began after <paramref name="start"/> operations and never answered.
        /// </summary>
        public void Begin(object request, int start)
        {
            _calls.Add(new Call(start, null, request, RegisterOutcome.Unknown));
            _profiles.Clear();
        }

        /// <summary>
        /// Recovers the register from every crash state of <paramref name="recording"/>'s trace through a fresh process,
        /// and requires the spec to allow what each reads after every answer given by its crash point; no point is
        /// sampled. Returns what the states recovered.
        /// </summary>
        public async Task<HashSet<RegisterOutcome>> JudgeEveryStateAsync(RecordingFileSystem recording, SimulationRun? run = null, string? context = null)
        {
            var enumerator = CrashStateEnumerator.Of(recording, run?.Seed ?? 0);
            run?.UseHistory(() => enumerator.Operations.Select(static (operation, index) => string.Create(CultureInfo.InvariantCulture, $"#{index} {operation}")));
            var states = enumerator.Enumerate(0, enumerator.Operations.Count, int.MaxValue);
            Assert.Equal(0, states.SampledPoints);
            var recovered = new HashSet<RegisterOutcome>();
            foreach (var state in states.States)
            {
                _ = recovered.Add(await JudgeAsync(enumerator, state, context));
            }

            TestDiagnostics.WriteLine(states.ToString());
            return recovered;
        }

        private async Task<RegisterOutcome> JudgeAsync(CrashStateEnumerator enumerator, CrashState state, string? context)
        {
            var recovering = new RecordingFileSystem(Root, enumerator.Build(state));
            var executor = _knowledge!.Over(await RegisterAsync(recovering));
            RegisterOutcome recovered;
            try
            {
                recovered = await executor.ReadAsync(Token);
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException(Report(context, state.Point, enumerator.Describe(state)), error);
            }

            var (allowed, explanation, _) = _spec.Allows(
                _spec.GetOperation(ConditionalRegisterSpec.Read), RegisterRead.Instance, recovered, AllowedStates(state.Point));
            if (!allowed)
            {
                Assert.Fail(Report(context, state.Point, $"The register recovered as {recovered}: {explanation}\n{enumerator.Describe(state)}"));
            }

            return recovered;
        }

        /// <summary>
        /// Returns the states the spec allows after the first <paramref name="point"/> operations, and requires it to
        /// allow every answer given by then: each call answered by then has its answer, and each begun but unanswered
        /// call an unknown outcome.
        /// </summary>
        private StateProfile AllowedStates(int point)
        {
            if (_profiles.TryGetValue(point, out var cached))
            {
                return cached;
            }

            var profile = new StateProfile(new ConditionalRegisterState());
            foreach (var call in _calls.Where(call => call.Start < point))
            {
                var outcome = call.End <= point ? call.Outcome : RegisterOutcome.Unknown;
                var name = call.Request is RegisterWrite ? ConditionalRegisterSpec.Write : ConditionalRegisterSpec.Read;
                var (allowed, explanation, next) = _spec.Allows(_spec.GetOperation(name), call.Request, outcome, profile);
                if (!allowed)
                {
                    Assert.Fail(Report(null, point, $"{call.Request} → {outcome}: {explanation}"));
                }

                profile = next;
            }

            _profiles[point] = profile;
            return profile;
        }

        private string Report(string? context, int point, string detail) =>
            string.Join(Environment.NewLine, new[] { context, detail, "Calls:" }.OfType<string>().Concat(_calls.Select(call => string.Create(
                CultureInfo.InvariantCulture,
                $"  {call.Request} from operation {call.Start}: {Status(call, point)}"))));

        private static string Status(Call call, int point) => call switch
        {
            _ when call.Start >= point => "not begun",
            _ when call.End <= point => call.Outcome.ToString(),
            _ => "unanswered",
        };

        private static ValueTask<IConditionalRegister> RegisterAsync(WalFileSystem fileSystem) =>
            new FileSystemMetaStore(Root, TimeProvider.System, fileSystem).GetRegisterAsync(Cluster, Name, Token);

        private sealed record Call(int Start, int? End, object Request, RegisterOutcome Outcome);
    }
}
