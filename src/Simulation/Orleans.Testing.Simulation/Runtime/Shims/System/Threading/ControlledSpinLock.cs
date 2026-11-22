using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// A cooperative, value-type replacement for <see cref="SpinLock"/>. Its state is held directly in the
/// struct, preserving the copy semantics of <see cref="SpinLock"/> while replacing CPU spinning with
/// deterministic scheduler pumping.
/// </summary>
public struct ControlledSpinLock
{
    private const string TypeName = "System.Threading.SpinLock";
    private const string EnterApi = TypeName + ".Enter";
    private const string TryEnterApi = TypeName + ".TryEnter";

    // A bool is kept separately from the owner because the root controlled strand has id zero.
    private bool _isHeld;
    // SpinLock's all-zero default has owner tracking enabled. Store the inverse so default(ControlledSpinLock)
    // preserves that BCL contract while the bool constructor can explicitly disable tracking.
    private bool _ownerTrackingDisabled;
    private long _ownerId;

    /// <summary>Initializes a controlled spin lock with optional logical-strand owner tracking.</summary>
    /// <param name="enableThreadOwnerTracking">Whether ownership is tracked by controlled logical strand.</param>
    public ControlledSpinLock(bool enableThreadOwnerTracking)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        _isHeld = false;
        _ownerTrackingDisabled = !enableThreadOwnerTracking;
        _ownerId = SimulationSynchronizationFlow.None;
    }

    /// <summary>Gets whether any controlled strand holds the lock.</summary>
    public readonly bool IsHeld =>
        (SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsHeld"), _isHeld).Item2;

    /// <summary>Gets whether the current controlled strand holds this owner-tracked lock.</summary>
    /// <exception cref="InvalidOperationException">Owner tracking is disabled.</exception>
    public readonly bool IsHeldByCurrentThread
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsHeldByCurrentThread");
            if (_ownerTrackingDisabled)
            {
                throw new InvalidOperationException("Thread owner tracking is disabled.");
            }

            return _isHeld && _ownerId == SimulationSynchronizationFlow.CurrentId;
        }
    }

    /// <summary>Gets whether owner tracking was enabled when this lock was constructed.</summary>
    public readonly bool IsThreadOwnerTrackingEnabled =>
        (SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsThreadOwnerTrackingEnabled"), !_ownerTrackingDisabled).Item2;

    /// <summary>Enters the lock, cooperatively pumping the scheduler while it is held.</summary>
    /// <param name="lockTaken">Must be <see langword="false"/> on entry; set after acquisition.</param>
    public void Enter(ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EnterApi);
        RequireLockNotTaken(lockTaken);
        if (TryAcquire())
        {
            lockTaken = true;
            return;
        }

        Acquire(Timeout.Infinite);
        lockTaken = true;
    }

    /// <summary>Attempts an immediate, non-blocking acquisition.</summary>
    /// <param name="lockTaken">Must be <see langword="false"/> on entry; set after acquisition.</param>
    public void TryEnter(ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        RequireLockNotTaken(lockTaken);
        lockTaken = TryAcquire();
    }

    /// <summary>Attempts to enter the lock within a controlled virtual-time timeout.</summary>
    /// <param name="millisecondsTimeout">Zero for an immediate attempt, -1 to wait indefinitely, or a finite timeout.</param>
    /// <param name="lockTaken">Must be <see langword="false"/> on entry; set after acquisition.</param>
    public void TryEnter(int millisecondsTimeout, ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        RequireLockNotTaken(lockTaken);
        ValidateTimeout(millisecondsTimeout, nameof(millisecondsTimeout));
        if (millisecondsTimeout == 0)
        {
            lockTaken = TryAcquire();
            return;
        }

        if (TryAcquire())
        {
            lockTaken = true;
            return;
        }

        lockTaken = Acquire(millisecondsTimeout);
    }

    /// <summary>Attempts to enter the lock within a controlled virtual-time timeout.</summary>
    /// <param name="timeout">The timeout, converted to milliseconds as by <see cref="SpinLock"/>.</param>
    /// <param name="lockTaken">Must be <see langword="false"/> on entry; set after acquisition.</param>
    public void TryEnter(TimeSpan timeout, ref bool lockTaken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterApi);
        RequireLockNotTaken(lockTaken);
        int millisecondsTimeout = ToMilliseconds(timeout);
        if (millisecondsTimeout == 0)
        {
            lockTaken = TryAcquire();
            return;
        }

        if (TryAcquire())
        {
            lockTaken = true;
            return;
        }

        lockTaken = Acquire(millisecondsTimeout);
    }

    /// <summary>Releases the lock using the default memory-barrier behavior.</summary>
    public void Exit()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Exit");
        Release();
    }

    /// <summary>Releases the lock.</summary>
    /// <param name="useMemoryBarrier">Ignored because all controlled operations run on one logical thread.</param>
    public void Exit(bool useMemoryBarrier)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Exit");
        Release();
    }

    private static void RequireLockNotTaken(bool lockTaken)
    {
        if (lockTaken)
        {
            throw new ArgumentException(
                "The lockTaken argument must be set to false before calling this method.",
                nameof(lockTaken));
        }
    }

    private bool TryAcquire()
    {
        if (_isHeld)
        {
            ThrowIfRecursiveOwner();
            return false;
        }

        _isHeld = true;
        if (!_ownerTrackingDisabled)
        {
            _ownerId = SimulationSynchronizationFlow.CurrentId;
        }

        return true;
    }

    private bool Acquire(int millisecondsTimeout)
    {
        if (millisecondsTimeout == Timeout.Infinite)
        {
            while (_isHeld)
            {
                // A struct instance cannot be captured by the predicate accepted by DrainUntil. Pump one
                // controlled work item at a time so this exact referenced struct remains observable to a
                // nested strand; when no work can run, delegate deadlock detection to the scheduler.
                if (!SimulationTaskRuntime.RunOne(EnterApi, CancellationToken.None))
                {
                    SimulationTaskRuntime.DrainUntil(static () => false, EnterApi, CancellationToken.None);
                }
            }

            return TryAcquire();
        }

        bool timedOut = false;
        ISimulationTimer deadline = SimulationTaskRuntime.RegisterTimeout(
            TimeSpan.FromMilliseconds(millisecondsTimeout),
            () => timedOut = true,
            TryEnterApi);
        while (_isHeld && !timedOut)
        {
            if (!SimulationTaskRuntime.RunOne(TryEnterApi, CancellationToken.None))
            {
                // With no ready work, the only possible progress is a modelled deadline. Draining to the
                // local timeout advances virtual time without consuming CPU; if another deadline causes
                // work to become ready, the next loop iteration evaluates the shared struct again.
                SimulationTaskRuntime.DrainUntil(() => timedOut, TryEnterApi, CancellationToken.None);
            }
        }

        if (TryAcquire())
        {
            deadline.Cancel();
            return true;
        }

        return false;
    }

    private void Release()
    {
        if (!_ownerTrackingDisabled && (!_isHeld || _ownerId != SimulationSynchronizationFlow.CurrentId))
        {
            throw new SynchronizationLockException();
        }

        // Like SpinLock with owner tracking disabled, Exit cannot establish ownership and can release an
        // unheld lock. This is intentionally not strengthened in the controlled implementation.
        _isHeld = false;
        _ownerId = SimulationSynchronizationFlow.None;
    }

    private readonly void ThrowIfRecursiveOwner()
    {
        if (!_ownerTrackingDisabled && _ownerId == SimulationSynchronizationFlow.CurrentId)
        {
            throw new LockRecursionException();
        }
    }

    private static void ValidateTimeout(int millisecondsTimeout, string parameterName)
    {
        if (millisecondsTimeout < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        double totalMilliseconds = timeout.TotalMilliseconds;
        if (totalMilliseconds < int.MinValue || totalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        int millisecondsTimeout = (int)totalMilliseconds;
        ValidateTimeout(millisecondsTimeout, nameof(timeout));
        return millisecondsTimeout;
    }
}
