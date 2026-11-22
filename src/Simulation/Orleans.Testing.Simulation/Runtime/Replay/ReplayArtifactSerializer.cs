using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Racing;

namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>Reads and writes bounded replay artifacts using stable canonical UTF-8 JSON.</summary>
public static class ReplayArtifactSerializer
{
    private const int MaxJsonDepth = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = MaxJsonDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters =
        {
            new StrictJsonStringEnumConverter<ReplayTerminationKind>(),
            new StrictJsonStringEnumConverter<SimulationSeedDomain>(),
            new StrictJsonStringEnumConverter<SimulationDecisionKind>(),
            new StrictJsonStringEnumConverter<RaceAccessKind>(),
        },
    };

    /// <summary>Serializes an artifact to canonical compact UTF-8 JSON.</summary>
    public static byte[] Serialize(ReplayArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Validate(artifact);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteArtifact(writer, artifact);
        }

        if (stream.Length > ReplayArtifactLimits.MaxDocumentBytes)
        {
            throw new ReplayArtifactFormatException(
                $"Replay artifact is {stream.Length} bytes; the limit is {ReplayArtifactLimits.MaxDocumentBytes} bytes.");
        }

        return stream.ToArray();
    }

    /// <summary>Serializes an artifact to canonical compact JSON text.</summary>
    public static string ToJson(ReplayArtifact artifact) => Encoding.UTF8.GetString(Serialize(artifact));

    /// <summary>Parses and validates a replay artifact from UTF-8 JSON.</summary>
    public static ReplayArtifact Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > ReplayArtifactLimits.MaxDocumentBytes)
        {
            throw new ReplayArtifactFormatException(
                $"Replay artifact is {utf8Json.Length} bytes; the limit is {ReplayArtifactLimits.MaxDocumentBytes} bytes.");
        }

        try
        {
            ValidateRequiredWireProperties(utf8Json);
            ReplayArtifact? artifact = JsonSerializer.Deserialize<ReplayArtifact>(utf8Json, SerializerOptions);
            if (artifact is null)
            {
                throw new ReplayArtifactFormatException("Replay artifact JSON contained no document.");
            }

            Validate(artifact);
            return artifact;
        }
        catch (JsonException exception)
        {
            throw new ReplayArtifactFormatException("Replay artifact JSON is malformed.", exception);
        }
    }

    private static void ValidateRequiredWireProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxJsonDepth,
        });
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonElement root = document.RootElement;
        _ = RequireWireProperty(root, "format", "format");
        _ = RequireWireProperty(root, "schemaVersion", "schemaVersion");
        _ = RequireWireProperty(root, "decisions", "decisions");
        _ = RequireWireProperty(root, "raceSchedulingPoints", "raceSchedulingPoints");

        if (TryGetObjectProperty(root, "scheduler", out JsonElement scheduler))
        {
            _ = RequireWireProperty(scheduler, "options", "scheduler.options");
        }

        if (TryGetObjectProperty(root, "instrumentation", out JsonElement instrumentation))
        {
            _ = RequireWireProperty(
                instrumentation,
                "assemblies",
                "instrumentation.assemblies");
        }

        JsonElement diagnostics = RequireWireProperty(root, "diagnostics", "diagnostics");
        if (diagnostics.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _ = RequireWireProperty(
            diagnostics,
            "operations",
            "diagnostics.operations");
        JsonElement resources = RequireWireProperty(
            diagnostics,
            "resources",
            "diagnostics.resources");
        _ = RequireWireProperty(
            diagnostics,
            "pendingTimers",
            "diagnostics.pendingTimers");
        JsonElement deadlockCycles = RequireWireProperty(
            diagnostics,
            "deadlockCycles",
            "diagnostics.deadlockCycles");

        RequireCollectionProperties(
            resources,
            "waiters",
            "diagnostics.resources");
        RequireCollectionProperties(
            deadlockCycles,
            "edges",
            "diagnostics.deadlockCycles");
    }

    private static JsonElement RequireWireProperty(
        JsonElement owner,
        string propertyName,
        string path)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            throw new ReplayArtifactFormatException($"{path} is required.");
        }

        return value;
    }

    private static bool TryGetObjectProperty(
        JsonElement owner,
        string propertyName,
        out JsonElement value)
    {
        return owner.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static void RequireCollectionProperties(
        JsonElement collection,
        string propertyName,
        string collectionPath)
    {
        if (collection.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (JsonElement element in collection.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                _ = RequireWireProperty(
                    element,
                    propertyName,
                    $"{collectionPath}[{index}].{propertyName}");
            }

            index++;
        }
    }

    /// <summary>Reads a bounded replay artifact from a file.</summary>
    public static ReplayArtifact Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Replay artifact was not found.", path);
        }

        if (info.Length > ReplayArtifactLimits.MaxDocumentBytes)
        {
            throw new ReplayArtifactFormatException(
                $"Replay artifact is {info.Length} bytes; the limit is {ReplayArtifactLimits.MaxDocumentBytes} bytes.");
        }

        return Deserialize(File.ReadAllBytes(path));
    }

    /// <summary>Writes canonical JSON to a file, replacing an existing artifact atomically.</summary>
    public static void Write(string path, ReplayArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = Serialize(artifact);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string? temporaryPath = null;
        try
        {
            FileStream? stream = null;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                string candidate = fullPath + "."
                    + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
                    + ".tmp";
                try
                {
                    stream = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    temporaryPath = candidate;
                    break;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                }
            }

            if (stream is null || temporaryPath is null)
            {
                throw new IOException(
                    $"Could not create a unique temporary file for replay artifact '{fullPath}' after 16 attempts.");
            }

            using (stream)
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Computes the artifact id as lower-case SHA-256 of its canonical bytes.</summary>
    public static string ComputeId(ReplayArtifact artifact) =>
        Convert.ToHexStringLower(SHA256.HashData(Serialize(artifact)));

    private static void Validate(ReplayArtifact artifact)
    {
        RequireString(artifact.Format, nameof(artifact.Format));
        if (!string.Equals(artifact.Format, ReplayArtifact.SchemaName, StringComparison.Ordinal))
        {
            throw new ReplayArtifactFormatException(
                $"Unsupported replay format '{artifact.Format}'. Expected '{ReplayArtifact.SchemaName}'.");
        }

        if (artifact.SchemaVersion != ReplayArtifact.CurrentSchemaVersion)
        {
            throw new ReplayArtifactFormatException(
                $"Unsupported replay schema version {artifact.SchemaVersion}. This runtime supports version {ReplayArtifact.CurrentSchemaVersion}. " +
                "Readers ignore unknown optional properties within a supported version; incompatible changes require a new schema version.");
        }

        if (artifact.Scheduler is null)
        {
            throw new ReplayArtifactFormatException("scheduler is required.");
        }
        RequireString(artifact.Scheduler.Strategy, "scheduler.strategy");
        if (artifact.Scheduler.Options is null)
        {
            throw new ReplayArtifactFormatException("scheduler.options is required.");
        }

        if (artifact.Scheduler.Options.Count > ReplayArtifactLimits.MaxSchedulerOptions)
        {
            throw new ReplayArtifactFormatException(
                $"Scheduler option count {artifact.Scheduler.Options.Count} exceeds {ReplayArtifactLimits.MaxSchedulerOptions}.");
        }

        foreach (KeyValuePair<string, string> option in artifact.Scheduler.Options)
        {
            string? key = option.Key;
            string? value = option.Value;
            if (key is null)
            {
                throw new ReplayArtifactFormatException("scheduler.options contains a null key.");
            }

            RequireString(key, "scheduler.options key");
            if (value is null)
            {
                throw new ReplayArtifactFormatException($"scheduler.options['{key}'] cannot be null.");
            }

            RequireString(value, $"scheduler.options['{key}']");
        }

        if (artifact.Environment is null)
        {
            throw new ReplayArtifactFormatException("environment is required.");
        }
        RequireString(artifact.Environment.ClockworkVersion, "environment.clockworkVersion");
        RequireString(artifact.Environment.RuntimeCompatibility, "environment.runtimeCompatibility");
        RequireString(artifact.Environment.ProcessArchitecture, "environment.processArchitecture");
        RequireString(artifact.Environment.OperatingSystem, "environment.operatingSystem");

        if (artifact.Instrumentation is { } instrumentation)
        {
            RequireString(instrumentation.ManifestId, "instrumentation.manifestId");
            RequireSha256(instrumentation.ManifestSha256, "instrumentation.manifestSha256");
            RequireString(instrumentation.EngineVersion, "instrumentation.engineVersion");
            RequireString(instrumentation.RuleSetId, "instrumentation.ruleSetId");
            RequireString(instrumentation.RuleSetVersion, "instrumentation.ruleSetVersion");
            RequireString(instrumentation.RuleSetSignature, "instrumentation.ruleSetSignature");
            RequireString(instrumentation.Mode, "instrumentation.mode");
            if (instrumentation.Assemblies is null)
            {
                throw new ReplayArtifactFormatException("instrumentation.assemblies is required.");
            }

            if (instrumentation.Assemblies.Count > ReplayArtifactLimits.MaxAssemblies)
            {
                throw new ReplayArtifactFormatException(
                    $"Instrumentation assembly count {instrumentation.Assemblies.Count} exceeds {ReplayArtifactLimits.MaxAssemblies}.");
            }

            string? previousName = null;
            for (var index = 0; index < instrumentation.Assemblies.Count; index++)
            {
                string path = $"instrumentation.assemblies[{index}]";
                ReplayAssemblyIdentity assembly = RequireElement(instrumentation.Assemblies[index], path);
                RequireString(assembly.Name, $"{path}.name");
                RequireSha256(assembly.Sha256, $"{path}.sha256");
                if (previousName is not null && StringComparer.Ordinal.Compare(previousName, assembly.Name) >= 0)
                {
                    throw new ReplayArtifactFormatException(
                        "Instrumentation assemblies must be strictly ordered by unique ordinal name.");
                }

                previousName = assembly.Name;
                RequireOptionalString(assembly.RuntimeCompatibility, $"{path}.runtimeCompatibility");
            }
        }

        if (artifact.Decisions is null)
        {
            throw new ReplayArtifactFormatException("decisions is required.");
        }

        if (artifact.Decisions.Count > ReplayArtifactLimits.MaxDecisions)
        {
            throw new ReplayArtifactFormatException(
                $"Decision count {artifact.Decisions.Count} exceeds {ReplayArtifactLimits.MaxDecisions}.");
        }

        for (var index = 0; index < artifact.Decisions.Count; index++)
        {
            string path = $"decisions[{index}]";
            ReplayDecision decision = RequireElement(artifact.Decisions[index], path);
            if (decision.Sequence != index)
            {
                throw new ReplayArtifactFormatException(
                    $"Decision sequence must be contiguous from zero; index {index} contains sequence {decision.Sequence}.");
            }

            RequireDefinedEnum(decision.Domain, $"{path}.domain");
            RequireDefinedEnum(decision.Kind, $"{path}.kind");
            RequireOptionalString(decision.SourceId, $"{path}.sourceId");
            RequireOptionalString(decision.InputMetadata, $"{path}.inputMetadata");
            RequireString(decision.SelectedResult, $"{path}.selectedResult", allowEmpty: true);
            RequireOptionalString(decision.NodeId, $"{path}.nodeId");
        }

        if (artifact.RaceSchedulingPoints is null)
        {
            throw new ReplayArtifactFormatException("raceSchedulingPoints is required.");
        }

        if (artifact.RaceSchedulingPoints.Count > ReplayArtifactLimits.MaxRaceSchedulingPoints)
        {
            throw new ReplayArtifactFormatException(
                $"Race scheduling point count {artifact.RaceSchedulingPoints.Count} exceeds {ReplayArtifactLimits.MaxRaceSchedulingPoints}.");
        }

        long previousSequence = 0;
        for (var index = 0; index < artifact.RaceSchedulingPoints.Count; index++)
        {
            string path = $"raceSchedulingPoints[{index}]";
            ReplayRaceSchedulingPoint point = RequireElement(artifact.RaceSchedulingPoints[index], path);
            if (point.Sequence <= previousSequence)
            {
                throw new ReplayArtifactFormatException("Race scheduling point sequences must be strictly increasing.");
            }

            previousSequence = point.Sequence;
            RequireDefinedEnum(point.Kind, $"{path}.kind");
            RequireString(point.Location, $"{path}.location");
            RequireString(point.Member, $"{path}.member");
            RequireOptionalString(point.SourceFile, $"{path}.sourceFile");
        }

        if (artifact.Outcome is null)
        {
            throw new ReplayArtifactFormatException("outcome is required.");
        }
        RequireDefinedEnum(artifact.Outcome.Kind, "outcome.kind");

        RequireOptionalString(artifact.Outcome.FailureIdentity, "outcome.failureIdentity");
        RequireOptionalString(artifact.Outcome.Diagnostic, "outcome.diagnostic");

        if (artifact.Diagnostics is null)
        {
            throw new ReplayArtifactFormatException("diagnostics is required.");
        }

        RequireString(artifact.Diagnostics.Liveness, "diagnostics.liveness");
        if (artifact.Diagnostics.Operations is null)
        {
            throw new ReplayArtifactFormatException("diagnostics.operations is required.");
        }
        if (artifact.Diagnostics.Resources is null)
        {
            throw new ReplayArtifactFormatException("diagnostics.resources is required.");
        }
        if (artifact.Diagnostics.PendingTimers is null)
        {
            throw new ReplayArtifactFormatException("diagnostics.pendingTimers is required.");
        }
        if (artifact.Diagnostics.DeadlockCycles is null)
        {
            throw new ReplayArtifactFormatException("diagnostics.deadlockCycles is required.");
        }

        if (artifact.Diagnostics.Operations.Count > ReplayArtifactLimits.MaxDecisions ||
            artifact.Diagnostics.Resources.Count > ReplayArtifactLimits.MaxDecisions ||
            artifact.Diagnostics.PendingTimers.Count > ReplayArtifactLimits.MaxDecisions ||
            artifact.Diagnostics.DeadlockCycles.Count > ReplayArtifactLimits.MaxDecisions)
        {
            throw new ReplayArtifactFormatException("A diagnostic collection exceeds the replay artifact item limit.");
        }

        for (var operationIndex = 0; operationIndex < artifact.Diagnostics.Operations.Count; operationIndex++)
        {
            string path = $"diagnostics.operations[{operationIndex}]";
            ReplayOperationDiagnostic operation =
                RequireElement(artifact.Diagnostics.Operations[operationIndex], path);
            RequireString(operation.State, $"{path}.state");
            RequireOptionalString(operation.Node, $"{path}.node");
            RequireOptionalString(operation.Description, $"{path}.description");
            RequireOptionalString(operation.WaitReason, $"{path}.waitReason");
        }

        for (var resourceIndex = 0; resourceIndex < artifact.Diagnostics.Resources.Count; resourceIndex++)
        {
            string path = $"diagnostics.resources[{resourceIndex}]";
            ReplayResourceDiagnostic resource =
                RequireElement(artifact.Diagnostics.Resources[resourceIndex], path);
            RequireString(resource.Kind, $"{path}.kind");
            RequireOptionalString(resource.Name, $"{path}.name");
            if (resource.Waiters is null)
            {
                throw new ReplayArtifactFormatException($"{path}.waiters cannot be null.");
            }

            for (var waiterIndex = 0; waiterIndex < resource.Waiters.Count; waiterIndex++)
            {
                string waiterPath = $"{path}.waiters[{waiterIndex}]";
                ReplayWaiterDiagnostic waiter = RequireElement(resource.Waiters[waiterIndex], waiterPath);
                RequireOptionalString(waiter.Reason, $"{waiterPath}.reason");
            }
        }

        for (var timerIndex = 0; timerIndex < artifact.Diagnostics.PendingTimers.Count; timerIndex++)
        {
            RequireElement(
                artifact.Diagnostics.PendingTimers[timerIndex],
                $"diagnostics.pendingTimers[{timerIndex}]");
        }

        for (var cycleIndex = 0; cycleIndex < artifact.Diagnostics.DeadlockCycles.Count; cycleIndex++)
        {
            string path = $"diagnostics.deadlockCycles[{cycleIndex}]";
            ReplayDeadlockCycleDiagnostic cycle =
                RequireElement(artifact.Diagnostics.DeadlockCycles[cycleIndex], path);
            if (cycle.Edges is null)
            {
                throw new ReplayArtifactFormatException($"{path}.edges cannot be null.");
            }

            for (var edgeIndex = 0; edgeIndex < cycle.Edges.Count; edgeIndex++)
            {
                RequireElement(cycle.Edges[edgeIndex], $"{path}.edges[{edgeIndex}]");
            }
        }

        if (artifact.Diagnostics.Race is { } race)
        {
            ValidateRaceAccess(race.First, "diagnostics.race.first");
            ValidateRaceAccess(race.Second, "diagnostics.race.second");
        }
    }

    private static void ValidateRaceAccess(ReplayRaceAccessDiagnostic? access, string field)
    {
        if (access is null)
        {
            throw new ReplayArtifactFormatException($"{field} is required.");
        }

        RequireString(access.Kind, $"{field}.kind");
        RequireString(access.Location, $"{field}.location");
        RequireString(access.Member, $"{field}.member");
        RequireOptionalString(access.SourceFile, $"{field}.sourceFile");
    }

    private static T RequireElement<T>(T? value, string path)
        where T : class
    {
        if (value is null)
        {
            throw new ReplayArtifactFormatException($"{path} cannot be null.");
        }

        return value;
    }

    private static void RequireDefinedEnum<TEnum>(TEnum value, string field)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ReplayArtifactFormatException($"{field} value '{value}' is not defined.");
        }
    }

    private static void RequireSha256(string value, string field)
    {
        RequireString(value, field);
        if (value.Length != 64 || value.Any(static c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
        {
            throw new ReplayArtifactFormatException($"{field} must be a lower-case 64-character SHA-256 value.");
        }
    }

    private static void RequireOptionalString(string? value, string field)
    {
        if (value is not null)
        {
            RequireString(value, field, allowEmpty: true);
        }
    }

    private static void RequireString(string? value, string field, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            throw new ReplayArtifactFormatException($"{field} is required.");
        }

        if (value.Length > ReplayArtifactLimits.MaxStringLength)
        {
            throw new ReplayArtifactFormatException(
                $"{field} length {value.Length} exceeds {ReplayArtifactLimits.MaxStringLength}.");
        }
    }

    private static void WriteArtifact(Utf8JsonWriter writer, ReplayArtifact artifact)
    {
        writer.WriteStartObject();
        writer.WriteString("format", artifact.Format);
        writer.WriteNumber("schemaVersion", artifact.SchemaVersion);
        writer.WriteNumber("rootSeed", artifact.SimulationSeed);
        WriteScheduler(writer, artifact.Scheduler);
        WriteInstrumentation(writer, artifact.Instrumentation);
        WriteEnvironment(writer, artifact.Environment);
        WriteDecisions(writer, artifact.Decisions);
        WriteRaceSchedulingPoints(writer, artifact.RaceSchedulingPoints);
        WriteOutcome(writer, artifact.Outcome);
        WriteDiagnostics(writer, artifact.Diagnostics);
        writer.WriteEndObject();
    }

    private static void WriteScheduler(Utf8JsonWriter writer, ReplaySchedulerConfiguration scheduler)
    {
        writer.WritePropertyName("scheduler");
        writer.WriteStartObject();
        writer.WriteString("strategy", scheduler.Strategy);
        if (scheduler.ScheduleSeed is { } scheduleSeed)
        {
            writer.WriteNumber("scheduleSeed", scheduleSeed);
        }
        else
        {
            writer.WriteNull("scheduleSeed");
        }

        writer.WritePropertyName("options");
        writer.WriteStartObject();
        foreach ((string key, string value) in scheduler.Options.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteInstrumentation(Utf8JsonWriter writer, ReplayInstrumentationIdentity? instrumentation)
    {
        writer.WritePropertyName("instrumentation");
        if (instrumentation is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("manifestId", instrumentation.ManifestId);
        writer.WriteString("manifestSha256", instrumentation.ManifestSha256);
        writer.WriteString("engineVersion", instrumentation.EngineVersion);
        writer.WriteString("ruleSetId", instrumentation.RuleSetId);
        writer.WriteString("ruleSetVersion", instrumentation.RuleSetVersion);
        writer.WriteString("ruleSetSignature", instrumentation.RuleSetSignature);
        writer.WriteString("mode", instrumentation.Mode);
        writer.WritePropertyName("assemblies");
        writer.WriteStartArray();
        foreach (ReplayAssemblyIdentity assembly in instrumentation.Assemblies.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", assembly.Name);
            writer.WriteString("sha256", assembly.Sha256);
            WriteNullableString(writer, "runtimeCompatibility", assembly.RuntimeCompatibility);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEnvironment(Utf8JsonWriter writer, ReplayEnvironmentIdentity environment)
    {
        writer.WritePropertyName("environment");
        writer.WriteStartObject();
        writer.WriteString("clockworkVersion", environment.ClockworkVersion);
        writer.WriteString("runtimeCompatibility", environment.RuntimeCompatibility);
        writer.WriteString("processArchitecture", environment.ProcessArchitecture);
        writer.WriteString("operatingSystem", environment.OperatingSystem);
        writer.WriteEndObject();
    }

    private static void WriteDecisions(Utf8JsonWriter writer, IReadOnlyList<ReplayDecision> decisions)
    {
        writer.WritePropertyName("decisions");
        writer.WriteStartArray();
        foreach (ReplayDecision decision in decisions)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", decision.Sequence);
            writer.WriteString("domain", decision.Domain.ToString());
            writer.WriteString("kind", decision.Kind.ToString());
            WriteNullableString(writer, "sourceId", decision.SourceId);
            WriteNullableString(writer, "inputMetadata", decision.InputMetadata);
            writer.WriteString("selectedResult", decision.SelectedResult);
            WriteNullableString(writer, "nodeId", decision.NodeId);
            writer.WriteNumber("logicalExecutionId", decision.LogicalExecutionId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRaceSchedulingPoints(
        Utf8JsonWriter writer,
        IReadOnlyList<ReplayRaceSchedulingPoint> points)
    {
        writer.WritePropertyName("raceSchedulingPoints");
        writer.WriteStartArray();
        foreach (ReplayRaceSchedulingPoint point in points)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", point.Sequence);
            writer.WriteNumber("operationId", point.OperationId);
            writer.WriteString("kind", point.Kind.ToString());
            writer.WriteString("location", point.Location);
            writer.WriteString("member", point.Member);
            writer.WriteNumber("ilOffset", point.ILOffset);
            WriteNullableString(writer, "sourceFile", point.SourceFile);
            writer.WriteNumber("sourceLine", point.SourceLine);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOutcome(Utf8JsonWriter writer, ReplayOutcome outcome)
    {
        writer.WritePropertyName("outcome");
        writer.WriteStartObject();
        writer.WriteString("kind", outcome.Kind.ToString());
        WriteNullableString(writer, "failureIdentity", outcome.FailureIdentity);
        WriteNullableString(writer, "diagnostic", outcome.Diagnostic);
        writer.WriteEndObject();
    }

    private static void WriteDiagnostics(Utf8JsonWriter writer, ReplayDiagnosticSnapshot diagnostics)
    {
        writer.WritePropertyName("diagnostics");
        writer.WriteStartObject();
        writer.WriteString("liveness", diagnostics.Liveness);
        writer.WriteNumber("virtualTimeTicks", diagnostics.VirtualTimeTicks);
        writer.WritePropertyName("operations");
        writer.WriteStartArray();
        foreach (ReplayOperationDiagnostic operation in diagnostics.Operations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", operation.Id);
            writer.WriteNumber("parentId", operation.ParentId);
            writer.WriteString("state", operation.State);
            WriteNullableString(writer, "node", operation.Node);
            WriteNullableString(writer, "description", operation.Description);
            WriteNullableString(writer, "waitReason", operation.WaitReason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("resources");
        writer.WriteStartArray();
        foreach (ReplayResourceDiagnostic resource in diagnostics.Resources)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", resource.Id);
            writer.WriteString("kind", resource.Kind);
            WriteNullableString(writer, "name", resource.Name);
            if (resource.OwnerId is { } ownerId)
            {
                writer.WriteNumber("ownerId", ownerId);
            }
            else
            {
                writer.WriteNull("ownerId");
            }

            writer.WritePropertyName("waiters");
            writer.WriteStartArray();
            foreach (ReplayWaiterDiagnostic waiter in resource.Waiters)
            {
                writer.WriteStartObject();
                writer.WriteNumber("operationId", waiter.OperationId);
                writer.WriteNumber("enqueueSequence", waiter.EnqueueSequence);
                if (waiter.TimeoutTicks is { } timeoutTicks)
                {
                    writer.WriteNumber("timeoutTicks", timeoutTicks);
                }
                else
                {
                    writer.WriteNull("timeoutTicks");
                }

                WriteNullableString(writer, "reason", waiter.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("pendingTimers");
        writer.WriteStartArray();
        foreach (ReplayTimerDiagnostic timer in diagnostics.PendingTimers)
        {
            writer.WriteStartObject();
            writer.WriteNumber("dueTicks", timer.DueTicks);
            writer.WriteNumber("sequence", timer.Sequence);
            writer.WriteNumber("operationId", timer.OperationId);
            writer.WriteNumber("resourceId", timer.ResourceId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("deadlockCycles");
        writer.WriteStartArray();
        foreach (ReplayDeadlockCycleDiagnostic cycle in diagnostics.DeadlockCycles)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("edges");
            writer.WriteStartArray();
            foreach (ReplayDeadlockEdgeDiagnostic edge in cycle.Edges)
            {
                writer.WriteStartObject();
                writer.WriteNumber("operationId", edge.OperationId);
                writer.WriteNumber("resourceId", edge.ResourceId);
                writer.WriteNumber("ownerId", edge.OwnerId);
                writer.WriteNumber("enqueueSequence", edge.EnqueueSequence);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("race");
        if (diagnostics.Race is { } race)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("first");
            WriteRaceAccess(writer, race.First);
            writer.WritePropertyName("second");
            WriteRaceAccess(writer, race.Second);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }

    private static void WriteRaceAccess(Utf8JsonWriter writer, ReplayRaceAccessDiagnostic access)
    {
        writer.WriteStartObject();
        writer.WriteNumber("operationId", access.OperationId);
        writer.WriteString("kind", access.Kind);
        writer.WriteString("location", access.Location);
        writer.WriteString("member", access.Member);
        writer.WriteNumber("ilOffset", access.ILOffset);
        WriteNullableString(writer, "sourceFile", access.SourceFile);
        writer.WriteNumber("sourceLine", access.SourceLine);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}
