using Azure;
using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The Accordant <see cref="IArchiveLogStore"/> spec (archive-logs.md §10) over <see cref="AzureArchiveLogStore"/> through
/// the real SDK and the fake Azure transport, which the simulation schedules: each request waits a seeded virtual delay,
/// so concurrent writers and readers interleave as the seed chooses and replay with it, while the transport fails requests
/// before and after they land, lands some after their own retry or after their call gave up, fails a server after a write
/// landed, and breaks a read's body, and the SDK's retries meet their own landing; and racing writers of one key's chain
/// (<see cref="ArchiveLogRaces"/>) under the same schedule.
/// </summary>
/// <remarks>
/// Each case runs in a simulation of its own, seeded by the case (<see cref="SimulatedConformance"/>): sequences with
/// earlier writes replayed and concurrent groups, whose calls start as operations of its scheduler, so CsCheck shrinks a
/// failing case's operations, faults, and interleaving together. The simulation's driver checks at teardown that no page
/// is leaked and no work outlives the case; <c>just explore</c> sweeps the races' seeds beyond the corpus at milestones.
/// </remarks>
public sealed class SimulatedAzureArchiveLogStoreConformanceTests
{
    private const int Iterations = 20;

    /// <summary>
    /// The stride of the explored windows at the gate: the SDK's pipeline gives each window about fifty choice points, so
    /// exploring each of Accordant's 166 groups takes about a minute, which the nightly window runs.
    /// </summary>
    private const int GateStride = 8;

    public static TheoryData<int> Seeds => SeededSimulation.Corpus(1, 2);

    private const int RacesPerRun = 30;

    private static readonly FakeAzureFault[] TransportFaults =
        [FakeAzureFault.FailBefore, FakeAzureFault.FailAfter, FakeAzureFault.DeliverLater, FakeAzureFault.ServerErrorAfterLanding, FakeAzureFault.BreakMidBody];

    [Fact]
    public async Task TheAzureStoreConformsUnderTheSimulationsSchedule()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(conformance, []);
        await kit.SampleAsync(kit.Sequences(20).WithReplays(), Iterations, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(8).WithReplays(), Iterations, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// Racing writers of a key's chain hold it under the simulation's schedule, with every fault the transport injects. The
    /// races are drawn from the seed in the test host, where CsCheck runs (verification.md §7.1).
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RacingWritersHoldTheChainUnderTheSimulationsSchedule(int seed)
    {
        var pcg = new PCG((uint)seed, (ulong)seed);
        var races = Enumerable.Range(0, RacesPerRun).Select(_ => ArchiveLogRaces.Cases.Generate(pcg, null, out Size _)).ToList();
        SeededSimulation.Run(seed, async environment =>
        {
            foreach (var race in races)
            {
                await ArchiveLogRaces.CheckAsync(race, (seed, run) => RaceAsync(environment, seed, run), static writer => writer(), SimulationEnvironment.Clock);
            }
        });
    }

    /// <summary>A case replays from its seed alone, and the transport's faults fire, a retry meeting its own landing among them.</summary>
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
        for (var index = 0; index < 10; index++)
        {
            await kit.RunAsync((index % 2 == 0 ? sequences : groups).Generate(pcg, null, out _));
        }

        Assert.True(targets.Sum(static target => target.Faults) > 0, "No request failed.");
        Assert.True(targets.Sum(static target => target.Unknown) > 0, "No call's outcome was unknown.");

        var first = runs.ToList();
        runs.Clear();
        foreach (var (testCase, _, _) in first)
        {
            await kit.RunAsync(testCase);
        }

        Assert.Equal(first.Select(static run => (run.Schedule, run.Trace)), runs.Select(static run => (run.Schedule, run.Trace)));
    }

    private static ConformanceKit<ArchiveLogStoreState, Target> Kit(SimulatedConformance conformance, List<Target> targets, bool explored = false, bool mutant = false) =>
        new ConformanceKit<ArchiveLogStoreState, Target>(
                explored ? ExploredSpec() : ArchiveLogStoreSpec.Create(unknownOutcomes: true, staleReads: true),
                static () => new ArchiveLogStoreState(),
                seed =>
                {
                    var target = new Target(conformance.Environment, FormattableString.Invariant($"logs-{seed}"), explored, mutant);
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
    /// <c>Stale</c>, and a run's units land apart.
    /// </summary>
    private static Microsoft.Accordant.Spec<ArchiveLogStoreState> ExploredSpec() =>
        ArchiveLogStoreSpec.Create(unknownOutcomes: false, staleReads: true, retriesMeetLandings: false, interleavedWrites: true);

    /// <summary>
    /// Every concurrent group of Accordant's generation over the kit's input set, after every shorter sequence, conforms:
    /// every <see cref="GateStride"/>th group of racing writes under every schedule of its window within the gate's bound,
    /// and the rest under their seeded schedules, or every group explored under <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c>, which
    /// takes about a minute. The fake transport yields to the scheduler before each request, so each interleaving of the
    /// group's requests through the real SDK is a schedule.
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
    /// A service which evaluates an <c>Append Block</c>'s conditions before a wait and appends after it
    /// (<see cref="FakeAzureBlobService.ChecksAppendConditionsBeforeWritingMutant"/>) appends a racing writer's unit after
    /// another's at one position. The exploration of the windows of racing writes finds a schedule the spec does not
    /// allow, which preempts the default; under the seeded schedule of each case, one simulation per case, the same
    /// windows meet it only on some cases, which the test measures.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAnAppendConditionCheckedBeforeItsWrite()
    {
        var explored = new SimulatedConformance(ScheduleBound.Gate);
        var kit = Kit(explored, [], explored: true, mutant: true);
        var racing = ConcurrentCases(kit).Where(RacesWrites).ToList();
        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(() => kit.RunAsync(racing));
        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
        var found = explored.Explorations[^1];
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
            $"The append-condition mutant: explored, caught on case {explored.Cases} of {racing.Count} at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {racing.Count} cases."));
    }

