using System.Collections.Generic;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledThreadPool"/> shims: <c>QueueUserWorkItem</c> /
/// <c>UnsafeQueueUserWorkItem</c> queue their callback as a fresh controlled operation (run when the loop
/// is pumped, not inline), the safe variants flow the caller's <see cref="ExecutionContext"/> while the
/// unsafe variants do not, the native-overlapped surface is rejected precisely, the registered-wait
/// factories fire their <see cref="WaitOrTimerCallback"/> as a controlled operation on signal
/// (<c>timedOut: false</c>) or virtual-time timeout (<c>timedOut: true</c>) honouring executeOnlyOnce /
/// repeating and <c>Unregister</c>.
/// </summary>
public sealed class ControlledThreadPoolTests
{
    private sealed class RecordingWorkItem : IThreadPoolWorkItem
    {
        public bool Executed { get; private set; }

        public void Execute() => Executed = true;
    }

    [Fact]
    public void QueueUserWorkItemQueuesCallbackAndRunsWhenPumped()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ran = false;
            var accepted = ControlledThreadPool.QueueUserWorkItem(_ => ran = true);

            Assert.True(accepted);
            Assert.False(ran); // queued, not run inline.

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(ran);
        });
    }

    [Fact]
    public void QueueUserWorkItemPassesState()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            object? seen = null;
            ControlledThreadPool.QueueUserWorkItem(s => seen = s, "payload");
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal("payload", seen);
        });
    }

    [Fact]
    public void GenericQueueUserWorkItemPassesTypedState()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var seen = 0;
            ControlledThreadPool.QueueUserWorkItem(s => seen = s, 42, preferLocal: true);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(42, seen);
        });
    }

    [Fact]
    public void UnsafeQueueUserWorkItemRunsWorkItem()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var item = new RecordingWorkItem();
            ControlledThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
            Assert.False(item.Executed);

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(item.Executed);
        });
    }

    [Fact]
    public void UnsafeGenericQueueUserWorkItemPassesTypedState()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var seen = 0;
            ControlledThreadPool.UnsafeQueueUserWorkItem(s => seen = s, 7, preferLocal: false);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(7, seen);
        });
    }

    [Fact]
    public void SafeQueueFlowsCapturedExecutionContext()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            var seen = -1;

            // Safe QueueUserWorkItem captures the caller's ExecutionContext at enqueue time.
            ControlledThreadPool.QueueUserWorkItem(_ => seen = ambient.Value);

            // Mutating the ambient value after enqueue must not affect the flowed snapshot.
            ambient.Value = 9;
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(5, seen);
        });
    }

    [Fact]
    public void UnsafeQueueDoesNotFlowCapturedExecutionContext()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            var seen = -1;

            // Unsafe queueing does not flow caller AsyncLocal values into the fresh worker context.
            ControlledThreadPool.UnsafeQueueUserWorkItem(_ => seen = ambient.Value, state: null);

            ambient.Value = 9;
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(0, seen);
        });
    }

    [Fact]
    public void RejectNativeOverlappedThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ex = Assert.Throws<SimulationApiException>(
                () => ControlledThreadPool.RejectNativeOverlapped(
                    "System.Threading.ThreadPool.UnsafeQueueNativeOverlapped"));
            Assert.Contains("UnsafeQueueNativeOverlapped", ex.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RegisteredWaitFiresOnceOnSignalWithTimedOutFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var fires = new List<bool>();

            ControlledRegisteredWaitHandle reg = ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (_, timedOut) => fires.Add(timedOut), state: null, Timeout.Infinite, executeOnlyOnce: true);

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Empty(fires); // Armed but blocked: no signal yet.

            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal([false], fires); // Signalled => timedOut false.

            // executeOnlyOnce: a second signal must not fire the callback again.
            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal([false], fires);

            Assert.True(reg.Unregister(null));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void RegisteredWaitFiresOnTimeoutWithTimedOutTrue()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var fires = new List<bool>();

            ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (_, timedOut) => fires.Add(timedOut), state: null, 100, executeOnlyOnce: true);

            // The virtual-time deadline elapses with no signal => the callback fires with timedOut true.
            DrainWithTime(coordinator);
            Assert.Equal([true], fires);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void RegisteredWaitUIntTimeoutUsesOnlyUIntMaxAsTheInfiniteSentinel()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            ControlledRegisteredWaitHandle infinite = ControlledThreadPool.RegisterWaitForSingleObject(
                evt,
                (_, _) => { },
                state: null,
                uint.MaxValue,
                executeOnlyOnce: true);
            Assert.True(infinite.Unregister(null));

            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledThreadPool.RegisterWaitForSingleObject(
                    evt,
                    (_, _) => { },
                    state: null,
                    (uint)int.MaxValue + 1,
                    executeOnlyOnce: true));
            Assert.Equal("millisecondsTimeOutInterval", exception.ParamName);
        });
    }

    [Fact]
    public void RepeatingRegisteredWaitFiresEachSignalUntilUnregister()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var count = 0;

            ControlledRegisteredWaitHandle reg = ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (_, _) => count++, state: null, Timeout.Infinite, executeOnlyOnce: false);

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(1, count);

            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(2, count); // Re-armed and fired again.

            Assert.True(reg.Unregister(null));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            // After Unregister a further signal is not delivered.
            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(2, count);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void RepeatingRegisteredWaitRearmsBeforeBlockingCallbackSoAutoResetSignalsAreNotLost()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            AutoResetEvent releaseFirstCallback = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var count = 0;

            ControlledRegisteredWaitHandle registration = ControlledThreadPool.RegisterWaitForSingleObject(
                evt,
                (_, _) =>
                {
                    count++;
                    if (count == 1)
                    {
                        SimulationTaskRuntime.QueueWork(
                            () =>
                            {
                                ControlledEventWaitHandle.Set(evt);
                                ControlledEventWaitHandle.Set(evt);
                                ControlledEventWaitHandle.Set(releaseFirstCallback);
                            },
                            "test.registered-wait-signals",
                            flowExecutionContext: false);
                        Assert.True(ControlledWaitHandle.WaitOne(releaseFirstCallback));
                    }
                },
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(3, count);
            Assert.True(registration.Unregister(null));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public void RegisteredWaitCallbackRunsAsAFreshControlledStrand()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var monitor = new object();
            Exception? callbackException = null;

            ControlledThreadPool.RegisterWaitForSingleObject(
                evt,
                (_, _) => callbackException = Record.Exception(() => ControlledMonitor.Exit(monitor)),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: true);

            ControlledMonitor.Enter(monitor);
            try
            {
                ControlledEventWaitHandle.Set(evt);
                coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            }
            finally
            {
                ControlledMonitor.Exit(monitor);
            }

            Assert.IsType<SynchronizationLockException>(callbackException);
        });
    }

    [Fact]
    public void UnregisterSignalsCompletionHandleWhenProvided()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            ManualResetEvent done = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);

            ControlledRegisteredWaitHandle reg = ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (_, _) => { }, state: null, Timeout.Infinite, executeOnlyOnce: false);

            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.True(reg.Unregister(done));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            // The completion handle is signalled once the registration has stopped firing.
            Assert.True(ControlledWaitHandle.WaitOne(done, 0));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void SafeRegisterFlowsCapturedExecutionContext()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            var seen = -1;
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            // Safe RegisterWaitForSingleObject captures the caller's ExecutionContext at registration time.
            ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (_, _) => seen = ambient.Value, state: null, Timeout.Infinite, executeOnlyOnce: true);

            ambient.Value = 9; // Post-registration mutation must not affect the flowed snapshot.
            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(5, seen);
        });
    }

    [Fact]
    public void UnsafeRegisterDoesNotFlowCapturedExecutionContext()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var ambient = new AsyncLocal<int> { Value = 5 };
            var seen = -1;
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            // Unsafe variant does not capture the caller's context; it runs under a clean user execution
            // context rather than inheriting whichever AsyncLocal values happen to be ambient while the
            // controlled loop dispatches the registration.
            ControlledThreadPool.UnsafeRegisterWaitForSingleObject(
                evt, (_, _) => seen = ambient.Value, state: null, Timeout.Infinite, executeOnlyOnce: true);

            ambient.Value = 9;
            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(0, seen);
        });
    }

    [Fact]
    public void RegisterWaitPassesState()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            object? seen = null;

            ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (s, _) => seen = s, state: "payload", Timeout.Infinite, executeOnlyOnce: true);

            ControlledEventWaitHandle.Set(evt);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal("payload", seen);
        });
    }

    [Fact]
    public void OutsideSimulationRegisterWaitFailsBeforeTouchingHandlesOrCallback()
    {
        using var evt = new ManualResetEvent(false);
        using var fired = new ManualResetEventSlim(false);
        ControlledRegisteredWaitHandle? registration = null;

        Exception? exception = Record.Exception(
            () => registration = ControlledThreadPool.RegisterWaitForSingleObject(
                evt, (_, _) => fired.Set(), state: null, Timeout.Infinite, executeOnlyOnce: true));

        Assert.Null(registration);
        Assert.False(evt.WaitOne(0));
        Assert.False(fired.IsSet);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.ThreadPool.RegisterWaitForSingleObject");
    }

    [Fact]
    public void OutsideSimulationQueueFailsBeforeQueuingTheCallback()
    {
        var ran = false;

        Exception? exception = Record.Exception(
            () => ControlledThreadPool.QueueUserWorkItem(_ => ran = true));

        Assert.False(ran);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.ThreadPool.QueueUserWorkItem");

        Exception? nullCallbackException = Record.Exception(
            () => ControlledThreadPool.QueueUserWorkItem(null!));
        SimulationNotActiveExceptionAssert.Equal(
            nullCallbackException,
            "System.Threading.ThreadPool.QueueUserWorkItem");
    }

    // Pumps the loop and folds virtual-time deadlines in, mirroring a host drive loop: run ready work to
    // idle, then advance to the next pending deadline (firing timeouts) and pump again, until quiescent.
    private static void DrainWithTime(SimulationSchedulerTestHost coordinator)
    {
        while (true)
        {
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            TimeSpan? due = coordinator.Scheduler.NextTimerDue;
            if (due is null)
            {
                return;
            }

            coordinator.Scheduler.AdvanceVirtualTimeTo(due.Value);
        }
    }

    [Theory]
    [InlineData((int)QueueVariant.QueueWaitCallback, 5)]
    [InlineData((int)QueueVariant.QueueGeneric, 5)]
    [InlineData((int)QueueVariant.UnsafeQueueWaitCallback, 0)]
    [InlineData((int)QueueVariant.UnsafeQueueGeneric, 0)]
    [InlineData((int)QueueVariant.UnsafeQueueWorkItem, 0)]
    public void QueueVariantsMatchBclExecutionContextFlowAtExecutionTime(int variantValue, int expected)
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var variant = (QueueVariant)variantValue;
            var ambient = new AsyncLocal<int> { Value = 5 };
            var seen = -1;
            bool accepted;

            switch (variant)
            {
                case QueueVariant.QueueWaitCallback:
                    accepted = ControlledThreadPool.QueueUserWorkItem(
                        _ => seen = ambient.Value,
                        state: new object());
                    break;
                case QueueVariant.QueueGeneric:
                    accepted = ControlledThreadPool.QueueUserWorkItem(
                        _ => seen = ambient.Value,
                        state: 17,
                        preferLocal: true);
                    break;
                case QueueVariant.UnsafeQueueWaitCallback:
                    accepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                        _ => seen = ambient.Value,
                        state: new object());
                    break;
                case QueueVariant.UnsafeQueueGeneric:
                    accepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                        _ => seen = ambient.Value,
                        state: 17,
                        preferLocal: false);
                    break;
                case QueueVariant.UnsafeQueueWorkItem:
                    accepted = ControlledThreadPool.UnsafeQueueUserWorkItem(
                        new ExecutionContextRecordingWorkItem(() => seen = ambient.Value),
                        preferLocal: true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variantValue));
            }

            Assert.True(accepted);
            Assert.Equal(-1, seen);
            Assert.Equal(1, coordinator.Scheduler.RunnableOperationCount);
            Assert.False(coordinator.Scheduler.IsIdle);

            ambient.Value = 9;
            Assert.Equal(1, coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken));

            Assert.Equal(expected, seen);
            Assert.Equal(9, ambient.Value);
            Assert.Equal(0, coordinator.Scheduler.RunnableOperationCount);
            Assert.Equal(0, coordinator.Scheduler.WaitingOperationCount);
            Assert.Null(coordinator.Scheduler.NextTimerDue);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    private enum QueueVariant
    {
        QueueWaitCallback,
        QueueGeneric,
        UnsafeQueueWaitCallback,
        UnsafeQueueGeneric,
        UnsafeQueueWorkItem,
    }

    private sealed class ExecutionContextRecordingWorkItem(Action execute) : IThreadPoolWorkItem
    {
        public void Execute() => execute();
    }
}
