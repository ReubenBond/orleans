using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Tool;

internal static class ReplayCommands
{
    private static readonly HashSet<string> RecordValueOptions = new(StringComparer.Ordinal)
    {
        "assembly", "scenario-type", "artifact", "simulation-seed", "seed", "schedule-seed", "strategy",
        "max-steps", "manifest",
    };

    private static readonly HashSet<string> ReplayValueOptions = new(StringComparer.Ordinal)
    {
        "assembly", "scenario-type", "max-steps", "manifest",
    };

    private static readonly HashSet<string> ExploreValueOptions = new(StringComparer.Ordinal)
    {
        "assembly", "scenario-type", "output", "simulation-seed", "seed", "schedule-seed", "count",
        "max-failures", "max-steps", "time-limit", "manifest",
    };

    private static readonly HashSet<string> MinimizeValueOptions = new(StringComparer.Ordinal)
    {
        "assembly", "scenario-type", "output", "max-attempts", "max-steps", "time-limit", "manifest",
    };

    public static ExitCode RunRecord(string[] args, TextWriter output)
    {
        ArgumentReader reader = ArgumentReader.Parse(args, RecordValueOptions);
        EnsureNoPositionals(reader, "record");
        string assembly = Require(reader.GetString("assembly"), "--assembly");
        string scenarioType = Require(reader.GetString("scenario-type"), "--scenario-type");
        string artifactPath = Require(reader.GetString("artifact"), "--artifact");
        int simulationSeed = ParseInt(
            Require(GetSimulationSeed(reader), "--simulation-seed"),
            "--simulation-seed");
        ReplaySchedulingPolicy policy = ParsePolicy(reader.GetString("strategy", "seeded-random")!);
        int? scheduleSeed = ParseOptionalInt(reader.GetString("schedule-seed"), "--schedule-seed");
        if (policy != ReplaySchedulingPolicy.SeededRandom && scheduleSeed is not null)
        {
            throw new UsageException("--schedule-seed is only valid with --strategy seeded-random.");
        }

        int maxSteps = ParsePositiveInt(reader.GetString("max-steps", "1000000")!, "--max-steps");
        ReplayInstrumentationIdentity? instrumentation = ReadOptionalManifest(reader.GetString("manifest"));
        bool json = reader.GetFlag("json");
        reader.EnsureAllConsumed();

        Func<IReplayScenario> factory = ReplayScenarioLoader.LoadFactory(assembly, scenarioType);
        ReplayExecutionResult result = ReplayRunner.Record(
            new ReplayRecordingOptions
            {
                SimulationSeed = simulationSeed,
                SchedulingPolicy = policy,
                ScheduleSeed = scheduleSeed,
                MaxSteps = maxSteps,
                Instrumentation = instrumentation,
            },
            scheduler => factory().Configure(scheduler),
            CancellationToken.None);
        ReplayArtifactSerializer.Write(artifactPath, result.Artifact);
        WriteExecution(output, result, Path.GetFullPath(artifactPath), json);
        return ExitForOutcome(result.Artifact.Outcome);
    }

    public static ExitCode RunReplay(string[] args, TextWriter output)
    {
        ArgumentReader reader = ArgumentReader.Parse(args, ReplayValueOptions);
        string artifactPath = RequireSinglePositional(reader, "replay", "<artifact>");
        string assembly = Require(reader.GetString("assembly"), "--assembly");
        string scenarioType = Require(reader.GetString("scenario-type"), "--scenario-type");
        int maxSteps = ParsePositiveInt(reader.GetString("max-steps", "1000000")!, "--max-steps");
        ReplayInstrumentationIdentity? instrumentation = ReadOptionalManifest(reader.GetString("manifest"));
        bool json = reader.GetFlag("json");
        reader.EnsureAllConsumed();

        ReplayArtifact artifact = ReplayArtifactSerializer.Read(artifactPath);
        ReplayCompatibilityRequirements requirements = ReplayCompatibilityRequirements.Current() with
        {
            Instrumentation = instrumentation,
        };
        Func<IReplayScenario> factory = ReplayScenarioLoader.LoadFactory(assembly, scenarioType);
        ReplayExecutionResult result = ReplayRunner.Replay(
            artifact,
            requirements,
            scheduler => factory().Configure(scheduler),
            maxSteps,
            CancellationToken.None);
        WriteExecution(
            output,
            result,
            Path.GetFullPath(artifactPath),
            json);
        return ExitForOutcome(result.Artifact.Outcome);
    }

