using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="TaskFactory"/> / <see cref="TaskFactory{TResult}"/>
/// <c>StartNew</c> family and the generic <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;</c> closure
/// . Once a fixture is rewritten with the controlled-task rule set,
/// <c>Task.Factory.StartNew</c> queues its body as a fresh controlled operation and the generic
/// continuation runs on the single logical thread — proving the rewriter's combined
/// generic-method-on-generic-type binding resolves correctly at a real call site.
/// </summary>
public sealed class TaskFactoryConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Globalization;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class FactoryProbe {
            // Task.Factory.StartNew(Func<TResult>) computes on the logical thread and returns its value.
            public static Task<int> StartValue() => ValueImpl();
            private static async Task<int> ValueImpl() => await Task.Factory.StartNew(() => 42);
            public static Task StartActionOnly(int[] sink) => Task.Factory.StartNew(() => sink[0] = 42);

            // Task.Factory.StartNew(Action) mutates shared state under the cluster drive.
            public static Task<int> StartAction(int[] sink) => ActionImpl(sink);
            private static async Task<int> ActionImpl(int[] sink)
            {
                await Task.Factory.StartNew(() => { sink[0] = 7; });
                return sink[0];
            }

            public static Task<int> StateResult() =>
                Task.Factory.StartNew(
                    state => (int)state!,
                    42,
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default);

            public static Task<int> GenericStateResult() =>
                new TaskFactory<int>().StartNew(
                    state => (int)state!,
                    43,
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default);

            public static Task<long[]> Identity(Func<long> strand) => IdentityImpl(strand);
            private static async Task<long[]> IdentityImpl(Func<long> strand)
            {
                long[] values = [Environment.CurrentManagedThreadId, strand(), 0, 0];
                await Task.Factory.StartNew(
                    state =>
                    {
                        var pair = (object[])state!;
                        var sink = (long[])pair[0];
                        var readStrand = (Func<long>)pair[1];
                        sink[2] = Environment.CurrentManagedThreadId;
                        sink[3] = readStrand();
                    },
                    new object[] { values, strand },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default);
                return values;
            }

            public static Task<string> Trace() => TraceImpl();
            private static async Task<string> TraceImpl()
            {
                var trace = new List<int>();
                Task[] tasks = new Task[4];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Factory.StartNew(
                        state => trace.Add((int)state!),
                        i,
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        TaskScheduler.Default);
                }

