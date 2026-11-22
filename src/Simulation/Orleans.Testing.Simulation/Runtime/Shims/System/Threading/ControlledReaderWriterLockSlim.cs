using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Controlled receiver-first shims for <see cref="ReaderWriterLockSlim"/>. The real lock is retained only
/// as an identity handle; ownership, recursion, waiter counts, and timeouts are modelled per logical
/// strand. Contended entries cooperatively drain the simulation loop rather than blocking a physical
/// thread. Once a writer is waiting, new readers and upgradeable readers are held back, preventing writer
/// starvation while preserving an upgradeable owner's ability to upgrade.
/// </summary>
public static class ControlledReaderWriterLockSlim
{
    private const string TypeName = "System.Threading.ReaderWriterLockSlim";
    private const string EnterReadApi = TypeName + ".EnterReadLock";
    private const string EnterUpgradeableApi = TypeName + ".EnterUpgradeableReadLock";
    private const string EnterWriteApi = TypeName + ".EnterWriteLock";
    private const string TryEnterReadApi = TypeName + ".TryEnterReadLock";
    private const string TryEnterUpgradeableApi = TypeName + ".TryEnterUpgradeableReadLock";
    private const string TryEnterWriteApi = TypeName + ".TryEnterWriteLock";

    private enum WaitKind
    {
        Read,
        UpgradeableRead,
        Write,
    }

    private enum WaitOutcome
    {
        Pending,
        TimedOut,
        Disposed,
    }

    private sealed class Waiter
    {
        public required WaitKind Kind { get; init; }

        public required long Owner { get; init; }

        public WaitOutcome Outcome { get; set; }

        public ISimulationTimer? Deadline { get; set; }
    }

    private sealed class State
    {
        public required LockRecursionPolicy RecursionPolicy { get; init; }

        public bool Disposed { get; set; }

        public Dictionary<long, int> Readers { get; } = [];

        public long? UpgradeableOwner { get; set; }

        public int UpgradeableRecursion { get; set; }

        public long? WriterOwner { get; set; }

        public int WriterRecursion { get; set; }

        public List<Waiter> Waiters { get; } = [];
    }

    private static readonly ConditionalWeakTable<ReaderWriterLockSlim, State> States = new();

