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
/// End-to-end golden tests for the shipped controlled <see cref="System.Threading.ThreadPool"/> rule
/// family: they rewrite real <c>ThreadPool.QueueUserWorkItem</c> / <c>UnsafeQueueUserWorkItem</c> call
/// sites in a compiled fixture against the real <c>Clockwork</c> assembly and assert on the
/// rewritten IL and manifest. This proves the static signatures declared in <c>BuiltInRuleSets</c> line
/// up with the actual <see cref="Orleans.Testing.Simulation.Shims.System.Threading.ControlledThreadPool"/> members, and that
/// the native-overlapped overload is rejected at the call site, while the registered-wait factories are
/// redirected to controlled shims and the <c>RegisteredWaitHandle</c> type is substituted.
/// </summary>
public sealed class ControlledThreadPoolRuleGoldenTests
{
    private const string Fixture = """
        using System.Threading;

        namespace Fx
        {
            public static class PoolUser
            {
                public static bool Queue() => ThreadPool.QueueUserWorkItem(_ => { });
                public static bool QueueState(object s) => ThreadPool.QueueUserWorkItem(_ => { }, s);
                public static bool QueueGeneric(int s) => ThreadPool.QueueUserWorkItem(x => { }, s, false);
                public static bool UnsafeQueueState(object s) => ThreadPool.UnsafeQueueUserWorkItem(_ => { }, s);
                public static bool UnsafeQueueItem(IThreadPoolWorkItem w) => ThreadPool.UnsafeQueueUserWorkItem(w, false);
                public static bool UnsafeQueueGeneric(int s) => ThreadPool.UnsafeQueueUserWorkItem(x => { }, s, false);
                public static unsafe bool Native(NativeOverlapped* p) => ThreadPool.UnsafeQueueNativeOverlapped(p);
                public static RegisteredWaitHandle RegisterWait(WaitHandle h) =>
                    ThreadPool.RegisterWaitForSingleObject(h, (_, _) => { }, null, 1000, true);
                public static RegisteredWaitHandle RegisterWaitTimeSpan(WaitHandle h) =>
                    ThreadPool.RegisterWaitForSingleObject(h, (_, _) => { }, null, System.TimeSpan.Zero, true);
                public static RegisteredWaitHandle UnsafeRegisterWait(WaitHandle h) =>
                    ThreadPool.UnsafeRegisterWaitForSingleObject(h, (_, _) => { }, null, 1000, true);
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.ControlledThreadPool).Assembly.Location;

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
    public void SafeQueueOverloadsAreRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.PoolSafe");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.PoolSafe.rewritten.dll"));

        foreach (var name in new[] { "Queue", "QueueState", "QueueGeneric" })
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.PoolUser", name);
            Assert.True(CecilInspect.CallsAnyContaining(method, "ControlledThreadPool::QueueUserWorkItem"));
            Assert.False(CecilInspect.CallsAnyContaining(method, "Threading.ThreadPool::QueueUserWorkItem"));
        }

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.threadpool.queue.generic" && t.Policy == SimulationApiPolicy.Controlled);
    }

    [Fact]
    public void UnsafeQueueOverloadsAreRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.PoolUnsafe");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.PoolUnsafe.rewritten.dll"));

        foreach (var name in new[] { "UnsafeQueueState", "UnsafeQueueItem", "UnsafeQueueGeneric" })
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.PoolUser", name);
            Assert.True(CecilInspect.CallsAnyContaining(method, "ControlledThreadPool::UnsafeQueueUserWorkItem"));
            Assert.False(CecilInspect.CallsAnyContaining(method, "Threading.ThreadPool::UnsafeQueueUserWorkItem"));
        }
    }

    [Fact]
    public void NativeOverlappedIsRejectedAtTheCallSite()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.PoolNative");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.PoolNative.rewritten.dll"));

        MethodDefinition native = CecilInspect.GetMethod(module, "Fx.PoolUser", "Native");
        Assert.True(CecilInspect.CallsAnyContaining(native, "ControlledThreadPool::RejectNativeOverlapped"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.threadpool.unsafequeuenativeoverlapped" && t.Policy == SimulationApiPolicy.Rejected);
    }

    [Fact]
    public void RegisteredWaitOverloadsAreRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.PoolRegisterWait");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.PoolRegisterWait.rewritten.dll"));

        foreach (var name in new[] { "RegisterWait", "RegisterWaitTimeSpan" })
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.PoolUser", name);
            Assert.True(CecilInspect.CallsAnyContaining(method, "ControlledThreadPool::RegisterWaitForSingleObject"));
            Assert.False(CecilInspect.CallsAnyContaining(method, "Threading.ThreadPool::RegisterWaitForSingleObject"));
        }

        MethodDefinition unsafeMethod = CecilInspect.GetMethod(module, "Fx.PoolUser", "UnsafeRegisterWait");
        Assert.True(CecilInspect.CallsAnyContaining(
            unsafeMethod, "ControlledThreadPool::UnsafeRegisterWaitForSingleObject"));

        // The RegisteredWaitHandle return type is whole-type substituted onto the controlled handle.
        Assert.True(CecilInspect.CallsAnyContaining(
            CecilInspect.GetMethod(module, "Fx.PoolUser", "RegisterWait"), "ControlledRegisteredWaitHandle"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.threadpool.registerwait.int32" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.threadpool.registerwait.timespan" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.threadpool.unsaferegisterwait.int32" && t.Policy == SimulationApiPolicy.Controlled);
    }
}
