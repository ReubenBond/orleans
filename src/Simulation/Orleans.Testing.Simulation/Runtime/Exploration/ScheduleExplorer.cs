using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Exploration;

/// <summary>Exploration stop reason.</summary>
public enum ExplorationTerminationReason
{
    /// <summary>Every configured iteration completed.</summary>
    IterationLimit,

    /// <summary>The configured failure count was reached.</summary>
    FailureLimit,

    /// <summary>The wall-clock safety bound was reached between deterministic iterations.</summary>
    TimeLimit,

    /// <summary>Exploration cancellation was requested.</summary>
    Canceled,
}

/// <summary>Configures bounded serial schedule exploration.</summary>
public sealed record ScheduleExplorationOptions
{
    /// <summary>Gets the simulation seed held constant across all iterations.</summary>
    public required int SimulationSeed { get; init; }

    /// <summary>Gets the first explicit schedule seed. Later seeds are deterministic increments.</summary>
    public required int FirstScheduleSeed { get; init; }

    /// <summary>Gets the maximum number of iterations.</summary>
    public int MaxIterations { get; init; } = 100;

    /// <summary>Gets the maximum controlled steps per iteration.</summary>
    public int MaxStepsPerIteration { get; init; } = 1_000_000;

    /// <summary>Gets the maximum number of non-success outcomes to collect.</summary>
    public int MaxFailures { get; init; } = 1;

    /// <summary>
    /// Gets the simulated processor count every iteration's runtime uses (see
    /// <see cref="ReplayRecordingOptions.ProcessorCount"/>). Defaults to 4.
    /// </summary>
    public int ProcessorCount { get; init; } = Shims.SimulationRuntimeEnvironment.DefaultProcessorCount;

    /// <summary>
    /// Gets an optional wall-clock safety bound. It is checked only between iterations, so an
    /// iteration and its artifact are never truncated by this bound.
    /// </summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Gets the instrumentation closure identity, when applicable.</summary>
    public ReplayInstrumentationIdentity? Instrumentation { get; init; }
}

/// <summary>One deterministic exploration iteration.</summary>
public sealed record ScheduleExplorationIteration
{
    /// <summary>Gets the zero-based iteration index.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the stable iteration identity.</summary>
    public required string IterationId { get; init; }

    /// <summary>Gets the schedule seed varied for this iteration.</summary>
    public required int ScheduleSeed { get; init; }

    /// <summary>Gets the execution result and replay artifact.</summary>
    public required ReplayExecutionResult Execution { get; init; }

    /// <summary>Gets whether this iteration produced a non-success outcome.</summary>
    public bool IsFailure => Execution.Artifact.Outcome.Kind != ReplayTerminationKind.Completed;
}

/// <summary>Aggregate results from bounded schedule exploration.</summary>
public sealed record ScheduleExplorationResult
{
    /// <summary>Gets why exploration stopped.</summary>
    public required ExplorationTerminationReason TerminationReason { get; init; }

    /// <summary>Gets completed iterations in deterministic index order.</summary>
    public required IReadOnlyList<ScheduleExplorationIteration> Iterations { get; init; }

    /// <summary>Gets outcome counts in enum order.</summary>
    public required IReadOnlyDictionary<ReplayTerminationKind, int> OutcomeCounts { get; init; }

    /// <summary>
    /// Gets the smallest retained artifact for each stable failure category/identity. Size is compared
    /// by decision count, then race-point count, then artifact id.
    /// </summary>
    public required IReadOnlyDictionary<string, ReplayExecutionResult> RetainedFailures { get; init; }

    /// <summary>Gets the number of non-success outcomes observed.</summary>
    public int FailureCount => Iterations.Count(static iteration => iteration.IsFailure);
}

