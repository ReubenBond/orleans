using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// End-to-end golden tests for the shipped controlled <see cref="System.Threading.Thread"/> rule family:
/// they rewrite real <c>Thread</c> call sites in a compiled fixture against the real
/// <c>Clockwork</c> assembly and assert on the rewritten IL and manifest. This proves the
/// static/instance signatures declared in <c>BuiltInRuleSets</c> line up with the actual
/// <see cref="Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread"/> members - constructors become
/// <c>Create</c>, instance <c>Start</c>/<c>Join</c> become static shims taking the receiver, the static
/// <c>Sleep</c>/<c>Yield</c> stay static, and the rejected OS-specific surface (priority) is redirected
/// to a rejecting shim.
/// </summary>
public sealed class ControlledThreadRuleGoldenTests
{
    private const string Fixture = """
        using System.Threading;

        namespace Fx
        {
            public static class ThreadUser
            {
                public static Thread Make() => new Thread(() => { });
                public static Thread MakeParameterized() => new Thread(o => { });
                public static void StartIt(Thread t) => t.Start();
                public static void StartWith(Thread t, object o) => t.Start(o);
                public static void JoinIt(Thread t) => t.Join();
                public static bool JoinTimed(Thread t) => t.Join(5);
                public static void Nap() => Thread.Sleep(5);
                public static bool Cede() => Thread.Yield();
                public static void Prioritise(Thread t) => t.Priority = ThreadPriority.Highest;
                public static void Interrupt(Thread t) => t.Interrupt();
                public static void Apartment(Thread t) => t.ApartmentState = ApartmentState.STA;
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread).Assembly.Location;

    private static RewriteResult RewriteFixture(RewriteTestContext context, string assemblyName)
    {
        string fixturePath = context.CompileFixture(assemblyName, Fixture);
        string outputPath = Path.Combine(
            context.Directory, Path.GetFileNameWithoutExtension(fixturePath) + ".rewritten.dll");

        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        RewriteRuleSet ruleSet = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);
        var result = RewriteEngine.Rewrite(new RewriteRequest(fixturePath, outputPath, ruleSet, options));
        result.EnsureSuccess();
        return result;
    }

    [Fact]
    public void ConstructorsAreRedirectedToControlledCreate()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.ThreadCtor");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ThreadCtor.rewritten.dll"));

        MethodDefinition make = CecilInspect.GetMethod(module, "Fx.ThreadUser", "Make");
        Assert.True(CecilInspect.CallsAnyContaining(make, "ControlledThread::Create"));
        Assert.False(CecilInspect.CallsAnyContaining(make, "Threading.Thread::.ctor"));

        MethodDefinition parameterized = CecilInspect.GetMethod(module, "Fx.ThreadUser", "MakeParameterized");
        Assert.True(CecilInspect.CallsAnyContaining(parameterized, "ControlledThread::Create"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.thread.ctor.threadstart" && t.Policy == SimulationApiPolicy.Controlled);
    }

    [Fact]
    public void InstanceStartAndJoinAreRedirectedToStaticShimsTakingReceiver()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.ThreadStartJoin");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ThreadStartJoin.rewritten.dll"));

        MethodDefinition startIt = CecilInspect.GetMethod(module, "Fx.ThreadUser", "StartIt");
        Assert.True(CecilInspect.CallsAnyContaining(startIt, "ControlledThread::Start"));
        Assert.False(CecilInspect.CallsAnyContaining(startIt, "Threading.Thread::Start"));

        MethodDefinition startWith = CecilInspect.GetMethod(module, "Fx.ThreadUser", "StartWith");
        Assert.True(CecilInspect.CallsAnyContaining(startWith, "ControlledThread::Start"));

        MethodDefinition joinIt = CecilInspect.GetMethod(module, "Fx.ThreadUser", "JoinIt");
        Assert.True(CecilInspect.CallsAnyContaining(joinIt, "ControlledThread::Join"));
        Assert.False(CecilInspect.CallsAnyContaining(joinIt, "Threading.Thread::Join"));

        MethodDefinition joinTimed = CecilInspect.GetMethod(module, "Fx.ThreadUser", "JoinTimed");
        Assert.True(CecilInspect.CallsAnyContaining(joinTimed, "ControlledThread::Join"));
    }

    [Fact]
    public void StaticHintsAreRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.ThreadStatics");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ThreadStatics.rewritten.dll"));

        MethodDefinition nap = CecilInspect.GetMethod(module, "Fx.ThreadUser", "Nap");
        Assert.True(CecilInspect.CallsAnyContaining(nap, "ControlledThread::Sleep"));
        Assert.False(CecilInspect.CallsAnyContaining(nap, "Threading.Thread::Sleep"));

        MethodDefinition cede = CecilInspect.GetMethod(module, "Fx.ThreadUser", "Cede");
        Assert.True(CecilInspect.CallsAnyContaining(cede, "ControlledThread::Yield"));
        Assert.False(CecilInspect.CallsAnyContaining(cede, "Threading.Thread::Yield"));
    }

    [Fact]
    public void OsSpecificSurfaceIsRedirectedToRejectingShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.ThreadRejected");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ThreadRejected.rewritten.dll"));

        MethodDefinition prioritise = CecilInspect.GetMethod(module, "Fx.ThreadUser", "Prioritise");
        Assert.True(CecilInspect.CallsAnyContaining(prioritise, "ControlledThread::SetPriority"));
        Assert.False(CecilInspect.CallsAnyContaining(prioritise, "Threading.Thread::set_Priority"));

        MethodDefinition interrupt = CecilInspect.GetMethod(module, "Fx.ThreadUser", "Interrupt");
        Assert.True(CecilInspect.CallsAnyContaining(interrupt, "ControlledThread::Interrupt"));

        MethodDefinition apartment = CecilInspect.GetMethod(module, "Fx.ThreadUser", "Apartment");
        Assert.True(CecilInspect.CallsAnyContaining(apartment, "ControlledThread::SetApartmentStateProperty"));
        Assert.False(CecilInspect.CallsAnyContaining(apartment, "Threading.Thread::set_ApartmentState"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.thread.set_priority" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.thread.interrupt" && t.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.thread.set_apartmentstate" && t.Policy == SimulationApiPolicy.Rejected);
    }
}
