using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Testing.Simulation.Instrumentation.Configuration;

/// <summary>
/// The declarative, serializable configuration that drives the build task and CLI: which rule-set
/// documents to load and which assemblies in a closure to include or exclude. Like
/// <see cref="Rules.RewriteRuleSet"/> it is pure data - loading it never executes arbitrary code -
/// and it exposes a stable <see cref="ComputeSignature"/> so it can participate in incremental
/// build keys and idempotence markers.
/// </summary>
public sealed record InstrumentationConfiguration
{
    /// <summary>The configuration schema version this type understands.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Gets the paths of the rule-set JSON documents to load and merge, in precedence order (a later
    /// document's rule overrides an earlier document's rule with the same id). Resolved relative to
    /// the configuration file when loaded from disk.
    /// </summary>
    public ImmutableArray<string> RuleSetPaths { get; init; } = [];

    /// <summary>
    /// Gets the instrumentation granularity. The default <see cref="InstrumentationMode.Controlled"/>
    /// performs only configured controlled-API rewriting; <see cref="InstrumentationMode.RaceExploration"/>
    /// additionally injects fine-grained memory and control-flow scheduling points.
    /// </summary>
    public InstrumentationMode Mode { get; init; } = InstrumentationMode.Controlled;

    /// <summary>
    /// Gets the ids of the built-in rule sets to enable (see
    /// <see cref="Rules.BuiltIn.BuiltInRuleSets.AvailableIds"/>). Built-in rule sets are merged at the
    /// <em>lowest</em> precedence, so a document in <see cref="RuleSetPaths"/> can override any
    /// built-in rule by id. Empty (the default) enables no built-ins, preserving prior behaviour.
    /// </summary>
    public ImmutableArray<string> BuiltInRuleSetIds { get; init; } = [];

    /// <summary>
    /// Gets the built-in rule families to include (see <see cref="Rules.BuiltIn.BuiltInRuleFamily"/>).
    /// Empty (the default) includes every family. Applied to every enabled built-in rule set.
    /// </summary>
    public ImmutableArray<string> BuiltInIncludeFamilies { get; init; } = [];

    /// <summary>
    /// Gets the built-in rule families to exclude even when included. Exclusion always wins.
    /// </summary>
    public ImmutableArray<string> BuiltInExcludeFamilies { get; init; } = [];

    /// <summary>
    /// Gets the case-insensitive file-name glob patterns (<c>*</c>/<c>?</c>) selecting which
    /// assemblies in the discovered closure are eligible for rewriting. An empty list includes every
    /// non-excluded managed assembly.
    /// </summary>
    public ImmutableArray<string> IncludePatterns { get; init; } = [];

    /// <summary>
    /// Gets the case-insensitive file-name glob patterns selecting assemblies to exclude from
    /// rewriting even if they match an include pattern. Exclusion always wins over inclusion.
    /// </summary>
    public ImmutableArray<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// Gets the feature version (major.minor) of the target runtime rules are evaluated against (see
    /// <see cref="Rewriting.RewriteOptions.TargetRuntime"/>), or <see langword="null"/> to derive it from the
    /// closure's <c>runtimeconfig.json</c> (see <see cref="Closure.TargetRuntimeDiscovery"/>).
    /// </summary>
    public Version? TargetRuntime { get; init; }

    /// <summary>
    /// Gets where <see cref="TargetRuntime"/> came from, such as the configuration file or <c>OrleansSimulationTargetRuntime</c>,
    /// or <see langword="null"/> when it was set directly.
    /// </summary>
    public string? TargetRuntimeOrigin { get; internal init; }

    internal string? SourcePath { get; init; }

    /// <summary>
    /// Returns this configuration with a target runtime from an explicit setting, such as an MSBuild property or a
    /// command-line option. A target runtime the configuration already names must agree, so neither silently loses.
    /// </summary>
    /// <param name="targetRuntime">The explicitly set target runtime, or <see langword="null"/> if it is not set.</param>
    /// <param name="origin">The setting's name, for diagnostics.</param>
    /// <returns>The configuration with the target runtime.</returns>
    /// <exception cref="ConfigurationException">The configuration names a different target runtime.</exception>
    public InstrumentationConfiguration WithTargetRuntime(Version? targetRuntime, string origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(origin);
        if (targetRuntime is null || targetRuntime == TargetRuntime)
        {
            return this;
        }

        if (TargetRuntime is not null)
        {
            throw new ConfigurationException(
                $"{origin} names target runtime {targetRuntime}, but {TargetRuntimeOrigin ?? "the configuration"} names " +
                $"{TargetRuntime}. Set the target runtime in one place.");
        }

        return this with { TargetRuntime = targetRuntime, TargetRuntimeOrigin = origin };
    }

    /// <summary>
    /// Computes a stable SHA-256 signature over every field that affects the rewritten output. Used
    /// as part of the incremental build key so a configuration change invalidates staged outputs.
    /// </summary>
    /// <returns>The lower-case hex signature.</returns>
    public string ComputeSignature() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalString())));

    /// <summary>Returns a stable, unambiguous canonical encoding of the configuration.</summary>
    /// <returns>The canonical string.</returns>
    public string ToCanonicalString()
    {
        var canonical = new CanonicalEncoding(nameof(InstrumentationConfiguration));
        canonical.AddInt32("SchemaVersion", CurrentSchemaVersion);
        canonical.AddString(nameof(Mode), Mode.ToString());
        canonical.AddStringArray(nameof(RuleSetPaths), RuleSetPaths);
        canonical.AddStringArray(nameof(BuiltInRuleSetIds), BuiltInRuleSetIds);
        canonical.AddStringArray(nameof(BuiltInIncludeFamilies), BuiltInIncludeFamilies);
        canonical.AddStringArray(nameof(BuiltInExcludeFamilies), BuiltInExcludeFamilies);
        canonical.AddStringArray(nameof(IncludePatterns), IncludePatterns);
        canonical.AddStringArray(nameof(ExcludePatterns), ExcludePatterns);
        canonical.AddString(nameof(TargetRuntime), TargetRuntime?.ToString());
        return canonical.ToString();
    }
}
