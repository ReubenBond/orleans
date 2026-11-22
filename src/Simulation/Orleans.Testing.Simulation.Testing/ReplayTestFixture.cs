using System.Globalization;
using System.Text;
using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Testing;

/// <summary>Environment variable names understood by replay-aware test fixtures.</summary>
public static class ReplayTestEnvironment
{
    /// <summary>Path to a complete artifact which should be replayed instead of recording a new run.</summary>
    public const string Artifact = "ORLEANS_SIMULATION_REPLAY_ARTIFACT";

    /// <summary>Optional simulation seed override.</summary>
    public const string SimulationSeed = "ORLEANS_SIMULATION_SEED";

    /// <summary>Optional schedule seed override.</summary>
    public const string ScheduleSeed = "ORLEANS_SIMULATION_SCHEDULE_SEED";

    /// <summary>Optional directory for failed-test artifacts.</summary>
    public const string ArtifactDirectory = "ORLEANS_SIMULATION_ARTIFACT_DIRECTORY";

    /// <summary>Optional maximum exploration iteration count.</summary>
    public const string ExplorationIterations = "ORLEANS_SIMULATION_EXPLORATION_ITERATIONS";

    /// <summary>Optional exploration wall-clock safety limit as an invariant <see cref="TimeSpan"/>.</summary>
    public const string ExplorationTimeLimit = "ORLEANS_SIMULATION_EXPLORATION_TIME_LIMIT";

    /// <summary>Optional maximum number of exploration failures to observe.</summary>
    public const string ExplorationMaxFailures = "ORLEANS_SIMULATION_EXPLORATION_MAX_FAILURES";
}

/// <summary>Configures a framework-neutral replay-aware test fixture.</summary>
public sealed record ReplayTestConfiguration
{
    /// <summary>Gets the stable test class/suite identity.</summary>
    public required string TestClassName { get; init; }

    /// <summary>Gets the stable test method/case identity.</summary>
    public required string TestMethodName { get; init; }

    /// <summary>Gets an explicit simulation seed, overriding test-identity and environment derivation.</summary>
    public int? SimulationSeed { get; init; }

    /// <summary>Gets an explicit schedule seed, overriding environment derivation.</summary>
    public int? ScheduleSeed { get; init; }

    /// <summary>Gets the artifact directory, overriding the environment and temporary default.</summary>
    public string? ArtifactDirectory { get; init; }

    /// <summary>Gets the controlled step bound for each execution.</summary>
    public int MaxSteps { get; init; } = 1_000_000;

    /// <summary>
    /// Gets an explicit simulated processor count for recorded runs (see
    /// <see cref="ReplayRecordingOptions.ProcessorCount"/>), or <see langword="null"/> for the default. When set, an
    /// environment-selected replay also requires the artifact to have recorded this count.
    /// </summary>
    public int? ProcessorCount { get; init; }

    /// <summary>Gets whether successful recordings should also be written.</summary>
    public bool RecordSuccessfulRuns { get; init; }

    /// <summary>Gets the instrumentation compatibility identity, when applicable.</summary>
    public ReplayInstrumentationIdentity? Instrumentation { get; init; }
}

/// <summary>
/// Configures repeated schedule exploration. Explicit values override environment settings; omitted
/// values use environment settings and then fixture defaults.
/// </summary>
public sealed record ReplayTestCampaignOptions
{
    /// <summary>
    /// Gets the maximum iteration count. When omitted, the default is 100 unless a time limit is the
    /// only configured bound.
    /// </summary>
    public int? MaxIterations { get; init; }

    /// <summary>Gets an optional wall-clock limit checked between complete deterministic iterations.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Gets the maximum number of non-success outcomes to observe. The default is one.</summary>
    public int? MaxFailures { get; init; }
}

/// <summary>The structured result and reproducibility data for one replay-aware test run.</summary>
public sealed record ReplayTestResult
{
    /// <summary>Gets the simulation seed.</summary>
    public required int SimulationSeed { get; init; }

    /// <summary>Gets the schedule seed.</summary>
    public required int ScheduleSeed { get; init; }

    /// <summary>Gets the record/replay execution result.</summary>
    public required ReplayExecutionResult Execution { get; init; }

    /// <summary>Gets the written or replayed artifact path, when available.</summary>
    public string? ArtifactPath { get; init; }

