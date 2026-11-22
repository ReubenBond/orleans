using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for <see cref="RewriteOperationKind.SubstituteType"/>: type-reference operands in a
/// method body (<c>ldtoken</c>, <c>isinst</c>, <c>newarr</c>) are replaced with an imported reference
/// to the substitute type.
/// </summary>
public sealed class TypeSubstitutionGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Types
            {
                public static string Name() => typeof(LegacyMarker).Name;
                public static bool Check(object o) => o is LegacyMarker;
                public static LegacyMarker[] Arr() => new LegacyMarker[2];
            }
        }
        """;

    private static RewriteRuleSet SubstitutionRuleSet() => new(
        "clockwork.fixtures.types",
        "1.0",
        [
            RewriteRule.SubstituteType(
                "substitute-marker",
                "ClockworkFixtures.Api.LegacyMarker",
                RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernMarker")),
        ]);

    [Fact]
    public void TypeOperandsAreSubstituted()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.TypeSub", Fixture);

        var result = context.Rewrite(fixturePath, SubstitutionRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TypeSub.rewritten.dll"));

        foreach (string methodName in new[] { "Name", "Check", "Arr" })
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.Types", methodName);
            List<string> operands = CecilInspect.TypeOperands(method);
            Assert.DoesNotContain(operands, o => o.Contains("LegacyMarker", StringComparison.Ordinal));
        }

        MethodDefinition name = CecilInspect.GetMethod(module, "Fx.Types", "Name");
        Assert.Contains(CecilInspect.TypeOperands(name), o => o.Contains("ModernMarker", StringComparison.Ordinal));
    }

    [Fact]
    public void SubstitutionIsRecordedInManifest()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.TypeSubManifest", Fixture);

        var result = context.Rewrite(fixturePath, SubstitutionRuleSet());
        result.EnsureSuccess();

        Assert.Contains(result.Manifest.Transformations, t => t.RuleId == "substitute-marker");
        Assert.All(
            result.Manifest.Transformations,
            t => Assert.Equal(RewriteOperationKind.SubstituteType, t.Operation));
    }

    [Fact]
    public void TargetedTypeOutsideSupportedRuntimeFails()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.TypeSub.Runtime", Fixture);
        RewriteRule rule = RewriteRule.SubstituteType(
            "substitute-runtime-specific",
            "ClockworkFixtures.Api.LegacyMarker",
            RewriteReplacement.Type(FixtureSources.ShimAssemblyName, "ClockworkFixtures.Shims.ModernMarker")) with
        {
            SupportedRuntimes = RuntimeVersionRange.AtLeast(new Version(11, 0)),
        };
        var options = new RewriteOptions
        {
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
            TargetRuntime = new Version(10, 0),
        };

        RewriteResult result = context.Rewrite(
            fixturePath,
            new RewriteRuleSet("clockwork.fixtures.types.runtime", "1.0", [rule]),
            options);

        Assert.False(result.Succeeded);
        Assert.Equal(RewriteDiagnosticIds.RuntimeOutOfRange, Assert.Single(result.Errors).Id);
        Assert.False(result.WasWritten);
    }

    [Fact]
    public void UnresolvableTargetedTypeReplacementFails()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.TypeSub.Missing", Fixture);
        var ruleSet = new RewriteRuleSet(
            "clockwork.fixtures.types.missing",
            "1.0",
            [
                RewriteRule.SubstituteType(
                    "substitute-missing",
                    "ClockworkFixtures.Api.LegacyMarker",
                    RewriteReplacement.Type("Missing", "Missing.ModernMarker")),
            ]);

        RewriteResult result = context.Rewrite(fixturePath, ruleSet);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Id == RewriteDiagnosticIds.UnresolvedReplacement);
        Assert.False(result.WasWritten);
    }

}
