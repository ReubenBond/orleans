using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Golden tests for the core call-site operations: static and instance call redirection, constructor
/// (newobj) redirection, generic-instance method redirection, post-call wrapping, and rejection
/// injection. Assertions are on rewritten IL structure and the emitted manifest, not raw bytes.
/// </summary>
public sealed class CallSiteRewriteGoldenTests
{
    private const string BasicFixture = """
        using ClockworkFixtures.Api;

        namespace Fx
        {
            public static class Basic
            {
                public static long Ticks() => RealClock.UtcNowTicks();
                public static int Instance() { var s = new Service(3); return s.GetValue(); }
                public static int Make() { var w = new Widget(5); return w.X; }
                public static int Gen() => GenericOps.Echo<int>(11);
                public static int Metered() { var m = new Meterable(); return m.Measure(); }
                public static void Danger() => Forbidden.DangerousWrite("hi");
            }
        }
        """;

    [Fact]
    public void StaticCallIsRedirectedToShim()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.StaticCall", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(OutputOf(fixturePath, context));
        MethodDefinition ticks = CecilInspect.GetMethod(module, "Fx.Basic", "Ticks");

        Assert.True(CecilInspect.CallsAnyContaining(ticks, "ClockShim::UtcNowTicks"));
        Assert.False(CecilInspect.CallsAnyContaining(ticks, "RealClock::UtcNowTicks"));
    }

    [Fact]
    public void InstanceCallIsRedirectedToStaticShimTakingReceiver()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.InstanceCall", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(OutputOf(fixturePath, context));
        MethodDefinition instance = CecilInspect.GetMethod(module, "Fx.Basic", "Instance");

        Assert.True(CecilInspect.CallsAnyContaining(instance, "ClockShim::GetValue"));
        Assert.False(CecilInspect.CallsAnyContaining(instance, "Service::GetValue"));
        // The Service constructor is not a rule target and must remain.
        Assert.True(CecilInspect.CallsAnyContaining(instance, "Service::.ctor"));
    }

    [Fact]
    public void NewObjIsRedirectedToStaticFactory()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.NewObj", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(OutputOf(fixturePath, context));
        MethodDefinition make = CecilInspect.GetMethod(module, "Fx.Basic", "Make");

        Assert.True(CecilInspect.CallsAnyContaining(make, "ClockShim::CreateWidget"));
        Assert.False(CecilInspect.CallsAnyContaining(make, "Widget::.ctor"));
    }

    [Fact]
    public void GenericInstanceMethodCarriesTypeArgumentsToReplacement()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Generic", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(OutputOf(fixturePath, context));
        MethodDefinition gen = CecilInspect.GetMethod(module, "Fx.Basic", "Gen");

        Assert.True(CecilInspect.CallsAnyContaining(gen, "ClockShim::Echo"));
        Assert.False(CecilInspect.CallsAnyContaining(gen, "GenericOps::Echo"));
        // The type argument (System.Int32) is preserved on the redirected generic call.
        Assert.Contains(CecilInspect.CallTargets(gen), t => t.Contains("Echo", StringComparison.Ordinal) && t.Contains("Int32", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericPostCallWrapperInflatesTheConcreteReturnType()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.GenericWrap", BasicFixture);
        var ruleSet = new RewriteRuleSet(
            "clockwork.generic-wrapper",
            "1.0",
            [
                RewriteRule.WrapAfterCall(
                    "wrap-generic",
                    new MemberSignature("ClockworkFixtures.Api.GenericOps", "Echo"),
                    RewriteReplacement.Method(
                        FixtureSources.ShimAssemblyName,
                        "ClockworkFixtures.Shims.ClockShim",
                        "WrapGeneric")),
            ]);

        context.Rewrite(fixturePath, ruleSet).EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.GenericWrap.rewritten.dll"));
        MethodDefinition generic = CecilInspect.GetMethod(module, "Fx.Basic", "Gen");
        Assert.Contains(
            CecilInspect.CallTargets(generic),
            target => target.Contains("WrapGeneric<System.Int32>", StringComparison.Ordinal));
    }

    [Fact]
    public void PostCallWrapperIsInsertedAfterOriginalCall()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Wrap", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(OutputOf(fixturePath, context));
        MethodDefinition metered = CecilInspect.GetMethod(module, "Fx.Basic", "Metered");

        List<string> targets = CecilInspect.CallTargets(metered);
        int measureIndex = targets.FindIndex(t => t.Contains("Meterable::Measure", StringComparison.Ordinal));
        int wrapIndex = targets.FindIndex(t => t.Contains("ClockShim::WrapMeasure", StringComparison.Ordinal));

        Assert.True(measureIndex >= 0, "original Measure call must be preserved");
        Assert.True(wrapIndex > measureIndex, "wrapper must be inserted after the original call");
    }

    [Fact]
    public void RejectionIsInjectedBeforeOriginalInvocation()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Reject", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        using ModuleDefinition module = context.LoadModule(OutputOf(fixturePath, context));
        MethodDefinition danger = CecilInspect.GetMethod(module, "Fx.Basic", "Danger");

        List<string> targets = CecilInspect.CallTargets(danger);
        int rejectIndex = targets.FindIndex(t => t.Contains("ClockShim::Reject", StringComparison.Ordinal));
        int dangerIndex = targets.FindIndex(t => t.Contains("Forbidden::DangerousWrite", StringComparison.Ordinal));

        Assert.True(rejectIndex >= 0, "a rejection call must be injected");
        Assert.True(dangerIndex > rejectIndex, "the original invocation must remain after the rejection");
        Assert.Contains("ClockworkFixtures.Api.Forbidden.DangerousWrite", CecilInspect.StringLiterals(danger));
    }

    [Fact]
    public void ManifestRecordsEverySiteWithOutcomeAndPolicy()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Manifest", BasicFixture);

        var result = context.Rewrite(fixturePath, RewriteTestContext.StandardRuleSet());
        result.EnsureSuccess();

        InstrumentationManifest manifest = result.Manifest;
        Assert.Equal("clockwork.fixtures", manifest.RuleSetId);
        Assert.False(manifest.WasNoOp);
        Assert.NotNull(manifest.Output);

        Assert.Equal(6, manifest.Transformations.Length);
        Assert.Contains(manifest.Transformations, t => t.RuleId == "redirect-utcnowticks" && t.Outcome == TransformationOutcome.Transformed);
        Assert.Contains(manifest.Transformations, t => t.RuleId == "redirect-widget-ctor" && t.Outcome == TransformationOutcome.Transformed);
        Assert.Contains(manifest.Transformations, t => t.RuleId == "reject-dangerouswrite" && t.Outcome == TransformationOutcome.Rejected);
        Assert.All(manifest.Transformations, t => Assert.False(string.IsNullOrEmpty(t.Method)));
        Assert.Contains("\"schemaVersion\": 3", manifest.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestSerializationIsDeterministic()
    {
        using var context = RewriteTestContext.Create();
        string fixturePath = context.CompileFixture("Fx.Det", BasicFixture);

        // Rewriting the same input with the same rules must produce a stable transformation list in a
        // stable order. (Input/output content hashes are validated separately and are not compared
        // here, since the intent is deterministic ordering and structure.)
        string firstOut = Path.Combine(context.Directory, "det-1.dll");
        string secondOut = Path.Combine(context.Directory, "det-2.dll");
        var firstResult = context.Rewrite(fixturePath, firstOut, RewriteTestContext.StandardRuleSet());
        var secondResult = context.Rewrite(fixturePath, secondOut, RewriteTestContext.StandardRuleSet());

        Assert.Equal(
            firstResult.Manifest.Transformations,
            secondResult.Manifest.Transformations);

        Assert.Equal(
            Render(firstResult.Manifest.Transformations),
            Render(secondResult.Manifest.Transformations));

        static string Render(System.Collections.Immutable.ImmutableArray<ManifestTransformation> transformations) =>
            string.Join(
                "\n",
                transformations
                    .OrderBy(t => t.Method, StringComparer.Ordinal)
                    .ThenBy(t => t.ILOffset)
                    .ThenBy(t => t.RuleId, StringComparer.Ordinal)
                    .Select(t => $"{t.Method}|{t.ILOffset}|{t.RuleId}|{t.Operation}|{t.Outcome}|{t.Policy}|{t.Target}|{t.Replacement}"));
    }

    private static string OutputOf(string inputPath, RewriteTestContext context) =>
        Path.Combine(context.Directory, Path.GetFileNameWithoutExtension(inputPath) + ".rewritten.dll");
}