                await Task.WhenAll(tasks);
                return string.Join(",", trace);
            }

            // Generic Task<T>.ContinueWith<TNewResult> projects the antecedent result.
            public static Task<string> Projected() => ProjectImpl();
            private static async Task<string> ProjectImpl()
            {
                var antecedent = Task.Factory.StartNew(() => 21);
                var projected = antecedent.ContinueWith(t => (t.Result * 2).ToString(CultureInfo.InvariantCulture));
                return await projected;
            }

            // Generic Task<T>.ContinueWith(Action<Task<T>>) observes the typed antecedent.
            public static Task<int> Observed() => ObserveImpl();
            private static async Task<int> ObserveImpl()
            {
                int seen = 0;
                var antecedent = Task.Factory.StartNew(() => 11);
                await antecedent.ContinueWith(t => { seen = t.Result; });
                return seen;
            }

            // AttachedToParent is rejected precisely inside a simulation.
            public static Task<bool> RejectsAttached()
            {
                try
                {
                    Task.Factory.StartNew(() => { }, TaskCreationOptions.AttachedToParent);
                    return Task.FromResult(false);
                }
                catch (Exception ex) when (ex.GetType().Name == "SimulationApiException")
                {
                    return Task.FromResult(true);
                }
            }

            public static Task<bool> RejectsCustomScheduler()
            {
                var schedulers = new ConcurrentExclusiveSchedulerPair();
                try
                {
                    Task.Factory.StartNew(
                        () => { },
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        schedulers.ExclusiveScheduler);
                    return Task.FromResult(false);
                }
                catch (Exception ex) when (ex.GetType().Name == "SimulationApiException")
                {
                    return Task.FromResult(true);
                }
                finally
                {
                    schedulers.Complete();
                }
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public TaskFactoryConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TaskFactoryRel", "Conf.FactoryProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.TaskFactoryDbg", "Conf.FactoryProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Fact]
    public void StartNewComputesResultOnTheLogicalThread()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("StartValue"))!;
        Assert.Equal(42, Result<int>(task));
    }

    [Fact]
    public void StartNewActionAdvancesUnderTheClusterDrive()
    {
        using var host = new SimulationHost(Start);
        var sink = new int[1];
        var task = (Task<int>)host.Invoke(Method("StartAction"), sink)!;
        Assert.Equal(7, Result<int>(task));
        Assert.Equal(7, sink[0]);
    }

    [Fact]
    public void StateAndFullSchedulerOverloadsPreserveResults()
    {
        using var host = new SimulationHost(Start);
        var state = (Task<int>)host.Invoke(Method("StateResult"))!;
        var genericState = (Task<int>)host.Invoke(Method("GenericStateResult"))!;
        Assert.Equal(42, Result<int>(state));
        Assert.Equal(42, state.AsyncState);
        Assert.Equal(43, Result<int>(genericState));
        Assert.Equal(43, genericState.AsyncState);
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void StartNewRunsOnTheControlledLogicalThreadAndFreshStrand(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var task = (Task<long[]>)host.Invoke(
            Method("Identity", optimize),
            (Func<long>)(() => Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.CurrentId))!;
        long[] values = Result<long[]>(task);

        Assert.Equal(values[0], values[2]);
        Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, values[1]);
        Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, values[3]);
        Assert.NotEqual(values[1], values[3]);
    }

    [Theory]
    [MemberData(nameof(Optimize))]
    public void StartNewTraceIsRepeatable(bool optimize)
    {
        string Run()
        {
            using var host = new SimulationHost(Start);
            return Result<string>((Task<string>)host.Invoke(Method("Trace", optimize))!);
        }

        Assert.Equal("0,1,2,3", Run());
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void GenericContinueWithProjectsTheAntecedentResult()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<string>)host.Invoke(Method("Projected"))!;
        Assert.Equal("42", Result<string>(task));
    }

    [Fact]
    public void GenericContinueWithActionObservesTypedAntecedent()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("Observed"))!;
        Assert.Equal(11, Result<int>(task));
    }

    [Fact]
    public void StartNewRejectsAttachedToParentInsideSimulation()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("RejectsAttached"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void StartNewRejectsCustomSchedulerInsideSimulation()
    {
        // The guard is off: completing the probe's scheduler pair queues its completion to the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        var task = (Task<bool>)host.Invoke(Method("RejectsCustomScheduler"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenStartNewRequiresActiveSimulationWithoutRunningItsAction()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedTaskFactory",
            "Conf.FactoryProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("StartValue").Invoke(null, null)!;
        Assert.Equal(42, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("StartActionOnly"), sink);
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
    public void StartNewFlowsCapturedExecutionContext()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(ExecutionContextMethod("StartNewFlowsContext"))!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        Assert.Equal(5, Result<int>(task));
    }

    private const string ExecutionContextSource = """
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class FactoryExecutionContextProbe {
            public static Task<int> StartNewFlowsContext()
            {
                var ambient = new AsyncLocal<int> { Value = 5 };
                var task = Task.Factory.StartNew(() => ambient.Value);
                ambient.Value = 9;
                return task;
            }
        } }
        """;

    private StagedProbe? _executionContextProbe;

    private MethodInfo ExecutionContextMethod(string name) =>
        (_executionContextProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskFactoryExecutionContext",
            "Conf.FactoryExecutionContextProbe",
            ExecutionContextSource,
            optimize: true)).Method(name);

    [Theory]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.Bare)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.UnmatchedRequested)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.MatchingNotRequested)]
    [InlineData((int)StartNewOceDelegateShape.Action, (int)StartNewOceCase.MatchingRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.Bare)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.UnmatchedRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.MatchingNotRequested)]
    [InlineData((int)StartNewOceDelegateShape.Function, (int)StartNewOceCase.MatchingRequested)]
    public void StartNewOceClassificationMatchesBcl(int delegateShapeValue, int oceCaseValue)
    {
        var delegateShape = (StartNewOceDelegateShape)delegateShapeValue;
        var oceCase = (StartNewOceCase)oceCaseValue;
        using var associatedSource = new CancellationTokenSource();
        using var unrelatedSource = new CancellationTokenSource();

        OperationCanceledException thrown;
        switch (oceCase)
        {
            case StartNewOceCase.Bare:
                thrown = new OperationCanceledException();
                break;
            case StartNewOceCase.UnmatchedRequested:
                unrelatedSource.Cancel();
                thrown = new OperationCanceledException(unrelatedSource.Token);
                break;
            case StartNewOceCase.MatchingNotRequested:
            case StartNewOceCase.MatchingRequested:
                thrown = new OperationCanceledException(associatedSource.Token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(oceCaseValue));
        }

        using var host = new SimulationHost(Start);
        string methodName = delegateShape == StartNewOceDelegateShape.Action
            ? "StartOceAction"
            : "StartOceFunction";
        var task = (Task)host.Invoke(
            EdgeCaseMethod(methodName),
            (int)oceCase,
            associatedSource,
            thrown)!;

        bool shouldCancel = oceCase == StartNewOceCase.MatchingRequested;
        Assert.Equal(shouldCancel, associatedSource.IsCancellationRequested);
        AssertOceTaskCompletion(task, thrown, shouldCancel);
    }

    private const string EdgeCaseSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class FactoryOceProbe {
            public static Task StartOceAction(
                int scenario,
                CancellationTokenSource associated,
                OperationCanceledException error)
            {
                Action body = () =>
                {
                    if (scenario == 3) associated.Cancel();
                    throw error;
                };
                return scenario == 0
                    ? Task.Factory.StartNew(body)
                    : Task.Factory.StartNew(body, associated.Token);
            }

            public static Task StartOceFunction(
                int scenario,
                CancellationTokenSource associated,
                OperationCanceledException error)
            {
                Func<int> body = () =>
                {
                    if (scenario == 3) associated.Cancel();
                    throw error;
                };
                return scenario == 0
                    ? Task.Factory.StartNew(body)
                    : Task.Factory.StartNew(body, associated.Token);
            }
        } }
        """;

    private StagedProbe? _edgeCaseProbe;

    private MethodInfo EdgeCaseMethod(string name) =>
        (_edgeCaseProbe ??= _fixture.StageControlledTasks(
            "Conf.TaskFactoryOce",
            "Conf.FactoryOceProbe",
            EdgeCaseSource,
            optimize: true)).Method(name);

    private static void AssertOceTaskCompletion(
        Task task,
        OperationCanceledException thrown,
        bool shouldCancel)
    {
        if (shouldCancel)
        {
            Assert.Equal(TaskStatus.Canceled, task.Status);
            Assert.True(task.IsCanceled);
            Assert.Null(task.Exception);
            var awaited = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
            Assert.Equal(thrown.CancellationToken, awaited.CancellationToken);
            return;
        }

        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.False(task.IsCanceled);
        var aggregate = Assert.IsType<AggregateException>(task.Exception);
        var inner = Assert.Single(aggregate.InnerExceptions);
        var fault = Assert.IsType<OperationCanceledException>(inner);
        Assert.Same(thrown, fault);
        Assert.Equal(thrown.CancellationToken, fault.CancellationToken);
        var awaitedFault = Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.Same(thrown, awaitedFault);
        Assert.Equal(thrown.CancellationToken, awaitedFault.CancellationToken);
    }

    private enum StartNewOceDelegateShape
    {
        Action,
        Function,
    }

    private enum StartNewOceCase
    {
        Bare,
        UnmatchedRequested,
        MatchingNotRequested,
        MatchingRequested,
    }
}
