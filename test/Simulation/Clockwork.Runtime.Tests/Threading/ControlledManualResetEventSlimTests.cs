using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

public sealed class ControlledManualResetEventSlimTests
{
    [Fact]
    public void ConstructorsPreserveSignalAndSpinMetadata()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim unset = ControlledManualResetEventSlim.Create();
            ManualResetEventSlim set = ControlledManualResetEventSlim.Create(initialState: true);
            ManualResetEventSlim configured = ControlledManualResetEventSlim.Create(initialState: false, spinCount: 2047);

            Assert.False(ControlledManualResetEventSlim.IsSet(unset));
            Assert.True(ControlledManualResetEventSlim.IsSet(set));
            Assert.Equal(2047, ControlledManualResetEventSlim.SpinCount(configured));
            Assert.False(ControlledManualResetEventSlim.Wait(configured, 0));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2048)]
    public void ConstructorRejectsInvalidSpinCount(int spinCount)
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledManualResetEventSlim.Create(false, spinCount));
            Assert.Equal("spinCount", exception.ParamName);
        });
    }

    [Fact]
    public void SetResetAndIsSetFollowManualResetSemantics()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false);

            ControlledManualResetEventSlim.Set(evt);
            Assert.True(ControlledManualResetEventSlim.IsSet(evt));
            Assert.True(ControlledManualResetEventSlim.Wait(evt, 0));
            Assert.True(ControlledManualResetEventSlim.Wait(evt, 0));

            ControlledManualResetEventSlim.Reset(evt);
            Assert.False(ControlledManualResetEventSlim.IsSet(evt));
            Assert.False(ControlledManualResetEventSlim.Wait(evt, 0));
        });
    }

    [Fact]
    public void AllWaitOverloadsUseTheControlledSignal()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(true);

            ControlledManualResetEventSlim.Wait(evt);
            ControlledManualResetEventSlim.Wait(evt, CancellationToken.None);
            Assert.True(ControlledManualResetEventSlim.Wait(evt, 0));
            Assert.True(ControlledManualResetEventSlim.Wait(evt, 0, CancellationToken.None));
            Assert.True(ControlledManualResetEventSlim.Wait(evt, TimeSpan.Zero));
            Assert.True(ControlledManualResetEventSlim.Wait(evt, TimeSpan.Zero, CancellationToken.None));
        });
    }

    [Fact]
    public void ZeroAndFiniteWaitsUseVirtualTimeWithoutBusySpinning()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false, 2047);

            Assert.False(ControlledManualResetEventSlim.Wait(evt, 0));
            Assert.False(ControlledManualResetEventSlim.Wait(evt, TimeSpan.FromMilliseconds(25)));

            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void CancellationWinsBeforeSignalAndInvalidIntegerTimeout()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(true);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => ControlledManualResetEventSlim.Wait(evt, cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => ControlledManualResetEventSlim.Wait(evt, -2, cancellation.Token));
        });
    }

    [Fact]
    public void InvalidTimeSpanIsValidatedBeforeCancellation()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledManualResetEventSlim.Wait(evt, TimeSpan.FromDays(1000), cancellation.Token));
            Assert.Equal("timeout", exception.ParamName);
        });
    }

    [Fact]
    public void SignalBeforeDeadlineWinsAndReleasesAllWaiters()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false);
            var outcomes = new List<bool>();
            var first = ControlledThread.Create(() => outcomes.Add(ControlledManualResetEventSlim.Wait(evt, 500)));
            var second = ControlledThread.Create(() => outcomes.Add(ControlledManualResetEventSlim.Wait(evt, 500)));
            var setter = ControlledThread.Create(() => VirtualDelayThenRun(100, () => ControlledManualResetEventSlim.Set(evt)));

            ControlledThread.Start(first);
            ControlledThread.Start(second);
            ControlledThread.Start(setter);
            ControlledThread.Join(first);
            ControlledThread.Join(second);
            ControlledThread.Join(setter);

            Assert.Equal([true, true], outcomes);
            Assert.True(ControlledManualResetEventSlim.IsSet(evt));
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void TimeoutAndCancellationRaceResolveOneWinnerAndCleanDeadline()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false);
            using var cancellation = new CancellationTokenSource();
            Exception? caught = null;
            var waiter = ControlledThread.Create(() =>
            {
                try
                {
                    _ = ControlledManualResetEventSlim.Wait(evt, 500, cancellation.Token);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });
            var canceler = ControlledThread.Create(() => VirtualDelayThenRun(100, cancellation.Cancel));

            ControlledThread.Start(waiter);
            ControlledThread.Start(canceler);
            ControlledThread.Join(waiter);
            ControlledThread.Join(canceler);

            Assert.IsAssignableFrom<OperationCanceledException>(caught);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitHandleBridgeIsCachedTracksSignalAndIsDisposedWithEvent()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false);
            WaitHandle bridge = ControlledManualResetEventSlim.WaitHandle(evt);

            Assert.Same(bridge, ControlledManualResetEventSlim.WaitHandle(evt));
            Assert.False(ControlledWaitHandle.WaitOne(bridge, 0));

            var callbacks = 0;
            ControlledThreadPool.RegisterWaitForSingleObject(
                bridge,
                (_, timedOut) =>
                {
                    Assert.False(timedOut);
                    callbacks++;
                },
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: true);

            ControlledManualResetEventSlim.Set(evt);
            Assert.True(ControlledWaitHandle.WaitOne(bridge, 0));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(1, callbacks);

            ControlledManualResetEventSlim.Reset(evt);
            Assert.False(ControlledWaitHandle.WaitOne(bridge, 0));

            ControlledManualResetEventSlim.Dispose(evt);
            Assert.Throws<ObjectDisposedException>(() => ControlledManualResetEventSlim.WaitHandle(evt));
            Assert.Throws<ObjectDisposedException>(() => ControlledWaitHandle.WaitOne(bridge, 0));
        });
    }

    [Fact]
    public void DisposeFaultsBlockedWaiterAndPreservesReadableMetadata()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ManualResetEventSlim evt = ControlledManualResetEventSlim.Create(false);
            Exception? caught = null;
            var waiter = ControlledThread.Create(() =>
            {
                try
                {
                    ControlledManualResetEventSlim.Wait(evt);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });

            ControlledThread.Start(waiter);
            ControlledManualResetEventSlim.Dispose(evt);
            ControlledThread.Join(waiter);

            Assert.IsType<ObjectDisposedException>(caught);
            Assert.False(ControlledManualResetEventSlim.IsSet(evt));
            _ = ControlledManualResetEventSlim.SpinCount(evt);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void InactiveSimulationGuardRunsBeforeCreatingOrUsingEvent()
    {
        ManualResetEventSlim? created = null;
        Exception? createException = Record.Exception(() => created = ControlledManualResetEventSlim.Create());
        Assert.Null(created);
        SimulationNotActiveExceptionAssert.Equal(createException, "System.Threading.ManualResetEventSlim..ctor");

        using var raw = new ManualResetEventSlim(false);
        Exception? setException = Record.Exception(() => ControlledManualResetEventSlim.Set(raw));
        SimulationNotActiveExceptionAssert.Equal(setException, "System.Threading.ManualResetEventSlim.Set");
    }

    private static void VirtualDelayThenRun(int delayMilliseconds, Action action)
    {
        ManualResetEventSlim timer = ControlledManualResetEventSlim.Create(false);
        Assert.False(ControlledManualResetEventSlim.Wait(timer, delayMilliseconds));
        action();
    }
}
