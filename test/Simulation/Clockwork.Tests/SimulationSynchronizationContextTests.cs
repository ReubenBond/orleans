namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationSynchronizationContextTests
{
    [Fact]
    public void InstallSetsCurrentToTheSimulationContextAndDisposeRestoresThePrevious()
    {
        var queue = CreateQueue();
        var previous = new SynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(previous);

        try
        {
            using (queue.SynchronizationContext.Install())
            {
                Assert.Same(queue.SynchronizationContext, SynchronizationContext.Current);
            }

            Assert.Same(previous, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    [Fact]
    public void InstallIsANoOpWhenTheSameContextIsAlreadyCurrent()
    {
        var queue = CreateQueue();

        try
        {
            using var outer = queue.SynchronizationContext.Install();
            var beforeNested = SynchronizationContext.Current;

            using (queue.SynchronizationContext.Install())
            {
                // Nested install for the same underlying queue must not replace the context,
                // since disposing an "already installed" scope must not undo the outer install.
                Assert.Same(beforeNested, SynchronizationContext.Current);
            }

            Assert.Same(beforeNested, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    [Fact]
    public void CreateCopySharesTheSameUnderlyingSchedulerAsTheOriginal()
    {
        var queue = CreateQueue();
        var copy = (SimulationSynchronizationContext)queue.SynchronizationContext.CreateCopy();

        Assert.True(queue.SynchronizationContext.IsSameScheduler(copy));
        Assert.True(copy.IsSameScheduler(queue.SynchronizationContext));
    }

    [Fact]
    public void ContextsBackedByDifferentQueuesAreNotTheSameScheduler()
    {
        var first = CreateQueue();
        var second = CreateQueue();

        Assert.False(first.SynchronizationContext.IsSameScheduler(second.SynchronizationContext));
    }

    [Fact]
    public void SendOnAFreeQueueFromAForeignContextSchedulesAndSynchronouslyPumpsUntilExecuted()
    {
        var queue = CreateQueue();
        var executed = false;

        queue.SynchronizationContext.Send(_ => executed = true, null);

        // Send only returns once the callback has actually run - no queue draining required.
        Assert.True(executed);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void SendPreservesFifoOrderRelativeToAlreadyPendingWork()
    {
        var queue = CreateQueue();
        var order = new List<string>();

        queue.SynchronizationContext.Post(_ => order.Add("posted-1"), null);
        queue.SynchronizationContext.Post(_ => order.Add("posted-2"), null);

        queue.SynchronizationContext.Send(_ => order.Add("sent"), null);

        // The already-pending posts have smaller sequence numbers, so pumping must run them
        // first, in registration order, before reaching the synchronously-sent callback.
        Assert.Equal(["posted-1", "posted-2", "sent"], order);
    }

    [Fact]
    public void SendPropagatesExceptionsThrownByTheCallbackToTheCaller()
    {
        var queue = CreateQueue();
        var expected = new InvalidTimeZoneException("boom");
        var invocationCount = 0;

        var thrown = Assert.Throws<InvalidTimeZoneException>(
            () => queue.SynchronizationContext.Send(
                _ =>
                {
                    invocationCount++;
                    throw expected;
                },
                null));

        Assert.Same(expected, thrown);
        Assert.Equal(1, invocationCount);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void SendExecutesExactlyOnceAfterAnEarlierFailureAndThenRethrowsThatFailure()
    {
        var queue = CreateQueue();
        var order = new List<string>();
        var expected = new FormatException("earlier");
        var sentInvocationCount = 0;

        queue.SynchronizationContext.Post(
            _ =>
            {
                order.Add("posted-fault");
                throw expected;
            },
            null);
        queue.SynchronizationContext.Post(_ => order.Add("posted-after-fault"), null);

        var thrown = Assert.Throws<FormatException>(
            () => queue.SynchronizationContext.Send(
                _ =>
                {
                    sentInvocationCount++;
                    order.Add("sent");
                },
                null));

        Assert.Same(expected, thrown);
        Assert.Equal(["posted-fault", "posted-after-fault", "sent"], order);
        Assert.Equal(1, sentInvocationCount);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void SendAggregatesEarlierFailuresInExactFifoOrderAfterFulfillingTheSend()
    {
        var queue = CreateQueue();
        var order = new List<string>();
        var firstFailure = new FormatException("first");
        var secondFailure = new InvalidDataException("second");
        var sentInvocationCount = 0;

        queue.SynchronizationContext.Post(_ => order.Add("posted-1"), null);
        queue.SynchronizationContext.Post(
            _ =>
            {
                order.Add("posted-fault-1");
                throw firstFailure;
            },
            null);
        queue.SynchronizationContext.Post(_ => order.Add("posted-2"), null);
        queue.SynchronizationContext.Post(
            _ =>
            {
                order.Add("posted-fault-2");
                throw secondFailure;
            },
            null);

        var thrown = Assert.Throws<AggregateException>(
            () => queue.SynchronizationContext.Send(
                _ =>
                {
                    sentInvocationCount++;
                    order.Add("sent");
                },
                null));

        Assert.Equal(
            ["posted-1", "posted-fault-1", "posted-2", "posted-fault-2", "sent"],
            order);
        Assert.Equal([firstFailure, secondFailure], thrown.InnerExceptions);
        Assert.Equal(1, sentInvocationCount);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void SendAggregatesEarlierAndSentFailuresInDeterministicFifoOrder()
    {
        var queue = CreateQueue();
        var order = new List<string>();
        var earlierFailure = new FormatException("earlier");
        var sentFailure = new InvalidDataException("sent");
        var sentInvocationCount = 0;

        queue.SynchronizationContext.Post(
            _ =>
            {
                order.Add("posted-fault");
                throw earlierFailure;
            },
            null);
        queue.SynchronizationContext.Post(_ => order.Add("posted-after-fault"), null);

        var thrown = Assert.Throws<AggregateException>(
            () => queue.SynchronizationContext.Send(
                _ =>
                {
                    sentInvocationCount++;
                    order.Add("sent-fault");
                    throw sentFailure;
                },
                null));

        Assert.Equal(["posted-fault", "posted-after-fault", "sent-fault"], order);
        Assert.Equal([earlierFailure, sentFailure], thrown.InnerExceptions);
        Assert.Equal(1, sentInvocationCount);
        Assert.False(queue.HasItems);
    }

    [Fact]
    public void SendExecutesInlineWithoutTouchingTheQueueWhenAlreadyOnTheOwningSynchronizationContext()
    {
        var queue = CreateQueue();

        try
        {
            using (queue.SynchronizationContext.Install())
            {
                queue.SynchronizationContext.Post(_ => { }, null); // Something already pending.

                var executed = false;
                queue.SynchronizationContext.Send(_ => executed = true, null);

                Assert.True(executed);
                Assert.True(queue.HasItems); // The unrelated pending post was left untouched.
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    [Fact]
    public async Task SendExecutesInlineWhenTheCurrentTaskSchedulerIsTheOwningSimulationTaskScheduler()
    {
        var queue = CreateQueue();
        var scheduler = new SimulationTaskScheduler(queue);
        var executed = false;

        var task = Task.Factory.StartNew(
            () => queue.SynchronizationContext.Send(_ => executed = true, null),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.None,
            scheduler);

        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        await task;

        Assert.True(executed);
        Assert.False(queue.HasItems); // Ran inline - never touched the queue itself.
    }

    [Fact]
    public void SendFromAGenuinelyDifferentThreadWhileTheGuardIsHeldRejectsWithAPreciseDiagnostic()
    {
        var queue = CreateQueue();
        using var blockerEntered = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();

        queue.Enqueue(() =>
        {
            blockerEntered.Set();
            releaseBlocker.Wait(TestContext.Current.CancellationToken);
        });

        var backgroundThread = new Thread(() => queue.RunOnce(TestContext.Current.CancellationToken))
        {
            IsBackground = true,
        };
        backgroundThread.Start();

        try
        {
            blockerEntered.Wait(TestContext.Current.CancellationToken);

            var ex = Assert.Throws<InvalidOperationException>(() => queue.SynchronizationContext.Send(_ => { }, null));
            Assert.Contains("Send", ex.Message, StringComparison.Ordinal);
            Assert.Contains("concurrently", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            releaseBlocker.Set();
            backgroundThread.Join();
        }
    }

    [Fact]
    public async Task SendStopsWhenDetachmentCancelsItsScheduledCallback()
    {
        var queue = CreateQueue();
        SimulationSynchronizationContext context = queue.SynchronizationContext;
        var sentCallbackRan = false;
        queue.Enqueue(() =>
        {
            foreach (ScheduledItem item in queue.Detach())
            {
                item.Dispose();
            }
        });

        ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => Task.Run(
                    () => context.Send(_ => sentCallbackRan = true, null),
                    TestContext.Current.CancellationToken)
                .WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));

        Assert.Contains(
            nameof(SimulationSchedulerLane),
            exception.ObjectName,
            StringComparison.Ordinal);
        Assert.False(sentCallbackRan);
    }

    [Fact]
    public void PostQueuesTheCallbackInsteadOfRunningItImmediately()
    {
        var queue = CreateQueue();
        var executed = false;

        queue.SynchronizationContext.Post(_ => executed = true, null);

        Assert.False(executed);
        Assert.True(queue.RunOnce(TestContext.Current.CancellationToken));
        Assert.True(executed);
    }

    private static SimulationSchedulerLane CreateQueue() => SimulationTestHarness.NewLane();
}
