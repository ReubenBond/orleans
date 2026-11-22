using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

public sealed class ControlledTimerTests
{
    [Fact]
    public void PublicEntriesRequireActiveSimulation()
    {
        Assert.Throws<Orleans.Testing.Simulation.Runtime.Shims.SimulationNotActiveException>(() => new ControlledTimer(_ => { }));
        Assert.Throws<Orleans.Testing.Simulation.Runtime.Shims.SimulationNotActiveException>(() => _ = ControlledTimer.ActiveCount);
    }

    [Fact]
    public void ConstructorOnlyTimerIsDisabledAndUsesItselfAsState()
    {
        var coordinator = new SimulationSchedulerTestHost();
        object? observed = null;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new ControlledTimer(state => observed = state);
            Assert.Equal(0, ControlledTimer.ActiveCount);
            Assert.True(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Same(timer, observed);
            Assert.Equal(0, ControlledTimer.ActiveCount);
        });
    }

    [Fact]
    public void OneShotAndPeriodicFiringsUseVirtualTime()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var fired = new List<TimeSpan>();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var timer = new ControlledTimer(
                _ => fired.Add(coordinator.Scheduler.VirtualTime),
                null,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20));

            Assert.Equal(TimeSpan.FromMilliseconds(10), coordinator.Scheduler.NextTimerDue);
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(10));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(30));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30)], fired);
            Assert.Equal(TimeSpan.FromMilliseconds(50), coordinator.Scheduler.NextTimerDue);
        });
    }

    [Fact]
    public void ReentrantChangeCancelsTheOldPeriodicGeneration()
    {
        var coordinator = new SimulationSchedulerTestHost();
        ControlledTimer? timer = null;
        var fired = new List<TimeSpan>();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            timer = new ControlledTimer(
                _ =>
                {
                    fired.Add(coordinator.Scheduler.VirtualTime);
                    if (fired.Count == 1)
                    {
                        Assert.True(timer!.Change(5, Timeout.Infinite));
                    }
                },
                null,
                1,
                2);

            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(1));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMilliseconds(6), coordinator.Scheduler.NextTimerDue);

            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(6));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal([TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(6)], fired);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            timer!.Dispose();
        });
    }

    [Fact]
    public void DisposeSuppressesAnAlreadyQueuedCallback()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var fired = false;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var timer = new ControlledTimer(_ => fired = true, null, 0, Timeout.Infinite);
            timer.Dispose();
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
        });

        Assert.False(fired);
    }

    [Fact]
    public void CallbackFlowsConstructionExecutionContextOnAFreshStrand()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var local = new AsyncLocal<string?>();
        string? observed = null;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            local.Value = "captured";
            using var timer = new ControlledTimer(_ => observed = local.Value, null, 0, Timeout.Infinite);
            local.Value = "changed";
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
        });

        Assert.Equal("captured", observed);
    }

    [Fact]
    public void SameDeadlineCallbacksRunInRegistrationOrder()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var order = new List<int>();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var first = new ControlledTimer(_ => order.Add(1), null, 5, Timeout.Infinite);
            using var second = new ControlledTimer(_ => order.Add(2), null, 5, Timeout.Infinite);
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(5));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
        });

        Assert.Equal([1, 2], order);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public void SignedTimeoutValidationMatchesTheBcl(int invalid)
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ControlledTimer(_ => { }, null, invalid, Timeout.Infinite));
            using var timer = new ControlledTimer(_ => { });
            Assert.Throws<ArgumentOutOfRangeException>(() => timer.Change(invalid, Timeout.Infinite));
        });
    }

    [Fact]
    public void DisposeWaitHandleSignalsControlledEventAndRejectsKernelEscape()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var timer = new ControlledTimer(_ => { });
            using ManualResetEvent completion = ControlledEventWaitHandle.CreateManualResetEvent(false);

            Assert.True(timer.Dispose(completion));
            Assert.True(ControlledWaitHandle.WaitOne(completion, 0));

            using var uncontrolled = new ManualResetEvent(false);
            using var second = new ControlledTimer(_ => { });
            Assert.Throws<SimulationApiException>(() => second.Dispose(uncontrolled));
        });
    }
}
