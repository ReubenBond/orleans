using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Orleans.Durable.Storage.Simulation;
using Orleans.Testing.Simulation;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Testing;
using Xunit.Sdk;

namespace Orleans.Durable.Storage.Testing;

/// <content>
/// Runs scenarios in Clockwork clusters: the seed decides every schedule, network, and fault choice, every invariant is
/// checked after every step, the run tears its components down and checks what they leave, and a failure leaves
/// Clockwork's replay artifact in the results directory (docs/spec/verification.md §7.4).
/// </content>
/// <remarks>
/// <para>
/// <c>eng/OrleansSimulation.targets</c> links this file into simulation projects only. It runs in the test host, outside every
/// simulation, so it keeps ordinary IL: it drives the cluster from the test thread, and the scenario it starts on the
/// cluster's scheduler lane resumes there after each <c>await</c>. The driver observes the test's cancellation token
/// between steps. A scenario passes rewritten code only tokens it owns: the test host cancels its own token on a thread
/// of its own, which would run there, outside the simulation, the callbacks rewritten code registered on it.
/// </para>
/// <para>
/// A step is one operation Clockwork's scheduler dispatches. After each step the driver runs the environment's
/// <see cref="InvariantChecks"/> and observes the run. Once the scenario succeeds, the driver runs
/// <see cref="SimulationEnvironment.TeardownAsync"/>, then the work due at that instant without advancing virtual time,
/// then the environment's teardown checks, which include that nothing remains to run and that every page the scenario
/// rented returned. A failed debug assertion, a violated invariant or teardown check, a failed scenario or teardown, a
/// run which can make no progress, and a run past its budgets fail it: the driver records the failure in the trace and
/// raises it on the cluster's lane, so Clockwork records the run as faulted, with the failure's type as its identity.
/// Work which reaches the simulation from a thread outside its scheduler fails it too: Clockwork's simulation-thread
/// check reports it to the driver, and Clockwork records the run as aborted, since that work ran outside the schedule.
/// </para>
/// <para>
/// The driver records and replays through Clockwork's synchronous entry (<see cref="ReplayRunner.RecordCluster"/>),
/// whose callback is the driver's own step loop: the entry-task form would hand the stepping to Clockwork's runner,
/// which runs no checks between steps. A synchronous entry's run is faulted when an operation faults, aborted when work
/// reaches the simulation from a thread outside its scheduler, and otherwise completed; the scenario's own outcome is
/// the driver's to judge.
/// </para>
/// <para>
/// <c>CLOG_TEST_SEED</c> replaces the seed; <c>CLOG_SIM_SEEDS</c> replaces a corpus theory's seeds;
/// <c>ORLEANS_SIMULATION_REPLAY_ARTIFACT</c> replays an artifact's exact schedule instead of recording one; and
/// <c>CLOG_SIM_MINIMIZE</c> minimizes a failure's schedule.
/// </para>
/// </remarks>
internal static partial class SeededSimulation
{
    /// <summary>
    /// The environment variable which replaces a corpus theory's seeds: a seed, an inclusive range <c>first..last</c>,
    /// or a comma-separated list of either.
    /// </summary>
    internal const string SeedsVariable = "CLOG_SIM_SEEDS";

    /// <summary>
    /// The environment variable which, set to <c>1</c> or <c>true</c>, minimizes a failed run's schedule.
    /// </summary>
    internal const string MinimizeVariable = "CLOG_SIM_MINIMIZE";

    /// <summary>
    /// The name of the teardown check that no work outlives the simulation's components (§11.1).
    /// </summary>
    internal const string QuiescenceCheck = "nothing outlives the simulation's components (§11.1)";

    /// <summary>
    /// The name of the teardown check that every page the scenario rented returned (BUF-I1, BUF-I2).
    /// </summary>
    internal const string PageCheck = "every ArcBuffer page the scenario rented returned (BUF-I1, BUF-I2)";

    /// <summary>
    /// The most steps one run may take before it fails as stalled.
    /// </summary>
    internal const int MaxSteps = 200_000;

    /// <summary>
    /// The most consecutive steps one run may take without virtual time advancing before it fails as livelocked: work
    /// which never waits keeps every timer from firing.
    /// </summary>
    internal const int MaxStepsAtOneInstant = 10_000;

    private const int ReportedTraceEntries = 40;

    // A candidate schedule which runs this many steps beyond the failing run's is a different run.
    private const int CandidateStepMargin = 64;

    /// <summary>
    /// The most virtual time one run may take before it fails as stalled.
    /// </summary>
    internal static readonly TimeSpan MaxVirtualTime = TimeSpan.FromMinutes(5);

    private static readonly ReplayMinimizationOptions Minimization = new()
    {
        MaxAttempts = 200,
        TimeLimit = TimeSpan.FromMinutes(2),
    };

    private enum Phase
    {
        Scenario,
        Teardown,
        Quiescing,
    }

