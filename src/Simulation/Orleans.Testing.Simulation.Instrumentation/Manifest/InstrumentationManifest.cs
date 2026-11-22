using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Configuration;

namespace Orleans.Testing.Simulation.Instrumentation.Manifest;

/// <summary>
/// The identity of an assembly recorded in a manifest: its simple name, file name, content hash, and
/// symbol availability.
/// </summary>
/// <param name="Name">The simple assembly name.</param>
/// <param name="FileName">The file name the assembly was read from or written to.</param>
/// <param name="Sha256">The lower-case hex SHA-256 of the assembly bytes, or <see langword="null"/> if not computed.</param>
/// <param name="HasSymbols">Whether debug symbols are present.</param>
/// <param name="SymbolKind">The detected symbol form (<c>None</c>/<c>Portable</c>/<c>Embedded</c>/<c>Unsupported</c>).</param>
public readonly record struct ManifestAssemblyIdentity(
    string Name,
    string FileName,
    string? Sha256,
    bool HasSymbols,
    string SymbolKind);

/// <summary>
/// A type excluded from rewriting, with the reason it was excluded.
/// </summary>
/// <param name="TypeFullName">The Cecil full name of the excluded type.</param>
/// <param name="Reason">The recorded exclusion reason.</param>
public readonly record struct ManifestExclusion(string TypeFullName, string Reason);

/// <summary>
/// The deterministic record of a single rewrite: input/output identity and hashes, the engine and
/// rule-set version, every site the engine acted on, unresolved references, exclusions, and
/// diagnostics. All collections are emitted in a stable order and the JSON serialization is invariant
/// so that two rewrites of identical inputs with identical rules produce byte-identical manifests.
/// </summary>
public sealed record InstrumentationManifest
{
    /// <summary>The manifest schema version.</summary>
    public const int SchemaVersion = 3;

    /// <summary>Gets the producing engine's name.</summary>
    public string EngineName { get; init; } = "Clockwork.Instrumentation";

    /// <summary>Gets the producing engine's version.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>Gets the applied rule set's identity.</summary>
    public required string RuleSetId { get; init; }

    /// <summary>Gets the applied rule set's version.</summary>
    public required string RuleSetVersion { get; init; }

    /// <summary>Gets the applied rule set's content signature.</summary>
    public required string RuleSetSignature { get; init; }

    /// <summary>Gets the instrumentation granularity used for this rewrite.</summary>
    public InstrumentationMode Mode { get; init; } = InstrumentationMode.Controlled;

    /// <summary>Gets the input assembly identity.</summary>
    public required ManifestAssemblyIdentity Input { get; init; }

    /// <summary>Gets the output assembly identity, if the assembly was written.</summary>
    public ManifestAssemblyIdentity? Output { get; init; }

    /// <summary>Gets a value indicating whether the rewrite was a verified no-op (already rewritten).</summary>
    public bool WasNoOp { get; init; }

    /// <summary>Gets the recorded transformations.</summary>
    public ImmutableArray<ManifestTransformation> Transformations { get; init; } = [];

    /// <summary>Gets the excluded types.</summary>
    public ImmutableArray<ManifestExclusion> Exclusions { get; init; } = [];

    /// <summary>Gets the unresolved reference names.</summary>
    public ImmutableArray<string> UnresolvedReferences { get; init; } = [];

    /// <summary>Gets the diagnostics produced during the rewrite.</summary>
    public ImmutableArray<RewriteDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Serializes the manifest to deterministic, indented JSON. Collections are sorted into a stable
    /// order and all values are formatted with the invariant culture.
    /// </summary>
    public string ToJson()
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["engineName"] = EngineName,
            ["engineVersion"] = EngineVersion,
            ["ruleSetId"] = RuleSetId,
            ["ruleSetVersion"] = RuleSetVersion,
            ["ruleSetSignature"] = RuleSetSignature,
            ["mode"] = Mode.ToString(),
            ["wasNoOp"] = WasNoOp,
            ["input"] = SerializeIdentity(Input),
            ["output"] = Output is { } output ? SerializeIdentity(output) : null,
            ["transformations"] = SerializeTransformations(),
            ["exclusions"] = SerializeExclusions(),
            ["unresolvedReferences"] = SerializeUnresolvedReferences(),
            ["diagnostics"] = SerializeDiagnostics(),
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject SerializeIdentity(ManifestAssemblyIdentity identity) => new()
    {
        ["name"] = identity.Name,
        ["fileName"] = identity.FileName,
        ["sha256"] = identity.Sha256,
        ["hasSymbols"] = identity.HasSymbols,
        ["symbolKind"] = identity.SymbolKind,
    };

    private JsonArray SerializeTransformations()
    {
        var array = new JsonArray();
        foreach (ManifestTransformation transformation in Transformations
            .OrderBy(t => t.Method, StringComparer.Ordinal)
            .ThenBy(t => t.ILOffset)
            .ThenBy(t => t.RuleId, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["ruleId"] = transformation.RuleId,
                ["operation"] = transformation.Operation.ToString(),
                ["outcome"] = transformation.Outcome.ToString(),
                ["policy"] = transformation.Policy.ToString(),
                ["target"] = transformation.Target,
                ["replacement"] = transformation.Replacement,
                ["method"] = transformation.Method,
                ["ilOffset"] = transformation.ILOffset,
                ["sourceFile"] = transformation.SourceFile,
                ["sourceLine"] = transformation.SourceLine,
            });
        }

        return array;
    }

    private JsonArray SerializeExclusions()
    {
        var array = new JsonArray();
        foreach (ManifestExclusion exclusion in Exclusions
            .OrderBy(e => e.TypeFullName, StringComparer.Ordinal)
            .ThenBy(e => e.Reason, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["type"] = exclusion.TypeFullName,
                ["reason"] = exclusion.Reason,
            });
        }

        return array;
    }

    private JsonArray SerializeUnresolvedReferences()
    {
        var array = new JsonArray();
        foreach (string reference in UnresolvedReferences.OrderBy(r => r, StringComparer.Ordinal))
        {
            array.Add(reference);
        }

        return array;
    }

    private JsonArray SerializeDiagnostics()
    {
        var array = new JsonArray();
        foreach (RewriteDiagnostic diagnostic in Diagnostics
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Method, StringComparer.Ordinal)
            .ThenBy(d => d.ILOffset)
            .ThenBy(d => d.Message, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["id"] = diagnostic.Id,
                ["severity"] = diagnostic.Severity.ToString(),
                ["message"] = diagnostic.Message,
                ["method"] = diagnostic.Method,
                ["ilOffset"] = diagnostic.ILOffset,
            });
        }

        return array;
    }
}
