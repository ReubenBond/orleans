using System.Collections.Concurrent;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Deterministic replacement for <see cref="Timer"/>. Rewritten timer references are substituted with
/// this type, so no constructor or member can allocate an operating-system timer.
/// </summary>
public sealed class ControlledTimer : MarshalByRefObject, IDisposable, IAsyncDisposable, ITimer
{
    private const string Api = "System.Threading.Timer";
    private const uint MaxSupportedTimeout = 0xfffffffe;
    private static readonly ConcurrentDictionary<Guid, long> ActiveCounts = new();
    private readonly ShimTimerRegistration _registration;

    /// <summary>Creates a disabled timer whose callback state is this timer.</summary>
    public ControlledTimer(TimerCallback callback)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        ArgumentNullException.ThrowIfNull(callback);
        _registration = new ShimTimerRegistration(
            snapshot,
            callback,
            this,
            ExecutionContext.Capture(),
            OnActiveChanged);
    }

    /// <summary>Creates and arms a timer using signed millisecond values.</summary>
    public ControlledTimer(TimerCallback callback, object? state, int dueTime, int period)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        ArgumentNullException.ThrowIfNull(callback);
        _registration = CreateRegistration(snapshot, callback, state);
        _registration.Change(ValidateInt(dueTime, nameof(dueTime)), ValidateInt(period, nameof(period)));
    }

    /// <summary>Creates and arms a timer using signed 64-bit millisecond values.</summary>
    public ControlledTimer(TimerCallback callback, object? state, long dueTime, long period)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        ArgumentNullException.ThrowIfNull(callback);
        _registration = CreateRegistration(snapshot, callback, state);
        _registration.Change(ValidateLong(dueTime, nameof(dueTime)), ValidateLong(period, nameof(period)));
    }

    /// <summary>Creates and arms a timer using time spans.</summary>
    public ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        ArgumentNullException.ThrowIfNull(callback);
        _registration = CreateRegistration(snapshot, callback, state);
        _registration.Change(
            ValidateTimeSpan(dueTime, nameof(dueTime)),
            ValidateTimeSpan(period, nameof(period)));
    }

    /// <summary>Creates and arms a timer using unsigned millisecond values.</summary>
    public ControlledTimer(TimerCallback callback, object? state, uint dueTime, uint period)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        ArgumentNullException.ThrowIfNull(callback);
        _registration = CreateRegistration(snapshot, callback, state);
        _registration.Change(NormalizeUInt(dueTime), NormalizeUInt(period));
    }

    /// <summary>Gets the number of enabled controlled timers in the active simulation.</summary>
    public static long ActiveCount
    {
        get
        {
            SimulationExecutionSnapshot snapshot = RequireRuntime("get_ActiveCount");
            return ActiveCounts.TryGetValue(snapshot.Runtime.Id, out long count) ? count : 0;
        }
    }

    /// <summary>Changes the timer using signed millisecond values.</summary>
    public bool Change(int dueTime, int period)
    {
        RequireRuntime("Change");
        return _registration.Change(ValidateInt(dueTime, nameof(dueTime)), ValidateInt(period, nameof(period)));
    }

    /// <summary>Changes the timer using signed 64-bit millisecond values.</summary>
    public bool Change(long dueTime, long period)
    {
        RequireRuntime("Change");
        return _registration.Change(ValidateLong(dueTime, nameof(dueTime)), ValidateLong(period, nameof(period)));
    }

    /// <summary>Changes the timer using time spans.</summary>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        RequireRuntime("Change");
        return _registration.Change(
            ValidateTimeSpan(dueTime, nameof(dueTime)),
            ValidateTimeSpan(period, nameof(period)));
    }

    /// <summary>Changes the timer using unsigned millisecond values.</summary>
    public bool Change(uint dueTime, uint period)
    {
        RequireRuntime("Change");
        return _registration.Change(NormalizeUInt(dueTime), NormalizeUInt(period));
    }

    /// <summary>Stops the timer without waiting for a callback already running.</summary>
    public void Dispose()
    {
        RequireRuntime("Dispose");
        _registration.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the timer and signals the supplied controlled event when callbacks quiesce.</summary>
    public bool Dispose(WaitHandle notifyObject)
    {
        RequireRuntime("Dispose");
        ArgumentNullException.ThrowIfNull(notifyObject);
        return _registration.Dispose(notifyObject);
    }

    /// <summary>Stops the timer and asynchronously waits for a callback already running.</summary>
    public ValueTask DisposeAsync()
    {
        RequireRuntime("DisposeAsync");
        return _registration.DisposeAsync();
    }

    private static SimulationExecutionSnapshot RequireRuntime(string member) =>
        SimulationRuntimeDispatch.RequireActiveSimulation($"{Api}.{member}");

    private ShimTimerRegistration CreateRegistration(
        SimulationExecutionSnapshot snapshot,
        TimerCallback callback,
        object? state) =>
        new(snapshot, callback, state, ExecutionContext.Capture(), OnActiveChanged);

    private static TimeSpan ValidateInt(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, -1, parameterName);
        return value == Timeout.Infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(value);
    }

    private static TimeSpan ValidateLong(long value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, -1, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxSupportedTimeout, parameterName);
        return value == Timeout.Infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(value);
    }

    private static TimeSpan ValidateTimeSpan(TimeSpan value, string parameterName)
    {
        long milliseconds = (long)value.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, -1, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, MaxSupportedTimeout, parameterName);
        return milliseconds == Timeout.Infinite
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan NormalizeUInt(uint value) =>
        value == uint.MaxValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(value);

    private void OnActiveChanged(Guid runtimeId, bool active) =>
        ActiveCounts.AddOrUpdate(runtimeId, active ? 1 : 0, (_, current) => active ? current + 1 : current - 1);
}

