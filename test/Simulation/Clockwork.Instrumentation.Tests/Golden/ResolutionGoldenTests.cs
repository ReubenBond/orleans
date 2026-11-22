using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for strict resolution: a targeted member whose replacement cannot be resolved is a
/// hard failure (<c>CWR0001</c>) - the engine never silently skips a targeted call.
/// </summary>
public sealed class ResolutionGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Resolve
            {
                public static long Ticks() => RealClock.UtcNowTicks();
            }
        }
        """;

    [Fact]
    public void UnresolvableTargetedReplacementFails()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Resolve", Fixture);

        var ruleSet = new RewriteRuleSet(
            "clockwork.fixtures.bad",
            "1.0",
            [
                RewriteRule.RedirectCall(
                    "redirect-missing",
                    MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
                    RewriteReplacement.Method(
                        FixtureSources.ShimAssemblyName,
                        "ClockworkFixtures.Shims.ClockShim",
                        "ThisMethodDoesNotExist")),
            ]);

        var result = context.Rewrite(fixturePath, ruleSet);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, d => d.Id == RewriteDiagnosticIds.UnresolvedReplacement);
        Assert.False(result.WasWritten);
    }

    [Fact]
    public void TargetedCallOutsideSupportedRuntimeFails()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Resolve.Runtime", Fixture);
        RewriteRule rule = RewriteRule.RedirectCall(
            "redirect-runtime-specific",
            MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
            RewriteReplacement.Method(
                FixtureSources.ShimAssemblyName,
                "ClockworkFixtures.Shims.ClockShim",
                "UtcNowTicks")) with
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
            new RewriteRuleSet("clockwork.fixtures.runtime", "1.0", [rule]),
            options);

        Assert.False(result.Succeeded);
        Assert.Equal(RewriteDiagnosticIds.RuntimeOutOfRange, Assert.Single(result.Errors).Id);
        Assert.False(result.WasWritten);
        Assert.Empty(result.Manifest.Transformations);
    }

    [Fact]
    public void LaterNet10RuleAppliesWhenEarlierNet11RuleIsOutOfRange()
    {
        AssertRuntimeRuleSelected(
            [RuntimeSpecificRule(11), RuntimeSpecificRule(10)],
            targetRuntime: 10,
            expectedRuleId: "redirect-net10");
    }

    [Fact]
    public void LaterNet11RuleAppliesWhenEarlierNet10RuleIsOutOfRange()
    {
        AssertRuntimeRuleSelected(
            [RuntimeSpecificRule(10), RuntimeSpecificRule(11)],
            targetRuntime: 11,
            expectedRuleId: "redirect-net11");
    }

    private static void AssertRuntimeRuleSelected(
        IReadOnlyList<RewriteRule> rules,
        int targetRuntime,
        string expectedRuleId)
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture($"Fx.Resolve.Net{targetRuntime}", Fixture);
        var options = new RewriteOptions
        {
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
            TargetRuntime = new Version(targetRuntime, 0),
        };

        string outputPath = fixturePath + ".rewritten.dll";
        RewriteResult result = context.Rewrite(
            fixturePath,
            outputPath,
            new RewriteRuleSet("clockwork.fixtures.runtime-order", "1.0", rules),
            options);

        result.EnsureSuccess();
        Assert.Equal(expectedRuleId, Assert.Single(result.Manifest.Transformations).RuleId);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Id == RewriteDiagnosticIds.RuntimeOutOfRange);

        // The output records the runtimes the in-range rule supports, which a simulation checks when it starts.
        Assert.Equal($"[{targetRuntime}.0,{targetRuntime}.0]", ReadSupportedRuntimes(context, outputPath));
    }

    [Fact]
    public void OutputRecordsTheRuntimesEveryRuleThatSupportsTheTargetSupports()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Resolve.Range", Fixture);
        string outputPath = fixturePath + ".rewritten.dll";
        RewriteRule clock = RuntimeSpecificRule(11) with
        {
            SupportedRuntimes = new RuntimeVersionRange(new Version(10, 0), new Version(12, 0)),
        };
        RewriteRule unused = clock with
        {
            Id = "unused",
            Target = MemberSignature.Method("ClockworkFixtures.Api.RealClock", "Unused"),
            SupportedRuntimes = RuntimeVersionRange.AtLeast(new Version(11, 0)),
        };
        var options = new RewriteOptions
        {
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
            TargetRuntime = new Version(11, 0),
        };

        context.Rewrite(fixturePath, outputPath, new RewriteRuleSet("clockwork.fixtures.range", "1.0", [clock, unused]), options)
            .EnsureSuccess();

        Assert.Equal("[11.0,12.0]", ReadSupportedRuntimes(context, outputPath));
    }

    private static string? ReadSupportedRuntimes(RewriteTestContext context, string assemblyPath)
    {
        using ModuleDefinition module = context.LoadModule(assemblyPath);
        return module.Assembly.CustomAttributes
            .Where(attribute => attribute.AttributeType.FullName == typeof(System.Reflection.AssemblyMetadataAttribute).FullName
                && (string)attribute.ConstructorArguments[0].Value == "Clockwork.SupportedRuntimes")
            .Select(attribute => (string)attribute.ConstructorArguments[1].Value)
            .SingleOrDefault();
    }

    private static RewriteRule RuntimeSpecificRule(int runtime)
    {
        var version = new Version(runtime, 0);
        return RewriteRule.RedirectCall(
            $"redirect-net{runtime}",
            MemberSignature.Method("ClockworkFixtures.Api.RealClock", "UtcNowTicks"),
            RewriteReplacement.Method(
                FixtureSources.ShimAssemblyName,
                "ClockworkFixtures.Shims.ClockShim",
                "UtcNowTicks")) with
        {
            SupportedRuntimes = new RuntimeVersionRange(version, version),
        };
    }
}
