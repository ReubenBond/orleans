using System;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// The controlled stand-in for <see cref="global::System.Threading.SpinWait"/> (a value type). The rewriter
/// substitutes the type at every use site, so a local/field typed <c>SpinWait</c> becomes a
/// <see cref="ControlledSpinWait"/>, each <c>new SpinWait()</c> / <c>default</c> becomes the controlled
/// value, and the instance members (<see cref="Count"/>, <see cref="NextSpinWillYield"/>,
/// <see cref="Reset"/>, both <see cref="SpinOnce()"/> overloads) plus the static <see cref="SpinUntil(Func{bool})"/>
/// overloads redirect to this type. The public surface mirrors <see cref="global::System.Threading.SpinWait"/>
/// exactly. This follows the <c>Lock</c>/<c>Lock.Scope</c> value-type substitution precedent.
/// </para>
/// <para>
/// Inside a simulation a spin never burns CPU or consumes real time: <see cref="SpinOnce()"/> is a
/// cooperative no-op that only advances the observable spin <see cref="Count"/> (so
/// <see cref="NextSpinWillYield"/> and <see cref="Count"/> stay meaningful), and <see cref="SpinUntil(Func{bool})"/>
/// pumps the deterministic loop until its predicate holds - a never-satisfiable predicate surfaces as the
/// loop-model deadlock diagnostic rather than a real busy-spin. The finite <see cref="SpinUntil(Func{bool}, int)"/>
/// / <see cref="SpinUntil(Func{bool}, TimeSpan)"/> overloads use a virtual-time deadline (first-winner:
/// a predicate that can be satisfied now always beats the timeout).
/// Design adapted from Microsoft Coyote (MIT), whose controlled <c>SpinWait</c> likewise yields to the
/// scheduler instead of spinning.
/// </para>
/// </summary>
public struct ControlledSpinWait
{
    // The BCL SpinWait yields (rather than busy-spinning) once the spin count passes this threshold. We
    // mirror the documented value so NextSpinWillYield is observable inside a simulation.
    private const int YieldThreshold = 10;

    private const string SpinOnceApi = "System.Threading.SpinWait.SpinOnce";
    private const string SpinUntilApi = "System.Threading.SpinWait.SpinUntil";

    // The modelled spin count.
    private int _count;

    /// <summary>Controlled <see cref="global::System.Threading.SpinWait.Count"/>: the number of spins performed.</summary>
    public readonly int Count =>
        (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.SpinWait.get_Count"), _count).Item2;

    /// <summary>Controlled <see cref="global::System.Threading.SpinWait.NextSpinWillYield"/>.</summary>
    public readonly bool NextSpinWillYield =>
        (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.SpinWait.get_NextSpinWillYield"), _count >= YieldThreshold).Item2;

    /// <summary>Controlled <see cref="global::System.Threading.SpinWait.Reset"/>.</summary>
    public void Reset()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.SpinWait.Reset");
        _count = 0;
    }

    /// <summary>
    /// Controlled <see cref="global::System.Threading.SpinWait.SpinOnce()"/>: inside a simulation a cooperative
    /// no-op that advances the observable spin count without burning CPU or consuming real time.
    /// </summary>
    public void SpinOnce()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SpinOnceApi);
        AdvanceCount();
    }

    /// <summary>
    /// Controlled <see cref="global::System.Threading.SpinWait.SpinOnce(int)"/>: the sleep-1 threshold has no
    /// meaning without real time, so inside a simulation this is the same cooperative no-op as
    /// <see cref="SpinOnce()"/>.
    /// </summary>
    /// <param name="sleep1Threshold">The BCL sleep-1 threshold (ignored inside a simulation).</param>
    public void SpinOnce(int sleep1Threshold)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SpinOnceApi);
        // Validate exactly as the BCL does (-1 disables the sleep-1 fallback; anything below that is invalid).
        if (sleep1Threshold < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(sleep1Threshold), sleep1Threshold, "The threshold must be -1 or non-negative.");
        }

        _ = SpinOnceApi;
        AdvanceCount();
    }

    private void AdvanceCount()
    {
        if (_count < int.MaxValue)
        {
            _count++;
        }
    }

    /// <summary>
    /// Controlled <see cref="global::System.Threading.SpinWait.SpinUntil(Func{bool})"/>: pumps the deterministic
    /// loop until <paramref name="condition"/> holds instead of busy-spinning.
    /// </summary>
    /// <param name="condition">The predicate that ends the spin.</param>
    public static void SpinUntil(Func<bool> condition)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SpinUntilApi);
        ArgumentNullException.ThrowIfNull(condition);
        if (condition())
        {
            return;
        }

        SimulationTaskRuntime.DrainUntil(condition, SpinUntilApi, CancellationToken.None);
    }

    /// <summary>
    /// Controlled <see cref="global::System.Threading.SpinWait.SpinUntil(Func{bool}, int)"/>: pumps the loop until
    /// the predicate holds or a virtual-time deadline elapses.
    /// </summary>
    /// <param name="condition">The predicate that ends the spin.</param>
    /// <param name="millisecondsTimeout">Zero for a non-blocking check, -1 for infinite, or a finite virtual-time deadline.</param>
    /// <returns><see langword="true"/> if <paramref name="condition"/> was met before the deadline.</returns>
    public static bool SpinUntil(Func<bool> condition, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SpinUntilApi);
        ArgumentNullException.ThrowIfNull(condition);
        if (millisecondsTimeout < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), millisecondsTimeout, "The timeout must be -1 (infinite) or a non-negative value.");
        }

        if (condition())
        {
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        if (millisecondsTimeout == Timeout.Infinite)
        {
            SimulationTaskRuntime.DrainUntil(condition, SpinUntilApi, CancellationToken.None);
            return true;
        }

        // A finite spin registers a deterministic virtual-time deadline. It elapses only when the loop has
        // no other runnable work and advances modelled time to it, so a predicate that can be satisfied now
        // always wins over the timeout (first-winner). No wall-clock time is consumed.
        bool timedOut = false;
        ISimulationTimer deadline = SimulationTaskRuntime.RegisterTimeout(
            TimeSpan.FromMilliseconds(millisecondsTimeout),
            onElapsed: () => timedOut = true,
            SpinUntilApi);

        SimulationTaskRuntime.DrainUntil(() => timedOut || condition(), SpinUntilApi, CancellationToken.None);

        bool met = condition();
        if (met)
        {
            deadline.Cancel();
        }

        return met;
    }

    /// <summary>
    /// Controlled <see cref="global::System.Threading.SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>.
    /// </summary>
    /// <param name="condition">The predicate that ends the spin.</param>
    /// <param name="timeout">The virtual-time deadline, interpreted as for the milliseconds overload.</param>
    /// <returns><see langword="true"/> if <paramref name="condition"/> was met before the deadline.</returns>
    public static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SpinUntilApi);
        long totalMilliseconds = (long)timeout.TotalMilliseconds;
        if (totalMilliseconds < Timeout.Infinite || totalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
        }

        return SpinUntil(condition, (int)totalMilliseconds);
    }
}