/// <summary>Runs bounded serial exploration while keeping model/application seeds fixed.</summary>
public static class ScheduleExplorer
{
    /// <summary>Explores schedule seeds serially with explicit cancellation.</summary>
    public static ScheduleExplorationResult Explore(
        ScheduleExplorationOptions configuration,
        Action<SimulationScheduler> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scenario);
        return ExploreCore(
            configuration,
            (recording, iterationCancellation) =>
                ReplayRunner.Record(recording, scenario, iterationCancellation),
            cancellationToken);
    }

    /// <summary>
    /// Explores schedule seeds serially using one fresh <see cref="SimulationCluster"/> per iteration.
    /// The scenario is a synchronous entry, as for
    /// <see cref="ReplayRunner.RecordCluster(ReplayRecordingOptions, Action{SimulationCluster}, CancellationToken)"/>.
    /// </summary>
    public static ScheduleExplorationResult ExploreCluster(
        ScheduleExplorationOptions configuration,
        Action<SimulationCluster> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scenario);
        return ExploreCore(
            configuration,
            (recording, iterationCancellation) =>
                ReplayRunner.RecordCluster(recording, scenario, iterationCancellation),
            cancellationToken);
    }

    /// <summary>
    /// Explores schedule seeds serially using one fresh <see cref="SimulationCluster"/> per iteration,
    /// recording each run with <see cref="ReplayRunner.RecordClusterEntry"/>: the scenario returns the
    /// run's entry task. Replay a retained artifact with <see cref="ReplayRunner.ReplayClusterEntry"/>.
    /// </summary>
    public static ScheduleExplorationResult ExploreClusterEntry(
        ScheduleExplorationOptions configuration,
        Func<SimulationCluster, Task> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scenario);
        return ExploreCore(
            configuration,
            (recording, iterationCancellation) =>
                ReplayRunner.RecordClusterEntry(recording, scenario, iterationCancellation),
            cancellationToken);
    }

    private static ScheduleExplorationResult ExploreCore(
        ScheduleExplorationOptions configuration,
        Func<ReplayRecordingOptions, CancellationToken, ReplayExecutionResult> record,
        CancellationToken cancellationToken)
    {
        Validate(configuration);
        var stopwatch = Stopwatch.StartNew();
        var iterations = new List<ScheduleExplorationIteration>(Math.Min(configuration.MaxIterations, 1_024));
        var counts = new SortedDictionary<ReplayTerminationKind, int>();
        var retained = new SortedDictionary<string, ReplayExecutionResult>(StringComparer.Ordinal);
        ExplorationTerminationReason terminationReason = ExplorationTerminationReason.IterationLimit;
        var failureCount = 0;

        for (var index = 0; index < configuration.MaxIterations; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                terminationReason = ExplorationTerminationReason.Canceled;
                break;
            }

            if (configuration.TimeLimit is { } timeLimit && stopwatch.Elapsed >= timeLimit)
            {
                terminationReason = ExplorationTerminationReason.TimeLimit;
                break;
            }

            int scheduleSeed = DeriveScheduleSeed(configuration.FirstScheduleSeed, index);
            ReplayExecutionResult execution;
            try
            {
                execution = record(
                    new ReplayRecordingOptions
                    {
                        SimulationSeed = configuration.SimulationSeed,
                        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
                        ScheduleSeed = scheduleSeed,
                        MaxSteps = configuration.MaxStepsPerIteration,
                        ProcessorCount = configuration.ProcessorCount,
                        Instrumentation = configuration.Instrumentation,
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                terminationReason = ExplorationTerminationReason.Canceled;
                break;
            }

            var iteration = new ScheduleExplorationIteration
            {
                Index = index,
                IterationId = CreateIterationId(configuration.SimulationSeed, scheduleSeed, index),
                ScheduleSeed = scheduleSeed,
                Execution = execution,
            };
            iterations.Add(iteration);
            counts[execution.Artifact.Outcome.Kind] =
                counts.GetValueOrDefault(execution.Artifact.Outcome.Kind) + 1;

            if (!iteration.IsFailure)
            {
                continue;
            }

            failureCount++;
            RetainSmallest(retained, execution);
            if (failureCount >= configuration.MaxFailures)
            {
                terminationReason = ExplorationTerminationReason.FailureLimit;
                break;
            }
        }

        return new ScheduleExplorationResult
        {
            TerminationReason = terminationReason,
            Iterations = iterations,
            OutcomeCounts = counts,
            RetainedFailures = retained,
        };
    }

    /// <summary>Derives the schedule seed for a zero-based iteration.</summary>
    public static int DeriveScheduleSeed(int firstScheduleSeed, int iteration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(iteration);
        return unchecked(firstScheduleSeed + iteration);
    }

    /// <summary>Creates a stable iteration identity from simulation seed, schedule seed, and index.</summary>
    public static string CreateIterationId(int simulationSeed, int scheduleSeed, int iteration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(iteration);
        string input = string.Create(
            CultureInfo.InvariantCulture,
            $"{simulationSeed}:{scheduleSeed}:{iteration}");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..12];
        return string.Create(CultureInfo.InvariantCulture, $"iteration-{iteration:D6}-{hash}");
    }

    private static void RetainSmallest(
        SortedDictionary<string, ReplayExecutionResult> retained,
        ReplayExecutionResult candidate)
    {
        ReplayOutcome outcome = candidate.Artifact.Outcome;
        string key = $"{outcome.Kind}:{outcome.FailureIdentity ?? "<none>"}";
        if (!retained.TryGetValue(key, out ReplayExecutionResult? current) ||
            Compare(candidate, current) < 0)
        {
            retained[key] = candidate;
        }
    }

    private static int Compare(ReplayExecutionResult left, ReplayExecutionResult right)
    {
        int byDecisions = left.Artifact.Decisions.Count.CompareTo(right.Artifact.Decisions.Count);
        if (byDecisions != 0)
        {
            return byDecisions;
        }

        int byRacePoints = RaceSchedulingPointCount(left.Artifact).CompareTo(RaceSchedulingPointCount(right.Artifact));
        return byRacePoints != 0
            ? byRacePoints
            : StringComparer.Ordinal.Compare(left.ArtifactId, right.ArtifactId);
    }

    // An artifact retains only the most recent points, whose sequence numbers count every point the run reached.
    private static long RaceSchedulingPointCount(ReplayArtifact artifact) =>
        artifact.RaceSchedulingPoints.Count == 0 ? 0 : artifact.RaceSchedulingPoints[^1].Sequence;

    private static void Validate(ScheduleExplorationOptions configuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxStepsPerIteration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxFailures);
        if (configuration.TimeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "TimeLimit must be positive when specified.");
        }
    }
}
