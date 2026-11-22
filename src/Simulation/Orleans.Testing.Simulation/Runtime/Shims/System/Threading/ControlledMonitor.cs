using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Static shims for the <see cref="Monitor"/> surface, and the engine behind an ordinary C#
/// <c>lock (object)</c> statement (which the compiler lowers to
/// <c>Monitor.Enter(obj, ref bool)</c> / <c>Monitor.Exit(obj)</c>, so redirecting those members
/// controls every <c>lock</c> automatically). The rewriter redirects the static <see cref="Monitor"/>
/// calls to the matching methods here.
/// </para>
/// <para>
/// Inside a simulation a monitor is modelled entirely in terms of the single cooperative logical thread:
/// ownership is tracked per lock object against the ambient
/// <see cref="SimulationSynchronizationFlow.CurrentId"/> (the logical strand, not the physical managed
/// thread, since every strand shares one thread), reentrancy is a recursion count, and a contended
/// <c>Enter</c> "blocks" by pumping the deterministic loop (via
/// <see cref="SimulationTaskRuntime.DrainUntil"/>) until the lock is free rather than blocking an OS
/// thread. A wait that can never make progress surfaces as the standard controlled deadlock diagnostic.
/// <see cref="Wait(object)"/>/<see cref="Pulse(object)"/>/<see cref="PulseAll(object)"/> implement the
/// condition-variable protocol on the same kernel.
/// </para>
/// <para>
/// Per-object state is held in a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the lock
/// object, so associating a lock with its controlled state never keeps the lock object alive: once the
/// application drops the object the entry is collected. All state mutation happens on the one logical
/// thread, so no internal locking of the state is required.
/// </para>
/// <para>
/// This mirrors Microsoft Coyote's controlled <c>Monitor</c>
/// (<c>Microsoft.Coyote.Rewriting.Types.Threading.Monitor</c>, MIT-licensed); Coyote schedules real
/// threads against a synchronized resource, whereas Clockwork tracks ownership against the cooperative
/// logical strand. The observable mutual-exclusion and signalling semantics are the same. Finite
/// timeouts use the deterministic virtual-time deadline engine (a timed wait is paused until a modelled
/// instant, never real time), so <c>TryEnter</c>/<c>Wait</c> return on the simulated deadline.
/// </para>
/// </summary>
public static class ControlledMonitor
{
    private const string EnterApi = "System.Threading.Monitor.Enter";
    private const string TryEnterApi = "System.Threading.Monitor.TryEnter";
    private const string WaitApi = "System.Threading.Monitor.Wait";

    /// <summary>Sentinel owner value meaning "no strand currently owns the monitor".</summary>
    private const long Unowned = long.MinValue;

    private sealed class MonitorState
    {
        // The logical strand that owns the monitor, or Unowned. 0 (the root strand) is a valid owner, so
        // a dedicated sentinel distinct from every strand id is used for the unowned state.
        public long Owner = Unowned;

        // Number of unmatched Enter calls by the owning strand (reentrancy depth). Zero iff Unowned.
        public int Recursion;

        // Strands blocked in Wait, in arrival order. A Pulse moves the front waiter to Pulsed; PulseAll
        // moves them all. A pulsed waiter reacquires (restoring its recursion) once the lock is free.
        public readonly List<Waiter> WaitSet = new();
    }

    private sealed class Waiter
    {
        public bool Pulsed;

        // Set when a finite Monitor.Wait's virtual-time deadline elapses before a pulse selected it. A
        // timed-out waiter is no longer eligible for Pulse/PulseAll (the pulse must not be consumed by a
        // waiter that has already given up), yet it still reacquires the monitor before Wait returns false.
        public bool TimedOut;

        public int SavedRecursion;
    }

    private static readonly ConditionalWeakTable<object, MonitorState> States = new();

    private static MonitorState StateOf(object obj) => States.GetOrCreateValue(obj);

