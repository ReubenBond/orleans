using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.Tasks.Parallel"/> surface.
/// Once a fixture is rewritten with the controlled-task rule set, <c>Parallel.Invoke</c>/<c>For</c>/
/// <c>ForEach</c> decompose their branches into controlled operations on the coordinator and drain the
/// deterministic cluster drive until all complete, so every branch runs on the single logical thread;
/// faults aggregate into an <see cref="AggregateException"/>; and the break/stop (<c>ParallelLoopState</c>)
/// overloads are rejected precisely - proving the rule shapes resolve at real call sites.
/// </summary>
public sealed class ParallelConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        namespace Conf { public static class ParallelProbe {
            // Parallel.For runs every iteration on the single logical thread.
            public static Task<int> ForSumsAllIterations()
            {
                int sum = 0;
                Parallel.For(0, 5, i => sum += i);
                return Task.FromResult(sum);
            }

            public static void ForActionOnly(int[] sink) =>
                Parallel.For(0, 1, _ => sink[0]++);

            public static Task<int> ForCountsIterations()
            {
                int count = 0;
                Parallel.For(0, 5, _ => System.Threading.Interlocked.Increment(ref count));
                return Task.FromResult(count);
            }

            // Parallel.Invoke runs every action.
            public static Task<int> InvokeRunsAllActions()
            {
                int count = 0;
                Parallel.Invoke(() => count += 1, () => count += 10, () => count += 100);
                return Task.FromResult(count);
            }

            // Parallel.ForEach processes every element.
            public static Task<int> ForEachSumsAllElements()
            {
                int sum = 0;
                var items = new List<int> { 3, 4, 5 };
                Parallel.ForEach(items, x => sum += x);
                return Task.FromResult(sum);
            }

            // A body fault surfaces as an AggregateException.
            public static Task<bool> BodyFaultAggregates()
            {
                try
                {
                    Parallel.For(0, 3, i => { if (i == 2) throw new InvalidOperationException("boom"); });
                    return Task.FromResult(false);
                }
                catch (AggregateException ex)
                {
                    return Task.FromResult(ex.InnerException is InvalidOperationException);
                }
            }

            // The break/stop overload is rejected until it can be modelled deterministically.
            public static Task<int> ForLoopStateIsRejected()
            {
                Parallel.For(0, 3, (i, state) => state.Break());
                return Task.FromResult(0);
            }

            public static Task<long[]> IdentityAndTrace(Func<long> strand)
            {
                long[] values = new long[9];
                values[0] = Environment.CurrentManagedThreadId;
                values[1] = strand();
                var trace = new List<int>();
                Parallel.For(0, 3, index =>
                {
                    values[2 + index * 2] = Environment.CurrentManagedThreadId;
                    values[3 + index * 2] = strand();
                    trace.Add(index + 1);
                });
                values[8] = trace[0] * 100 + trace[1] * 10 + trace[2];
                return Task.FromResult(values);
            }

            private static int _invokeNullActionSideEffectCount;

            public static void InvokeWithNullAction()
            {
                _invokeNullActionSideEffectCount = 0;
                Action[] actions = { () => _invokeNullActionSideEffectCount++, null };
                Parallel.Invoke(actions);
            }

            public static int InvokeNullActionSideEffectCount() => _invokeNullActionSideEffectCount;
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public ParallelConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.ParallelRel", "Conf.ParallelProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.ParallelDbg", "Conf.ParallelProbe", Source, optimize: false));
    }

    public static TheoryData<bool> Optimize => new() { true, false };

    [Theory]
    [MemberData(nameof(Optimize))]
    public void ParallelBranchesRunTheFirstOnTheCallerAndTheOthersOnFreshControlledStrandsWithRepeatableTrace(bool optimize)
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

        // The caller runs the first worker, as the BCL's root replica does, so it takes the first element as its own
        // strand; every other worker is a fresh strand.
        Assert.Equal(first[1], first[3]);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(first[0], first[2 + i * 2]);
            Assert.NotEqual(Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.None, first[3 + i * 2]);
        }

        // The caller's worker takes the first iteration, as the caller's strand, as the BCL's root replica runs on the
        // caller's thread; while it runs, the worker it launched takes the second, on a fresh strand.
        Assert.Equal(first[1], first[3]);
        Assert.NotEqual(first[1], first[5]);
    }

    [Fact]
    public void ForRunsEveryIterationUnderTheClusterDrive()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("ForSumsAllIterations"))!;
        Assert.Equal(0 + 1 + 2 + 3 + 4, Result<int>(task));
    }

    [Fact]
    public void InvokeRunsEveryAction()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("InvokeRunsAllActions"))!;
        Assert.Equal(111, Result<int>(task));
    }

    [Fact]
    public void ForEachProcessesEveryElement()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("ForEachSumsAllElements"))!;
        Assert.Equal(12, Result<int>(task));
    }

    [Fact]
    public void BodyFaultAggregatesIntoAggregateException()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("BodyFaultAggregates"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void ForLoopStateOverloadIsRejected()
    {
        using var host = new SimulationHost(Start);
        var ex = Assert.ThrowsAny<Exception>(() => host.Invoke(Method("ForLoopStateIsRejected")));
        var unsupported = Unwrap(ex);
        Assert.Equal(
            "Orleans.Testing.Simulation.Runtime.SimulationApiException",
            unsupported.GetType().FullName);
    }

    [Fact]
    public async Task OnlyRewrittenForRequiresActiveSimulationWithoutRunningItsAction()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedParallel",
            "Conf.ParallelProbe",
            Source);
        var task = (Task<int>)uninstrumented.Method("ForCountsIterations").Invoke(null, null)!;
        Assert.Equal(5, await task);

        int[] sink = [0];
        SimulationNotActiveExceptionAssert.Throws(Method("ForActionOnly"), sink);
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

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void InvokeNullActionMatchesBclExceptionShape()
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.ThrowsAny<Exception>(() => host.Invoke(Method("InvokeWithNullAction")));

        Assert.Equal(0, (int)host.Invoke(Method("InvokeNullActionSideEffectCount"))!);
        var argument = Assert.IsType<ArgumentException>(Unwrap(exception));
        Assert.Null(argument.ParamName);
    }
}
