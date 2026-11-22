using Orleans.Testing.Simulation.Instrumentation.Closure;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Orleans.Testing.Simulation.Instrumentation.Build;

/// <summary>
/// The MSBuild task that runs Orleans simulation's deterministic IL instrumentation over a resolved
/// application output directory and stages the instrumented closure under <c>obj</c>. It is opt-in:
/// the accompanying targets only invoke it when <c>OrleansSimulationInstrumentationEnabled</c> is
/// <see langword="true"/>. The task builds an <see cref="InstrumentationConfiguration"/> from either a
/// configuration file or its own properties, merges the configured rule-set documents, runs the
/// <see cref="InstrumentationRunner"/>, and surfaces every rewrite diagnostic as an MSBuild
/// message/warning/error so a targeted failure fails the build rather than silently degrading output.
/// </summary>
public sealed class OrleansSimulationInstrumentTask : MSBuildTask
{
    /// <summary>Gets or sets the resolved application output directory to instrument.</summary>
    [Required]
    public string SourceDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the staging directory the instrumented closure is written to (owned by the task).</summary>
    [Required]
    public string StagingDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional JSON configuration file path.</summary>
    public string? ConfigurationPath { get; set; }

    /// <summary>Gets or sets the rule-set JSON documents to load and merge (appended to any from the configuration file).</summary>
    public ITaskItem[] RuleSetPaths { get; set; } = [];

    /// <summary>Gets or sets the built-in rule set ids to enable (e.g. <c>clockwork.bcl.deterministic</c>). Merged at lowest precedence.</summary>
    public ITaskItem[] BuiltInRuleSets { get; set; } = [];

    /// <summary>Gets or sets the built-in rule families to include (empty includes every family).</summary>
    public ITaskItem[] BuiltInIncludeFamilies { get; set; } = [];

    /// <summary>Gets or sets the built-in rule families to exclude even when included.</summary>
    public ITaskItem[] BuiltInExcludeFamilies { get; set; } = [];

    /// <summary>Gets or sets the include glob patterns selecting assemblies eligible for rewriting.</summary>
    public ITaskItem[] IncludePatterns { get; set; } = [];

    /// <summary>Gets or sets the exclude glob patterns removing assemblies from rewriting.</summary>
    public ITaskItem[] ExcludePatterns { get; set; } = [];

    /// <summary>Gets or sets the instrumentation mode (<c>Controlled</c> or <c>RaceExploration</c>).</summary>
    public string InstrumentationMode { get; set; } = nameof(Configuration.InstrumentationMode.Controlled);

    /// <summary>
    /// Gets or sets the feature version (major.minor) of the target runtime rules are evaluated against when the
    /// configuration file names none, or empty to derive it from the closure's <c>runtimeconfig.json</c>.
    /// </summary>
    public string? TargetRuntime { get; set; }

    /// <summary>Gets or sets the entry assembly's simple name or file name, or empty to auto-detect it.</summary>
    public string? EntryAssemblyName { get; set; }

    /// <summary>Gets or sets the path the closure manifest is written to, or empty for the default sibling path.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>Gets or sets the path to the atomic success marker used for incremental builds.</summary>
    public string? CachePath { get; set; }

