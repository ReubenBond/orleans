using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

public sealed class ControlledSpinLockTests
{
    [Fact]
    public void DefaultAndConstructedLocksExposeSpinLockPropertiesAndCopySemantics()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ControlledSpinLock @default = default;
            Assert.True(@default.IsThreadOwnerTrackingEnabled);
            Assert.False(@default.IsHeld);
            Assert.False(@default.IsHeldByCurrentThread);

            var disabled = new ControlledSpinLock(enableThreadOwnerTracking: false);
            Assert.False(disabled.IsThreadOwnerTrackingEnabled);
            Assert.Throws<InvalidOperationException>(() => _ = disabled.IsHeldByCurrentThread);

            var tracked = new ControlledSpinLock(enableThreadOwnerTracking: true);
            Assert.True(tracked.IsThreadOwnerTrackingEnabled);
            Assert.False(tracked.IsHeld);
            Assert.False(tracked.IsHeldByCurrentThread);

            var taken = false;
            tracked.Enter(ref taken);
            ControlledSpinLock copy = tracked;
            Assert.True(copy.IsHeld);
            Assert.True(copy.IsHeldByCurrentThread);
            copy.Exit();
            Assert.True(tracked.IsHeld);
            tracked.Exit();
        });
    }

    [Fact]
    public void ImmediateEnterAndExitMutateTheReferencedStruct()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledSpinLock(enableThreadOwnerTracking: true);
            var taken = false;
            gate.Enter(ref taken);

            Assert.True(taken);
            Assert.True(gate.IsHeld);
            Assert.True(gate.IsHeldByCurrentThread);
            gate.Exit(useMemoryBarrier: false);
            Assert.False(gate.IsHeld);
            Assert.False(gate.IsHeldByCurrentThread);

            taken = false;
            gate.TryEnter(ref taken);
            Assert.True(taken);
            gate.Exit();
        });
    }

    [Fact]
    public void RefLockTakenValidationAndTimeoutValidationMatchContracts()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledSpinLock(enableThreadOwnerTracking: false);
            var alreadyTaken = true;

            var alreadyTakenException = Assert.Throws<ArgumentException>(() => gate.TryEnter(-2, ref alreadyTaken));
            Assert.Equal("lockTaken", alreadyTakenException.ParamName);
            Assert.True(alreadyTaken);

            var notTaken = false;
            var timeoutException = Assert.Throws<ArgumentOutOfRangeException>(() => gate.TryEnter(-2, ref notTaken));
            Assert.Equal("millisecondsTimeout", timeoutException.ParamName);
            Assert.False(notTaken);

            timeoutException = Assert.Throws<ArgumentOutOfRangeException>(
                () => gate.TryEnter(TimeSpan.FromMilliseconds(-2), ref notTaken));
            Assert.Equal("timeout", timeoutException.ParamName);
            Assert.False(notTaken);

            gate.Enter(ref notTaken);
            Assert.True(notTaken);
            var contended = false;
            gate.TryEnter(0, ref contended);
            Assert.False(contended);
            gate.Exit();

            // SpinLock truncates a TimeSpan to milliseconds before validation, so this becomes its
            // infinite (-1) timeout instead of being rejected as a value below -1 ms.
            var convertedTaken = false;
            gate.TryEnter(TimeSpan.FromTicks(-10_001), ref convertedTaken);
            Assert.True(convertedTaken);
            gate.Exit();
        });
    }

    [Fact]
    public void ContendedEnterCooperativelyPumpsUntilAnotherStrandReleases()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledSpinLock(enableThreadOwnerTracking: false);
            var ownerTaken = false;
            gate.Enter(ref ownerTaken);

            var contenderTaken = false;
            Thread contender = ControlledThread.Create(() => gate.TryEnter(Timeout.Infinite, ref contenderTaken));
            Thread releaser = ControlledThread.Create(() => gate.Exit());
            ControlledThread.Start(contender);
            ControlledThread.Start(releaser);
            ControlledThread.Join(contender);

            Assert.True(contenderTaken);
            Assert.True(gate.IsHeld);
            gate.Exit();
            ControlledThread.Join(releaser);
        });
    }

    [Fact]
    public void FiniteTimeoutLeavesLockTakenFalse()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledSpinLock(enableThreadOwnerTracking: false);
            var ownerTaken = false;
            gate.Enter(ref ownerTaken);

            var contenderTaken = true;
            Thread contender = ControlledThread.Create(() =>
            {
                contenderTaken = false;
                gate.TryEnter(TimeSpan.FromMilliseconds(10), ref contenderTaken);
            });
            ControlledThread.Start(contender);
            ControlledThread.Join(contender);

            Assert.False(contenderTaken);
            Assert.True(gate.IsHeld);
            gate.Exit();
        });
    }

    [Fact]
    public void OwnerTrackingRejectsRecursionAndNonOwnerExit()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var gate = new ControlledSpinLock(enableThreadOwnerTracking: true);
            var taken = false;
            gate.Enter(ref taken);

            var recursiveTaken = false;
            Assert.Throws<LockRecursionException>(() => gate.TryEnter(ref recursiveTaken));
            Assert.False(recursiveTaken);
            Assert.Throws<LockRecursionException>(() => gate.Enter(ref recursiveTaken));
            Assert.False(recursiveTaken);

            Exception? nonOwnerException = null;
            var isHeldByNonOwner = true;
            Thread nonOwner = ControlledThread.Create(() =>
            {
                isHeldByNonOwner = gate.IsHeldByCurrentThread;
                nonOwnerException = Record.Exception(gate.Exit);
            });
            ControlledThread.Start(nonOwner);
            ControlledThread.Join(nonOwner);

            Assert.False(isHeldByNonOwner);
            Assert.IsType<SynchronizationLockException>(nonOwnerException);
            Assert.True(gate.IsHeldByCurrentThread);
            gate.Exit();
        });
    }

    [Fact]
    public void ControlledEntriesAssertAnActiveSimulationBeforeMutation()
    {
        ControlledSpinLock? constructed = null;
        Exception? constructorException = Record.Exception(
            () => constructed = new ControlledSpinLock(enableThreadOwnerTracking: true));

        Assert.Null(constructed);
        SimulationNotActiveExceptionAssert.Equal(constructorException, "System.Threading.SpinLock..ctor");

        var gate = default(ControlledSpinLock);
        Exception? propertyException = Record.Exception(() => _ = gate.IsHeld);
        SimulationNotActiveExceptionAssert.Equal(propertyException, "System.Threading.SpinLock.get_IsHeld");

        var taken = false;
        Exception? enterException = Record.Exception(() => gate.Enter(ref taken));
        Assert.False(taken);
        SimulationNotActiveExceptionAssert.Equal(enterException, "System.Threading.SpinLock.Enter");
    }
}
