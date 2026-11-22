using System.Collections.Concurrent;
using System.Reflection;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;
using Polly;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Runs <see cref="Ledger"/> and <see cref="LedgerWriter"/> over three LedgerStore replicas on simulated nodes, reached
/// across Clockwork's network through <see cref="SimulatedLedgerReplicaTransport"/>. The build rewrites Clog's assemblies and the kit under
/// Clockwork's controlled rule sets, so their tasks, awaits, locks, and system clock run on the simulation's logical
/// thread and virtual time; the seed decides every interleaving, network delay, lost or duplicated message, partition,
/// pause, crash, and recovery backoff.
/// </summary>
public sealed class QuorumSimulationTests
{
    private const int Writes = 24;
    private const int Quorum = 2;
    private const string RewriteSignature = "Clockwork.RewriteSignature";
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly string[] ReplicaEventKinds = ["call", "served", "answer", "handled"];

    /// <summary>
    /// The simulation build deploys Clog's assemblies and the kit rewritten, and leaves ordinary IL in the test assembly,
    /// which starts each simulation, and in the fatal-error handler's assembly, whose interceptor the test host installs
    /// (eng/OrleansSimulation.targets' exclusion).
    /// </summary>
    [Fact]
    public void TheBuildRewritesTheStorageAndTheKitButNotTheTests()
    {
        Assert.True(IsRewritten(typeof(Ledger).Assembly));
        Assert.True(IsRewritten(typeof(ResiliencePipeline).Assembly));
        Assert.True(IsRewritten(typeof(ArcBuffer).Assembly));
        Assert.True(IsRewritten(typeof(SimulationEnvironment).Assembly));
        Assert.False(IsRewritten(typeof(QuorumSimulationTests).Assembly));
        Assert.False(IsRewritten(typeof(FatalErrorHandler).Assembly));
    }

    /// <summary>
    /// The rewritten assemblies whose debug assertions the simulations evaluate build in the tests' configuration, so the
    /// Debug leg runs them in Debug (verification §12).
    /// </summary>
    [Fact]
    public void TheRewrittenCodeWhichHoldsDebugAssertionsBuildsInTheTestsConfiguration() =>
        Assert.All(
            [typeof(Ledger).Assembly, typeof(ArcBufferWriter).Assembly],
            static assembly => Assert.Equal(TestDiagnostics.Configuration, assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration));

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(1, 4);

    // The theory and the pinned trace share seed 1's run, so it runs once, unless the run exports its ledger trace.
    private static readonly ConcurrentDictionary<int, Lazy<ClusterSimulationRun>> Runs = new();

    /// <summary>
    /// A writer appending through <see cref="LedgerWriter"/> races a recovery executor with a <see cref="Ledger"/> of its
    /// own, while seal checks and reads run, across lost and duplicated messages, a partitioned replica, a paused one,
    /// and nodes which crash while they serve the recovery's seal and while the recovery runs, and restart; the writer
    /// waits out the faults with its production backoff. After every step the ledger trace checks that no
    /// replica answers a writer write written after it answered sealed (LR-I8), that the contiguous tails replicas
    /// report stay within the writer's completed prefix and never decrease (LR-I6), that the writer never sees a
    /// recovery's end written, so every address it saw written with every lower one lies below every end, and that each
    /// end lies within <c>[c, c + 2K]</c> (LP-I7), and that a seal check begun after a recovery completed reports sealed
    /// (LP-I9). Every position below each end reads back the writer's value (LP-I7, LR-I5), and once the faults heal a
    /// recovery completes within <see cref="RecoveryLiveness"/>'s bound (ledger-protocol §6.5).
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void AWriterRacingRecoveryLosesNoAcknowledgedWrite(int seed) => _ = RunSeed(seed);