    /// <summary>Gets whether the scenario completed without a failure outcome.</summary>
    public bool IsSuccessful => Execution.Artifact.Outcome.Kind == ReplayTerminationKind.Completed;

    /// <summary>Formats a stable failure message containing seed and artifact attachment information.</summary>
    public string ToFailureMessage()
    {
        var builder = new StringBuilder();
        builder.Append("Orleans simulation test outcome: ").Append(Execution.Artifact.Outcome.Kind).Append('\n');
        builder.Append("Simulation seed: ").Append(SimulationSeed).Append('\n');
        builder.Append("Schedule seed: ").Append(ScheduleSeed).Append('\n');
        builder.Append("Failure identity: ")
            .Append(Execution.Artifact.Outcome.FailureIdentity ?? "none")
            .Append('\n');
        builder.Append("Replay artifact: ").Append(ArtifactPath ?? "not written");
        if (ArtifactPath is not null)
        {
            builder.Append('\n').Append("Environment replay: ")
                .Append(ReplayTestEnvironment.Artifact).Append('=').Append(ArtifactPath);
        }

        return builder.ToString();
    }

    /// <summary>Throws a framework-neutral test failure exception when the scenario did not complete.</summary>
    public void ThrowIfFailed()
    {
        if (!IsSuccessful)
        {
            throw new ReplayTestFailureException(this);
        }
    }

    /// <summary>Formats explicit CLI arguments for this artifact and scenario harness.</summary>
    public string GetReplayCommand(string assemblyPath, string scenarioType)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        ArgumentException.ThrowIfNullOrEmpty(scenarioType);
        if (ArtifactPath is null)
        {
            throw new InvalidOperationException("No replay artifact path is available.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"dotnet orleans-simulation replay \"{ArtifactPath}\" --assembly \"{assemblyPath}\" --scenario-type \"{scenarioType}\"");
    }
}

/// <summary>The aggregate result and reproducibility data for one repeated test campaign.</summary>
public sealed record ReplayTestCampaignResult
{
    /// <summary>Gets the simulation seed held constant across the campaign.</summary>
    public required int SimulationSeed { get; init; }

    /// <summary>Gets the underlying deterministic exploration result.</summary>
    public required ScheduleExplorationResult Exploration { get; init; }

    /// <summary>Gets persisted artifact paths keyed by stable iteration id.</summary>
    public required IReadOnlyDictionary<string, string> ArtifactPaths { get; init; }

    /// <summary>Gets retained failure artifact paths keyed by stable failure identity.</summary>
    public required IReadOnlyDictionary<string, string> RetainedFailureArtifactPaths { get; init; }

    /// <summary>Gets whether every completed iteration succeeded.</summary>
    public bool IsSuccessful => Exploration.FailureCount == 0;

    /// <summary>Formats a stable aggregate failure message with retained replay artifacts.</summary>
    public string ToFailureMessage()
    {
        var builder = new StringBuilder();
        builder.Append("Orleans simulation exploration outcome: ")
            .Append(Exploration.FailureCount)
            .Append(" failure(s) in ")
            .Append(Exploration.Iterations.Count)
            .Append(" iteration(s)")
            .Append('\n');
        builder.Append("Termination: ").Append(Exploration.TerminationReason).Append('\n');
        builder.Append("Simulation seed: ").Append(SimulationSeed);
        foreach ((string failure, string path) in RetainedFailureArtifactPaths)
        {
            builder.Append('\n').Append("Replay artifact [").Append(failure).Append("]: ").Append(path);
        }

        return builder.ToString();
    }

    /// <summary>Throws a framework-neutral failure exception when any campaign iteration failed.</summary>
    public void ThrowIfFailed()
    {
        if (!IsSuccessful)
        {
            throw new ReplayTestCampaignFailureException(this);
        }
    }
}

/// <summary>Exception thrown by <see cref="ReplayTestResult.ThrowIfFailed"/>.</summary>
public sealed class ReplayTestFailureException : Exception
{
    /// <summary>Initializes a replay-aware test failure.</summary>
    public ReplayTestFailureException(ReplayTestResult result)
        : base(result?.ToFailureMessage())
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>Gets the structured test result.</summary>
    public ReplayTestResult Result { get; }
}

