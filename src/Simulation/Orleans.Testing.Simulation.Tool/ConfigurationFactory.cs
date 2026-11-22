using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

namespace Orleans.Testing.Simulation.Tool;

/// <summary>
/// Builds an <see cref="InstrumentationConfiguration"/> from command-line options, optionally layered
/// over a JSON configuration file. Rule-set paths given on the command line are always appended so a
/// caller can add documents without editing the file. This mirrors the MSBuild task's precedence so
/// the CLI and the build produce identical configurations from equivalent inputs.
/// </summary>
internal static class ConfigurationFactory
{
    /// <summary>The value options this factory understands, contributed to a command's option set.</summary>
    public static readonly ImmutableArray<string> ValueOptions =
    [
        "config", "rule-set", "include", "exclude", "mode", "target-runtime",
        "builtin", "builtin-include", "builtin-exclude",
    ];

    /// <summary>Builds the configuration described by the reader's options.</summary>
    /// <param name="reader">The parsed command arguments.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="ConfigurationException">A configuration file or option value was invalid.</exception>
    /// <exception cref="UsageException">An enum option value was not recognized.</exception>
    public static InstrumentationConfiguration Build(ArgumentReader reader)
    {
        string? configPath = reader.GetString("config");
        InstrumentationConfiguration configuration = configPath is not null
            ? InstrumentationConfigurationLoader.Load(configPath)
            : new InstrumentationConfiguration
            {
                IncludePatterns = [.. reader.GetMany("include")],
                ExcludePatterns = [.. reader.GetMany("exclude")],
                Mode = ParseEnum<InstrumentationMode>(
                    reader.GetString("mode"),
                    InstrumentationMode.Controlled),
            };

        // As in the MSBuild task, '--target-runtime' must agree with a configuration file's targetRuntime; without either,
        // the runner derives the target from the closure's runtimeconfig.json.
        configuration = configuration.WithTargetRuntime(ParseTargetRuntime(reader.GetString("target-runtime")), "--target-runtime");

        IReadOnlyList<string> extraRuleSets = reader.GetMany("rule-set");
        if (extraRuleSets.Count > 0)
        {
            configuration = configuration with
            {
                RuleSetPaths = [.. configuration.RuleSetPaths, .. extraRuleSets],
            };
        }

        configuration = ApplyBuiltInOptions(reader, configuration);

        return configuration;
    }

    private static InstrumentationConfiguration ApplyBuiltInOptions(ArgumentReader reader, InstrumentationConfiguration configuration)
    {
        IReadOnlyList<string> builtIns = reader.GetMany("builtin");
        IReadOnlyList<string> include = reader.GetMany("builtin-include");
        IReadOnlyList<string> exclude = reader.GetMany("builtin-exclude");

        if (builtIns.Count > 0)
        {
            // '--builtin all' is a convenience for every shipped rule set id.
            IEnumerable<string> ids = builtIns.SelectMany(value =>
                string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
                    ? BuiltInRuleSets.AvailableIds.AsEnumerable()
                    : [value]);

            configuration = configuration with
            {
                BuiltInRuleSetIds = [.. configuration.BuiltInRuleSetIds.Concat(ids).Distinct(StringComparer.Ordinal)],
            };
        }

        if (include.Count > 0)
        {
            configuration = configuration with { BuiltInIncludeFamilies = [.. include] };
        }

        if (exclude.Count > 0)
        {
            configuration = configuration with { BuiltInExcludeFamilies = [.. exclude] };
        }

        return configuration;
    }

    private static Version? ParseTargetRuntime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RuntimeFeatureVersion.TryParse(value, out Version? version)
            ? version
            : throw new UsageException(
                $"'--target-runtime' value '{value}' is not a runtime feature version (major.minor, such as 11.0).");
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse(value, ignoreCase: false, out TEnum result) && Enum.IsDefined(result))
        {
            return result;
        }

        string allowed = string.Join(", ", Enum.GetNames<TEnum>());
        throw new UsageException($"'{value}' is not one of: {allowed}.");
    }
}
