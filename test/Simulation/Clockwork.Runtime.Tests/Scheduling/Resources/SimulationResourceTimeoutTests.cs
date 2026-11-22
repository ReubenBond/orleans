using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling.Resources;

/// <summary>
/// Coverage of virtual-time timeout waits: zero, finite, and infinite budgets; deterministic
/// resolution of release-vs-timeout races; monotonic modeled-time advancement driven only when the
/// runnable set is exhausted; and correct terminal outcomes with no real-time delays.
/// </summary>
public sealed class SimulationResourceTimeoutTests
{
    private static SimulationPauseReason Reason(string tag) => SimulationPauseReason.ResourceWait(tag);

    [Fact]
    public void ZeroTimeoutNeverParksAndResolvesTimedOutImmediately()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        SimulationWaitOutcome? outcome = null;
        var parked = false;

        var op = scheduler.Schedule("op", () =>
        {
            outcome = scheduler.WaitOnResource(resource, TimeSpan.Zero, Reason("sem"));
            parked = true;
        });

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationWaitOutcome.TimedOut, outcome);
        Assert.True(parked);
        Assert.Equal(SimulationOperationState.Completed, op.State);
        Assert.Equal(TimeSpan.Zero, scheduler.VirtualTime);
        Assert.Equal(0, resource.WaiterCount);
    }

    [Fact]
    public void FiniteTimeoutFiresWhenNoSignalArrivesAndAdvancesVirtualTime()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;
        var op = scheduler.Schedule("op", () =>
        {
            outcome = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(5), Reason("m"));
        });

        // Park on the resource with a deadline.
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Paused, op.State);
        Assert.False(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Single(scheduler.CapturePendingTimeouts());

        // Nothing runnable -> advancing virtual time fires the timeout.
        Assert.True(scheduler.TryAdvanceVirtualTime());
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.VirtualTime);
        Assert.Equal(SimulationOperationState.Runnable, op.State);

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationWaitOutcome.TimedOut, outcome);
        Assert.Empty(scheduler.CapturePendingTimeouts());
    }

    [Fact]
    public void DrainAdvancesVirtualTimeToResolveFiniteTimeouts()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;
        scheduler.Schedule("op", () =>
        {
            outcome = scheduler.WaitOnResource(resource, TimeSpan.FromMilliseconds(250), Reason("m"));
        });

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationWaitOutcome.TimedOut, outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(250), scheduler.VirtualTime);
    }

    [Fact]
    public void SignalBeatsTimeoutWhenDeliveredBeforeTimeAdvances()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;

        scheduler.Schedule("waiter", () =>
        {
            outcome = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(1), Reason("m"));
        });
        scheduler.Schedule("signaler", () => scheduler.SignalOne(resource));

        scheduler.Drain(TestContext.Current.CancellationToken);

        // The signal ran while time was still 0; the same-instant timeout never fired.
        Assert.Equal(SimulationWaitOutcome.Signaled, outcome);
        Assert.Equal(TimeSpan.Zero, scheduler.VirtualTime);
        Assert.Empty(scheduler.CapturePendingTimeouts());
    }

    [Fact]
    public void InfiniteTimeoutRegistersNoDeadlineAndOnlyASignalWakesIt()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;
        var op = scheduler.Schedule("op", () =>
        {
            outcome = scheduler.WaitOnResource(resource, Timeout.InfiniteTimeSpan, Reason("m"));
        });

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Paused, op.State);
        Assert.Empty(scheduler.CapturePendingTimeouts());

        // No timeout is pending, so advancing time does nothing and the op stays parked.
        Assert.False(scheduler.TryAdvanceVirtualTime());
        Assert.Equal(SimulationOperationState.Paused, op.State);

        Assert.NotNull(scheduler.SignalOne(resource));
        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationWaitOutcome.Signaled, outcome);
    }

    [Fact]
    public void CoincidentTimeoutsFireTogetherInDeterministicRegistrationOrder()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        var order = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var name = $"w{i}";
            scheduler.Schedule(name, () =>
            {
                var outcome = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(2), Reason("sem"));
                Assert.Equal(SimulationWaitOutcome.TimedOut, outcome);
                order.Add(name);
            });
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, scheduler.CapturePendingTimeouts().Count);

        // A single advance fires all three coincident deadlines.
        Assert.True(scheduler.TryAdvanceVirtualTime());
        Assert.Equal(TimeSpan.FromSeconds(2), scheduler.VirtualTime);
        Assert.Empty(scheduler.CapturePendingTimeouts());

        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(["w1", "w2", "w3"], order);
    }

    [Fact]
    public void EarlierDeadlineFiresBeforeLaterDeadline()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        var wokeAt = new List<(string Name, TimeSpan At)>();

        scheduler.Schedule("late", () =>
        {
            scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(10), Reason("sem"));
            wokeAt.Add(("late", scheduler.VirtualTime));
        });
        scheduler.Schedule("early", () =>
        {
            scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(3), Reason("sem"));
            wokeAt.Add(("early", scheduler.VirtualTime));
        });

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(2, wokeAt.Count);
        Assert.Equal("early", wokeAt[0].Name);
        Assert.Equal(TimeSpan.FromSeconds(3), wokeAt[0].At);
        Assert.Equal("late", wokeAt[1].Name);
        Assert.Equal(TimeSpan.FromSeconds(10), wokeAt[1].At);
    }

    [Fact]
    public void SignalingOneWaiterLeavesTheOtherToTimeOut()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        var results = new Dictionary<string, SimulationWaitOutcome>();

        scheduler.Schedule("a", () => results["a"] = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(4), Reason("sem")));
        scheduler.Schedule("b", () => results["b"] = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(4), Reason("sem")));

        // Park both.
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.False(scheduler.RunStep(TestContext.Current.CancellationToken));

        // Signal exactly one (FIFO -> "a"), then let time fire the other's deadline.
        Assert.NotNull(scheduler.SignalOne(resource));
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationWaitOutcome.Signaled, results["a"]);
        Assert.Equal(SimulationWaitOutcome.TimedOut, results["b"]);
        Assert.Equal(TimeSpan.FromSeconds(4), scheduler.VirtualTime);
    }

    [Fact]
    public void NegativeTimeoutOtherThanInfiniteThrows()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Custom, "r");
        ArgumentOutOfRangeException? caught = null;
        scheduler.Schedule("op", () =>
        {
            try
            {
                scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(-2), Reason("r"));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                caught = ex;
            }
        });

        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.NotNull(caught);
    }

    [Fact]
    public void AdvancingVirtualTimeWhileOperationsAreRunnableIsANoOp()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        scheduler.Schedule("waiter", () => scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(5), Reason("m")));
        scheduler.Schedule("other", () => { });

        // Park the waiter; "other" is still runnable.
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.False(scheduler.TryAdvanceVirtualTime());
        Assert.Equal(TimeSpan.Zero, scheduler.VirtualTime);
    }

    [Fact]
    public void SchedulerDeadlineDueTimeSaturatesAtTimeSpanMaxValue()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Timer, "timer");
        var outcomes = new List<SimulationWaitOutcome>();

        scheduler.Schedule(
            "to-max",
            () => outcomes.Add(scheduler.WaitOnResource(resource, TimeSpan.MaxValue, Reason("max"))));
        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.MaxValue, scheduler.VirtualTime);

        scheduler.Schedule(
            "past-max",
            () => outcomes.Add(scheduler.WaitOnResource(resource, TimeSpan.FromTicks(1), Reason("saturated"))));
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal([SimulationWaitOutcome.TimedOut, SimulationWaitOutcome.TimedOut], outcomes);
        Assert.Equal(TimeSpan.MaxValue, scheduler.VirtualTime);
        Assert.Empty(scheduler.CapturePendingTimeouts());
    }
}
