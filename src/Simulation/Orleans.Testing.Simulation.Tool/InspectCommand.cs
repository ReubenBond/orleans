using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Inspection;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Signing;

namespace Orleans.Testing.Simulation.Tool;

/// <summary>
/// The <c>inspect</c> command: reports the managed / ReadyToRun / mixed-mode shape, strong-name state,
/// debug-symbol form, and Orleans simulation idempotence marker of one or more assemblies (given as files or
/// directories). When a rule set is supplied it also reports the merged rule-set identity and whether
/// each already-instrumented assembly's marker matches that rule set's signature. Every value is an
/// observed fact; nothing is claimed that is not read from the file. Output is text or JSON.
/// </summary>
internal static class InspectCommand
{
    public static ExitCode Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var valueOptions = new HashSet<string>(StringComparer.Ordinal) { "source" };
        valueOptions.UnionWith(ConfigurationFactory.ValueOptions);

        ArgumentReader reader = ArgumentReader.Parse(args, valueOptions);
        bool json = reader.GetFlag("json");

        var targets = new List<string>();
        foreach (string positional in reader.Positional)
        {
            targets.Add(positional);
        }

        if (reader.GetString("source") is { } source)
        {
            targets.Add(source);
        }

        string? expectedSignature = null;
        RewriteRuleSet? ruleSet = null;
        if (ConfigurationFactory.ValueOptions.Any(reader.IsSupplied))
        {
            InstrumentationConfiguration configuration = ConfigurationFactory.Build(reader);
            ruleSet = RuleSetMerge.LoadAndMerge(configuration).RuleSet;
            expectedSignature = ruleSet.ComputeSignature();
        }
        else
        {
            // Ensure config-related options are still marked consumed for validation.
            _ = ConfigurationFactory.ValueOptions;
        }

        reader.EnsureAllConsumed();

        if (targets.Count == 0)
        {
            throw new UsageException("The 'inspect' command requires at least one assembly or directory path.");
        }

        List<string> files = ExpandTargets(targets);
        if (files.Count == 0)
        {
            throw new UsageException("No assemblies were found at the given paths.");
        }

        var inspections = files
            .Select(AssemblyInspector.Inspect)
            .OrderBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        if (json)
        {
            output.WriteLine(WriteJson(inspections, ruleSet, expectedSignature));
        }
        else
        {
            WriteText(inspections, ruleSet, expectedSignature, output);
        }

        return ExitCode.Success;
    }

    private static List<string> ExpandTargets(IEnumerable<string> targets)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string target in targets)
        {
            if (Directory.Exists(target))
            {
                foreach (string dll in Directory.EnumerateFiles(target, "*.dll", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetFullPath(dll));
                }

                foreach (string exe in Directory.EnumerateFiles(target, "*.exe", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetFullPath(exe));
                }
            }
            else if (File.Exists(target))
            {
                files.Add(Path.GetFullPath(target));
            }
            else
            {
                throw new UsageException($"Path '{target}' was not found.");
            }
        }

        return [.. files];
    }

    private static string DescribeImage(AssemblyInspection inspection)
    {
        if (!inspection.IsManaged)
        {
            return "native/non-managed";
        }

        if (inspection.Image.IsReadyToRun)
        {
            return "ReadyToRun (AOT native image)";
        }

        return inspection.Image.IsMixedMode ? "mixed-mode (managed + native)" : "IL-only";
    }

    private static void WriteText(
        IReadOnlyList<AssemblyInspection> inspections,
        RewriteRuleSet? ruleSet,
        string? expectedSignature,
        TextWriter output)
    {
        if (ruleSet is not null)
        {
            output.WriteLine($"Rule set: {ruleSet.Id} v{ruleSet.Version} ({ruleSet.Rules.Length} rules), signature {expectedSignature}");
        }

        foreach (AssemblyInspection inspection in inspections)
        {
            output.WriteLine(inspection.Path);
            output.WriteLine($"  image:       {DescribeImage(inspection)}");
            if (inspection.IsManaged)
            {
                output.WriteLine($"  strongName:  {inspection.StrongName.Status}{Token(inspection.StrongName)}");
                output.WriteLine($"  authenticode:{(inspection.Image.HasAuthenticodeSignature ? " present (preserved, never re-applied)" : " none")}");
                output.WriteLine($"  symbols:     {inspection.Symbols}");
                output.WriteLine($"  instrumented:{DescribeMarker(inspection, expectedSignature)}");
            }
        }
    }

    private static string WriteJson(
        IReadOnlyList<AssemblyInspection> inspections,
        RewriteRuleSet? ruleSet,
        string? expectedSignature)
    {
        var array = new JsonArray();
        foreach (AssemblyInspection inspection in inspections)
        {
            var node = new JsonObject
            {
                ["path"] = inspection.Path,
                ["managed"] = inspection.IsManaged,
                ["image"] = DescribeImage(inspection),
                ["readyToRun"] = inspection.Image.IsReadyToRun,
                ["mixedMode"] = inspection.IsManaged && inspection.Image.IsMixedMode,
                ["authenticode"] = inspection.Image.HasAuthenticodeSignature,
                ["strongName"] = inspection.StrongName.Status.ToString(),
                ["publicKeyToken"] = inspection.StrongName.PublicKeyToken,
                ["symbols"] = inspection.Symbols.ToString(),
                ["instrumented"] = inspection.IsInstrumented,
            };

            if (inspection.Marker is { } marker)
            {
                node["marker"] = new JsonObject
                {
                    ["engineVersion"] = marker.EngineVersion,
                    ["ruleSetId"] = marker.RuleSetId,
                    ["ruleSetVersion"] = marker.RuleSetVersion,
                    ["signature"] = marker.Signature,
                    ["optionsFingerprint"] = marker.OptionsFingerprint,
                    ["matchesRuleSet"] = expectedSignature is null ? null : marker.Signature == expectedSignature,
                };
            }

            array.Add(node);
        }

        var doc = new JsonObject
        {
            ["command"] = "inspect",
            ["assemblies"] = array,
        };

        if (ruleSet is not null)
        {
            doc["ruleSet"] = new JsonObject
            {
                ["id"] = ruleSet.Id,
                ["version"] = ruleSet.Version,
                ["ruleCount"] = ruleSet.Rules.Length,
                ["signature"] = expectedSignature,
            };
        }

        return doc.ToJsonString(Indented);
    }

    private static string Token(StrongNameInfo info) =>
        info.PublicKeyToken is { Length: > 0 } token ? $" (pkt {token})" : string.Empty;

    private static string DescribeMarker(AssemblyInspection inspection, string? expectedSignature)
    {
        if (inspection.Marker is not { } marker)
        {
            return " no";
        }

        string match = expectedSignature is null
            ? string.Empty
            : marker.Signature == expectedSignature ? " (matches rule set)" : " (DIFFERENT rule set)";
        return $" yes: engine {marker.EngineVersion}, rules {marker.RuleSetId} v{marker.RuleSetVersion}{match}";
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
