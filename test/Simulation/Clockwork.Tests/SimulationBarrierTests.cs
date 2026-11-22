namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers <see cref="SimulationBarrier"/>: cyclic N-participant rendezvous semantics, deterministic
/// FIFO release order dispatched through the queue, barrier reset/reuse across rounds, and
/// cancellation retracting an arrival instead of silently counting toward release.
/// </summary>
public sealed class SimulationBarrierTests
{
    [Fact]
    public void ArrivedCountIncrementsPerArrivalAndNoOneIsReleasedBeforeAllParticipantsArrive()
    {
        var queue = CreateQueue();
        var barrier = new SimulationBarrier(queue, participantCount: 3);

        var first = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, barrier.ArrivedCount);
        Assert.False(first.IsCompleted);

        var second = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, barrier.ArrivedCount);
        Assert.False(second.IsCompleted);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void TheFinalArrivalReleasesEveryParticipantOfThatRoundInFifoOrderThroughTheQueue()
    {
        var queue = CreateQueue();
        var barrier = new SimulationBarrier(queue, participantCount: 3);

        var first = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        var second = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        var third = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, barrier.ArrivedCount); // Reset immediately upon release.
        Assert.False(first.IsCompleted); // Release is scheduled, not applied inline.

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(second.IsCompletedSuccessfully);
        Assert.False(third.IsCompleted);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(third.IsCompletedSuccessfully);
    }

    [Fact]
    public void TheBarrierResetsAndCanBeReusedForASecondRound()
    {
        var queue = CreateQueue();
        var barrier = new SimulationBarrier(queue, participantCount: 2);

        _ = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        _ = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, queue.RunUntilIdle(TestContext.Current.CancellationToken));

        var thirdRoundFirst = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, barrier.ArrivedCount);
        Assert.False(thirdRoundFirst.IsCompleted);

        var thirdRoundSecond = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, queue.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.True(thirdRoundFirst.IsCompletedSuccessfully);
        Assert.True(thirdRoundSecond.IsCompletedSuccessfully);
    }

    [Fact]
    public void CancellingAWaitingParticipantRetractsItsArrivalSoItDoesNotCountTowardRelease()
    {
        var queue = CreateQueue();
        var barrier = new SimulationBarrier(queue, participantCount: 3);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var first = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        var canceled = barrier.ArriveAndWaitAsync(cts.Token);
        Assert.Equal(2, barrier.ArrivedCount);

        cts.Cancel();
        Assert.True(canceled.IsCanceled);
        Assert.Equal(1, barrier.ArrivedCount); // Retracted - back down to just "first".

        // Two more arrivals are now needed (not one) to complete the round.
        var third = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, barrier.ArrivedCount);
        Assert.False(first.IsCompleted);

        var fourth = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, barrier.ArrivedCount);

        Assert.Equal(3, queue.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.True(fourth.IsCompletedSuccessfully);
    }

    [Fact]
    public void ArriveAndWaitAsyncWithAnAlreadyCanceledTokenReturnsACanceledTaskWithoutArriving()
    {
        var queue = CreateQueue();
        var barrier = new SimulationBarrier(queue, participantCount: 2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();

        var task = barrier.ArriveAndWaitAsync(cts.Token);

        Assert.True(task.IsCanceled);
        Assert.Equal(0, barrier.ArrivedCount);
    }

    [Fact]
    public void ConstructorRejectsANonPositiveParticipantCount()
    {
        var queue = CreateQueue();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationBarrier(queue, participantCount: 0));
    }

    [Fact]
    public void ASingleParticipantBarrierReleasesImmediatelyOnEachArrival()
    {
        var queue = CreateQueue();
        var barrier = new SimulationBarrier(queue, participantCount: 1);

        var task = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CrossThreadCancellationRacingFinalArrivalRollsBackOrReleasesTheWholeRound()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var queue = CreateQueue();
            var barrier = new SimulationBarrier(queue, participantCount: 2);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var start = new ManualResetEventSlim();

            var first = barrier.ArriveAndWaitAsync(cts.Token);
            var cancellation = Task.Run(
                () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    cts.Cancel();
                },
                TestContext.Current.CancellationToken);

            start.Set();
            var second = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
            await cancellation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            queue.RunUntilIdle(TestContext.Current.CancellationToken);

            if (first.IsCanceled)
            {
                Assert.Equal(1, barrier.ArrivedCount);
                Assert.False(second.IsCompleted);

                var replacement = barrier.ArriveAndWaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, queue.RunUntilIdle(TestContext.Current.CancellationToken));
                Assert.True(second.IsCompletedSuccessfully);
                Assert.True(replacement.IsCompletedSuccessfully);
            }
            else
            {
                Assert.Equal(0, barrier.ArrivedCount);
                Assert.True(first.IsCompletedSuccessfully);
                Assert.True(second.IsCompletedSuccessfully);
            }

            Assert.False(queue.HasItems);
        }
    }

    [Fact]
    public void CancelingAWaiterDisposesItsCancellationRegistration()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var barrierReference = CreateCanceledBarrierReference(cts);

        AssertEventuallyCollected(barrierReference);
        GC.KeepAlive(cts);
    }

    private static SimulationSchedulerLane CreateQueue() => SimulationTestHarness.NewLane();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateCanceledBarrierReference(CancellationTokenSource cts)
    {
        var barrier = new SimulationBarrier(CreateQueue(), participantCount: 2);
        var task = barrier.ArriveAndWaitAsync(cts.Token);
        var reference = new WeakReference(barrier);

        cts.Cancel();
        Assert.True(task.IsCanceled);
        Assert.Equal(0, barrier.ArrivedCount);
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
