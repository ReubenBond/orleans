using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

#pragma warning disable xUnit1051 // Exact timeout overloads are the subject under test.

public sealed class ControlledCountdownEventTests
{
    [Fact]
    public void CountsAddSignalResetAndAllWaitOverloadsFollowCountdownSemantics()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var countdown = new ControlledCountdownEvent(2);
            Assert.Equal(2, countdown.InitialCount);
            Assert.Equal(2, countdown.CurrentCount);
            Assert.False(countdown.IsSet);
            countdown.AddCount();
            countdown.AddCount(2);
            Assert.Equal(5, countdown.CurrentCount);
            Assert.True(countdown.TryAddCount());
            Assert.True(countdown.TryAddCount(2));
            Assert.Equal(8, countdown.CurrentCount);
            Assert.False(countdown.Signal(7));
            Assert.True(countdown.Signal());

            countdown.Wait();
            countdown.Wait(CancellationToken.None);
            Assert.True(countdown.Wait(0));
            Assert.True(countdown.Wait(0, CancellationToken.None));
            Assert.True(countdown.Wait(TimeSpan.Zero));
            Assert.True(countdown.Wait(TimeSpan.Zero, CancellationToken.None));
            Assert.False(countdown.TryAddCount());
            Assert.Throws<InvalidOperationException>(() => countdown.AddCount());

            countdown.Reset(3);
            Assert.Equal(3, countdown.InitialCount);
            Assert.Equal(3, countdown.CurrentCount);
            countdown.Signal(3);
            countdown.Reset();
            Assert.Equal(3, countdown.CurrentCount);
        });
    }

    [Fact]
    public void WaitsUseVirtualTimeoutCancellationAndSignalRaces()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var countdown = new ControlledCountdownEvent(1);
            Assert.False(countdown.Wait(0));
            Assert.False(countdown.Wait(TimeSpan.FromMilliseconds(10)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => countdown.Wait(10, cancellation.Token));

            bool? result = null;
            Thread waiter = ControlledThread.Create(() => result = countdown.Wait(100));
            ControlledThread.Start(waiter);
            Assert.True(countdown.Signal());
            ControlledThread.Join(waiter);
            Assert.True(result);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitHandleBridgeIsCachedTracksStateAndComposesWithRegisteredWaits()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var countdown = new ControlledCountdownEvent(1);
            WaitHandle bridge = countdown.WaitHandle;
            Assert.Same(bridge, countdown.WaitHandle);
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

            countdown.Signal();
            Assert.True(ControlledWaitHandle.WaitOne(bridge, 0));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(1, callbacks);

            countdown.Reset(1);
            Assert.False(ControlledWaitHandle.WaitOne(bridge, 0));
            countdown.Dispose();
            Assert.Throws<ObjectDisposedException>(() => countdown.WaitHandle);
            Assert.Throws<ObjectDisposedException>(() => ControlledWaitHandle.WaitOne(bridge, 0));
        });
    }

    [Fact]
    public void ValidationDisposalAndInactiveGuardsMatchControlledSurface()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Equal("initialCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => new ControlledCountdownEvent(-1)).ParamName);

            var countdown = new ControlledCountdownEvent(1);
            Assert.Equal("signalCount", Assert.Throws<ArgumentOutOfRangeException>(() => countdown.AddCount(0)).ParamName);
            Assert.Equal("signalCount", Assert.Throws<ArgumentOutOfRangeException>(() => countdown.Signal(0)).ParamName);
            Assert.Equal("millisecondsTimeout", Assert.Throws<ArgumentOutOfRangeException>(() => countdown.Wait(-2)).ParamName);
            Assert.Equal("timeout", Assert.Throws<ArgumentOutOfRangeException>(
                () => countdown.Wait(TimeSpan.FromDays(1000))).ParamName);
            countdown.Dispose();
            Assert.Throws<ObjectDisposedException>(() => countdown.Wait());
            Assert.Equal(1, countdown.CurrentCount);
        });

        ControlledCountdownEvent? created = null;
        Exception? exception = Record.Exception(() => created = new ControlledCountdownEvent(1));
        Assert.Null(created);
        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.CountdownEvent..ctor");
    }
}
