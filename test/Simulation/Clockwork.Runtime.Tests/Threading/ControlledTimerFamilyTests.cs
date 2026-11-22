using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tests.Shims;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

public sealed class ControlledTimerFamilyTests
{
    [Fact]
    public void TimersTimerOneShotRaisesElapsedAtVirtualTime()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var elapsed = new List<System.Timers.ElapsedEventArgs>();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new Orleans.Testing.Simulation.Shims.System.Timers.ControlledTimer(TimeSpan.FromMilliseconds(25))
            {
                AutoReset = false,
            };
            timer.Elapsed += (_, args) => elapsed.Add(args);
            timer.Start();

            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(25));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.False(timer.Enabled);
            Assert.Single(elapsed);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
        });
    }

    [Fact]
    public void TimersTimerStopAndCloseCancelPendingElapsedEvents()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var count = 0;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new Orleans.Testing.Simulation.Shims.System.Timers.ControlledTimer(10);
            timer.Elapsed += (_, _) => count++;
            timer.Start();
            timer.Stop();
            Assert.Null(coordinator.Scheduler.NextTimerDue);

            timer.Start();
            timer.Close();
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(100));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
        });

        Assert.Equal(0, count);
    }

    [Fact]
    public void TimersTimerRejectsUncontrolledMarshaling()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new Orleans.Testing.Simulation.Shims.System.Timers.ControlledTimer();
            Assert.Throws<SimulationApiException>(
                () => timer.SynchronizingObject = new SynchronizingObjectStub());
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public void TimersTimerConstructorValidatesInterval(double interval)
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(
            coordinator,
            () => Assert.Throws<ArgumentException>(() => new Orleans.Testing.Simulation.Shims.System.Timers.ControlledTimer(interval)));
    }

    [Fact]
    public void PeriodicTimerCoalescesTicksUntilConsumed()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new ControlledPeriodicTimer(TimeSpan.FromMilliseconds(10));

            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(10));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(20));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            ValueTask<bool> tick = timer.WaitForNextTickAsync();
            Assert.True(tick.IsCompletedSuccessfully);
            Assert.True(tick.Result);

            ValueTask<bool> next = timer.WaitForNextTickAsync();
            Assert.False(next.IsCompleted);
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(30));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(next.Result);
        });
    }

    [Fact]
    public void PeriodicTimerEnforcesSingleConsumerAndDisposeReturnsFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var timer = new ControlledPeriodicTimer(TimeSpan.FromMilliseconds(10));
            ValueTask<bool> waiter = timer.WaitForNextTickAsync();
            Assert.Throws<InvalidOperationException>(
                () => GC.KeepAlive(timer.WaitForNextTickAsync(TestContext.Current.CancellationToken).AsTask()));

            timer.Dispose();
            Assert.True(waiter.IsCompleted);
            Assert.False(waiter.Result);
            ValueTask<bool> afterDispose = timer.WaitForNextTickAsync();
            Assert.True(afterDispose.IsCompletedSuccessfully);
            Assert.False(afterDispose.Result);
        });
    }

    [Fact]
    public void PeriodicTimerCancellationOnlyCancelsTheCurrentWait()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new ControlledPeriodicTimer(TimeSpan.FromMilliseconds(10));
            using var cancellation = new CancellationTokenSource();
            ValueTask<bool> waiter = timer.WaitForNextTickAsync(cancellation.Token);
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => waiter.GetAwaiter().GetResult());

            ValueTask<bool> next = timer.WaitForNextTickAsync();
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(10));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(next.Result);
        });
    }

    [Fact]
    public void PeriodicTimerPeriodChangeResetsTheFutureSchedule()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new ControlledPeriodicTimer(TimeSpan.FromMilliseconds(10));
            timer.Period = TimeSpan.FromMilliseconds(25);
            Assert.Equal(TimeSpan.FromMilliseconds(25), timer.Period);
            Assert.Equal(TimeSpan.FromMilliseconds(25), coordinator.Scheduler.NextTimerDue);
        });
    }

    private sealed class SynchronizingObjectStub : System.ComponentModel.ISynchronizeInvoke
    {
        public bool InvokeRequired => true;

        public IAsyncResult BeginInvoke(Delegate method, object?[]? args) => throw new NotSupportedException();

        public object? EndInvoke(IAsyncResult result) => throw new NotSupportedException();

        public object? Invoke(Delegate method, object?[]? args) => throw new NotSupportedException();
    }
}
