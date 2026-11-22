using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling.Resources;

/// <summary>
/// Coverage of synchronous cancellation-token integration for resource waits: cancellation is
/// observed on the cancelling operation's own thread (no thread-pool hop, no <c>CancelAsync</c>),
/// release/timeout/cancel races resolve deterministically to exactly one terminal reason, and
/// registrations never leak past the wait.
/// </summary>
public sealed class SimulationResourceCancellationTests
{
    private static SimulationPauseReason Reason(string tag) => SimulationPauseReason.ResourceWait(tag);

    [Fact]
    public void AlreadyCanceledTokenResolvesCanceledWithoutParking()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;
        var paused = false;

        var op = scheduler.Schedule("op", () =>
        {
            outcome = scheduler.WaitOnResource(resource, Timeout.InfiniteTimeSpan, Reason("m"), cts.Token);
            paused = true;
        });

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationWaitOutcome.Canceled, outcome);
        Assert.True(paused);
        Assert.Equal(SimulationOperationState.Completed, op.State);
        Assert.Equal(0, resource.WaiterCount);
    }

    [Fact]
    public void CanceledTokenTakesPrecedenceOverZeroTimeout()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        SimulationWaitOutcome? outcome = null;
        scheduler.Schedule("op", () => outcome = scheduler.WaitOnResource(resource, TimeSpan.Zero, Reason("sem"), cts.Token));

        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationWaitOutcome.Canceled, outcome);
    }

    [Fact]
    public void CancelingTokenWhileParkedWakesTheOperationWithCanceled()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;

        var waiter = scheduler.Schedule("waiter", () =>
            outcome = scheduler.WaitOnResource(resource, Timeout.InfiniteTimeSpan, Reason("m"), cts.Token));
        scheduler.Schedule("canceller", cts.Cancel);

        // Park the waiter.
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Paused, waiter.State);
        Assert.Equal(1, resource.WaiterCount);

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationWaitOutcome.Canceled, outcome);
        Assert.Equal(SimulationOperationState.Completed, waiter.State);
        Assert.Equal(0, resource.WaiterCount);
    }

    [Fact]
    public void CancelBeatsTimeoutWhenDeliveredBeforeTimeAdvances()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;

        scheduler.Schedule("waiter", () =>
            outcome = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(30), Reason("m"), cts.Token));
        scheduler.Schedule("canceller", cts.Cancel);

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationWaitOutcome.Canceled, outcome);
        // Cancellation happened during execution; virtual time never had to advance.
        Assert.Equal(TimeSpan.Zero, scheduler.VirtualTime);
        Assert.Empty(scheduler.CapturePendingTimeouts());
    }

    [Fact]
    public void SignalBeatsCancelWhenSignalIsDeliveredFirst()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;

        scheduler.Schedule("waiter", () =>
            outcome = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(30), Reason("m"), cts.Token));
        scheduler.Schedule("signaler", () => scheduler.SignalOne(resource));
        scheduler.Schedule("canceller", cts.Cancel);

        scheduler.Drain(TestContext.Current.CancellationToken);

        // Signaler (id 2) runs before canceller (id 3); the waiter is already resolved Signaled, so
        // the later cancellation is a no-op.
        Assert.Equal(SimulationWaitOutcome.Signaled, outcome);
    }

    [Fact]
    public void TimeoutBeatsCancelWhenCancellationNeverArrives()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;
        scheduler.Schedule("waiter", () =>
            outcome = scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(2), Reason("m"), cts.Token));

        // No one cancels; the deadline fires deterministically.
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationWaitOutcome.TimedOut, outcome);
        Assert.Equal(TimeSpan.FromSeconds(2), scheduler.VirtualTime);
    }

    [Fact]
    public void CancelingTokenAfterWaitCompletedIsHarmless()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationWaitOutcome? outcome = null;

        var waiter = scheduler.Schedule("waiter", () =>
            outcome = scheduler.WaitOnResource(resource, Timeout.InfiniteTimeSpan, Reason("m"), cts.Token));
        scheduler.Schedule("signaler", () => scheduler.SignalOne(resource));

        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationWaitOutcome.Signaled, outcome);
        Assert.Equal(SimulationOperationState.Completed, waiter.State);

        // The registration was disposed when the wait finished; canceling now must not fire a stale
        // callback or throw.
        cts.Cancel();
        Assert.Equal(SimulationOperationState.Completed, waiter.State);
    }

    [Fact]
    public void OnlyTheCanceledWaiterWakesWhenSharedTokenCancelsOneOfManyWaits()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        using var canceledCts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        var results = new Dictionary<string, SimulationWaitOutcome>();

        // "a" is cancelable; "b" waits indefinitely with no token.
        scheduler.Schedule("a", () =>
            results["a"] = scheduler.WaitOnResource(resource, Timeout.InfiniteTimeSpan, Reason("sem"), canceledCts.Token));
        scheduler.Schedule("b", () =>
            results["b"] = scheduler.WaitOnResource(resource, Reason("sem")));
        scheduler.Schedule("canceller", canceledCts.Cancel);

        // Park a and b.
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(2, resource.WaiterCount);

        // Cancel; only "a" is removed. "b" is still parked.
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(1, resource.WaiterCount);

        // Wake "a" so it can record its Canceled outcome, then signal the remaining waiter "b".
        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationWaitOutcome.Canceled, results["a"]);
        Assert.False(results.ContainsKey("b"));

        Assert.NotNull(scheduler.SignalOne(resource));
        scheduler.Drain(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationWaitOutcome.Signaled, results["b"]);
    }
}
