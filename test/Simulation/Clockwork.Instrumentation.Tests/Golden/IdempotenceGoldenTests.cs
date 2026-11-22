using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Shims;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the assembly-level idempotence marker: a first rewrite stamps the signature,
/// re-running with the same rule set is a verified no-op, and re-running with an incompatible rule-set
/// version fails clearly rather than double-rewriting.
/// </summary>
public sealed class IdempotenceGoldenTests
{
    private const string Fixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Idem
            {
                public static long Ticks() => RealClock.UtcNowTicks();
            }
        }
        """;

    [Fact]
    public void FirstRewriteStampsSignature()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Idem1", Fixture);
        string outputPath = Path.Combine(context.Directory, "Fx.Idem1.rewritten.dll");

        var result = context.Rewrite(fixturePath, outputPath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        Assert.True(result.WasWritten);
        Assert.False(result.WasNoOp);

        using ModuleDefinition module = context.LoadModule(outputPath);
        Assert.True(CecilInspect.HasRewriteSignature(module));
    }

    [Fact]
    public void RewrittenInputWithoutSupportedRuntimesIsNotAVerifiedNoOp()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.IdemRuntimes", Fixture);
        string firstOut = Path.Combine(context.Directory, "Fx.IdemRuntimes.rewritten.dll");
        string strippedOut = Path.Combine(context.Directory, "Fx.IdemRuntimes.stripped.dll");
        context.Rewrite(fixturePath, firstOut, RewriteTestContext.StandardRuleSet()).EnsureSuccess();
        using (var assembly = AssemblyDefinition.ReadAssembly(firstOut))
        {
            assembly.CustomAttributes.Remove(assembly.CustomAttributes.Single(
                attribute => attribute.ConstructorArguments.Count == 2
                    && (string)attribute.ConstructorArguments[0].Value == "Clockwork.SupportedRuntimes"));
            assembly.Write(strippedOut);
        }

        RewriteResult second = context.Rewrite(
            strippedOut,
            Path.Combine(context.Directory, "Fx.IdemRuntimes.twice.dll"),
            RewriteTestContext.StandardRuleSet());

        Assert.False(second.Succeeded);
        RewriteDiagnostic diagnostic = Assert.Single(
            second.Errors,
            error => error.Id == RewriteDiagnosticIds.IncompatibleRewriteVersion);
        Assert.Contains("existing supported runtimes 'none', requested '[,]'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReRewriteWithSameRulesIsVerifiedNoOp()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Idem2", Fixture);
        string firstOut = Path.Combine(context.Directory, "Fx.Idem2.rewritten.dll");
        context.Rewrite(fixturePath, firstOut, RewriteTestContext.StandardRuleSet()).EnsureSuccess();

        string secondOut = Path.Combine(context.Directory, "Fx.Idem2.twice.dll");
        var second = context.Rewrite(firstOut, secondOut, RewriteTestContext.StandardRuleSet());
        second.EnsureSuccess();

        Assert.True(second.WasNoOp);
        Assert.True(second.Succeeded);
        Assert.Empty(second.Manifest.Transformations);
        Assert.Contains(second.Diagnostics, d => d.Id == RewriteDiagnosticIds.AlreadyRewritten);
    }

    [Fact]
    public void ReRewriteWithIncompatibleVersionFails()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Idem3", Fixture);
        string firstOut = Path.Combine(context.Directory, "Fx.Idem3.rewritten.dll");
        context.Rewrite(fixturePath, firstOut, RewriteTestContext.StandardRuleSet("1.0")).EnsureSuccess();

        string secondOut = Path.Combine(context.Directory, "Fx.Idem3.twice.dll");
        var second = context.Rewrite(firstOut, secondOut, RewriteTestContext.StandardRuleSet("2.0"));

        Assert.False(second.Succeeded);
        Assert.Contains(second.Errors, d => d.Id == RewriteDiagnosticIds.IncompatibleRewriteVersion);
        Assert.False(second.WasWritten);
    }

    [Fact]
    public void VersionOneBuiltInRewriteIsRejectedByCurrentVersionTwoRules()
    {
        const string source = """
            namespace Fx
            {
                public static class BuiltInVersion
                {
                    public static System.DateTime UtcNow() => System.DateTime.UtcNow;
                }
            }
            """;
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.BuiltInVersion", source);
        string firstOut = Path.Combine(context.Directory, "Fx.BuiltInVersion.v1.dll");
        RewriteRuleSet current = BuiltInRuleSets.BuildDeterministicBcl([BuiltInRuleFamily.Clock]);
        var versionOne = new RewriteRuleSet(current.Id, "1.0.0", current.Rules);
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [typeof(SimulationRuntimeDispatch).Assembly.Location],
            ReferenceSearchDirectories = [AppContext.BaseDirectory],
        };

        RewriteResult first = context.Rewrite(fixturePath, firstOut, versionOne, options);
        first.EnsureSuccess();
        Assert.Contains(
            first.Manifest.Transformations,
            transformation => transformation.RuleId == "clockwork.bcl.datetime.utcnow");
        RewriteResult second = context.Rewrite(
            firstOut,
            Path.Combine(context.Directory, "Fx.BuiltInVersion.v2.dll"),
            current,
            options);

        Assert.False(second.Succeeded);
        RewriteDiagnostic diagnostic = Assert.Single(
            second.Errors,
            error => error.Id == RewriteDiagnosticIds.IncompatibleRewriteVersion);
        Assert.Equal("3.0.0", current.Version);
        Assert.Contains(
            "'clockwork.bcl.deterministic' v1.0.0",
            diagnostic.Message,
            StringComparison.Ordinal);
        Assert.False(second.WasWritten);
    }

    [Fact]
    public void EverySemanticOptionChangeRequiresCleanInput()
    {
        using var context = RewriteTestContext.Create();
        var baseline = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [context.ShimPath],
            ReferenceSearchDirectories = [context.Directory],
        };
        (string Name, RewriteOptions Options)[] variants =
        [
            ("replacement assemblies", baseline with { ReplacementAssemblyPaths = [context.ShimPath, context.ApiPath] }),
            ("reference search directories", baseline with { ReferenceSearchDirectories = [context.Directory, Path.GetTempPath()] }),
            ("target runtime", baseline with { TargetRuntime = new Version(10, 0) }),
            ("excluded types", baseline with { ExcludedTypeFullNames = ["Fx.Other"] }),
            ("unresolved-reference warnings", baseline with { WarnOnUnresolvedReferences = false }),
            ("output hashing", baseline with { ComputeOutputHash = false }),
            ("exception hardening", baseline with { HardenExceptionHandlers = true }),
            ("uncontrolled-task detection", baseline with { DetectUncontrolledTasks = true }),
        ];

        foreach ((string name, RewriteOptions variant) in variants)
        {
            string fixturePath = context.CompileFixture("Fx.Option." + name.Replace(' ', '.'), Fixture);
            string firstOut = fixturePath + ".rewritten.dll";
            context.Rewrite(fixturePath, firstOut, RewriteTestContext.StandardRuleSet(), baseline).EnsureSuccess();

            RewriteResult second = context.Rewrite(
                firstOut,
                fixturePath + ".twice.dll",
                RewriteTestContext.StandardRuleSet(),
                variant);

            Assert.False(second.Succeeded);
            RewriteDiagnostic diagnostic = Assert.Single(
                second.Errors,
                error => error.Id == RewriteDiagnosticIds.IncompatibleRewriteVersion);
            Assert.Contains("rewrite options", diagnostic.Message);
        }
    }

    [Fact]
    public void ExclusionOrderingDoesNotChangeOptionsFingerprint()
    {
        var first = new RewriteOptions { TargetRuntime = RewriteTestContext.TargetRuntime, ExcludedTypeFullNames = ["Fx.B", "Fx.A", "Fx.B"] };
        var second = new RewriteOptions { TargetRuntime = RewriteTestContext.TargetRuntime, ExcludedTypeFullNames = ["Fx.A", "Fx.B"] };

        Assert.Equal(first.ComputeSemanticFingerprint(), second.ComputeSemanticFingerprint());
    }

    [Fact]
    public void DescriptionChangesRuleSetSignature()
    {
        RewriteRule firstRule = RewriteTestContext.StandardRuleSet().Rules[0] with { Description = "first reason" };
        RewriteRule secondRule = firstRule with { Description = "second reason" };

        Assert.NotEqual(
            new RewriteRuleSet("r", "1", [firstRule]).ComputeSignature(),
            new RewriteRuleSet("r", "1", [secondRule]).ComputeSignature());
    }
}
