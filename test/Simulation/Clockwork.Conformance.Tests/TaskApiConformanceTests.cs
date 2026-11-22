using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the ordinary <see cref="Task"/> surface that application code calls
/// directly (combinators, synchronous waits, continuations) once rewritten with the controlled-task
/// rule set, plus its cross-cutting guarantees: multiple awaiters on one antecedent,
/// deterministic <c>WhenAny</c> ordering, fault propagation through <c>WhenAll</c>, synchronous waits
/// that pump the loop instead of dead-locking, per-node isolation, and simulation-only execution for
/// rewritten assemblies.
/// </summary>
/// <remarks>
/// These tests drive the non-generic <c>WhenAll</c>/<c>WhenAny</c>, <c>Task.Wait()</c>, and
/// <c>ContinueWith(Action&lt;Task&gt;)</c> overloads through non-generic <see cref="Task"/> antecedents;
/// the generic <c>Task&lt;T&gt;</c> combinators and the blocking <c>Result</c> accessor have their own
/// coverage in <see cref="GenericTaskConformanceTests"/>.
/// </remarks>
public sealed class TaskApiConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System.Threading.Tasks;
        namespace Conf { public static class TaskProbe {
            // ---- combinators (non-generic antecedents) ----
            public static Task WhenAllTwo(Task a, Task b) => WhenAllImpl(a, b);
            private static async Task WhenAllImpl(Task a, Task b) => await Task.WhenAll(a, b);

            public static Task<Task> WhenAnyTwo(Task a, Task b) => WhenAnyImpl(a, b);
            private static async Task<Task> WhenAnyImpl(Task a, Task b) => await Task.WhenAny(a, b);

            // ---- multiple awaiters share one antecedent ----
            public static Task<int> MultipleAwaiters(Task<int> shared) => Combine(AwaitOnce(shared), AwaitOnce(shared));
            private static async Task<int> AwaitOnce(Task<int> t) => await t;
            private static async Task<int> Combine(Task<int> a, Task<int> b) => await a + await b;

            // ---- continuation registered before the antecedent completes ----
            public static Task ContinueAfter(Task a, int[] sink) => a.ContinueWith(_ => { sink[0] = 7; });

            // ---- synchronous wait on a controlled producer must pump, not deadlock ----
            public static int WaitOnControlled()
            {
                Task<int> t = Producer();
                t.Wait();
                return t.Result;
            }

            public static Task<int> Producer() => ProducerImpl();
            private static async Task<int> ProducerImpl()
            {
                int sum = 0;
                for (int i = 0; i < 3; i++) { await Task.Yield(); sum += 10; }
                return sum;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public TaskApiConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TaskApi", "Conf.TaskProbe", Source, optimize: true));

    [Fact]
    public void WhenAllCompletesOnlyAfterEveryAntecedent()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();

        var task = (Task)host.InvokeWithWork(
            Method("WhenAllTwo"),
            [a.Task, b.Task],
            () => b.SetResult(),
            () => a.SetResult())!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
    }

    [Fact]
    public void WhenAllPropagatesTheFaultedAntecedentsException()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();
        var boom = new InvalidTimeZoneException("boom");

        var task = (Task)host.InvokeWithWork(
            Method("WhenAllTwo"),
            [a.Task, b.Task],
            () => a.SetException(boom),
            () => b.SetResult())!;

        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.Same(boom, task.Exception!.InnerException);
    }

    [Fact]
    public void WhenAnyPicksTheFirstCompleterDeterministically()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();

        // a completes before b on the single logical thread, so the winner is always a.
        var task = (Task<Task>)host.InvokeWithWork(
            Method("WhenAnyTwo"),
            [a.Task, b.Task],
            () => a.SetResult(),
            () => b.SetResult())!;

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Same(a.Task, Result<Task>(task));
    }

    [Fact]
    public void MultipleAwaitersOnOneAntecedentAllResume()
    {
        using var host = new SimulationHost(Start);
        var shared = new TaskCompletionSource<int>();

        var task = (Task<int>)host.InvokeWithWork(
            Method("MultipleAwaiters"),
            [shared.Task],
            () => shared.SetResult(21))!;

        Assert.Equal(42, Result<int>(task));
    }

    [Fact]
    public void ContinuationRunsOnTheLogicalThreadAfterTheAntecedent()
    {
        using var host = new SimulationHost(Start);
        var a = new TaskCompletionSource();
        var sink = new int[1];

        var task = (Task)host.InvokeWithWork(
            Method("ContinueAfter"),
            [a.Task, sink],
            () => a.SetResult())!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(7, sink[0]);
    }

    [Fact]
    public void SynchronousWaitPumpsTheLoopWithoutDeadlocking()
    {
        using var host = new SimulationHost(Start);

        object? result = host.Invoke(Method("WaitOnControlled"));

        Assert.Equal(30, result);
    }

    [Fact]
    public void EachNodePumpsItsOwnControlledWorkInIsolation()
    {
        using var host = new SimulationHost(Start, nodeAddresses: ["alpha", "beta"]);

        object? alpha = host.InvokeOnNode("alpha", Method("WaitOnControlled"));
        object? beta = host.InvokeOnNode("beta", Method("WaitOnControlled"));

        Assert.Equal(30, alpha);
        Assert.Equal(30, beta);
    }

    [Fact]
    public async Task OnlyRewrittenAsyncAssemblyRequiresActiveSimulation()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedTaskApi",
            "Conf.TaskProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("Producer").Invoke(null, null)!;
        int value = await task;

        Assert.Equal(30, value);
        SimulationNotActiveExceptionAssert.Throws(Method("Producer"));
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

    [Fact]
    public void ContinueWithFlowsCapturedExecutionContext()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(ExecutionContextMethod("ContinueWithFlowsContext"))!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(5, Result<int>(task));
    }

    private const string ExecutionContextSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class ContinueExecutionContextProbe {
            public static Task<int> ContinueWithFlowsContext()
            {
                var antecedent = new TaskCompletionSource();
                var ambient = new AsyncLocal<int> { Value = 5 };
                var seen = -1;
                var continuation = antecedent.Task.ContinueWith(_ => { seen = ambient.Value; });
                ambient.Value = 9;
                antecedent.SetResult();
                continuation.Wait();
                return Task.FromResult(seen);
            }
        } }
        """;

    private StagedProbe? _executionContextProbe;

    private MethodInfo ExecutionContextMethod(string name) =>
        (_executionContextProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskApiExecutionContext",
            "Conf.ContinueExecutionContextProbe",
            ExecutionContextSource,
            optimize: true)).Method(name);

    [Theory]
    [InlineData((int)ContinueWithOceShape.Action, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.Action, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.Action, (int)TokenlessOceCase.NotRequested)]
    [InlineData((int)ContinueWithOceShape.Result, (int)TokenlessOceCase.Bare)]
    [InlineData((int)ContinueWithOceShape.Result, (int)TokenlessOceCase.Requested)]
    [InlineData((int)ContinueWithOceShape.Result, (int)TokenlessOceCase.NotRequested)]
    public void ContinueWithOceWithoutAssociatedTokenFaults(int shapeValue, int oceCaseValue)
    {
        var shape = (ContinueWithOceShape)shapeValue;
        var oceCase = (TokenlessOceCase)oceCaseValue;
        using var thrownSource = new CancellationTokenSource();
        if (oceCase == TokenlessOceCase.Requested)
        {
            thrownSource.Cancel();
        }

        var thrown = oceCase == TokenlessOceCase.Bare
            ? new OperationCanceledException()
            : new OperationCanceledException(thrownSource.Token);
        using var host = new SimulationHost(Start);
        string methodName = shape == ContinueWithOceShape.Action
            ? "ContinueAction"
            : "ContinueResult";
        var antecedent = new TaskCompletionSource();
        var genericAntecedent = new TaskCompletionSource<int>();
        Task antecedentTask = shape == ContinueWithOceShape.Action
            ? antecedent.Task
            : genericAntecedent.Task;
        Action completeAntecedent = shape == ContinueWithOceShape.Action
            ? antecedent.SetResult
            : () => genericAntecedent.SetResult(21);
        var task = (Task)host.InvokeWithWork(
            EdgeCaseMethod(methodName),
            [antecedentTask, thrown],
            completeAntecedent)!;

        Assert.Equal(oceCase == TokenlessOceCase.Requested, thrownSource.IsCancellationRequested);
        Assert.True(antecedentTask.IsCompletedSuccessfully);
        AssertOceFault(task, thrown);
    }

    [Fact]
    public void WaitAllNullElementMatchesBcl()
    {
        using var host = new SimulationHost(Start);

        var error = Assert.Throws<ArgumentException>(() => host.Invoke(EdgeCaseMethod("WaitAllNullFirst")));

        Assert.Equal("tasks", error.ParamName);
    }

    [Fact]
    public void WaitAnyNullElementMatchesBcl()
    {
        using var host = new SimulationHost(Start);

        var error = Assert.Throws<ArgumentException>(() => host.Invoke(EdgeCaseMethod("WaitAnyNullFirst")));

        Assert.Equal("tasks", error.ParamName);
    }

    private const string EdgeCaseSource = """
        using System;
        using System.Threading.Tasks;
        namespace Conf { public static class TaskOceAndWaitProbe {
            public static Task ContinueAction(Task antecedent, OperationCanceledException error) =>
                antecedent.ContinueWith(_ => Throw(error));

            public static Task<int> ContinueResult(Task<int> antecedent, OperationCanceledException error) =>
                antecedent.ContinueWith(_ => ThrowResult(error));

            public static void WaitAllNullFirst() =>
                Task.WaitAll(new Task[] { null, Task.CompletedTask });

            public static int WaitAnyNullFirst() =>
                Task.WaitAny(new Task[] { null, Task.CompletedTask });

            private static void Throw(OperationCanceledException error) => throw error;
            private static int ThrowResult(OperationCanceledException error) => throw error;
        } }
        """;

    private StagedProbe? _edgeCaseProbe;

    private MethodInfo EdgeCaseMethod(string name) =>
        (_edgeCaseProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskOceAndWait",
            "Conf.TaskOceAndWaitProbe",
            EdgeCaseSource,
            optimize: true)).Method(name);

    private static void AssertOceFault(Task task, OperationCanceledException thrown)
    {
        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.False(task.IsCanceled);
        var aggregate = Assert.IsType<AggregateException>(task.Exception);
        var inner = Assert.Single(aggregate.InnerExceptions);
        var fault = Assert.IsType<OperationCanceledException>(inner);
        Assert.Same(thrown, fault);
        Assert.Equal(thrown.CancellationToken, fault.CancellationToken);
        var awaited = Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.Same(thrown, awaited);
        Assert.Equal(thrown.CancellationToken, awaited.CancellationToken);
    }

    private enum ContinueWithOceShape
    {
        Action,
        Result,
    }

    private enum TokenlessOceCase
    {
        Bare,
        Requested,
        NotRequested,
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NullWaitElementPositionsMatchBclWithoutCompletingValidSibling(
        bool waitAll,
        bool nullFirst)
    {
        using var host = new SimulationHost(Start);
        var pending = new TaskCompletionSource();
        string methodName = waitAll ? "WaitAll" : "WaitAny";

        Exception? error = Record.Exception(
            () => host.Invoke(NullWaitPositionMethod(methodName), pending.Task, nullFirst));

        Assert.False(pending.Task.IsCompleted);
        var argument = Assert.IsType<ArgumentException>(error);
        Assert.Equal("tasks", argument.ParamName);
    }

    private const string NullWaitPositionSource = """
        using System.Threading.Tasks;
        namespace Conf { public static class TaskNullWaitPositionProbe {
            public static void WaitAll(Task validSibling, bool nullFirst) =>
                Task.WaitAll(nullFirst
                    ? new Task[] { null, validSibling }
                    : new Task[] { validSibling, null });

            public static int WaitAny(Task validSibling, bool nullFirst) =>
                Task.WaitAny(nullFirst
                    ? new Task[] { null, validSibling }
                    : new Task[] { validSibling, null });
        } }
        """;

    private StagedProbe? _nullWaitPositionProbe;

    private MethodInfo NullWaitPositionMethod(string name) =>
        (_nullWaitPositionProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskNullWaitPosition",
            "Conf.TaskNullWaitPositionProbe",
            NullWaitPositionSource,
            optimize: true)).Method(name);
}
