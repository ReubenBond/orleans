using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the generic <see cref="Task{TResult}"/> surface that the controlled-task
/// rule set now covers: the generic <c>WhenAll&lt;T&gt;</c>/<c>WhenAny&lt;T&gt;</c> combinator overloads
/// (span, array, and enumerable bindings), the blocking <c>Task&lt;T&gt;.Result</c> accessor draining the
/// controlled loop instead of dead-locking a physical thread, the <c>TaskExtensions.Unwrap</c> extension
/// methods, and the controlled <see cref="TaskFactory"/>
/// scheduling surface. Each probe is compiled from ordinary source, rewritten, and executed inside a live
/// single-logical-thread <see cref="SimulationHost"/>, so any escape to the thread pool would hang.
/// </summary>
public sealed class GenericTaskConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        namespace Conf { public static class GenericTaskProbe {
            // ---- generic WhenAll<T> overloads: result order follows input order, not completion order ----
            public static Task<int[]> WhenAllPair(Task<int> a, Task<int> b) => WhenAllPairImpl(a, b);
            private static async Task<int[]> WhenAllPairImpl(Task<int> a, Task<int> b) => await Task.WhenAll(a, b);

            public static Task<int[]> WhenAllArray(Task<int>[] tasks) => WhenAllArrayImpl(tasks);
            private static async Task<int[]> WhenAllArrayImpl(Task<int>[] tasks) => await Task.WhenAll(tasks);

            public static Task<int[]> WhenAllEnumerable(Task<int>[] tasks) => WhenAllEnumerableImpl(tasks);
            private static async Task<int[]> WhenAllEnumerableImpl(Task<int>[] tasks)
            {
                IEnumerable<Task<int>> source = tasks;
                return await Task.WhenAll(source);
            }

            // ---- generic WhenAny<T> pair: winner is the first completer on the logical thread ----
            public static Task<Task<int>> WhenAnyPair(Task<int> a, Task<int> b) => WhenAnyPairImpl(a, b);
            private static async Task<Task<int>> WhenAnyPairImpl(Task<int> a, Task<int> b) => await Task.WhenAny(a, b);

            // ---- blocking Task<T>.Result on an incomplete controlled producer must drain, not deadlock ----
            public static int BlockingResult()
            {
                Task<int> t = Producer();
                return t.Result;
            }

            public static Task<int> Producer() => ProducerImpl();
            private static async Task<int> ProducerImpl()
            {
                int sum = 0;
                for (int i = 0; i < 3; i++) { await Task.Yield(); sum += 7; }
                return sum;
            }

            // ---- TaskFactory scheduling is controlled under simulation ----
            public static Task<int> FactoryStartNew() => System.Threading.Tasks.Task.Factory.StartNew(() => 5);

            // ---- TaskExtensions.Unwrap: inner+outer both complete on the logical thread ----
            public static Task<int> UnwrapGeneric(Task<Task<int>> outer) => UnwrapGenericImpl(outer);
            private static async Task<int> UnwrapGenericImpl(Task<Task<int>> outer) => await outer.Unwrap();

            public static Task UnwrapNonGeneric(Task<Task> outer) => UnwrapNonGenericImpl(outer);
            private static async Task UnwrapNonGenericImpl(Task<Task> outer) => await outer.Unwrap();
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public GenericTaskConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.GenericTask", "Conf.GenericTaskProbe", Source, optimize: true));

    [Fact]
    public void WhenAllGenericPairPreservesInputOrderRegardlessOfCompletionOrder()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        // b completes before a, but WhenAll<int> must still return results in input order [a, b].
        var task = (Task<int[]>)host.InvokeWithWork(
            Method("WhenAllPair"),
            [a.Task, b.Task],
            () => b.SetResult(20),
            () => a.SetResult(10))!;

        Assert.Equal([10, 20], Result<int[]>(task));
    }

    [Fact]
    public void WhenAllGenericArrayGathersEveryResultInOrder()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();
        var c = new TaskCompletionSource<int>();

