using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledSpinWait"/> value type. Inside a simulation a spin never
/// burns CPU or consumes real time: <see cref="ControlledSpinWait.SpinOnce()"/> only advances the observable
/// spin count and <see cref="ControlledSpinWait.SpinUntil(Func{bool})"/> pumps the deterministic loop.
/// Controlled entry points require an active simulation.
/// </summary>
public sealed class ControlledSpinWaitTests
{
    [Fact]
    public void SpinOnceAdvancesObservableCount()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var spin = new ControlledSpinWait();
            Assert.Equal(0, spin.Count);
            spin.SpinOnce();
            spin.SpinOnce();
            Assert.Equal(2, spin.Count);
            spin.Reset();
            Assert.Equal(0, spin.Count);
        });
    }

    [Fact]
    public void SpinOnceWithThresholdAdvancesCount()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var spin = new ControlledSpinWait();
            spin.SpinOnce(sleep1Threshold: -1);
            spin.SpinOnce(sleep1Threshold: 20);
            Assert.Equal(2, spin.Count);
        });
    }

    [Fact]
    public void SpinOnceRejectsThresholdBelowMinusOne()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var spin = new ControlledSpinWait();
            Assert.Throws<ArgumentOutOfRangeException>(() => spin.SpinOnce(-2));
        });
    }

    [Fact]
    public void NextSpinWillYieldFlipsPastThreshold()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var spin = new ControlledSpinWait();
            Assert.False(spin.NextSpinWillYield);
            for (int i = 0; i < 10; i++)
            {
                spin.SpinOnce();
            }

            Assert.True(spin.NextSpinWillYield);
        });
    }

    [Fact]
    public void SpinUntilReturnsWhenPredicateAlreadyHolds()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ControlledSpinWait.SpinUntil(() => true);
            Assert.True(ControlledSpinWait.SpinUntil(() => true, 0));
            Assert.True(ControlledSpinWait.SpinUntil(() => true, TimeSpan.Zero));
        });
    }

    [Fact]
    public void SpinUntilZeroTimeoutDoesNotBlock()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.False(ControlledSpinWait.SpinUntil(() => false, 0));
        });
    }

    [Fact]
    public void SpinUntilFiniteTimeoutElapsesWhenPredicateNeverHolds()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.False(ControlledSpinWait.SpinUntil(() => false, 25));
            Assert.False(ControlledSpinWait.SpinUntil(() => false, TimeSpan.FromMilliseconds(25)));
        });
    }

    [Fact]
    public void SpinUntilRejectsInvalidTimeout()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlledSpinWait.SpinUntil(() => true, -2));
            Assert.Throws<ArgumentNullException>(() => ControlledSpinWait.SpinUntil(null!));
        });
    }

    [Fact]
    public void OutsideSimulationFailsBeforeMutatingStateOrInvokingCondition()
    {
        var spin = new ControlledSpinWait();
        Exception? spinException = Record.Exception(() => spin.SpinOnce());

        Assert.Equal(default, spin);
        SimulationNotActiveExceptionAssert.Equal(
            spinException,
            "System.Threading.SpinWait.SpinOnce");

        var conditionInvoked = false;
        Exception? untilException = Record.Exception(
            () => ControlledSpinWait.SpinUntil(
                () =>
                {
                    conditionInvoked = true;
                    return true;
                },
                0));

        Assert.False(conditionInvoked);
        SimulationNotActiveExceptionAssert.Equal(
            untilException,
            "System.Threading.SpinWait.SpinUntil");
    }
}
