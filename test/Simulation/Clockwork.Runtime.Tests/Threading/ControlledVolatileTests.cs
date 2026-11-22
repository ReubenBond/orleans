using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tests.Threading;

/// <summary>
/// Tests for the controlled <see cref="ControlledVolatile"/> shims. Each shim delegates to the real
/// <see cref="Volatile"/> primitive inside simulation and must preserve the exact value read/written and
/// the acquire/release fence intent.
/// </summary>
public sealed class ControlledVolatileTests
{
    [Fact]
    public void ReadReturnsWrittenValueForEveryPrimitive()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            bool b = true; Assert.True(ControlledVolatile.Read(ref b));
            sbyte sb = -5; Assert.Equal((sbyte)-5, ControlledVolatile.Read(ref sb));
            byte by = 5; Assert.Equal((byte)5, ControlledVolatile.Read(ref by));
            short s = -7; Assert.Equal((short)-7, ControlledVolatile.Read(ref s));
            ushort us = 7; Assert.Equal((ushort)7, ControlledVolatile.Read(ref us));
            int i = -11; Assert.Equal(-11, ControlledVolatile.Read(ref i));
            uint ui = 11; Assert.Equal(11u, ControlledVolatile.Read(ref ui));
            long l = -13; Assert.Equal(-13L, ControlledVolatile.Read(ref l));
            ulong ul = 13; Assert.Equal(13ul, ControlledVolatile.Read(ref ul));
            IntPtr ip = 17; Assert.Equal((IntPtr)17, ControlledVolatile.Read(ref ip));
            UIntPtr up = 19; Assert.Equal((UIntPtr)19, ControlledVolatile.Read(ref up));
            float f = 1.5f; Assert.Equal(1.5f, ControlledVolatile.Read(ref f));
            double d = 2.5; Assert.Equal(2.5, ControlledVolatile.Read(ref d));
        });
    }

    [Fact]
    public void WriteStoresValueForEveryPrimitive()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            bool b = false; ControlledVolatile.Write(ref b, true); Assert.True(b);
            int i = 0; ControlledVolatile.Write(ref i, 42); Assert.Equal(42, i);
            long l = 0; ControlledVolatile.Write(ref l, 42); Assert.Equal(42L, l);
            double d = 0; ControlledVolatile.Write(ref d, 3.5); Assert.Equal(3.5, d);
            IntPtr ip = 0; ControlledVolatile.Write(ref ip, 9); Assert.Equal((IntPtr)9, ip);
        });
    }

    [Fact]
    public void GenericReadWriteOperateOnReferences()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var value = new object();
            object? slot = null;
            ControlledVolatile.Write(ref slot, value);
            Assert.Same(value, ControlledVolatile.Read(ref slot));
        });
    }

    [Fact]
    public void BarriersAreNoThrow()
    {
        var coordinator = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            ControlledVolatile.ReadBarrier();
            ControlledVolatile.WriteBarrier();
        });
    }

    [Fact]
    public void OutsideSimulationWriteFailsBeforeMutatingState()
    {
        int i = 0;

        Exception? exception = Record.Exception(() => ControlledVolatile.Write(ref i, 7));

        Assert.Equal(0, i);
        SimulationNotActiveExceptionAssert.Equal(
            exception,
            "System.Threading.Volatile.Write");
    }
}
