using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="Task.Run(System.Action)"/> family:
/// once a fixture is rewritten with the controlled-task rule set, <c>Task.Run</c> queues its body as a
/// fresh controlled operation on the simulation coordinator rather than an uncontrolled physical
/// thread-pool thread, so the work runs deterministically on the single logical thread with correct
/// result, unwrap, fault, and cancellation semantics. Rewritten calls require an active simulation.
/// </summary>
public sealed class TaskRunConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class RunProbe {
            // Task.Run(Func<TResult>) computes on the logical thread and returns its value.
            public static Task<int> RunValue() => Impl();
            private static async Task<int> Impl() => await Task.Run(() => 42);
            public static Task RunActionOnly(int[] sink) => Task.Run(() => sink[0] = 42);

            // Task.Run(Func<Task<TResult>>) unwraps the inner async task.
            public static Task<int> RunUnwrap() => UnwrapImpl();
            private static async Task<int> UnwrapImpl() => await Task.Run(async () => { await Task.Yield(); return 21 + 21; });

            // Fire-and-forget Task.Run advances under the cluster drive and mutates shared state.
            public static Task<int> FireAndForget(int[] sink) => FfImpl(sink);
            private static async Task<int> FfImpl(int[] sink)
            {
                var t = Task.Run(() => { sink[0] = 99; });
                await t;
                return sink[0];
            }

            // A faulting body surfaces the original exception through the awaited task.
            public static Task Faulting() => FaultImpl();
            private static async Task FaultImpl() => await Task.Run(() => throw new InvalidTimeZoneException("boom"));

            // A pre-canceled token cancels the task and never runs the body.
            public static Task<bool> Canceled()
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                bool ran = false;
                var t = Task.Run(() => { ran = true; }, cts.Token);
                return WaitCanceled(t, () => ran);
            }
            private static async Task<bool> WaitCanceled(Task t, Func<bool> ran)
            {
                try { await t; } catch (OperationCanceledException) { }
                return t.IsCanceled && !ran();
            }

            public static async Task<long[]> IdentityAndTrace(Func<long> strand)
            {
                long[] values = new long[9];
                values[0] = Environment.CurrentManagedThreadId;
                values[1] = strand();
                var trace = new System.Collections.Generic.List<int>();
                Task[] tasks = new Task[3];
                for (int i = 0; i < tasks.Length; i++)
                {
                    int index = i;
                    tasks[i] = Task.Run(() =>
                    {
                        values[2 + index * 2] = Environment.CurrentManagedThreadId;
                        values[3 + index * 2] = strand();
                        trace.Add(index + 1);
                    });
                }

                await Task.WhenAll(tasks);
                values[8] = trace[0] * 100 + trace[1] * 10 + trace[2];
                return values;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public TaskRunConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TaskRunRel", "Conf.RunProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TaskRunDbg", "Conf.RunProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void RunUsesFreshControlledStrandsWithRepeatableTrace(bool optimize)
    {
        long[] Run()
        {
            using var host = new SimulationHost(Start);
            return Result<long[]>((Task<long[]>)host.Invoke(
                Method("IdentityAndTrace", optimize),
                (Func<long>)(() => Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.CurrentId))!);
        }

        long[] first = Run();
        long[] second = Run();
        Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, first[1]);
        Assert.Equal(123, first[8]);
        Assert.Equal(first[8], second[8]);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(first[0], first[2 + i * 2]);
            Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, first[3 + i * 2]);
            Assert.NotEqual(first[1], first[3 + i * 2]);
        }
    }

    [Fact]
    public void RunComputesResultOnTheLogicalThread()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("RunValue"))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Fact]
    public void RunOfAsyncDelegateUnwrapsInnerTask()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("RunUnwrap"))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Fact]
    public void FireAndForgetRunAdvancesUnderTheClusterDrive()
    {
        using var host = new SimulationHost(Start);
        var sink = new int[1];
        var task = (Task<int>)host.Invoke(Method("FireAndForget"), sink)!;
        Assert.Equal(99, Result<int>(task));
        Assert.Equal(99, sink[0]);
    }

    [Fact]
    public void RunPropagatesTheBodyFault()
    {
        using var host = new SimulationHost(Start);
        var task = (Task)host.Invoke(Method("Faulting"))!;
        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.IsType<InvalidTimeZoneException>(task.Exception!.InnerException);
    }

    [Fact]
    public void RunWithCanceledTokenCancelsWithoutRunningBody()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("Canceled"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenRunRequiresActiveSimulationWithoutRunningItsAction()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedTaskRun",
            "Conf.RunProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("RunValue").Invoke(null, null)!;
        Assert.Equal(42, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("RunActionOnly"), sink);
        Assert.Equal(0, sink[0]);
    }

    private MethodInfo Method(string name) => _release.Value.Method(name);

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    private static T Result<T>(object task)
    {
        var typed = (Task)task;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void TaskRunFlowsCapturedExecutionContext()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(ExecutionContextMethod("RunFlowsContext"))!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(5, Result<int>(task));
    }

    private const string ExecutionContextSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class RunExecutionContextProbe {
            public static Task<int> RunFlowsContext()
            {
                var ambient = new AsyncLocal<int> { Value = 5 };
                var task = Task.Run(() => ambient.Value);
                ambient.Value = 9;
                return task;
            }
        } }
        """;

    private StagedProbe? _executionContextProbe;

    private MethodInfo ExecutionContextMethod(string name) =>
        (_executionContextProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskRunExecutionContext",
            "Conf.RunExecutionContextProbe",
            ExecutionContextSource,
            optimize: true)).Method(name);

    [Fact]
    public void RunOfCanceledInnerTaskPreservesToken()
    {
        using var innerCancellation = new CancellationTokenSource();
        innerCancellation.Cancel();
        using var host = new SimulationHost(Start);

        var task = (Task)host.Invoke(EdgeCaseMethod("RunCanceledInner"), innerCancellation.Token)!;

        AssertCanceledTaskCarriesToken(task, innerCancellation.Token);
    }

    [Fact]
    public void RunOfCanceledGenericInnerTaskPreservesToken()
    {
        using var innerCancellation = new CancellationTokenSource();
        innerCancellation.Cancel();
        using var host = new SimulationHost(Start);

        var task = (Task<int>)host.Invoke(
            EdgeCaseMethod("RunCanceledGenericInner"),
            innerCancellation.Token)!;

        AssertCanceledTaskCarriesToken(task, innerCancellation.Token);
    }

    private const string EdgeCaseSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class RunCanceledInnerProbe {
            public static Task RunCanceledInner(CancellationToken token) =>
                Task.Run(() => Task.FromCanceled(token));

            public static Task<int> RunCanceledGenericInner(CancellationToken token) =>
                Task.Run(() => Task.FromCanceled<int>(token));
        } }
        """;

    private StagedProbe? _edgeCaseProbe;

    private MethodInfo EdgeCaseMethod(string name) =>
        (_edgeCaseProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskRunCanceledInner",
            "Conf.RunCanceledInnerProbe",
            EdgeCaseSource,
            optimize: true)).Method(name);

    private static void AssertCanceledTaskCarriesToken(Task task, CancellationToken expectedToken)
    {
        Assert.Equal(TaskStatus.Canceled, task.Status);
        Assert.True(task.IsCanceled);
        Assert.Null(task.Exception);
        var error = Assert.Throws<TaskCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.Equal(expectedToken, error.CancellationToken);
    }
}
