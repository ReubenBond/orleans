using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Controlled receiver-first shims for <see cref="ManualResetEventSlim"/>. The real event is retained only
/// as an identity object: its signal, waiters, and bridge handle are modelled under weak per-instance state.
/// </summary>
public static class ControlledManualResetEventSlim
{
    private const string TypeName = "System.Threading.ManualResetEventSlim";
    private const string WaitApi = TypeName + ".Wait";

    private sealed class State
    {
        public State(bool isSet, int spinCount)
        {
            IsSet = isSet;
            SpinCount = spinCount;
        }

        public object Gate { get; } = new();

        public bool IsSet { get; set; }

        public int SpinCount { get; }

        public bool Disposed { get; set; }

        public WaitHandle? WaitHandle { get; set; }

        public List<SimulationWaiter> Waiters { get; } = [];
    }

    private static readonly ConditionalWeakTable<ManualResetEventSlim, State> States = new();

    /// <summary>Controlled <c>new ManualResetEventSlim()</c>.</summary>
    public static ManualResetEventSlim Create()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        return CreateCore(initialState: false, spinCount: null);
    }

    /// <summary>Controlled <c>new ManualResetEventSlim(bool)</c>.</summary>
    public static ManualResetEventSlim Create(bool initialState)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        return CreateCore(initialState, spinCount: null);
    }

    /// <summary>Controlled <c>new ManualResetEventSlim(bool, int)</c>.</summary>
    public static ManualResetEventSlim Create(bool initialState, int spinCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        return CreateCore(initialState, spinCount);
    }

    /// <summary>Gets whether the controlled event is signalled.</summary>
    public static bool IsSet(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsSet");
        ArgumentNullException.ThrowIfNull(instance);
        State state = GetState(instance, TypeName + ".get_IsSet");
        lock (state.Gate)
        {
            // The BCL exposes IsSet after disposal, so this is intentionally not GetUsableState.
            return state.IsSet;
        }
    }

    /// <summary>Gets the observable configured spin count. It is metadata only and never causes a busy spin.</summary>
    public static int SpinCount(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_SpinCount");
        ArgumentNullException.ThrowIfNull(instance);
        State state = GetState(instance, TypeName + ".get_SpinCount");
        lock (state.Gate)
        {
            // The BCL exposes SpinCount after disposal.
            return state.SpinCount;
        }
    }

    /// <summary>Sets the event and releases every blocked waiter.</summary>
    public static void Set(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Set");
        ArgumentNullException.ThrowIfNull(instance);
        State state = GetState(instance, TypeName + ".Set");
        List<SimulationWaiter.Claim> completed;
        WaitHandle? bridge;
        lock (state.Gate)
        {
            ThrowIfDisposed(state);
            state.IsSet = true;
            bridge = state.WaitHandle;
            completed = SimulationWaiter.TakeAll(state.Waiters);
        }

        if (bridge is not null)
        {
            ControlledWaitHandle.UpdateBridgeSignal(bridge, signaled: true);
        }

        CompleteWaiters(completed);
    }

    /// <summary>Resets the event, making subsequent waits block until a Set.</summary>
    public static void Reset(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Reset");
        ArgumentNullException.ThrowIfNull(instance);
        State state = GetState(instance, TypeName + ".Reset");
        WaitHandle? bridge;
        lock (state.Gate)
        {
            ThrowIfDisposed(state);
            state.IsSet = false;
            bridge = state.WaitHandle;
        }

        if (bridge is not null)
        {
            ControlledWaitHandle.UpdateBridgeSignal(bridge, signaled: false);
        }
    }

    /// <summary>Waits until the event is signalled.</summary>
    public static void Wait(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        WaitControlled(instance, Timeout.Infinite, CancellationToken.None);
    }

    /// <summary>Waits until the event is signalled or cancellation is requested.</summary>
    public static void Wait(ManualResetEventSlim instance, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        WaitControlled(instance, Timeout.Infinite, cancellationToken);
    }

    /// <summary>Waits until the event is signalled or the virtual timeout expires.</summary>
    public static bool Wait(ManualResetEventSlim instance, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, millisecondsTimeout, CancellationToken.None);
    }

    /// <summary>Waits until the event is signalled, cancelled, or the virtual timeout expires.</summary>
    public static bool Wait(ManualResetEventSlim instance, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Waits until the event is signalled or the virtual timeout expires.</summary>
    public static bool Wait(ManualResetEventSlim instance, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, ToMilliseconds(timeout), CancellationToken.None);
    }

    /// <summary>Waits until the event is signalled, cancelled, or the virtual timeout expires.</summary>
    public static bool Wait(ManualResetEventSlim instance, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        return WaitControlled(instance, ToMilliseconds(timeout), cancellationToken);
    }

    /// <summary>Gets the cached controlled manual-reset bridge for this event.</summary>
    public static WaitHandle WaitHandle(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_WaitHandle");
        ArgumentNullException.ThrowIfNull(instance);
        State state = GetState(instance, TypeName + ".get_WaitHandle");
        lock (state.Gate)
        {
            ThrowIfDisposed(state);
            return state.WaitHandle ??= ControlledWaitHandle.CreateBridge(state.IsSet);
        }
    }

    /// <summary>Disposes the controlled model and faults blocked waiters.</summary>
    public static void Dispose(ManualResetEventSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Dispose");
        ArgumentNullException.ThrowIfNull(instance);
        State state = GetState(instance, TypeName + ".Dispose");
        List<SimulationWaiter.Claim> completed;
        WaitHandle? bridge;
        lock (state.Gate)
        {
            if (state.Disposed)
            {
                return;
            }

            state.Disposed = true;
            bridge = state.WaitHandle;
            completed = SimulationWaiter.TakeAll(state.Waiters);
        }

        FaultDisposedWaiters(completed);
        ControlledWaitHandle.DisposeBridge(bridge);
    }

    private static ManualResetEventSlim CreateCore(bool initialState, int? spinCount)
    {
        ManualResetEventSlim instance = spinCount is int configuredSpinCount
            ? new ManualResetEventSlim(initialState, configuredSpinCount)
            : new ManualResetEventSlim(initialState);
        States.Add(instance, new State(initialState, instance.SpinCount));
        return instance;
    }

    private static State GetState(ManualResetEventSlim instance, string api)
    {
        if (States.TryGetValue(instance, out State? state))
        {
            return state;
        }

        throw new SimulationApiException(
            SimulationApiCategory.ManualResetEventSlim,
            api,
            "the event was not created through the controlled ManualResetEventSlim surface, so its signal and controlled waiter state are unknown.");
    }

    private static bool WaitControlled(ManualResetEventSlim instance, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        State state = GetState(instance, WaitApi);
        SimulationWaiter waiter;
        lock (state.Gate)
        {
            // The BCL gives disposal precedence, but for a usable event cancellation takes precedence over
            // an invalid millisecond timeout and over an already-signalled state.
            ThrowIfDisposed(state);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTimeout(millisecondsTimeout);
            if (state.IsSet)
            {
                return true;
            }

            if (millisecondsTimeout == 0)
            {
                return false;
            }

            waiter = EnqueueUnderLock(state, millisecondsTimeout);
        }

        AttachCancellation(state, waiter, cancellationToken);
        SimulationTaskRuntime.DrainUntil(() => waiter.Task.IsCompleted, WaitApi, cancellationToken);
        return waiter.Task.GetAwaiter().GetResult();
    }

    private static SimulationWaiter EnqueueUnderLock(State state, int millisecondsTimeout)
    {
        var waiter = new SimulationWaiter();
        state.Waiters.Add(waiter);
        if (millisecondsTimeout != Timeout.Infinite)
        {
            SimulationWaiter.AttachDeadline(
                waiter,
                TimeSpan.FromMilliseconds(millisecondsTimeout),
                WaitApi,
                pendingWaiter => ResolveTimedOut(state, pendingWaiter));
        }

        return waiter;
    }

    private static void AttachCancellation(State state, SimulationWaiter waiter, CancellationToken cancellationToken) =>
        SimulationWaiter.AttachCancellation(
            state.Gate,
            waiter,
            (pendingWaiter, token) => ResolveCanceled(state, pendingWaiter, token),
            cancellationToken);

    private static void ResolveCanceled(State state, SimulationWaiter waiter, CancellationToken cancellationToken)
    {
        SimulationWaiter.Claim claim;
        lock (state.Gate)
        {
            if (!SimulationWaiter.TryTake(state.Waiters, waiter, out claim))
            {
                return;
            }
        }

        claim.Cancel(cancellationToken);
    }

    private static void ResolveTimedOut(State state, SimulationWaiter waiter)
    {
        SimulationWaiter.Claim claim;
        lock (state.Gate)
        {
            if (!SimulationWaiter.TryTake(
                state.Waiters,
                waiter,
                out claim,
                cancelDeadline: false))
            {
                return;
            }
        }

        claim.Complete(false);
    }

    private static void CompleteWaiters(List<SimulationWaiter.Claim> completed)
    {
        foreach (SimulationWaiter.Claim claim in completed)
        {
            claim.Complete(true);
        }
    }

    private static void FaultDisposedWaiters(List<SimulationWaiter.Claim> completed)
    {
        foreach (SimulationWaiter.Claim claim in completed)
        {
            claim.Fault(new ObjectDisposedException(nameof(ManualResetEventSlim)));
        }
    }

    private static void ThrowIfDisposed(State state) =>
        ObjectDisposedException.ThrowIf(state.Disposed, typeof(ManualResetEventSlim));

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
