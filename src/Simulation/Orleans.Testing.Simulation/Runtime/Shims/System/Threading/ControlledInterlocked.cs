using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Static shims for the <see cref="global::System.Threading.Interlocked"/> atomic surface. The rewriter
/// redirects each <see cref="global::System.Threading.Interlocked"/> call site to the matching static method
/// here whose signature (including the leading <c>ref</c> location) is identical to the BCL target, so
/// the atomic read-modify-write is performed against the caller's real memory location and the exact
/// return value the BCL contract specifies is preserved for every overload.
/// </para>
/// <para>
/// <b>Exploration policy (documented).</b> Clockwork runs every controlled operation cooperatively on a
/// single logical thread, so no operation is ever preempted <em>inside</em> a synchronous method body.
/// An interlocked read-modify-write therefore executes as one indivisible cooperative step - it can
/// never be split, and no other operation can observe a half-applied value - without Clockwork having to
/// take a lock or insert an interleaving point mid-operation. The natural scheduling points remain the
/// surrounding <c>await</c>/yield boundaries the controlled machinery already models. This is the key
/// difference from Microsoft Coyote (MIT-licensed), whose controlled <c>Interlocked</c> must insert a
/// scheduling point before delegating because Coyote schedules real, preemptible OS threads; Clockwork's
/// cooperative single-logical-thread model makes the atomic guarantee free, so the active-simulation shim delegates
/// straight to the real <see cref="global::System.Threading.Interlocked"/> primitive (which additionally gives
/// exact overflow, memory-order, and reference-write semantics).
/// The single delegation point is where a race-exploration access tracking attaches; it is intentionally
/// left as a direct delegation today so no atomic operation is ever split.
/// </para>
/// </summary>
public static class ControlledInterlocked
{
    /// <summary>Controlled <see cref="Interlocked.Increment(ref int)"/>.</summary>
    /// <param name="location">The value to increment.</param>
    /// <returns>The incremented value.</returns>
    public static int Increment(ref int location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Increment"), Interlocked.Increment(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Increment(ref long)"/>.</summary>
    /// <param name="location">The value to increment.</param>
    /// <returns>The incremented value.</returns>
    public static long Increment(ref long location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Increment"), Interlocked.Increment(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Increment(ref uint)"/>.</summary>
    /// <param name="location">The value to increment.</param>
    /// <returns>The incremented value.</returns>
    public static uint Increment(ref uint location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Increment"), Interlocked.Increment(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Increment(ref ulong)"/>.</summary>
    /// <param name="location">The value to increment.</param>
    /// <returns>The incremented value.</returns>
    public static ulong Increment(ref ulong location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Increment"), Interlocked.Increment(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Decrement(ref int)"/>.</summary>
    /// <param name="location">The value to decrement.</param>
    /// <returns>The decremented value.</returns>
    public static int Decrement(ref int location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Decrement"), Interlocked.Decrement(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Decrement(ref long)"/>.</summary>
    /// <param name="location">The value to decrement.</param>
    /// <returns>The decremented value.</returns>
    public static long Decrement(ref long location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Decrement"), Interlocked.Decrement(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Decrement(ref uint)"/>.</summary>
    /// <param name="location">The value to decrement.</param>
    /// <returns>The decremented value.</returns>
    public static uint Decrement(ref uint location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Decrement"), Interlocked.Decrement(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Decrement(ref ulong)"/>.</summary>
    /// <param name="location">The value to decrement.</param>
    /// <returns>The decremented value.</returns>
    public static ulong Decrement(ref ulong location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Decrement"), Interlocked.Decrement(ref location)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Add(ref int, int)"/>.</summary>
    /// <param name="location">The addend that receives the sum.</param>
    /// <param name="value">The value to add.</param>
    /// <returns>The new value stored at <paramref name="location"/>.</returns>
    public static int Add(ref int location, int value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Add"), Interlocked.Add(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Add(ref long, long)"/>.</summary>
    /// <param name="location">The addend that receives the sum.</param>
    /// <param name="value">The value to add.</param>
    /// <returns>The new value stored at <paramref name="location"/>.</returns>
    public static long Add(ref long location, long value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Add"), Interlocked.Add(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Add(ref uint, uint)"/>.</summary>
    /// <param name="location">The addend that receives the sum.</param>
    /// <param name="value">The value to add.</param>
    /// <returns>The new value stored at <paramref name="location"/>.</returns>
    public static uint Add(ref uint location, uint value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Add"), Interlocked.Add(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Add(ref ulong, ulong)"/>.</summary>
    /// <param name="location">The addend that receives the sum.</param>
    /// <param name="value">The value to add.</param>
    /// <returns>The new value stored at <paramref name="location"/>.</returns>
    public static ulong Add(ref ulong location, ulong value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Add"), Interlocked.Add(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.And(ref int, int)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise AND.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static int And(ref int location, int value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.And"), Interlocked.And(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.And(ref uint, uint)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise AND.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static uint And(ref uint location, uint value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.And"), Interlocked.And(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.And(ref long, long)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise AND.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static long And(ref long location, long value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.And"), Interlocked.And(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.And(ref ulong, ulong)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise AND.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static ulong And(ref ulong location, ulong value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.And"), Interlocked.And(ref location, value)).Item2;

    /// <summary>Controlled generic <see cref="Interlocked.And{T}(ref T, T)"/>.</summary>
    /// <typeparam name="T">The integer or enum type of the location.</typeparam>
    /// <param name="location">The value to combine with a bitwise AND.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static T And<T>(ref T location, T value) where T : struct => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.And"), Interlocked.And(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Or(ref int, int)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise OR.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static int Or(ref int location, int value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Or"), Interlocked.Or(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Or(ref uint, uint)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise OR.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static uint Or(ref uint location, uint value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Or"), Interlocked.Or(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Or(ref long, long)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise OR.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static long Or(ref long location, long value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Or"), Interlocked.Or(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Or(ref ulong, ulong)"/>.</summary>
    /// <param name="location">The value to combine with a bitwise OR.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static ulong Or(ref ulong location, ulong value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Or"), Interlocked.Or(ref location, value)).Item2;

    /// <summary>Controlled generic <see cref="Interlocked.Or{T}(ref T, T)"/>.</summary>
    /// <typeparam name="T">The integer or enum type of the location.</typeparam>
    /// <param name="location">The value to combine with a bitwise OR.</param>
    /// <param name="value">The mask.</param>
    /// <returns>The original value stored at <paramref name="location"/>.</returns>
    public static T Or<T>(ref T location, T value) where T : struct => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Or"), Interlocked.Or(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref int, int)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static int Exchange(ref int location, int value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref long, long)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static long Exchange(ref long location, long value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref object, object)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static object? Exchange(ref object? location, object? value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref sbyte, sbyte)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static sbyte Exchange(ref sbyte location, sbyte value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref short, short)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static short Exchange(ref short location, short value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref byte, byte)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static byte Exchange(ref byte location, byte value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref ushort, ushort)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static ushort Exchange(ref ushort location, ushort value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref uint, uint)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static uint Exchange(ref uint location, uint value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref ulong, ulong)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static ulong Exchange(ref ulong location, ulong value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref float, float)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static float Exchange(ref float location, float value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref double, double)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static double Exchange(ref double location, double value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref IntPtr, IntPtr)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static IntPtr Exchange(ref IntPtr location, IntPtr value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.Exchange(ref UIntPtr, UIntPtr)"/>.</summary>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static UIntPtr Exchange(ref UIntPtr location, UIntPtr value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled generic <see cref="Interlocked.Exchange{T}(ref T, T)"/>.</summary>
    /// <typeparam name="T">The reference, primitive, or enum type of the location.</typeparam>
    /// <param name="location">The location whose value is set to <paramref name="value"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>The original value.</returns>
    public static T Exchange<T>(ref T location, T value) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Exchange"), Interlocked.Exchange(ref location, value)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref int, int, int)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static int CompareExchange(ref int location, int value, int comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref long, long, long)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static long CompareExchange(ref long location, long value, long comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref object, object, object)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static object? CompareExchange(ref object? location, object? value, object? comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref sbyte, sbyte, sbyte)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static sbyte CompareExchange(ref sbyte location, sbyte value, sbyte comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref short, short, short)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static short CompareExchange(ref short location, short value, short comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref byte, byte, byte)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static byte CompareExchange(ref byte location, byte value, byte comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref ushort, ushort, ushort)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static ushort CompareExchange(ref ushort location, ushort value, ushort comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref uint, uint, uint)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static uint CompareExchange(ref uint location, uint value, uint comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref ulong, ulong, ulong)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static ulong CompareExchange(ref ulong location, ulong value, ulong comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref float, float, float)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static float CompareExchange(ref float location, float value, float comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref double, double, double)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static double CompareExchange(ref double location, double value, double comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref IntPtr, IntPtr, IntPtr)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static IntPtr CompareExchange(ref IntPtr location, IntPtr value, IntPtr comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled <see cref="Interlocked.CompareExchange(ref UIntPtr, UIntPtr, UIntPtr)"/>.</summary>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static UIntPtr CompareExchange(ref UIntPtr location, UIntPtr value, UIntPtr comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled generic <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>.</summary>
    /// <typeparam name="T">The reference, primitive, or enum type of the location.</typeparam>
    /// <param name="location">The destination compared against <paramref name="comparand"/>.</param>
    /// <param name="value">The value stored when the comparison succeeds.</param>
    /// <param name="comparand">The value compared against <paramref name="location"/>.</param>
    /// <returns>The original value.</returns>
    public static T CompareExchange<T>(ref T location, T value, T comparand) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.CompareExchange"), Interlocked.CompareExchange(ref location, value, comparand)).Item2;

    /// <summary>Controlled 64-bit signed <c>Interlocked.Read</c>.</summary>
    /// <param name="location">The 64-bit value to read atomically.</param>
    /// <returns>The value read.</returns>
    public static long Read(ref long location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Read"), Interlocked.Read(ref location)).Item2;

    /// <summary>Controlled 64-bit unsigned <c>Interlocked.Read</c>.</summary>
    /// <param name="location">The 64-bit value to read atomically.</param>
    /// <returns>The value read.</returns>
    public static ulong Read(ref ulong location) => (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.Read"), Interlocked.Read(ref location)).Item2;

    /// <summary>
    /// Controlled <see cref="Interlocked.MemoryBarrier()"/>. On the cooperative single-logical-thread
    /// model there is no cross-core reordering to fence, but the real barrier is issued so the observable
    /// memory-order intent is preserved verbatim.
    /// </summary>
    public static void MemoryBarrier()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.MemoryBarrier");
        Interlocked.MemoryBarrier();
    }

    /// <summary>Controlled <see cref="Interlocked.MemoryBarrierProcessWide()"/>.</summary>
    public static void MemoryBarrierProcessWide()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Interlocked.MemoryBarrierProcessWide");
        Interlocked.MemoryBarrierProcessWide();
    }
}
