using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

public sealed class ControlledMutexTests
{
    [Fact]
    public void InitiallyOwnedMutexIsRecursiveAndBlocksAnotherStrand()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create(initiallyOwned: true);
            Assert.True(ControlledWaitHandle.WaitOne(mutex, 0));

            var acquired = true;
            Thread contender = ControlledThread.Create(() => acquired = ControlledWaitHandle.WaitOne(mutex, 0));
            ControlledThread.Start(contender);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.False(acquired);
            ControlledMutex.ReleaseMutex(mutex);
            ControlledMutex.ReleaseMutex(mutex);

            var afterRelease = false;
            Thread next = ControlledThread.Create(() =>
            {
                afterRelease = ControlledWaitHandle.WaitOne(mutex, 0);
                ControlledMutex.ReleaseMutex(mutex);
            });
            ControlledThread.Start(next);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(afterRelease);
        });
    }

    [Fact]
    public void ContendersAcquireInReleaseOrder()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create();
            var order = new List<int>();
            ManualResetEvent gate = ControlledEventWaitHandle.CreateManualResetEvent(initialState: false);

            Thread owner = ControlledThread.Create(() =>
            {
                Assert.True(ControlledWaitHandle.WaitOne(mutex, 0));
                order.Add(1);
                Assert.True(ControlledWaitHandle.SignalAndWait(mutex, gate));
                order.Add(4);
            });
            Thread first = ControlledThread.Create(() =>
            {
                Assert.True(ControlledWaitHandle.WaitOne(mutex));
                order.Add(2);
                ControlledEventWaitHandle.Set(gate);
                ControlledMutex.ReleaseMutex(mutex);
            });
            Thread second = ControlledThread.Create(() =>
            {
                Assert.True(ControlledWaitHandle.WaitOne(mutex));
                order.Add(3);
                ControlledMutex.ReleaseMutex(mutex);
            });

            ControlledThread.Start(owner);
            ControlledThread.Start(first);
            ControlledThread.Start(second);
            ControlledThread.Join(owner);
            ControlledThread.Join(first);
            ControlledThread.Join(second);

            Assert.Equal([1, 2, 3, 4], order);
        });
    }

    [Fact]
    public void NonOwnerReleaseThrowsApplicationException()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create(initiallyOwned: true);
            Exception? exception = null;
            Thread nonOwner = ControlledThread.Create(() => exception = Record.Exception(() => ControlledMutex.ReleaseMutex(mutex)));

            ControlledThread.Start(nonOwner);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.IsType<ApplicationException>(exception);
            ControlledMutex.ReleaseMutex(mutex);
        });
    }

    [Fact]
    public void FiniteWaitTimesOutUntilOwnerReleases()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create(initiallyOwned: true);
            var acquired = true;
            Thread contender = ControlledThread.Create(() => acquired = ControlledWaitHandle.WaitOne(mutex, 100));

            ControlledThread.Start(contender);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            coordinator.Scheduler.AdvanceVirtualTimeTo(TimeSpan.FromMilliseconds(100));
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.False(acquired);

            ControlledMutex.ReleaseMutex(mutex);
            Assert.True(ControlledWaitHandle.WaitOne(mutex, 0));
            ControlledMutex.ReleaseMutex(mutex);
        });
    }

    [Fact]
    public void WaitAnyAcquiresMutexAndSignalAndWaitReleasesIt()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create();
            AutoResetEvent other = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: false);

            Assert.Equal(0, ControlledWaitHandle.WaitAny([mutex, other], 0));
            ControlledMutex.ReleaseMutex(mutex);

            Assert.True(ControlledWaitHandle.WaitOne(mutex, 0));
            ControlledEventWaitHandle.Set(other);
            Assert.True(ControlledWaitHandle.SignalAndWait(mutex, other, 0, exitContext: false));

            var acquired = false;
            Thread contender = ControlledThread.Create(() =>
            {
                acquired = ControlledWaitHandle.WaitOne(mutex, 0);
                ControlledMutex.ReleaseMutex(mutex);
            });
            ControlledThread.Start(contender);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(acquired);
        });
    }

    [Fact]
    public void WaitAnyAcquiresMutexForTheWaitingStrandWhenAnotherStrandReleasesIt()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create(initiallyOwned: true);
            Exception? releaseException = null;
            Thread waiter = ControlledThread.Create(() =>
            {
                Assert.Equal(0, ControlledWaitHandle.WaitAny([mutex]));
                releaseException = Record.Exception(() => ControlledMutex.ReleaseMutex(mutex));
            });

            ControlledThread.Start(waiter);
            SimulationTaskRuntime.QueueWork(
                () => SimulationSynchronizationFlow.RunAsStrand(
                    SimulationSynchronizationFlow.None,
                    () => ControlledMutex.ReleaseMutex(mutex)),
                "test.release-mutex",
                flowExecutionContext: false);

            ControlledThread.Join(waiter);

            Assert.Null(releaseException);
            Assert.True(coordinator.Scheduler.IsIdle);
        });
    }

    [Fact]
    public void WaitAllWithMutexIsRejectedWithoutAcquiringOtherHandles()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create();
            AutoResetEvent signaled = ControlledEventWaitHandle.CreateAutoResetEvent(initialState: true);

            Assert.Throws<SimulationApiException>(() => ControlledWaitHandle.WaitAll([signaled, mutex], 0));
            Assert.True(ControlledWaitHandle.WaitOne(signaled, 0));
        });
    }

    [Fact]
    public void DisposeMakesWaitAndReleaseThrow()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create(initiallyOwned: true);
            ControlledWaitHandle.Dispose(mutex);

            Assert.Throws<ObjectDisposedException>(() => ControlledWaitHandle.WaitOne(mutex, 0));
            Assert.Throws<ObjectDisposedException>(() => ControlledMutex.ReleaseMutex(mutex));
        });
    }

    [Fact]
    public void NullNamedConstructorsCreateUnnamedMutexes()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex first = ControlledMutex.CreateNamed(false, name: null);
            Mutex second = ControlledMutex.CreateNamed(false, name: null, out bool createdNew);
            Mutex third = ControlledMutex.CreateNamed(false, name: null, options: default);
            Mutex fourth = ControlledMutex.CreateNamed(false, name: null, options: default, out bool optionsCreatedNew);

            Assert.True(createdNew);
            Assert.True(optionsCreatedNew);
            Assert.True(ControlledWaitHandle.WaitOne(first, 0));
            Assert.True(ControlledWaitHandle.WaitOne(second, 0));
            Assert.True(ControlledWaitHandle.WaitOne(third, 0));
            Assert.True(ControlledWaitHandle.WaitOne(fourth, 0));
            ControlledMutex.ReleaseMutex(first);
            ControlledMutex.ReleaseMutex(second);
            ControlledMutex.ReleaseMutex(third);
            ControlledMutex.ReleaseMutex(fourth);
        });
    }

    [Fact]
    public void NamedAndOpenExistingFormsAreRejected()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SimulationApiException>(() => ControlledMutex.CreateNamed(false, "clockwork-mutex"));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.CreateNamed(false, "clockwork-mutex", out _));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.CreateNamed(false, "clockwork-mutex", default));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.CreateNamed(false, "clockwork-mutex", default, out _));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.OpenExisting("clockwork-mutex"));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.OpenExisting("clockwork-mutex", default));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.TryOpenExisting("clockwork-mutex", out _));
            Assert.Throws<SimulationApiException>(() => ControlledMutex.TryOpenExisting("clockwork-mutex", default, out _));
        });
    }

    [Fact]
    public void OwnerExitWithoutReleaseLeavesLogicalOwnershipHeld()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Mutex mutex = ControlledMutex.Create();
            Thread owner = ControlledThread.Create(() => Assert.True(ControlledWaitHandle.WaitOne(mutex, 0)));

            ControlledThread.Start(owner);
            ControlledThread.Join(owner);

            // Deliberately no synthetic OS-abandonment: the lost logical owner keeps the mutex unavailable.
            Assert.False(ControlledWaitHandle.WaitOne(mutex, 0));
        });
    }

    [Fact]
    public void OutsideSimulationConstructorFailsBeforeCreatingMutex()
    {
        Mutex? mutex = null;
        Exception? exception = Record.Exception(() => mutex = ControlledMutex.Create());

        Assert.Null(mutex);
        SimulationNotActiveExceptionAssert.Equal(exception, "System.Threading.Mutex..ctor");
    }
}
