using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationNodeContextTests
{
    [Fact]
    public void SchedulerExposesTheLaneTimeAuthority()
    {
        var (scheduler, _, context) = CreateComponents();

        Assert.Same(scheduler, context.Scheduler);
        Assert.Same(scheduler, context.SchedulerLane.Scheduler);
    }

    [Fact]
    public void NewContextStartsInRunningState()
    {
        var context = CreateContext();

        Assert.Equal(SimulationNodeState.Running, context.State);
        Assert.False(context.IsSuspended);
    }

    [Fact]
    public void SuspendPreventsStepFromRunningReadyWork()
    {
        var context = CreateContext();
        var executed = false;
        context.SchedulerLane.Enqueue(() => executed = true);

        context.Suspend();

        Assert.True(context.IsSuspended);
        Assert.False(context.Step(TestContext.Current.CancellationToken));
        Assert.False(executed);
    }

    [Fact]
    public void ResumeAllowsPreviouslyQueuedReadyWorkToRun()
    {
        var context = CreateContext();
        var executed = false;
        context.SchedulerLane.Enqueue(() => executed = true);
        context.Suspend();

        context.Resume();

        Assert.False(context.IsSuspended);
        Assert.True(context.Step(TestContext.Current.CancellationToken));
        Assert.True(executed);
    }

    [Fact]
    public void RunUntilIdleReturnsZeroWhileSuspendedEvenWithReadyWork()
    {
        var context = CreateContext();
        context.SchedulerLane.Enqueue(() => { });
        context.SchedulerLane.Enqueue(() => { });
        context.Suspend();

        Assert.Equal(0, context.RunUntilIdle(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StepAndRunUntilIdleHonorCancellationWhileSuspended()
    {
        var context = CreateContext();
        context.Suspend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => context.Step(cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => context.RunUntilIdle(cancellation.Token));
    }

    [Fact]
    public void HasReadyTasksReflectsQueueStateAndSuspension()
    {
        var (scheduler, _, context) = CreateComponents();

        Assert.False(context.HasReadyTasks);

        context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(5));
        Assert.False(context.HasReadyTasks);

        Advance(scheduler, TimeSpan.FromSeconds(5));
        Assert.True(context.HasReadyTasks);

        context.Suspend();
        Assert.False(context.HasReadyTasks);
    }

    [Fact]
    public void SuspendForWithoutAnExternalQueueThrows()
    {
        var context = SimulationTestHarness.NewNodeComponents().Context;

        Assert.Throws<InvalidOperationException>(() => context.SuspendFor(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SuspendForAutomaticallyResumesAfterTheDurationElapses()
    {
        var externalQueue = SimulationTestHarness.NewLane();
        var (scheduler, _, context) = SimulationTestHarness.NewNodeComponents(externalLane: externalQueue);

        context.SuspendFor(TimeSpan.FromSeconds(5));
        Assert.True(context.IsSuspended);

        // The auto-resume is scheduled on the external queue, not the node's own queue.
        Advance(scheduler, TimeSpan.FromSeconds(5));
        Assert.True(context.IsSuspended);

        Assert.True(externalQueue.RunOnce(TestContext.Current.CancellationToken));
        Assert.False(context.IsSuspended);
    }

    [Fact]
    public void SuspendForWithNonPositiveDurationThrows()
    {
        var externalQueue = SimulationTestHarness.NewLane();
        var context = SimulationTestHarness.NewNodeComponents(externalLane: externalQueue).Context;

        Assert.Throws<ArgumentOutOfRangeException>(() => context.SuspendFor(TimeSpan.Zero));
    }

    [Fact]
    public void SuspendForMaximumDurationOverflowRestoresRunningStateAndLane()
    {
        var externalQueue = SimulationTestHarness.NewLane();
        var context = SimulationTestHarness.NewNodeComponents(externalLane: externalQueue).Context;
        var executed = false;
        context.SchedulerLane.Enqueue(() => executed = true);

        Assert.Throws<ArgumentOutOfRangeException>(() => context.SuspendFor(TimeSpan.MaxValue));

        Assert.Equal(SimulationNodeState.Running, context.State);
        Assert.False(context.IsSuspended);
        Assert.True(context.Step(TestContext.Current.CancellationToken));
        Assert.True(executed);
        Assert.False(context.HasPendingAttachmentWork);
    }

    [Fact]
    public void FailedSuspendForCleansTrackingAndContextCanBeReused()
    {
        var externalQueue = SimulationTestHarness.NewLane();
        var (scheduler, _, context) =
            SimulationTestHarness.NewNodeComponents(externalLane: externalQueue);

        Assert.Throws<ArgumentOutOfRangeException>(() => context.SuspendFor(TimeSpan.MaxValue));
        Assert.False(context.HasPendingAttachmentWork);

        context.SuspendFor(TimeSpan.FromSeconds(1));
        Advance(scheduler, TimeSpan.FromSeconds(1));
        Assert.True(externalQueue.RunOnce(TestContext.Current.CancellationToken));

        Assert.False(context.IsSuspended);
        Assert.False(context.HasPendingAttachmentWork);
        context.BeginAttachmentCleanup();
        context.CompleteAttachmentCleanup();
    }

    private static (SimulationScheduler Scheduler, SingleThreadedGuard Guard, SimulationNodeContext Context) CreateComponents()
    {
        return SimulationTestHarness.NewNodeComponents();
    }

    private static SimulationNodeContext CreateContext() => CreateComponents().Context;

    private static void Advance(SimulationScheduler scheduler, TimeSpan delta) =>
        scheduler.AdvanceVirtualTimeTo(scheduler.VirtualTime + delta);
}
