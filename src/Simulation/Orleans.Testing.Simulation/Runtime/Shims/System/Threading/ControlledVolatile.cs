using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Static shims for the <see cref="global::System.Threading.Volatile"/> acquire/release surface. The rewriter
/// redirects each <see cref="global::System.Threading.Volatile"/> call site to the matching static method here
/// whose signature (including the leading <c>ref</c> location) is identical to the BCL target, so the
/// exact value is read from — or written to — the caller's real memory location and the acquire (read) /
/// release (write) fence intent the BCL contract specifies is preserved for every overload.
/// </para>
/// <para>
/// <b>Exploration policy (documented).</b> Clockwork runs every controlled operation cooperatively on a
/// single logical thread, so a volatile read or write executes as one indivisible cooperative step and
/// the ordering guarantee the fence provides is never observable to another operation mid-flight. Like
/// the <see cref="ControlledInterlocked"/> shim (and mirroring Microsoft Coyote's, MIT-licensed,
/// controlled volatile surface) the active-simulation shim delegates straight to the real
/// <see cref="global::System.Threading.Volatile"/> primitive, which additionally preserves the exact value and
/// memory-order semantics. The single delegation point is where a
/// race-exploration access tracking attaches; it is intentionally left as a direct delegation today.
/// </para>
/// </summary>
public static class ControlledVolatile
{
    /// <summary>Controlled <c>Volatile.Read(ref bool)</c>.</summary>
    public static bool Read(ref bool location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref sbyte)</c>.</summary>
    public static sbyte Read(ref sbyte location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref byte)</c>.</summary>
    public static byte Read(ref byte location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref short)</c>.</summary>
    public static short Read(ref short location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref ushort)</c>.</summary>
    public static ushort Read(ref ushort location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref int)</c>.</summary>
    public static int Read(ref int location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref uint)</c>.</summary>
    public static uint Read(ref uint location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref long)</c>.</summary>
    public static long Read(ref long location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref ulong)</c>.</summary>
    public static ulong Read(ref ulong location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref IntPtr)</c>.</summary>
    public static IntPtr Read(ref IntPtr location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref UIntPtr)</c>.</summary>
    public static UIntPtr Read(ref UIntPtr location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref float)</c>.</summary>
    public static float Read(ref float location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read(ref double)</c>.</summary>
    public static double Read(ref double location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Read&lt;T&gt;(ref T)</c> for reference types.</summary>
    public static T Read<T>(ref T location) where T : class? => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Read"), Volatile.Read(ref location)).Item2;

    /// <summary>Controlled <c>Volatile.Write(ref bool, bool)</c>.</summary>
    public static void Write(ref bool location, bool value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref sbyte, sbyte)</c>.</summary>
    public static void Write(ref sbyte location, sbyte value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref byte, byte)</c>.</summary>
    public static void Write(ref byte location, byte value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref short, short)</c>.</summary>
    public static void Write(ref short location, short value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref ushort, ushort)</c>.</summary>
    public static void Write(ref ushort location, ushort value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref int, int)</c>.</summary>
    public static void Write(ref int location, int value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref uint, uint)</c>.</summary>
    public static void Write(ref uint location, uint value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref long, long)</c>.</summary>
    public static void Write(ref long location, long value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref ulong, ulong)</c>.</summary>
    public static void Write(ref ulong location, ulong value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref IntPtr, IntPtr)</c>.</summary>
    public static void Write(ref IntPtr location, IntPtr value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref UIntPtr, UIntPtr)</c>.</summary>
    public static void Write(ref UIntPtr location, UIntPtr value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref float, float)</c>.</summary>
    public static void Write(ref float location, float value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write(ref double, double)</c>.</summary>
    public static void Write(ref double location, double value) { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.Write&lt;T&gt;(ref T, T)</c> for reference types.</summary>
    public static void Write<T>(ref T location, T value) where T : class? { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.Write"); Volatile.Write(ref location, value); }

    /// <summary>Controlled <c>Volatile.ReadBarrier()</c> (acquire fence).</summary>
    public static void ReadBarrier() { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.ReadBarrier"); Volatile.ReadBarrier(); }

    /// <summary>Controlled <c>Volatile.WriteBarrier()</c> (release fence).</summary>
    public static void WriteBarrier() { SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Volatile.WriteBarrier"); Volatile.WriteBarrier(); }
}
