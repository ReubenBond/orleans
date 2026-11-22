using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Runtime.Policy;

namespace Orleans.Testing.Simulation.Instrumentation.Configuration;

/// <summary>
/// Reads and writes a <see cref="RewriteRuleSet"/> as a strict, declarative JSON document. This is
/// the mechanism by which built-in Clockwork rules and user/application/third-party rule
/// sets, are supplied to the build task and CLI: rules are <em>pure data</em>, so loading a rule set
/// never compiles or executes arbitrary code. Every field is validated (unknown enum values, missing
/// required members, malformed signatures, and duplicate rule ids are hard errors) so that authoring
/// mistakes surface as a <see cref="ConfigurationException"/> rather than a silent misrewrite.
/// </summary>
/// <remarks>
/// The schema is intentionally a faithful projection of the in-memory rule model:
/// <code>
/// {
///   "schemaVersion": 2,
///   "id": "clockwork.example",
///   "version": "1.0",
///   "rules": [
///     {
///       "id": "redirect-utcnow",
///       "operation": "RedirectCall",
///       "target":      { "type": "System.DateTime", "member": "get_UtcNow", "parameters": [] },
///       "replacement": { "assembly": "Shims", "type": "Shims.Clock", "member": "UtcNow", "parameters": [] },
///       "policy": "Controlled",
///       "supportedRuntimes": { "min": "10.0", "max": null },
///       "description": "optional"
///     }
///   ]
/// }
/// </code>
/// A <c>null</c>/omitted <c>member</c> targets the type itself (type substitution); a <c>null</c>/omitted
/// <c>parameters</c> matches any overload, while an empty array matches a parameterless overload exactly. A
/// <c>RedirectCall</c> rule over a virtual member may give a <c>baseCallReplacement</c>, with the same shape as
/// <c>replacement</c>, to which the non-virtual <c>call</c> sites (an override's base call) are redirected.
/// </remarks>
public static class RuleSetJson
{
    /// <summary>The rule-set document schema version this loader understands.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Loads and validates a rule set from a JSON file.</summary>
    /// <param name="path">The path of the rule-set document.</param>
    /// <returns>The parsed, validated rule set.</returns>
    /// <exception cref="ConfigurationException">The document is malformed or fails validation.</exception>
    public static RewriteRuleSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath;
        try
        {
            fullPath = InstrumentationPath.GetFullPath(path, "Rule-set document");
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new ConfigurationException(
                $"Rule-set document '{path}' is not a valid path: {exception.Message}",
                exception);
        }

        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Rule-set document '{path}' was not found.");
        }

        string json;
        try
        {
            json = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"Rule-set document '{path}' could not be read: {ex.Message}", ex);
        }

        return Parse(json, fullPath);
    }

    /// <summary>Parses and validates a rule set from a JSON string.</summary>
    /// <param name="json">The rule-set document text.</param>
    /// <param name="sourceName">A name for the source used in error messages (e.g. the file path).</param>
    /// <returns>The parsed, validated rule set.</returns>
    /// <exception cref="ConfigurationException">The document is malformed or fails validation.</exception>
    public static RewriteRuleSet Parse(string json, string? sourceName = null)
    {
        string origin = sourceName is null ? "rule-set document" : $"rule-set document '{sourceName}'";
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

            EnsureOnlyProperties(root, origin, "schemaVersion", "id", "version", "rules");

            int schema = GetOptionalInt(root, "schemaVersion", origin) ?? SchemaVersion;
            if (schema != SchemaVersion)
            {
                throw new ConfigurationException(
                    $"{origin} declares unsupported schemaVersion {schema}; this engine supports version {SchemaVersion}.");
            }

            string id = GetRequiredString(root, "id", origin);
            string version = GetRequiredString(root, "version", origin);

            if (!root.TryGetProperty("rules", out JsonElement rules) || rules.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigurationException($"{origin} must contain a 'rules' array.");
            }

            var parsed = new List<RewriteRule>();
            int index = 0;
            foreach (JsonElement ruleElement in rules.EnumerateArray())
            {
                parsed.Add(ParseRule(ruleElement, origin, index));
                index++;
            }

            try
            {
                return new RewriteRuleSet(id, version, parsed);
            }
            catch (ArgumentException ex)
            {
                throw new ConfigurationException($"{origin} is invalid: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Serializes a rule set to a deterministic, indented JSON document.</summary>
    /// <param name="ruleSet">The rule set to serialize.</param>
    /// <returns>The JSON text.</returns>
    public static string Write(RewriteRuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);

        var ruleArray = new JsonArray();
        foreach (RewriteRule rule in ruleSet.Rules)
        {
            var ruleObject = new JsonObject
            {
                ["id"] = rule.Id,
                ["operation"] = rule.Operation.ToString(),
                ["target"] = WriteSignature(rule.Target.DeclaringTypeFullName, rule.Target.MemberName, rule.Target.ParameterTypeFullNames, assembly: null),
                ["replacement"] = WriteReplacement(rule.Replacement),
            };
            if (rule.BaseCallReplacement is { } baseCall)
            {
                ruleObject["baseCallReplacement"] = WriteReplacement(baseCall);
            }

            ruleObject["policy"] = rule.Policy.ToString();
            ruleObject["supportedRuntimes"] = new JsonObject
            {
                ["min"] = rule.SupportedRuntimes.Minimum?.ToString(),
                ["max"] = rule.SupportedRuntimes.Maximum?.ToString(),
            };
            ruleObject["description"] = rule.Description;
            ruleArray.Add(ruleObject);
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = ruleSet.Id,
            ["version"] = ruleSet.Version,
            ["rules"] = ruleArray,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject WriteReplacement(RewriteReplacement replacement) =>
        WriteSignature(replacement.DeclaringTypeFullName, replacement.MemberName, replacement.ParameterTypeFullNames, replacement.AssemblyName);

    private static JsonObject WriteSignature(string type, string? member, ImmutableArray<string> parameters, string? assembly)
    {
        var obj = new JsonObject();
        if (assembly is not null)
        {
            obj["assembly"] = assembly;
        }

        obj["type"] = type;
        obj["member"] = member;
        if (!parameters.IsDefault)
        {
            var array = new JsonArray();
            foreach (string parameter in parameters)
            {
                array.Add(parameter);
            }

            obj["parameters"] = array;
        }

        return obj;
    }

    private static RewriteRule ParseRule(JsonElement element, string origin, int index)
    {
        string context = $"{origin}, rule[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"{context} must be a JSON object.");
        }

        EnsureOnlyProperties(
            element,
            context,
            "id",
            "operation",
            "target",
            "replacement",
            "baseCallReplacement",
            "policy",
            "supportedRuntimes",
            "description");

        string id = GetRequiredString(element, "id", context);
        context = $"{origin}, rule '{id}'";

        RewriteOperationKind operation = ParseEnum<RewriteOperationKind>(GetRequiredString(element, "operation", context), "operation", context);

        (string targetType, string? targetMember, ImmutableArray<string> targetParameters) =
            ParseSignature(element, "target", context, requireAssembly: false).Signature;

        bool isTypeOperation = operation == RewriteOperationKind.SubstituteType;
        if (isTypeOperation && targetMember is not null)
        {
            throw new ConfigurationException($"{context}: a SubstituteType rule must target a type, so 'target.member' must be omitted.");
        }

        if (!isTypeOperation && targetMember is null)
        {
            throw new ConfigurationException($"{context}: operation '{operation}' targets a member, so 'target.member' is required.");
        }

        MemberSignature target = targetParameters.IsDefault
            ? new MemberSignature(targetType, targetMember)
            : new MemberSignature(targetType, targetMember, targetParameters);

        (string? replacementAssembly, (string replacementType, string? replacementMember, ImmutableArray<string> replacementParameters)) =
            ParseSignature(element, "replacement", context, requireAssembly: true);

        if (isTypeOperation && replacementMember is not null)
        {
            throw new ConfigurationException($"{context}: a SubstituteType rule's 'replacement.member' must be omitted (it names a substitute type).");
        }

        if (!isTypeOperation && replacementMember is null)
        {
            throw new ConfigurationException($"{context}: operation '{operation}' requires 'replacement.member'.");
        }

        var replacement = new RewriteReplacement(
            replacementAssembly!,
            replacementType,
            replacementMember,
            replacementParameters);

        RewriteReplacement? baseCallReplacement = null;
        if (element.TryGetProperty("baseCallReplacement", out JsonElement baseCallElement) && baseCallElement.ValueKind != JsonValueKind.Null)
        {
            if (operation != RewriteOperationKind.RedirectCall)
            {
                throw new ConfigurationException($"{context}: 'baseCallReplacement' applies only to a RedirectCall rule.");
            }

            (string? baseCallAssembly, (string baseCallType, string? baseCallMember, ImmutableArray<string> baseCallParameters)) =
                ParseSignature(element, "baseCallReplacement", context, requireAssembly: true);
            if (baseCallMember is null)
            {
                throw new ConfigurationException($"{context}: 'baseCallReplacement.member' is required.");
            }

            baseCallReplacement = new RewriteReplacement(baseCallAssembly!, baseCallType, baseCallMember, baseCallParameters);
        }

        SimulationApiPolicy policy = element.TryGetProperty("policy", out JsonElement policyElement) && policyElement.ValueKind != JsonValueKind.Null
            ? ParseEnum<SimulationApiPolicy>(RequireString(policyElement, "policy", context), "policy", context)
            : SimulationApiPolicy.Controlled;

        RuntimeVersionRange runtimes = ParseRuntimeRange(element, context);
        string? description = GetOptionalString(element, "description", context);

        return new RewriteRule
        {
            Id = id,
            Operation = operation,
            Target = target,
            Replacement = replacement,
            BaseCallReplacement = baseCallReplacement,
            Policy = policy,
            SupportedRuntimes = runtimes,
            Description = description,
        };
    }

    private static (string? Assembly, (string Type, string? Member, ImmutableArray<string> Parameters) Signature) ParseSignature(
        JsonElement parent, string propertyName, string context, bool requireAssembly)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"{context}: '{propertyName}' must be a JSON object.");
        }

        EnsureOnlyProperties(element, $"{context}.{propertyName}", "assembly", "type", "member", "parameters");

        string type = GetRequiredString(element, "type", $"{context}.{propertyName}");
        string? member = GetOptionalString(element, "member", $"{context}.{propertyName}");
        if (member is not null && member.Length == 0)
        {
            throw new ConfigurationException($"{context}: '{propertyName}.member' must not be empty; omit it to target a type.");
        }

        string? assembly = null;
        if (requireAssembly)
        {
            assembly = GetRequiredString(element, "assembly", $"{context}.{propertyName}");
        }
        else if (element.TryGetProperty("assembly", out _))
        {
            throw new ConfigurationException($"{context}: 'target' must not specify an 'assembly'; targets match the assembly being rewritten.");
        }

        ImmutableArray<string> parameters = default;
        if (element.TryGetProperty("parameters", out JsonElement parametersElement) && parametersElement.ValueKind != JsonValueKind.Null)
        {
            if (parametersElement.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigurationException($"{context}: '{propertyName}.parameters' must be an array of type full names.");
            }

            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (JsonElement parameter in parametersElement.EnumerateArray())
            {
                string value = RequireString(parameter, $"{propertyName}.parameters[]", context);
                if (value.Length == 0)
                {
                    throw new ConfigurationException($"{context}: a '{propertyName}.parameters' entry must be a non-empty type full name.");
                }

                builder.Add(value);
            }

            parameters = builder.ToImmutable();
        }

        return (assembly, (type, member, parameters));
    }

    private static RuntimeVersionRange ParseRuntimeRange(JsonElement element, string context)
    {
        if (!element.TryGetProperty("supportedRuntimes", out JsonElement range) || range.ValueKind == JsonValueKind.Null)
        {
            return RuntimeVersionRange.All;
        }

        if (range.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigurationException($"{context}: 'supportedRuntimes' must be an object with optional 'min'/'max'.");
        }

        EnsureOnlyProperties(range, $"{context}.supportedRuntimes", "min", "max");

        Version? min = ParseOptionalVersion(range, "min", context);
        Version? max = ParseOptionalVersion(range, "max", context);
        if (min is not null && max is not null && min > max)
        {
            throw new ConfigurationException($"{context}: 'supportedRuntimes.min' ({min}) is greater than 'max' ({max}).");
        }

        return new RuntimeVersionRange(min, max);
    }

    private static Version? ParseOptionalVersion(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string text = RequireString(value, $"supportedRuntimes.{propertyName}", context);
        if (!RuntimeFeatureVersion.TryParse(text, out Version? version))
        {
            throw new ConfigurationException(
                $"{context}: 'supportedRuntimes.{propertyName}' value '{text}' is not a runtime feature version (major.minor, such as 11.0).");
        }

        return version;
    }

    private static TEnum ParseEnum<TEnum>(string value, string propertyName, string context)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: false, out TEnum result) && Enum.IsDefined(result))
        {
            return result;
        }

        string allowed = string.Join(", ", Enum.GetNames<TEnum>());
        throw new ConfigurationException($"{context}: '{propertyName}' value '{value}' is not one of: {allowed}.");
    }

    private static void EnsureOnlyProperties(JsonElement element, string context, params string[] allowedProperties)
    {
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ConfigurationException($"{context}: unknown property '{property.Name}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw new ConfigurationException($"{context}: property '{property.Name}' is specified more than once.");
            }
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new ConfigurationException($"{context}: required property '{propertyName}' is missing.");
        }

        return RequireString(value, propertyName, context);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequireString(value, propertyName, context);
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw new ConfigurationException($"{context}: '{propertyName}' must be an integer.");
        }

        return result;
    }

    private static string RequireString(JsonElement value, string propertyName, string context)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ConfigurationException($"{context}: '{propertyName}' must be a string.");
        }

        string text = value.GetString() ?? string.Empty;
        if (propertyName is "id" or "version" or "type" or "assembly" && string.IsNullOrWhiteSpace(text))
        {
            throw new ConfigurationException($"{context}: '{propertyName}' must not be empty.");
        }

        if (text.Length > ClosureManifestLimits.MaxStringLength)
        {
            throw new ConfigurationException(
                $"{context}: '{propertyName}' length {text.Length} exceeds {ClosureManifestLimits.MaxStringLength}.");
        }

        return text;
    }
}
