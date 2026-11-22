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
/// End-to-end golden tests for the shipped controlled-task rule set
/// (<see cref="BuiltInRuleSets.ControlledTasksId"/>): they rewrite real <see cref="System.Threading.Tasks.Task"/>
/// call sites in a compiled fixture against the real <c>Clockwork</c> assembly and assert
/// on the rewritten IL and manifest. This proves the static/instance signatures in
/// <c>BuiltInRuleSets.BuildControlledTasks</c> line up with the actual <c>ControlledTask</c> members, so
/// the inventory is not merely declarative.
/// </summary>
public sealed class ControlledTaskRuleGoldenTests
{
    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Fx
        {
            public sealed class Outer<T>
            {
                public sealed class Inner { }
            }

            public static class RecursiveTaskUser
            {
                public static Outer<T>.Inner Read<T>(Task<Outer<T>.Inner> task) => task.Result;
            }

            public static class TaskUser
            {
                public static Task All(Task a, Task b) => Task.WhenAll(a, b);
                public static Task AllList(IEnumerable<Task> tasks) => Task.WhenAll(tasks);
                public static Task<Task> Any(Task a, Task b) => Task.WhenAny(a, b);
                public static void Block(Task t) => t.Wait();
                public static Task DelayMilliseconds() => Task.Delay(5);
                public static Task DelayTimeSpan() => Task.Delay(System.TimeSpan.FromMilliseconds(5));
                public static Task DelayMillisecondsCancellation() => Task.Delay(5, default);
                public static Task DelayTimeSpanCancellation() => Task.Delay(System.TimeSpan.FromMilliseconds(5), default(System.Threading.CancellationToken));
                public static Task DelayTimeProvider() => Task.Delay(System.TimeSpan.FromMilliseconds(5), System.TimeProvider.System);
                public static Task DelayTimeProviderCancellation() => Task.Delay(System.TimeSpan.FromMilliseconds(5), System.TimeProvider.System, default);
                public static Task Offloaded() => Task.Run(() => { });
                public static Task Started() => Task.Factory.StartNew(() => { });
                public static Task<int> StartedValue() => Task.Factory.StartNew(() => 7);
                public static Task Continued(Task<int> t) => t.ContinueWith(x => { });
                public static Task<string> Projected(Task<int> t) => t.ContinueWith(x => x.Result.ToString());

                public static Task<int[]> AllT(Task<int> a, Task<int> b) => Task.WhenAll(a, b);
                public static Task<int[]> AllListT(IEnumerable<Task<int>> tasks) => Task.WhenAll(tasks);
                public static Task<Task<int>> AnyT(Task<int> a, Task<int> b) => Task.WhenAny(a, b);
                public static int BlockResult(Task<int> t) => t.Result;

                public static async ValueTask VtAsync(ValueTask inner) => await inner.ConfigureAwait(false);
                public static async ValueTask<int> VtAsyncT(ValueTask<int> inner) => await inner.ConfigureAwait(false);

                public static async Task ConfiguredBool(Task inner) => await inner.ConfigureAwait(false);
                public static async Task<int> ConfiguredBoolT(Task<int> inner) => await inner.ConfigureAwait(false);
                public static async Task ConfiguredOptions(Task inner) => await inner.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                public static async Task<int> ConfiguredOptionsT(Task<int> inner) => await inner.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

                public static Task Factory() => Task.Factory.StartNew(() => { });
                public static Task FactoryToken() => Task.Factory.StartNew(() => { }, CancellationToken.None);
                public static Task FactoryOptions() => Task.Factory.StartNew(() => { }, TaskCreationOptions.DenyChildAttach);
                public static Task FactoryScheduler() => Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                public static Task FactoryState() => Task.Factory.StartNew(_ => { }, 1);
                public static Task FactoryStateToken() => Task.Factory.StartNew(_ => { }, 1, CancellationToken.None);
                public static Task FactoryStateOptions() => Task.Factory.StartNew(_ => { }, 1, TaskCreationOptions.DenyChildAttach);
                public static Task FactoryStateScheduler() => Task.Factory.StartNew(_ => { }, 1, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                public static Task<int> FactoryFunc() => Task.Factory.StartNew(() => 5);
                public static Task<int> FactoryFuncToken() => Task.Factory.StartNew(() => 5, CancellationToken.None);
                public static Task<int> FactoryFuncOptions() => Task.Factory.StartNew(() => 5, TaskCreationOptions.DenyChildAttach);
                public static Task<int> FactoryFuncScheduler() => Task.Factory.StartNew(() => 5, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                public static Task<int> FactoryFuncState() => Task.Factory.StartNew(s => (int)s!, 5);
                public static Task<int> FactoryFuncStateToken() => Task.Factory.StartNew(s => (int)s!, 5, CancellationToken.None);
                public static Task<int> FactoryFuncStateOptions() => Task.Factory.StartNew(s => (int)s!, 5, TaskCreationOptions.DenyChildAttach);
                public static Task<int> FactoryFuncStateScheduler() => Task.Factory.StartNew(s => (int)s!, 5, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                public static Task<int> FactoryGeneric() => new TaskFactory<int>().StartNew(() => 7);
                public static Task<int> FactoryGenericToken() => new TaskFactory<int>().StartNew(() => 7, CancellationToken.None);
                public static Task<int> FactoryGenericOptions() => new TaskFactory<int>().StartNew(() => 7, TaskCreationOptions.DenyChildAttach);
                public static Task<int> FactoryGenericScheduler() => new TaskFactory<int>().StartNew(() => 7, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                public static Task<int> FactoryGenericState() => new TaskFactory<int>().StartNew(s => (int)s!, 7);
                public static Task<int> FactoryGenericStateToken() => new TaskFactory<int>().StartNew(s => (int)s!, 7, CancellationToken.None);
                public static Task<int> FactoryGenericStateOptions() => new TaskFactory<int>().StartNew(s => (int)s!, 7, TaskCreationOptions.DenyChildAttach);
                public static Task<int> FactoryGenericStateScheduler() => new TaskFactory<int>().StartNew(s => (int)s!, 7, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);

                public static Task Unwrapped(Task<Task> t) => t.Unwrap();
                public static Task<int> UnwrappedT(Task<Task<int>> t) => t.Unwrap();
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTask).Assembly.Location;

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
    public void StaticCombinatorsAreRedirectedToControlledTask()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.TaskCombinators");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskCombinators.rewritten.dll"));

        MethodDefinition all = CecilInspect.GetMethod(module, "Fx.TaskUser", "All");
        Assert.True(CecilInspect.CallsAnyContaining(all, "ControlledTask::WhenAll"));
        Assert.False(CecilInspect.CallsAnyContaining(all, "Threading.Tasks.Task::WhenAll"));

        MethodDefinition allList = CecilInspect.GetMethod(module, "Fx.TaskUser", "AllList");
        Assert.True(CecilInspect.CallsAnyContaining(allList, "ControlledTask::WhenAll"));

        MethodDefinition any = CecilInspect.GetMethod(module, "Fx.TaskUser", "Any");
        Assert.True(CecilInspect.CallsAnyContaining(any, "ControlledTask::WhenAny"));
        Assert.False(CecilInspect.CallsAnyContaining(any, "Threading.Tasks.Task::WhenAny"));
    }

    [Fact]
    public void InstanceWaitIsRedirectedToStaticShimTakingReceiver()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.TaskWait");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskWait.rewritten.dll"));

