using System.Reflection;
using Orleans.Testing.Simulation.Instrumentation.Diagnostics;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// A method compiled with runtime async suspends and resumes through the runtime's own continuation machinery, which
/// bypasses the simulation coordinator, so rewriting one with a rule set that controls async methods fails with
/// <c>CWR0013</c>. The runtime-async <c>AsyncHelpers.Await</c> intrinsics that take a configured awaitable resume their
/// caller through that machinery, so rewritten code reaches them only through rejecting shims, which throw a
/// <see cref="SimulationApiException"/> naming the overload. <c>AsyncHelpers.HandleAsyncEntryPoint</c>, through which an
/// async <c>Main</c> returns, waits for its task through the controlled awaiter.
/// </summary>
public sealed class RuntimeAsyncHelpersConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const Mono.Cecil.MethodImplAttributes RuntimeAsync =
        (Mono.Cecil.MethodImplAttributes)System.Reflection.MethodImplAttributes.Async;

    private const string RuntimeAsyncSource = """
        using System;
        using System.Threading.Tasks;
        namespace Conf { public static class RuntimeAsyncMethods {
            public static async Task<int> IncrementAsync(Task<int> value) => await value + 1;
            public static Func<Task<int>, Task<int>> Lambda() => async value => await value + 2;
            public static Task<int> LocalFunction(Task<int> value)
            {
                return AddAsync(value);
                static async Task<int> AddAsync(Task<int> value) => await value + 3;
            }
        } }
        """;

    private const string Source = """
        #pragma warning disable SYSLIB5007
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;
        namespace Conf { public static class RuntimeAsyncProbe {
            public static void AwaitTask() => AsyncHelpers.Await(Task.CompletedTask.ConfigureAwait(false));
            public static int AwaitTaskOfT() => AsyncHelpers.Await(Task.FromResult(1).ConfigureAwait(false));
            public static void AwaitValueTask() => AsyncHelpers.Await(default(ValueTask).ConfigureAwait(false));
            public static int AwaitValueTaskOfT() => AsyncHelpers.Await(new ValueTask<int>(1).ConfigureAwait(false));
            public static object HandleEntryPoint(Task<int> task) { AsyncHelpers.HandleAsyncEntryPoint((Task)task); return "returned"; }
            public static object HandleEntryPointOfInt32(Task<int> task) => AsyncHelpers.HandleAsyncEntryPoint(task);
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public RuntimeAsyncHelpersConformanceTests()
    {
        _probe = new(() => _fixture.StageControlledTasks("Conf.RuntimeAsync", "Conf.RuntimeAsyncProbe", Source, optimize: true));
    }

    public static TheoryData<string, string> Overloads => new()
    {
        { "AwaitTask", "System.Runtime.CompilerServices.AsyncHelpers.Await(System.Runtime.CompilerServices.ConfiguredTaskAwaitable)" },
        { "AwaitTaskOfT", "System.Runtime.CompilerServices.AsyncHelpers.Await<T>(System.Runtime.CompilerServices.ConfiguredTaskAwaitable<T>)" },
        { "AwaitValueTask", "System.Runtime.CompilerServices.AsyncHelpers.Await(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable)" },
        { "AwaitValueTaskOfT", "System.Runtime.CompilerServices.AsyncHelpers.Await<T>(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<T>)" },
    };

    public static TheoryData<bool, BuiltInRuleFamily> AsyncMethodFamilies => new()
    {
        { false, BuiltInRuleFamily.AsyncMachinery },
        { true, BuiltInRuleFamily.AsyncMachinery },
        { false, BuiltInRuleFamily.ValueTaskMachinery },
        { true, BuiltInRuleFamily.ValueTaskMachinery },
    };

    [Theory]
    [MemberData(nameof(Overloads))]
    public void ConfiguredAwaitIntrinsicIsRejectedByName(string probe, string apiName)
    {
        using var host = new SimulationHost(Start);

        SimulationApiException exception = Assert.Throws<SimulationApiException>(() => host.Invoke(_probe.Value.Method(probe)));

        Assert.Equal(SimulationApiCategory.Task, exception.Category);
        Assert.Equal(apiName, exception.ApiName);
        Assert.Contains("compile the method without runtime async", exception.Message, StringComparison.Ordinal);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("HandleEntryPoint", "returned")]
    [InlineData("HandleEntryPointOfInt32", 41)]
    public async Task EntryPointWaitPumpsTheSimulationUntilTheTaskCompletes(string probe, object expected)
    {
        MethodInfo method = _probe.Value.Method(probe);

        // A blocking wait would hang this thread forever, so it runs off the test thread and the theory's timeout
        // reports the hang.
        object? result = await Task.Run(
            () =>
            {
                using var host = new SimulationHost(Start);
                var entryTask = new TaskCompletionSource<int>();
                return host.InvokeWithWork(method, [entryTask.Task], () => entryTask.SetResult(41));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(AsyncMethodFamilies))]
    public void RuntimeAsyncMethodFailsTheRewrite(bool optimize, BuiltInRuleFamily family)
    {
        (string sourceDll, RewriteResult result) = _fixture.RewriteRuntimeAsync(
            $"Conf.RuntimeAsync{family}{(optimize ? "Rel" : "Dbg")}", RuntimeAsyncSource, optimize, [family]);

        int runtimeAsyncMethods = RuntimeAsyncMethodCount(sourceDll);
        RewriteDiagnostic[] diagnostics =
            [.. result.Manifest.Diagnostics.Where(diagnostic => diagnostic.Id == RewriteDiagnosticIds.RuntimeAsyncMethod)];

        // The method, the lambda, and the local function.
        Assert.Equal(3, runtimeAsyncMethods);
        Assert.False(result.Succeeded);
        Assert.Equal(runtimeAsyncMethods, diagnostics.Length);
        RewriteDiagnostic method = Assert.Single(diagnostics, diagnostic => diagnostic.Method == "Conf.RuntimeAsyncMethods.IncrementAsync");
        Assert.Contains("remove the 'runtime-async=on' compiler feature", method.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeAsyncMethodRewritesWhenTheRuleSetLeavesAsyncMethodsUncontrolled()
    {
        (_, RewriteResult result) = _fixture.RewriteRuntimeAsync(
            "Conf.RuntimeAsyncTaskTime", RuntimeAsyncSource, optimize: true, [BuiltInRuleFamily.TaskTime]);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic => diagnostic.Id == RewriteDiagnosticIds.RuntimeAsyncMethod);
    }

    private static int RuntimeAsyncMethodCount(string assemblyPath)
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(assemblyPath);
        return module.GetTypes().SelectMany(type => type.Methods).Count(method => (method.ImplAttributes & RuntimeAsync) != 0);
    }

    public void Dispose() => _fixture.Dispose();
}
