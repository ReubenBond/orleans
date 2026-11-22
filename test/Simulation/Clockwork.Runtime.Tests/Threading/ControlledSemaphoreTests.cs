using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

public sealed class ControlledSemaphoreTests
{
    [Fact]
    public void WaitOneConsumesPermitsAndReleaseReturnsPreviousCount()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(initialCount: 2, maximumCount: 3);

            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.Equal(0, ControlledSemaphore.Release(semaphore));
            Assert.Equal(1, ControlledSemaphore.Release(semaphore, 2));

            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 0));
        });
    }

    [Fact]
    public void InvalidCountsAndReleaseCountPreserveArgumentNames()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Equal("initialCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledSemaphore.Create(-1, 1)).ParamName);
            Assert.Equal("maximumCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledSemaphore.Create(0, 0)).ParamName);
            Assert.Null(Assert.Throws<ArgumentException>(
                () => ControlledSemaphore.Create(2, 1)).ParamName);

            Semaphore semaphore = ControlledSemaphore.Create(0, 1);
            Assert.Equal("releaseCount", Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledSemaphore.Release(semaphore, 0)).ParamName);
        });
    }

    [Fact]
    public void FiniteAndZeroWaitsObserveModelledCount()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(0, 1);

            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 100));
            ControlledSemaphore.Release(semaphore);
            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
        });
    }

    [Fact]
    public void ContendersAcquireReleasedPermitsInFifoOrder()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(0, 1);
            var order = new List<int>();

            Thread first = ControlledThread.Create(() =>
            {
                Assert.True(ControlledWaitHandle.WaitOne(semaphore));
                order.Add(1);
            });
            Thread second = ControlledThread.Create(() =>
            {
                Assert.True(ControlledWaitHandle.WaitOne(semaphore));
                order.Add(2);
            });

            ControlledThread.Start(first);
            ControlledThread.Start(second);

            ControlledSemaphore.Release(semaphore);
            ControlledThread.Join(first);
            Assert.Equal([1], order);

            ControlledSemaphore.Release(semaphore);
            ControlledThread.Join(second);

            Assert.Equal([1, 2], order);
        });
    }

    [Fact]
    public void WaitAnySelectsLowestEligibleIndexAndConsumesSemaphorePermit()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(1, 1);
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);

            Assert.Equal(0, ControlledWaitHandle.WaitAny([semaphore, evt], 0));
            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    [Fact]
    public void WaitAllAtomicallyConsumesSemaphorePermitsAndEventSignals()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(0, 1);
            AutoResetEvent evt = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);

            Assert.False(ControlledWaitHandle.WaitAll([semaphore, evt], 0));
            Assert.True(ControlledWaitHandle.WaitOne(evt, 0));

            ControlledEventWaitHandle.Set(evt);
            ControlledSemaphore.Release(semaphore);
            Assert.True(ControlledWaitHandle.WaitAll([semaphore, evt], 0));
            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.False(ControlledWaitHandle.WaitOne(evt, 0));
        });
    }

    [Fact]
    public void RegisteredWaitConsumesPermitAndHonorsExecuteOnlyOnce()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(0, 2);
            var callbacks = 0;

            ControlledThreadPool.RegisterWaitForSingleObject(
                semaphore,
                (_, timedOut) =>
                {
                    Assert.False(timedOut);
                    callbacks++;
                },
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: true);

            ControlledSemaphore.Release(semaphore);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(1, callbacks);
            Assert.False(ControlledWaitHandle.WaitOne(semaphore, 0));

            ControlledSemaphore.Release(semaphore);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.Equal(1, callbacks);
            Assert.True(ControlledWaitHandle.WaitOne(semaphore, 0));
        });
    }

    [Fact]
    public void ReleaseOverflowThrowsSemaphoreFullException()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(1, 1);
            Assert.Throws<SemaphoreFullException>(() => ControlledSemaphore.Release(semaphore));
        });
    }

    [Fact]
    public void DisposeMakesWaitAndReleaseThrow()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore semaphore = ControlledSemaphore.Create(0, 1);
            ControlledWaitHandle.Dispose(semaphore);

            Assert.Throws<ObjectDisposedException>(() => ControlledWaitHandle.WaitOne(semaphore, 0));
            Assert.Throws<ObjectDisposedException>(() => ControlledSemaphore.Release(semaphore));
        });
    }

    [Fact]
    public void NullNamedConstructorsCreateUnnamedSemaphores()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Semaphore first = ControlledSemaphore.CreateNamed(1, 1, name: null);
            Semaphore second = ControlledSemaphore.CreateNamed(1, 1, name: null, out bool createdNew);
            Semaphore third = ControlledSemaphore.CreateNamed(1, 1, name: null, options: default);
            Semaphore fourth = ControlledSemaphore.CreateNamed(1, 1, name: null, options: default, out bool optionsCreatedNew);

            Assert.True(createdNew);
            Assert.True(optionsCreatedNew);
            Assert.True(ControlledWaitHandle.WaitOne(first, 0));
            Assert.True(ControlledWaitHandle.WaitOne(second, 0));
            Assert.True(ControlledWaitHandle.WaitOne(third, 0));
            Assert.True(ControlledWaitHandle.WaitOne(fourth, 0));
        });
    }

    [Fact]
    public void NamedAndOpenExistingFormsAreRejectedBeforeKernelUse()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.CreateNamed(0, 1, "clockwork-semaphore"));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.CreateNamed(0, 1, "clockwork-semaphore", out _));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.CreateNamed(0, 1, "clockwork-semaphore", default));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.CreateNamed(0, 1, "clockwork-semaphore", default, out _));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.OpenExisting("clockwork-semaphore"));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.OpenExisting("clockwork-semaphore", default));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.TryOpenExisting("clockwork-semaphore", out _));
            Assert.Throws<SimulationApiException>(() => ControlledSemaphore.TryOpenExisting("clockwork-semaphore", default, out _));
        });
    }

    [Fact]
    public void ConstructorFailsOutsideSimulationBeforeCreatingSemaphore()
    {
        Semaphore? semaphore = null;
        Exception? exception = Record.Exception(() => semaphore = ControlledSemaphore.Create(0, 1));

        Assert.Null(semaphore);
        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.Semaphore..ctor");
    }

    [Fact]
    public void ReleaseFailsOutsideSimulationBeforeValidatingReceiver()
    {
        Exception? exception = Record.Exception(() => ControlledSemaphore.Release(null!));

        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.Semaphore.Release");
    }
}
