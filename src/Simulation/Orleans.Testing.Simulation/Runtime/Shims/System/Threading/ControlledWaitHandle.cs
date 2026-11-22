using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Static shims for the <see cref="WaitHandle"/> base surface (the members every event/wait handle
/// inherits): the <see cref="WaitHandle.WaitOne()"/> overloads, <see cref="WaitHandle.Dispose()"/> /
/// <c>Close</c>, and the raw OS-handle accessors. <see cref="WaitHandle"/> is abstract, so - as with the
/// controlled <see cref="SemaphoreSlim"/> surface - the controlled object <em>is</em> a real
/// <see cref="AutoResetEvent"/> / <see cref="ManualResetEvent"/> / <see cref="EventWaitHandle"/> used
/// purely as an identity handle, and the rewriter redirects each inherited instance member to a static
/// method here whose first parameter is the receiver.
/// </para>
/// <para>
/// This type also owns the shared, weak-keyed signalled-state registry that every controlled event uses
/// (<see cref="ControlledEventWaitHandle"/> populates it in its <c>Create</c> factories, and wait-handle and atomic control's
/// <see cref="SemaphoreSlim.AvailableWaitHandle"/> bridge registers a manual-reset entry). A synchronous
/// <see cref="WaitHandle.WaitOne()"/> pumps the deterministic loop until the handle is signalled (an
/// auto-reset handle consumes the signal, a manual-reset handle leaves it set), or a finite virtual-time
/// deadline elapses - never real time, never a blocked physical thread. A wait that can never be satisfied
/// surfaces as the standard controlled deadlock diagnostic.
/// </para>
/// <para>
/// Adapted from Microsoft Coyote (MIT), whose controlled wait handles likewise model the signalled state
/// on the cooperative scheduler instead of touching a kernel object. The raw handle accessors
/// (<c>Handle</c>/<c>SafeWaitHandle</c>) expose an OS primitive and are rejected precisely, as is a wait
/// against a handle the controlled surface never created.
/// </para>
/// </summary>
public static class ControlledWaitHandle
{
    private const string WaitOneApi = "System.Threading.WaitHandle.WaitOne";
    private const string HandleApi = "System.Threading.WaitHandle.Handle";
    private const string SafeWaitHandleApi = "System.Threading.WaitHandle.SafeWaitHandle";

    /// <summary>A single blocked <see cref="WaitHandle.WaitOne()"/> caller.</summary>
    internal sealed class Waiter : SimulationWaiter
    {
        public long OwnerId { get; init; }
    }

    internal sealed class MultiWaiter : SimulationWaiter
    {
        public required HandleState[] States { get; init; }

        public required bool WaitAll { get; init; }

        // The logical strand which started this multi-handle wait. Completion can be driven by a
        // different signaler's strand, but mutex acquisition must remain owned by the waiter.
        public required long OwnerId { get; init; }

        public int WinnerIndex { get; set; } = -1;
    }

    /// <summary>Base state for a controlled wait handle.</summary>
    internal abstract class HandleState
    {
        public bool Disposed { get; set; }

        // Callers blocked in WaitOne, in arrival order.
        public List<Waiter> Waiters { get; } = new();

        // Coordinated WaitAny/WaitAll callers registered across this and their other target handles.
        internal List<MultiWaiter> MultiWaiters { get; } = new();

        /// <summary>Returns whether this handle can be acquired by <paramref name="strandId"/>.</summary>
        internal abstract bool IsAvailable(long strandId);

        /// <summary>Acquires this handle for <paramref name="strandId"/> if available.</summary>
        internal abstract bool TryAcquire(long strandId);
    }

    /// <summary>The modelled signalled state of one controlled event/wait handle.</summary>
    internal sealed class EventState : HandleState
    {
        public EventState(EventResetMode mode, bool signaled)
        {
            Mode = mode;
            Signaled = signaled;
        }

        public EventResetMode Mode { get; }

        public bool Signaled { get; set; }

        internal override bool IsAvailable(long strandId) => Signaled;

        internal override bool TryAcquire(long strandId) => TryConsume(this);
    }

    /// <summary>The modelled ownership state of one controlled mutex.</summary>
    internal sealed class MutexState : HandleState
    {
        // A null owner represents an available mutex. Zero is a valid root-strand identity.
        public long? OwnerId { get; private set; }

        public int RecursionCount { get; private set; }

        internal override bool IsAvailable(long strandId) => OwnerId is null || OwnerId == strandId;

        internal override bool TryAcquire(long strandId)
        {
            if (OwnerId is null)
            {
                OwnerId = strandId;
                RecursionCount = 1;
                return true;
            }

            if (OwnerId == strandId)
            {
                RecursionCount++;
                return true;
            }

            return false;
        }

        internal void Release(long strandId)
        {
            if (OwnerId != strandId)
            {
#pragma warning disable CA2201 // ApplicationException is the BCL Mutex.ReleaseMutex contract.
                throw new ApplicationException("Object synchronization method was called from an unsynchronized block of code.");
#pragma warning restore CA2201
            }

            if (--RecursionCount != 0)
            {
                return;
            }

            OwnerId = null;
            ReleaseNextWaiter(this);
        }

        // A controlled mutex remains owned when its logical owner exits without ReleaseMutex, so a later
        // waiter produces the scheduler's deadlock diagnostic instead of fabricating OS abandonment.
        internal void NotifyOwnerStrandCompleted(long strandId)
        {
            if (OwnerId == strandId)
            {
                return;
            }
        }
    }

    /// <summary>The modelled permit count of one controlled kernel semaphore.</summary>
    internal sealed class SemaphoreState : HandleState
    {
        public SemaphoreState(int count, int maximumCount)
        {
            Count = count;
            MaximumCount = maximumCount;
        }

        public int Count { get; private set; }

        public int MaximumCount { get; }

        internal override bool IsAvailable(long strandId) => Count > 0;

        internal override bool TryAcquire(long strandId)
        {
            if (Count == 0)
            {
                return false;
            }

            Count--;
            return true;
        }

        internal int Release(int releaseCount)
        {
            int previous = Count;
            if ((long)Count + releaseCount > MaximumCount)
            {
                throw new SemaphoreFullException();
            }

            Count += releaseCount;
            ReleaseWaiters(this);
            return previous;
        }
    }

    private static readonly ConditionalWeakTable<WaitHandle, HandleState> States = new();

    /// <summary>Associates a modelled signalled state with a controlled wait handle. Used by the event and bridge factories.</summary>
    /// <param name="handle">The real wait-handle identity object.</param>
    /// <param name="state">The modelled state to associate.</param>
    internal static void Register(WaitHandle handle, HandleState state) => States.AddOrUpdate(handle, state);

    /// <summary>Attempts to resolve the modelled state of a wait handle.</summary>
    internal static bool TryGetState(WaitHandle handle, out HandleState state) => States.TryGetValue(handle, out state!);

    private static HandleState StateOrThrow(WaitHandle handle, string api)
    {
        if (States.TryGetValue(handle, out HandleState? state))
        {
            return state;
        }

        throw new SimulationApiException(
            SimulationApiCategory.WaitHandle,
            api,
            "the wait handle was not created by Clockwork's controlled event surface, so it has no modelled " +
            "signalled state; waiting on an uncontrolled kernel handle would block a physical thread outside " +
            "the deterministic scheduler.");
    }

    /// <summary>Resolves the modelled state of a controlled event, throwing if unknown or disposed. Used by Set/Reset.</summary>
    internal static EventState StateForOperation(WaitHandle handle, string api)
    {
        HandleState state = StateOrThrow(handle, api);
        ThrowIfDisposed(state);
        return state as EventState ?? throw new SimulationApiException(
            SimulationApiCategory.WaitHandle,
            api,
            "the requested operation is not supported by this controlled wait-handle type.");
    }

    /// <summary>Resolves the modelled ownership state of a controlled mutex.</summary>
    internal static MutexState MutexStateForOperation(Mutex handle, string api)
    {
        HandleState state = StateOrThrow(handle, api);
        ThrowIfDisposed(state);
        return state as MutexState ?? throw new SimulationApiException(
            SimulationApiCategory.WaitHandle,
            api,
            "the requested operation is not supported by this controlled wait-handle type.");
    }

    /// <summary>Resolves the modelled state of a controlled handle for an operation which can wait on any handle kind.</summary>
    internal static HandleState StateForWaitOperation(WaitHandle handle, string api)
    {
        HandleState state = StateOrThrow(handle, api);
        ThrowIfDisposed(state);
        return state;
    }

    /// <summary>Resolves the modelled state of a controlled kernel semaphore.</summary>
    internal static SemaphoreState SemaphoreStateForOperation(Semaphore handle, string api)
    {
        HandleState state = StateOrThrow(handle, api);
        ThrowIfDisposed(state);
        return state as SemaphoreState ?? throw new SimulationApiException(
            SimulationApiCategory.WaitHandle,
            api,
            "the requested operation is not supported by this controlled wait-handle type.");
    }

    // ---- externally-driven bridge handle (SemaphoreSlim.AvailableWaitHandle) ----

    /// <summary>
    /// Creates a controlled manual-reset wait handle whose signalled state is driven by an external
    /// condition rather than by <c>Set</c>/<c>Reset</c>. The real <see cref="ManualResetEvent"/> is used
    /// purely as an identity handle; callers publish state transitions through
    /// <see cref="UpdateBridgeSignal"/>. This backs <see cref="SemaphoreSlim.AvailableWaitHandle"/>, whose
    /// handle is signalled exactly while the semaphore has a permit available (count &gt; 0).
    /// </summary>
    /// <param name="signaled">The initial signalled state (permit available).</param>
    /// <returns>The identity handle registered with a manual-reset modelled state.</returns>
    internal static ManualResetEvent CreateBridge(bool signaled)
    {
        // The real event is only an identity object. Its kernel state is never observed or mutated by the
        // controlled surface; EventState below is the sole source of truth for its signal.
        var handle = new ManualResetEvent(false);
        Register(handle, new EventState(EventResetMode.ManualReset, signaled));
        return handle;
    }

    /// <summary>
    /// Publishes the signalled state of a bridge handle to track its external condition. A rising edge
    /// releases every waiter (manual-reset semantics); waiting on the handle never consumes the underlying
    /// resource. No-ops if the handle is unknown or already disposed.
    /// </summary>
    /// <param name="handle">The bridge identity handle.</param>
    /// <param name="signaled">The new signalled state.</param>
    internal static void UpdateBridgeSignal(WaitHandle handle, bool signaled)
    {
        if (!TryGetState(handle, out HandleState state) || state is not EventState eventState || state.Disposed)
        {
            return;
        }

        eventState.Signaled = signaled;
        if (signaled)
        {
            ReleaseWaiters(eventState);
        }
    }

    /// <summary>Marks a bridge handle's modelled state disposed so subsequent waits fault precisely.</summary>
    /// <param name="handle">The bridge identity handle, or <see langword="null"/> if none was materialised.</param>
    internal static void DisposeBridge(WaitHandle? handle)
    {
        if (handle is not null && TryGetState(handle, out HandleState state))
        {
            state.Disposed = true;
            List<SimulationWaiter.Claim> waiters = SimulationWaiter.TakeAll(state.Waiters);
            foreach (SimulationWaiter.Claim waiter in waiters)
            {
                waiter.Fault(new ObjectDisposedException(nameof(WaitHandle)));
            }

            foreach (MultiWaiter waiter in state.MultiWaiters.ToArray())
            {
                FaultMultiWaiter(waiter, new ObjectDisposedException(nameof(WaitHandle)));
            }
            handle.Dispose();
        }
    }

    // ---- signalled-state kernel shared by events and WaitOne ----

    /// <summary>
    /// Consumes a signal if the handle is set: an auto-reset handle clears its signal (one waiter's worth),
    /// a manual-reset handle stays set. Returns whether the caller may proceed without blocking.
    /// </summary>
    internal static bool TryConsume(EventState state)
    {
        if (!state.Signaled)
        {
            return false;
        }

        if (state.Mode == EventResetMode.AutoReset)
        {
            state.Signaled = false;
        }

        return true;
    }

    /// <summary>
    /// Releases waiters made runnable by the current signalled state. A manual-reset signal releases every
    /// waiter and stays set; an auto-reset signal releases exactly one waiter and then clears (that waiter
    /// consumed the signal).
    /// </summary>
    internal static void ReleaseWaiters(EventState state)
    {
        NotifyMultiWaiters(state);

        if (state.Mode == EventResetMode.ManualReset)
        {
            if (!state.Signaled)
            {
                return;
            }

            List<SimulationWaiter.Claim> waiters = SimulationWaiter.TakeAll(state.Waiters);
            foreach (SimulationWaiter.Claim waiter in waiters)
            {
                waiter.Complete(true);
            }

            return;
        }

        // Auto-reset: a single set releases exactly one eligible waiter and is consumed by it.
        while (state.Signaled && state.Waiters.Count > 0)
        {
            Waiter waiter = state.Waiters[0];
            if (SimulationWaiter.TryTake(state.Waiters, waiter, out SimulationWaiter.Claim claim))
            {
                state.Signaled = false;
                claim.Complete(true);
            }
        }
    }

    private static void ReleaseNextWaiter(MutexState state)
    {
        NotifyMultiWaiters(state);
        if (state.OwnerId is not null)
        {
            return;
        }

        while (state.Waiters.Count > 0)
        {
            Waiter waiter = state.Waiters[0];
            if (!state.TryAcquire(waiter.OwnerId))
            {
                return;
            }

            if (SimulationWaiter.TryTake(state.Waiters, waiter, out SimulationWaiter.Claim claim))
            {
                claim.Complete(true);
                return;
            }
        }
    }

    private static void ReleaseWaiters(SemaphoreState state)
    {
        NotifyMultiWaiters(state);

        while (state.Count > 0 && state.Waiters.Count > 0)
        {
            Waiter waiter = state.Waiters[0];
            if (state.TryAcquire(waiter.OwnerId))
            {
                if (SimulationWaiter.TryTake(state.Waiters, waiter, out SimulationWaiter.Claim claim))
                {
                    claim.Complete(true);
                }
            }
        }
    }

    internal static bool WaitControlled(WaitHandle handle, int millisecondsTimeout, string api)
    {
        ValidateTimeout(millisecondsTimeout);
        HandleState state = StateForWaitOperation(handle, api);
        return WaitControlled(state, millisecondsTimeout, api);
    }

    private static bool WaitControlled(HandleState state, int millisecondsTimeout, string api)
    {
        ValidateTimeout(millisecondsTimeout);

        if (state.TryAcquire(SimulationSynchronizationFlow.CurrentId))
        {
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        var waiter = new Waiter { OwnerId = SimulationSynchronizationFlow.CurrentId };
        state.Waiters.Add(waiter);
        if (millisecondsTimeout != Timeout.Infinite)
        {
            SimulationWaiter.AttachDeadline(
                waiter,
                TimeSpan.FromMilliseconds(millisecondsTimeout),
                api,
                pendingWaiter =>
                {
                    if (SimulationWaiter.TryTake(
                        state.Waiters,
                        pendingWaiter,
                        out SimulationWaiter.Claim claim,
                        cancelDeadline: false))
                    {
                        claim.Complete(false);
                    }
                });
        }

        SimulationTaskRuntime.DrainUntil(() => waiter.Task.IsCompleted, api, CancellationToken.None);
        return waiter.Task.GetAwaiter().GetResult();
    }

    // ---- WaitOne overloads (receiver-first) ----

    /// <summary>Controlled <see cref="WaitHandle.WaitOne()"/>.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <returns><see langword="true"/> when the handle is signalled.</returns>
    public static bool WaitOne(WaitHandle instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitOneApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, Timeout.Infinite, WaitOneApi);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitOne(int)"/>.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <returns><see langword="true"/> when the handle is signalled before the deadline.</returns>
    public static bool WaitOne(WaitHandle instance, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitOneApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, millisecondsTimeout, WaitOneApi);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitOne(TimeSpan)"/>.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <returns><see langword="true"/> when the handle is signalled before the deadline.</returns>
    public static bool WaitOne(WaitHandle instance, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitOneApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, ToMilliseconds(timeout), WaitOneApi);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitOne(int, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <param name="exitContext">Ignored inside a simulation (there is no synchronization context to exit).</param>
    /// <returns><see langword="true"/> when the handle is signalled before the deadline.</returns>
    public static bool WaitOne(WaitHandle instance, int millisecondsTimeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitOneApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, millisecondsTimeout, WaitOneApi);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitOne(TimeSpan, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <param name="exitContext">Ignored inside a simulation (there is no synchronization context to exit).</param>
    /// <returns><see langword="true"/> when the handle is signalled before the deadline.</returns>
    public static bool WaitOne(WaitHandle instance, TimeSpan timeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitOneApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, ToMilliseconds(timeout), WaitOneApi);
    }

    // ---- WaitAny / WaitAll / SignalAndWait (static multi-handle operations) ----

    /// <summary>The value returned by <see cref="WaitHandle.WaitAny(WaitHandle[])"/> when the wait times out.</summary>
    public const int WaitTimeout = 258;

    /// <summary>Controlled <see cref="WaitHandle.WaitAny(WaitHandle[])"/>.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <returns>The array index of the handle that satisfied the wait.</returns>
    public static int WaitAny(WaitHandle[] waitHandles)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyControlled(waitHandles, Timeout.Infinite);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAny(WaitHandle[], int)"/>.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <returns>The signalled handle's index, or <see cref="WaitTimeout"/> on timeout.</returns>
    public static int WaitAny(WaitHandle[] waitHandles, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyControlled(waitHandles, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan)"/>.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <returns>The signalled handle's index, or <see cref="WaitTimeout"/> on timeout.</returns>
    public static int WaitAny(WaitHandle[] waitHandles, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyControlled(waitHandles, ToMilliseconds(timeout));
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAny(WaitHandle[], int, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <param name="exitContext">Ignored inside a simulation.</param>
    /// <returns>The signalled handle's index, or <see cref="WaitTimeout"/> on timeout.</returns>
    public static int WaitAny(WaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyControlled(waitHandles, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <param name="exitContext">Ignored inside a simulation.</param>
    /// <returns>The signalled handle's index, or <see cref="WaitTimeout"/> on timeout.</returns>
    public static int WaitAny(WaitHandle[] waitHandles, TimeSpan timeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyControlled(waitHandles, ToMilliseconds(timeout));
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAll(WaitHandle[])"/>.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <returns><see langword="true"/> when every handle became signalled.</returns>
    public static bool WaitAll(WaitHandle[] waitHandles)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllControlled(waitHandles, Timeout.Infinite);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAll(WaitHandle[], int)"/>.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <returns><see langword="true"/> when every handle became signalled before the deadline.</returns>
    public static bool WaitAll(WaitHandle[] waitHandles, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllControlled(waitHandles, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAll(WaitHandle[], TimeSpan)"/>.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <returns><see langword="true"/> when every handle became signalled before the deadline.</returns>
    public static bool WaitAll(WaitHandle[] waitHandles, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllControlled(waitHandles, ToMilliseconds(timeout));
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAll(WaitHandle[], int, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <param name="exitContext">Ignored inside a simulation.</param>
    /// <returns><see langword="true"/> when every handle became signalled before the deadline.</returns>
    public static bool WaitAll(WaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllControlled(waitHandles, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="WaitHandle.WaitAll(WaitHandle[], TimeSpan, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="waitHandles">The handles to wait on.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <param name="exitContext">Ignored inside a simulation.</param>
    /// <returns><see langword="true"/> when every handle became signalled before the deadline.</returns>
    public static bool WaitAll(WaitHandle[] waitHandles, TimeSpan timeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllControlled(waitHandles, ToMilliseconds(timeout));
    }

    /// <summary>Controlled <see cref="WaitHandle.SignalAndWait(WaitHandle, WaitHandle)"/>.</summary>
    /// <param name="toSignal">The handle to signal.</param>
    /// <param name="toWaitOn">The handle to then wait on.</param>
    /// <returns><see langword="true"/> when <paramref name="toWaitOn"/> became signalled.</returns>
    public static bool SignalAndWait(WaitHandle toSignal, WaitHandle toWaitOn)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SignalAndWaitApi);
        return SignalAndWaitControlled(toSignal, toWaitOn, Timeout.Infinite);
    }

    /// <summary>Controlled <see cref="WaitHandle.SignalAndWait(WaitHandle, WaitHandle, int, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="toSignal">The handle to signal.</param>
    /// <param name="toWaitOn">The handle to then wait on.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, <see cref="Timeout.Infinite"/>, or zero.</param>
    /// <param name="exitContext">Ignored inside a simulation.</param>
    /// <returns><see langword="true"/> when <paramref name="toWaitOn"/> became signalled before the deadline.</returns>
    public static bool SignalAndWait(WaitHandle toSignal, WaitHandle toWaitOn, int millisecondsTimeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SignalAndWaitApi);
        return SignalAndWaitControlled(toSignal, toWaitOn, millisecondsTimeout);
    }

    /// <summary>Controlled <see cref="WaitHandle.SignalAndWait(WaitHandle, WaitHandle, TimeSpan, bool)"/>. The <paramref name="exitContext"/> flag has no meaning to the cooperative scheduler.</summary>
    /// <param name="toSignal">The handle to signal.</param>
    /// <param name="toWaitOn">The handle to then wait on.</param>
    /// <param name="timeout">The virtual-time timeout.</param>
    /// <param name="exitContext">Ignored inside a simulation.</param>
    /// <returns><see langword="true"/> when <paramref name="toWaitOn"/> became signalled before the deadline.</returns>
    public static bool SignalAndWait(WaitHandle toSignal, WaitHandle toWaitOn, TimeSpan timeout, bool exitContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SignalAndWaitApi);
        return SignalAndWaitControlled(toSignal, toWaitOn, ToMilliseconds(timeout));
    }

    // ---- multi-handle kernel ----

    // The largest handle array WaitAny/WaitAll accept, matching the BCL's WAIT_OBJECT limit.
    private const int MaxWaitHandles = 64;

    private const string WaitAnyApi = "System.Threading.WaitHandle.WaitAny";
    private const string WaitAllApi = "System.Threading.WaitHandle.WaitAll";
    private const string SignalAndWaitApi = "System.Threading.WaitHandle.SignalAndWait";

    private static int WaitAnyControlled(WaitHandle[] waitHandles, int millisecondsTimeout)
    {
        ValidateTimeout(millisecondsTimeout);
        HandleState[] states = ResolveStates(waitHandles, WaitAnyApi, requireUnique: false);
        long owner = SimulationSynchronizationFlow.CurrentId;

        // Fast path: serve the lowest-index available handle, acquiring it (auto-reset clears).
        int index = FirstAvailable(states, owner);
        if (index >= 0)
        {
            _ = states[index].TryAcquire(owner);
            return index;
        }

        if (millisecondsTimeout == 0)
        {
            return WaitTimeout;
        }

        var waiter = new MultiWaiter { States = states, WaitAll = false, OwnerId = owner };
        RegisterMultiWaiter(waiter);
        if (TryResolveMultiWaiter(waiter))
        {
            return waiter.WinnerIndex;
        }

        ArmMultiWaiterTimeout(waiter, millisecondsTimeout, WaitAnyApi);
        SimulationTaskRuntime.DrainUntil(() => !waiter.IsPending, WaitAnyApi, CancellationToken.None);
        return waiter.Task.GetAwaiter().GetResult()
            ? waiter.WinnerIndex
            : WaitTimeout;
    }

    private static bool WaitAllControlled(WaitHandle[] waitHandles, int millisecondsTimeout)
    {
        ValidateTimeout(millisecondsTimeout);
        HandleState[] states = ResolveStates(waitHandles, WaitAllApi, requireUnique: true);
        RejectWaitAllWithMutex(states);
        long owner = SimulationSynchronizationFlow.CurrentId;

        // Fast path: only succeeds if every handle is simultaneously signalled, and then consumes them all
        // atomically (an auto-reset handle is never partially consumed).
        if (AllSignaled(states, owner))
        {
            ConsumeAll(states, owner);
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        var waiter = new MultiWaiter { States = states, WaitAll = true, OwnerId = owner };
        RegisterMultiWaiter(waiter);
        if (TryResolveMultiWaiter(waiter))
        {
            return true;
        }

        ArmMultiWaiterTimeout(waiter, millisecondsTimeout, WaitAllApi);
        SimulationTaskRuntime.DrainUntil(() => !waiter.IsPending, WaitAllApi, CancellationToken.None);
        return waiter.Task.GetAwaiter().GetResult();
    }

    private static bool SignalAndWaitControlled(WaitHandle toSignal, WaitHandle toWaitOn, int millisecondsTimeout)
    {
        ArgumentNullException.ThrowIfNull(toSignal);
        ArgumentNullException.ThrowIfNull(toWaitOn);
        ValidateTimeout(millisecondsTimeout);

        // Validate both handles before mutating either. This matches the BCL contract that an invalid wait
        // target cannot leave the signal target changed.
        HandleState signalState = StateOrThrow(toSignal, SignalAndWaitApi);
        ThrowIfDisposed(signalState);
        HandleState waitState = StateForWaitOperation(toWaitOn, SignalAndWaitApi);
        Signal(signalState);

        // Then block on the second handle, exactly as a controlled WaitOne would.
        return WaitControlled(waitState, millisecondsTimeout, SignalAndWaitApi);
    }

    private static HandleState[] ResolveStates(WaitHandle[] waitHandles, string api, bool requireUnique)
    {
        ArgumentNullException.ThrowIfNull(waitHandles);
        if (waitHandles.Length == 0)
        {
            throw new ArgumentException("Waithandles cannot be empty.", nameof(waitHandles));
        }

        if (waitHandles.Length > MaxWaitHandles)
        {
            throw new NotSupportedException($"The number of WaitHandles must be less than or equal to {MaxWaitHandles}.");
        }

        var states = new HandleState[waitHandles.Length];
        for (int i = 0; i < waitHandles.Length; i++)
        {
            WaitHandle handle = waitHandles[i];
            if (handle is null)
            {
                throw new ArgumentNullException(nameof(waitHandles), "A wait handle in the array was null.");
            }

            if (requireUnique)
            {
                for (int j = 0; j < i; j++)
                {
                    if (ReferenceEquals(waitHandles[j], handle))
                    {
                        throw new DuplicateWaitObjectException(nameof(waitHandles), "The wait-handle array contains a duplicate handle.");
                    }
                }
            }

            HandleState state = StateOrThrow(handle, api);
            ThrowIfDisposed(state);
            states[i] = state;
        }

        return states;
    }

    private static int FirstAvailable(HandleState[] states, long owner)
    {
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].IsAvailable(owner))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool AllSignaled(HandleState[] states, long owner)
    {
        for (int i = 0; i < states.Length; i++)
        {
            if (!states[i].IsAvailable(owner))
            {
                return false;
            }
        }

        return true;
    }

    private static void ConsumeAll(HandleState[] states, long owner)
    {
        for (int i = 0; i < states.Length; i++)
        {
            _ = states[i].TryAcquire(owner);
        }
    }

    private static void RegisterMultiWaiter(MultiWaiter waiter)
    {
        foreach (HandleState state in UniqueStates(waiter.States))
        {
            state.MultiWaiters.Add(waiter);
        }
    }

    private static void ArmMultiWaiterTimeout(MultiWaiter waiter, int millisecondsTimeout, string api)
    {
        if (millisecondsTimeout == Timeout.Infinite || !waiter.IsPending)
        {
            return;
        }

        SimulationWaiter.AttachDeadline(
            waiter,
            TimeSpan.FromMilliseconds(millisecondsTimeout),
            api,
            pendingWaiter => CompleteMultiWaiter(
                pendingWaiter,
                succeeded: false,
                cancelDeadline: false));
    }

    private static void NotifyMultiWaiters(HandleState state)
    {
        foreach (MultiWaiter waiter in state.MultiWaiters.ToArray())
        {
            _ = TryResolveMultiWaiter(waiter);
        }
    }

    private static bool TryResolveMultiWaiter(MultiWaiter waiter)
    {
        if (!waiter.IsPending)
        {
            return false;
        }

        if (waiter.WaitAll)
        {
            if (!AllSignaled(waiter.States, waiter.OwnerId))
            {
                return false;
            }

            ConsumeAll(waiter.States, waiter.OwnerId);
            CompleteMultiWaiter(waiter, succeeded: true);
            return true;
        }

        int index = FirstAvailable(waiter.States, waiter.OwnerId);
        if (index < 0 || !waiter.States[index].TryAcquire(waiter.OwnerId))
        {
            return false;
        }

        waiter.WinnerIndex = index;
        CompleteMultiWaiter(waiter, succeeded: true);
        return true;
    }

    private static void CompleteMultiWaiter(
        MultiWaiter waiter,
        bool succeeded,
        bool cancelDeadline = true)
    {
        if (!waiter.TryClaim(out SimulationWaiter.Claim claim, cancelDeadline))
        {
            return;
        }

        foreach (HandleState state in UniqueStates(waiter.States))
        {
            state.MultiWaiters.Remove(waiter);
        }

        claim.Complete(succeeded);
    }

    private static void FaultMultiWaiter(MultiWaiter waiter, Exception exception)
    {
        if (!waiter.TryClaim(out SimulationWaiter.Claim claim))
        {
            return;
        }

        foreach (HandleState state in UniqueStates(waiter.States))
        {
            state.MultiWaiters.Remove(waiter);
        }

        claim.Fault(exception);
    }

    private static IEnumerable<HandleState> UniqueStates(HandleState[] states)
    {
        for (int i = 0; i < states.Length; i++)
        {
            bool seen = false;
            for (int j = 0; j < i; j++)
            {
                if (ReferenceEquals(states[j], states[i]))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                yield return states[i];
            }
        }
    }

    private static void RejectWaitAllWithMutex(HandleState[] states)
    {
        foreach (HandleState state in states)
        {
            if (state is MutexState)
            {
                throw new SimulationApiException(
                    SimulationApiCategory.WaitHandle,
                    WaitAllApi,
                    "WaitAll containing a Mutex is not supported until the shared wait kernel can model the BCL's atomic multi-mutex acquisition semantics.");
            }
        }
    }

    private static void Signal(HandleState state)
    {
        switch (state)
        {
            case EventState eventState:
                eventState.Signaled = true;
                ReleaseWaiters(eventState);
                return;
            case MutexState mutexState:
                mutexState.Release(SimulationSynchronizationFlow.CurrentId);
                return;
            case SemaphoreState semaphoreState:
                _ = semaphoreState.Release(1);
                return;
            default:
                throw new SimulationApiException(
                    SimulationApiCategory.WaitHandle,
                    SignalAndWaitApi,
                    "the requested signal operation is not supported by this controlled wait-handle type.");
        }
    }

    // ---- registered-wait kernel (ThreadPool.RegisterWaitForSingleObject) ----

    /// <summary>
    /// Arms one passive, non-blocking registered-wait iteration: adds a waiter to the target handle's FIFO
    /// waiter set (so an auto-reset signal or semaphore permit is consumed exactly once, in arrival order)
    /// and registers a virtual-time deadline that completes the waiter with
    /// <see langword="false"/> on elapse. The caller schedules a controlled continuation on the returned
    /// waiter's completion task rather than blocking the logical thread, so a background registration never
    /// occupies the cooperative scheduler. A result of <see langword="true"/> means signalled;
    /// <see langword="false"/> means the deadline elapsed (or the waiter was cancelled by
    /// <see cref="CancelRegisteredWaiter"/>).
    /// </summary>
    /// <param name="state">The controlled target handle's modelled state.</param>
    /// <param name="millisecondsTimeout">The virtual-time timeout, or <see cref="Timeout.Infinite"/>.</param>
    /// <param name="api">The originating API name, used for diagnostics.</param>
    /// <returns>The registered waiter, whose completion task the caller continues on.</returns>
    internal static Waiter ArmRegisteredWaiter(HandleState state, int millisecondsTimeout, string api)
    {
        var waiter = new Waiter();
        state.Waiters.Add(waiter);
        if (millisecondsTimeout != Timeout.Infinite)
        {
            SimulationWaiter.AttachDeadline(
                waiter,
                TimeSpan.FromMilliseconds(millisecondsTimeout),
                api,
                pendingWaiter =>
                {
                    if (SimulationWaiter.TryTake(
                        state.Waiters,
                        pendingWaiter,
                        out SimulationWaiter.Claim claim,
                        cancelDeadline: false))
                    {
                        claim.Complete(false);
                    }
                });
        }

        return waiter;
    }

    /// <summary>
    /// Cancels a still-pending registered waiter (used by <c>Unregister</c>): removes it from the event's
    /// waiter set, cancels its virtual-time deadline, and completes it with <see langword="false"/> so the
    /// caller's scheduled continuation runs and observes the cancellation. Idempotent if already completed.
    /// </summary>
    /// <param name="state">The handle the waiter is registered on.</param>
    /// <param name="waiter">The pending waiter to cancel.</param>
    internal static void CancelRegisteredWaiter(HandleState state, Waiter waiter)
    {
        if (SimulationWaiter.TryTake(state.Waiters, waiter, out SimulationWaiter.Claim claim))
        {
            claim.Complete(false);
        }
    }

    /// <summary>
    /// Sets a controlled event's modelled signal and releases eligible waiters, if the handle has modelled
    /// state and is not disposed. Used to signal an optional <c>Unregister</c> completion handle. No-ops for
    /// an uncontrolled or disposed handle.
    /// </summary>
    /// <param name="handle">The handle to signal, or <see langword="null"/>.</param>
    internal static void TrySignal(WaitHandle? handle)
    {
        if (handle is not null && TryGetState(handle, out HandleState state) && state is EventState eventState && !state.Disposed)
        {
            eventState.Signaled = true;
            ReleaseWaiters(eventState);
        }
    }

    // ---- lifecycle ----

    /// <summary>Controlled <see cref="WaitHandle.Dispose()"/>.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    public static void Dispose(WaitHandle instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.WaitHandle.Dispose");
        ArgumentNullException.ThrowIfNull(instance);
        if (States.TryGetValue(instance, out HandleState? state))
        {
            state.Disposed = true;
            List<SimulationWaiter.Claim> waiters = SimulationWaiter.TakeAll(state.Waiters);
            foreach (SimulationWaiter.Claim waiter in waiters)
            {
                waiter.Fault(new ObjectDisposedException(nameof(WaitHandle)));
            }

            foreach (MultiWaiter waiter in state.MultiWaiters.ToArray())
            {
                FaultMultiWaiter(waiter, new ObjectDisposedException(nameof(WaitHandle)));
            }
        }

        // Always release the real identity object's OS handle; the modelled state above is what a
        // controlled wait observes.
        instance.Dispose();
    }

    /// <summary>Controlled <see cref="WaitHandle.Close()"/>.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    public static void Close(WaitHandle instance) => Dispose(instance);

    // ---- raw OS-handle accessors: rejected precisely ----

    /// <summary>Rejected controlled <see cref="WaitHandle.Handle"/> getter.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <returns>Never returns inside a simulation; throws instead.</returns>
    public static IntPtr GetHandle(WaitHandle instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(HandleApi);
        ArgumentNullException.ThrowIfNull(instance);
        throw RawHandleRejected(HandleApi);
    }

    /// <summary>Rejected controlled <see cref="WaitHandle.Handle"/> setter.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <param name="value">The requested native handle.</param>
    public static void SetHandle(WaitHandle instance, IntPtr value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(HandleApi);
        ArgumentNullException.ThrowIfNull(instance);
        throw RawHandleRejected(HandleApi);
    }

    /// <summary>Rejected controlled <see cref="WaitHandle.SafeWaitHandle"/> getter.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <returns>Never returns inside a simulation; throws instead.</returns>
    public static SafeWaitHandle GetSafeWaitHandle(WaitHandle instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SafeWaitHandleApi);
        ArgumentNullException.ThrowIfNull(instance);
        throw RawHandleRejected(SafeWaitHandleApi);
    }

    /// <summary>Rejected controlled <see cref="WaitHandle.SafeWaitHandle"/> setter.</summary>
    /// <param name="instance">The receiving wait handle.</param>
    /// <param name="value">The requested safe handle.</param>
    public static void SetSafeWaitHandle(WaitHandle instance, SafeWaitHandle value)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SafeWaitHandleApi);
        ArgumentNullException.ThrowIfNull(instance);
        throw RawHandleRejected(SafeWaitHandleApi);
    }

    private static SimulationApiException RawHandleRejected(string api) =>
        new(
            SimulationApiCategory.WaitHandle,
            api,
            "it exposes the underlying OS wait handle, which would let code block a physical thread or " +
            "signal a kernel object outside the deterministic scheduler.");

    private static void ThrowIfDisposed(HandleState state) =>
        ObjectDisposedException.ThrowIf(state.Disposed, typeof(WaitHandle));

    private static void ValidateTimeout(int millisecondsTimeout)
    {
        if (millisecondsTimeout < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), millisecondsTimeout, "The timeout must be -1 (infinite) or a non-negative value.");
        }
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        long total = (long)timeout.TotalMilliseconds;
        if (total < Timeout.Infinite || total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
        }

        return (int)total;
    }
}