    /// <summary>Controlled <c>new ReaderWriterLockSlim()</c>.</summary>
    public static ReaderWriterLockSlim Create()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        return CreateCore(LockRecursionPolicy.NoRecursion);
    }

    /// <summary>Controlled <c>new ReaderWriterLockSlim(LockRecursionPolicy)</c>.</summary>
    public static ReaderWriterLockSlim Create(LockRecursionPolicy recursionPolicy)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + "..ctor");
        if (recursionPolicy is not LockRecursionPolicy.NoRecursion and not LockRecursionPolicy.SupportsRecursion)
        {
            throw new ArgumentOutOfRangeException(nameof(recursionPolicy));
        }

        return CreateCore(recursionPolicy);
    }

    /// <summary>Gets the controlled lock's recursion policy.</summary>
    public static LockRecursionPolicy RecursionPolicy(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_RecursionPolicy");
        return GetState(instance, TypeName + ".get_RecursionPolicy").RecursionPolicy;
    }

    /// <summary>Gets the number of logical strands holding a normal read lock.</summary>
    public static int CurrentReadCount(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_CurrentReadCount");
        var state = GetState(instance, TypeName + ".get_CurrentReadCount");
        return state.Readers.Count;
    }

    /// <summary>Gets whether the current logical strand holds a normal read lock.</summary>
    public static bool IsReadLockHeld(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsReadLockHeld");
        var state = GetState(instance, TypeName + ".get_IsReadLockHeld");
        return ReadCount(state, SimulationSynchronizationFlow.CurrentId) != 0;
    }

    /// <summary>Gets whether the current logical strand holds the upgradeable read lock.</summary>
    public static bool IsUpgradeableReadLockHeld(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsUpgradeableReadLockHeld");
        var state = GetState(instance, TypeName + ".get_IsUpgradeableReadLockHeld");
        return state.UpgradeableOwner == SimulationSynchronizationFlow.CurrentId;
    }

    /// <summary>Gets whether the current logical strand holds the write lock.</summary>
    public static bool IsWriteLockHeld(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_IsWriteLockHeld");
        var state = GetState(instance, TypeName + ".get_IsWriteLockHeld");
        return state.WriterOwner == SimulationSynchronizationFlow.CurrentId;
    }

    /// <summary>Gets the current logical strand's normal read recursion count.</summary>
    public static int RecursiveReadCount(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_RecursiveReadCount");
        var state = GetState(instance, TypeName + ".get_RecursiveReadCount");
        return ReadCount(state, SimulationSynchronizationFlow.CurrentId);
    }

    /// <summary>Gets the current logical strand's upgradeable read recursion count.</summary>
    public static int RecursiveUpgradeCount(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_RecursiveUpgradeCount");
        var state = GetState(instance, TypeName + ".get_RecursiveUpgradeCount");
        return state.UpgradeableOwner == SimulationSynchronizationFlow.CurrentId ? state.UpgradeableRecursion : 0;
    }

    /// <summary>Gets the current logical strand's write recursion count.</summary>
    public static int RecursiveWriteCount(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".get_RecursiveWriteCount");
        var state = GetState(instance, TypeName + ".get_RecursiveWriteCount");
        return state.WriterOwner == SimulationSynchronizationFlow.CurrentId ? state.WriterRecursion : 0;
    }

    /// <summary>Gets the count of pending normal-read waiters.</summary>
    public static int WaitingReadCount(ReaderWriterLockSlim instance) =>
        WaitingCount(instance, WaitKind.Read, TypeName + ".get_WaitingReadCount");

    /// <summary>Gets the count of pending upgradeable-read waiters.</summary>
    public static int WaitingUpgradeCount(ReaderWriterLockSlim instance) =>
        WaitingCount(instance, WaitKind.UpgradeableRead, TypeName + ".get_WaitingUpgradeCount");

    /// <summary>Gets the count of pending write waiters, including upgrade-to-write attempts.</summary>
    public static int WaitingWriteCount(ReaderWriterLockSlim instance) =>
        WaitingCount(instance, WaitKind.Write, TypeName + ".get_WaitingWriteCount");

    /// <summary>Enters a normal read lock, cooperatively waiting when required.</summary>
    public static void EnterReadLock(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EnterReadApi);
        Enter(instance, WaitKind.Read, Timeout.Infinite, EnterReadApi);
    }

    /// <summary>Attempts to enter a normal read lock using a controlled timeout.</summary>
    public static bool TryEnterReadLock(ReaderWriterLockSlim instance, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterReadApi);
        ValidateTimeout(millisecondsTimeout);
        return Enter(instance, WaitKind.Read, millisecondsTimeout, TryEnterReadApi);
    }

    /// <summary>Attempts to enter a normal read lock using a controlled timeout.</summary>
    public static bool TryEnterReadLock(ReaderWriterLockSlim instance, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterReadApi);
        ArgumentNullException.ThrowIfNull(instance);
        return TryEnterReadLock(instance, ToMilliseconds(timeout));
    }

    /// <summary>Exits a normal read lock held by the current logical strand.</summary>
    public static void ExitReadLock(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".ExitReadLock");
        var state = GetUsableState(instance, TypeName + ".ExitReadLock");
        var owner = SimulationSynchronizationFlow.CurrentId;
        var count = ReadCount(state, owner);
        if (count == 0)
        {
            throw new SynchronizationLockException();
        }

        SetReadCount(state, owner, count - 1);
        RaceSynchronization.Signal(instance);
    }

    /// <summary>Enters the sole upgradeable read lock, cooperatively waiting when required.</summary>
    public static void EnterUpgradeableReadLock(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EnterUpgradeableApi);
        Enter(instance, WaitKind.UpgradeableRead, Timeout.Infinite, EnterUpgradeableApi);
    }

    /// <summary>Attempts to enter the upgradeable read lock using a controlled timeout.</summary>
    public static bool TryEnterUpgradeableReadLock(ReaderWriterLockSlim instance, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterUpgradeableApi);
        ValidateTimeout(millisecondsTimeout);
        return Enter(instance, WaitKind.UpgradeableRead, millisecondsTimeout, TryEnterUpgradeableApi);
    }

    /// <summary>Attempts to enter the upgradeable read lock using a controlled timeout.</summary>
    public static bool TryEnterUpgradeableReadLock(ReaderWriterLockSlim instance, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterUpgradeableApi);
        ArgumentNullException.ThrowIfNull(instance);
        return TryEnterUpgradeableReadLock(instance, ToMilliseconds(timeout));
    }

    /// <summary>Exits the upgradeable read lock held by the current logical strand.</summary>
    public static void ExitUpgradeableReadLock(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".ExitUpgradeableReadLock");
        var state = GetUsableState(instance, TypeName + ".ExitUpgradeableReadLock");
        var owner = SimulationSynchronizationFlow.CurrentId;
        if (state.UpgradeableOwner != owner)
        {
            throw new SynchronizationLockException();
        }

        if (--state.UpgradeableRecursion == 0)
        {
            state.UpgradeableOwner = null;
        }

        RaceSynchronization.ExitExclusive(instance);
        RaceSynchronization.Signal(instance);
    }

    /// <summary>Enters the write lock, cooperatively waiting when required.</summary>
    public static void EnterWriteLock(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EnterWriteApi);
        Enter(instance, WaitKind.Write, Timeout.Infinite, EnterWriteApi);
    }

    /// <summary>Attempts to enter the write lock using a controlled timeout.</summary>
    public static bool TryEnterWriteLock(ReaderWriterLockSlim instance, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterWriteApi);
        ValidateTimeout(millisecondsTimeout);
        return Enter(instance, WaitKind.Write, millisecondsTimeout, TryEnterWriteApi);
    }

    /// <summary>Attempts to enter the write lock using a controlled timeout.</summary>
    public static bool TryEnterWriteLock(ReaderWriterLockSlim instance, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TryEnterWriteApi);
        ArgumentNullException.ThrowIfNull(instance);
        return TryEnterWriteLock(instance, ToMilliseconds(timeout));
    }

    /// <summary>Exits the write lock held by the current logical strand.</summary>
    public static void ExitWriteLock(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".ExitWriteLock");
        var state = GetUsableState(instance, TypeName + ".ExitWriteLock");
        if (state.WriterOwner != SimulationSynchronizationFlow.CurrentId)
        {
            throw new SynchronizationLockException();
        }

        if (--state.WriterRecursion == 0)
        {
            state.WriterOwner = null;
        }

        RaceSynchronization.ExitExclusive(instance);
        RaceSynchronization.Signal(instance);
    }

    /// <summary>Disposes the controlled model and rejects all further operations.</summary>
    public static void Dispose(ReaderWriterLockSlim instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TypeName + ".Dispose");
        var state = GetState(instance, TypeName + ".Dispose");
        if (state.Disposed)
        {
            return;
        }

        if (state.Readers.Count != 0 ||
            state.UpgradeableOwner is not null ||
            state.WriterOwner is not null ||
            state.Waiters.Any(waiter => waiter.Outcome == WaitOutcome.Pending))
        {
            throw new SynchronizationLockException();
        }

        state.Disposed = true;
    }

    private static ReaderWriterLockSlim CreateCore(LockRecursionPolicy recursionPolicy)
    {
        var instance = new ReaderWriterLockSlim(recursionPolicy);
        States.Add(instance, new State { RecursionPolicy = recursionPolicy });
        return instance;
    }

    private static int WaitingCount(ReaderWriterLockSlim instance, WaitKind kind, string api)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        var state = GetState(instance, api);
        return state.Waiters.Count(waiter => waiter.Kind == kind && waiter.Outcome == WaitOutcome.Pending);
    }

    private static bool Enter(
        ReaderWriterLockSlim instance,
        WaitKind kind,
        int millisecondsTimeout,
        string api)
    {
        var state = GetUsableState(instance, api);
        var owner = SimulationSynchronizationFlow.CurrentId;

        if (IsRecursive(state, kind, owner))
        {
            EnsureRecursionAllowed(state);
            Acquire(state, kind, owner);
            TrackAcquisition(instance, kind);
            return true;
        }

        ValidateCrossModeEntry(state, kind, owner);
        if (CanAcquire(state, kind, owner, waiter: null))
        {
            Acquire(state, kind, owner);
            TrackAcquisition(instance, kind);
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        var waiter = new Waiter { Kind = kind, Owner = owner };
        state.Waiters.Add(waiter);
        if (millisecondsTimeout != Timeout.Infinite)
        {
            waiter.Deadline = SimulationTaskRuntime.RegisterTimeout(
                TimeSpan.FromMilliseconds(millisecondsTimeout),
                () => waiter.Outcome = WaitOutcome.TimedOut,
                api);
        }

        SimulationTaskRuntime.DrainUntil(
            () => waiter.Outcome != WaitOutcome.Pending || CanAcquire(state, kind, owner, waiter),
            api,
            CancellationToken.None);

        if (waiter.Outcome == WaitOutcome.Disposed)
        {
            state.Waiters.Remove(waiter);
            throw new ObjectDisposedException(nameof(ReaderWriterLockSlim));
        }

        if (waiter.Outcome == WaitOutcome.TimedOut)
        {
            state.Waiters.Remove(waiter);
            return false;
        }

        waiter.Deadline?.Cancel();
        state.Waiters.Remove(waiter);
        Acquire(state, kind, owner);
        TrackAcquisition(instance, kind);
        return true;
    }

    private static bool CanAcquire(State state, WaitKind kind, long owner, Waiter? waiter)
    {
        if (state.Disposed || (state.WriterOwner is not null && state.WriterOwner != owner))
        {
            return false;
        }

        if (waiter is null && state.Waiters.Count != 0 && !IsUpgradeableOwnerUpgrade(state, kind, owner))
        {
            return false;
        }

        return kind switch
        {
            WaitKind.Read => !HasPendingWriter(state),
            WaitKind.UpgradeableRead =>
                state.UpgradeableOwner is null && !HasPendingWriter(state),
            WaitKind.Write =>
                (IsUpgradeableOwnerUpgrade(state, kind, owner) ||
                 waiter is null ||
                 IsFirstWriter(state, waiter)) &&
                CanEnterWrite(state, owner),
            _ => false,
        };
    }

    private static bool CanEnterWrite(State state, long owner)
    {
        var ownReads = ReadCount(state, owner);
        var allReads = state.Readers.Values.Sum();
        return allReads == ownReads && (state.UpgradeableOwner is null || state.UpgradeableOwner == owner);
    }

    private static bool HasPendingWriter(State state) =>
        state.Waiters.Any(waiter => waiter.Kind == WaitKind.Write && waiter.Outcome == WaitOutcome.Pending);

    private static bool IsFirstWriter(State state, Waiter waiter) =>
        state.Waiters.FirstOrDefault(candidate =>
            candidate.Kind == WaitKind.Write && candidate.Outcome == WaitOutcome.Pending) == waiter;

    private static bool IsUpgradeableOwnerUpgrade(State state, WaitKind kind, long owner) =>
        kind == WaitKind.Write && state.UpgradeableOwner == owner;

    private static bool IsRecursive(State state, WaitKind kind, long owner) =>
        kind switch
        {
            WaitKind.Read => ReadCount(state, owner) != 0,
            WaitKind.UpgradeableRead => state.UpgradeableOwner == owner,
            WaitKind.Write => state.WriterOwner == owner,
            _ => false,
        };

    private static void ValidateCrossModeEntry(State state, WaitKind kind, long owner)
    {
        var holdsRead = ReadCount(state, owner) != 0;
        var holdsUpgradeable = state.UpgradeableOwner == owner;
        var holdsWrite = state.WriterOwner == owner;
        if (state.RecursionPolicy == LockRecursionPolicy.SupportsRecursion)
        {
            if (holdsWrite)
            {
                return;
            }

            if (holdsRead && kind is WaitKind.Write or WaitKind.UpgradeableRead)
            {
                throw new LockRecursionException();
            }

            return;
        }

        var allowedCrossModeEntry =
            (kind == WaitKind.Read && holdsUpgradeable && !holdsWrite) ||
            (kind == WaitKind.Write && holdsUpgradeable && !holdsRead && !holdsWrite);
        if ((holdsRead || holdsUpgradeable || holdsWrite) && !allowedCrossModeEntry)
        {
            throw new LockRecursionException();
        }
    }

    private static void EnsureRecursionAllowed(State state)
    {
        if (state.RecursionPolicy == LockRecursionPolicy.NoRecursion)
        {
            throw new LockRecursionException();
        }
    }

    private static void Acquire(State state, WaitKind kind, long owner)
    {
        switch (kind)
        {
            case WaitKind.Read:
                SetReadCount(state, owner, ReadCount(state, owner) + 1);
                break;
            case WaitKind.UpgradeableRead:
                state.UpgradeableOwner = owner;
                state.UpgradeableRecursion++;
                break;
            case WaitKind.Write:
                state.WriterOwner = owner;
                state.WriterRecursion++;
                break;
        }
    }

    private static void TrackAcquisition(ReaderWriterLockSlim instance, WaitKind kind)
    {
        if (kind == WaitKind.Read)
        {
            RaceSynchronization.Wait(instance);
        }
        else
        {
            RaceSynchronization.Wait(instance);
            RaceSynchronization.EnterExclusive(instance);
        }
    }

    private static int ReadCount(State state, long owner) =>
        state.Readers.TryGetValue(owner, out var count) ? count : 0;

    private static void SetReadCount(State state, long owner, int count)
    {
        if (count == 0)
        {
            state.Readers.Remove(owner);
        }
        else
        {
            state.Readers[owner] = count;
        }
    }

    private static State GetState(ReaderWriterLockSlim instance, string api)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (States.TryGetValue(instance, out State? state))
        {
            return state;
        }

        throw new InvalidOperationException(
            $"{api} requires a ReaderWriterLockSlim created through the controlled surface.");
    }

    private static State GetUsableState(ReaderWriterLockSlim instance, string api)
    {
        var state = GetState(instance, api);
        ObjectDisposedException.ThrowIf(state.Disposed, instance);
        return state;
    }

    private static void ValidateTimeout(int millisecondsTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        var totalMilliseconds = timeout.TotalMilliseconds;
        if (totalMilliseconds < Timeout.Infinite || totalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return (int)totalMilliseconds;
    }
}
