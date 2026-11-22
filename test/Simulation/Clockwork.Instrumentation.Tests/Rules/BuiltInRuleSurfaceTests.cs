using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rules;

/// <summary>
/// Classifies the whole surface of every type the built-in rules target against the reference pack this project
/// compiles against. A type is targeted when a rule names or substitutes it, or when one of its members' signatures
/// mentions a process-control type: a public class, struct, or interface of the assembly that declares
/// <see cref="System.Diagnostics.Process"/>. Members a delegate declares invoke its target and are not scanned.
/// Every public and protected method, constructor, accessor, operator, and field of a targeted type must be controlled,
/// rejected, substituted, or rebuilt by the rules, or listed in <see cref="UnrewrittenMembers"/> with the reason it stays
/// a BCL call; a listed member that the pack lacks or that a rule matches is stale. A runtime that adds a member to a
/// targeted type, or a process-control type to its process assembly, therefore fails these tests until the member is
/// classified, and a runtime that adds an assembly, or a public type to a namespace a targeted type is in, fails a
/// snapshot until the addition is reviewed.
/// </summary>
[Collection(ReferencePackGroup.Name)]
public sealed class BuiltInRuleSurfaceTests
{
    private const string SnapshotVariable = "ORLEANS_SIMULATION_UPDATE_SNAPSHOTS";

    private static readonly string[] KnownProcessControlTypes =
    [
        "Microsoft.Win32.SafeHandles.SafeProcessHandle",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.Runtime.InteropServices.UnixProcessStartArguments",
        "System.Runtime.InteropServices.WindowsProcessStartArguments",
    ];

    // The names of the members that start, signal, wait on, reschedule, or terminate a process, which no reason lists.
    private static readonly Regex ProcessControlVerb = new(
        "^(Start|StartAndForget|Run|RunAsync|RunAndCaptureText|RunAndCaptureTextAsync|Kill|Signal|Resume|CloseMainWindow|" +
        "(Try)?WaitFor\\w*|Begin(Output|Error)ReadLine|ReadAll\\w*|get_Responding|ResetIdealProcessor|" +
        "set_(EnableRaisingEvents|PriorityClass|PriorityBoostEnabled|PriorityLevel|ProcessorAffinity|IdealProcessor|" +
        "MaxWorkingSet|MinWorkingSet))$",
        RegexOptions.CultureInvariant);

    private static readonly Lazy<Surface> Targets = new(Surface.Find);