    /// <summary>
    /// Gets the test's cancellation token, which the driver observes between steps, outside the simulation (§7.1).
    /// </summary>
    private static CancellationToken TestCancellationToken
    {
        get
        {
#pragma warning disable RS0030 // The driver runs in the test host and never passes the token to rewritten code.
            return TestContext.Current.CancellationToken;
#pragma warning restore RS0030
        }
    }

    /// <summary>
    /// Returns the seeds of a corpus theory: <paramref name="count"/> seeds from <paramref name="first"/>, or the seeds
    /// of <c>CLOG_SIM_SEEDS</c> when it is set, for seed exploration and for replaying one row.
    /// </summary>
    /// <param name="first">The corpus's first seed.</param>
    /// <param name="count">The corpus's size.</param>
    /// <returns>The seeds.</returns>
    internal static TheoryData<int> Corpus(int first, int count) => [.. CorpusSeeds(first, count)];

    /// <summary>
    /// Returns the seeds <see cref="Corpus"/> gives as a theory's data, for a corpus-wide check over the same seeds.
    /// </summary>
    /// <param name="first">The corpus's first seed.</param>
    /// <param name="count">The corpus's size.</param>
    /// <returns>The seeds.</returns>
    internal static IEnumerable<int> CorpusSeeds(int first, int count) =>
        Environment.GetEnvironmentVariable(SeedsVariable) is { Length: > 0 } value
            ? ParseSeeds(value)
            : Enumerable.Range(first, count);

    /// <summary>
    /// Runs <paramref name="scenario"/> in a Clockwork cluster seeded with <paramref name="seed"/>, or with
    /// <c>CLOG_TEST_SEED</c> when it is set, and fails with a report when the run fails.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <param name="scenario">The scenario, which builds its simulation in the environment it receives.</param>
    /// <param name="minimize">
    /// Whether a failure's schedule is minimized, or <see langword="null"/> to minimize when <c>CLOG_SIM_MINIMIZE</c> is
    /// set.
    /// </param>
    /// <returns>The successful run.</returns>
    /// <exception cref="SimulationFailedException">The run failed.</exception>
    internal static ClusterSimulationRun Run(int seed, Func<SimulationEnvironment, Task> scenario, bool? minimize = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return Execute(seed, scenario, minimize ?? MinimizationRequested());
    }

    /// <summary>
    /// Runs <paramref name="scenario"/> as <see cref="Run"/> does, then checks that it is deterministic: a second run
    /// from the seed alone, and an exact replay of the first run's artifact, record the same trace.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The first run.</returns>
    internal static ClusterSimulationRun RunAndReplay(int seed, Func<SimulationEnvironment, Task> scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return AssertReplays(Execute(seed, scenario, MinimizationRequested()), scenario);
    }

    /// <summary>
    /// Checks that <paramref name="first"/>, a run of <paramref name="scenario"/> which <see cref="Run"/> returned, is
    /// deterministic, as <see cref="RunAndReplay"/> does, so a corpus's run serves its replay check too.
    /// </summary>
    /// <param name="first">The run.</param>
    /// <param name="scenario">The scenario it ran.</param>
    /// <returns><paramref name="first"/>.</returns>
    internal static ClusterSimulationRun AssertReplays(ClusterSimulationRun first, Func<SimulationEnvironment, Task> scenario)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(scenario);
        var cancellationToken = TestCancellationToken;
        var second = Record(first.Seed, scenario, cancellationToken);
        AssertSameRun(first, second, "a second run from the seed");
        if (!string.Equals(first.ArtifactId, second.ArtifactId, StringComparison.Ordinal))
        {
            Assert.Fail(Invariant(
                $"Seed {first.Seed} recorded the same trace twice but different schedules: artifacts {first.ArtifactId} and {second.ArtifactId}."));
        }

        ClusterSimulationRun replay;
        try
        {
            replay = Replay(first.Artifact, scenario, MaxSteps, cancellationToken);
        }
        catch (Exception exception) when (exception is SimulationDecisionReplayMismatchException or ReplayOutcomeMismatchException)
        {
            throw new SimulationFailedException(Invariant($"Seed {first.Seed} did not replay its artifact: {exception.Message}"), exception);
        }

