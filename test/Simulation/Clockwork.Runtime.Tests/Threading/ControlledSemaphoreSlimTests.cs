using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

// The controlled shims mirror the BCL surface, so most methods have a CancellationToken overload. These
// tests intentionally exercise the specific overloads (including token-less and explicit-token ones); the
// TestContext token guidance does not apply to the deterministic simulation loop.
#pragma warning disable xUnit1051

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledSemaphoreSlim"/> shims: the count and waiter set are
/// modelled on the cooperative logical thread, a contended synchronous <c>Wait</c> pumps the loop until
/// a permit is released, <c>WaitAsync</c> completes when a permit is released, max-count is enforced,
/// cancellation is honoured, and <c>AvailableWaitHandle</c> is bridged to a controlled manual-reset handle
/// that tracks count &gt; 0.
/// </summary>
public sealed class ControlledSemaphoreSlimTests
{
    [Fact]
    public void WaitDecrementsAndReleaseIncrements()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(2);

            Assert.Equal(2, ControlledSemaphoreSlim.CurrentCount(sem));
            ControlledSemaphoreSlim.Wait(sem);
            Assert.Equal(1, ControlledSemaphoreSlim.CurrentCount(sem));
            ControlledSemaphoreSlim.Wait(sem);
            Assert.Equal(0, ControlledSemaphoreSlim.CurrentCount(sem));