internal sealed class ShimTimerRegistration
{
    private readonly SimulationExecutionSnapshot _snapshot;
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private readonly ExecutionContext? _context;
    private readonly Action<Guid, bool>? _onActiveChanged;
    private ISimulationTimer? _deadline;
    private TaskCompletionSource? _disposeCompletion;
    private WaitHandle? _disposeSignal;
    private TimeSpan _period;
    private long _generation;
    private int _callbacksRunning;
    private bool _active;
    private bool _disposed;
    private bool _disposedWithSignal;

    public ShimTimerRegistration(
        SimulationExecutionSnapshot snapshot,
        TimerCallback callback,
        object? state,
        ExecutionContext? context,
        Action<Guid, bool>? onActiveChanged = null)
    {
        _snapshot = snapshot;
        _callback = callback;
        _state = state;
        _context = context;
        _onActiveChanged = onActiveChanged;
        _ = SimulationTaskRuntime.RequireScheduler("System.Threading.Timer..ctor");
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        EnsureRuntime();
        if (_disposed)
        {
            return false;
        }

        _generation++;
        _deadline?.Cancel();
        _deadline = null;
        _period = NormalizePeriod(period);

        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            SetActive(false);
            return true;
        }

        SetActive(true);
        Arm(dueTime, _generation);
        return true;
    }

    public void Dispose()
    {
        EnsureRuntime();
        if (!TryDispose())
        {
            return;
        }

        CompleteDisposalIfQuiescent();
    }

    internal void DisableFromCancellation()
    {
        if (_disposed)
        {
            return;
        }

        _generation++;
        _deadline?.Cancel();
        _deadline = null;
        SetActive(false);
    }

    public bool Dispose(WaitHandle signal)
    {
        EnsureRuntime();
        _ = ControlledWaitHandle.StateForOperation(signal, "System.Threading.Timer.Dispose");
        if (!TryDispose())
        {
            return false;
        }

        _disposedWithSignal = true;
        _disposeSignal = signal;
        CompleteDisposalIfQuiescent();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        EnsureRuntime();
        if (_disposedWithSignal)
        {
            return ValueTask.FromException(new InvalidOperationException(
                "DisposeAsync cannot follow Dispose(WaitHandle) for the same timer."));
        }

        if (!_disposed)
        {
            _ = TryDispose();
        }

        if (_callbacksRunning == 0)
        {
            return ValueTask.CompletedTask;
        }

        // No continuation is registered with the BCL on this task: controlled awaits, combinators, and continuations
        // observe it through the coordinator. RunContinuationsAsynchronously would only send continuations which
        // uncontrolled code registers to the real thread pool, so the task completes them on the logical thread.
        _disposeCompletion ??= new TaskCompletionSource();
        return new ValueTask(_disposeCompletion.Task);
    }

    private void Arm(TimeSpan delay, long generation)
    {
        if (delay == TimeSpan.Zero)
        {
            DeadlineElapsed(generation);
            return;
        }

        _deadline = SimulationTaskRuntime.RegisterTimeout(
            _snapshot,
            delay,
            () => DeadlineElapsed(generation),
            "System.Threading.Timer");
    }

    private void DeadlineElapsed(long generation)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        _deadline = null;
        if (_period > TimeSpan.Zero)
        {
            Arm(_period, generation);
        }
        else
        {
            SetActive(false);
        }

        SimulationTaskRuntime.QueueCapturedWork(
            _snapshot,
            _context,
            () => InvokeCallback(generation),
            "System.Threading.Timer");
    }

    private void InvokeCallback(long generation)
    {
        if (_disposed || generation != _generation)
        {
            CompleteDisposalIfQuiescent();
            return;
        }

        _callbacksRunning++;
        try
        {
            _callback(_state);
        }
        finally
        {
            _callbacksRunning--;
            CompleteDisposalIfQuiescent();
        }
    }

    private bool TryDispose()
    {
        if (_disposed)
        {
            return false;
        }

        _disposed = true;
        _generation++;
        _deadline?.Cancel();
        _deadline = null;
        SetActive(false);
        return true;
    }

    private void CompleteDisposalIfQuiescent()
    {
        if (!_disposed || _callbacksRunning != 0)
        {
            return;
        }

        if (_disposeSignal is not null)
        {
            ControlledWaitHandle.TrySignal(_disposeSignal);
            _disposeSignal = null;
        }

        if (_disposeCompletion is { } completion && SimulationCompletion.TrySetResult(completion, "System.Threading.Timer.DisposeAsync"))
        {
            // Callbacks which ran before disposal completed happen before an await of DisposeAsync resumes.
            RaceSynchronization.Signal(completion.Task);
        }
    }

    private void EnsureRuntime()
    {
        SimulationExecutionSnapshot current =
            SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Timer");
        if (current.Runtime.Id != _snapshot.Runtime.Id)
        {
            throw new InvalidOperationException(
                "A controlled timer can only be used by the simulation runtime which created it.");
        }
    }

    private static TimeSpan NormalizePeriod(TimeSpan period) =>
        period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? TimeSpan.Zero : period;

    private void SetActive(bool value)
    {
        if (_active == value)
        {
            return;
        }

        _active = value;
        _onActiveChanged?.Invoke(_snapshot.Runtime.Id, value);
    }
}