        AssertSameRun(first, replay, "the exact replay of its artifact");
        return first;
    }

    private static ClusterSimulationRun Execute(int seed, Func<SimulationEnvironment, Task> scenario, bool minimize)
    {
        var cancellationToken = TestCancellationToken;
        ClusterSimulationRun run;
        if (Environment.GetEnvironmentVariable(ReplayTestEnvironment.Artifact) is { Length: > 0 } path)
        {
            var artifact = ReplayArtifactSerializer.Read(path);
            TestDiagnostics.WriteLine(Invariant($"Replaying {path}, recorded from seed {artifact.SimulationSeed}."));
            run = Replay(artifact, scenario, MaxSteps, cancellationToken);
        }
        else
        {
            var resolved = Seed(seed);
            TestDiagnostics.WriteLine(resolved == seed
                ? Invariant($"Seed {resolved}; replay with {SeedVariable}={resolved}.")
                : Invariant($"Seed {resolved} from {SeedVariable}, replacing {seed}."));
            run = Record(resolved, scenario, cancellationToken);
        }

        return run.Failure is null ? run : throw Report(seed, run, scenario, minimize, cancellationToken);
    }

    private static bool MinimizationRequested() => IsSet(MinimizeVariable);

    private static bool IsSet(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { } value
        && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<int> ParseSeeds(string value)
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split("..", StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2
                || !int.TryParse(range[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var first)
                || !int.TryParse(range[^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var last)
                || last < first)
            {
                throw new FormatException(Invariant($"{SeedsVariable} holds '{value}'; expected seeds or ranges such as '7' or '1..500', separated by commas."));
            }

            for (var seed = first; seed <= last; seed++)
            {
                yield return seed;
            }
        }
    }

    private static ClusterSimulationRun Record(
        int seed,
        Func<SimulationEnvironment, Task> scenario,
        CancellationToken cancellationToken,
        Func<ISimulationSchedulingStrategy, ISimulationSchedulingStrategy>? strategy = null)
    {
        var stopwatch = Stopwatch.StartNew();
        return Log("Recorded", RecordQuietly(seed, scenario, cancellationToken, strategy), stopwatch);
    }

    private static ClusterSimulationRun RecordQuietly(
        int seed,
        Func<SimulationEnvironment, Task> scenario,
        CancellationToken cancellationToken,
        Func<ISimulationSchedulingStrategy, ISimulationSchedulingStrategy>? strategy)
    {
        using var execution = new ClusterExecution(scenario, MaxSteps, cancellationToken, strategy);
        var result = ReplayRunner.RecordCluster(
            new ReplayRecordingOptions
            {
                SimulationSeed = seed,
                SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
                MaxSteps = MaxSteps,
                IncludeDiagnosticMessages = true,
            },
            execution.Replica,
            cancellationToken);
        return execution.Complete(seed, result);
    }

    /// <summary>
    /// Runs one conformance case's scenario in a fresh cluster seeded with <paramref name="seed"/> exactly, with every
    /// check <see cref="Run"/> runs, and returns the run, failed or not, without writing evidence: the conformance kit
    /// runs many such cases while it shrinks and explores, and owns their replay (verification.md §4.1).
    /// </summary>
    /// <param name="seed">The case's seed, which no environment variable replaces.</param>
    /// <param name="scenario">The case's scenario.</param>
    /// <param name="strategy">
    /// Wraps the run's seeded scheduling strategy, such as a <see cref="WindowSchedulingStrategy"/> which explores the
    /// case's concurrent windows, or <see langword="null"/> to schedule by the seed alone.
    /// </param>
    /// <returns>The run.</returns>
    internal static ClusterSimulationRun RunCase(
        int seed,
        Func<SimulationEnvironment, Task> scenario,
        Func<ISimulationSchedulingStrategy, ISimulationSchedulingStrategy>? strategy = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return RecordQuietly(seed, scenario, TestCancellationToken, strategy);
    }

    private static ClusterSimulationRun Replay(ReplayArtifact artifact, Func<SimulationEnvironment, Task> scenario, int maxSteps, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        return Log("Replayed", ReplayQuietly(artifact, scenario, maxSteps, cancellationToken), stopwatch);
    }

    private static ClusterSimulationRun ReplayQuietly(ReplayArtifact artifact, Func<SimulationEnvironment, Task> scenario, int maxSteps, CancellationToken cancellationToken)
    {
        using var execution = new ClusterExecution(scenario, maxSteps, cancellationToken, strategy: null);
        var result = ReplayRunner.ReplayCluster(artifact, ReplayCompatibilityRequirements.Current(), execution.Replica, maxSteps, cancellationToken);
        return execution.Complete(artifact.SimulationSeed, result);
    }

    private static ClusterSimulationRun Log(string action, ClusterSimulationRun run, Stopwatch stopwatch)
    {
        TestDiagnostics.WriteLine(Invariant(
            $"{action} seed {run.Seed}: {run.Steps} steps, {run.Elapsed.TotalMilliseconds:0.###} virtual ms, {stopwatch.Elapsed.TotalMilliseconds:0} wall ms{(run.Failure is null ? string.Empty : ", failed")}."));
        return run;
    }

    /// <summary>
    /// Writes a failed run's evidence to the results directory and returns the exception which reports it.
    /// </summary>
    private static SimulationFailedException Report(int seed, ClusterSimulationRun run, Func<SimulationEnvironment, Task> scenario, bool minimize, CancellationToken cancellationToken)
    {
        var name = Invariant($".seed-{run.Seed}");
        var history = WriteHistory(run.Trace, name + ".history.log");
        var artifact = ResultPath(name + ".cwr.json");
        ReplayArtifactSerializer.Write(artifact, run.Artifact);
        var replay = ReplayVariables(seed, run.Seed);
        var test = DisplayNameCommand();
        var report = new StringBuilder()
            .AppendLine(Invariant($"Seed {run.Seed} failed after step {run.Steps}: {run.Failure!.GetType().Name}: {run.Failure.Message}"))
            .AppendLine("Replay the seed:")
            .AppendLine(Invariant($"  {string.Join(' ', replay.Select(static variable => Invariant($"{variable.Name}={variable.Value}")))} {test}"))
            .AppendLine(Invariant($"  {PowerShell(replay, test)}"));
        if (run.Artifact.Outcome.Kind == ReplayTerminationKind.Aborted)
        {
            _ = report.AppendLine("The recorded schedule does not replay: Clockwork aborted the run, since work ran outside it.");
        }
        else
        {
            _ = report
                .AppendLine("Replay the recorded schedule:")
                .AppendLine(Invariant($"  {PowerShell([.. replay, (ReplayTestEnvironment.Artifact, artifact)], test)}"));
        }

        _ = report
            .AppendLine(Invariant($"History: {history}"))
            .AppendLine(Invariant($"Replay artifact: {artifact} ({run.Artifact.Outcome.Kind} {run.Artifact.Outcome.FailureIdentity}, {run.Artifact.Decisions.Count} decisions)"));
        var minimized = minimize
            ? Minimize(run, scenario, name, cancellationToken)
            : (Invariant($"Not minimized: set {MinimizeVariable}=1 to minimize the schedule."), null, null);
        _ = report.AppendLine(minimized.Description);
        _ = report.AppendLine(Invariant($"Last {Math.Min(ReportedTraceEntries, run.Trace.Count)} of {run.Trace.Count} trace entries:"));
        foreach (var entry in run.Trace.Skip(Math.Max(0, run.Trace.Count - ReportedTraceEntries)))
        {
            _ = report.Append("  ").AppendLine(entry);
        }

        var message = report.ToString();
        TestDiagnostics.WriteLine(message);
        return new SimulationFailedException(message, run.Failure)
        {
            Seed = run.Seed,
            Steps = run.Steps,
            HistoryPath = history,
            ArtifactPath = artifact,
            MinimizedArtifactPath = minimized.Artifact,
            MinimizedTracePath = minimized.Trace,
        };
    }

    /// <summary>
    /// Returns the environment variables which make the failing row run with its seed alone: <c>CLOG_SIM_SEEDS</c> makes
    /// a corpus theory's row exist, and <c>CLOG_TEST_SEED</c> replaces a seed which differs from the row's.
    /// </summary>
    private static (string Name, string Value)[] ReplayVariables(int seed, int resolved) =>
        resolved == seed
            ? [(SeedsVariable, Invariant($"{seed}"))]
            : [(SeedsVariable, Invariant($"{seed}")), (SeedVariable, Invariant($"{resolved}"))];

    private static string PowerShell(IReadOnlyList<(string Name, string Value)> variables, string command) =>
        Invariant($"{string.Concat(variables.Select(static variable => Invariant($"$env:{variable.Name}='{variable.Value}'; ")))}{command}; {string.Join("; ", variables.Select(static variable => Invariant($"Remove-Item Env:{variable.Name}")))}");

    /// <summary>
    /// Shrinks a failed run's schedule with Clockwork's minimizer. A candidate
    /// counts only when its exact replay fails in at most the failing run's steps with the same failure, by type and by
    /// invariant name or message. The minimized artifact, Clockwork's rendering of it, and the history its replay records
    /// are written to the results directory.
    /// </summary>
    private static (string Description, string? Artifact, string? Trace) Minimize(
        ClusterSimulationRun run,
        Func<SimulationEnvironment, Task> scenario,
        string name,
        CancellationToken cancellationToken)
    {
        if (run.Failure is SimulationStalledException)
        {
            return ("Not minimized: a stalled run's schedule fails by running out, which any longer schedule reproduces.", null, null);
        }

        if (run.Artifact.Outcome.Kind is ReplayTerminationKind.Completed or ReplayTerminationKind.Aborted)
        {
            return (Invariant($"Not minimized: Clockwork recorded the run as {run.Artifact.Outcome.Kind}."), null, null);
        }

        var identity = FailureIdentity(run.Failure);
        var maxSteps = checked((int)run.Steps + CandidateStepMargin);
        ReplayMinimizationResult minimized;
        try
        {
            minimized = ReplayTraceMinimizer.Minimize(
                run.Artifact,
                Minimization,
                artifact => Observe(artifact, scenario, identity, maxSteps, cancellationToken),
                cancellationToken);
        }
        catch (ReplayMinimizationException exception)
        {
            return (Invariant($"Not minimized: {exception.Message}"), null, null);
        }

        if (minimized.MinimizedDecisionCount == minimized.OriginalDecisionCount)
        {
            return (Invariant($"Not minimized: no decision of {minimized.OriginalDecisionCount} could be removed in {minimized.Attempts} attempts."), null, null);
        }

        var artifactPath = ResultPath(name + ".min.cwr.json");
        ReplayArtifactSerializer.Write(artifactPath, minimized.MinimizedArtifact);
        var tracePath = ResultPath(name + ".min.trace.txt");
        File.WriteAllText(tracePath, ReplayTraceRenderer.RenderText(minimized.MinimizedArtifact));
        string history;
        try
        {
            history = WriteHistory(ReplayQuietly(minimized.MinimizedArtifact, scenario, maxSteps, cancellationToken).Trace, name + ".min.history.log");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            history = Invariant($"not replayed: {exception.Message}");
        }

        return (
            Invariant($"Minimized from {minimized.OriginalDecisionCount} to {minimized.MinimizedDecisionCount} decisions in {minimized.Attempts} attempts: {artifactPath}, trace {tracePath}, history {history}"),
            artifactPath,
            tracePath);
    }

    /// <summary>
    /// Returns what identifies a failure across candidate schedules: its type, with the invariant it violated, the site of
    /// the debug assertion it failed, or its message, so a candidate which fails differently does not count.
    /// </summary>
    private static string FailureIdentity(Exception? failure) => failure switch
    {
        null => string.Empty,
        InvariantViolationException { Violation: { } violation } => Invariant($"{failure.GetType().FullName}: {violation.Invariant}"),
        DebugAssertionException assertion => Invariant($"{failure.GetType().FullName}: {assertion.SiteKey}: {assertion.Message}"),
        SimulationOutcomeException outcome => Invariant($"{failure.GetType().FullName}: {outcome.Kind} {outcome.FailureIdentity}"),
        _ => Invariant($"{failure.GetType().FullName}: {failure.Message}"),
    };

    /// <summary>
    /// Replays a candidate schedule exactly, within the failing run's steps, and reports whether it fails as the
    /// original did.
    /// </summary>
    private static ReplayFailureObservation Observe(
        ReplayArtifact artifact,
        Func<SimulationEnvironment, Task> scenario,
        string identity,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidate = ReplayQuietly(artifact, scenario, maxSteps, cancellationToken);
            var outcome = candidate.Artifact.Outcome;
            var sameFailure = string.Equals(FailureIdentity(candidate.Failure), identity, StringComparison.Ordinal);
            return new ReplayFailureObservation
            {
                Reproduced = sameFailure && outcome.Kind is not (ReplayTerminationKind.Completed or ReplayTerminationKind.Aborted),
                Kind = outcome.Kind,
                FailureIdentity = outcome.FailureIdentity,
                RejectionReason = sameFailure ? null : "different-failure",
            };
        }
        catch (ReplayOutcomeMismatchException exception)
        {
            return new ReplayFailureObservation
            {
                Reproduced = false,
                Kind = exception.Actual.Kind,
                FailureIdentity = exception.Actual.FailureIdentity,
                RejectionReason = "outcome-mismatch",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A divergent, incompatible, or aborted candidate, including one whose cluster then failed to dispose with
            // work left over, does not reproduce the failure.
            return new ReplayFailureObservation { Reproduced = false, RejectionReason = exception.Message };
        }
    }

    private static void AssertSameRun(ClusterSimulationRun first, ClusterSimulationRun second, string description)
    {
        var length = Math.Min(first.Trace.Count, second.Trace.Count);
        var divergence = 0;
        while (divergence < length && string.Equals(first.Trace[divergence], second.Trace[divergence], StringComparison.Ordinal))
        {
            divergence++;
        }

        if (divergence == length && first.Trace.Count == second.Trace.Count && first.Steps == second.Steps)
        {
            return;
        }

        var firstPath = WriteHistory(first.Trace, Invariant($".seed-{first.Seed}.first.history.log"));
        var secondPath = WriteHistory(second.Trace, Invariant($".seed-{first.Seed}.replay.history.log"));
        Assert.Fail(Invariant(
            $"Seed {first.Seed} did not replay in {description}: after {first.Steps} and {second.Steps} steps, the traces diverge at entry {divergence} ('{Entry(first.Trace, divergence)}' then '{Entry(second.Trace, divergence)}'). Histories: {firstPath}, {secondPath}"));
    }

    private static string ResultPath(string suffix) => TestDiagnostics.PathFor(TestContext.Current.Test?.TestDisplayName ?? "simulation", suffix);

    /// <summary>
    /// One execution of a scenario in a fresh cluster, which <see cref="ReplayRunner"/> records or replays. Executions
    /// run one at a time (§7.1): one which starts while another runs fails.
    /// </summary>
    private sealed class ClusterExecution : IDisposable
    {
        private static ClusterExecution? _active;

        private readonly Func<SimulationEnvironment, Task> _scenarioFactory;
        private readonly int _maxSteps;
        private readonly CancellationToken _cancellationToken;
        private readonly Func<ISimulationSchedulingStrategy, ISimulationSchedulingStrategy>? _strategy;

        // The debug assertions this run owns: those its test raised from this count on, since Clockwork's lanes carry the
        // test's execution context (§11.2).
        private readonly ITest? _test = TestContext.Current.Test;
        private readonly long _assertions = DebugAssertions.RaisedCount;

        // The fatal errors this run owns: those its test reported from this count on (conventions.md §14).
        private readonly long _reports = FatalErrors.ReportedCount;

        // The assertion count when the run's failure was set: an assertion raised before it takes precedence.
        private long _assertionsAtFailure = long.MaxValue;
        private SimulationCluster? _cluster;
        private SimulationEnvironment? _environment;
        private ArcBufferLeakCheck? _pages;
        private Task? _scenario;
        private Task? _teardown;
        private Phase _phase;
        private TimeSpan _instant;
        private long _stepsAtInstant;

        public ClusterExecution(
            Func<SimulationEnvironment, Task> scenario,
            int maxSteps,
            CancellationToken cancellationToken,
            Func<ISimulationSchedulingStrategy, ISimulationSchedulingStrategy>? strategy)
        {
            if (Interlocked.CompareExchange(ref _active, this, null) is not null)
            {
                throw new InvalidOperationException(
                    "A simulation started while another was running; simulations run one at a time (docs/spec/verification.md §7.1).");
            }

            _scenarioFactory = scenario;
            _maxSteps = maxSteps;
            _cancellationToken = cancellationToken;
            _strategy = strategy;
        }

        public long Steps { get; private set; }

        public Exception? Failure { get; private set; }

        private SimulationCluster Cluster => _cluster!;

        private SimulationEnvironment Environment => _environment!;

        /// <summary>
        /// Starts the scenario on the cluster's lane and drives the cluster one step at a time through the scenario,
        /// its teardown, and the work due at the teardown's instant, until the run completes or fails.
        /// </summary>
        public void Replica(SimulationCluster cluster)
        {
            // The driver's thread is the simulation's while it builds the environment and steps the cluster, so the
            // rewritten code it calls passes Clockwork's simulation-thread check (§7.1). Clockwork's runner already holds
            // the scope on this thread; the driver holds it too, so the guarantee is its own.
            var scheduler = cluster.Scheduler;
            using var control = scheduler.EnterControlScope();
            if (_strategy is { } strategy)
            {
                // Before the first step, so the wrapped strategy makes every choice of the run.
                scheduler.SchedulingStrategy = strategy(scheduler.SchedulingStrategy);
            }

            _cluster = cluster;
            _environment = new SimulationEnvironment(cluster);
            Environment.AddTeardownCheck(QuiescenceCheck, () => SimulationLeakCheck.FindLeftovers(cluster, _cancellationToken));
            cluster.SchedulerLane.Enqueue(() =>
            {
                // Current in the scenario's execution context, the check counts the pages the scenario and the work it
                // starts rent, and runs after the quiescence check, once that work has ended.
                _pages = ArcBufferLeakCheck.StartUnasserted();
                Environment.AddTeardownCheck(PageCheck, _pages.DescribeLeaks);
                _scenario = _scenarioFactory(Environment);
            });
            try
            {
                while (true)
                {
                    var ran = scheduler.RunStep(_cancellationToken);
                    if (ran)
                    {
                        Steps++;
                        _stepsAtInstant = scheduler.VirtualTime == _instant ? _stepsAtInstant + 1 : 1;
                        _instant = scheduler.VirtualTime;
                    }

                    if (Failure is not null)
                    {
                        // The raised failure is runnable at once; a run which keeps running other work instead ends here.
                        if (!ran || _stepsAtInstant > MaxStepsAtOneInstant || Steps >= _maxSteps)
                        {
                            return;
                        }

                        continue;
                    }

                    var failure = FindFailure(ran);
                    if (failure is null)
                    {
                        if (Advance())
                        {
                            continue;
                        }

                        if (ran)
                        {
                            continue;
                        }

                        if (_phase == Phase.Quiescing)
                        {
                            // Nothing more is due at the teardown's instant.
                            if (Check(Environment.TeardownChecks) is not { } violation)
                            {
                                return;
                            }

                            failure = new InvariantViolationException(violation);
                        }
                        else if (TryAdvanceVirtualTime(scheduler))
                        {
                            continue;
                        }
                        else
                        {
                            // A raised failure is runnable, so only a stalled run gets here.
                            failure = new SimulationStalledException(Invariant(
                                $"The {_phase.ToString().ToLowerInvariant()} stalled after step {Steps}: nothing is runnable and no timer is pending. {scheduler.DescribeLiveness()}"));
                        }
                    }

                    Fail(failure);
                }
            }
            catch (Exception exception) when (exception is not SimulationThreadAffinityException && (Failure is null || ReferenceEquals(exception, Failure)))
            {
                // The raised failure faulted its operation, or a callback of the scenario's failed and faulted its own.
                // Work which reached the simulation from a thread outside its scheduler ran outside the recorded
                // schedule, so the filter passes its violation on, and Clockwork aborts the run (§7.4).
                if (Failure is null)
                {
                    SetFailure(exception);
                }
            }
        }

        public ClusterSimulationRun Complete(int seed, ReplayExecutionResult result)
        {
            var outcome = result.Artifact.Outcome;

            // The run reports its debug assertions, including one whose exception escaped the step which raised it, and
            // one raised before its failure was set, which takes precedence and carries that failure (§11.2).
            var assertion = DebugAssertions.TakeRaisedBy(_test, _assertions).Find(record => record.Sequence < _assertionsAtFailure);
            var failure = assertion?.WithContext(Failure) ?? Failure ?? result.ExecutionException;

            // A report the steps left untaken, as the last step's, is judged as every step's is.
            if (_environment is not null && TakeFatalErrors() is { } unjudged)
            {
                failure ??= unjudged;
            }

            if (failure is null && outcome.Kind != ReplayTerminationKind.Completed)
            {
                failure = new SimulationOutcomeException(outcome.Kind.ToString(), outcome.FailureIdentity, outcome.Diagnostic);
            }

            return new ClusterSimulationRun(
                seed,
                Steps,
                _environment?.Trace.Elapsed ?? TimeSpan.Zero,
                [.. _environment?.Trace.Entries ?? []],
                result.Artifact,
                result.ArtifactId,
                ScheduleId(result.Artifact),
                failure);
        }

        /// <summary>
        /// Hashes a run's decisions and outcome alone. An artifact's identity also covers the rewritten assemblies and
        /// the machine, so it changes with every build of the code under test and on every platform.
        /// </summary>
        private static string ScheduleId(ReplayArtifact artifact)
        {
            var schedule = new StringBuilder();
            foreach (var decision in artifact.Decisions)
            {
                _ = schedule.Append(CultureInfo.InvariantCulture, $"{decision.Domain}|{decision.Kind}|{decision.SourceId}|{decision.InputMetadata}|{decision.SelectedResult}|{decision.NodeId}\n");
            }

            _ = schedule.Append(CultureInfo.InvariantCulture, $"{artifact.Outcome.Kind}|{artifact.Outcome.FailureIdentity}");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schedule.ToString())));
        }

        public void Dispose()
        {
            // A run which ended without completing, such as a minimization candidate which diverged, leaves its records
            // to no other owner.
            _ = DebugAssertions.TakeRaisedBy(_test, _assertions);
            _ = FatalErrors.TakeReportedBy(_test, _reports);
            _pages?.Dispose();
            _ = Interlocked.CompareExchange(ref _active, null, this);
        }

        private static bool TryAdvanceVirtualTime(SimulationScheduler scheduler)
        {
            var time = scheduler.VirtualTime;
            return scheduler.TryAdvanceVirtualTime() || scheduler.VirtualTime != time;
        }

        /// <summary>
        /// Returns why the run fails after a step: a failed debug assertion, which the code under test may have caught,
        /// then a fatal error no fault injection caused, then a violated invariant, then a failed
        /// scenario or teardown, then a livelock, then an exhausted budget.
        /// </summary>
        private Exception? FindFailure(bool ran)
        {
            if (DebugAssertions.FirstRaisedBy(_test, _assertions) is { } assertion)
            {
                return assertion;
            }

            if (TakeFatalErrors() is { } unexpected)
            {
                return unexpected;
            }

            if (ran && Check(Environment.Invariants) is { } violation)
            {
                return new InvariantViolationException(violation);
            }

            if ((_phase == Phase.Scenario ? _scenario : _teardown) is { IsCompleted: true, IsCompletedSuccessfully: false } task)
            {
                return task.Exception?.InnerException
                    ?? new OperationCanceledException(Invariant($"The {_phase.ToString().ToLowerInvariant()} was cancelled."));
            }

            if (_stepsAtInstant > MaxStepsAtOneInstant)
            {
                return new SimulationStalledException(Invariant(
                    $"The {_phase.ToString().ToLowerInvariant()} livelocked: {_stepsAtInstant} steps ran at {Environment.Trace.Elapsed.TotalMilliseconds:0.###} ms without virtual time advancing, so no timer can fire."));
            }

            return Steps >= _maxSteps || Environment.Trace.Elapsed > MaxVirtualTime
                ? new SimulationStalledException(Invariant(
                    $"The {_phase.ToString().ToLowerInvariant()} exceeded its budget of {_maxSteps} steps or {MaxVirtualTime.TotalMinutes} virtual minutes: {Steps} steps, {Environment.Trace.Elapsed.TotalMinutes:0.###} minutes."))
                : null;
        }

        /// <summary>
        /// Runs <paramref name="checks"/> in the order they were added and returns the first violation, or
        /// <see langword="null"/> when every invariant holds.
        /// </summary>
        /// <remarks>
        /// A check which throws cannot vouch for its invariant, so its exception is its violation, and a
        /// <see cref="SimulationThreadAffinityException"/> propagates, so Clockwork aborts the run (§7.4). The driver
        /// catches a check's exception in its own ordinary IL: Clockwork guards every broad or filtered catch in
        /// rewritten code, the kit's included, with a check which requires the simulation's context, and the driver's
        /// thread carries none.
        /// </remarks>
        private InvariantViolation? Check(InvariantChecks checks)
        {
            foreach (var (name, check) in checks.Checks)
            {
                string? description;
                try
                {
                    description = check();
                }
                catch (Exception exception) when (exception is not SimulationThreadAffinityException)
                {
                    description = Invariant($"the check threw {exception.GetType().Name}: {exception.Message}");
                }

                if (description is not null)
                {
                    return new InvariantViolation(name, description, Steps, Environment.Trace.Elapsed);
                }
            }

            return null;
        }

        /// <summary>
        /// Takes the fatal errors the run reported during the step. A failed local storage operation which a node's fault
        /// injection caused stopped the process of the node whose root holds its path, which the node crashes and restarts
        /// (verification.md §7.4). Any other report fails the run: a defect, a failure no node's root holds, or a failure
        /// no injection caused.
        /// </summary>
        private InvariantViolationException? TakeFatalErrors()
        {
            foreach (var report in FatalErrors.TakeReportedBy(_test, _reports))
            {
                if (report.Failure.IsDefect)
                {
                    return new InvariantViolationException(Invariant($"Step {Steps} reported a defect: {report.Failure.Describe()}"));
                }

                if (report.Path is null || Environment.StorageRootOf(report.Path) is not { } root)
                {
                    return new InvariantViolationException(Invariant(
                        $"Step {Steps} reported a local storage failure outside every node's root: {report.Failure.Describe()}"));
                }

                if (!Environment.WasInjected(root, report.Failure))
                {
                    return new InvariantViolationException(Invariant(
                        $"Step {Steps} reported a local storage failure no fault injection caused: {report.Failure.Describe()}"));
                }

                Environment.Trace.Record(Invariant($"{root} reported a failed {report.Site}: its process stops"));
            }

            return null;
        }

        /// <summary>
        /// Moves to the next phase once the current one's task succeeded, returning whether it moved.
        /// </summary>
        private bool Advance()
        {
            if (_phase == Phase.Scenario && _scenario is { IsCompletedSuccessfully: true })
            {
                _phase = Phase.Teardown;
                Cluster.SchedulerLane.Enqueue(() => _teardown = Environment.TeardownAsync());
                return true;
            }

            if (_phase == Phase.Teardown && _teardown is { IsCompletedSuccessfully: true })
            {
                _phase = Phase.Quiescing;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Raises <paramref name="failure"/> on the cluster's lane, so the operation which runs it faults with it.
        /// </summary>
        private void Fail(Exception failure)
        {
            SetFailure(failure);
            Environment.Trace.Record(Invariant($"step {Steps}: {failure.GetType().Name}: {failure.Message}"));
            Cluster.SchedulerLane.Enqueue(() => ExceptionDispatchInfo.Throw(failure));
        }

        private void SetFailure(Exception failure)
        {
            Failure = failure;
            _assertionsAtFailure = DebugAssertions.RaisedCount;
        }
    }
}

