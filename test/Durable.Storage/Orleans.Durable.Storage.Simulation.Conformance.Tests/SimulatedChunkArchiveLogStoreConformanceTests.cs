using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The Accordant <see cref="IArchiveLogStore"/> spec (archive-logs.md §7.4, §10) over <see cref="ChunkArchiveLogStore"/>
/// and <see cref="SimulatedObjectStore"/>, each case in a simulation of its own (verification.md §4.1): CsCheck's
/// sequences and concurrent groups with earlier writes replayed under injected object-store faults (unknown outcomes,
/// delayed landings, retried answers, and read failures), and Accordant's concurrent windows explored under
/// <see cref="WindowScheduleExplorer"/>.
/// </summary>
public sealed class SimulatedChunkArchiveLogStoreConformanceTests
{
    private const int Iterations = 20;

    /// <summary>
    /// The stride of the explored windows at the gate: each chunk-layout write lists, creates, and lists again, so
    /// exploring every group of Accordant's generation takes over two minutes, which <c>CLOG_SCHEDULE_BOUND=deep</c> runs.
    /// </summary>
    private const int GateStride = 8;

    [Fact]
    public async Task TheChunkStoreConformsUnderTheSimulationsSchedule()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(conformance, []);
        await kit.SampleAsync(kit.Sequences(20).WithReplays(), Iterations, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(8).WithReplays(), Iterations, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// A case replays from its seed alone, and the simulated object store's faults fire: unknown outcomes, delayed
    /// landings, and retried answers.
    /// </summary>
    [Fact]
    public async Task ACaseReplaysFromItsSeedAndItsFaultsFire()
    {
        List<Target> targets = [];
        var conformance = new SimulatedConformance();
        List<(ConformanceCase Case, string Schedule, int Trace)> runs = [];
        conformance.Observed = (testCase, run) => runs.Add((testCase, run.ScheduleId, run.Trace.Count));
        var kit = Kit(conformance, targets);
        var pcg = new PCG(1, 1);
        var sequences = kit.Sequences(20).WithReplays();
        var groups = kit.ConcurrentSequences(8).WithReplays();
        for (var index = 0; index < 30; index++)
        {
            await kit.RunAsync((index % 2 == 0 ? sequences : groups).Generate(pcg, null, out _));
        }

        Assert.True(targets.Sum(static target => target.Store.UnknownOutcomes) > 0, "No write's outcome was unknown.");
        Assert.True(targets.Sum(static target => target.Store.DelayedLandings) > 0, "No write landed late.");
        Assert.True(targets.Sum(static target => target.Store.RetriedAnswers) > 0, "No retried write met its own landing.");
        Assert.True(targets.Sum(static target => target.Unknown) > 0, "No call's outcome was unknown.");

        var first = runs.ToList();
        runs.Clear();
        foreach (var (testCase, _, _) in first)
        {
            await kit.RunAsync(testCase);
        }

        Assert.Equal(first.Select(static run => (run.Schedule, run.Trace)), runs.Select(static run => (run.Schedule, run.Trace)));
    }

    /// <summary>
    /// Every concurrent group of Accordant's generation over the kit's input set, after every shorter sequence, conforms:
    /// every <see cref="GateStride"/>th group of racing writes under every schedule of its window within the gate's bound,
    /// and the rest under their seeded schedules, or every group explored under <c>CLOG_SCHEDULE_BOUND=deep</c>. The
    /// simulated object store yields to the scheduler in place of latency, so each interleaving of the group's listings,
    /// reads, and conditional creates is a schedule the exploration runs.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Kit(conformance, [], explored: true);
        var sampled = new SimulatedConformance();
        var sampledKit = Kit(sampled, [], explored: true);
        var cases = ConcurrentCases(kit).ToList();

        var racing = cases.Where((testCase, index) => ScheduleBound.IsDeep || RacesWrites(testCase) && index % GateStride == 0).ToList();
        await kit.RunAsync(racing);
        await sampledKit.RunAsync(cases.Except(racing));

        TestDiagnostics.WriteLine(conformance.Summary());
        TestDiagnostics.WriteLine(sampled.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// An object store which evaluates a conditional create's condition in one step and writes unconditionally in a second
    /// (<see cref="SimulatedObjectStore.ChecksBeforeWritingMutant"/>) lets two racing appends or openers at one position
    /// both create their object and both answer <see cref="ArchiveLogWriteOutcome.Applied"/>. The exploration of the
    /// racing-writes windows finds a preempted schedule the spec does not allow while the default schedule passes.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAConditionCheckedBeforeItsWrite()
    {
        var explored = new SimulatedConformance(ScheduleBound.Gate);
        var kit = Kit(explored, [], explored: true, mutant: true);
        var racing = ConcurrentCases(kit).Where(RacesWrites).ToList();
        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(() => kit.RunAsync(racing));
        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
        Assert.Contains(WindowScheduleExplorer.ScheduleVariable + "=", failure.Message, StringComparison.Ordinal);
        var found = explored.Explorations[^1];
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        var sampled = new SimulatedConformance();
        var sampledKit = Kit(sampled, [], explored: true, mutant: true);
        var caught = 0;
        foreach (var testCase in racing)
        {
            try
            {
                await sampledKit.RunAsync([testCase]);
            }
            catch (ConformanceScheduleException)
            {
                caught++;
            }
        }

        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The check-then-write mutant: explored, caught on case {explored.Cases} of {racing.Count} at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {racing.Count} cases."));
    }

    private static ConformanceKit<ArchiveLogStoreState, Target> Kit(
        SimulatedConformance conformance,
        List<Target> targets,
        bool explored = false,
        bool mutant = false) =>
        new ConformanceKit<ArchiveLogStoreState, Target>(
                explored ? ExploredSpec() : ArchiveLogStoreSpec.Create(unknownOutcomes: true, staleReads: true, chunkLayout: true),
                static () => new ArchiveLogStoreState(),
                seed =>
                {
                    var target = new Target(conformance.Environment, FormattableString.Invariant($"chunk-logs-{seed}"), explored, mutant);
                    targets.Add(target);
                    return Task.FromResult(target);
                },
                ArchiveLogStoreSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ArchiveLogStoreSpec.Conflicts,
                scheduler: conformance)
            .ArchiveLogStoreOperations(static (target, request) => target.RunAsync(request));

    /// <summary>
    /// Returns the spec of the explored windows, whose requests never fail: a read which a write overlapped may answer
    /// <c>Stale</c>, a run's units land apart, and a chunk-layout write whose post-create listing sees a concurrent newer
    /// generation answers <c>ConditionFailed</c> even though its own object landed (archive-logs.md §7.4).
    /// </summary>
    private static Microsoft.Accordant.Spec<ArchiveLogStoreState> ExploredSpec() =>
        ArchiveLogStoreSpec.Create(unknownOutcomes: false, staleReads: true, retriesMeetLandings: true, interleavedWrites: true, chunkLayout: true);

    private static IEnumerable<ConcurrentTestCase> ConcurrentCases(ConformanceKit<ArchiveLogStoreState, Target> kit)
    {
        var spec = ExploredSpec();
        var cases = spec.GenerateConcurrentTests(new ArchiveLogStoreState(), ArchiveLogStoreKit.InputSet(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        return cases;
    }

    private static bool RacesWrites(ConcurrentTestCase testCase) =>
        testCase.Segments.Any(static segment =>
            segment.OperationCalls.Count(static call => call.OperationInput.Request is LogAppend or LogOpen or LogDeleteBase) >= 2);

    /// <summary>
    /// One case's <see cref="SimulatedObjectStore"/> and <see cref="ChunkArchiveLogStore"/>, injecting object-store faults
    /// on seeded runs or yielding to the scheduler on explored runs.
    /// </summary>
    private sealed class Target : IAsyncDisposable
    {
        private readonly ArchiveLogStoreExecutor _executor;
        private int _unknown;
        private int _writing;
        private int _writes;

        public Target(SimulationEnvironment environment, string site, bool explored, bool mutant)
        {
            Store = new SimulatedObjectStore(environment, site)
            {
                UnknownOutcomeRate = explored ? 0 : 0.3,
                RetriedAnswerRate = explored ? 0 : 0.15,
                ReadFailureRate = explored ? 0 : 0.1,
                MaxLatency = TimeSpan.FromMilliseconds(10),
                MaxLandingDelay = TimeSpan.FromMilliseconds(20),
                YieldsForLatency = explored,
                ChecksBeforeWritingMutant = mutant,
            };
            _executor = new ArchiveLogStoreExecutor(new ChunkArchiveLogStore(Store));
        }

        public SimulatedObjectStore Store { get; }

        public int Unknown => _unknown;

        /// <summary>Runs a call; a read which a write overlapped and which answers <c>Stale</c> reports it as racing.</summary>
        public async Task<LogOutcome> RunAsync(object request)
        {
            var write = request is LogAppend or LogOpen or LogDeleteBase;
            var (writing, writes, unknown) = (Volatile.Read(ref _writing), Volatile.Read(ref _writes), Volatile.Read(ref _unknown));
            if (write)
            {
                _ = Interlocked.Increment(ref _writing);
                _ = Interlocked.Increment(ref _writes);
            }

            try
            {
                var outcome = await _executor.RunAsync(request, CancellationToken.None);
                var overlapped = writing > 0 || unknown > 0 || Volatile.Read(ref _writes) != writes;
                return outcome.Kind == LogOutcomeKind.Stale && overlapped ? new LogOutcome(LogOutcomeKind.StaleRacing) : outcome;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                _ = Interlocked.Increment(ref _unknown);
                return LogOutcome.Unknown;
            }
            finally
            {
                if (write)
                {
                    _ = Interlocked.Decrement(ref _writing);
                }
            }
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}
