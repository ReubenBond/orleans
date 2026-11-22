using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Configuration;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Policy;
using Orleans.Testing.Simulation.Runtime.Shims;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Rules;

/// <summary>
/// Verifies the built-in deterministic BCL rule set: its inventory is coherent (unique ids, expected
/// families, every controlled signature mapped), every replacement actually resolves in the shipped
/// <c>Clockwork</c> assembly, and the family selection / strict-guard plumbing behaves.
/// </summary>
public sealed class BuiltInRuleSetsTests
{
    private static string ShimAssemblyPath => Path.Combine(AppContext.BaseDirectory, "Orleans.Testing.Simulation.dll");

    [Fact]
    public void InventoryHasUniqueIdsAndTargetsTheShimAssembly()
    {
        ImmutableArray<(BuiltInRuleFamily Family, RewriteRule Rule)> inventory = BuiltInRuleSets.DeterministicBclInventory;

        Assert.Equal("clockwork.bcl.deterministic", BuiltInRuleSets.DeterministicBclId);
        Assert.Equal("3.0.0", BuiltInRuleSets.DeterministicBclVersion);
        Assert.NotEmpty(inventory);
        Assert.Equal(inventory.Length, inventory.Select(e => e.Rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(inventory, e => Assert.Equal(BuiltInRuleSets.ShimAssemblyName, e.Rule.Replacement.AssemblyName));
        Assert.All(inventory, e => Assert.StartsWith("clockwork.bcl.", e.Rule.Id, StringComparison.Ordinal));

        Assert.All(inventory, e => Assert.Equal(SimulationApiPolicy.Controlled, e.Rule.Policy));
    }

    [Fact]
    public void InventoryTargetsOnlyVersionTwoControlledBclTypes()
    {
        string[] expected =
        [
            "Orleans.Testing.Simulation.Shims.System.ControlledDateTime",
            "Orleans.Testing.Simulation.Shims.System.ControlledDateTimeOffset",
            "Orleans.Testing.Simulation.Shims.System.ControlledEnvironment",
            "Orleans.Testing.Simulation.Shims.System.ControlledGuid",
            "Orleans.Testing.Simulation.Shims.System.ControlledRandom",
            "Orleans.Testing.Simulation.Shims.System.Diagnostics.ControlledStopwatch",
            "Orleans.Testing.Simulation.Shims.System.Security.Cryptography.ControlledRandomNumberGenerator",
        ];
        string[] actual = BuiltInRuleSets.DeterministicBclInventory
            .Select(entry => entry.Rule.Replacement.DeclaringTypeFullName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, type => type.Contains("Deterministic", StringComparison.Ordinal));
    }

    [Fact]
    public void CoversEveryControlledSignature()
    {
        var targets = BuiltInRuleSets.DeterministicBclInventory
            .Select(e => Display(e.Rule.Target))
            .ToHashSet(StringComparer.Ordinal);

        string[] expected =
        [
            "System.DateTime::get_Now()",
            "System.DateTime::get_UtcNow()",
            "System.DateTime::get_Today()",
            "System.DateTimeOffset::get_Now()",
            "System.DateTimeOffset::get_UtcNow()",
            "System.Diagnostics.Stopwatch::GetTimestamp()",
            "System.Diagnostics.Stopwatch::GetElapsedTime(System.Int64)",
            "System.Environment::get_TickCount()",
            "System.Environment::get_TickCount64()",
            "System.Guid::NewGuid()",
            "System.Guid::CreateVersion7()",
            "System.Guid::CreateVersion7(System.DateTimeOffset)",
            "System.Random::get_Shared()",
            "System.Random::.ctor()",
            "System.Random::.ctor(System.Int32)",
            "System.Security.Cryptography.RandomNumberGenerator::Create()",
            "System.Security.Cryptography.RandomNumberGenerator::Create(System.String)",
            "System.Security.Cryptography.RandomNumberGenerator::Fill(System.Span`1<System.Byte>)",
            "System.Security.Cryptography.RandomNumberGenerator::GetBytes(System.Int32)",
            "System.Security.Cryptography.RandomNumberGenerator::GetInt32(System.Int32)",
            "System.Security.Cryptography.RandomNumberGenerator::GetInt32(System.Int32,System.Int32)",
            "System.Security.Cryptography.RandomNumberGenerator::GetItems(System.ReadOnlySpan`1<!!0>,System.Span`1<!!0>)",
            "System.Security.Cryptography.RandomNumberGenerator::GetItems(System.ReadOnlySpan`1<!!0>,System.Int32)",
            "System.Security.Cryptography.RandomNumberGenerator::GetHexString(System.Span`1<System.Char>,System.Boolean)",
            "System.Security.Cryptography.RandomNumberGenerator::GetHexString(System.Int32,System.Boolean)",
            "System.Security.Cryptography.RandomNumberGenerator::GetString(System.ReadOnlySpan`1<System.Char>,System.Int32)",
            "System.Security.Cryptography.RandomNumberGenerator::Shuffle(System.Span`1<!!0>)",
        ];

        foreach (string signature in expected)
        {
            Assert.Contains(signature, targets);
        }

        Assert.Equal(expected.Length, targets.Count);
    }

    [Fact]
    public void EveryReplacementResolvesToExactlyOneStaticShimMethod()
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(ShimAssemblyPath);

        foreach ((_, RewriteRule rule) in BuiltInRuleSets.DeterministicBclInventory)
        {
            RewriteReplacement replacement = rule.Replacement;
            TypeDefinition? type = module.GetType(replacement.DeclaringTypeFullName);
            Assert.True(type is not null, $"Shim type '{replacement.DeclaringTypeFullName}' missing for rule '{rule.Id}'.");

            List<MethodDefinition> matches = type!.Methods
                .Where(m => m.Name == replacement.MemberName
                    && m.IsStatic
                    && m.IsPublic
                    && ParametersMatch(m, replacement.ParameterTypeFullNames))
                .ToList();

            Assert.True(matches.Count == 1, $"Rule '{rule.Id}' resolved {matches.Count} shim methods for '{replacement.ToCanonicalString()}'.");
        }
    }

    [Fact]
    public void ControlledTaskInventoryIsVersionTwoAndEveryReplacementResolves()
    {
        Assert.Equal("3.0.0", BuiltInRuleSets.ControlledTasksVersion);
        using ModuleDefinition module = ModuleDefinition.ReadModule(ShimAssemblyPath);

        foreach ((_, RewriteRule rule) in BuiltInRuleSets.ControlledTasksInventory)
        {
            RewriteReplacement replacement = rule.Replacement;
            TypeDefinition? type = module.GetType(replacement.DeclaringTypeFullName);
            Assert.True(type is not null, $"Shim type '{replacement.DeclaringTypeFullName}' missing for rule '{rule.Id}'.");

            if (rule.Operation == RewriteOperationKind.SubstituteType)
            {
                continue;
            }

            List<MethodDefinition> matches = type!.Methods
                .Where(m => m.Name == replacement.MemberName
                    && m.IsStatic
                    && m.IsPublic
                    && ParametersMatch(m, replacement.ParameterTypeFullNames))
                .ToList();
            Assert.True(matches.Count == 1, $"Rule '{rule.Id}' resolved {matches.Count} shim methods for '{replacement.ToCanonicalString()}'.");
        }
    }

    [Fact]
    public void ModernSynchronizationFamiliesHaveExactControlledAndRejectedRuleCounts()
    {
        (BuiltInRuleFamily Family, int Controlled, int Rejected)[] expected =
        [
            (BuiltInRuleFamily.ReaderWriterLockSlim, 26, 0),
            (BuiltInRuleFamily.ManualResetEventSlim, 15, 0),
            (BuiltInRuleFamily.Mutex, 3, 9),
            (BuiltInRuleFamily.KernelSemaphore, 3, 8),
            (BuiltInRuleFamily.SpinLock, 1, 0),
            (BuiltInRuleFamily.ExecutionContext, 8, 1),
            (BuiltInRuleFamily.SynchronizationContext, 8, 1),
            (BuiltInRuleFamily.Barrier, 1, 0),
            (BuiltInRuleFamily.CountdownEvent, 1, 0),
        ];

        foreach ((BuiltInRuleFamily family, int controlled, int rejected) in expected)
        {
            RewriteRule[] rules = BuiltInRuleSets.ControlledTasksInventory
                .Where(entry => entry.Family == family)
                .Select(entry => entry.Rule)
                .ToArray();
            Assert.Equal(controlled, rules.Count(rule => rule.Policy == SimulationApiPolicy.Controlled));
            Assert.Equal(rejected, rules.Count(rule => rule.Policy == SimulationApiPolicy.Rejected));
        }
    }

    private static bool ParametersMatch(MethodDefinition method, ImmutableArray<string> parameterTypeFullNames)
    {
        if (parameterTypeFullNames.IsDefault)
        {
            return true;
        }

        if (method.Parameters.Count != parameterTypeFullNames.Length)
        {
            return false;
        }

        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (method.Parameters[i].ParameterType.FullName != parameterTypeFullNames[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string Display(MemberSignature signature) =>
        signature.MemberName is null
            ? signature.DeclaringTypeFullName
            : $"{signature.DeclaringTypeFullName}::{signature.MemberName}(" +
                string.Join(",", signature.ParameterTypeFullNames) + ")";

    [Fact]
    public void BuildRestrictsToSelectedFamilies()
    {
        RewriteRuleSet clockOnly = BuiltInRuleSets.BuildDeterministicBcl([BuiltInRuleFamily.Clock]);
        Assert.All(clockOnly.Rules, r => Assert.StartsWith("clockwork.bcl.", r.Id, StringComparison.Ordinal));
        Assert.Contains(clockOnly.Rules, r => r.Id == "clockwork.bcl.datetime.utcnow");
        Assert.DoesNotContain(clockOnly.Rules, r => r.Id == "clockwork.bcl.guid.newguid");

        RewriteRuleSet all = BuiltInRuleSets.BuildDeterministicBcl(BuiltInRuleSets.AllFamilies);
        Assert.Equal(BuiltInRuleSets.DeterministicBclInventory.Length, all.Rules.Length);

        // Selecting fewer families changes the content signature (feeds the incremental key).
        Assert.NotEqual(all.ComputeSignature(), clockOnly.ComputeSignature());
    }

    [Fact]
    public void EmptyFamilySelectionProducesEmptyRuleSet()
    {
        RewriteRuleSet none = BuiltInRuleSets.BuildDeterministicBcl([]);
        Assert.Empty(none.Rules);
    }
}
