using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledParallel"/> shims: <c>Invoke</c> / <c>For</c> / <c>ForEach</c> run
/// their iterations on controlled workers on the coordinator and pump the deterministic loop until every worker has
/// finished, so all iterations run on the single logical thread; body faults are aggregated into an
/// <see cref="AggregateException"/>; the options token is observed; argument and cancellation checks happen in the BCL's
/// order; and the loop-state / TLocal overloads are rejected precisely.
/// </summary>
public sealed class ControlledParallelTests
{
    private static readonly int[] ForEachItems = [10, 20, 30];

    [Fact]
    public void ForRunsEveryIterationOnTheLogicalThread()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            int sum = 0;
            ParallelLoopResult result = ControlledParallel.For(0, 5, i => sum += i);

            Assert.Equal(0 + 1 + 2 + 3 + 4, sum);
            Assert.True(result.IsCompleted);
            Assert.Null(result.LowestBreakIteration);
        });
    }

    [Fact]
    public void ForLongRunsEveryIteration()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            long sum = 0;
            ControlledParallel.For(0L, 4L, i => sum += i);
            Assert.Equal(0 + 1 + 2 + 3, sum);
        });
    }

    [Fact]
    public void InvokeRunsEveryAction()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            bool a = false, b = false, c = false;
            ControlledParallel.Invoke(() => a = true, () => b = true, () => c = true);
            Assert.True(a && b && c);
        });
    }

    [Fact]
    public void ForEachProcessesEveryElement()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var seen = new List<int>();
            ControlledParallel.ForEach(ForEachItems, seen.Add);
            Assert.Equal(3, seen.Count);
            Assert.Contains(10, seen);
            Assert.Contains(20, seen);
            Assert.Contains(30, seen);
        });
    }

    [Fact]
    public void BodyFaultsAreAggregated()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ex = Assert.Throws<AggregateException>(
                () => ControlledParallel.For(0, 3, i =>
                {
                    if (i == 1)
                    {
                        throw new InvalidOperationException("boom");
                    }
                }));

            Assert.Single(ex.InnerExceptions);
            Assert.IsType<InvalidOperationException>(ex.InnerExceptions[0]);
        });
    }

    [Fact]
    public void CancelledOptionsTokenThrowsBeforeRunning()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var options = new ParallelOptions { CancellationToken = cts.Token };

            bool ran = false;
            Assert.Throws<OperationCanceledException>(
                () => ControlledParallel.For(0, 3, options, _ => ran = true));
            Assert.False(ran);
        });
    }

    [Fact]
    public void CancellationByFirstBodyPreventsLaterBodiesFromRunning()
    {
        var coordinator = new SimulationSchedulerTestHost();
        using var cancellation = new CancellationTokenSource();
        var executed = new List<int>();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var options = new ParallelOptions { CancellationToken = cancellation.Token };

            var exception = Assert.Throws<OperationCanceledException>(
                () => ControlledParallel.For(
                    0,
                    3,
                    options,
                    index =>
                    {
                        executed.Add(index);
                        if (index == 0)
                        {
                            cancellation.Cancel();
                        }
                    }));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal([0], executed);
        });

        coordinator.Scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal([0], executed);
    }

    [Fact]
    public void ForChecksItsRangeBeforeTheTokenAndForEachChecksTheTokenFirstAsTheBclDoes()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var options = new ParallelOptions { CancellationToken = new CancellationToken(canceled: true) };
        (bool ForCompleted, Type? ForEachThrew) bcl = (
            Parallel.For(3, 3, options, _ => { }).IsCompleted,
            Record.Exception(() => Parallel.ForEach(Array.Empty<int>(), options, _ => { }))?.GetType());
        (bool ForCompleted, Type? ForEachThrew) controlled = default;

        TaskTestHarness.RunInSimulation(coordinator, () =>
            controlled = (
                ControlledParallel.For(3, 3, options, _ => { }).IsCompleted,
                Record.Exception(() => ControlledParallel.ForEach(Array.Empty<int>(), options, _ => { }))?.GetType()));

        Assert.Equal((true, typeof(OperationCanceledException)), bcl);
        Assert.Equal(bcl, controlled);
    }

    [Fact]
    public void ForEachAsyncReportsAFailingSourceThroughItsTaskAsTheBclDoes()
    {
        var coordinator = new SimulationSchedulerTestHost();
        Task bclLoop = Parallel.ForEachAsync(new ThrowingEnumerable(), (_, _) => default);
        (bool Faulted, Type? Fault) bcl = (bclLoop.IsFaulted, bclLoop.Exception?.InnerException?.GetType());
        (bool Faulted, Type? Fault) controlled = default;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Task loop = ControlledParallel.ForEachAsync(new ThrowingEnumerable(), (_, _) => default);
            controlled = (loop.IsFaulted, loop.Exception?.InnerException?.GetType());
        });

        Assert.Equal((true, typeof(NotSupportedException)), bcl);
        Assert.Equal(bcl, controlled);
    }

    [Fact]
    public void RejectUnsupportedThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ex = Assert.Throws<SimulationApiException>(
                () => ControlledParallel.RejectUnsupported("System.Threading.Tasks.Parallel.For"));
            Assert.Contains("Parallel.For", ex.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OutsideSimulationForFailsBeforeInvokingBody()
    {
        int count = 0;

        Exception? exception = Record.Exception(
            () => ControlledParallel.For(0, 4, _ => Interlocked.Increment(ref count)));

        Assert.Equal(0, count);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.Tasks.Parallel.For");

        Exception? nullBodyException = Record.Exception(
            () => ControlledParallel.For(0, 4, null!));
        SimulationNotActiveExceptionAssert.Equal(
            nullBodyException,
            "System.Threading.Tasks.Parallel.For");
    }

    private enum InvokeOverload
    {
        Actions,
        OptionsAndActions,
    }

    private sealed class ThrowingEnumerable : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator() => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData((int)InvokeOverload.Actions)]
    [InlineData((int)InvokeOverload.OptionsAndActions)]
    public void InvokeNullActionMatchesBclExceptionShape(int overloadValue)
    {
        var overload = (InvokeOverload)overloadValue;
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sideEffectCount = 0;
            Action[] actions = [() => sideEffectCount++, null!];

            Exception? exception = Record.Exception(() =>
            {
                switch (overload)
                {
                    case InvokeOverload.Actions:
                        ControlledParallel.Invoke(actions);
                        break;
                    case InvokeOverload.OptionsAndActions:
                        ControlledParallel.Invoke(new ParallelOptions(), actions);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(overloadValue), overloadValue, null);
                }
            });

            Assert.Equal(0, sideEffectCount);
            var argument = Assert.IsType<ArgumentException>(exception);
            Assert.Null(argument.ParamName);
        });
    }

    [Theory]
    [InlineData((int)InvokeOverload.Actions)]
    [InlineData((int)InvokeOverload.OptionsAndActions)]
    public void InvokeNullActionLeavesEveryPublicLoopDiagnosticUnchanged(int overloadValue)
    {
        var overload = (InvokeOverload)overloadValue;
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var before = (
                coordinator.Scheduler.VirtualTime,
                coordinator.Scheduler.RunnableOperationCount,
                coordinator.Scheduler.WaitingOperationCount,
                NextDeadline: coordinator.Scheduler.NextTimerDue,
                coordinator.Scheduler.IsIdle);
            var sideEffectCount = 0;
            Action[] actions = [() => sideEffectCount++, null!];

            Exception? exception = Record.Exception(() =>
            {
                if (overload == InvokeOverload.Actions)
                {
                    ControlledParallel.Invoke(actions);
                }
                else
                {
                    ControlledParallel.Invoke(new ParallelOptions(), actions);
                }
            });

            Assert.Equal(0, sideEffectCount);
            Assert.Equal(before.VirtualTime, coordinator.Scheduler.VirtualTime);
            Assert.Equal(before.RunnableOperationCount, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(before.WaitingOperationCount, coordinator.Scheduler.WaitingOperationCount);
            Assert.Equal(before.NextDeadline, coordinator.Scheduler.NextTimerDue);
            Assert.Equal(before.IsIdle, coordinator.Scheduler.IsIdle);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);

            var argument = Assert.IsType<ArgumentException>(exception);
            Assert.Null(argument.ParamName);
        });
    }
}