    /// <summary>Gets the path the closure manifest was written to.</summary>
    [Output]
    public string ResolvedManifestPath { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the run was satisfied incrementally from cache.</summary>
    [Output]
    public bool WasIncremental { get; private set; }

    /// <inheritdoc/>
    public override bool Execute()
    {
        InstrumentationConfiguration configuration;
        RewriteRuleSet ruleSet;
        try
        {
            configuration = BuildConfiguration();
            ruleSet = RuleSetMerge.LoadAndMerge(configuration).RuleSet;
        }
        catch (ConfigurationException ex)
        {
            Log.LogError(null, "CWR0200", null, ConfigurationPath, 0, 0, 0, 0, ex.Message);
            return false;
        }

        InstrumentationResult result;
        try
        {
            var request = new InstrumentationRequest
            {
                SourceDirectory = SourceDirectory,
                StagingDirectory = StagingDirectory,
                Configuration = configuration,
                RuleSet = ruleSet,
                EntryAssemblyName = NullIfEmpty(EntryAssemblyName),
            };

            if (NullIfEmpty(ManifestPath) is { } manifestOverride)
            {
                request = request with { ManifestPath = manifestOverride };
            }

            if (NullIfEmpty(CachePath) is { } cacheOverride)
            {
                request = request with { CachePath = cacheOverride };
            }

            result = InstrumentationRunner.Run(request);
        }
        catch (ClosureException ex)
        {
            Log.LogError(null, "CWR0201", null, SourceDirectory, 0, 0, 0, 0, ex.Message);
            return false;
        }

        ResolvedManifestPath = result.ManifestPath;
        WasIncremental = result.WasIncrementalHit;

        foreach (AssemblyInstrumentationResult assembly in result.Assemblies)
        {
            foreach (RewriteDiagnostic diagnostic in assembly.Diagnostics)
            {
                Report(diagnostic);
            }
        }

        foreach (RewriteDiagnostic diagnostic in result.Diagnostics)
        {
            Report(diagnostic);
        }

        if (result.WasIncrementalHit)
        {
            Log.LogMessage(MessageImportance.Low, "Orleans simulation instrumentation is up to date (incremental).");
        }
        else if (result.Succeeded)
        {
            Log.LogMessage(
                MessageImportance.Normal,
                "Orleans simulation instrumented {0} assemblies ({1} verified no-ops) for .NET {2}, copied {3} assets to '{4}'.",
                result.RewrittenCount,
                result.NoOpCount,
                result.TargetRuntime,
                result.CopiedAssets.Length,
                result.StagingDirectory);
        }

        return result.Succeeded && !Log.HasLoggedErrors;
    }

    private InstrumentationConfiguration BuildConfiguration()
    {
        InstrumentationConfiguration configuration = NullIfEmpty(ConfigurationPath) is { } path
            ? InstrumentationConfigurationLoader.Load(path)
            : new InstrumentationConfiguration
            {
                Mode = ParseEnum<InstrumentationMode>(InstrumentationMode, nameof(InstrumentationMode)),
            };

        // OrleansSimulationTargetRuntime must agree with a configuration file's targetRuntime; without either, the runner derives
        // the target from the closure's runtimeconfig.json.
        configuration = configuration.WithTargetRuntime(ParseTargetRuntime(TargetRuntime), "OrleansSimulationTargetRuntime");

        System.Collections.Immutable.ImmutableArray<string> taskIncludes = ToPatternArray(IncludePatterns);
        if (!taskIncludes.IsDefaultOrEmpty)
        {
            configuration = configuration with
            {
                IncludePatterns = [.. configuration.IncludePatterns, .. taskIncludes],
            };
        }

        System.Collections.Immutable.ImmutableArray<string> taskExcludes = ToPatternArray(ExcludePatterns);
        if (!taskExcludes.IsDefaultOrEmpty)
        {
            configuration = configuration with
            {
                ExcludePatterns = [.. configuration.ExcludePatterns, .. taskExcludes],
            };
        }

        System.Collections.Immutable.ImmutableArray<string> taskRuleSets = ToPatternArray(RuleSetPaths);
        if (!taskRuleSets.IsDefaultOrEmpty)
        {
            configuration = configuration with
            {
                RuleSetPaths = [.. configuration.RuleSetPaths, .. taskRuleSets],
            };
        }

        System.Collections.Immutable.ImmutableArray<string> builtInIds = ToPatternArray(BuiltInRuleSets);
        if (!builtInIds.IsDefaultOrEmpty)
        {
            configuration = configuration with
            {
                BuiltInRuleSetIds = [.. configuration.BuiltInRuleSetIds.Concat(builtInIds).Distinct(StringComparer.Ordinal)],
            };
        }

        System.Collections.Immutable.ImmutableArray<string> includeFamilies = ToPatternArray(BuiltInIncludeFamilies);
        if (!includeFamilies.IsDefaultOrEmpty)
        {
            configuration = configuration with { BuiltInIncludeFamilies = includeFamilies };
        }

        System.Collections.Immutable.ImmutableArray<string> excludeFamilies = ToPatternArray(BuiltInExcludeFamilies);
        if (!excludeFamilies.IsDefaultOrEmpty)
        {
            configuration = configuration with { BuiltInExcludeFamilies = excludeFamilies };
        }

        return configuration;
    }

    private void Report(RewriteDiagnostic diagnostic)
    {
        switch (diagnostic.Severity)
        {
            case RewriteDiagnosticSeverity.Error:
                Log.LogError(null, diagnostic.Id, null, null, 0, 0, 0, 0, diagnostic.Message);
                break;
            case RewriteDiagnosticSeverity.Warning:
                Log.LogWarning(null, diagnostic.Id, null, null, 0, 0, 0, 0, diagnostic.Message);
                break;
            default:
                Log.LogMessage(MessageImportance.Low, "{0}: {1}", diagnostic.Id, diagnostic.Message);
                break;
        }
    }

    private static System.Collections.Immutable.ImmutableArray<string> ToPatternArray(ITaskItem[] items) =>
        items.Length == 0
            ? []
            : [.. items.Select(i => i.ItemSpec).Where(s => !string.IsNullOrWhiteSpace(s))];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Version? ParseTargetRuntime(string? value)
    {
        if (NullIfEmpty(value) is not { } text)
        {
            return null;
        }

        return RuntimeFeatureVersion.TryParse(text, out Version? version)
            ? version
            : throw new ConfigurationException(
                $"OrleansSimulationTargetRuntime '{text}' is not a runtime feature version (major.minor, such as 11.0).");
    }

    private static TEnum ParseEnum<TEnum>(string value, string propertyName)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: false, out TEnum result) && Enum.IsDefined(result))
        {
            return result;
        }

        string allowed = string.Join(", ", Enum.GetNames<TEnum>());
        throw new ConfigurationException($"{propertyName} value '{value}' is not one of: {allowed}.");
    }
}