/// <summary>Exception thrown by <see cref="ReplayTestCampaignResult.ThrowIfFailed"/>.</summary>
public sealed class ReplayTestCampaignFailureException : Exception
{
    /// <summary>Initializes a repeated exploration failure.</summary>
    public ReplayTestCampaignFailureException(ReplayTestCampaignResult result)
        : base(result?.ToFailureMessage())
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>Gets the structured campaign result.</summary>
    public ReplayTestCampaignResult Result { get; }
}

/// <summary>
/// Records failed controlled scenarios, explores repeated schedules, and replays an artifact selected
/// through <see cref="ReplayTestEnvironment.Artifact"/>.
/// </summary>
public sealed class ReplayTestFixture
{
    private const int DefaultCampaignIterations = 100;
    private const int DefaultCampaignMaxFailures = 1;
    private readonly ReplayTestConfiguration _configuration;

    /// <summary>Initializes a replay-aware fixture.</summary>
    public ReplayTestFixture(ReplayTestConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(configuration.TestClassName);
        ArgumentException.ThrowIfNullOrEmpty(configuration.TestMethodName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxSteps);
        if (configuration.ProcessorCount is { } processorCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount, nameof(configuration));
        }

        _configuration = configuration;
    }

    private int ProcessorCount => _configuration.ProcessorCount ?? SimulationRuntimeEnvironment.DefaultProcessorCount;

    /// <summary>Gets the stable seed derived by the existing simulation test-identity helper.</summary>
    public int TestIdentitySeed => SimulationSeed.FromStrings(
        _configuration.TestClassName,
        _configuration.TestMethodName);

    /// <summary>
    /// Records or environment-replays a fresh controlled scenario with explicit cancellation.
    /// The synchronous scenario callback runs inline on the calling thread under the scheduler's
    /// control scope. Work which it queues runs on scheduler-owned operation workers; both are part
    /// of the same logical simulation thread.
    /// </summary>
    public ReplayTestResult Run(
        Action<SimulationScheduler> scenario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        string? replayPath = GetEnvironmentValue(ReplayTestEnvironment.Artifact);
        if (replayPath is not null)
        {
            ReplayArtifact artifact = ReplayArtifactSerializer.Read(replayPath);
            ReplayExecutionResult replay = ReplayRunner.Replay(
                artifact,
                ReplayCompatibilityRequirements.Current() with
                {
                    Instrumentation = _configuration.Instrumentation,
                    ProcessorCount = _configuration.ProcessorCount,
                },
                scenario,
                _configuration.MaxSteps,
                cancellationToken);
            return new ReplayTestResult
            {
                SimulationSeed = artifact.SimulationSeed,
                ScheduleSeed = artifact.Scheduler.ScheduleSeed ?? 0,
                Execution = replay,
                ArtifactPath = Path.GetFullPath(replayPath),
            };
        }

        int simulationSeed = ResolveSimulationSeed();
        int scheduleSeed = ResolveScheduleSeed(simulationSeed);
        ReplayExecutionResult execution = ReplayRunner.Record(
            new ReplayRecordingOptions
            {
                SimulationSeed = simulationSeed,
                SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
                ScheduleSeed = scheduleSeed,
                MaxSteps = _configuration.MaxSteps,
                ProcessorCount = ProcessorCount,
                Instrumentation = _configuration.Instrumentation,
            },
            scenario,
            cancellationToken);

        string? artifactPath = null;
        if (_configuration.RecordSuccessfulRuns ||
            execution.Artifact.Outcome.Kind != ReplayTerminationKind.Completed)
        {
            artifactPath = WriteArtifact(execution, iterationId: null);
        }

        return new ReplayTestResult
        {
            SimulationSeed = simulationSeed,
            ScheduleSeed = scheduleSeed,
            Execution = execution,
            ArtifactPath = artifactPath,
        };
    }

    /// <summary>Explores repeated schedules using environment settings and fixture defaults.</summary>
    public ReplayTestCampaignResult Explore(
        Action<SimulationScheduler> scenario,
        CancellationToken cancellationToken) =>
        Explore(scenario, options: null, cancellationToken);

    /// <summary>
    /// Explores repeated schedules. The callback is invoked once for each fresh scheduler and must
    /// construct fresh scenario state on every invocation.
    /// </summary>
    public ReplayTestCampaignResult Explore(
        Action<SimulationScheduler> scenario,
        ReplayTestCampaignOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= new ReplayTestCampaignOptions();

        if (GetEnvironmentValue(ReplayTestEnvironment.Artifact) is not null)
        {
            EnsureReplayHasNoCampaignOverrides(options);
            return CreateReplayCampaignResult(Run(scenario, cancellationToken));
        }

        TimeSpan? timeLimit = options.TimeLimit ??
            ParseEnvironmentTimeSpan(ReplayTestEnvironment.ExplorationTimeLimit);
        int? requestedIterations = options.MaxIterations ??
            ParseEnvironmentPositiveInt(ReplayTestEnvironment.ExplorationIterations);
        int maxIterations = requestedIterations ??
            (timeLimit is null ? DefaultCampaignIterations : int.MaxValue);
        int maxFailures = options.MaxFailures ??
            ParseEnvironmentPositiveInt(ReplayTestEnvironment.ExplorationMaxFailures) ??
            DefaultCampaignMaxFailures;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFailures);
        if (timeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TimeLimit must be positive when specified.");
        }

        int simulationSeed = ResolveSimulationSeed();
        int firstScheduleSeed = ResolveScheduleSeed(simulationSeed);
        ScheduleExplorationResult exploration = ScheduleExplorer.Explore(
            new ScheduleExplorationOptions
            {
                SimulationSeed = simulationSeed,
                FirstScheduleSeed = firstScheduleSeed,
                MaxIterations = maxIterations,
                MaxFailures = maxFailures,
                MaxStepsPerIteration = _configuration.MaxSteps,
                ProcessorCount = ProcessorCount,
                TimeLimit = timeLimit,
                Instrumentation = _configuration.Instrumentation,
            },
            scenario,
            cancellationToken);
        (IReadOnlyDictionary<string, string> artifactPaths,
            IReadOnlyDictionary<string, string> retainedFailureArtifactPaths) =
            WriteCampaignArtifacts(exploration);

        return new ReplayTestCampaignResult
        {
            SimulationSeed = simulationSeed,
            Exploration = exploration,
            ArtifactPaths = artifactPaths,
            RetainedFailureArtifactPaths = retainedFailureArtifactPaths,
        };
    }

    private static ReplayTestCampaignResult CreateReplayCampaignResult(ReplayTestResult replay)
    {
        string iterationId = ScheduleExplorer.CreateIterationId(
            replay.SimulationSeed,
            replay.ScheduleSeed,
            iteration: 0);
        var iteration = new ScheduleExplorationIteration
        {
            Index = 0,
            IterationId = iterationId,
            ScheduleSeed = replay.ScheduleSeed,
            Execution = replay.Execution,
        };
        var counts = new SortedDictionary<ReplayTerminationKind, int>
        {
            [replay.Execution.Artifact.Outcome.Kind] = 1,
        };
        var retained = new SortedDictionary<string, ReplayExecutionResult>(StringComparer.Ordinal);
        var retainedPaths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!replay.IsSuccessful)
        {
            string failure = GetFailureKey(replay.Execution);
            retained[failure] = replay.Execution;
            retainedPaths[failure] = replay.ArtifactPath!;
        }

        var artifactPaths = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [iterationId] = replay.ArtifactPath!,
        };
        return new ReplayTestCampaignResult
        {
            SimulationSeed = replay.SimulationSeed,
            Exploration = new ScheduleExplorationResult
            {
                TerminationReason = ExplorationTerminationReason.IterationLimit,
                Iterations = [iteration],
                OutcomeCounts = counts,
                RetainedFailures = retained,
            },
            ArtifactPaths = artifactPaths,
            RetainedFailureArtifactPaths = retainedPaths,
        };
    }

    private (
        IReadOnlyDictionary<string, string> ArtifactPaths,
        IReadOnlyDictionary<string, string> RetainedFailureArtifactPaths)
        WriteCampaignArtifacts(ScheduleExplorationResult exploration)
    {
        var artifactPaths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (_configuration.RecordSuccessfulRuns)
        {
            foreach (ScheduleExplorationIteration iteration in exploration.Iterations)
            {
                artifactPaths[iteration.IterationId] =
                    WriteArtifact(iteration.Execution, iteration.IterationId);
            }
        }

        var retainedPaths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string failure, ReplayExecutionResult retained) in exploration.RetainedFailures)
        {
            ScheduleExplorationIteration? iteration = exploration.Iterations.FirstOrDefault(candidate =>
                string.Equals(candidate.Execution.ArtifactId, retained.ArtifactId, StringComparison.Ordinal));
            if (iteration is null)
            {
                throw new InvalidOperationException(
                    $"No campaign iteration matches retained artifact '{retained.ArtifactId}' for '{failure}'.");
            }

            if (!artifactPaths.TryGetValue(iteration.IterationId, out string? path))
            {
                path = WriteArtifact(retained, iteration.IterationId);
                artifactPaths[iteration.IterationId] = path;
            }

            retainedPaths[failure] = path;
        }

        return (artifactPaths, retainedPaths);
    }

    private int ResolveSimulationSeed()
    {
        if (_configuration.SimulationSeed is { } explicitSeed)
        {
            return explicitSeed;
        }

        return ParseEnvironmentSeed(ReplayTestEnvironment.SimulationSeed) ??
            TestIdentitySeed;
    }

    private int ResolveScheduleSeed(int simulationSeed) =>
        _configuration.ScheduleSeed ??
        ParseEnvironmentSeed(ReplayTestEnvironment.ScheduleSeed) ??
        new SimulationSeedAuthority(simulationSeed).GetDomainSeed(SimulationSeedDomain.Scheduler);

    private string WriteArtifact(ReplayExecutionResult execution, string? iterationId)
    {
        string safeIdentity = SanitizeFileName(
            _configuration.TestClassName + "." + _configuration.TestMethodName);
        string artifactId = execution.ArtifactId[..12];
        string suffix = iterationId is null
            ? artifactId
            : string.Create(CultureInfo.InvariantCulture, $"{iterationId}-{artifactId}");
        string path = Path.GetFullPath(Path.Combine(ResolveArtifactDirectory(), $"{safeIdentity}-{suffix}.cwr.json"));
        ReplayArtifactSerializer.Write(path, execution.Artifact);
        return path;
    }

    private string ResolveArtifactDirectory() =>
        _configuration.ArtifactDirectory ??
        GetEnvironmentValue(ReplayTestEnvironment.ArtifactDirectory) ??
        Path.Combine(Path.GetTempPath(), "orleans-simulation-test-artifacts");

    private static void EnsureReplayHasNoCampaignOverrides(ReplayTestCampaignOptions options)
    {
        if (options.MaxIterations is not null ||
            options.TimeLimit is not null ||
            options.MaxFailures is not null ||
            GetEnvironmentValue(ReplayTestEnvironment.ExplorationIterations) is not null ||
            GetEnvironmentValue(ReplayTestEnvironment.ExplorationTimeLimit) is not null ||
            GetEnvironmentValue(ReplayTestEnvironment.ExplorationMaxFailures) is not null)
        {
            throw new InvalidOperationException(
                $"{ReplayTestEnvironment.Artifact} replays exactly one execution and cannot be combined " +
                "with exploration limits.");
        }
    }

    private static string GetFailureKey(ReplayExecutionResult execution)
    {
        ReplayOutcome outcome = execution.Artifact.Outcome;
        return $"{outcome.Kind}:{outcome.FailureIdentity ?? "<none>"}";
    }

    private static int? ParseEnvironmentSeed(string name) =>
        ParseSeed(name, GetEnvironmentValue(name));

    private static int? ParseSeed(string name, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        {
            throw new InvalidOperationException(
                $"Environment variable {name} must be a 32-bit integer, not '{value}'.");
        }

        return seed;
    }

    private static int? ParseEnvironmentPositiveInt(string name)
    {
        string? value = GetEnvironmentValue(name);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
            parsed <= 0)
        {
            throw new InvalidOperationException(
                $"Environment variable {name} must be a positive 32-bit integer, not '{value}'.");
        }

        return parsed;
    }

    private static TimeSpan? ParseEnvironmentTimeSpan(string name)
    {
        string? value = GetEnvironmentValue(name);
        if (value is null)
        {
            return null;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed) ||
            parsed <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Environment variable {name} must be a positive invariant TimeSpan, not '{value}'.");
        }

        return parsed;
    }

    private static string? GetEnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }
}
