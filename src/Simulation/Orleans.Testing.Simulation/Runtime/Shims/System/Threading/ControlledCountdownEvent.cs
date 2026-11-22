using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>A fully controlled replacement for <see cref="global::System.Threading.CountdownEvent"/>.</summary>
public sealed class ControlledCountdownEvent : IDisposable
{
    private const string TypeName = "System.Threading.CountdownEvent";
    private const string WaitApi = TypeName + ".Wait";

    private readonly object _gate = new();
    private readonly List<SimulationWaiter> _waiters = [];
    private bool _disposed;
    private int _currentCount;
    private int _initialCount;
    private WaitHandle? _waitHandle;

    /// <summary>Initializes a controlled countdown event.</summary>
    public ControlledCountdownEvent(int initialCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        ValidateCount(initialCount, nameof(initialCount));
        _initialCount = initialCount;
        _currentCount = initialCount;
    }

    /// <summary>Gets the current count.</summary>
    public int CurrentCount
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_CurrentCount");
            lock (_gate)
            {
                return _currentCount;
            }
        }
    }

    /// <summary>Gets the configured initial count.</summary>
    public int InitialCount
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_InitialCount");
            lock (_gate)
            {
                return _initialCount;
            }
        }
    }

    /// <summary>Gets whether the count has reached zero.</summary>
    public bool IsSet
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsSet");
            lock (_gate)
            {
                return _currentCount == 0;
            }
        }
    }

    /// <summary>Gets the cached controlled manual-reset bridge for the set state.</summary>
    public WaitHandle WaitHandle
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_WaitHandle");
            lock (_gate)
            {
                ThrowIfDisposed();
                return _waitHandle ??= ControlledWaitHandle.CreateBridge(_currentCount == 0);
            }
        }
    }

    /// <summary>Adds one to the count.</summary>
    public void AddCount() => AddCount(1);

    /// <summary>Adds to the count.</summary>
    public void AddCount(int signalCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".AddCount");
        AddCountCore(signalCount, throwIfSet: true);
    }

    /// <summary>Attempts to add one to the count.</summary>
    public bool TryAddCount() => TryAddCount(1);

    /// <summary>Attempts to add to the count.</summary>
    public bool TryAddCount(int signalCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".TryAddCount");
        return AddCountCore(signalCount, throwIfSet: false);
    }

    /// <summary>Signals one count and returns whether the event became set.</summary>
    public bool Signal() => Signal(1);

    /// <summary>Signals counts and returns whether the event became set.</summary>
    public bool Signal(int signalCount)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Signal");
        ArgumentOutOfRangeException.ThrowIfLessThan(signalCount, 1);

        List<SimulationWaiter.Claim>? waiters = null;
        WaitHandle? bridge = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (signalCount > _currentCount)
            {
                throw new InvalidOperationException("The signal count exceeds the current count.");
            }

            _currentCount -= signalCount;
            if (_currentCount == 0)
            {
                waiters = TakeWaitersUnderLock();
                bridge = _waitHandle;
            }
        }

        if (bridge is not null)
        {
            ControlledWaitHandle.UpdateBridgeSignal(bridge, signaled: true);
        }

        CompleteWaiters(waiters, exception: null);
        return waiters is not null;
    }

    /// <summary>Restores the current count to the configured initial count.</summary>
    public void Reset() => Reset(_initialCount);

    /// <summary>Changes the initial count and restores the current count.</summary>
    public void Reset(int count)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Reset");
        ValidateCount(count, nameof(count));
        WaitHandle? bridge;
        List<SimulationWaiter.Claim>? waiters = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            _initialCount = count;
            _currentCount = count;
            bridge = _waitHandle;
            if (count == 0)
            {
                waiters = TakeWaitersUnderLock();
            }
        }

        if (bridge is not null)
        {
            ControlledWaitHandle.UpdateBridgeSignal(bridge, count == 0);
        }

        CompleteWaiters(waiters, exception: null);
    }

    /// <summary>Waits until the countdown reaches zero.</summary>
    public void Wait() => WaitCore(Timeout.Infinite, CancellationToken.None);

    /// <summary>Waits until the countdown reaches zero or cancellation is requested.</summary>
    public void Wait(CancellationToken cancellationToken) => WaitCore(Timeout.Infinite, cancellationToken);

    /// <summary>Waits until the countdown reaches zero or the virtual deadline elapses.</summary>
    public bool Wait(int millisecondsTimeout) => WaitCore(millisecondsTimeout, CancellationToken.None);

    /// <summary>Waits until the countdown reaches zero, cancellation, or the virtual deadline elapses.</summary>
    public bool Wait(int millisecondsTimeout, CancellationToken cancellationToken) =>
        WaitCore(millisecondsTimeout, cancellationToken);

    /// <summary>Waits until the countdown reaches zero or the virtual deadline elapses.</summary>
    public bool Wait(TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return WaitCore(ToMilliseconds(timeout), CancellationToken.None);
    }

    /// <summary>Waits until the countdown reaches zero, cancellation, or the virtual deadline elapses.</summary>
    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return WaitCore(ToMilliseconds(timeout), cancellationToken);
    }

    /// <summary>Disposes this countdown event and faults blocked waiters.</summary>
    public void Dispose()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Dispose");
        List<SimulationWaiter.Claim> waiters;
        WaitHandle? bridge;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waiters = TakeWaitersUnderLock();
            bridge = _waitHandle;
        }

        CompleteWaiters(waiters, new ObjectDisposedException(nameof(ControlledCountdownEvent)));
        ControlledWaitHandle.DisposeBridge(bridge);
    }

    private bool AddCountCore(int signalCount, bool throwIfSet)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(signalCount, 1);

        WaitHandle? bridge = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_currentCount == 0)
            {
                if (throwIfSet)
                {
                    throw new InvalidOperationException("The countdown event is already set.");
                }

                return false;
            }

            if (signalCount > int.MaxValue - _currentCount)
            {
                throw new InvalidOperationException("The resulting count exceeds Int32.MaxValue.");
            }

            _currentCount += signalCount;
            bridge = _waitHandle;
        }

        if (bridge is not null)
        {
            ControlledWaitHandle.UpdateBridgeSignal(bridge, signaled: false);
        }

        return true;
    }

    private bool WaitCore(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(millisecondsTimeout);

        SimulationWaiter? waiter = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_currentCount == 0)
            {
                return true;
            }

            if (millisecondsTimeout == 0)
            {
                return false;
            }

            waiter = new SimulationWaiter();
            _waiters.Add(waiter);
            if (millisecondsTimeout != Timeout.Infinite)
            {
                SimulationWaiter.AttachDeadline(
                    waiter,
                    TimeSpan.FromMilliseconds(millisecondsTimeout),
                    WaitApi,
                    pendingWaiter => TimeoutWaiter(pendingWaiter));
            }
        }

        AttachCancellation(waiter!, cancellationToken);
        SimulationTaskRuntime.DrainUntil(() => waiter!.Task.IsCompleted, WaitApi, cancellationToken);
        return waiter!.Task.GetAwaiter().GetResult();
    }

    private void AttachCancellation(SimulationWaiter waiter, CancellationToken cancellationToken) =>
        SimulationWaiter.AttachCancellation(
            _gate,
            waiter,
            (pendingWaiter, token) => CancelWaiter(pendingWaiter, token),
            cancellationToken);

    private void TimeoutWaiter(SimulationWaiter waiter) => CompleteFailedWaiter(waiter, null, timedOut: true);

    private void CancelWaiter(SimulationWaiter waiter, CancellationToken cancellationToken) =>
        CompleteFailedWaiter(waiter, cancellationToken, timedOut: false);

    private void CompleteFailedWaiter(SimulationWaiter waiter, CancellationToken? cancellationToken, bool timedOut)
    {
        SimulationWaiter.Claim claim;
        lock (_gate)
        {
            if (!SimulationWaiter.TryTake(
                _waiters,
                waiter,
                out claim,
                cancelDeadline: !timedOut))
            {
                return;
            }
        }

        if (timedOut)
        {
            claim.Complete(false);
        }
        else
        {
            claim.Cancel(cancellationToken!.Value);
        }
    }

    private List<SimulationWaiter.Claim> TakeWaitersUnderLock() => SimulationWaiter.TakeAll(_waiters);

    private static void CompleteWaiters(IEnumerable<SimulationWaiter.Claim>? waiters, Exception? exception)
    {
        if (waiters is null)
        {
            return;
        }

        foreach (SimulationWaiter.Claim waiter in waiters)
        {
            if (exception is null)
            {
                waiter.Complete(true);
            }
            else
            {
                waiter.Fault(exception);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, typeof(ControlledCountdownEvent));

    private static void ValidateCount(int count, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count, parameterName);
    }

    private static void ValidateTimeout(int millisecondsTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        long milliseconds = (long)timeout.TotalMilliseconds;
        if (milliseconds < Timeout.Infinite || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return (int)milliseconds;
    }
}