    /// <summary>Controlled <see cref="Monitor.Enter(object)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    public static void Enter(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EnterApi);
        ArgumentNullException.ThrowIfNull(obj);
        AcquireControlled(obj);
    }

    /// <summary>Controlled <see cref="Monitor.Enter(object, ref bool)"/> (the C# <c>lock</c> lowering).</summary>
    /// <param name="obj">The lock object.</param>
    /// <param name="lockTaken">Set to <see langword="true"/> once the lock is held; must be <see langword="false"/> on entry.</param>
    public static void Enter(object obj, ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EnterApi);
        ArgumentNullException.ThrowIfNull(obj);
        if (lockTaken)
        {
            throw new ArgumentException("The lockTaken argument must be initialized to false before calling Monitor.Enter.", nameof(lockTaken));
        }

        AcquireControlled(obj);
        lockTaken = true;
    }

    /// <summary>Controlled <see cref="Monitor.Exit(object)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    public static void Exit(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Monitor.Exit");
        ArgumentNullException.ThrowIfNull(obj);
        var state = StateOf(obj);
        var me = SimulationSynchronizationFlow.CurrentId;
        if (state.Owner != me)
        {
            throw new SynchronizationLockException("The current strand does not own the lock being released.");
        }

        state.Recursion--;
        RaceSynchronization.ExitExclusive(obj);
        if (state.Recursion == 0)
        {
            state.Owner = Unowned;
        }
    }

    /// <summary>Controlled <see cref="Monitor.IsEntered(object)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    /// <returns><see langword="true"/> if the current strand owns the monitor.</returns>
    public static bool IsEntered(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Monitor.IsEntered");
        ArgumentNullException.ThrowIfNull(obj);
        return StateOf(obj).Owner == SimulationSynchronizationFlow.CurrentId;
    }

    /// <summary>
    /// Rejects the process-wide <see cref="Monitor.LockContentionCount"/> metric inside simulation.
    /// Its value aggregates physical runtime contention across unrelated simulations and cannot be
    /// represented faithfully by a per-simulation cooperative scheduler.
    /// </summary>
    /// <returns>Never returns.</returns>
    public static long LockContentionCount()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Monitor.LockContentionCount");
        throw new SimulationApiException(
            SimulationApiCategory.Task,
            "System.Threading.Monitor.LockContentionCount",
            "the process-wide physical lock contention metric has no faithful per-simulation meaning.");
    }

    /// <summary>Controlled <see cref="Monitor.TryEnter(object)"/>: a non-blocking acquire attempt.</summary>
    /// <param name="obj">The lock object.</param>
    /// <returns><see langword="true"/> if the lock was acquired.</returns>
    public static bool TryEnter(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        ArgumentNullException.ThrowIfNull(obj);
        return TryAcquireControlled(obj);
    }

    /// <summary>Controlled <see cref="Monitor.TryEnter(object, ref bool)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    /// <param name="lockTaken">Set to the acquisition result; must be <see langword="false"/> on entry.</param>
    public static void TryEnter(object obj, ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        ArgumentNullException.ThrowIfNull(obj);
        if (lockTaken)
        {
            throw new ArgumentException("The lockTaken argument must be initialized to false before calling Monitor.TryEnter.", nameof(lockTaken));
        }

        lockTaken = TryAcquireControlled(obj);
    }

    /// <summary>Controlled <see cref="Monitor.TryEnter(object, int)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    /// <param name="millisecondsTimeout">Zero for a non-blocking try; -1 (infinite) blocks indefinitely; a finite positive value blocks until acquisition or the simulated deadline.</param>
    /// <returns><see langword="true"/> if the lock was acquired.</returns>
    public static bool TryEnter(object obj, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        ArgumentNullException.ThrowIfNull(obj);
        ValidateTimeout(millisecondsTimeout);
        if (millisecondsTimeout == 0)
        {
            return TryAcquireControlled(obj);
        }

        if (millisecondsTimeout == Timeout.Infinite)
        {
            AcquireControlled(obj);
            return true;
        }

        return AcquireControlledWithTimeout(obj, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="Monitor.TryEnter(object, int, ref bool)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    /// <param name="millisecondsTimeout">The timeout, interpreted as for <see cref="TryEnter(object, int)"/>.</param>
    /// <param name="lockTaken">Set to the acquisition result; must be <see langword="false"/> on entry.</param>
    public static void TryEnter(object obj, int millisecondsTimeout, ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        ArgumentNullException.ThrowIfNull(obj);
        if (lockTaken)
        {
            throw new ArgumentException("The lockTaken argument must be initialized to false before calling Monitor.TryEnter.", nameof(lockTaken));
        }

        ValidateTimeout(millisecondsTimeout);
        if (millisecondsTimeout == 0)
        {
            lockTaken = TryAcquireControlled(obj);
            return;
        }

        if (millisecondsTimeout == Timeout.Infinite)
        {
            AcquireControlled(obj);
            lockTaken = true;
            return;
        }

        lockTaken = AcquireControlledWithTimeout(obj, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="Monitor.TryEnter(object, TimeSpan)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    /// <param name="timeout">The timeout, converted to milliseconds and interpreted as for <see cref="TryEnter(object, int)"/>.</param>
    /// <returns><see langword="true"/> if the lock was acquired.</returns>
    public static bool TryEnter(object obj, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        return TryEnter(obj, ToMilliseconds(timeout));
    }

    /// <summary>Controlled <see cref="Monitor.TryEnter(object, TimeSpan, ref bool)"/>.</summary>
    /// <param name="obj">The lock object.</param>
    /// <param name="timeout">The timeout, converted to milliseconds and interpreted as for <see cref="TryEnter(object, int)"/>.</param>
    /// <param name="lockTaken">Set to the acquisition result; must be <see langword="false"/> on entry.</param>
    public static void TryEnter(object obj, TimeSpan timeout, ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        TryEnter(obj, ToMilliseconds(timeout), ref lockTaken);
    }

    /// <summary>Controlled <see cref="Monitor.Wait(object)"/>.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    /// <returns><see langword="true"/> when the monitor was reacquired after a pulse.</returns>
    public static bool Wait(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(obj);
        return WaitControlled(obj, Timeout.Infinite);
    }

    /// <summary>Controlled <see cref="Monitor.Wait(object, int)"/>.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    /// <param name="millisecondsTimeout">Zero returns immediately without parking; -1 parks until pulsed; a finite positive value parks until pulsed or the simulated deadline elapses.</param>
    /// <returns><see langword="true"/> when the monitor was reacquired after a pulse; <see langword="false"/> for a zero timeout with no pending pulse.</returns>
    public static bool Wait(object obj, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(obj);
        ValidateTimeout(millisecondsTimeout);
        return WaitControlled(obj, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="Monitor.Wait(object, TimeSpan)"/>.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    /// <param name="timeout">The timeout, converted to milliseconds and interpreted as for <see cref="Wait(object, int)"/>.</param>
    /// <returns><see langword="true"/> when the monitor was reacquired after a pulse.</returns>
    public static bool Wait(object obj, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return Wait(obj, ToMilliseconds(timeout));
    }

    /// <summary>Controlled <see cref="Monitor.Wait(object, int, bool)"/>.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    /// <param name="millisecondsTimeout">The timeout, interpreted as for <see cref="Wait(object, int)"/>.</param>
    /// <param name="exitContext">The legacy synchronization-context flag; a no-op on modern .NET and ignored inside a simulation.</param>
    /// <returns><see langword="true"/> when the monitor was reacquired after a pulse.</returns>
    public static bool Wait(object obj, int millisecondsTimeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(obj);
        ValidateTimeout(millisecondsTimeout);
        return WaitControlled(obj, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="Monitor.Wait(object, TimeSpan, bool)"/>.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    /// <param name="timeout">The timeout, interpreted as for <see cref="Wait(object, int)"/>.</param>
    /// <param name="exitContext">The legacy synchronization-context flag; a no-op on modern .NET and ignored inside a simulation.</param>
    /// <returns><see langword="true"/> when the monitor was reacquired after a pulse.</returns>
    public static bool Wait(object obj, TimeSpan timeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return Wait(obj, ToMilliseconds(timeout), exitContext);
    }

    /// <summary>Controlled <see cref="Monitor.Pulse(object)"/>: makes the longest-waiting strand eligible to reacquire.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    public static void Pulse(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Monitor.Pulse");
        ArgumentNullException.ThrowIfNull(obj);
        var state = StateOf(obj);
        RequireOwnership(state, "Monitor.Pulse");
        for (var i = 0; i < state.WaitSet.Count; i++)
        {
            var waiter = state.WaitSet[i];
            if (!waiter.Pulsed && !waiter.TimedOut)
            {
                // Move the front un-pulsed, not-yet-timed-out waiter to the ready set. It reacquires
                // (restoring its full recursion) once this owner releases the lock; a pulse with no eligible
                // waiter is a no-op, so no signal is ever lost or stored.
                waiter.Pulsed = true;
                break;
            }
        }
    }

    /// <summary>Controlled <see cref="Monitor.PulseAll(object)"/>: makes every waiting strand eligible to reacquire.</summary>
    /// <param name="obj">The lock object; the current strand must own it.</param>
    public static void PulseAll(object obj)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Monitor.PulseAll");
        ArgumentNullException.ThrowIfNull(obj);
        var state = StateOf(obj);
        RequireOwnership(state, "Monitor.PulseAll");
        foreach (var waiter in state.WaitSet)
        {
            if (!waiter.TimedOut)
            {
                waiter.Pulsed = true;
            }
        }
    }

    /// <summary>
    /// Releases the monitor's full recursion count, parks the strand in the wait set, and reacquires the
    /// monitor (restoring the recursion count) once it has been pulsed and the lock is free again.
    /// </summary>
    private static bool WaitControlled(object obj, int millisecondsTimeout)
    {
        var state = StateOf(obj);
        var me = SimulationSynchronizationFlow.CurrentId;
        if (state.Owner != me)
        {
            throw new SynchronizationLockException("The current strand does not own the lock it is waiting on.");
        }

        var savedRecursion = state.Recursion;

        // Atomically (no cooperative yield occurs across these statements) release the complete recursion
        // count so a different strand can enter, and record the waiter in arrival order.
        var waiter = new Waiter { SavedRecursion = savedRecursion };
        state.WaitSet.Add(waiter);
        for (var recursion = 0; recursion < savedRecursion; recursion++)
        {
            RaceSynchronization.ExitExclusive(obj);
        }

        state.Owner = Unowned;
        state.Recursion = 0;

        if (millisecondsTimeout == 0)
        {
            // A zero-timeout Wait releases and immediately reacquires without ever parking; no pulse can
            // have arrived, so it reports failure after reacquiring.
            state.WaitSet.Remove(waiter);
            SimulationTaskRuntime.DrainUntil(() => state.Owner == Unowned, WaitApi, CancellationToken.None);
            state.Owner = me;
            state.Recursion = savedRecursion;
            TrackReacquisition(obj, savedRecursion);
            return false;
        }

        if (millisecondsTimeout != Timeout.Infinite)
        {
            // Finite wait: park until pulsed or the deterministic virtual-time deadline elapses, then
            // reacquire the monitor before returning either way (Wait must always re-own on return). The
            // deadline fires only when no other work can run at the current modelled instant, so a pulse
            // that is possible now always wins over the timeout - the first-winner policy. A timed-out
            // waiter is no longer pulse-eligible, so a late pulse is not consumed by a waiter that gave up.
            var deadline = SimulationTaskRuntime.RegisterTimeout(
                TimeSpan.FromMilliseconds(millisecondsTimeout),
                onElapsed: () => waiter.TimedOut = true,
                WaitApi);
            SimulationTaskRuntime.DrainUntil(
                () => (waiter.Pulsed || waiter.TimedOut) && state.Owner == Unowned,
                WaitApi,
                CancellationToken.None);
            deadline.Cancel();
            state.WaitSet.Remove(waiter);
            state.Owner = me;
            state.Recursion = savedRecursion;
            TrackReacquisition(obj, savedRecursion);
            return waiter.Pulsed;
        }

        // Park until pulsed and the lock is free. Reacquisition sets ownership synchronously, so at most
        // one pulsed waiter wins even when several are ready.
        SimulationTaskRuntime.DrainUntil(
            () => waiter.Pulsed && state.Owner == Unowned,
            WaitApi,
            CancellationToken.None);
        state.WaitSet.Remove(waiter);
        state.Owner = me;
        state.Recursion = savedRecursion;
        TrackReacquisition(obj, savedRecursion);
        return true;
    }

    private static void RequireOwnership(MonitorState state, string api)
    {
        if (state.Owner != SimulationSynchronizationFlow.CurrentId)
        {
            throw new SynchronizationLockException($"{api} requires the current strand to own the monitor.");
        }
    }

    /// <summary>
    /// Acquires the monitor for the current strand, blocking cooperatively (pumping the deterministic
    /// loop) until it is free. A reentrant acquire by the owner simply increments the recursion count.
    /// </summary>
    private static void AcquireControlled(object obj)
    {
        var state = StateOf(obj);
        var me = SimulationSynchronizationFlow.CurrentId;
        if (state.Owner == me)
        {
            state.Recursion++;
            RaceSynchronization.EnterExclusive(obj);
            return;
        }

        // Pump the loop until the lock is free. When DrainUntil returns the predicate held and no
        // cooperative yield has happened since, so no other strand can have taken the lock in between.
        SimulationTaskRuntime.DrainUntil(() => state.Owner == Unowned, EnterApi, CancellationToken.None);
        state.Owner = me;
        state.Recursion = 1;
        RaceSynchronization.EnterExclusive(obj);
    }

    /// <summary>
    /// Acquires the monitor for the current strand, blocking cooperatively until it is free or a
    /// deterministic virtual-time deadline elapses. A reentrant acquire by the owner succeeds immediately.
    /// The deadline only elapses when no other work can run at the current modelled instant, so a release
    /// that is possible now always wins over the timeout - the deterministic first-winner policy.
    /// </summary>
    /// <returns><see langword="true"/> if the lock was acquired before the deadline; otherwise <see langword="false"/>.</returns>
    private static bool AcquireControlledWithTimeout(object obj, int millisecondsTimeout)
    {
        var state = StateOf(obj);
        var me = SimulationSynchronizationFlow.CurrentId;
        if (state.Owner == me)
        {
            state.Recursion++;
            RaceSynchronization.EnterExclusive(obj);
            return true;
        }

        if (state.Owner == Unowned)
        {
            state.Owner = me;
            state.Recursion = 1;
            RaceSynchronization.EnterExclusive(obj);
            return true;
        }

        var deadline = SimulationTaskRuntime.RegisterTimeout(
            TimeSpan.FromMilliseconds(millisecondsTimeout),
            onElapsed: null,
            TryEnterApi);
        SimulationTaskRuntime.DrainUntil(
            () => state.Owner == Unowned || deadline.IsElapsed,
            TryEnterApi,
            CancellationToken.None);

        // Acquisition wins over the timeout whenever the lock is free: the loop only fires the deadline when
        // nothing else could run, so a release at the current instant is observed first.
        if (state.Owner == Unowned)
        {
            deadline.Cancel();
            state.Owner = me;
            state.Recursion = 1;
            RaceSynchronization.EnterExclusive(obj);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to acquire the monitor for the current strand without blocking. A reentrant acquire by
    /// the owner increments the recursion count and succeeds.
    /// </summary>
    private static bool TryAcquireControlled(object obj)
    {
        var state = StateOf(obj);
        var me = SimulationSynchronizationFlow.CurrentId;
        if (state.Owner == me)
        {
            state.Recursion++;
            RaceSynchronization.EnterExclusive(obj);
            return true;
        }

        if (state.Owner == Unowned)
        {
            state.Owner = me;
            state.Recursion = 1;
            RaceSynchronization.EnterExclusive(obj);
            return true;
        }

        return false;
    }

    private static void TrackReacquisition(object obj, int recursion)
    {
        for (var count = 0; count < recursion; count++)
        {
            RaceSynchronization.EnterExclusive(obj);
        }
    }

    private static void ValidateTimeout(int millisecondsTimeout)
    {
        if (millisecondsTimeout < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), millisecondsTimeout, "The timeout must be -1 (infinite) or a non-negative value.");
        }
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        var total = (long)timeout.TotalMilliseconds;
        if (total < Timeout.Infinite || total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
        }

        return (int)total;
    }
}
