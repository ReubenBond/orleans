using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// Checks <see cref="SimulatedConditionalRegister"/> and <see cref="FileSystemMetaStore"/> (over the in-memory
/// <see cref="RecordingFileSystem"/>) against <see cref="ConditionalRegisterSpec"/> through the conformance kit
/// (verification.md §4.1, §7.2, metastore.md §10): with unknown outcomes, a write the simulated register fails answers
/// nothing, whether it was lost, applied, or lands later, and the spec judges every answer, an applied-mismatch retry's
/// among them, against what the unknown writes may have done; and every schedule of small concurrent windows, which the
/// kit explores, judged by <c>AllowsConcurrent</c>.
/// </summary>
/// <remarks>
/// Each case runs in a simulation of its own, seeded by the case (<see cref="SimulatedConformance"/>). The file-system
/// MetaStore only ever takes the recorded file system, so no simulation reaches the real disk (verification.md §7.1).
/// </remarks>
public sealed class SimulatedConditionalRegisterConformanceTests
{
    private const int Iterations = 40;
    private const string RegisterName = "ledger-map";
    private static readonly Guid Cluster = new("0123456789abcdef0123456789abcdef");

    /// <summary>The root of each file-system case's recorded file system, which lives in memory.</summary>
    private static readonly string Root = Path.GetFullPath("clog-simulated-file-system-metastore");

    [Fact]
    public async Task TheSimulatedRegisterConformsWithUnknownOutcomes()
    {
        var conformance = new SimulatedConformance();
        var kit = SimulatedKit(conformance, [], unknownOutcomes: true);
        await kit.SampleAsync(kit.Sequences(24), Iterations, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), Iterations, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// Regression for #458 (CsCheck seed <c>1IAuNMHKaXx4</c>, simulation seed <c>390571010</c>): an applied-mismatch
    /// create leaves its version unlearned, so a concurrent write naming that value dispatches before a concurrent read
    /// learns the version and mismatches without applying.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriteBeforeReadAfterAppliedMismatchConforms()
    {
        List<SimulatedConditionalRegister> registers = [];
        List<(object Request, RegisterOutcome Outcome)> observed = [];
        var conformance = new SimulatedConformance();
        var kit = SimulatedKit(conformance, registers, unknownOutcomes: true, onOutcome: (request, outcome) => observed.Add((request, outcome)));
        ConformanceCase regression = new(
        [
            new([new(ConditionalRegisterSpec.Read, RegisterRead.Instance)]),
            new([new(ConditionalRegisterSpec.Read, RegisterRead.Instance)]),
            new([new(ConditionalRegisterSpec.Write, new RegisterWrite(null, 5))]),
            new([
                new(ConditionalRegisterSpec.Write, new RegisterWrite(10, 15)),
                new(ConditionalRegisterSpec.Read, RegisterRead.Instance),
                new(ConditionalRegisterSpec.Write, new RegisterWrite(5, 13)),
            ]),
            new([new(ConditionalRegisterSpec.Read, RegisterRead.Instance)]),
        ],
        Seed: 390571010);

        await kit.RunAsync(regression);
        var register = Assert.Single(registers);
        Assert.Equal(1, register.AppliedMismatches);
        Assert.Equal(ConditionalRegisterSpec.Payload(5), Assert.Single(register.AppliedWrites));
        Assert.Contains((new RegisterWrite(null, 5), new RegisterOutcome(RegisterOutcomeKind.Mismatch)), observed);
        Assert.Contains((new RegisterWrite(5, 13), new RegisterOutcome(RegisterOutcomeKind.Mismatch)), observed);
        Assert.Equal(2, observed.Count(static entry => entry.Request is RegisterRead && entry.Outcome == new RegisterOutcome(RegisterOutcomeKind.Present, 5)));
        Assert.Equal((RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 5)), observed[^1]);
    }

    /// <summary>
    /// A case replays from its seed alone, its faults and its interleaving with it, and each fault the register injects
    /// fires.
    /// </summary>
    [Fact]
    public async Task ACaseReplaysFromItsSeedAndEachFaultFires()
    {
        List<SimulatedConditionalRegister> registers = [];
        var conformance = new SimulatedConformance();
        List<(ConformanceCase Case, string Schedule, int Trace)> runs = [];
        conformance.Observed = (testCase, run) => runs.Add((testCase, run.ScheduleId, run.Trace.Count));
        var kit = SimulatedKit(conformance, registers, unknownOutcomes: true);
        var pcg = new PCG(1, 1);
        var sequences = kit.Sequences(24);
        var groups = kit.ConcurrentSequences(10);
        for (var index = 0; index < 20; index++)
        {
            await kit.RunAsync((index % 2 == 0 ? sequences : groups).Generate(pcg, null, out _));
        }

        Assert.True(registers.Sum(static register => register.UnknownOutcomes) > 0, "No write's outcome was unknown.");
        Assert.True(registers.Sum(static register => register.DelayedLandings) > 0, "No write landed late.");
        Assert.True(registers.Sum(static register => register.AppliedMismatches) > 0, "No write applied and answered a mismatch.");

        var first = runs.ToList();
        runs.Clear();
        foreach (var (testCase, _, _) in first)
        {
            await kit.RunAsync(testCase);
        }

        Assert.Equal(first.Select(static run => (run.Schedule, run.Trace)), runs.Select(static run => (run.Schedule, run.Trace)));
    }