        var task = (Task<int[]>)host.InvokeWithWork(
            Method("WhenAllArray"),
            [new[] { a.Task, b.Task, c.Task }],
            () => c.SetResult(3),
            () => a.SetResult(1),
            () => b.SetResult(2))!;

        Assert.Equal([1, 2, 3], Result<int[]>(task));
    }

    [Fact]
    public void WhenAllGenericEnumerableGathersEveryResultInOrder()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        var task = (Task<int[]>)host.InvokeWithWork(
            Method("WhenAllEnumerable"),
            [new[] { a.Task, b.Task }],
            () => b.SetResult(2),
            () => a.SetResult(1))!;

        Assert.Equal([1, 2], Result<int[]>(task));
    }

    [Fact]
    public void WhenAllGenericPropagatesTheFaultedAntecedentsException()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();
        var boom = new InvalidTimeZoneException("boom");

        var task = (Task)host.InvokeWithWork(
            Method("WhenAllPair"),
            [a.Task, b.Task],
            () => a.SetException(boom),
            () => b.SetResult(2))!;

        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.Same(boom, task.Exception!.InnerException);
    }

    [Fact]
    public void WhenAllGenericSurfacesCancellation()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        var task = (Task)host.InvokeWithWork(
            Method("WhenAllPair"),
            [a.Task, b.Task],
            () => a.SetCanceled(),
            () => b.SetResult(2))!;

        Assert.Equal(TaskStatus.Canceled, task.Status);
    }

    [Fact]
    public void WhenAnyGenericPicksTheFirstCompleterDeterministically()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource<int>();
        var b = new TaskCompletionSource<int>();

        // a completes before b on the single logical thread, so the winner is always a.
        var task = (Task<Task<int>>)host.InvokeWithWork(
            Method("WhenAnyPair"),
            [a.Task, b.Task],
            () => a.SetResult(11),
            () => b.SetResult(22))!;

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Same(a.Task, Result<Task<int>>(task));
    }

    [Fact]
    public void BlockingGenericResultOnIncompleteProducerDrainsWithoutDeadlocking()
    {
        using var host = new SimulationHost(Start);

        object? result = host.Invoke(Method("BlockingResult"));

        Assert.Equal(21, result);
    }

    [Fact]
    public void TaskFactoryStartNewIsControlledUnderSimulation()
    {
        using var host = new SimulationHost(Start);

        var task = (Task<int>)host.Invoke(Method("FactoryStartNew"))!;

        Assert.Equal(5, Result<int>(task));
    }

    [Fact]
    public async Task OnlyRewrittenTaskFactoryStartNewRequiresActiveSimulation()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedGenericTask",
            "Conf.GenericTaskProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("FactoryStartNew").Invoke(null, null)!;
        int value = await task;

        Assert.Equal(5, value);
        SimulationNotActiveExceptionAssert.Throws(Method("FactoryStartNew"));
    }

    [Fact]
    public void UnwrapGenericResolvesTheInnerTaskResultThroughTheControlledLoop()
    {
        using var host = new SimulationHost(Start);
        var outer = new TaskCompletionSource<Task<int>>();
        var inner = new TaskCompletionSource<int>();

        // outer yields the inner task, then the inner task completes; both on the logical thread.
        var task = (Task<int>)host.InvokeWithWork(
            Method("UnwrapGeneric"),
            [outer.Task],
            () => outer.SetResult(inner.Task),
            () => inner.SetResult(99))!;

        Assert.Equal(99, Result<int>(task));
    }

    [Fact]
    public void UnwrapNonGenericResolvesTheInnerTaskCompletion()
    {
        using var host = new SimulationHost(Start);
        var outer = new TaskCompletionSource<Task>();
        var inner = new TaskCompletionSource<int>();

        var task = (Task)host.InvokeWithWork(
            Method("UnwrapNonGeneric"),
            [outer.Task],
            () => outer.SetResult(inner.Task),
            () => inner.SetResult(1))!;

        Assert.True(task.IsCompletedSuccessfully);
    }

    private MethodInfo Method(string name) => _probe.Value.Method(name);

    private static T Result<T>(object task)
    {
        var typed = (Task)task;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();
}
