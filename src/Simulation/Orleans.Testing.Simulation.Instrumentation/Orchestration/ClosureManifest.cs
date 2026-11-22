using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Configuration;

namespace Orleans.Testing.Simulation.Instrumentation.Orchestration;

/// <summary>
/// A single assembly's entry in a <see cref="ClosureManifest"/>: its closure-relative path, what
/// happened to it, and the input/output content hashes for auditing and incremental reasoning.
/// </summary>
/// <param name="RelativePath">The assembly path relative to the closure root.</param>
/// <param name="WasRewritten">Whether the engine wrote rewritten IL.</param>
/// <param name="WasNoOp">Whether the assembly was a verified idempotent no-op.</param>
/// <param name="WasReSigned">Whether the staged output was re-signed.</param>
/// <param name="ReadyToRunStripped">Whether a ReadyToRun native image was stripped.</param>
/// <param name="InputSha256">The lower-hex SHA-256 of the input assembly bytes.</param>
/// <param name="OutputSha256">The lower-hex SHA-256 of the staged output bytes, or <see langword="null"/>.</param>
/// <param name="ErrorCount">The number of error-severity diagnostics for this assembly.</param>
public readonly record struct ClosureManifestEntry(
    string RelativePath,
    bool WasRewritten,
    bool WasNoOp,
    bool WasReSigned,
    bool ReadyToRunStripped,
    string? InputSha256,
    string? OutputSha256,
    int ErrorCount);

/// <summary>
/// A copied asset's entry in a <see cref="ClosureManifest"/>: its closure-relative path and the
/// lower-hex SHA-256 of the bytes copied into the staged closure.
/// </summary>
/// <param name="RelativePath">The asset path relative to the closure root.</param>
/// <param name="Sha256">The lower-hex SHA-256 of the staged asset bytes.</param>
public readonly record struct ClosureManifestCopiedAsset(string RelativePath, string Sha256);

/// <summary>
/// The deterministic, aggregate manifest for an entire instrumented application closure. It records
/// the engine and rule-set identity, the configuration and incremental keys, the entry assembly, the
/// per-assembly outcomes, and the assets copied verbatim. Like the per-assembly
/// <see cref="Manifest.InstrumentationManifest"/> it emits stable, sorted, timestamp-free JSON so two
/// runs over identical inputs produce byte-identical manifests.
/// </summary>
public sealed record ClosureManifest
{
    /// <summary>The closure-manifest schema version.</summary>
    public const int SchemaVersion = 3;

    /// <summary>Gets the producing engine's name.</summary>
    public string EngineName { get; init; } = "Clockwork.Instrumentation";

    /// <summary>Gets the producing engine's version.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>Gets the applied rule set's id.</summary>
    public required string RuleSetId { get; init; }

    /// <summary>Gets the applied rule set's version.</summary>
    public required string RuleSetVersion { get; init; }

    /// <summary>Gets the applied rule set's content signature.</summary>
    public required string RuleSetSignature { get; init; }

    /// <summary>Gets the effective configuration's signature.</summary>
    public required string ConfigurationSignature { get; init; }

    /// <summary>Gets the instrumentation granularity applied to this closure.</summary>
    public InstrumentationMode Mode { get; init; } = InstrumentationMode.Controlled;

    /// <summary>Gets the incremental cache key computed for the closure.</summary>
    public required string IncrementalKey { get; init; }

    /// <summary>Gets the entry assembly's closure-relative path, or <see langword="null"/> if none was detected.</summary>
    public string? EntryRelativePath { get; init; }

    /// <summary>Gets the per-assembly manifest entries.</summary>
    public ImmutableArray<ClosureManifestEntry> Assemblies { get; init; } = [];

    /// <summary>Gets the closure-relative paths and content hashes of assets copied verbatim.</summary>
    public ImmutableArray<ClosureManifestCopiedAsset> CopiedAssets { get; init; } = [];

    /// <summary>Serializes the manifest to deterministic, indented JSON.</summary>
    public string ToJson()
    {
        ClosureManifestJson.ValidateForSerialization(this);
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["engineName"] = EngineName,
            ["engineVersion"] = EngineVersion,
            ["ruleSetId"] = RuleSetId,
            ["ruleSetVersion"] = RuleSetVersion,
            ["ruleSetSignature"] = RuleSetSignature,
            ["configurationSignature"] = ConfigurationSignature,
            ["mode"] = Mode.ToString(),
            ["incrementalKey"] = IncrementalKey,
            ["entryRelativePath"] = EntryRelativePath,
            ["assemblies"] = SerializeAssemblies(),
            ["copiedAssets"] = SerializeCopiedAssets(),
        };

        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ClosureManifestJson.ValidateDocumentSize(Encoding.UTF8.GetByteCount(json));
        return json;
    }

    private JsonArray SerializeAssemblies()
    {
        var array = new JsonArray();
        foreach (ClosureManifestEntry entry in Assemblies.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["relativePath"] = entry.RelativePath,
                ["wasRewritten"] = entry.WasRewritten,
                ["wasNoOp"] = entry.WasNoOp,
                ["wasReSigned"] = entry.WasReSigned,
                ["readyToRunStripped"] = entry.ReadyToRunStripped,
                ["inputSha256"] = entry.InputSha256,
                ["outputSha256"] = entry.OutputSha256,
                ["errorCount"] = entry.ErrorCount,
            });
        }

        return array;
    }

    private JsonArray SerializeCopiedAssets()
    {
        var array = new JsonArray();
        foreach (ClosureManifestCopiedAsset asset in
            CopiedAssets.OrderBy(a => a.RelativePath, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["relativePath"] = asset.RelativePath,
                ["sha256"] = asset.Sha256,
            });
        }

        return array;
    }
}