    /// <summary>
    /// Every concurrent group of Accordant's generation over a small input set, after every shorter sequence, conforms
    /// under every schedule of its window within the gate's bound: the register's requests and answers yield to the
    /// scheduler, so each interleaving of the group's landings and answers is a schedule the exploration runs.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = SimulatedKit(conformance, [], unknownOutcomes: false, yields: true);
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: false, appliedMismatch: false);

        var cases = spec.GenerateConcurrentTests(new ConditionalRegisterState(), Inputs(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// A register which checks a conditional write's version in one request and writes in a second
    /// (<see cref="SimulatedConditionalRegister.ChecksBeforeWritingMutant"/>) breaks MS-I1 when a concurrent write at the
    /// same version lands between them. The exploration of the group's window finds that schedule on every run, and its
    /// report names the schedule, which <c>DURABLE_STORAGE_SCHEDULE</c> replays; the seeded schedules of sampled cases meet it only
    /// by chance, which the test measures.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAConditionCheckedBeforeItsWrite()
    {
        ConformanceCase witness = new(
        [
            new([new(ConditionalRegisterSpec.Write, new RegisterWrite(null, 1))]),
            new([new(ConditionalRegisterSpec.Write, new RegisterWrite(1, 2)), new(ConditionalRegisterSpec.Write, new RegisterWrite(1, 3))]),
        ]);

        var explored = new SimulatedConformance(ScheduleBound.Gate);
        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(
            () => SimulatedKit(explored, [], unknownOutcomes: false, yields: true, mutant: true).RunAsync(witness));
        Assert.Contains("The spec does not allow round 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(WindowScheduleExplorer.ScheduleVariable + "=", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(explored.Explorations);

        // The default schedule runs each call on until it waits and passes; the failing schedule preempts one.
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);
        TestDiagnostics.WriteLine(explored.Summary());

        // The same case under its seeded schedule, one simulation per seed, catches the mutant only on some seeds.
        const int Seeds = 40;
        var caught = 0;
        var sampled = new SimulatedConformance();
        var sampledKit = SimulatedKit(sampled, [], unknownOutcomes: false, yields: true, mutant: true);
        for (var seed = 1; seed <= Seeds; seed++)
        {
            try
            {
                await sampledKit.RunAsync(witness with { Seed = seed });
            }
            catch (ConformanceScheduleException)
            {
                caught++;
            }
        }

        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The check-then-write register mutant: explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
        Assert.InRange(caught, 1, Seeds);
    }

    /// <summary>
    /// Every concurrent group of Accordant's generation over <see cref="FileSystemMetaStore"/> on the in-memory recorded
    /// file system (<see cref="RecordingFileSystem"/> with <see cref="RecordingFileSystem.YieldsAtIo"/>) conforms under
    /// every schedule of its window within the bound.
    /// </summary>
    [Fact]
    public async Task FileSystemMetaStoreEveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: false, appliedMismatch: false);
        var kit = FileSystemKit(conformance);

        var cases = spec.GenerateConcurrentTests(new ConditionalRegisterState(), Inputs(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// CsCheck's sequences and concurrent sequences over <see cref="FileSystemMetaStore"/> on the in-memory recorded file
    /// system conform, each case under its seeded schedule.
    /// </summary>
    [Fact]
    public async Task FileSystemMetaStoreSequencesConformUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = FileSystemKit(conformance);
        await kit.SampleAsync(kit.Sequences(20), Iterations, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), Iterations, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// Returns the input set of the explored windows: racing creates, racing conditional updates of the created version,
    /// and a read.
    /// </summary>
    private static InputSet Inputs(Microsoft.Accordant.Spec<ConditionalRegisterState> spec)
    {
        var inputs = new InputSet();
        var write = spec.GetOperation<RegisterWrite, RegisterOutcome>(ConditionalRegisterSpec.Write);
        var read = spec.GetOperation<RegisterRead, RegisterOutcome>(ConditionalRegisterSpec.Read);
        foreach (var request in new RegisterWrite[] { new(null, 1), new(null, 2), new(1, 3), new(1, 4) })
        {
            inputs.Add(write.With(request, request.ToString()));
        }

        inputs.Add(read.With(RegisterRead.Instance, RegisterRead.Instance.ToString()));
        return inputs;
    }

    private static ConformanceKit<ConditionalRegisterState, SimulatedTarget> SimulatedKit(
        SimulatedConformance conformance,
        List<SimulatedConditionalRegister> registers,
        bool unknownOutcomes,
        bool yields = false,
        bool mutant = false,
        Action<object, RegisterOutcome>? onOutcome = null) =>
        RegisterOperations(
            new ConformanceKit<ConditionalRegisterState, SimulatedTarget>(
                ConditionalRegisterSpec.Create(unknownOutcomes, appliedMismatch: unknownOutcomes),
                static () => new ConditionalRegisterState(),
                seed =>
                {
                    var register = new SimulatedConditionalRegister(conformance.Environment, FormattableString.Invariant($"register-{seed}"))
                    {
                        UnknownOutcomeRate = unknownOutcomes ? 0.2 : 0,
                        AppliedMismatchRate = unknownOutcomes ? 0.15 : 0,
                        ReadFailureRate = unknownOutcomes ? 0.1 : 0,
                        YieldsForLatency = yields,
                        ChecksBeforeWritingMutant = mutant,
                    };
                    registers.Add(register);
                    return Task.FromResult(new SimulatedTarget(register));
                },
                ConditionalRegisterSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ConditionalRegisterSpec.Conflicts,
                scheduler: conformance),
            onOutcome is null
                ? static (target, request) => target.RunAsync(request)
                : async (target, request) =>
                {
                    var outcome = await target.RunAsync(request);
                    onOutcome(request, outcome);
                    return outcome;
                });

    private static ConformanceKit<ConditionalRegisterState, FileSystemTarget> FileSystemKit(SimulatedConformance conformance) =>
        RegisterOperations(
            new ConformanceKit<ConditionalRegisterState, FileSystemTarget>(
                ConditionalRegisterSpec.Create(unknownOutcomes: false, appliedMismatch: false),
                static () => new ConditionalRegisterState(),
                async _ =>
                {
                    var store = new FileSystemMetaStore(Root, SimulationEnvironment.Clock, new RecordingFileSystem(Root) { YieldsAtIo = true });
                    var register = await store.GetRegisterAsync(Cluster, RegisterName, CancellationToken.None);
                    return new FileSystemTarget(register);
                },
                ConditionalRegisterSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ConditionalRegisterSpec.Conflicts,
                scheduler: conformance),
            static (target, request) => target.RunAsync(request));

    private static ConformanceKit<ConditionalRegisterState, TTarget> RegisterOperations<TTarget>(
        ConformanceKit<ConditionalRegisterState, TTarget> kit,
        Func<TTarget, object, Task<RegisterOutcome>> run)
        where TTarget : IAsyncDisposable
    {
        var values = Gen.Byte[1, 16];
        Task<RegisterOutcome> Run<TRequest>(TTarget target, TRequest request)
            where TRequest : notnull =>
            run(target, request);

        return kit
            .Operation(
                ConditionalRegisterSpec.Write,
                Gen.Select(
                    Gen.Frequency((1, Gen.Const((byte?)null)), (16, values.Select(static value => (byte?)value))),
                    values,
                    static (expected, value) => new RegisterWrite(expected, value)),
                Run,
                weight: 3)
            .Operation(
                ConditionalRegisterSpec.Read,
                Gen.Const(RegisterRead.Instance),
                Run,
                weight: 2);
    }

    /// <summary>
    /// One simulated-register case: a request the register fails with an <see cref="IOException"/> or a
    /// <see cref="TimeoutException"/> answers nothing, so its outcome is unknown.
    /// </summary>
    private sealed class SimulatedTarget(SimulatedConditionalRegister register) : IAsyncDisposable
    {
        private readonly ConditionalRegisterExecutor _executor = new(register);

        public async Task<RegisterOutcome> RunAsync(object request)
        {
            try
            {
                return request switch
                {
                    RegisterWrite write => await _executor.WriteAsync(write, CancellationToken.None),
                    RegisterRead => await _executor.ReadAsync(CancellationToken.None),
                    _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unknown register request."),
                };
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                return RegisterOutcome.Unknown;
            }
        }

        public ValueTask DisposeAsync() => register.DisposeAsync();
    }

    /// <summary>
    /// One file-system MetaStore case, over a recorded file system of its own.
    /// </summary>
    private sealed class FileSystemTarget(IConditionalRegister register) : IAsyncDisposable
    {
        private readonly ConditionalRegisterExecutor _executor = new(register);

        public Task<RegisterOutcome> RunAsync(object request) => request switch
        {
            RegisterWrite write => _executor.WriteAsync(write, CancellationToken.None),
            RegisterRead => _executor.ReadAsync(CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unknown register request."),
        };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
