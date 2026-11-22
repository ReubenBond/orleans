namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationNodeDiscardTests
{
    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    public async Task DiscardPendingWorkCancelsDelayedCallbackWithoutAdvancingTimeAndAllowsDisposal(
        int delayMinutes)
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode node = cluster.AddNode("node-1");
        var callbackRan = false;
        node.Context.SchedulerLane.EnqueueAfter(
            () => callbackRan = true,
            TimeSpan.FromMinutes(delayMinutes));
        DateTimeOffset utcNow = node.Context.TimeProvider.GetUtcNow();

        node.Context.DiscardPendingWork();
        Assert.Equal(utcNow, node.Context.TimeProvider.GetUtcNow());
        await cluster.DisposeAsync();

        Assert.False(callbackRan);
        Assert.Equal(utcNow, cluster.Scheduler.UtcNow);
        Assert.Equal(SimulationNodeState.Discarded, node.Context.State);
    }

    [Fact]
    public async Task DiscardPendingWorkCancelsReadyDelayedAndTimeProviderCallbacks()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode node = cluster.AddNode("node-1");
        var readyRan = false;
        var delayedRan = false;
        var timerRan = false;
        node.Context.SchedulerLane.Enqueue(() => readyRan = true);
        node.Context.SchedulerLane.EnqueueAfter(
            () => delayedRan = true,
            TimeSpan.FromMinutes(1));
        using ITimer timer = node.Context.TimeProvider.CreateTimer(
            _ => timerRan = true,
            null,
            TimeSpan.FromMinutes(2),
            Timeout.InfiniteTimeSpan);

        node.Context.DiscardPendingWork();
        cluster.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMinutes(3));
        SimulationExecutionResult result =
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationNodeState.Discarded, node.Context.State);
        Assert.False(readyRan);
        Assert.False(delayedRan);
        Assert.False(timerRan);
        Assert.False(node.Context.HasReadyTasks);
        Assert.Null(node.Context.NextWaitingDueTime);
        Assert.False(node.Context.HasPendingAttachmentWork);
        Assert.Empty(node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Equal(0, cluster.Scheduler.PendingTimerRegistrationCount);
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(0, result.PendingWork.PendingCount);
    }

    [Fact]
    public async Task DiscardPendingWorkCancelsSuspendForSharedResumeAndStaysDiscarded()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode node = cluster.AddNode("node-1");
        node.SuspendFor(TimeSpan.FromMinutes(1));

        node.Context.DiscardPendingWork();
        cluster.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMinutes(2));
        SimulationExecutionResult result =
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationNodeState.Discarded, node.Context.State);
        Assert.False(node.Context.IsSuspended);
        Assert.Empty(cluster.SchedulerLane.CaptureScheduledItems());
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(0, result.PendingWork.PendingCount);
    }

    [Fact]
    public async Task DiscardPendingWorkWhileSuspendedRemovesBlockedWorkAndPendingDiagnostics()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode node = cluster.AddNode("node-1");
        var callbackRan = false;
        node.Suspend();
        node.Context.SchedulerLane.Enqueue(() => callbackRan = true);

        SimulationExecutionResult before =
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationExecutionReason.IdleWithPendingWork, before.Reason);
        Assert.Equal(1, before.PendingWork.BlockedCount);

        node.Context.DiscardPendingWork();
        SimulationExecutionResult after =
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationNodeState.Discarded, node.Context.State);
        Assert.False(node.Context.IsSuspended);
        Assert.False(callbackRan);
        Assert.Equal(SimulationExecutionReason.Idle, after.Reason);
        Assert.Equal(0, after.PendingWork.PendingCount);
        Assert.DoesNotContain(
            after.PendingWork.Items,
            static item => item.QueueIdentity == "node-1");
    }

    [Fact]
    public async Task DiscardPendingWorkIsIdempotentAndRejectsEveryNodeEnqueueSurface()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode node = cluster.AddNode("node-1");
        SimulationNodeContext context = node.Context;

        context.DiscardPendingWork();
        context.DiscardPendingWork();

        Assert.Equal(SimulationNodeState.Discarded, context.State);
        Assert.Throws<ObjectDisposedException>(() => context.SchedulerLane.Enqueue(static () => { }));
        Assert.Throws<ObjectDisposedException>(
            () => context.SchedulerLane.EnqueueAfter(static () => { }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(
            () => context.SynchronizationContext.Post(static _ => { }, null));
        Assert.Throws<ObjectDisposedException>(
            () => context.SynchronizationContext.Send(static _ => { }, null));
        Assert.Throws<ObjectDisposedException>(
            () => context.TimeProvider.CreateTimer(
                static _ => { },
                null,
                TimeSpan.Zero,
                Timeout.InfiniteTimeSpan));

        var task = new Task(static () => { });
        TaskSchedulerException taskException =
            Assert.Throws<TaskSchedulerException>(() => task.Start(context.TaskScheduler));
        Assert.IsType<ObjectDisposedException>(taskException.InnerException);

        Assert.Throws<ObjectDisposedException>(context.Suspend);
        Assert.Throws<ObjectDisposedException>(context.Resume);
        Assert.Throws<ObjectDisposedException>(
            () => context.SuspendFor(TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(
            () => context.Step(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(
            () => context.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.Empty(context.SchedulerLane.CaptureScheduledItems());
        Assert.Equal(0, context.Scheduler.PendingOperationCount);
        Assert.Equal(0, context.Scheduler.PendingTimerRegistrationCount);
    }

    [Fact]
    public async Task DiscardPendingWorkDoesNotAffectOtherNodesOrClusterGlobalWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode discarded = cluster.AddNode("discarded");
        SimulationNode survivor = cluster.AddNode("survivor");
        var discardedCount = 0;
        var survivorCount = 0;
        var clusterCount = 0;
        for (var index = 0; index < 3; index++)
        {
            discarded.Context.SchedulerLane.Enqueue(() => discardedCount++);
            survivor.Context.SchedulerLane.Enqueue(() => survivorCount++);
            cluster.SchedulerLane.Enqueue(() => clusterCount++);
        }

        discarded.Context.DiscardPendingWork();
        SimulationExecutionResult result =
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(0, discardedCount);
        Assert.Equal(3, survivorCount);
        Assert.Equal(3, clusterCount);
        Assert.Equal(SimulationNodeState.Discarded, discarded.Context.State);
        Assert.Equal(SimulationNodeState.Running, survivor.Context.State);
        Assert.DoesNotContain(discarded, cluster.ActiveNodes);
        Assert.Contains(survivor, cluster.ActiveNodes);
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(0, result.PendingWork.PendingCount);
    }

    [Fact]
    public async Task DiscardPendingWorkAfterClusterDisposalThrowsObjectDisposedException()
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNodeContext context = cluster.AddNode("node-1").Context;
        await cluster.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(context.DiscardPendingWork);
    }

    [Fact]
    public async Task DiscardPendingWorkFromActiveCallbackIsRejectedWithoutPartialDiscard()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode node = cluster.AddNode("node-1");
        Exception? rejection = null;
        SimulationNodeState? stateDuringCallback = null;
        var secondCallbackRan = false;
        node.Context.SchedulerLane.Enqueue(() =>
        {
            rejection = Record.Exception(node.Context.DiscardPendingWork);
            stateDuringCallback = node.Context.State;
        });
        node.Context.SchedulerLane.Enqueue(() => secondCallbackRan = true);

        SimulationExecutionResult result =
            cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(rejection);
        Assert.Contains("while one of its callbacks is running", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SimulationNodeState.Running, stateDuringCallback);
        Assert.Equal(SimulationNodeState.Running, node.Context.State);
        Assert.True(secondCallbackRan);
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(0, result.PendingWork.PendingCount);
    }
}