    public static TheoryData<string> TargetedTypes => [.. Targets.Value.TargetedTypes.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(TargetedTypes))]
    public void EveryMemberOfATargetedTypeIsClassified(string typeFullName)
    {
        Assert.True(ReferencePack.Shared.Types.TryGetValue(typeFullName, out TypeDefinition? type), $"'{typeFullName}' is not a public type of the reference pack.");
        Dictionary<string, UnrewrittenMember> listed = UnrewrittenMembers.All
            .Where(member => member.DeclaringTypeFullName == typeFullName)
            .GroupBy(member => member.MemberKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        var unclassified = new List<string>();
        var stale = new List<string>();
        foreach (IMemberDefinition member in ReferencePack.VisibleMembers(type))
        {
            string key = ReferencePack.MemberKey(member);
            Assert.True(members.Add(key), $"Two members of '{typeFullName}' are both named '{key}'.");
            RuleClassification classification = BuiltInRuleClassifier.Shared.Classify(member);
            bool needsReason = classification.Outcome is RuleOutcome.None or RuleOutcome.NewObjOnly;
            if (!listed.TryGetValue(key, out UnrewrittenMember entry))
            {
                if (needsReason)
                {
                    string reason = classification.Outcome == RuleOutcome.NewObjOnly ? $" // {nameof(UnrewrittenReason.ConstructorCall)}" : string.Empty;
                    unclassified.Add($"    \"{key}\",{reason}");
                }
            }
            else if (!needsReason)
            {
                stale.Add($"    \"{key}\" ({classification})");
            }
            else if ((classification.Outcome == RuleOutcome.NewObjOnly) != (entry.Reason == UnrewrittenReason.ConstructorCall))
            {
                stale.Add($"    \"{key}\" ({classification}, listed as {entry.Reason}: only a constructor a rule rewrites at newobj but not at call is {nameof(UnrewrittenReason.ConstructorCall)})");
            }
        }

        stale.AddRange(listed.Keys.Where(key => !members.Contains(key)).Select(key => $"    \"{key}\" (not a public or protected member)"));
        Assert.True(
            unclassified.Count == 0 && stale.Count == 0,
            $"'{typeFullName}' has members the built-in rules do not classify. Give each unclassified member a rule, or list it " +
            $"in {nameof(UnrewrittenMembers)} with the reason it stays a BCL call, and remove or correct each stale entry." + Environment.NewLine +
            "Unclassified:" + Environment.NewLine + string.Join(Environment.NewLine, unclassified) + Environment.NewLine +
            "Stale:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void EveryListedMemberBelongsToATargetedTypeOnce()
    {
        ImmutableHashSet<string> targeted = Targets.Value.TargetedTypes;
        string[] duplicates =
        [
            .. UnrewrittenMembers.All
                .GroupBy(member => member.QualifiedKey, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
        ];
        string[] untargeted =
        [
            .. UnrewrittenMembers.All
                .Select(member => member.DeclaringTypeFullName)
                .Where(type => !targeted.Contains(type))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.True(duplicates.Length == 0, "Members listed more than once:" + Environment.NewLine + string.Join(Environment.NewLine, duplicates));
        Assert.True(untargeted.Length == 0, "Listed members of types no rule targets:" + Environment.NewLine + string.Join(Environment.NewLine, untargeted));
    }

    [Fact]
    public void ProcessControlTypesIncludeEveryProcessLauncher() =>
        Assert.Superset(KnownProcessControlTypes.ToHashSet(StringComparer.Ordinal), Targets.Value.ProcessControlTypes.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void EveryMemberWithAProcessControlSignatureIsRejectedOrListed()
    {
        HashSet<string> listed = [.. UnrewrittenMembers.All.Select(member => member.QualifiedKey)];
        var violations = new List<string>();
        foreach (IMemberDefinition member in Targets.Value.ProcessControlMembers)
        {
            RuleClassification classification = BuiltInRuleClassifier.Shared.Classify(member);
            if (classification.Outcome == RuleOutcome.Rejected)
            {
                continue;
            }

            string name = ReferencePack.QualifiedKey(member);
            if (ProcessControlVerb.IsMatch(member.Name))
            {
                violations.Add($"{name} ({classification}): it starts, signals, waits on, reschedules, or terminates a process, so a rule must reject it");
            }
            else if (!listed.Contains(name))
            {
                violations.Add($"{name} ({classification})");
            }
        }

        Assert.Contains(Targets.Value.ProcessControlMembers, member => ProcessControlVerb.IsMatch(member.Name));
        Assert.True(
            violations.Count == 0,
            "Members whose signatures mention a process-control type must be rejected or listed:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ReferencePackAssembliesMatchTheSnapshot() =>
        AssertMatchesSnapshot(
            "ReferencePackAssemblies.txt",
            ReferencePack.Shared.AssemblyFileNames,
            "Review each added assembly for members that read a clock, entropy, or host state, schedule or block work, or " +
            "start or control a process, and give them rules (BuiltInRuleSets) when they need control.");

    [Fact]
    public void PublicTypesOfTargetedNamespacesMatchTheSnapshot()
    {
        ReferencePack pack = ReferencePack.Shared;
        HashSet<string> namespaces = [.. Targets.Value.TargetedTypes.Select(name => NamespaceOf(pack.Types[name]))];
        AssertMatchesSnapshot(
            "ReferencePackTypes.txt",
            [.. pack.Types.Values.Where(type => namespaces.Contains(NamespaceOf(type))).Select(type => type.FullName).Order(StringComparer.Ordinal)],
            "Review each added type for members that read a clock, entropy, or host state, schedule or block work, or start " +
            "or control a process, and give them rules (BuiltInRuleSets) when they need control; the theory then classifies " +
            "the rest of the type.");
    }

    private static string NamespaceOf(TypeDefinition type)
    {
        while (type.IsNested)
        {
            type = type.DeclaringType;
        }

        return type.Namespace;
    }

    private static void AssertMatchesSnapshot(string fileName, IReadOnlyList<string> actual, string review)
    {
        string path = Repository.PathOf("test", "Simulation", "Clockwork.Instrumentation.Tests", "Rules", fileName);
        if (Environment.GetEnvironmentVariable(SnapshotVariable) == "1")
        {
            File.WriteAllText(path, string.Join('\n', actual) + "\n");
        }

        string[] expected = File.Exists(path) ? [.. File.ReadAllLines(path).Where(line => line.Length > 0)] : [];
        string[] added = [.. actual.Except(expected, StringComparer.Ordinal)];
        string[] removed = [.. expected.Except(actual, StringComparer.Ordinal)];
        Assert.True(
            added.Length == 0 && removed.Length == 0,
            $"The reference pack at '{ReferencePack.Shared.Directory}' no longer matches '{path}'." + Environment.NewLine +
            "Added: " + string.Join(", ", added) + Environment.NewLine +
            "Removed: " + string.Join(", ", removed) + Environment.NewLine +
            review + $" Then refresh the snapshots with `$env:{SnapshotVariable}='1'; dotnet run --project " +
            "tests/Orleans.Testing.Simulation.Instrumentation.Tests/Orleans.Testing.Simulation.Instrumentation.Tests.csproj -- --filter-class " +
            $"{typeof(BuiltInRuleSurfaceTests).FullName}`.");
    }

    // The targeted types and the process-control signatures, found by one scan of the reference pack.
    private sealed record Surface(
        ImmutableHashSet<string> TargetedTypes,
        ImmutableHashSet<string> ProcessControlTypes,
        ImmutableArray<IMemberDefinition> ProcessControlMembers)
    {
        public static Surface Find()
        {
            ReferencePack pack = ReferencePack.Shared;
            ModuleDefinition processModule = pack.Types["System.Diagnostics.Process"].Module;
            ImmutableHashSet<string> processControlTypes =
            [
                .. processModule.GetTypes()
                    .Where(type => ReferencePack.IsVisible(type) && !type.IsEnum && !IsDelegate(type))
                    .Select(type => type.FullName),
            ];

            // A delegate's members invoke its target, which is classified where it is declared.
            ImmutableArray<IMemberDefinition> processControlMembers =
            [
                .. pack.Types.Values
                    .Where(type => !IsDelegate(type))
                    .OrderBy(type => type.FullName, StringComparer.Ordinal)
                    .SelectMany(ReferencePack.VisibleMembers)
                    .Where(member => SignatureMentions.Find((MemberReference)member, processControlTypes).Count > 0),
            ];
            ImmutableHashSet<string> targetedTypes =
            [
                .. BuiltInRuleClassifier.Shared.Rules.Select(rule => rule.Target.DeclaringTypeFullName),
                .. processControlMembers.Select(member => member.DeclaringType.FullName),
            ];
            return new Surface(targetedTypes, processControlTypes, processControlMembers);
        }

        private static bool IsDelegate(TypeDefinition type) => type.BaseType?.FullName == "System.MulticastDelegate";
    }
}