        MethodDefinition block = CecilInspect.GetMethod(module, "Fx.TaskUser", "Block");
        Assert.True(CecilInspect.CallsAnyContaining(block, "ControlledTask::Wait"));
        Assert.False(CecilInspect.CallsAnyContaining(block, "Threading.Tasks.Task::Wait"));
    }

    [Fact]
    public void DelaySurfacesAreRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.TaskTime");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskTime.rewritten.dll"));

        string[] methods =
        [
            "DelayMilliseconds",
            "DelayTimeSpan",
            "DelayMillisecondsCancellation",
            "DelayTimeSpanCancellation",
            "DelayTimeProvider",
            "DelayTimeProviderCancellation",
        ];
        foreach (string methodName in methods)
        {
            MethodDefinition delayed = CecilInspect.GetMethod(module, "Fx.TaskUser", methodName);
            Assert.True(CecilInspect.CallsAnyContaining(delayed, "ControlledTask::Delay"));
            Assert.False(CecilInspect.CallsAnyContaining(delayed, "Threading.Tasks.Task::Delay"));
        }

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        string[] ruleIds =
        [
            "clockwork.tasks.delay.milliseconds",
            "clockwork.tasks.delay.timespan",
            "clockwork.tasks.delay.milliseconds.cancellationtoken",
            "clockwork.tasks.delay.timespan.cancellationtoken",
            "clockwork.tasks.delay.timespan.timeprovider",
            "clockwork.tasks.delay.timespan.timeprovider.cancellationtoken",
        ];
        foreach (string ruleId in ruleIds)
        {
            Assert.Contains(transformations, t =>
                t.RuleId == ruleId && t.Policy == SimulationApiPolicy.Controlled);
        }
    }

    [Fact]
    public void TaskRunIsRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.TaskRun");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskRun.rewritten.dll"));

        MethodDefinition offloaded = CecilInspect.GetMethod(module, "Fx.TaskUser", "Offloaded");
        Assert.True(CecilInspect.CallsAnyContaining(offloaded, "ControlledTask::Run"));
        Assert.False(CecilInspect.CallsAnyContaining(offloaded, "Threading.Tasks.Task::Run"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.run.action" && t.Policy == SimulationApiPolicy.Controlled);
    }

    [Fact]
    public void TaskFactoryStartNewIsRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.TaskFactory");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskFactory.rewritten.dll"));

        MethodDefinition started = CecilInspect.GetMethod(module, "Fx.TaskUser", "Started");
        Assert.True(CecilInspect.CallsAnyContaining(started, "ControlledTaskFactory::StartNew"));
        Assert.False(CecilInspect.CallsAnyContaining(started, "Threading.Tasks.TaskFactory::StartNew"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.factory.startnew.func" && t.Policy == SimulationApiPolicy.Controlled);
    }

    [Fact]
    public void GenericContinueWithIsRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.TaskContinue");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskContinue.rewritten.dll"));

        MethodDefinition projected = CecilInspect.GetMethod(module, "Fx.TaskUser", "Projected");
        Assert.True(CecilInspect.CallsAnyContaining(projected, "ControlledTask::ContinueWith"));
        Assert.False(CecilInspect.CallsAnyContaining(projected, "Threading.Tasks.Task`1::ContinueWith"));

        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.continuewith.generic.func" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.continuewith.generic.action" && t.Policy == SimulationApiPolicy.Controlled);
    }

    [Fact]
    public void GenericCombinatorsAreRedirectedToControlledTask()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.TaskGenericCombinators");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskGenericCombinators.rewritten.dll"));

        MethodDefinition allT = CecilInspect.GetMethod(module, "Fx.TaskUser", "AllT");
        Assert.True(CecilInspect.CallsAnyContaining(allT, "ControlledTask::WhenAll"));
        Assert.False(CecilInspect.CallsAnyContaining(allT, "Threading.Tasks.Task::WhenAll"));

        MethodDefinition allListT = CecilInspect.GetMethod(module, "Fx.TaskUser", "AllListT");
        Assert.True(CecilInspect.CallsAnyContaining(allListT, "ControlledTask::WhenAll"));

        MethodDefinition anyT = CecilInspect.GetMethod(module, "Fx.TaskUser", "AnyT");
        Assert.True(CecilInspect.CallsAnyContaining(anyT, "ControlledTask::WhenAny"));
        Assert.False(CecilInspect.CallsAnyContaining(anyT, "Threading.Tasks.Task::WhenAny"));
    }

    [Fact]
    public void BlockingGenericResultIsRedirectedToControlledTask()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.TaskResult");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskResult.rewritten.dll"));

        MethodDefinition block = CecilInspect.GetMethod(module, "Fx.TaskUser", "BlockResult");
        Assert.True(CecilInspect.CallsAnyContaining(block, "ControlledTask::Result"));
        Assert.False(CecilInspect.CallsAnyContaining(block, "Task`1::get_Result"));
    }

    [Fact]
    public void BlockingResultWithNestedGenericReturnIsRedirectedWithoutRecursing()
    {
        using var context = RewriteTestContext.Create();
        RewriteResult result = RewriteFixture(context, "Fx.TaskRecursiveResult");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskRecursiveResult.rewritten.dll"));

        MethodDefinition read = CecilInspect.GetMethod(module, "Fx.RecursiveTaskUser", "Read");
        Assert.True(CecilInspect.CallsAnyContaining(read, "ControlledTask::Result"));
        Assert.False(CecilInspect.CallsAnyContaining(read, "Task`1::get_Result"));
        Assert.Contains(
            result.Manifest.Transformations,
            transformation => transformation.RuleId == "clockwork.tasks.result.generic"
                && transformation.Method.Contains(
                    "Fx.RecursiveTaskUser.Read",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void ValueTaskAsyncMachineryIsSubstituted()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.ValueTaskMachinery");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ValueTaskMachinery.rewritten.dll"));

        // The builder and configured awaiter live in the compiler-generated state machine, not the
        // user method - assert the whole module references the controlled types and no longer the BCL
        // AsyncValueTaskMethodBuilder create/start entry points.
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledAsyncValueTaskMethodBuilder"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledConfiguredValueTaskAwaitable"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(
            module, "CompilerServices.AsyncValueTaskMethodBuilder"));
    }

    [Fact]
    public void ConfigureAwaitOverloadsConstructTheMatchingControlledAwaitable()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.TaskConfigureAwait");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskConfigureAwait.rewritten.dll"));

        AssertConstructs(module, "ConfiguredBool", "ControlledConfiguredTaskAwaitable", "System.Boolean");
        AssertConstructs(module, "ConfiguredBoolT", "ControlledConfiguredTaskAwaitable`1<System.Int32>", "System.Boolean");
        AssertConstructs(
            module, "ConfiguredOptions", "ControlledConfiguredTaskAwaitable", "System.Threading.Tasks.ConfigureAwaitOptions");
        AssertConstructs(
            module, "ConfiguredOptionsT", "ControlledConfiguredTaskAwaitable`1<System.Int32>", "System.Threading.Tasks.ConfigureAwaitOptions");
    }

    [Fact]
    public void TaskFactoryInstanceStartNewIsRedirectedToControlledShim()
    {
        using var context = RewriteTestContext.Create();
        var result = RewriteFixture(context, "Fx.TaskFactory");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskFactory.rewritten.dll"));

        string[] methods =
        [
            "Factory", "FactoryToken", "FactoryOptions", "FactoryScheduler",
            "FactoryState", "FactoryStateToken", "FactoryStateOptions", "FactoryStateScheduler",
            "FactoryFunc", "FactoryFuncToken", "FactoryFuncOptions", "FactoryFuncScheduler",
            "FactoryFuncState", "FactoryFuncStateToken", "FactoryFuncStateOptions", "FactoryFuncStateScheduler",
            "FactoryGeneric", "FactoryGenericToken", "FactoryGenericOptions", "FactoryGenericScheduler",
            "FactoryGenericState", "FactoryGenericStateToken", "FactoryGenericStateOptions", "FactoryGenericStateScheduler",
        ];
        foreach (string methodName in methods)
        {
            MethodDefinition method = CecilInspect.GetMethod(module, "Fx.TaskUser", methodName);
            Assert.True(CecilInspect.CallsAnyContaining(method, "ControlledTaskFactory::StartNew"), methodName);
            Assert.False(CecilInspect.CallsAnyContaining(method, "Threading.Tasks.TaskFactory::StartNew"), methodName);
        }

        // The controlled runtime handles the TaskFactory scheduling surface (queued as a controlled operation on the
        // coordinator, like Task.Run), superseding the controlled async rejection.
        ImmutableArray<ManifestTransformation> transformations = result.Manifest.Transformations;
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.factory.startnew.action" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.factory.startnew.func" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Contains(transformations, t =>
            t.RuleId == "clockwork.tasks.factory.generic.startnew.func" && t.Policy == SimulationApiPolicy.Controlled);
        Assert.Equal(
            24,
            transformations
                .Select(t => t.RuleId)
                .Distinct(StringComparer.Ordinal)
                .Count(id => id.StartsWith("clockwork.tasks.factory.", StringComparison.Ordinal)));
    }

    [Fact]
    public void UnwrapExtensionIsRedirectedToControlledTask()
    {
        using var context = RewriteTestContext.Create();
        RewriteFixture(context, "Fx.TaskUnwrap");

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.TaskUnwrap.rewritten.dll"));

        MethodDefinition unwrapped = CecilInspect.GetMethod(module, "Fx.TaskUser", "Unwrapped");
        Assert.True(CecilInspect.CallsAnyContaining(unwrapped, "ControlledTask::Unwrap"));
        Assert.False(CecilInspect.CallsAnyContaining(unwrapped, "TaskExtensions::Unwrap"));

        MethodDefinition unwrappedT = CecilInspect.GetMethod(module, "Fx.TaskUser", "UnwrappedT");
        Assert.True(CecilInspect.CallsAnyContaining(unwrappedT, "ControlledTask::Unwrap"));
        Assert.False(CecilInspect.CallsAnyContaining(unwrappedT, "TaskExtensions::Unwrap"));
    }

    private static void AssertConstructs(ModuleDefinition module, string asyncMethod, string awaitable, string argument)
    {
        TypeDefinition stateMachine = module.GetTypes()
            .Single(type => type.Name.StartsWith($"<{asyncMethod}>", StringComparison.Ordinal));
        List<string> targets = CecilInspect.CallTargets(stateMachine.Methods.Single(method => method.Name == "MoveNext"));

        Assert.Contains(targets, target =>
            target.Contains($"{awaitable}::.ctor(", StringComparison.Ordinal)
            && target.EndsWith($",{argument})", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, target => target.Contains("::ConfigureAwait(", StringComparison.Ordinal));
    }
}
