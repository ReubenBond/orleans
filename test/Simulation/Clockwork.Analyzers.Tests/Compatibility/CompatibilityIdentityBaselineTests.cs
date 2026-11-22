using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Testing.Simulation.Analyzers;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Orchestration;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Replay;

namespace Orleans.Testing.Simulation.Analyzers.Tests.Compatibility;

/// <summary>
/// Pins the external identities which must survive the later package, assembly, namespace, and build
/// surface migration. The exhaustive schema tests remain at their owning layers; this single golden
/// records the cross-layer names and versions which old artifacts and consumers bind to.
/// </summary>
public sealed class CompatibilityIdentityBaselineTests
{
    [Fact]
    public void ExistingReplayManifestRuleAndDiagnosticIdentitiesMatchGolden()
    {
        string actual = JsonSerializer.Serialize(CreateBaseline(), SerializerOptions);
        string expected = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "test",
            "Simulation",
            "Clockwork.Analyzers.Tests",
            "Compatibility",
            "clockwork-identities.json"));

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    private static object CreateBaseline() => new
    {
        replay = new
        {
            format = ReplayArtifact.SchemaName,
            schemaVersion = ReplayArtifact.CurrentSchemaVersion,
            simulationSeedProperty = typeof(ReplayArtifact)
                .GetProperty(nameof(ReplayArtifact.SimulationSeed))!
                .GetCustomAttribute<JsonPropertyNameAttribute>()!
                .Name,
        },
        manifests = new
        {
            instrumentationSchemaVersion = InstrumentationManifest.SchemaVersion,
            closureSchemaVersion = ClosureManifest.SchemaVersion,
            configurationSchemaVersion = InstrumentationConfiguration.CurrentSchemaVersion,
            ruleSetSchemaVersion = RuleSetJson.SchemaVersion,
            instrumentationEngineName = CreateInstrumentationManifest().EngineName,
            closureEngineName = CreateClosureManifest().EngineName,
            rewriteSignatureMetadataKey = GetRewriteSignatureMetadataKey(),
            supportedRuntimesMetadataKey = GetConstant(
                typeof(ReplayArtifact).Assembly,
                "Orleans.Testing.Simulation.Runtime.SupportedRuntimesMarker",
                "Key"),
        },
        ruleSets = new
        {
            mergedId = RuleSetMerge.MergedId,
            deterministicBcl = new
            {
                id = BuiltInRuleSets.DeterministicBclId,
                version = BuiltInRuleSets.DeterministicBclVersion,
                ruleIdCount = BuiltInRuleSets.DeterministicBclInventory.Length,
                ruleIdsSha256 = HashIds(BuiltInRuleSets.DeterministicBclInventory
                    .Select(static entry => entry.Rule.Id)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
            },
            controlledTasks = new
            {
                id = BuiltInRuleSets.ControlledTasksId,
                version = BuiltInRuleSets.ControlledTasksVersion,
                ruleIdCount = BuiltInRuleSets.ControlledTasksInventory.Length,
                ruleIdsSha256 = HashIds(BuiltInRuleSets.ControlledTasksInventory
                    .Select(static entry => entry.Rule.Id)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
            },
        },
        diagnostics = new
        {
            categories = new[]
            {
                CompletionAuthorityAnalyzer.CompletionOutsideAuthority.Category,
                DeterminismDiagnostics.ControlledApi.Category,
            }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            analyzerIds = new[]
            {
                DeterminismDiagnostics.ControlledApi.Id,
                DeterminismDiagnostics.UnstableCollectionOrdering.Id,
                CompletionAuthorityAnalyzer.CompletionOutsideAuthority.Id,
            }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            rewriteIds = typeof(RewriteDiagnosticIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(static field => new
                {
                    name = field.Name,
                    id = (string)field.GetRawConstantValue()!,
                })
                .OrderBy(static item => item.id, StringComparer.Ordinal)
                .ThenBy(static item => item.name, StringComparer.Ordinal)
                .ToArray(),
        },
        integration = new
        {
            progressOutputAppContextKey = GetConstant(
                typeof(ReplayArtifact).Assembly,
                "Orleans.Testing.Simulation.SimulationProgressOutput",
                "AppContextKey"),
        },
    };

    private static InstrumentationManifest CreateInstrumentationManifest() => new()
    {
        EngineVersion = "baseline",
        RuleSetId = "baseline",
        RuleSetVersion = "baseline",
        RuleSetSignature = new string('0', 64),
        Input = new ManifestAssemblyIdentity("baseline", "baseline.dll", null, false, "None"),
    };

    private static ClosureManifest CreateClosureManifest() => new()
    {
        EngineVersion = "baseline",
        RuleSetId = "baseline",
        RuleSetVersion = "baseline",
        RuleSetSignature = new string('0', 64),
        ConfigurationSignature = new string('1', 64),
        IncrementalKey = new string('2', 64),
    };

    private static string GetRewriteSignatureMetadataKey()
    {
        Type type = typeof(InstrumentationManifest).Assembly.GetType(
            "Orleans.Testing.Simulation.Instrumentation.Attributes.RewriteSignatureMetadata",
            throwOnError: true)!;
        return (string)type.GetField("Key", BindingFlags.Public | BindingFlags.Static)!
            .GetRawConstantValue()!;
    }

    private static string GetConstant(Assembly assembly, string typeName, string fieldName)
    {
        Type type = assembly.GetType(typeName, throwOnError: true)!;
        return (string)type.GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
    }

    private static string HashIds(IEnumerable<string> ids) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids))));

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

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
    };
}