    public static ExitCode RunExplore(string[] args, TextWriter output)
    {
        ArgumentReader reader = ArgumentReader.Parse(args, ExploreValueOptions);
        EnsureNoPositionals(reader, "explore");
        string assembly = Require(reader.GetString("assembly"), "--assembly");
        string scenarioType = Require(reader.GetString("scenario-type"), "--scenario-type");
        string outputDirectory = Require(reader.GetString("output"), "--output");
        int simulationSeed = ParseInt(
            Require(GetSimulationSeed(reader), "--simulation-seed"),
            "--simulation-seed");
        int firstScheduleSeed = ParseInt(
            Require(reader.GetString("schedule-seed"), "--schedule-seed"),
            "--schedule-seed");
        int maxFailures = ParsePositiveInt(reader.GetString("max-failures", "1")!, "--max-failures");
        int maxSteps = ParsePositiveInt(reader.GetString("max-steps", "1000000")!, "--max-steps");
        TimeSpan? timeLimit = ParseOptionalTimeSpan(reader.GetString("time-limit"), "--time-limit");
        string? countValue = reader.GetString("count");
        int count = countValue is not null
            ? ParsePositiveInt(countValue, "--count")
            : timeLimit is null
                ? 100
                : int.MaxValue;
        ReplayInstrumentationIdentity? instrumentation = ReadOptionalManifest(reader.GetString("manifest"));
        bool json = reader.GetFlag("json");
        reader.EnsureAllConsumed();

        Func<IReplayScenario> factory = ReplayScenarioLoader.LoadFactory(assembly, scenarioType);
        ScheduleExplorationResult result = ScheduleExplorer.Explore(
            new ScheduleExplorationOptions
            {
                SimulationSeed = simulationSeed,
                FirstScheduleSeed = firstScheduleSeed,
                MaxIterations = count,
                MaxFailures = maxFailures,
                MaxStepsPerIteration = maxSteps,
                TimeLimit = timeLimit,
                Instrumentation = instrumentation,
            },
            scheduler => factory().Configure(scheduler),
            CancellationToken.None);

        Directory.CreateDirectory(outputDirectory);
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string failure, ReplayExecutionResult retained) in result.RetainedFailures)
        {
            ScheduleExplorationIteration iteration = result.Iterations.First(
                candidate => string.Equals(
                    candidate.Execution.ArtifactId,
                    retained.ArtifactId,
                    StringComparison.Ordinal));
            string path = Path.GetFullPath(Path.Combine(outputDirectory, iteration.IterationId + ".cwr.json"));
            ReplayArtifactSerializer.Write(path, retained.Artifact);
            paths[failure] = path;
        }

        WriteExploration(output, result, paths, json);
        return result.FailureCount == 0 ? ExitCode.Success : ExitCode.ExecutionFailure;
    }

    public static ExitCode RunMinimize(string[] args, TextWriter output)
    {
        ArgumentReader reader = ArgumentReader.Parse(args, MinimizeValueOptions);
        string artifactPath = RequireSinglePositional(reader, "minimize", "<artifact>");
        string assembly = Require(reader.GetString("assembly"), "--assembly");
        string scenarioType = Require(reader.GetString("scenario-type"), "--scenario-type");
        string outputPath = reader.GetString("output", artifactPath + ".min.json")!;
        int maxAttempts = ParsePositiveInt(reader.GetString("max-attempts", "1000")!, "--max-attempts");
        int maxSteps = ParsePositiveInt(reader.GetString("max-steps", "1000000")!, "--max-steps");
        TimeSpan? timeLimit = ParseOptionalTimeSpan(reader.GetString("time-limit"), "--time-limit");
        ReplayInstrumentationIdentity? instrumentation = ReadOptionalManifest(reader.GetString("manifest"));
        bool json = reader.GetFlag("json");
        reader.EnsureAllConsumed();

        ReplayArtifact artifact = ReplayArtifactSerializer.Read(artifactPath);
        Func<IReplayScenario> factory = ReplayScenarioLoader.LoadFactory(assembly, scenarioType);
        Func<ReplayArtifact, ReplayFailureObservation> predicate = ReplayFailurePredicates.ForScenario(
            ReplayCompatibilityRequirements.Current() with { Instrumentation = instrumentation },
            scheduler => factory().Configure(scheduler),
            maxSteps,
            CancellationToken.None);
        ReplayMinimizationResult result = ReplayTraceMinimizer.Minimize(
            artifact,
            new ReplayMinimizationOptions
            {
                MaxAttempts = maxAttempts,
                TimeLimit = timeLimit,
            },
            predicate,
            CancellationToken.None);
        ReplayArtifactSerializer.Write(outputPath, result.MinimizedArtifact);
        WriteMinimization(output, result, Path.GetFullPath(outputPath), json);
        return ExitCode.Success;
    }

    public static ExitCode RunTrace(string[] args, TextWriter output)
    {
        ArgumentReader reader = ArgumentReader.Parse(args, new HashSet<string>(StringComparer.Ordinal));
        if (reader.Positional.Count != 2 ||
            !string.Equals(reader.Positional[0], "show", StringComparison.Ordinal))
        {
            throw new UsageException("trace usage: dotnet orleans-simulation trace show <artifact> [--json]");
        }

        bool json = reader.GetFlag("json");
        reader.EnsureAllConsumed();
        ReplayArtifact artifact = ReplayArtifactSerializer.Read(reader.Positional[1]);
        output.WriteLine(json
            ? ReplayArtifactSerializer.ToJson(artifact)
            : ReplayTraceRenderer.RenderText(artifact));
        return ExitCode.Success;
    }

    private static void WriteExecution(
        TextWriter output,
        ReplayExecutionResult result,
        string artifactPath,
        bool json)
    {
        if (json)
        {
            var node = new JsonObject
            {
                ["artifactId"] = result.ArtifactId,
                ["artifactPath"] = artifactPath,
                ["outcome"] = result.Artifact.Outcome.Kind.ToString(),
                ["failureIdentity"] = result.Artifact.Outcome.FailureIdentity,
                ["steps"] = result.Steps,
                ["reproduced"] = result.Reproduced,
            };
            output.WriteLine(node.ToJsonString());
            return;
        }

        output.WriteLine($"artifact: {result.ArtifactId}");
        output.WriteLine($"path: {artifactPath}");
        output.WriteLine($"outcome: {result.Artifact.Outcome.Kind}");
        output.WriteLine($"failure: {result.Artifact.Outcome.FailureIdentity ?? "none"}");
        output.WriteLine($"steps: {result.Steps.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"reproduced: {result.Reproduced.ToString().ToLowerInvariant()}");
    }

    private static void WriteExploration(
        TextWriter output,
        ScheduleExplorationResult result,
        IReadOnlyDictionary<string, string> paths,
        bool json)
    {
        if (json)
        {
            var counts = new JsonObject();
            foreach ((ReplayTerminationKind kind, int count) in result.OutcomeCounts)
            {
                counts[kind.ToString()] = count;
            }

            var artifacts = new JsonObject();
            foreach ((string failure, string path) in paths)
            {
                artifacts[failure] = path;
            }

            output.WriteLine(new JsonObject
            {
                ["termination"] = result.TerminationReason.ToString(),
                ["iterations"] = result.Iterations.Count,
                ["failures"] = result.FailureCount,
                ["outcomes"] = counts,
                ["artifacts"] = artifacts,
            }.ToJsonString());
            return;
        }

        output.WriteLine($"termination: {result.TerminationReason}");
        output.WriteLine($"iterations: {result.Iterations.Count.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"failures: {result.FailureCount.ToString(CultureInfo.InvariantCulture)}");
        foreach ((ReplayTerminationKind kind, int count) in result.OutcomeCounts)
        {
            output.WriteLine($"outcome.{kind}: {count.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach ((string failure, string path) in paths)
        {
            output.WriteLine($"artifact.{failure}: {path}");
        }
    }

    private static void WriteMinimization(
        TextWriter output,
        ReplayMinimizationResult result,
        string path,
        bool json)
    {
        if (json)
        {
            output.WriteLine(new JsonObject
            {
                ["originalDecisions"] = result.OriginalDecisionCount,
                ["minimizedDecisions"] = result.MinimizedDecisionCount,
                ["attempts"] = result.Attempts,
                ["verified"] = result.Verified,
                ["artifactPath"] = path,
                ["artifactId"] = ReplayArtifactSerializer.ComputeId(result.MinimizedArtifact),
            }.ToJsonString());
            return;
        }

        output.WriteLine($"original decisions: {result.OriginalDecisionCount.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"minimized decisions: {result.MinimizedDecisionCount.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"attempts: {result.Attempts.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"verified: {result.Verified.ToString().ToLowerInvariant()}");
        output.WriteLine($"path: {path}");
    }

    private static ReplayInstrumentationIdentity? ReadOptionalManifest(string? path)
    {
        if (path is null)
        {
            return null;
        }

        ClosureManifest manifest = ClosureManifestJson.Read(path, out byte[] canonicalUtf8);
        return ReplayInstrumentationIdentityMapper.FromClosureManifest(
            manifest,
            canonicalUtf8,
            ReplayCompatibility.CurrentRuntimeCompatibility);
    }

    private static ExitCode ExitForOutcome(ReplayOutcome outcome) =>
        outcome.Kind == ReplayTerminationKind.Completed
            ? ExitCode.Success
            : ExitCode.ExecutionFailure;

    private static ReplaySchedulingPolicy ParsePolicy(string value) =>
        value switch
        {
            "fifo" => ReplaySchedulingPolicy.Fifo,
            "round-robin" => ReplaySchedulingPolicy.RoundRobin,
            "priority" => ReplaySchedulingPolicy.Priority,
            "seeded-random" => ReplaySchedulingPolicy.SeededRandom,
            _ => throw new UsageException(
                $"Option '--strategy' must be fifo, round-robin, priority, or seeded-random, not '{value}'."),
        };

    private static int ParsePositiveInt(string value, string option)
    {
        int parsed = ParseInt(value, option);
        return parsed > 0
            ? parsed
            : throw new UsageException($"Option '{option}' must be greater than zero.");
    }

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new UsageException($"Option '{option}' must be a 32-bit integer, not '{value}'.");

    private static int? ParseOptionalInt(string? value, string option) =>
        value is null ? null : ParseInt(value, option);

    private static string? GetSimulationSeed(ArgumentReader reader)
    {
        string? simulationSeed = reader.GetString("simulation-seed");
        string? legacySeed = reader.GetString("seed");
        if (simulationSeed is not null && legacySeed is not null)
        {
            throw new UsageException("Specify either --simulation-seed or the legacy --seed option, not both.");
        }

        return simulationSeed ?? legacySeed;
    }

    private static TimeSpan? ParseOptionalTimeSpan(string? value, string option) =>
        value is null
            ? null
            : TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed) && parsed > TimeSpan.Zero
                ? parsed
                : throw new UsageException($"Option '{option}' must be a positive invariant TimeSpan, not '{value}'.");

    private static string Require(string? value, string option) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new UsageException($"Option '{option}' is required.");

    private static string RequireSinglePositional(ArgumentReader reader, string command, string expected)
    {
        if (reader.Positional.Count != 1)
        {
            throw new UsageException($"{command} requires exactly one {expected} argument.");
        }

        return reader.Positional[0];
    }

    private static void EnsureNoPositionals(ArgumentReader reader, string command)
    {
        if (reader.Positional.Count != 0)
        {
            throw new UsageException($"{command} does not accept positional arguments.");
        }
    }
}

internal static class ReplayScenarioLoader
{
    public static Func<IReplayScenario> LoadFactory(string assemblyPath, string typeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        string fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Scenario harness assembly was not found.", fullPath);
        }

        Assembly assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
            candidate =>
                !string.IsNullOrEmpty(candidate.Location) &&
                string.Equals(
                    Path.GetFullPath(candidate.Location),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        Type type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false)
            ?? throw new UsageException($"Scenario type '{typeName}' was not found in '{fullPath}'.");
        if (!typeof(IReplayScenario).IsAssignableFrom(type) ||
            type.IsAbstract ||
            !(type.IsPublic || type.IsNestedPublic) ||
            type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new UsageException(
                $"Scenario type '{typeName}' must be a public, non-abstract IReplayScenario implementation with a public parameterless constructor.");
        }

        return () => (IReplayScenario)Activator.CreateInstance(type)!;
    }
}

internal static class ReplayInstrumentationIdentityMapper
{
    public static ReplayInstrumentationIdentity FromClosureManifest(
        ClosureManifest manifest,
        ReadOnlySpan<byte> canonicalManifestUtf8,
        string runtimeCompatibility)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(runtimeCompatibility);
        if (canonicalManifestUtf8.IsEmpty)
        {
            throw new ArgumentException("Canonical manifest bytes cannot be empty.", nameof(canonicalManifestUtf8));
        }

        var assemblies = new List<ReplayAssemblyIdentity>(manifest.Assemblies.Length);
        foreach (ClosureManifestEntry entry in manifest.Assemblies)
        {
            string hash = entry.OutputSha256 ?? entry.InputSha256
                ?? throw new ReplayArtifactFormatException(
                    $"Manifest assembly '{entry.RelativePath}' has no content hash.");
            assemblies.Add(new ReplayAssemblyIdentity
            {
                Name = entry.RelativePath,
                Sha256 = hash,
                RuntimeCompatibility = runtimeCompatibility,
            });
        }

        assemblies.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new ReplayInstrumentationIdentity
        {
            ManifestId = manifest.IncrementalKey,
            ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(canonicalManifestUtf8)),
            EngineVersion = manifest.EngineVersion,
            RuleSetId = manifest.RuleSetId,
            RuleSetVersion = manifest.RuleSetVersion,
            RuleSetSignature = manifest.RuleSetSignature,
            Mode = manifest.Mode.ToString(),
            Assemblies = assemblies,
        };
    }
}