/// <summary>
/// One run of a scenario in a Clockwork cluster.
/// </summary>
/// <param name="Seed">The seed which decided the run.</param>
/// <param name="Steps">The steps the run took.</param>
/// <param name="Elapsed">The virtual time the run took.</param>
/// <param name="Trace">The trace the run recorded.</param>
/// <param name="Artifact">Clockwork's replay artifact of the run.</param>
/// <param name="ArtifactId">The artifact's identity: a hash of its schedule, outcome, rewritten assemblies, and machine.</param>
/// <param name="ScheduleId">A hash of the run's decisions and outcome alone, the same for every build and platform.</param>
/// <param name="Failure">Why the run failed, or <see langword="null"/>.</param>
internal sealed record ClusterSimulationRun(
    int Seed,
    long Steps,
    TimeSpan Elapsed,
    IReadOnlyList<string> Trace,
    ReplayArtifact Artifact,
    string ArtifactId,
    string ScheduleId,
    Exception? Failure);

/// <summary>
/// Fails a test whose seeded Clockwork simulation failed, with the report and the paths of its evidence.
/// </summary>
public sealed class SimulationFailedException : Exception
{
    public SimulationFailedException()
    {
    }

    public SimulationFailedException(string message)
        : base(message)
    {
    }

    public SimulationFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public int Seed { get; init; }

    public long Steps { get; init; }

    public string? HistoryPath { get; init; }

    public string? ArtifactPath { get; init; }

    public string? MinimizedArtifactPath { get; init; }

    public string? MinimizedTracePath { get; init; }
}
