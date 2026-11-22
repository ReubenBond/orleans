using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Racing;

namespace Orleans.Testing.Simulation.Runtime.Tests.Replay;

public sealed class ReplayArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clockwork-replay", Guid.NewGuid().ToString("n"));

    [Fact]
    public void CanonicalJsonIsStableAndRoundTrips()
    {
        ReplayArtifact artifact = CreateArtifact();

        byte[] first = ReplayArtifactSerializer.Serialize(artifact);
        ReplayArtifact roundTripped = ReplayArtifactSerializer.Deserialize(first);
        byte[] second = ReplayArtifactSerializer.Serialize(roundTripped);

        Assert.Equal(first, second);
        Assert.Equal(ReplayArtifactSerializer.ComputeId(artifact), ReplayArtifactSerializer.ComputeId(roundTripped));
        Assert.DoesNotContain(Environment.CommandLine, Encoding.UTF8.GetString(first), StringComparison.Ordinal);
        Assert.DoesNotContain("recordingState", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingVersionTwoGoldenArtifactRemainsReadableAndCanonical()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "test",
            "Simulation",
            "Clockwork.Runtime.Tests",
            "Replay",
            "Golden",
            "replay-v2.json");
        string expected = File.ReadAllText(path).TrimEnd('\r', '\n');

        ReplayArtifact artifact = ReplayArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(expected));

        Assert.Equal("clockwork.replay", artifact.Format);
        Assert.Equal(2, artifact.SchemaVersion);
        Assert.Equal(42, artifact.SimulationSeed);
        Assert.Equal(expected, ReplayArtifactSerializer.ToJson(artifact));
    }

    [Fact]
    public void LegacyVersionTwoBytesWithInstrumentationIdentityAndClockworkAssemblyRemainReadable()
    {
        const string legacyArtifact =
            "{\"format\":\"clockwork.replay\",\"schemaVersion\":2,\"rootSeed\":42," +
            "\"scheduler\":{\"strategy\":\"seeded-random\",\"scheduleSeed\":17,\"options\":{}}," +
            "\"instrumentation\":{\"manifestId\":\"legacy-manifest\"," +
            "\"manifestSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
            "\"engineVersion\":\"0.1.0\",\"ruleSetId\":\"clockwork.bcl.deterministic\"," +
            "\"ruleSetVersion\":\"1\"," +
            "\"ruleSetSignature\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"," +
            "\"mode\":\"Controlled\",\"assemblies\":[{\"name\":\"Orleans.Testing.Simulation.dll\"," +
            "\"sha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"," +
            "\"runtimeCompatibility\":\".NETCoreApp,Version=v11.0\"}]}," +
            "\"environment\":{\"clockworkVersion\":\"0.1.0.0\"," +
            "\"runtimeCompatibility\":\".NETCoreApp,Version=v11.0\"," +
            "\"processArchitecture\":\"X64\",\"operatingSystem\":\"Windows\"}," +
            "\"decisions\":[],\"raceSchedulingPoints\":[]," +
            "\"outcome\":{\"kind\":\"Completed\",\"failureIdentity\":null,\"diagnostic\":null}," +
            "\"diagnostics\":{\"liveness\":\"Quiescent\",\"virtualTimeTicks\":0,\"operations\":[]," +
            "\"resources\":[],\"pendingTimers\":[],\"deadlockCycles\":[],\"race\":null}}";
        byte[] expectedBytes = Encoding.UTF8.GetBytes(legacyArtifact);

        ReplayArtifact artifact = ReplayArtifactSerializer.Deserialize(expectedBytes);

        Assert.Equal("clockwork.replay", artifact.Format);
        Assert.Equal(2, artifact.SchemaVersion);
        Assert.Equal("clockwork.bcl.deterministic", artifact.Instrumentation!.RuleSetId);
        Assert.Equal("Orleans.Testing.Simulation.dll", Assert.Single(artifact.Instrumentation.Assemblies).Name);
        Assert.Equal(expectedBytes, ReplayArtifactSerializer.Serialize(artifact));
    }

    [Fact]
    public void CanonicalJsonSortsSchedulerOptions()
    {
        ReplayArtifact artifact = CreateArtifact() with
        {
            Scheduler = new ReplaySchedulerConfiguration
            {
                Strategy = "seeded-random",
                ScheduleSeed = 17,
                Options = new Dictionary<string, string>
                {
                    ["zeta"] = "last",
                    ["alpha"] = "first",
                },
            },
        };

        string json = ReplayArtifactSerializer.ToJson(artifact);

        Assert.True(
            json.IndexOf("\"alpha\"", StringComparison.Ordinal) <
            json.IndexOf("\"zeta\"", StringComparison.Ordinal));
    }

    [Fact]
    public void FileWriteIsCanonicalAndReplaceable()
    {
        string path = Path.Combine(_root, "run.cwr.json");
        ReplayArtifact artifact = CreateArtifact();
        Directory.CreateDirectory(_root);
        File.WriteAllText(path + ".tmp", "untouched");

        ReplayArtifactSerializer.Write(path, artifact);
        ReplayArtifactSerializer.Write(path, artifact);

        Assert.Equal("untouched", File.ReadAllText(path + ".tmp"));
        Assert.Empty(Directory.EnumerateFiles(_root, "run.cwr.json.*.tmp"));
        Assert.Equal(ReplayArtifactSerializer.Serialize(artifact), File.ReadAllBytes(path));
        Assert.Equal(
            ReplayArtifactSerializer.Serialize(artifact),
            ReplayArtifactSerializer.Serialize(ReplayArtifactSerializer.Read(path)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"clockwork.replay\",\"schemaVersion\":1}")]
    [InlineData("{\"format\":\"other\",\"schemaVersion\":2}")]
    [InlineData("{\"format\":\"clockwork.replay\",\"schemaVersion\":2,\"rootSeed\":1,\"scheduler\":null,\"environment\":null,\"outcome\":null}")]
    public void CorruptOrIncompleteJsonIsRejected(string json)
    {
        Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("schemaVersion")]
    [InlineData("scheduler.options")]
    [InlineData("instrumentation.assemblies")]
    [InlineData("decisions")]
    [InlineData("raceSchedulingPoints")]
    [InlineData("diagnostics")]
    [InlineData("diagnostics.operations")]
    [InlineData("diagnostics.resources")]
    [InlineData("diagnostics.resources[0].waiters")]
    [InlineData("diagnostics.pendingTimers")]
    [InlineData("diagnostics.deadlockCycles")]
    [InlineData("diagnostics.deadlockCycles[0].edges")]
    public void MissingRequiredWirePropertyIsRejected(string path)
    {
        JsonObject owner = GetRequiredPropertyOwner(out JsonObject root, path);
        Assert.True(owner.Remove(GetPropertyName(path)));

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(
                Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Equal($"{path} is required.", exception.Message);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("schemaVersion")]
    [InlineData("scheduler.options")]
    [InlineData("instrumentation.assemblies")]
    [InlineData("decisions")]
    [InlineData("raceSchedulingPoints")]
    [InlineData("diagnostics")]
    [InlineData("diagnostics.operations")]
    [InlineData("diagnostics.resources")]
    [InlineData("diagnostics.resources[0].waiters")]
    [InlineData("diagnostics.pendingTimers")]
    [InlineData("diagnostics.deadlockCycles")]
    [InlineData("diagnostics.deadlockCycles[0].edges")]
    public void NullRequiredWirePropertyIsRejected(string path)
    {
        JsonObject owner = GetRequiredPropertyOwner(out JsonObject root, path);
        owner[GetPropertyName(path)] = null;

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(
                Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Equal($"{path} is required.", exception.Message);
    }

    [Fact]
    public void OversizedDocumentIsRejectedBeforeParsing()
    {
        byte[] content = new byte[ReplayArtifactLimits.MaxDocumentBytes + 1];

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(content));

        Assert.Contains("limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonContiguousDecisionStreamIsRejected()
    {
        ReplayArtifact artifact = CreateArtifact() with
        {
            Decisions =
            [
                CreateDecision(0),
                CreateDecision(2),
            ],
        };

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Serialize(artifact));

        Assert.Contains("contiguous", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOutcomeKindIsRejected()
    {
        ReplayArtifact artifact = CreateArtifact() with
        {
            Outcome = new ReplayOutcome { Kind = (ReplayTerminationKind)999 },
        };

        Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Serialize(artifact));
    }

    [Fact]
    public void CompleteArtifactGraphSerializeThenDeserializeRoundTrips()
    {
        ReplayArtifact expected = CreateCompleteArtifact();

        byte[] serialized = ReplayArtifactSerializer.Serialize(expected);
        ReplayArtifact actual = ReplayArtifactSerializer.Deserialize(serialized);

        Assert.Equivalent(expected, actual, strict: true);
        Assert.Equal(serialized, ReplayArtifactSerializer.Serialize(actual));
        Assert.Equal("resource wait", actual.Diagnostics.Resources[0].Waiters[0].Reason);
        Assert.Equal("Tests.Counter::Write", actual.Diagnostics.Race!.Second.Member);
    }

    [Theory]
    [InlineData("scheduler.options", "scheduler.options['alpha']")]
    [InlineData("instrumentation.assemblies", "instrumentation.assemblies[0]")]
    [InlineData("decisions", "decisions[0]")]
    [InlineData("raceSchedulingPoints", "raceSchedulingPoints[0]")]
    [InlineData("diagnostics.operations", "diagnostics.operations[0]")]
    [InlineData("diagnostics.resources", "diagnostics.resources[0]")]
    [InlineData("diagnostics.resources[].waiters", "diagnostics.resources[0].waiters[0]")]
    [InlineData("diagnostics.pendingTimers", "diagnostics.pendingTimers[0]")]
    [InlineData("diagnostics.deadlockCycles", "diagnostics.deadlockCycles[0]")]
    [InlineData("diagnostics.deadlockCycles[].edges", "diagnostics.deadlockCycles[0].edges[0]")]
    public void NullCollectionElementIsRejectedDuringSerialization(
        string collection,
        string expectedPath)
    {
        ReplayArtifact artifact = CreateArtifactWithNullCollectionElement(collection);

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Serialize(artifact));

        Assert.Equal($"{expectedPath} cannot be null.", exception.Message);
    }

    [Theory]
    [InlineData("scheduler.options", "scheduler.options['alpha']")]
    [InlineData("instrumentation.assemblies", "instrumentation.assemblies[0]")]
    [InlineData("decisions", "decisions[0]")]
    [InlineData("raceSchedulingPoints", "raceSchedulingPoints[0]")]
    [InlineData("diagnostics.operations", "diagnostics.operations[0]")]
    [InlineData("diagnostics.resources", "diagnostics.resources[0]")]
    [InlineData("diagnostics.resources[].waiters", "diagnostics.resources[0].waiters[0]")]
    [InlineData("diagnostics.pendingTimers", "diagnostics.pendingTimers[0]")]
    [InlineData("diagnostics.deadlockCycles", "diagnostics.deadlockCycles[0]")]
    [InlineData("diagnostics.deadlockCycles[].edges", "diagnostics.deadlockCycles[0].edges[0]")]
    public void NullCollectionElementIsRejectedDuringDeserialization(
        string collection,
        string expectedPath)
    {
        byte[] json = CreateJsonWithNullCollectionElement(collection);

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(json));

        Assert.Equal($"{expectedPath} cannot be null.", exception.Message);
    }

    [Theory]
    [InlineData("outcome.kind", "outcome.kind")]
    [InlineData("decisions[].domain", "decisions[0].domain")]
    [InlineData("decisions[].kind", "decisions[0].kind")]
    [InlineData("raceSchedulingPoints[].kind", "raceSchedulingPoints[0].kind")]
    public void UndefinedEnumValueIsRejectedDuringSerialization(
        string field,
        string expectedPath)
    {
        ReplayArtifact artifact = CreateArtifactWithUndefinedEnum(field);

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Serialize(artifact));

        Assert.Equal($"{expectedPath} value '999' is not defined.", exception.Message);
    }

    [Theory]
    [InlineData("outcome.kind", false)]
    [InlineData("outcome.kind", true)]
    [InlineData("decisions[].domain", false)]
    [InlineData("decisions[].domain", true)]
    [InlineData("decisions[].kind", false)]
    [InlineData("decisions[].kind", true)]
    [InlineData("raceSchedulingPoints[].kind", false)]
    [InlineData("raceSchedulingPoints[].kind", true)]
    public void UndefinedEnumJsonValueIsRejectedDuringDeserialization(
        string field,
        bool useIntegerValue)
    {
        byte[] json = CreateJsonWithUndefinedEnum(field, useIntegerValue);

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(json));

        Assert.Equal("Replay artifact JSON is malformed.", exception.Message);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("\"Completed, Faulted\"")]
    [InlineData("\"completed\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("\"Undefined\"")]
    [InlineData("\" Completed\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void InvalidReplayTerminationKindJsonTokenIsRejected(string jsonToken)
    {
        byte[] json = CreateJsonWithOutcomeKindToken(jsonToken);

        ReplayArtifactFormatException exception = Assert.Throws<ReplayArtifactFormatException>(
            () => ReplayArtifactSerializer.Deserialize(json));

        Assert.Equal("Replay artifact JSON is malformed.", exception.Message);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData(ReplayTerminationKind.Completed)]
    [InlineData(ReplayTerminationKind.Faulted)]
    [InlineData(ReplayTerminationKind.Canceled)]
    [InlineData(ReplayTerminationKind.Deadlocked)]
    [InlineData(ReplayTerminationKind.RaceDetected)]
    [InlineData(ReplayTerminationKind.BoundExceeded)]
    [InlineData(ReplayTerminationKind.Aborted)]
    public void ReplayTerminationKindSerializeThenDeserializeRoundTrips(ReplayTerminationKind kind)
    {
        ReplayArtifact artifact = CreateCompleteArtifact();
        artifact = artifact with
        {
            Outcome = artifact.Outcome with { Kind = kind },
        };

        ReplayArtifact actual = ReplayArtifactSerializer.Deserialize(
            ReplayArtifactSerializer.Serialize(artifact));

        Assert.Equal(kind, actual.Outcome.Kind);
        Assert.Equal(artifact.Outcome.FailureIdentity, actual.Outcome.FailureIdentity);
    }

    [Theory]
    [InlineData(SimulationSeedDomain.Scheduler)]
    [InlineData(SimulationSeedDomain.Network)]
    [InlineData(SimulationSeedDomain.Application)]
    [InlineData(SimulationSeedDomain.Identity)]
    [InlineData(SimulationSeedDomain.Buggify)]
    [InlineData(SimulationSeedDomain.Model)]
    public void SimulationSeedDomainSerializeThenDeserializeRoundTrips(SimulationSeedDomain domain)
    {
        ReplayArtifact artifact = CreateCompleteArtifact();
        artifact = artifact with
        {
            Decisions = [artifact.Decisions[0] with { Domain = domain }],
        };

        ReplayArtifact actual = ReplayArtifactSerializer.Deserialize(
            ReplayArtifactSerializer.Serialize(artifact));

        Assert.Equal(domain, actual.Decisions[0].Domain);
        Assert.Equal(artifact.Decisions[0].SelectedResult, actual.Decisions[0].SelectedResult);
    }

    [Theory]
    [InlineData(SimulationDecisionKind.RandomDraw)]
    [InlineData(SimulationDecisionKind.Choice)]
    [InlineData(SimulationDecisionKind.SchedulingOrder)]
    [InlineData(SimulationDecisionKind.ResourceWinner)]
    [InlineData(SimulationDecisionKind.NetworkBehavior)]
    [InlineData(SimulationDecisionKind.FaultActivation)]
    [InlineData(SimulationDecisionKind.Custom)]
    public void SimulationDecisionKindSerializeThenDeserializeRoundTrips(SimulationDecisionKind kind)
    {
        ReplayArtifact artifact = CreateCompleteArtifact();
        artifact = artifact with
        {
            Decisions = [artifact.Decisions[0] with { Kind = kind }],
        };

        ReplayArtifact actual = ReplayArtifactSerializer.Deserialize(
            ReplayArtifactSerializer.Serialize(artifact));

        Assert.Equal(kind, actual.Decisions[0].Kind);
        Assert.Equal(artifact.Decisions[0].SourceId, actual.Decisions[0].SourceId);
    }

    [Theory]
    [InlineData(RaceAccessKind.Read)]
    [InlineData(RaceAccessKind.Write)]
    [InlineData(RaceAccessKind.ControlFlow)]
    [InlineData(RaceAccessKind.UntrackedMemory)]
    public void RaceAccessKindSerializeThenDeserializeRoundTrips(RaceAccessKind kind)
    {
        ReplayArtifact artifact = CreateCompleteArtifact();
        artifact = artifact with
        {
            RaceSchedulingPoints = [artifact.RaceSchedulingPoints[0] with { Kind = kind }],
        };

        ReplayArtifact actual = ReplayArtifactSerializer.Deserialize(
            ReplayArtifactSerializer.Serialize(artifact));

        Assert.Equal(kind, actual.RaceSchedulingPoints[0].Kind);
        Assert.Equal(artifact.RaceSchedulingPoints[0].Location, actual.RaceSchedulingPoints[0].Location);
    }

    [Fact]
    public void CompatibilityMismatchFailsBeforeReplay()
    {
        ReplayArtifact artifact = CreateArtifact();
        ReplayCompatibilityRequirements requirements = ReplayCompatibilityRequirements.Current() with
        {
            RuntimeCompatibility = ".NETCoreApp,Version=v99.0",
        };

        ReplayCompatibilityException exception = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayCompatibility.Validate(artifact, requirements));

        Assert.Contains("runtime compatibility mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstrumentationHashMismatchFailsBeforeReplay()
    {
        ReplayArtifact artifact = CreateArtifact() with { Instrumentation = CreateInstrumentation('a') };
        ReplayCompatibilityRequirements requirements = ReplayCompatibilityRequirements.Current() with
        {
            Instrumentation = CreateInstrumentation('b'),
        };

        ReplayCompatibilityException exception = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayCompatibility.Validate(artifact, requirements));

        Assert.Contains("manifest hash mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOptionalPropertiesAreForwardCompatibleWithinSchema()
    {
        string json = ReplayArtifactSerializer.ToJson(CreateArtifact());
        string extended = json.Insert(json.Length - 1, ",\"futureOptional\":{\"value\":1}");

        ReplayArtifact artifact = ReplayArtifactSerializer.Deserialize(Encoding.UTF8.GetBytes(extended));

        Assert.Equal(ReplayArtifact.CurrentSchemaVersion, artifact.SchemaVersion);
    }

    [Fact]
    public void AbortedOutcomeIsRejectedForReplay()
    {
        ReplayArtifact artifact = CreateArtifact() with
        {
            Outcome = new ReplayOutcome { Kind = ReplayTerminationKind.Aborted },
        };

        Assert.Throws<ReplayCompatibilityException>(
            () => ReplayCompatibility.Validate(artifact, ReplayCompatibilityRequirements.Current()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ReplayArtifact CreateArtifact() => new()
    {
        SimulationSeed = 42,
        Scheduler = new ReplaySchedulerConfiguration
        {
            Strategy = "seeded-random",
            ScheduleSeed = 17,
        },
        Environment = ReplayCompatibility.CaptureEnvironment(),
        Decisions =
        [
            CreateDecision(0),
        ],
        RaceSchedulingPoints =
        [
            new ReplayRaceSchedulingPoint
            {
                Sequence = 1,
                OperationId = 2,
                Kind = RaceAccessKind.Write,
                Location = "field:Counter",
                Member = "Tests.Counter::Increment",
                ILOffset = 4,
                SourceFile = null,
                SourceLine = -1,
            },
        ],
        Outcome = new ReplayOutcome
        {
            Kind = ReplayTerminationKind.Faulted,
            FailureIdentity = "System.InvalidOperationException",
            Diagnostic = "known failure",
        },
    };

    private static ReplayDecision CreateDecision(long sequence) => new()
    {
        Sequence = sequence,
        Domain = SimulationSeedDomain.Scheduler,
        Kind = SimulationDecisionKind.SchedulingOrder,
        SourceId = "seeded-random",
        InputMetadata = "1,2",
        SelectedResult = "2",
        NodeId = null,
        LogicalExecutionId = 0,
    };

    private static ReplayArtifact CreateCompleteArtifact()
    {
        ReplayArtifact artifact = CreateArtifact();
        return artifact with
        {
            Scheduler = artifact.Scheduler with
            {
                Options = new Dictionary<string, string>
                {
                    ["alpha"] = "first",
                    ["zeta"] = "last",
                },
            },
            Instrumentation = CreateInstrumentation('a'),
            Diagnostics = new ReplayDiagnosticSnapshot
            {
                Liveness = "Deadlocked",
                VirtualTimeTicks = 1234,
                Operations =
                [
                    new ReplayOperationDiagnostic
                    {
                        Id = 1,
                        ParentId = 0,
                        State = "Waiting",
                        Node = "node-a",
                        Description = "worker",
                        WaitReason = "resource wait",
                    },
                ],
                Resources =
                [
                    new ReplayResourceDiagnostic
                    {
                        Id = 10,
                        Kind = "Semaphore",
                        Name = "gate",
                        OwnerId = 1,
                        Waiters =
                        [
                            new ReplayWaiterDiagnostic
                            {
                                OperationId = 2,
                                EnqueueSequence = 7,
                                TimeoutTicks = 999,
                                Reason = "resource wait",
                            },
                        ],
                    },
                ],
                PendingTimers =
                [
                    new ReplayTimerDiagnostic
                    {
                        DueTicks = 2000,
                        Sequence = 8,
                        OperationId = 2,
                        ResourceId = 10,
                    },
                ],
                DeadlockCycles =
                [
                    new ReplayDeadlockCycleDiagnostic
                    {
                        Edges =
                        [
                            new ReplayDeadlockEdgeDiagnostic
                            {
                                OperationId = 2,
                                ResourceId = 10,
                                OwnerId = 1,
                                EnqueueSequence = 7,
                            },
                        ],
                    },
                ],
                Race = new ReplayRacePairDiagnostic
                {
                    First = CreateRaceAccess("Read"),
                    Second = CreateRaceAccess("Write") with
                    {
                        OperationId = 2,
                        Member = "Tests.Counter::Write",
                        ILOffset = 8,
                    },
                },
            },
        };
    }

    private static ReplayRaceAccessDiagnostic CreateRaceAccess(string kind) => new()
    {
        OperationId = 1,
        Kind = kind,
        Location = "field:Counter",
        Member = "Tests.Counter::Read",
        ILOffset = 4,
        SourceFile = "Counter.cs",
        SourceLine = 12,
    };

    private static ReplayArtifact CreateArtifactWithNullCollectionElement(string collection)
    {
        ReplayArtifact artifact = CreateCompleteArtifact();
        return collection switch
        {
            "scheduler.options" => artifact with
            {
                Scheduler = artifact.Scheduler with
                {
                    Options = new Dictionary<string, string> { ["alpha"] = null! },
                },
            },
            "instrumentation.assemblies" => artifact with
            {
                Instrumentation = artifact.Instrumentation! with { Assemblies = [null!] },
            },
            "decisions" => artifact with { Decisions = [null!] },
            "raceSchedulingPoints" => artifact with { RaceSchedulingPoints = [null!] },
            "diagnostics.operations" => artifact with
            {
                Diagnostics = artifact.Diagnostics with { Operations = [null!] },
            },
            "diagnostics.resources" => artifact with
            {
                Diagnostics = artifact.Diagnostics with { Resources = [null!] },
            },
            "diagnostics.resources[].waiters" => artifact with
            {
                Diagnostics = artifact.Diagnostics with
                {
                    Resources =
                    [
                        artifact.Diagnostics.Resources[0] with { Waiters = [null!] },
                    ],
                },
            },
            "diagnostics.pendingTimers" => artifact with
            {
                Diagnostics = artifact.Diagnostics with { PendingTimers = [null!] },
            },
            "diagnostics.deadlockCycles" => artifact with
            {
                Diagnostics = artifact.Diagnostics with { DeadlockCycles = [null!] },
            },
            "diagnostics.deadlockCycles[].edges" => artifact with
            {
                Diagnostics = artifact.Diagnostics with
                {
                    DeadlockCycles =
                    [
                        artifact.Diagnostics.DeadlockCycles[0] with { Edges = [null!] },
                    ],
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(collection)),
        };
    }

    private static byte[] CreateJsonWithNullCollectionElement(string collection)
    {
        JsonNode root = JsonNode.Parse(ReplayArtifactSerializer.ToJson(CreateCompleteArtifact()))!;
        JsonObject diagnostics = root["diagnostics"]!.AsObject();

        switch (collection)
        {
            case "scheduler.options":
                root["scheduler"]!["options"]!.AsObject()["alpha"] = null;
                break;
            case "instrumentation.assemblies":
                root["instrumentation"]!["assemblies"]!.AsArray()[0] = null;
                break;
            case "decisions":
                root["decisions"]!.AsArray()[0] = null;
                break;
            case "raceSchedulingPoints":
                root["raceSchedulingPoints"]!.AsArray()[0] = null;
                break;
            case "diagnostics.operations":
                diagnostics["operations"]!.AsArray()[0] = null;
                break;
            case "diagnostics.resources":
                diagnostics["resources"]!.AsArray()[0] = null;
                break;
            case "diagnostics.resources[].waiters":
                diagnostics["resources"]!.AsArray()[0]!["waiters"]!.AsArray()[0] = null;
                break;
            case "diagnostics.pendingTimers":
                diagnostics["pendingTimers"]!.AsArray()[0] = null;
                break;
            case "diagnostics.deadlockCycles":
                diagnostics["deadlockCycles"]!.AsArray()[0] = null;
                break;
            case "diagnostics.deadlockCycles[].edges":
                diagnostics["deadlockCycles"]!.AsArray()[0]!["edges"]!.AsArray()[0] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(collection));
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static ReplayArtifact CreateArtifactWithUndefinedEnum(string field)
    {
        ReplayArtifact artifact = CreateCompleteArtifact();
        return field switch
        {
            "outcome.kind" => artifact with
            {
                Outcome = artifact.Outcome with { Kind = (ReplayTerminationKind)999 },
            },
            "decisions[].domain" => artifact with
            {
                Decisions = [artifact.Decisions[0] with { Domain = (SimulationSeedDomain)999 }],
            },
            "decisions[].kind" => artifact with
            {
                Decisions = [artifact.Decisions[0] with { Kind = (SimulationDecisionKind)999 }],
            },
            "raceSchedulingPoints[].kind" => artifact with
            {
                RaceSchedulingPoints =
                [
                    artifact.RaceSchedulingPoints[0] with { Kind = (RaceAccessKind)999 },
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    private static byte[] CreateJsonWithUndefinedEnum(string field, bool useIntegerValue)
    {
        JsonNode root = JsonNode.Parse(ReplayArtifactSerializer.ToJson(CreateCompleteArtifact()))!;
        JsonNode value = useIntegerValue ? JsonValue.Create(999) : JsonValue.Create("Undefined");

        switch (field)
        {
            case "outcome.kind":
                root["outcome"]!.AsObject()["kind"] = value;
                break;
            case "decisions[].domain":
                root["decisions"]!.AsArray()[0]!.AsObject()["domain"] = value;
                break;
            case "decisions[].kind":
                root["decisions"]!.AsArray()[0]!.AsObject()["kind"] = value;
                break;
            case "raceSchedulingPoints[].kind":
                root["raceSchedulingPoints"]!.AsArray()[0]!.AsObject()["kind"] = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static byte[] CreateJsonWithOutcomeKindToken(string jsonToken)
    {
        JsonNode root = JsonNode.Parse(ReplayArtifactSerializer.ToJson(CreateCompleteArtifact()))!;
        root["outcome"]!.AsObject()["kind"] = JsonNode.Parse(jsonToken);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static JsonObject GetRequiredPropertyOwner(
        out JsonObject root,
        string path)
    {
        root = JsonNode.Parse(
            ReplayArtifactSerializer.ToJson(CreateCompleteArtifact()))!.AsObject();
        return path switch
        {
            "scheduler.options" => root["scheduler"]!.AsObject(),
            "instrumentation.assemblies" => root["instrumentation"]!.AsObject(),
            "diagnostics.operations"
                or "diagnostics.resources"
                or "diagnostics.pendingTimers"
                or "diagnostics.deadlockCycles" => root["diagnostics"]!.AsObject(),
            "diagnostics.resources[0].waiters" =>
                root["diagnostics"]!["resources"]!.AsArray()[0]!.AsObject(),
            "diagnostics.deadlockCycles[0].edges" =>
                root["diagnostics"]!["deadlockCycles"]!.AsArray()[0]!.AsObject(),
            _ => root,
        };
    }

    private static string GetPropertyName(string path) =>
        path[(path.LastIndexOf('.') + 1)..];

    private static ReplayInstrumentationIdentity CreateInstrumentation(char hash) => new()
    {
        ManifestId = "manifest",
        ManifestSha256 = new string(hash, 64),
        EngineVersion = "1.0.0",
        RuleSetId = "rules",
        RuleSetVersion = "1",
        RuleSetSignature = new string('c', 64),
        Mode = "RaceExploration",
        Assemblies =
        [
            new ReplayAssemblyIdentity
            {
                Name = "app.dll",
                Sha256 = new string('d', 64),
                RuntimeCompatibility = ReplayCompatibility.CurrentRuntimeCompatibility,
            },
        ],
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (Orleans.slnx).");
    }
}
