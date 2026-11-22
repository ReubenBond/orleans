namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers <see cref="SimulationLatch"/>: one-shot countdown semantics, deterministic FIFO release
/// order dispatched through the queue, over/double-signal guards, and cancellation.
/// </summary>
public sealed class SimulationLatchTests
{
    [Fact]
    public void WaitAsyncOnAnAlreadySignaledLatchCompletesSynchronouslyWithoutTouchingTheQueue()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 0);

        Assert.True(latch.IsSignaled);
        var task = latch.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void SignalDecrementsRemainingCountAndReleasesWaitersOnlyWhenItReachesZero()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 3);
        var task = latch.WaitAsync(TestContext.Current.CancellationToken);

        latch.Signal();
        Assert.Equal(2, latch.RemainingCount);
        Assert.False(latch.IsSignaled);
        Assert.False(task.IsCompleted);

        latch.Signal();
        Assert.Equal(1, latch.RemainingCount);
        Assert.False(task.IsCompleted);

        latch.Signal();
        Assert.Equal(0, latch.RemainingCount);
        Assert.True(latch.IsSignaled);
        Assert.False(task.IsCompleted); // Release is scheduled, not applied inline.

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void SignalWithACountAppliesMultipleDecrementsAtOnce()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 5);

        latch.Signal(5);

        Assert.True(latch.IsSignaled);
    }

    [Fact]
    public void ReleasesWaitersInFifoOrderThroughTheQueue()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 1);

        var first = latch.WaitAsync(TestContext.Current.CancellationToken);
        var second = latch.WaitAsync(TestContext.Current.CancellationToken);
        var third = latch.WaitAsync(TestContext.Current.CancellationToken);

        latch.Signal();

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(second.IsCompletedSuccessfully);
        Assert.False(third.IsCompleted);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(third.IsCompletedSuccessfully);
    }

    [Fact]
    public void SignalingAnAlreadySignaledLatchThrows()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 1);

        latch.Signal();

        Assert.Throws<InvalidOperationException>(() => latch.Signal());
    }

    [Fact]
    public void SignalingWithACountThatExceedsRemainingCountThrowsAndDoesNotPartiallyApply()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 2);

        Assert.Throws<InvalidOperationException>(() => latch.Signal(3));
        Assert.Equal(2, latch.RemainingCount); // Rejected atomically - no partial decrement.
    }

    [Fact]
    public void SignalWithANonPositiveCountThrows()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => latch.Signal(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => latch.Signal(-1));
    }

    [Fact]
    public void ConstructorRejectsANegativeInitialCount()
    {
        var queue = CreateQueue();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationLatch(queue, initialCount: -1));
    }

    [Fact]
    public void WaitAsyncWithAnAlreadyCanceledTokenReturnsACanceledTaskWithoutRegisteringAWaiter()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();

        var task = latch.WaitAsync(cts.Token);

        Assert.True(task.IsCanceled);

        latch.Signal();
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void CancellingBeforeSignalRemovesTheWaiterSoItIsNotReleased()
    {
        var queue = CreateQueue();
        var latch = new SimulationLatch(queue, initialCount: 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var canceledTask = latch.WaitAsync(cts.Token);
        var otherTask = latch.WaitAsync(TestContext.Current.CancellationToken);

        cts.Cancel();
        Assert.True(canceledTask.IsCanceled);

        latch.Signal();
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(otherTask.IsCompletedSuccessfully);
        Assert.False(queue.HasItems); // Only the surviving waiter was enqueued for release.
    }

    [Fact]
    public async Task CrossThreadCancellationRacingSignalDoesNotLoseOrDuplicateAWaiter()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var queue = CreateQueue();
            var latch = new SimulationLatch(queue, initialCount: 1);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var start = new ManualResetEventSlim();

            var racingTask = latch.WaitAsync(cts.Token);
            var survivor = latch.WaitAsync(TestContext.Current.CancellationToken);
            var cancellation = Task.Run(
                () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    cts.Cancel();
                },
                TestContext.Current.CancellationToken);

            start.Set();
            latch.Signal();
            await cancellation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            queue.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(racingTask.IsCanceled || racingTask.IsCompletedSuccessfully);
            Assert.True(survivor.IsCompletedSuccessfully);
            Assert.False(queue.HasItems);
        }
    }

    private static SimulationSchedulerLane CreateQueue() => SimulationTestHarness.NewLane();
}
