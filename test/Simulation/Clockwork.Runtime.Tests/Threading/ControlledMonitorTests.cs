using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledMonitor"/> shims: mutual exclusion, reentrancy and
/// ownership are tracked against the cooperative logical strand rather than a physical thread; a
/// contended acquire pumps the deterministic loop instead of blocking an OS thread; and
/// <c>Wait</c>/<c>Pulse</c>/<c>PulseAll</c> implement the condition-variable protocol.
/// </summary>
public sealed class ControlledMonitorTests
{
    [Fact]
    public void EnterExitTracksOwnershipAndReentrancy()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();

            Assert.False(ControlledMonitor.IsEntered(gate));
            ControlledMonitor.Enter(gate);
            Assert.True(ControlledMonitor.IsEntered(gate));

            // Reentrant acquire by the same strand just deepens the recursion count.
            ControlledMonitor.Enter(gate);
            ControlledMonitor.Exit(gate);
            Assert.True(ControlledMonitor.IsEntered(gate));

            ControlledMonitor.Exit(gate);
            Assert.False(ControlledMonitor.IsEntered(gate));
        });
    }

    [Fact]
    public void ExitWithoutOwnershipThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SynchronizationLockException>(() => ControlledMonitor.Exit(new object()));
        });
    }

    [Fact]
    public void EnterNullThrowsArgumentNull()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(
            coordinator,
            () => Assert.Throws<ArgumentNullException>(() => ControlledMonitor.Enter(null!)));
    }

    [Fact]
    public void EnterWithLockTakenTrueThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var taken = true;
            Assert.Throws<ArgumentException>(() =>
            {
                ControlledMonitor.Enter(gate, ref taken);
            });
        });
    }

    [Fact]
    public void EnterRefBoolSetsLockTaken()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var taken = false;
            ControlledMonitor.Enter(gate, ref taken);
            Assert.True(taken);
            Assert.True(ControlledMonitor.IsEntered(gate));
            ControlledMonitor.Exit(gate);
        });
    }

    [Fact]
    public void TryEnterInvalidTimeoutThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(
            coordinator,
            () => Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledMonitor.TryEnter(new object(), -2)));
    }

    [Fact]
    public void TryEnterContendedReturnsFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var tookIt = true;

            ControlledMonitor.Enter(gate);
            var contender = ControlledThread.Create(() => tookIt = ControlledMonitor.TryEnter(gate));
            ControlledThread.Start(contender);

            // The contender runs a non-blocking TryEnter while the root strand still owns the lock.
            ControlledThread.Join(contender);
            ControlledMonitor.Exit(gate);

            Assert.False(tookIt);
        });
    }

    [Fact]
    public void ContendedEnterAcquiresAfterOwnerReleases()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var log = new List<int>();

            ControlledMonitor.Enter(gate);
            var contender = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(gate);
                log.Add(2);
                ControlledMonitor.Exit(gate);
            });
            ControlledThread.Start(contender);

            log.Add(1);
            ControlledMonitor.Exit(gate);
            ControlledThread.Join(contender);
            log.Add(3);

            Assert.Equal("1,2,3", string.Join(",", log));
        });
    }

    [Fact]
    public void WaitReleasesAndReacquiresAfterPulse()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            var woke = false;

            var waiter = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                ControlledMonitor.Wait(mon);
                woke = true;
                ControlledMonitor.Exit(mon);
            });
            var signaller = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                ControlledMonitor.Pulse(mon);
                ControlledMonitor.Exit(mon);
            });

            ControlledThread.Start(waiter);
            ControlledThread.Start(signaller);

            ControlledThread.Join(waiter);
            ControlledThread.Join(signaller);

            Assert.True(woke);
        });
    }

    [Fact]
    public void PulseAllWakesEveryWaiter()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            var woken = 0;

            Thread MakeWaiter() => ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                ControlledMonitor.Wait(mon);
                woken++;
                ControlledMonitor.Exit(mon);
            });

            var w1 = MakeWaiter();
            var w2 = MakeWaiter();
            var signaller = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                ControlledMonitor.PulseAll(mon);
                ControlledMonitor.Exit(mon);
            });

            ControlledThread.Start(w1);
            ControlledThread.Start(w2);
            ControlledThread.Start(signaller);

            ControlledThread.Join(w1);
            ControlledThread.Join(w2);
            ControlledThread.Join(signaller);

            Assert.Equal(2, woken);
        });
    }

    [Fact]
    public void PulseWithoutOwnershipThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SynchronizationLockException>(() => ControlledMonitor.Pulse(new object()));
        });
    }

    [Fact]
    public void WaitWithoutOwnershipThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SynchronizationLockException>(() => ControlledMonitor.Wait(new object()));
        });
    }

    [Fact]
    public void UnpulsedWaitSurfacesAsDeadlock()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            ControlledMonitor.Enter(mon);

            // No other strand can ever pulse, so the parked wait can never make progress. On the single
            // logical thread that is recognised immediately as a deadlock instead of a real-time hang.
            Assert.Throws<SimulationSynchronousWaitDeadlockException>(() => ControlledMonitor.Wait(mon));
        });
    }

    [Fact]
    public void OutsideSimulationEnterFailsBeforeAcquiringMonitor()
    {
        var gate = new object();

        Exception? exception = Record.Exception(() => ControlledMonitor.Enter(gate));

        Assert.False(Monitor.IsEntered(gate));
        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.Monitor.Enter");

        Exception? nullException = Record.Exception(() => ControlledMonitor.Enter(null!));
        SimulationNotActiveExceptionAssert.Equal(
            nullException,
            "System.Threading.Monitor.Enter");
    }

    [Fact]
    public void LockContentionCountIsRejectedInsideSimulation()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ex = Assert.Throws<SimulationApiException>(
                () => ControlledMonitor.LockContentionCount());
            Assert.Equal("System.Threading.Monitor.LockContentionCount", ex.ApiName);
        });
    }

    // ---- Finite virtual-time timeouts (Monitor.TryEnter / Monitor.Wait) ----
    //
    // Time only advances when nothing else can run, so a discrete event (a release, a pulse) always happens
    // before any timeout that needs a time advance. To sequence an event at a chosen virtual instant D these
    // tests build a deterministic "virtual delay" out of a finite Monitor.Wait that is never pulsed: it
    // parks, times out at exactly D, and its strand then performs the release/pulse. Comparing D to the
    // waiter's timeout T gives exact before/at/after-deadline coverage with no wall-clock time.

    private static void VirtualDelayThenRun(int delayMilliseconds, Action action)
    {
        var timer = new object();
        ControlledMonitor.Enter(timer);
        var pulsed = ControlledMonitor.Wait(timer, delayMilliseconds);
        Assert.False(pulsed); // Never pulsed, so it always elapses on its virtual deadline.
        ControlledMonitor.Exit(timer);
        action();
    }

    [Fact]
    public void TryEnterFiniteTimesOutWhenNeverReleased()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var acquired = true;

            ControlledMonitor.Enter(gate);
            var contender = ControlledThread.Create(() => acquired = ControlledMonitor.TryEnter(gate, 100));
            ControlledThread.Start(contender);

            // The root never releases before joining, so the contender parks and its virtual-time deadline
            // is the only event left: modelled time advances to it and the acquire returns false.
            ControlledThread.Join(contender);
            Assert.False(acquired);

            // The root still owns the lock; the timed-out acquire took nothing.
            Assert.True(ControlledMonitor.IsEntered(gate));
            ControlledMonitor.Exit(gate);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void TryEnterFiniteRefBoolTimesOut()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var taken = true;

            ControlledMonitor.Enter(gate);
            var contender = ControlledThread.Create(() =>
            {
                var t = false;
                ControlledMonitor.TryEnter(gate, 50, ref t);
                taken = t;
            });
            ControlledThread.Start(contender);
            ControlledThread.Join(contender);

            Assert.False(taken);
            ControlledMonitor.Exit(gate);
        });
    }

    [Fact]
    public void WaitFiniteTimesOutAndReacquiresMonitor()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            var signalled = true;
            var ownedAfter = false;

            var waiter = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                signalled = ControlledMonitor.Wait(mon, 100);
                // Wait must reacquire the monitor before returning, even on timeout.
                ownedAfter = ControlledMonitor.IsEntered(mon);
                ControlledMonitor.Exit(mon);
            });

            ControlledThread.Start(waiter);
            ControlledThread.Join(waiter);

            Assert.False(signalled); // Timed out - no pulse ever arrived.
            Assert.True(ownedAfter); // Monitor was reacquired before the timed-out Wait returned.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFinitePulsedBeforeDeadlineReturnsTrue()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            var signalled = false;

            var waiter = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                signalled = ControlledMonitor.Wait(mon, 500); // Deadline 500.
                ControlledMonitor.Exit(mon);
            });
            var signaller = ControlledThread.Create(() => VirtualDelayThenRun(100, () =>
            {
                ControlledMonitor.Enter(mon);
                ControlledMonitor.Pulse(mon);
                ControlledMonitor.Exit(mon);
            }));

            ControlledThread.Start(waiter);
            ControlledThread.Start(signaller);
            ControlledThread.Join(waiter);
            ControlledThread.Join(signaller);

            Assert.True(signalled); // Pulse at 100 (< 500) beat the timeout.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFinitePulsedAfterDeadlineReturnsFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            var signalled = true;

            var waiter = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                signalled = ControlledMonitor.Wait(mon, 100); // Deadline 100.
                ControlledMonitor.Exit(mon);
            });
            var signaller = ControlledThread.Create(() => VirtualDelayThenRun(500, () =>
            {
                ControlledMonitor.Enter(mon);
                // The waiter has already timed out and left, so this pulse selects no waiter (a late pulse
                // must not be consumed by a waiter that already gave up).
                ControlledMonitor.Pulse(mon);
                ControlledMonitor.Exit(mon);
            }));

            ControlledThread.Start(waiter);
            ControlledThread.Start(signaller);
            ControlledThread.Join(waiter);
            ControlledThread.Join(signaller);

            Assert.False(signalled); // Timed out at 100 before the pulse at 500.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteRestoresRecursionCountOnTimeout()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var mon = new object();
            var reentrantlyOwned = false;

            var waiter = ControlledThread.Create(() =>
            {
                ControlledMonitor.Enter(mon);
                ControlledMonitor.Enter(mon); // Recursion depth 2.
                ControlledMonitor.Wait(mon, 50); // Timeout: must restore the full depth-2 ownership.

                ControlledMonitor.Exit(mon);
                reentrantlyOwned = ControlledMonitor.IsEntered(mon); // Still owned after one Exit.
                ControlledMonitor.Exit(mon);
            });

            ControlledThread.Start(waiter);
            ControlledThread.Join(waiter);

            Assert.True(reentrantlyOwned);
            Assert.False(ControlledMonitor.IsEntered(mon));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteAtDeadlineIsDeterministicTimeout()
    {
        // When a pulse and the wait's deadline are due at the same virtual instant, the earlier-registered
        // deadline fires first. The waiter parks (and registers) before the signaller's delay, so the
        // waiter times out - a deterministic, replayable tie-break rather than a real-time race.
        for (var seed = 1; seed <= 5; seed++)
        {
            var coordinator = new SimulationSchedulerTestHost(seed);
            var signalled = true;

            TaskTestHarness.RunInSimulation(
                coordinator,
                () =>
                {
                    var mon = new object();
                    var waiter = ControlledThread.Create(() =>
                    {
                        ControlledMonitor.Enter(mon);
                        signalled = ControlledMonitor.Wait(mon, 100);
                        ControlledMonitor.Exit(mon);
                    });
                    var signaller = ControlledThread.Create(() => VirtualDelayThenRun(100, () =>
                    {
                        ControlledMonitor.Enter(mon);
                        ControlledMonitor.Pulse(mon);
                        ControlledMonitor.Exit(mon);
                    }));

                    ControlledThread.Start(waiter);
                    ControlledThread.Start(signaller);
                    ControlledThread.Join(waiter);
                    ControlledThread.Join(signaller);
                });

            Assert.False(signalled);
        }
    }

    [Fact]
    public void RepeatedFiniteTimeoutsLeaveNoLeakedWaiters()
    {
        // Stress: the same finite-timeout scenario replayed many times is deterministic and leaves the loop
        // idle every time (no parked waiter or pending deadline leaks across iterations).
        for (var i = 0; i < 200; i++)
        {
            var coordinator = new SimulationSchedulerTestHost();
            var acquired = true;

            TaskTestHarness.RunInSimulation(coordinator, () =>
            {
                var gate = new object();
                ControlledMonitor.Enter(gate);
                var contender = ControlledThread.Create(() => acquired = ControlledMonitor.TryEnter(gate, 25));
                ControlledThread.Start(contender);
                ControlledThread.Join(contender);
                ControlledMonitor.Exit(gate);
            });

            Assert.False(acquired);
            Assert.True(coordinator.Scheduler.IsIdle);
        }
    }

    [Fact]
    public void SafeThreadPoolCallbackDoesNotInheritMonitorOwnership()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new object();
            var nestedWasOwner = true;
            var nestedAcquired = true;

            ControlledMonitor.Enter(gate);
            var accepted = ControlledThreadPool.QueueUserWorkItem(
                _ =>
                {
                    nestedWasOwner = ControlledMonitor.IsEntered(gate);
                    nestedAcquired = ControlledMonitor.TryEnter(gate);
                    if (nestedAcquired)
                    {
                        ControlledMonitor.Exit(gate);
                    }
                },
                state: null);

            Assert.True(accepted);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(ControlledMonitor.IsEntered(gate));
            ControlledMonitor.Exit(gate);
            Assert.False(ControlledMonitor.IsEntered(gate));
            Assert.False(nestedWasOwner);
            Assert.False(nestedAcquired);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Theory]
    [InlineData((int)QueuedTaskVariant.TaskRun)]
    [InlineData((int)QueuedTaskVariant.TaskFactoryStartNew)]
    [InlineData((int)QueuedTaskVariant.ContinueWith)]
    public void QueuedTaskWorkDoesNotInheritMonitorOwnership(int variantValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (QueuedTaskVariant)variantValue;
            var gate = new object();
            var nestedWasOwner = true;
            var nestedAcquired = true;
            var antecedent = new TaskCompletionSource();

            void ProbeOwnership()
            {
                nestedWasOwner = ControlledMonitor.IsEntered(gate);
                nestedAcquired = ControlledMonitor.TryEnter(gate);
                if (nestedAcquired)
                {
                    ControlledMonitor.Exit(gate);
                }
            }

            ControlledMonitor.Enter(gate);
            Task task = variant switch
            {
                QueuedTaskVariant.TaskRun => ControlledTask.Run(ProbeOwnership),
                QueuedTaskVariant.TaskFactoryStartNew =>
                    ControlledTaskFactory.StartNew(Task.Factory, ProbeOwnership),
                QueuedTaskVariant.ContinueWith =>
                    ControlledTask.ContinueWith(antecedent.Task, _ => ProbeOwnership()),
                _ => throw new ArgumentOutOfRangeException(nameof(variantValue)),
            };

            Assert.False(task.IsCompleted);
            if (variant == QueuedTaskVariant.ContinueWith)
            {
                antecedent.SetResult();
            }

            coordinator.Scheduler.DrainUntil(() => task.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.Equal(TaskStatus.RanToCompletion, task.Status);
            Assert.True(ControlledMonitor.IsEntered(gate));
            ControlledMonitor.Exit(gate);
            Assert.False(ControlledMonitor.IsEntered(gate));
            Assert.False(nestedWasOwner);
            Assert.False(nestedAcquired);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    private enum QueuedTaskVariant
    {
        TaskRun,
        TaskFactoryStartNew,
        ContinueWith,
    }

    [Theory]
    [InlineData((int)NestedQueueVariant.SafeWaitCallback)]
    [InlineData((int)NestedQueueVariant.SafeGeneric)]
    [InlineData((int)NestedQueueVariant.UnsafeWaitCallback)]
    [InlineData((int)NestedQueueVariant.UnsafeGeneric)]
    [InlineData((int)NestedQueueVariant.UnsafeWorkItem)]
    [InlineData((int)NestedQueueVariant.TaskRun)]
    [InlineData((int)NestedQueueVariant.TaskFactoryStartNew)]
    [InlineData((int)NestedQueueVariant.ContinueWith)]
    public void NestedQueuedOperationsUseDistinctMonitorOwners(int variantValue)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (NestedQueueVariant)variantValue;
            var gate = new object();
            var outerOwnedBeforeInner = false;
            var outerOwnedAfterInner = false;
            var outerOwnedAfterExit = true;
            var innerWasOwner = true;
            var innerAcquired = true;
            var innerOwnedAfterTry = true;
            QueuedOperation? innerOperation = null;

            var outerOperation = QueueOperation(variant, () =>
            {
                ControlledMonitor.Enter(gate);
                outerOwnedBeforeInner = ControlledMonitor.IsEntered(gate);

                innerOperation = QueueOperation(variant, () =>
                {
                    innerWasOwner = ControlledMonitor.IsEntered(gate);
                    innerAcquired = ControlledMonitor.TryEnter(gate);
                    if (innerAcquired)
                    {
                        ControlledMonitor.Exit(gate);
                    }

                    innerOwnedAfterTry = ControlledMonitor.IsEntered(gate);
                });

                coordinator.Scheduler.DrainUntil(
                    () => innerOperation.IsCompleted,
                    "nested queued Monitor ownership probe",
                    TestContext.Current.CancellationToken);

                outerOwnedAfterInner = ControlledMonitor.IsEntered(gate);
                ControlledMonitor.Exit(gate);
                outerOwnedAfterExit = ControlledMonitor.IsEntered(gate);
            });

            Assert.False(outerOperation.IsCompleted);
            coordinator.Scheduler.DrainUntil(
                () => outerOperation.IsCompleted,
                "outer queued Monitor ownership probe",
                TestContext.Current.CancellationToken);

            AssertQueuedOperationCompleted(outerOperation);
            Assert.NotNull(innerOperation);
            AssertQueuedOperationCompleted(innerOperation);
            Assert.True(outerOwnedBeforeInner);
            Assert.False(innerWasOwner);
            Assert.False(innerAcquired);
            Assert.False(innerOwnedAfterTry);
            Assert.True(outerOwnedAfterInner);
            Assert.False(outerOwnedAfterExit);
            Assert.False(ControlledMonitor.IsEntered(gate));
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Equal(TimeSpan.Zero, coordinator.Scheduler.VirtualTime);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    private static QueuedOperation QueueOperation(NestedQueueVariant variant, Action callback)
    {
        var operation = new QueuedOperation(variant);

        void Execute()
        {
            callback();
            operation.CallbackCount++;
        }

        switch (variant)
        {
            case NestedQueueVariant.SafeWaitCallback:
                operation.WasAccepted = ControlledThreadPool.QueueUserWorkItem(
                    static state => ((Action)state!)(),
                    (Action)Execute);
                break;
            case NestedQueueVariant.SafeGeneric:
                operation.WasAccepted = ControlledThreadPool.QueueUserWorkItem(
                    static action => action(),
                    (Action)Execute,
                    preferLocal: true);
                break;
            case NestedQueueVariant.UnsafeWaitCallback:
                operation.WasAccepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                    static state => ((Action)state!)(),
                    (Action)Execute);
                break;
            case NestedQueueVariant.UnsafeGeneric:
                operation.WasAccepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                    static action => action(),
                    (Action)Execute,
                    preferLocal: false);
                break;
            case NestedQueueVariant.UnsafeWorkItem:
                operation.WasAccepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                    new DelegateWorkItem(Execute),
                    preferLocal: true);
                break;
            case NestedQueueVariant.TaskRun:
                operation.Completion = ControlledTask.Run(() =>
                {
                    Execute();
                    return QueuedOperation.ExpectedResult;
                });
                break;
            case NestedQueueVariant.TaskFactoryStartNew:
                operation.Completion = ControlledTaskFactory.StartNew(Task.Factory, () =>
                {
                    Execute();
                    return QueuedOperation.ExpectedResult;
                });
                break;
            case NestedQueueVariant.ContinueWith:
                var antecedent = new TaskCompletionSource<int>();
                operation.Antecedent = antecedent.Task;
                operation.Completion = ControlledTask.ContinueWith<int, int>(
                    antecedent.Task,
                    completed =>
                    {
                        operation.ObservedAntecedentResult = completed.Result;
                        Execute();
                        return QueuedOperation.ExpectedResult;
                    });
                antecedent.SetResult(QueuedOperation.ExpectedAntecedentResult);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        return operation;
    }

    private static void AssertQueuedOperationCompleted(QueuedOperation operation)
    {
        Assert.True(operation.IsCompleted);
        Assert.Equal(1, operation.CallbackCount);

        switch (operation.Variant)
        {
            case NestedQueueVariant.SafeWaitCallback:
            case NestedQueueVariant.SafeGeneric:
            case NestedQueueVariant.UnsafeWaitCallback:
            case NestedQueueVariant.UnsafeGeneric:
            case NestedQueueVariant.UnsafeWorkItem:
                Assert.Equal(true, operation.WasAccepted);
                break;
            case NestedQueueVariant.TaskRun:
            case NestedQueueVariant.TaskFactoryStartNew:
                var completion = operation.Completion;
                Assert.NotNull(completion);
                Assert.Equal(TaskStatus.RanToCompletion, completion.Status);
                Assert.Equal(QueuedOperation.ExpectedResult, completion.Result);
                break;
            case NestedQueueVariant.ContinueWith:
                var antecedent = operation.Antecedent;
                Assert.NotNull(antecedent);
                Assert.Equal(TaskStatus.RanToCompletion, antecedent.Status);
                Assert.Equal(QueuedOperation.ExpectedAntecedentResult, antecedent.Result);
                Assert.Equal(QueuedOperation.ExpectedAntecedentResult, operation.ObservedAntecedentResult);
                var continuation = operation.Completion;
                Assert.NotNull(continuation);
                Assert.Equal(TaskStatus.RanToCompletion, continuation.Status);
                Assert.Equal(QueuedOperation.ExpectedResult, continuation.Result);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Variant, null);
        }
    }

    private enum NestedQueueVariant
    {
        SafeWaitCallback,
        SafeGeneric,
        UnsafeWaitCallback,
        UnsafeGeneric,
        UnsafeWorkItem,
        TaskRun,
        TaskFactoryStartNew,
        ContinueWith,
    }

    private sealed class QueuedOperation(NestedQueueVariant variant)
    {
        public const int ExpectedAntecedentResult = 37;
        public const int ExpectedResult = 73;

        public NestedQueueVariant Variant { get; } = variant;

        public bool? WasAccepted { get; set; }

        public int CallbackCount { get; set; }

        public int? ObservedAntecedentResult { get; set; }

        public Task<int>? Antecedent { get; set; }

        public Task<int>? Completion { get; set; }

        public bool IsCompleted => Variant switch
        {
            NestedQueueVariant.SafeWaitCallback
                or NestedQueueVariant.SafeGeneric
                or NestedQueueVariant.UnsafeWaitCallback
                or NestedQueueVariant.UnsafeGeneric
                or NestedQueueVariant.UnsafeWorkItem => CallbackCount == 1,
            NestedQueueVariant.TaskRun
                or NestedQueueVariant.TaskFactoryStartNew
                or NestedQueueVariant.ContinueWith => Completion?.IsCompleted == true,
            _ => throw new ArgumentOutOfRangeException(nameof(Variant)),
        };
    }

    private sealed class DelegateWorkItem(Action execute) : IThreadPoolWorkItem
    {
        public void Execute() => execute();
    }
}