            var previous = ControlledSemaphoreSlim.Release(sem);
            Assert.Equal(0, previous);
            Assert.Equal(1, ControlledSemaphoreSlim.CurrentCount(sem));
        });
    }

    [Fact]
    public void WaitWithZeroTimeoutFailsWhenEmpty()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            Assert.False(ControlledSemaphoreSlim.Wait(sem, 0));
        });
    }

    [Fact]
    public void ReleaseBeyondMaxCountThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(1, 1);
            Assert.Throws<SemaphoreFullException>(() => ControlledSemaphoreSlim.Release(sem));
        });
    }

    [Fact]
    public void ReleaseInvalidCountThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlledSemaphoreSlim.Release(sem, 0));
        });
    }

    [Fact]
    public void ContendedWaitProceedsAfterRelease()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            var log = new List<int>();

            var consumer = ControlledThread.Create(() =>
            {
                ControlledSemaphoreSlim.Wait(sem);
                log.Add(2);
            });
            ControlledThread.Start(consumer);

            log.Add(1);
            ControlledSemaphoreSlim.Release(sem);
            ControlledThread.Join(consumer);
            log.Add(3);

            Assert.Equal("1,2,3", string.Join(",", log));
        });
    }

    [Fact]
    public void WaitAsyncCompletesWhenPermitReleased()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);

            // With no permit available the task is pending. A Release hands the permit to the waiter at once, and, as the
            // BCL runs a WaitAsync task's continuations asynchronously, a controlled operation of its own completes the
            // task, so no continuation runs inside Release.
            var task = ControlledSemaphoreSlim.WaitAsync(sem);
            Assert.False(task.IsCompleted);

            ControlledSemaphoreSlim.Release(sem);
            Assert.Equal(0, ControlledSemaphoreSlim.CurrentCount(sem));
            Assert.False(task.IsCompleted);

            Assert.Equal(1, coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken));
            Assert.True(task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void WaitAsyncAlreadyAvailableCompletesSynchronously()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(1);
            var task = ControlledSemaphoreSlim.WaitAsync(sem, 0);
            Assert.True(task.IsCompleted);
            Assert.True(task.Result);
            Assert.Equal(0, ControlledSemaphoreSlim.CurrentCount(sem));
        });
    }

    [Fact]
    public void WaitCancellationThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            using var cts = new CancellationTokenSource();
            Exception? caught = null;

            var waiter = ControlledThread.Create(() =>
            {
                try
                {
                    ControlledSemaphoreSlim.Wait(sem, cts.Token);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });
            ControlledThread.Start(waiter);

            var canceller = ControlledThread.Create(() => cts.Cancel());
            ControlledThread.Start(canceller);

            ControlledThread.Join(canceller);
            ControlledThread.Join(waiter);

            Assert.IsAssignableFrom<OperationCanceledException>(caught);
        });
    }

    [Fact]
    public void WaitAfterDisposeThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(1);
            ControlledSemaphoreSlim.Dispose(sem);
            Assert.Throws<ObjectDisposedException>(() => ControlledSemaphoreSlim.Wait(sem));
        });
    }

    [Fact]
    public void AvailableWaitHandleTracksCountTransitions()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(1);
            WaitHandle handle = ControlledSemaphoreSlim.AvailableWaitHandle(sem);

            // Signalled while a permit is available; observing it does not consume the permit.
            Assert.True(ControlledWaitHandle.WaitOne(handle, 0));
            Assert.Equal(1, ControlledSemaphoreSlim.CurrentCount(sem));

            // Draining the last permit clears the handle.
            ControlledSemaphoreSlim.Wait(sem);
            Assert.False(ControlledWaitHandle.WaitOne(handle, 0));

            // Releasing a permit re-signals the same cached handle.
            ControlledSemaphoreSlim.Release(sem);
            Assert.Same(handle, ControlledSemaphoreSlim.AvailableWaitHandle(sem));
            Assert.True(ControlledWaitHandle.WaitOne(handle, 0));
        });
    }

    [Fact]
    public void AvailableWaitHandleWakesAWaiterOnRelease()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            WaitHandle handle = ControlledSemaphoreSlim.AvailableWaitHandle(sem);
            var woke = false;

            var waiter = ControlledThread.Create(() => woke = ControlledWaitHandle.WaitOne(handle));
            var releaser = ControlledThread.Create(() => ControlledSemaphoreSlim.Release(sem));

            ControlledThread.Start(waiter);
            ControlledThread.Start(releaser);
            ControlledThread.Join(waiter);
            ControlledThread.Join(releaser);

            Assert.True(woke);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void AvailableWaitHandleRejectedAfterDispose()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(1);
            ControlledSemaphoreSlim.Dispose(sem);
            Assert.Throws<ObjectDisposedException>(() => ControlledSemaphoreSlim.AvailableWaitHandle(sem));
        });
    }

    [Fact]
    public void OutsideSimulationFailsBeforeCreatingSemaphore()
    {
        SemaphoreSlim? semaphore = null;

        Exception? exception = Record.Exception(
            () => semaphore = ControlledSemaphoreSlim.Create(1));

        Assert.Null(semaphore);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.SemaphoreSlim..ctor");
    }

    // ---- Finite virtual-time timeouts (SemaphoreSlim.Wait / WaitAsync) ----
    //
    // Modelled time only advances when nothing else can run, so a permit released, or a cancellation, at the
    // current virtual instant always wins over a timeout that needs a time advance. A "virtual delay" built
    // from a never-released finite Wait sequences an event at a chosen instant D for exact before/at/after
    // coverage, with no wall-clock time anywhere.

    private static void VirtualDelayThenRun(int delayMilliseconds, Action action)
    {
        var timer = ControlledSemaphoreSlim.Create(0);
        Assert.False(ControlledSemaphoreSlim.Wait(timer, delayMilliseconds));
        action();
    }

    [Fact]
    public void WaitFiniteTimesOutWhenNeverReleased()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);

            // The root strand itself pumps the loop; with no other work, modelled time advances to the
            // deadline and the wait returns false. No physical thread ever blocks and no real time passes.
            Assert.False(ControlledSemaphoreSlim.Wait(sem, 100));
            Assert.Equal(0, ControlledSemaphoreSlim.CurrentCount(sem));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteReleasedBeforeDeadlineSucceeds()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            var acquired = false;

            var consumer = ControlledThread.Create(() => acquired = ControlledSemaphoreSlim.Wait(sem, 500));
            var releaser = ControlledThread.Create(() =>
                VirtualDelayThenRun(100, () => ControlledSemaphoreSlim.Release(sem)));

            ControlledThread.Start(consumer);
            ControlledThread.Start(releaser);
            ControlledThread.Join(consumer);
            ControlledThread.Join(releaser);

            Assert.True(acquired); // Permit released at 100 (< 500) beat the timeout.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteReleasedAfterDeadlineTimesOut()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            var acquired = true;

            var consumer = ControlledThread.Create(() => acquired = ControlledSemaphoreSlim.Wait(sem, 100));
            var releaser = ControlledThread.Create(() =>
                VirtualDelayThenRun(500, () => ControlledSemaphoreSlim.Release(sem)));

            ControlledThread.Start(consumer);
            ControlledThread.Start(releaser);
            ControlledThread.Join(consumer);
            ControlledThread.Join(releaser);

            Assert.False(acquired); // Timed out at 100 before the release at 500.
            Assert.Equal(1, ControlledSemaphoreSlim.CurrentCount(sem)); // The late release just raised the count.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteCancelledBeforeDeadlineThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            using var cts = new CancellationTokenSource();
            Exception? caught = null;

            var waiter = ControlledThread.Create(() =>
            {
                try
                {
                    ControlledSemaphoreSlim.Wait(sem, 500, cts.Token);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });
            var canceller = ControlledThread.Create(() => VirtualDelayThenRun(100, cts.Cancel));

            ControlledThread.Start(waiter);
            ControlledThread.Start(canceller);
            ControlledThread.Join(waiter);
            ControlledThread.Join(canceller);

            // Cancellation at 100 (< 500) wins over the timeout and throws, exactly as the real semaphore.
            Assert.IsAssignableFrom<OperationCanceledException>(caught);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteTimesOutBeforeLateCancellation()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            using var cts = new CancellationTokenSource();
            var acquired = true;
            Exception? caught = null;

            var waiter = ControlledThread.Create(() =>
            {
                try
                {
                    acquired = ControlledSemaphoreSlim.Wait(sem, 100, cts.Token);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });
            var canceller = ControlledThread.Create(() => VirtualDelayThenRun(500, cts.Cancel));

            ControlledThread.Start(waiter);
            ControlledThread.Start(canceller);
            ControlledThread.Join(waiter);
            ControlledThread.Join(canceller);

            // Timeout at 100 wins over the cancellation at 500: returns false with no exception.
            Assert.Null(caught);
            Assert.False(acquired);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAsyncFiniteTimesOutWithFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            var task = ControlledSemaphoreSlim.WaitAsync(sem, 100);
            Assert.False(task.IsCompleted);

            // Drive the loop: with nothing else runnable, modelled time advances to the deadline, which
            // completes the task with false.
            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test-drive", TestContext.Current.CancellationToken);

            Assert.True(task.IsCompletedSuccessfully);
            Assert.False(task.Result);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAsyncFiniteReleasedBeforeDeadlineCompletesTrue()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            var task = ControlledSemaphoreSlim.WaitAsync(sem, 500);
            Assert.False(task.IsCompleted);

            // A release before the deadline completes the task with true and cancels the pending deadline.
            ControlledSemaphoreSlim.Release(sem);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(task.IsCompletedSuccessfully);
            Assert.True(task.Result);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAsyncFiniteCancelledCompletesCanceled()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            using var cts = new CancellationTokenSource();
            var task = ControlledSemaphoreSlim.WaitAsync(sem, 500, cts.Token);
            Assert.False(task.IsCompleted);

            cts.Cancel();
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(task.IsCanceled);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void CancellationDuringRegistrationCannotLeaveAStaleDeadline()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var sem = ControlledSemaphoreSlim.Create(0);
            using var cancellation = new CancellationTokenSource();
            try
            {
                // Force cancellation after the initial token check and waiter enqueue, immediately
                // before CancellationToken.Register. Register must invoke the callback synchronously.
                ControlledSemaphoreSlim.BeforeCancellationRegistrationForTesting = cancellation.Cancel;
                var task = ControlledSemaphoreSlim.WaitAsync(sem, 500, cancellation.Token);

                // The one step delivers the cancellation; no stale deadline remains to fire.
                Assert.Equal(1, coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken));
                Assert.True(task.IsCanceled);
                Assert.Null(coordinator.Scheduler.NextTimerDue);
                Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            }
            finally
            {
                ControlledSemaphoreSlim.BeforeCancellationRegistrationForTesting = null;
            }
        });
    }

    [Fact]
    public void ExternalCancellationRacingReleaseOrDisposePreservesOneWinnerWithoutLeaks()
    {
        // Release and Dispose are controlled APIs, so they run on the simulation's logical thread; cancellation is the
        // one completion which may arrive from another thread, where its token is cancelled.
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var coordinator = new SimulationSchedulerTestHost();
            bool releaseFirst = iteration % 2 == 0;
            TaskTestHarness.RunInSimulation(coordinator, () =>
            {
                var sem = ControlledSemaphoreSlim.Create(0);
                using var cancellation = new CancellationTokenSource();
                var waiter = ControlledSemaphoreSlim.WaitAsync(sem, 500, cancellation.Token);
                using var start = new Barrier(2);
                var testCancellation = TestContext.Current.CancellationToken;

                var canceler = Task.Run(
                    () =>
                    {
                        start.SignalAndWait(testCancellation);
                        cancellation.Cancel();
                    },
                    testCancellation);

                start.SignalAndWait(testCancellation);
                if (releaseFirst)
                {
                    ControlledSemaphoreSlim.Release(sem);
                    ControlledSemaphoreSlim.Dispose(sem);
                }
                else
                {
                    ControlledSemaphoreSlim.Dispose(sem);
                    Assert.Throws<ObjectDisposedException>(() => ControlledSemaphoreSlim.Release(sem));
                }

                canceler.WaitAsync(TimeSpan.FromSeconds(5), testCancellation).GetAwaiter().GetResult();
                coordinator.Scheduler.RunUntilIdle(testCancellation);

                Assert.True(waiter.IsCompleted);
                if (waiter.IsCompletedSuccessfully)
                {
                    Assert.True(releaseFirst);
                    Assert.True(waiter.Result);
                }
                else if (waiter.IsFaulted)
                {
                    Assert.False(releaseFirst);
                    Assert.IsType<ObjectDisposedException>(Assert.Single(waiter.Exception!.InnerExceptions));
                }
                else
                {
                    Assert.True(waiter.IsCanceled);
                }

                Assert.Null(coordinator.Scheduler.NextTimerDue);
                Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            });
        }
    }

    [Fact]
    public void RepeatedFiniteTimeoutsAreDeterministicAndLeakFree()
    {
        // Same-seed replay + no-leak stress: the finite-timeout outcome is identical every run and the loop
        // ends idle each time, so no waiter or deadline leaks across iterations.
        for (var seed = 1; seed <= 25; seed++)
        {
            var coordinator = new SimulationSchedulerTestHost(seed);
            var acquired = true;

            TaskTestHarness.RunInSimulation(
                coordinator,
                () =>
                {
                    var sem = ControlledSemaphoreSlim.Create(0);
                    acquired = ControlledSemaphoreSlim.Wait(sem, 50);
                });

            Assert.False(acquired);
            Assert.True(coordinator.Scheduler.IsIdle);
        }
    }

    private enum ReceiverOperation
    {
        CurrentCount,
        Wait,
        WaitCancellationToken,
        WaitMilliseconds,
        WaitMillisecondsCancellationToken,
        WaitTimeSpan,
        WaitTimeSpanCancellationToken,
        WaitAsync,
        WaitAsyncCancellationToken,
        WaitAsyncMilliseconds,
        WaitAsyncMillisecondsCancellationToken,
        WaitAsyncTimeSpan,
        WaitAsyncTimeSpanCancellationToken,
        Release,
        ReleaseCount,
        Dispose,
    }

    [Theory]
    [InlineData((int)ReceiverOperation.CurrentCount)]
    [InlineData((int)ReceiverOperation.Wait)]
    [InlineData((int)ReceiverOperation.WaitCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitMilliseconds)]
    [InlineData((int)ReceiverOperation.WaitMillisecondsCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitTimeSpan)]
    [InlineData((int)ReceiverOperation.WaitTimeSpanCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsync)]
    [InlineData((int)ReceiverOperation.WaitAsyncCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsyncMilliseconds)]
    [InlineData((int)ReceiverOperation.WaitAsyncMillisecondsCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsyncTimeSpan)]
    [InlineData((int)ReceiverOperation.WaitAsyncTimeSpanCancellationToken)]
    [InlineData((int)ReceiverOperation.Release)]
    [InlineData((int)ReceiverOperation.ReleaseCount)]
    [InlineData((int)ReceiverOperation.Dispose)]
    public void ExternallyCreatedSemaphoreIsRejectedByEveryReceivingShimInsideSimulation(
        int operationValue)
    {
        var operation = (ReceiverOperation)operationValue;
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            using var semaphore = new SemaphoreSlim(1, 1);

            var exception = Assert.Throws<SimulationApiException>(
                () => InvokeReceiverOperation(semaphore, operation));

            Assert.Equal(ExpectedApiName(operation), exception.ApiName);
            Assert.Contains(
                "not created through the controlled SemaphoreSlim surface",
                exception.Message,
                StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData((int)ReceiverOperation.CurrentCount)]
    [InlineData((int)ReceiverOperation.Wait)]
    [InlineData((int)ReceiverOperation.WaitCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitMilliseconds)]
    [InlineData((int)ReceiverOperation.WaitMillisecondsCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitTimeSpan)]
    [InlineData((int)ReceiverOperation.WaitTimeSpanCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsync)]
    [InlineData((int)ReceiverOperation.WaitAsyncCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsyncMilliseconds)]
    [InlineData((int)ReceiverOperation.WaitAsyncMillisecondsCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsyncTimeSpan)]
    [InlineData((int)ReceiverOperation.WaitAsyncTimeSpanCancellationToken)]
    [InlineData((int)ReceiverOperation.Release)]
    [InlineData((int)ReceiverOperation.ReleaseCount)]
    [InlineData((int)ReceiverOperation.Dispose)]
    public void ExternallyCreatedSemaphoreRequiresActiveSimulationForEveryReceivingShim(
        int operationValue)
    {
        var operation = (ReceiverOperation)operationValue;
        using var semaphore = new SemaphoreSlim(1, 1);

        Exception? exception = Record.Exception(
            () => InvokeReceiverOperation(semaphore, operation));

        Assert.Equal(1, semaphore.CurrentCount);
        Assert.True(semaphore.Wait(0));
        semaphore.Release();
        SimulationNotActiveExceptionAssert.Equal(exception, ExpectedApiName(operation));
    }

    private static void InvokeReceiverOperation(SemaphoreSlim semaphore, ReceiverOperation operation)
    {
        switch (operation)
        {
            case ReceiverOperation.CurrentCount:
                _ = ControlledSemaphoreSlim.CurrentCount(semaphore);
                break;
            case ReceiverOperation.Wait:
                ControlledSemaphoreSlim.Wait(semaphore);
                break;
            case ReceiverOperation.WaitCancellationToken:
                ControlledSemaphoreSlim.Wait(semaphore, CancellationToken.None);
                break;
            case ReceiverOperation.WaitMilliseconds:
                _ = ControlledSemaphoreSlim.Wait(semaphore, 0);
                break;
            case ReceiverOperation.WaitMillisecondsCancellationToken:
                _ = ControlledSemaphoreSlim.Wait(semaphore, 0, CancellationToken.None);
                break;
            case ReceiverOperation.WaitTimeSpan:
                _ = ControlledSemaphoreSlim.Wait(semaphore, TimeSpan.Zero);
                break;
            case ReceiverOperation.WaitTimeSpanCancellationToken:
                _ = ControlledSemaphoreSlim.Wait(semaphore, TimeSpan.Zero, CancellationToken.None);
                break;
            case ReceiverOperation.WaitAsync:
                ControlledSemaphoreSlim.WaitAsync(semaphore).GetAwaiter().GetResult();
                break;
            case ReceiverOperation.WaitAsyncCancellationToken:
                ControlledSemaphoreSlim.WaitAsync(semaphore, CancellationToken.None).GetAwaiter().GetResult();
                break;
            case ReceiverOperation.WaitAsyncMilliseconds:
                _ = ControlledSemaphoreSlim.WaitAsync(semaphore, 0).GetAwaiter().GetResult();
                break;
            case ReceiverOperation.WaitAsyncMillisecondsCancellationToken:
                _ = ControlledSemaphoreSlim.WaitAsync(semaphore, 0, CancellationToken.None)
                    .GetAwaiter().GetResult();
                break;
            case ReceiverOperation.WaitAsyncTimeSpan:
                _ = ControlledSemaphoreSlim.WaitAsync(semaphore, TimeSpan.Zero).GetAwaiter().GetResult();
                break;
            case ReceiverOperation.WaitAsyncTimeSpanCancellationToken:
                _ = ControlledSemaphoreSlim.WaitAsync(semaphore, TimeSpan.Zero, CancellationToken.None)
                    .GetAwaiter().GetResult();
                break;
            case ReceiverOperation.Release:
                _ = ControlledSemaphoreSlim.Release(semaphore);
                break;
            case ReceiverOperation.ReleaseCount:
                _ = ControlledSemaphoreSlim.Release(semaphore, 1);
                break;
            case ReceiverOperation.Dispose:
                ControlledSemaphoreSlim.Dispose(semaphore);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static string ExpectedApiName(ReceiverOperation operation) =>
        operation switch
        {
            ReceiverOperation.CurrentCount => "System.Threading.SemaphoreSlim.get_CurrentCount",
            ReceiverOperation.Wait or
            ReceiverOperation.WaitCancellationToken or
            ReceiverOperation.WaitMilliseconds or
            ReceiverOperation.WaitMillisecondsCancellationToken or
            ReceiverOperation.WaitTimeSpan or
            ReceiverOperation.WaitTimeSpanCancellationToken => "System.Threading.SemaphoreSlim.Wait",
            ReceiverOperation.WaitAsync or
            ReceiverOperation.WaitAsyncCancellationToken or
            ReceiverOperation.WaitAsyncMilliseconds or
            ReceiverOperation.WaitAsyncMillisecondsCancellationToken or
            ReceiverOperation.WaitAsyncTimeSpan or
            ReceiverOperation.WaitAsyncTimeSpanCancellationToken => "System.Threading.SemaphoreSlim.WaitAsync",
            ReceiverOperation.Release or
            ReceiverOperation.ReleaseCount => "System.Threading.SemaphoreSlim.Release",
            ReceiverOperation.Dispose => "System.Threading.SemaphoreSlim.Dispose",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    [Theory]
    [InlineData((int)ReceiverOperation.CurrentCount)]
    [InlineData((int)ReceiverOperation.Wait)]
    [InlineData((int)ReceiverOperation.WaitCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitMilliseconds)]
    [InlineData((int)ReceiverOperation.WaitMillisecondsCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitTimeSpan)]
    [InlineData((int)ReceiverOperation.WaitTimeSpanCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsync)]
    [InlineData((int)ReceiverOperation.WaitAsyncCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsyncMilliseconds)]
    [InlineData((int)ReceiverOperation.WaitAsyncMillisecondsCancellationToken)]
    [InlineData((int)ReceiverOperation.WaitAsyncTimeSpan)]
    [InlineData((int)ReceiverOperation.WaitAsyncTimeSpanCancellationToken)]
    [InlineData((int)ReceiverOperation.Release)]
    [InlineData((int)ReceiverOperation.ReleaseCount)]
    [InlineData((int)ReceiverOperation.Dispose)]
    public void ExternalSemaphoreRejectionPreservesRawInstanceAndPublicLoopState(
        int operationValue)
    {
        var operation = (ReceiverOperation)operationValue;
        var coordinator = new SimulationSchedulerTestHost();
        using var semaphore = new SemaphoreSlim(1, 1);
        Exception? error = null;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            error = Record.Exception(() => InvokeReceiverOperation(semaphore, operation));

            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });

        var rejection = Assert.IsType<SimulationApiException>(error);
        Assert.Equal(ExpectedApiName(operation), rejection.ApiName);
        Assert.Contains(
            "not created through the controlled SemaphoreSlim surface",
            rejection.Message,
            StringComparison.Ordinal);

        Assert.Equal(1, semaphore.CurrentCount);
        Assert.True(semaphore.Wait(0));
        Assert.Equal(0, semaphore.CurrentCount);
        Assert.Equal(0, semaphore.Release());
        Assert.Equal(1, semaphore.CurrentCount);
    }
}
