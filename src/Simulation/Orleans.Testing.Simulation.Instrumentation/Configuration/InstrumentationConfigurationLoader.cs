using System.Collections.Immutable;
using System.Text.Json;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;

namespace Orleans.Testing.Simulation.Instrumentation.Configuration;

/// <summary>
/// Loads and strictly validates an <see cref="InstrumentationConfiguration"/> from a JSON document.
/// Relative rule-set paths are resolved against the configuration file's directory so a
/// configuration file is self-contained and portable. Unknown or duplicate properties, unknown enum
/// values, wrong JSON types, and missing required fields are hard <see cref="ConfigurationException"/>s.
/// </summary>
public static class InstrumentationConfigurationLoader
{
    /// <summary>Loads and validates a configuration from a JSON file, resolving relative paths.</summary>
    /// <param name="path">The configuration file path.</param>
    /// <returns>The validated configuration with resolved absolute paths.</returns>
    /// <exception cref="ConfigurationException">The document is malformed or fails validation.</exception>
    public static InstrumentationConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = NormalizePath(path, "Configuration file");
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Configuration file '{path}' was not found.");
        }

        string json;
        try
        {
            json = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"Configuration file '{path}' could not be read: {ex.Message}", ex);
        }

        string baseDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        return Parse(json, baseDirectory, fullPath) with { SourcePath = fullPath };
    }

    /// <summary>Parses and validates a configuration from a JSON string.</summary>
    /// <param name="json">The configuration text.</param>
    /// <param name="baseDirectory">The directory relative paths are resolved against, or <see langword="null"/> to keep paths as written.</param>
    /// <param name="sourceName">A name for the source used in error messages.</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="ConfigurationException">The document is malformed or fails validation.</exception>
    public static InstrumentationConfiguration Parse(string json, string? baseDirectory = null, string? sourceName = null)
    {
        string origin = sourceName is null ? "configuration" : $"configuration '{sourceName}'";
        string? normalizedBaseDirectory = baseDirectory is null
            ? null
            : NormalizePath(baseDirectory, $"{origin} base directory");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"{origin} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigurationException($"{origin} must be a JSON object.");
            }

            EnsureOnlyProperties(
                root,
                origin,
                "schemaVersion",
                "ruleSets",
                "mode",
                "builtInRuleSets",
                "builtInIncludeFamilies",
                "builtInExcludeFamilies",
                "include",
                "exclude",
                "targetRuntime");

            int schema = GetRequiredInt(root, "schemaVersion", origin);
            if (schema != InstrumentationConfiguration.CurrentSchemaVersion)
            {
                throw new ConfigurationException(
                    $"{origin} declares unsupported schemaVersion {schema}; this tool supports version {InstrumentationConfiguration.CurrentSchemaVersion}.");
            }

            ImmutableArray<string> ruleSets = ResolvePaths(
                GetStringArray(root, "ruleSets", origin),
                normalizedBaseDirectory,
                origin);
            InstrumentationMode mode =
                GetOptionalEnum<InstrumentationMode>(root, "mode", origin) ?? InstrumentationMode.Controlled;
            ImmutableArray<string> builtInRuleSets = GetStringArray(root, "builtInRuleSets", origin);
            ImmutableArray<string> builtInInclude = GetStringArray(root, "builtInIncludeFamilies", origin);
            ImmutableArray<string> builtInExclude = GetStringArray(root, "builtInExcludeFamilies", origin);
            ImmutableArray<string> include = GetStringArray(root, "include", origin);
            ImmutableArray<string> exclude = GetStringArray(root, "exclude", origin);
            Version? targetRuntime = GetOptionalFeatureVersion(root, "targetRuntime", origin);

            return new InstrumentationConfiguration
            {
                RuleSetPaths = ruleSets,
                Mode = mode,
                BuiltInRuleSetIds = builtInRuleSets,
                BuiltInIncludeFamilies = builtInInclude,
                BuiltInExcludeFamilies = builtInExclude,
                IncludePatterns = include,
                ExcludePatterns = exclude,
                TargetRuntime = targetRuntime,
                TargetRuntimeOrigin = targetRuntime is null ? null : $"'targetRuntime' in {origin}",
            };
        }
    }

    private static ImmutableArray<string> ResolvePaths(
        ImmutableArray<string> paths,
        string? baseDirectory,
        string origin)
    {
        if (baseDirectory is null || paths.IsDefaultOrEmpty)
        {
            return paths;
        }

        return [.. paths.Select((path, index) => CombineAndNormalizePath(
            baseDirectory,
            path,
            $"{origin}: 'ruleSets[{index}]'"))];
    }

    private static string NormalizePath(string path, string description)
    {
        try
        {
            return InstrumentationPath.GetFullPath(path, description);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ConfigurationException(
                $"{description} '{path}' is not a valid path: {exception.Message}",
                exception);
        }
    }

    private static string CombineAndNormalizePath(
        string baseDirectory,
        string path,
        string description)
    {
        try
        {
            return InstrumentationPath.CombineAndGetFullPath(baseDirectory, path, description);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ConfigurationException(
                $"{description} '{path}' is not a valid path: {exception.Message}",
                exception);
        }
    }

    private static ImmutableArray<string> GetStringArray(JsonElement element, string propertyName, string origin)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigurationException($"{origin}: '{propertyName}' must be an array of strings.");
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ConfigurationException($"{origin}: every '{propertyName}' entry must be a string.");
            }

            string text = item.GetString() ?? string.Empty;
            if (text.Length == 0)
            {
                throw new ConfigurationException($"{origin}: '{propertyName}' entries must be non-empty.");
            }

            EnsureWithinStringLimit(text, $"{origin}: '{propertyName}' entry");
            builder.Add(text);
        }

        return builder.ToImmutable();
    }

    private static string? GetOptionalString(JsonElement element, string propertyName, string origin)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ConfigurationException($"{origin}: '{propertyName}' must be a string.");
        }

        string text = value.GetString() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        EnsureWithinStringLimit(text, $"{origin}: '{propertyName}'");
        return text;
    }

    private static int GetRequiredInt(JsonElement element, string propertyName, string origin)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new ConfigurationException($"{origin}: required property '{propertyName}' is missing.");
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw new ConfigurationException($"{origin}: '{propertyName}' must be an integer.");
        }

        return result;
    }

    private static Version? GetOptionalFeatureVersion(JsonElement element, string propertyName, string origin)
    {
        string? text = GetOptionalString(element, propertyName, origin);
        if (text is null)
        {
            return null;
        }

        if (!RuntimeFeatureVersion.TryParse(text, out Version? version))
        {
            throw new ConfigurationException(
                $"{origin}: '{propertyName}' value '{text}' is not a runtime feature version (major.minor, such as 11.0).");
        }

        return version;
    }

    private static TEnum? GetOptionalEnum<TEnum>(JsonElement element, string propertyName, string origin)
        where TEnum : struct, Enum
    {
        string? text = GetOptionalString(element, propertyName, origin);
        if (text is null)
        {
            return null;
        }

        if (Enum.TryParse(text, ignoreCase: false, out TEnum result) && Enum.IsDefined(result))
        {
            return result;
        }

        string allowed = string.Join(", ", Enum.GetNames<TEnum>());
        throw new ConfigurationException($"{origin}: '{propertyName}' value '{text}' is not one of: {allowed}.");
    }

    private static void EnsureOnlyProperties(JsonElement element, string origin, params string[] allowedProperties)
    {
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ConfigurationException($"{origin}: unknown property '{property.Name}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw new ConfigurationException($"{origin}: property '{property.Name}' is specified more than once.");
            }
        }
    }

    private static void EnsureWithinStringLimit(string value, string field)
    {
        if (value.Length > ClosureManifestLimits.MaxStringLength)
        {
            throw new ConfigurationException(
                $"{field} length {value.Length} exceeds {ClosureManifestLimits.MaxStringLength}.");
        }
    }
}