    /// <summary>
    /// Across the corpus, a node crashes while it serves the first recovery's seal and one while that recovery runs, and
    /// each restarts, so the race's checks and its ledger trace cover a seal or a recovery write whose record a crash
    /// left in flight (docs/spec/verification.md §5.1).
    /// </summary>
    [Fact]
    public void TheCorpusCrashesNodesWhileTheyServeASealAndWhileARecoveryRuns()
    {
        string[] trace = [.. SeededSimulation.CorpusSeeds(1, 4).SelectMany(static seed => RunSeed(seed).Trace)];
        Assert.Contains(trace, static entry => entry.Contains("while it serves recovery-1's seal", StringComparison.Ordinal));
        Assert.Contains(trace, static entry => entry.Contains("while recovery-1 runs", StringComparison.Ordinal));
        Assert.Contains(trace, static entry => entry.Contains("after its crash during recovery-1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The recovery race replays from its seed and from its artifact, so the writer's jittered write backoff and
    /// recovery's, which repeats the replica requests whose deadlines passed, are deterministic under Clockwork; the run
    /// repeats such requests, and leaves replica operations running past the operations which started them.
    /// </summary>
    /// <remarks>
    /// Seed 21's first recovery starts while the writer's first window is in flight and the faults last, and repeats
    /// requests the network lost; the writer meets its seal, and the second recovery, once the faults heal, finds the
    /// first's end.
    /// </remarks>
    [Fact]
    public void TheRecoveryRaceReplaysFromItsSeed()
    {
        var run = SeededSimulation.RunAndReplay(21, environment => SimulateAsync(environment));

        var counters = Assert.Single(run.Trace, static entry => entry.Contains(" recovery repetitions ", StringComparison.Ordinal));
        Assert.Matches(@" recovery repetitions [1-9]\d*, tracked stragglers [1-9]\d*$", counters);
    }

    /// <summary>
    /// One seed's trace, steps, and schedule are pinned, so anything which changes a seeded run across processes, such as
    /// hash randomization, state shared between tests, or a Clockwork update which reorders the schedule, changes the
    /// snapshot. The schedule's identity covers its decisions alone, so a build or platform which runs the same schedule
    /// leaves it unchanged. The ledger trace's client steps are pinned as recorded, and its replica events, which the
    /// schedule's identity orders, by their count per kind.
    /// </summary>
    /// <returns>A task which completes once the snapshot is verified.</returns>
    [Fact]
    public Task OneSeedsTraceAndScheduleArePinned()
    {
        // A run which exports its trace logs each restarted node's slot states in its restart events, and reads any
        // checkpointed leaf they cover before the node serves, which can change the schedule, so with traces enabled the
        // pinned run is one of its own, which exports none.
        var run = LedgerTraces.Enabled ? SeededSimulation.Run(1, static environment => SimulateAsync(environment, exports: false)) : RunSeed(1);
        var replicaEvents = run.Trace.Where(IsReplicaEvent).ToArray();
        List<string> lines =
        [
            Invariant($"steps {run.Steps}"),
            Invariant($"schedule {run.ScheduleId} ({run.Artifact.Decisions.Count} decisions)"),
            Invariant($"outcome {run.Artifact.Outcome.Kind} {run.Artifact.Outcome.FailureIdentity}").TrimEnd(),
            .. ReplicaEventKinds.Select(kind => Invariant($"ledger {kind} events {replicaEvents.Count(entry => entry.Contains(Invariant($" ledger {kind} "), StringComparison.Ordinal))}")),
            .. run.Trace.Where(static entry => !IsReplicaEvent(entry)),
        ];
        return Verify(string.Join('\n', lines));
    }

    /// <summary>
    /// Seed 1's ledger trace, rendered as the module trace validation checks, is pinned, so that a change to how the
    /// export orders a node's answers, derives its deliveries and landed writes, or shapes its records shows as a reviewed
    /// diff (docs/spec/verification.md §5.1).
    /// </summary>
    /// <returns>A task which completes once the snapshot is verified.</returns>
    [Fact]
    public Task OneSeedsLedgerTraceModuleIsPinned()
    {
        string? module = null;
        _ = SeededSimulation.Run(1, environment => SimulateAsync(environment, rendered => module = rendered));
        return Verify(module);
    }

    private static ClusterSimulationRun RunSeed(int seed) =>
        Runs.GetOrAdd(seed, static seed => new Lazy<ClusterSimulationRun>(() => SeededSimulation.Run(seed, static environment => SimulateAsync(environment)))).Value;

    private static bool IsReplicaEvent(string entry) =>
        ReplicaEventKinds.Any(kind => entry.Contains(Invariant($" ledger {kind} "), StringComparison.Ordinal));

    private static bool IsRewritten(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(static attribute => attribute.Key == RewriteSignature);

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Runs the recovery race and hands its ledger trace, rendered, to <paramref name="rendered"/>; with
    /// <paramref name="exports"/>, it also exports the trace when <see cref="LedgerTraces.Enabled"/>.
    /// </summary>
    private static async Task SimulateAsync(SimulationEnvironment environment, Action<string>? rendered = null, bool exports = true)
    {
        // The scenario's calls carry a cancellable token, as production callers' do, which nothing cancels: the scenario
        // owns it, so no thread outside the simulation cancels it (docs/spec/verification.md §7.1). CancellationToken.None
        // would skip the code under test's registrations on it, and change the seeds' schedules.
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var network = environment.Network;
        network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        network.MaxJitter = TimeSpan.FromMilliseconds(4);
        network.MessageDropRate = 0.05;
        environment.Rpc.DuplicationRate = 0.05;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, cancellationToken);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        var exported = exports && LedgerTraces.Enabled;
        replicas.LedgerTrace.LogsRecoveredReplicas = exported || rendered is not null;
        _ = LedgerStoreInvariants.Register(environment, replicas);

        // Each client has its own Ledger over the same replicas, on the virtual clock, as separate processes would.
        var writerQuorum = replicas.OpenQuorum(SimulatedStorage.Ledger, Options, Quorum, "writer");
        await using var writerLedger = replicas.OpenLedger(SimulatedStorage.Ledger, writerQuorum);
        await using var writer = writerLedger.CreateWriter(SimulationEnvironment.Clock);
        replicas.LedgerTrace.TraceWriter(writer, "writer", SimulatedStorage.Ledger);
        await using var checker = replicas.OpenLedger(SimulatedStorage.Ledger, Options, Quorum, "checker");
        await using var reader = replicas.OpenLedger(SimulatedStorage.Ledger, Options, Quorum, "reader");
        await new RecoveryRace(environment, replicas, writer, checker, reader, SimulatedStorage.Ledger, Options, Quorum, Writes, SimulatedStorage.Payload, cancellationToken).RunAsync();
        environment.Trace.Record(Invariant($"recovery repetitions {Repetitions(replicas.LedgerTrace)}, tracked stragglers {writerQuorum.TrackedStragglers}"));
        if (exported)
        {
            LedgerTraces.Export(environment, "quorum", replicas.LedgerTrace, SimulatedStorage.Ledger, nodes, Quorum, Options.WindowSize);
        }

        rendered?.Invoke(LedgerTraces.Render(replicas.LedgerTrace, SimulatedStorage.Ledger, nodes, Quorum, Options.WindowSize));
    }

    /// <summary>
    /// Counts the requests a recovery sent again after their deadline passed: its backoff's repetitions.
    /// </summary>
    private static int Repetitions(LedgerTrace ledger)
    {
        var events = ledger.Events;
        var calls = events.Where(static entry => entry.Kind == LedgerEventKind.Called).ToDictionary(static entry => entry.Call!.Value);
        return events.Count(entry =>
            entry.Kind == LedgerEventKind.Answered
            && entry.Client.StartsWith("recovery", StringComparison.Ordinal)
            && entry.Answer == nameof(TimeoutException)
            && calls[entry.Call!.Value] is var missed
            && events.Skip(checked((int)entry.Sequence)).Any(later =>
                later.Kind == LedgerEventKind.Called
                && later.Client == missed.Client
                && later.Node == missed.Node
                && later.Operation == missed.Operation
                && later.Address == missed.Address));
    }
}
