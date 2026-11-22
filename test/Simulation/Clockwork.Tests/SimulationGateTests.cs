namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers <see cref="SimulationGate"/>: level-triggered open/close semantics, deterministic FIFO
/// release order dispatched through the queue (never inline), reusability across multiple
/// open/close cycles, and cancellation (both already-canceled and racing with release).
/// </summary>
public sealed class SimulationGateTests
{
    [Fact]
    public void WaitAsyncOnAnAlreadyOpenGateCompletesSynchronouslyWithoutTouchingTheQueue()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue, isOpen: true);

        var task = gate.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void WaitAsyncOnAClosedGateDoesNotCompleteUntilOpenIsCalledAndTheQueueIsDrained()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);

        var task = gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        gate.Open();
        Assert.False(task.IsCompleted); // Release is scheduled, not applied inline.

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void OpenReleasesMultipleWaitersInFifoOrderThroughTheQueue()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);

        var first = gate.WaitAsync(TestContext.Current.CancellationToken);
        var second = gate.WaitAsync(TestContext.Current.CancellationToken);
        var third = gate.WaitAsync(TestContext.Current.CancellationToken);

        gate.Open();

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
    public void CloseThenOpenAgainReleasesANewRoundOfWaiters()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);

        gate.Open(); // No waiters registered yet - nothing to release.
        Assert.Equal(0, queue.RunUntilIdle(TestContext.Current.CancellationToken));

        gate.Close();
        Assert.False(gate.IsOpen);

        var task = gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        gate.Open();
        Assert.Equal(1, queue.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void OpeningAnAlreadyOpenGateIsIdempotentAndDoesNotThrow()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue, isOpen: true);

        gate.Open();
        gate.Open();

        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void WaitAsyncWithAnAlreadyCanceledTokenReturnsACanceledTaskWithoutRegisteringAWaiter()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();

        var task = gate.WaitAsync(cts.Token);

        Assert.True(task.IsCanceled);

        gate.Open();
        Assert.False(queue.HasItems); // The canceled call never registered a waiter to release.
    }

    [Fact]
    public void OpeningBeforeCancellationClaimsTheWaiterEvenBeforeTheQueueDrains()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var task = gate.WaitAsync(cts.Token);

        gate.Open(); // Enqueues the completion, but does not run it yet.
        cts.Cancel();

        Assert.False(task.IsCompleted);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void CancellingAfterReleaseHasAlreadyRunIsANoOp()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var task = gate.WaitAsync(cts.Token);
        gate.Open();
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);

        cts.Cancel();

        Assert.True(task.IsCompletedSuccessfully); // Still successfully completed, not canceled.
    }

    [Fact]
    public async Task CrossThreadCancellationRacingOpenHasExactlyOneWinnerAndPreservesTheWaiterList()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var queue = CreateQueue();
            var gate = new SimulationGate(queue);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var start = new ManualResetEventSlim();

            var racingTask = gate.WaitAsync(cts.Token);
            var survivor = gate.WaitAsync(TestContext.Current.CancellationToken);
            var cancellation = Task.Run(
                () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    cts.Cancel();
                },
                TestContext.Current.CancellationToken);

            start.Set();
            gate.Open();
            await cancellation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            queue.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(racingTask.IsCanceled || racingTask.IsCompletedSuccessfully);
            Assert.True(survivor.IsCompletedSuccessfully);
            Assert.False(queue.HasItems);
        }
    }

    [Fact]
    public void CancelingAMiddleWaiterLeavesSurvivorsInFifoOrder()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var first = gate.WaitAsync(TestContext.Current.CancellationToken);
        var canceled = gate.WaitAsync(cts.Token);
        var third = gate.WaitAsync(TestContext.Current.CancellationToken);

        cts.Cancel();
        gate.Open();

        Assert.True(canceled.IsCanceled);
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(third.IsCompleted);
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(third.IsCompletedSuccessfully);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void ReleasingAWaiterDisposesItsCancellationRegistration()
    {
        var queue = CreateQueue();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var gateReference = CreateReleasedGateReference(queue, cts.Token);

        AssertEventuallyCollected(gateReference);
        GC.KeepAlive(cts);
    }

    [Fact]
    public async Task ReleaseDoesNotDeadlockWhileTheTokenIsRunningAnotherCancellationCallback()
    {
        var queue = CreateQueue();
        var gate = new SimulationGate(queue);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCancellationToContinue = new ManualResetEventSlim();

        var task = gate.WaitAsync(cts.Token);
        using var blockingRegistration = cts.Token.Register(
            () =>
            {
                callbackEntered.Set();
                allowCancellationToContinue.Wait(TestContext.Current.CancellationToken);
            });

        var cancellation = Task.Run(cts.Cancel, TestContext.Current.CancellationToken);
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var release = Task.Run(gate.Open, TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(
            release,
            Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        allowCancellationToContinue.Set();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Same(release, completed);
        await release;

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void NameIsExposedForDiagnosticsAndDefaultsToNull()
    {
        var queue = CreateQueue();
        Assert.Null(new SimulationGate(queue).Name);
        Assert.Equal("startup", new SimulationGate(queue, name: "startup").Name);
    }

    private static SimulationSchedulerLane CreateQueue() => SimulationTestHarness.NewLane();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedGateReference(SimulationSchedulerLane queue, CancellationToken cancellationToken)
    {
        var gate = new SimulationGate(queue);
        var task = gate.WaitAsync(cancellationToken);
        var reference = new WeakReference(gate);

        gate.Open();
        Assert.Equal(1, queue.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
        return reference;
    }

    private static void AssertEventuallyCollected(WeakReference reference)
    {
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive);
    }
}
