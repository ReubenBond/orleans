using System.Security.Cryptography;
using System.Text.Json;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Tool;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Cli;

public sealed class ReplayCliCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "clockwork-replay-cli",
        Guid.NewGuid().ToString("n"));

    [Fact]
    public void RecordReplayAndTraceShowRoundTripExplicitScenario()
    {
        string artifact = Path.Combine(_root, "success.cwr.json");

        (ExitCode runCode, string runOutput, string runError) = Invoke(
            "record",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--artifact", artifact,
            "--simulation-seed", "42",
            "--schedule-seed", "7",
            "--json");
        (ExitCode replayCode, string replayOutput, string replayError) = Invoke(
            "replay",
            artifact,
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--json");
        (ExitCode traceCode, string traceOutput, string traceError) = Invoke(
            "trace", "show", artifact);

        Assert.Equal(ExitCode.Success, runCode);
        Assert.Equal(ExitCode.Success, replayCode);
        Assert.Equal(ExitCode.Success, traceCode);
        Assert.True(File.Exists(artifact));
        Assert.Empty(runError);
        Assert.Empty(replayError);
        Assert.Empty(traceError);
        Assert.Equal("Completed", JsonElement.Parse(runOutput).GetProperty("outcome").GetString());
        Assert.True(JsonElement.Parse(replayOutput).GetProperty("reproduced").GetBoolean());
        Assert.Contains("Orleans simulation replay clockwork.replay/v2", traceOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordReturnsClassifiedFailureAndWritesArtifact()
    {
        string artifact = Path.Combine(_root, "fault.cwr.json");

        (ExitCode code, string output, string error) = Invoke(
            "record",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(FaultScenario).FullName!,
            "--artifact", artifact,
            "--seed", "43",
            "--schedule-seed", "8");

        Assert.Equal(ExitCode.ExecutionFailure, code);
        Assert.Contains("outcome: Faulted", output, StringComparison.Ordinal);
        Assert.Empty(error);
        Assert.Equal(ReplayTerminationKind.Faulted, ReplayArtifactSerializer.Read(artifact).Outcome.Kind);
    }

    [Fact]
    public void ManifestIdentityIsMappedFromTypedClosureManifest()
    {
        string artifactPath = Path.Combine(_root, "manifest.cwr.json");
        ClosureManifest manifest = CreateManifest(new string('a', 64));
        string manifestPath = WriteManifest("manifest.json", manifest);

        (ExitCode code, _, string error) = Invoke(
            "record",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--artifact", artifactPath,
            "--simulation-seed", "42",
            "--manifest", manifestPath);

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(error);
        ReplayInstrumentationIdentity identity =
            Assert.IsType<ReplayInstrumentationIdentity>(
                ReplayArtifactSerializer.Read(artifactPath).Instrumentation);
        Assert.Equal(manifest.IncrementalKey, identity.ManifestId);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath))),
            identity.ManifestSha256);
        ReplayAssemblyIdentity assembly = Assert.Single(identity.Assemblies);
        Assert.Equal("app.dll", assembly.Name);
        Assert.Equal(new string('e', 64), assembly.Sha256);
        Assert.Equal(ReplayCompatibility.CurrentRuntimeCompatibility, assembly.RuntimeCompatibility);
    }

    [Fact]
    public void ReplayRejectsDifferentCopiedAssetIdentity()
    {
        string artifactPath = Path.Combine(_root, "manifest-mismatch.cwr.json");
        ClosureManifest recorded = CreateManifest(new string('a', 64));
        ClosureManifest current = recorded with
        {
            CopiedAssets =
            [
                new ClosureManifestCopiedAsset(
                    "app.runtimeconfig.json",
                    new string('b', 64)),
            ],
        };
        string recordedManifest = WriteManifest("recorded-manifest.json", recorded);
        string currentManifest = WriteManifest("current-manifest.json", current);
        (ExitCode recordCode, _, _) = Invoke(
            "record",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--artifact", artifactPath,
            "--simulation-seed", "42",
            "--manifest", recordedManifest);

        (ExitCode replayCode, _, string replayError) = Invoke(
            "replay",
            artifactPath,
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--manifest", currentManifest);

        Assert.Equal(ExitCode.Success, recordCode);
        Assert.Equal(ExitCode.ReplayError, replayCode);
        Assert.Contains("manifest", replayError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExploreWritesRetainedRaceArtifact()
    {
        string outputDirectory = Path.Combine(_root, "explore");

        (ExitCode code, string output, string error) = Invoke(
            "explore",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(RaceScenario).FullName!,
            "--output", outputDirectory,
            "--simulation-seed", "44",
            "--schedule-seed", "9",
            "--count", "4",
            "--json");

        Assert.Equal(ExitCode.ExecutionFailure, code);
        Assert.Empty(error);
        Assert.Equal(1, JsonElement.Parse(output).GetProperty("failures").GetInt32());
        string retained = Assert.Single(Directory.GetFiles(outputDirectory, "*.cwr.json"));
        Assert.Equal(ReplayTerminationKind.RaceDetected, ReplayArtifactSerializer.Read(retained).Outcome.Kind);
    }

    [Fact]
    public void ExploreTimeLimitCanBeTheOnlyRunBound()
    {
        (ExitCode code, string output, string error) = Invoke(
            "explore",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--output", Path.Combine(_root, "time-only"),
            "--simulation-seed", "44",
            "--schedule-seed", "9",
            "--time-limit", TimeSpan.FromTicks(1).ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            "--json");

        JsonElement result = JsonElement.Parse(output);
        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(error);
        Assert.Equal("TimeLimit", result.GetProperty("termination").GetString());
        Assert.Equal(0, result.GetProperty("iterations").GetInt32());
    }

    [Fact]
    public void MinimizeWritesVerifiedSmallerArtifact()
    {
        string artifact = RecordLongFaultArtifact();
        ReplayArtifact original = ReplayArtifactSerializer.Read(artifact);
        string minimizedPath = Path.Combine(_root, "minimized.cwr.json");

        (ExitCode code, string output, string error) = Invoke(
            "minimize",
            artifact,
            "--assembly", TestAssembly,
            "--scenario-type", typeof(LongFaultScenario).FullName!,
            "--output", minimizedPath,
            "--max-attempts", "200",
            "--json");

        ReplayArtifact minimized = ReplayArtifactSerializer.Read(minimizedPath);
        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(error);
        Assert.True(JsonElement.Parse(output).GetProperty("verified").GetBoolean());
        Assert.True(minimized.Decisions.Count < original.Decisions.Count);
        Assert.Equal(original.Outcome.FailureIdentity, minimized.Outcome.FailureIdentity);
    }

    [Fact]
    public void MissingExplicitScenarioTypeIsUsageError()
    {
        (ExitCode code, _, string error) = Invoke(
            "record",
            "--assembly", TestAssembly,
            "--artifact", Path.Combine(_root, "missing.cwr.json"),
            "--simulation-seed", "1");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("--scenario-type", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--stop-on-first")]
    [InlineData("--parallelism")]
    public void RemovedExplorationOptionsAreUsageErrors(string option)
    {
        (ExitCode code, _, string error) = Invoke(
            "explore",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--output", Path.Combine(_root, "removed-option"),
            "--simulation-seed", "1",
            "--schedule-seed", "1",
            option);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown option", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordRejectsSimulationAndLegacySeedTogether()
    {
        (ExitCode code, _, string error) = Invoke(
            "record",
            "--assembly", TestAssembly,
            "--scenario-type", typeof(SuccessScenario).FullName!,
            "--artifact", Path.Combine(_root, "ambiguous-seed.cwr.json"),
            "--simulation-seed", "1",
            "--seed", "2");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("not both", error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public sealed class SuccessScenario : IReplayScenario
    {
        public void Configure(SimulationScheduler scheduler)
        {
            scheduler.Schedule("one", scheduler.Yield);
            scheduler.Schedule("two", static () => { });
        }
    }

    public sealed class FaultScenario : IReplayScenario
    {
        public void Configure(SimulationScheduler scheduler) =>
            scheduler.Schedule("fault", static () => throw new KnownCliFailureException());
    }

    public sealed class RaceScenario : IReplayScenario
    {
        public void Configure(SimulationScheduler scheduler)
        {
            var target = new object();
            scheduler.Schedule(
                "one",
                () => RaceInstrumentation.WriteInstance(
                    target, "State.Value", "CliRace::One", 1, sourceFile: null, sourceLine: -1));
            scheduler.Schedule(
                "two",
                () => RaceInstrumentation.WriteInstance(
                    target, "State.Value", "CliRace::Two", 2, sourceFile: null, sourceLine: -1));
        }
    }

    public sealed class LongFaultScenario : IReplayScenario
    {
        public void Configure(SimulationScheduler scheduler)
        {
            scheduler.Schedule("background-one", () => YieldMany(scheduler));
            scheduler.Schedule("background-two", () => YieldMany(scheduler));
            scheduler.Schedule("fault", static () => throw new KnownCliFailureException());
        }

        private static void YieldMany(SimulationScheduler scheduler)
        {
            for (var index = 0; index < 5; index++)
            {
                scheduler.Yield();
            }
        }
    }

    private static string TestAssembly => typeof(ReplayCliCommandTests).Assembly.Location;

    private string WriteManifest(string fileName, ClosureManifest manifest)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, manifest.ToJson());
        return path;
    }

    private static ClosureManifest CreateManifest(string incrementalKey) => new()
    {
        EngineVersion = "1.2.3",
        RuleSetId = "clockwork.test",
        RuleSetVersion = "1",
        RuleSetSignature = new string('c', 64),
        ConfigurationSignature = new string('d', 64),
        Mode = InstrumentationMode.Controlled,
        IncrementalKey = incrementalKey,
        EntryRelativePath = "app.dll",
        Assemblies =
        [
            new ClosureManifestEntry(
                "app.dll",
                WasRewritten: true,
                WasNoOp: false,
                WasReSigned: false,
                ReadyToRunStripped: false,
                InputSha256: new string('d', 64),
                OutputSha256: new string('e', 64),
                ErrorCount: 0),
        ],
        CopiedAssets =
        [
            new ClosureManifestCopiedAsset("app.runtimeconfig.json", new string('f', 64)),
        ],
    };

    private string RecordLongFaultArtifact()
    {
        for (var seed = 1; seed <= 100; seed++)
        {
            string artifact = Path.Combine(_root, $"long-{seed}.cwr.json");
            (ExitCode code, _, _) = Invoke(
                "record",
                "--assembly", TestAssembly,
                "--scenario-type", typeof(LongFaultScenario).FullName!,
                "--artifact", artifact,
                "--simulation-seed", "45",
                "--schedule-seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(ExitCode.ExecutionFailure, code);
            if (ReplayArtifactSerializer.Read(artifact).Decisions.Count >= 4)
            {
                return artifact;
            }
        }

        throw new InvalidOperationException("The CLI seed corpus did not produce a long trace.");
    }

    private static (ExitCode Code, string Output, string Error) Invoke(params string[] args)
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        ExitCode code = Program.Run(args, output, error);
        return (code, output.ToString().Trim(), error.ToString().Trim());
    }

    private sealed class KnownCliFailureException : Exception;
}
