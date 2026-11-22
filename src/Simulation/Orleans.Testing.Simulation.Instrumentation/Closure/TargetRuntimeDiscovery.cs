using System.Text.Json;
using Orleans.Testing.Simulation.Instrumentation.Configuration;

namespace Orleans.Testing.Simulation.Instrumentation.Closure;

/// <summary>
/// Resolves the target runtime an instrumentation run evaluates rules against: the configured one, or else the
/// runtime the closure's entry assembly runs on, which its <c>runtimeconfig.json</c> names. The host reads the same
/// file to choose a runtime, so the rules are classified against the runtime the code runs on, not the framework it
/// compiled against. A closure that does not name one fails rather than leave the rules unchecked.
/// </summary>
public static class TargetRuntimeDiscovery
{
    // Microsoft.NETCore.App is the runtime itself; the other shared frameworks ship in lockstep with it, so their
    // feature version is the runtime's.
    private static readonly string[] RuntimeFrameworkNames =
        ["Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App"];

    private static readonly JsonDocumentOptions RuntimeConfigJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Resolves the target runtime for <paramref name="plan"/>.</summary>
    /// <param name="configuration">The configuration, whose <see cref="InstrumentationConfiguration.TargetRuntime"/> wins when set.</param>
    /// <param name="plan">The discovered closure.</param>
    /// <returns>The target runtime's feature version, and where it came from.</returns>
    /// <exception cref="ClosureException">No target runtime is configured and the closure does not name one.</exception>
    public static (Version TargetRuntime, string Origin) Resolve(InstrumentationConfiguration configuration, ClosurePlan plan)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plan);
        return configuration.TargetRuntime is { } configured
            ? (configured, configuration.TargetRuntimeOrigin ?? "configured")
            : FromRuntimeConfig(plan);
    }

    private static (Version TargetRuntime, string Origin) FromRuntimeConfig(ClosurePlan plan)
    {
        if (plan.EntryAssemblyRelativePath is not { } entry)
        {
            throw CannotDerive("the closure has no entry assembly whose runtimeconfig.json names its runtime");
        }

        string relative = Path.ChangeExtension(entry, ".runtimeconfig.json");
        string path = Path.Combine(plan.RootDirectory, relative);
        if (!File.Exists(path))
        {
            throw CannotDerive($"'{relative}' was not found");
        }

        JsonDocument document;
        try
        {
            // Parsing the stream skips a UTF-8 byte order mark, which the host accepts.
            using FileStream stream = File.OpenRead(path);
            document = JsonDocument.Parse(stream, RuntimeConfigJsonOptions);
        }
        catch (JsonException exception)
        {
            throw CannotDerive($"'{relative}' is not valid JSON: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CannotDerive($"'{relative}' is unreadable: {exception.Message}", exception);
        }

        using (document)
        {
            Dictionary<string, string?> frameworks = ReadFrameworks(document.RootElement);
            foreach (string name in RuntimeFrameworkNames)
            {
                if (!frameworks.TryGetValue(name, out string? text))
                {
                    continue;
                }

                return TryGetFeatureVersion(text, out Version? version)
                    ? (version, $"from '{relative}'")
                    : throw CannotDerive($"'{relative}' references {name} version '{text}', which is not a .NET version");
            }

            throw CannotDerive($"'{relative}' references none of {string.Join(", ", RuntimeFrameworkNames)}");
        }
    }

    // A framework-dependent app names its shared frameworks in 'framework' or 'frameworks'; a self-contained app names
    // the ones it carries in 'includedFrameworks'.
    private static Dictionary<string, string?> ReadFrameworks(JsonElement root)
    {
        var frameworks = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("runtimeOptions", out JsonElement options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return frameworks;
        }

        if (options.TryGetProperty("framework", out JsonElement single))
        {
            Add(single);
        }

        foreach (string property in (string[])["frameworks", "includedFrameworks"])
        {
            if (options.TryGetProperty(property, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement framework in list.EnumerateArray())
                {
                    Add(framework);
                }
            }
        }

        return frameworks;

        void Add(JsonElement framework)
        {
            if (framework.ValueKind == JsonValueKind.Object
                && framework.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String)
            {
                frameworks.TryAdd(
                    name.GetString()!,
                    framework.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String
                        ? version.GetString()
                        : null);
            }
        }
    }

    // A framework version is a full runtime version, possibly prerelease (11.0.0-rc.2.26504.105); rules are classified
    // against its feature version.
    private static bool TryGetFeatureVersion(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (text is null)
        {
            return false;
        }

        int suffix = text.IndexOfAny(['-', '+']);
        if (!Version.TryParse(suffix < 0 ? text : text[..suffix], out Version? parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor);
        return true;
    }

    private static ClosureException CannotDerive(string reason) => new(CannotDeriveMessage(reason));

    private static ClosureException CannotDerive(string reason, Exception innerException) =>
        new(CannotDeriveMessage(reason), innerException);

    private static string CannotDeriveMessage(string reason) =>
        $"Clockwork evaluates rules against a target runtime, which is not configured and cannot be derived: {reason}. " +
        "Set it to a feature version such as 11.0 with the configuration file's 'targetRuntime', the " +
        "OrleansSimulationTargetRuntime MSBuild property, or the tool's '--target-runtime' option.";
}