    /// <summary>
    /// Returns Accordant's concurrent cases over the kit's input set: a group of the set's requests after every shorter
    /// sequence.
    /// </summary>
    private static IEnumerable<ConcurrentTestCase> ConcurrentCases(ConformanceKit<ArchiveLogStoreState, Target> kit)
    {
        var spec = ExploredSpec();
        var cases = spec.GenerateConcurrentTests(new ArchiveLogStoreState(), ArchiveLogStoreKit.InputSet(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        return cases;
    }

    /// <summary>
    /// Returns whether a case holds a group of two or more writes, whose conditions race.
    /// </summary>
    private static bool RacesWrites(ConcurrentTestCase testCase) =>
        testCase.Segments.Any(static segment =>
            segment.OperationCalls.Count(static call => call.OperationInput.Request is LogAppend or LogOpen or LogDeleteBase) >= 2);

    private static async Task RaceAsync(SimulationEnvironment environment, int seed, Func<ArchiveLogRaces.RaceStore, Task> run)
    {
        var random = environment.CreateRandom(FormattableString.Invariant($"race-{seed}"));
        using var service = new FakeAzureBlobService(
            faults: _ => ArchiveLogRaces.IsChecking || random.NextDouble() >= 0.2 ? FakeAzureFault.None : TransportFaults[random.Next(TransportFaults.Length)],
            schedule: async cancellationToken => await Task.Delay(TimeSpan.FromMilliseconds(random.Next(1, 20)), SimulationEnvironment.Clock, cancellationToken),
            checkInvariants: true);
        var layout = new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 3, 100, 50, AzureArchiveLogMetadata.NameRule);
        var store = new AzureArchiveLogStore(service.Client(random.Next(4) == 0 ? 0 : 3), "json", layout);
        await run(new(
            store,
            generation => service.Records.Count(record => record.Operation == FakeAzureOperation.CreateAppendBlob && record.Status == 201
                && record.Headers.GetValueOrDefault("x-ms-meta-wal_generation") == generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            service.DeliverDeferred));
        Assert.True(service.Violations.Count == 0, string.Join(Environment.NewLine, service.Violations));
    }

    /// <summary>
    /// One case's fake service and store, whose requests wait seeded virtual delays and sometimes fail; or, when explored,
    /// never fail and yield to the scheduler in place of each delay, so every interleaving of concurrent requests is a
    /// scheduling choice an exploration enumerates; with the mutant, the service's
    /// <see cref="FakeAzureBlobService.ChecksAppendConditionsBeforeWritingMutant"/> is set.
    /// </summary>
    private sealed class Target : IAsyncDisposable
    {
        private readonly FakeAzureBlobService _service;
        private readonly ArchiveLogStoreExecutor _executor;
        private int _faults;
        private int _unknown;
        private int _writing;
        private int _writes;

        public Target(SimulationEnvironment environment, string site, bool explored = false, bool mutant = false)
        {
            var random = environment.CreateRandom(site);
            _service = new FakeAzureBlobService(
                faults: request =>
                {
                    var draw = random.NextDouble();
                    if (explored || draw >= 0.3)
                    {
                        return FakeAzureFault.None;
                    }

                    _ = Interlocked.Increment(ref _faults);
                    return TransportFaults[random.Next(TransportFaults.Length)];
                },
                schedule: explored
                    ? static _ => SimulationTasks.Yield()
                    : async cancellationToken => await Task.Delay(TimeSpan.FromMilliseconds(random.Next(1, 20)), SimulationEnvironment.Clock, cancellationToken),
                checkInvariants: true)
            {
                ChecksAppendConditionsBeforeWritingMutant = mutant,
            };
            var layout = new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 100, 50, AzureArchiveLogMetadata.NameRule);
            _executor = new ArchiveLogStoreExecutor(new AzureArchiveLogStore(_service.Client(explored || random.Next(4) != 0 ? 3 : 0), "json", layout));
        }

        public int Faults => _faults;

        public int Unknown => _unknown;

        /// <summary>Runs a call; a read which a write overlapped and which answers <c>Stale</c> reports it as racing.</summary>
        public async Task<LogOutcome> RunAsync(object request)
        {
            var write = request is LogAppend or LogOpen or LogDeleteBase;
            var (writing, writes, deferred) = (Volatile.Read(ref _writing), Volatile.Read(ref _writes), _service.HasDeferred);
            if (write)
            {
                _ = Interlocked.Increment(ref _writing);
                _ = Interlocked.Increment(ref _writes);
            }

            try
            {
                var outcome = await _executor.RunAsync(request, CancellationToken.None);
                var overlapped = writing > 0 || deferred || Volatile.Read(ref _writes) != writes;
                return outcome.Kind == LogOutcomeKind.Stale && overlapped ? new LogOutcome(LogOutcomeKind.StaleRacing) : outcome;
            }
            catch (Exception exception) when (ArchiveLogRaces.IsTransportFailure(exception))
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

        public ValueTask DisposeAsync()
        {
            var violations = _service.Violations;
            _service.Dispose();
            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
            return ValueTask.CompletedTask;
        }
    }
}
