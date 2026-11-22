using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledInterlocked"/> shims. Under Clockwork's cooperative
/// single-logical-thread scheduler each read-modify-write is an indivisible step, so every shim delegates
/// to the real <see cref="Interlocked"/> primitive and must preserve the exact atomic return, overflow,
/// and reference-write semantics under an active simulation.
/// </summary>
public sealed class ControlledInterlockedTests
{
    [Fact]
    public void IncrementDecrementReturnUpdatedValue()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            int i32 = 0;
            Assert.Equal(1, ControlledInterlocked.Increment(ref i32));
            Assert.Equal(1, i32);
            Assert.Equal(0, ControlledInterlocked.Decrement(ref i32));
            Assert.Equal(0, i32);

            long i64 = 0;
            Assert.Equal(1L, ControlledInterlocked.Increment(ref i64));
            Assert.Equal(0L, ControlledInterlocked.Decrement(ref i64));

            uint u32 = 0;
            Assert.Equal(1u, ControlledInterlocked.Increment(ref u32));
            Assert.Equal(0u, ControlledInterlocked.Decrement(ref u32));

            ulong u64 = 0;
            Assert.Equal(1ul, ControlledInterlocked.Increment(ref u64));
            Assert.Equal(0ul, ControlledInterlocked.Decrement(ref u64));
        });
    }

    [Fact]
    public void AddReturnsSumAndStoresIt()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            int i32 = 10;
            Assert.Equal(17, ControlledInterlocked.Add(ref i32, 7));
            Assert.Equal(17, i32);

            long i64 = 10;
            Assert.Equal(17L, ControlledInterlocked.Add(ref i64, 7));

            uint u32 = 10;
            Assert.Equal(17u, ControlledInterlocked.Add(ref u32, 7));

            ulong u64 = 10;
            Assert.Equal(17ul, ControlledInterlocked.Add(ref u64, 7));
        });
    }

    [Fact]
    public void AndOrApplyBitwiseAndReturnOriginal()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            int and = 0b1111;
            Assert.Equal(0b1111, ControlledInterlocked.And(ref and, 0b1010));
            Assert.Equal(0b1010, and);

            int or = 0b0100;
            Assert.Equal(0b0100, ControlledInterlocked.Or(ref or, 0b0001));
            Assert.Equal(0b0101, or);

            long andL = 0b1111;
            Assert.Equal(0b1111L, ControlledInterlocked.And(ref andL, 0b1010));
            Assert.Equal(0b1010L, andL);
        });
    }

    [Fact]
    public void ExchangeReturnsOriginalAndStoresNew()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            int i32 = 5;
            Assert.Equal(5, ControlledInterlocked.Exchange(ref i32, 9));
            Assert.Equal(9, i32);

            double d = 1.5;
            Assert.Equal(1.5, ControlledInterlocked.Exchange(ref d, 2.5));
            Assert.Equal(2.5, d);

            object? o = "a";
            var previous = ControlledInterlocked.Exchange(ref o, "b");
            Assert.Equal("a", previous);
            Assert.Equal("b", o);
        });
    }

    [Fact]
    public void CompareExchangeSwapsOnlyWhenComparandMatches()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            int i32 = 7;
            Assert.Equal(7, ControlledInterlocked.CompareExchange(ref i32, 42, 7));
            Assert.Equal(42, i32);

            Assert.Equal(42, ControlledInterlocked.CompareExchange(ref i32, 99, 7));
            Assert.Equal(42, i32);

            string? s = "x";
            var swapped = ControlledInterlocked.CompareExchange(ref s, "y", "x");
            Assert.Equal("x", swapped);
            Assert.Equal("y", s);
        });
    }

    [Fact]
    public void GenericExchangeAndCompareExchangeOperateOnReferences()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var a = new object();
            var b = new object();
            object? slot = a;

            Assert.Same(a, ControlledInterlocked.Exchange(ref slot, b));
            Assert.Same(b, slot);

            Assert.Same(b, ControlledInterlocked.CompareExchange(ref slot, a, b));
            Assert.Same(a, slot);
        });
    }

    [Fact]
    public void ReadReturnsCurrentValue()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            long i64 = 123;
            Assert.Equal(123L, ControlledInterlocked.Read(ref i64));

            ulong u64 = 456;
            Assert.Equal(456ul, ControlledInterlocked.Read(ref u64));
        });
    }

    [Fact]
    public void OutsideSimulationIncrementFailsBeforeMutatingState()
    {
        int value = 41;

        Exception? exception = Record.Exception(
            () => ControlledInterlocked.Increment(ref value));

        Assert.Equal(41, value);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.Interlocked.Increment");
    }
}
