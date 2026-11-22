using System;
using System.Collections.Generic;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled event / wait-handle surface (<see cref="ControlledEventWaitHandle"/> and
/// <see cref="ControlledWaitHandle"/>). Inside a simulation the signalled state and its deterministic FIFO
/// waiter set are modelled on the cooperative logical thread: a <see cref="WaitHandle.WaitOne()"/> with no
/// signal pumps the loop until <c>Set</c>, an auto-reset event wakes exactly one waiter while a manual-reset
/// event releases all and stays signalled, and finite timeouts consume only virtual time. Named /
/// cross-process APIs and the raw handle accessors are rejected precisely.
/// </summary>
public sealed class ControlledEventWaitHandleTests
{
    // ---- construction + immediate (zero-wait) semantics ----

    [Fact]
    public void AutoResetEventInitiallySignalledConsumesOnce()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);

            // The single signal is consumed by the first WaitOne; the second times out immediately.
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));
            Assert.False(ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    [Fact]
    public void ManualResetEventInitiallySignalledStaysSignalled()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent evt = ControlledEventWaitHandle.CreateManualResetEvent(initialState: true);

            // A manual-reset event remains signalled until Reset, so repeated waits all succeed.
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));

            Assert.True(ControlledEventWaitHandle.Reset(evt));
            Assert.False(ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    [Fact]
    public void AutoResetEventSetThenWaitConsumesSignal()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            Assert.False(ControlledWaitHandle.WaitOne(evt, 0));
            Assert.True(ControlledEventWaitHandle.Set(evt));
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));
            Assert.False(ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    // ---- cross-strand signal wakes a blocked waiter ----

    [Fact]
    public void AutoResetSetWakesExactlyOneWaiter()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var woke = new List<int>();

            var first = ControlledThread.Create(() =>
            {
                ControlledWaitHandle.WaitOne(evt);
                woke.Add(1);
            });
            var second = ControlledThread.Create(() =>
            {
                ControlledWaitHandle.WaitOne(evt);
                woke.Add(2);
            });

            ControlledThread.Start(first);
            ControlledThread.Start(second);

            // A single Set releases exactly one waiter (FIFO: the first to block).
            ControlledEventWaitHandle.Set(evt);
            ControlledThread.Join(first);

            Assert.Equal([1], woke);

            // The second waiter is still blocked; a second Set releases it.
            ControlledEventWaitHandle.Set(evt);
            ControlledThread.Join(second);

            Assert.Equal([1, 2], woke);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void ManualResetSetReleasesAllWaiters()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent evt = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            var woke = new List<int>();

            var first = ControlledThread.Create(() =>
            {
                ControlledWaitHandle.WaitOne(evt);
                woke.Add(1);
            });
            var second = ControlledThread.Create(() =>
            {
                ControlledWaitHandle.WaitOne(evt);
                woke.Add(2);
            });

            ControlledThread.Start(first);
            ControlledThread.Start(second);

            // A single Set releases every waiter and stays signalled.
            ControlledEventWaitHandle.Set(evt);
            ControlledThread.Join(first);
            ControlledThread.Join(second);

            Assert.Equal([1, 2], woke);
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0)); // Still signalled.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    // ---- finite virtual-time timeouts / signal-vs-timeout races ----

    [Fact]
    public void WaitFiniteTimesOutWhenNeverSet()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            Assert.False(ControlledWaitHandle.WaitOne(evt, 100));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteSetBeforeDeadlineSucceeds()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var signalled = false;

            var waiter = ControlledThread.Create(() => signalled = ControlledWaitHandle.WaitOne(evt, 500));
            var setter = ControlledThread.Create(() =>
                VirtualDelayThenRun(100, () => ControlledEventWaitHandle.Set(evt)));

            ControlledThread.Start(waiter);
            ControlledThread.Start(setter);
            ControlledThread.Join(waiter);
            ControlledThread.Join(setter);

            Assert.True(signalled); // Set at 100 (< 500) beat the timeout.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitFiniteSetAfterDeadlineTimesOut()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var signalled = true;

            var waiter = ControlledThread.Create(() => signalled = ControlledWaitHandle.WaitOne(evt, 100));
            var setter = ControlledThread.Create(() =>
                VirtualDelayThenRun(500, () => ControlledEventWaitHandle.Set(evt)));

            ControlledThread.Start(waiter);
            ControlledThread.Start(setter);
            ControlledThread.Join(waiter);
            ControlledThread.Join(setter);

            Assert.False(signalled); // Timed out at 100 before the Set at 500.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitOneTimeSpanOverloadHonoursVirtualDeadline()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            Assert.False(ControlledWaitHandle.WaitOne(evt, TimeSpan.FromMilliseconds(100)));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    // ---- disposal ----

    [Fact]
    public void WaitAfterDisposeThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            ControlledWaitHandle.Dispose(evt);
            Assert.Throws<ObjectDisposedException>(() => ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    [Fact]
    public void SetAfterDisposeThrows()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent evt = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            ControlledWaitHandle.Close(evt);
            Assert.Throws<ObjectDisposedException>(() => ControlledEventWaitHandle.Set(evt));
        });
    }

    // ---- invalid input ----

    [Fact]
    public void WaitOneRejectsInvalidTimeout()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlledWaitHandle.WaitOne(evt, -2));
        });
    }

    // ---- named / cross-process + raw handle rejections ----

    [Fact]
    public void NamedEventConstructorIsRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SimulationApiException>(() =>
                ControlledEventWaitHandle.CreateNamedEvent(false, EventResetMode.AutoReset, "clockwork-named"));
        });
    }

    [Fact]
    public void NullNamedEventConstructorIsControlled()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // A null name is a degenerate unnamed event, which is fully controlled.
            EventWaitHandle evt = ControlledEventWaitHandle.CreateNamedEvent(true, EventResetMode.ManualReset, name: null);
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    [Fact]
    public void OpenExistingIsRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SimulationApiException>(() =>
                ControlledEventWaitHandle.OpenExisting("clockwork-named"));
        });
    }

    [Fact]
    public void TryOpenExistingIsRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SimulationApiException>(() =>
                ControlledEventWaitHandle.TryOpenExisting("clockwork-named", out _));
        });
    }

    [Fact]
    public void RawHandleAccessorsAreRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            Assert.Throws<SimulationApiException>(() => ControlledWaitHandle.GetHandle(evt));
            Assert.Throws<SimulationApiException>(() => ControlledWaitHandle.GetSafeWaitHandle(evt));
        });
    }

    [Fact]
    public void WaitingOnUncontrolledHandleIsRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // An event constructed directly (not via a Create factory) has no modelled state.
            using var raw = new AutoResetEvent(false);
            Assert.Throws<SimulationApiException>(() => ControlledWaitHandle.WaitOne(raw, 0));
        });
    }

    [Fact]
    public void OutsideSimulationFailsBeforeCreatingEventHandle()
    {
        ManualResetEvent? evt = null;

        Exception? exception = Record.Exception(
            () => evt = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false));

        Assert.Null(evt);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.ManualResetEvent..ctor");
    }

    // ---- WaitAny / WaitAll / SignalAndWait ----

    [Fact]
    public void WaitAnyReturnsLowestSignalledIndex()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            ManualResetEvent b = ControlledEventWaitHandle.CreateManualResetEvent(initialState: true);
            AutoResetEvent c = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);

            // Both b (index 1) and c (index 2) are signalled; WaitAny serves the lowest index.
            Assert.Equal(1, ControlledWaitHandle.WaitAny([a, b, c]));
        });
    }

    [Fact]
    public void WaitAnyConsumesExactlyOneAutoResetSignal()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            AutoResetEvent b = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);

            Assert.Equal(1, ControlledWaitHandle.WaitAny([a, b])); // Consumes b.
            Assert.Equal(ControlledWaitHandle.WaitTimeout, ControlledWaitHandle.WaitAny([a, b], 0));
        });
    }

    [Fact]
    public void WaitAnyTimesOutWhenNoneSignalled()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            AutoResetEvent b = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            Assert.Equal(ControlledWaitHandle.WaitTimeout, ControlledWaitHandle.WaitAny([a, b], 100));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAnyWakesWhenAnotherStrandSetsAHandle()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            AutoResetEvent b = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var index = -1;

            var waiter = ControlledThread.Create(() => index = ControlledWaitHandle.WaitAny([a, b]));
            var setter = ControlledThread.Create(() => ControlledEventWaitHandle.Set(b));

            ControlledThread.Start(waiter);
            ControlledThread.Start(setter);
            ControlledThread.Join(waiter);
            ControlledThread.Join(setter);

            Assert.Equal(1, index);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAnyCapturesAnAutoResetSignalThatIsResetBeforeTheSchedulerRepolls()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            AutoResetEvent b = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var index = -1;

            Thread waiter = ControlledThread.Create(() => index = ControlledWaitHandle.WaitAny([a, b], 100));
            Thread signaler = ControlledThread.Create(() =>
            {
                ControlledEventWaitHandle.Set(b);
                ControlledEventWaitHandle.Reset(b);
            });
            ControlledThread.Start(waiter);
            ControlledThread.Start(signaler);
            ControlledThread.Join(waiter);
            ControlledThread.Join(signaler);

            Assert.Equal(1, index);
        });
    }

    [Fact]
    public void WaitAllSucceedsOnlyWhenAllSignalled()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent a = ControlledEventWaitHandle.CreateManualResetEvent(initialState: true);
            AutoResetEvent b = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            // Not all signalled -> zero-timeout WaitAll fails and consumes nothing.
            Assert.False(ControlledWaitHandle.WaitAll([a, b], 0));
            Assert.True(ControlledWaitHandle.WaitOne(a, 0)); // 'a' was never consumed.

            ControlledEventWaitHandle.Set(b);
            Assert.True(ControlledWaitHandle.WaitAll([a, b], 0)); // Both signalled now.
            Assert.False(ControlledWaitHandle.WaitOne(b, 0)); // 'b' (auto-reset) was consumed atomically.
        });
    }

    [Fact]
    public void WaitAllWakesWhenAllHandlesEventuallySet()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            AutoResetEvent b = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var all = false;

            var waiter = ControlledThread.Create(() => all = ControlledWaitHandle.WaitAll([a, b]));
            var setter = ControlledThread.Create(() =>
            {
                ControlledEventWaitHandle.Set(a);
                ControlledEventWaitHandle.Set(b);
            });

            ControlledThread.Start(waiter);
            ControlledThread.Start(setter);
            ControlledThread.Join(waiter);
            ControlledThread.Join(setter);

            Assert.True(all);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAllCompletesWhenAllHandlesAreBrieflySignalledTogether()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent a = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            ManualResetEvent b = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            var completed = false;

            Thread waiter = ControlledThread.Create(() => completed = ControlledWaitHandle.WaitAll([a, b], 100));
            Thread signaler = ControlledThread.Create(() =>
            {
                ControlledEventWaitHandle.Set(a);
                ControlledEventWaitHandle.Set(b);
                ControlledEventWaitHandle.Reset(a);
                ControlledEventWaitHandle.Reset(b);
            });
            ControlledThread.Start(waiter);
            ControlledThread.Start(signaler);
            ControlledThread.Join(waiter);
            ControlledThread.Join(signaler);

            Assert.True(completed);
        });
    }

    [Fact]
    public void WaitAllRejectsDuplicateHandles()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            Assert.Throws<DuplicateWaitObjectException>(() => ControlledWaitHandle.WaitAll([a, a]));
        });
    }

    [Fact]
    public void WaitAnyAllowsDuplicateHandles()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);
            Assert.Equal(0, ControlledWaitHandle.WaitAny([a, a]));
        });
    }

    [Fact]
    public void WaitAnyRejectsEmptyArray()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<ArgumentException>(() => ControlledWaitHandle.WaitAny([]));
        });
    }

    [Fact]
    public void WaitAllRejectsNullElement()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            AutoResetEvent a = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            Assert.Throws<ArgumentNullException>(() => ControlledWaitHandle.WaitAll([a, null!]));
        });
    }

    [Fact]
    public void SignalAndWaitSignalsFirstThenWaitsOnSecond()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent gate = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            AutoResetEvent proceed = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
            var order = new List<int>();

            var partner = ControlledThread.Create(() =>
            {
                ControlledWaitHandle.WaitOne(gate); // Wait until SignalAndWait sets the gate.
                order.Add(1);
                ControlledEventWaitHandle.Set(proceed); // Release the SignalAndWait caller.
            });
            ControlledThread.Start(partner);

            // Atomically signal 'gate' (waking the partner) then wait on 'proceed'.
            Assert.True(ControlledWaitHandle.SignalAndWait(gate, proceed));
            order.Add(2);

            ControlledThread.Join(partner);
            Assert.Equal([1, 2], order);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void SignalAndWaitTimesOutOnSecondHandle()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent gate = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            AutoResetEvent proceed = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            Assert.False(ControlledWaitHandle.SignalAndWait(gate, proceed, 100, exitContext: false));
            Assert.True(ControlledWaitHandle.WaitOne(gate, 0)); // The gate was still signalled.
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void SignalAndWaitValidatesTheWaitTargetBeforeSignallingAndCanReleaseASemaphore()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEvent gate = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);
            using var unknown = new AutoResetEvent(false);
            Assert.Throws<SimulationApiException>(
                () => ControlledWaitHandle.SignalAndWait(gate, unknown, 0, exitContext: false));
            Assert.False(ControlledWaitHandle.WaitOne(gate, 0));

            Semaphore semaphore = ControlledSemaphore.Create(initialCount: 0, maximumCount: 1);
            ManualResetEvent proceed = ControlledEventWaitHandle.CreateManualResetEvent(initialState: true);
            Assert.True(ControlledWaitHandle.SignalAndWait(semaphore, proceed, 0, exitContext: false));
            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));

            Semaphore full = ControlledSemaphore.Create(initialCount: 1, maximumCount: 1);
            Assert.Throws<SemaphoreFullException>(
                () => ControlledWaitHandle.SignalAndWait(full, proceed, 0, exitContext: false));
        });
    }

    // A "virtual delay" built from a never-set finite Wait sequences an action at a chosen virtual instant,
    // with no wall-clock time anywhere, for exact before/at/after timeout coverage.
    private static void VirtualDelayThenRun(int delayMilliseconds, Action action)
    {
        AutoResetEvent timer = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);
        Assert.False(ControlledWaitHandle.WaitOne(timer, delayMilliseconds));
        action();
    }
}
