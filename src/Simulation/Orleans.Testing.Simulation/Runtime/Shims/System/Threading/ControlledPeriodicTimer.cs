using System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>Deterministic virtual-time replacement for <see cref="PeriodicTimer"/>.</summary>
public sealed class ControlledPeriodicTimer : IDisposable
{
    private const string Api = "System.Threading.PeriodicTimer";
    private const uint MaxSupportedTimeout = 0xfffffffe;
    private readonly ShimTimerRegistration _registration;
    private readonly TickState _state = new();
    private TimeSpan _period;

    /// <summary>Creates and starts a periodic timer.</summary>
    public ControlledPeriodicTimer(TimeSpan period)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        _period = ValidatePeriod(period, nameof(period));
        _registration = new ShimTimerRegistration(snapshot, _ => _state.Signal(), null, null);
        _registration.Change(_period, _period);
    }

    /// <summary>Creates and starts a periodic timer using a supported controlled provider.</summary>
    public ControlledPeriodicTimer(TimeSpan period, TimeProvider timeProvider)
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        _period = ValidatePeriod(period, nameof(period));
        ArgumentNullException.ThrowIfNull(timeProvider);
        ControlledTimeProvider.ValidateProvider(timeProvider, Api);
        _registration = new ShimTimerRegistration(snapshot, _ => _state.Signal(), null, null);
        _registration.Change(_period, _period);
    }

    /// <summary>Gets or sets the period between virtual ticks.</summary>
    public TimeSpan Period
    {
        get
        {
            RequireRuntime("get_Period");
            return _period;
        }

        set
        {
            RequireRuntime("set_Period");
            TimeSpan validated = ValidatePeriod(value, nameof(value));
            ObjectDisposedException.ThrowIf(!_registration.Change(validated, validated), this);

            _period = validated;
        }
    }

    /// <summary>Waits for the next coalesced virtual tick.</summary>
    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
    {
        RequireRuntime("WaitForNextTickAsync");
        return _state.WaitForNextTickAsync(this, cancellationToken);
    }

    /// <summary>Stops the timer and completes an active waiter with <see langword="false"/>.</summary>
    public void Dispose()
    {
        RequireRuntime("Dispose");
        _registration.Dispose();
        _state.Signal(stopping: true);
        GC.SuppressFinalize(this);
    }

    private static SimulationExecutionSnapshot RequireRuntime(string member) =>
        SimulationRuntimeDispatch.RequireActiveSimulation($"{Api}.{member}");

    private static TimeSpan ValidatePeriod(TimeSpan value, string parameterName)
    {
        long milliseconds = (long)value.TotalMilliseconds;
        if ((milliseconds < 1 || milliseconds > MaxSupportedTimeout) && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value == Timeout.InfiniteTimeSpan
            ? value
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class TickState : IValueTaskSource<bool>
    {
        private readonly object _lock = new();
        private ManualResetValueTaskSourceCore<bool> _source;
        private CancellationTokenRegistration _cancellationRegistration;
        private ControlledPeriodicTimer? _owner;
        private bool _stopped;
        private bool _signaled;
        private bool _activeWait;

        public ValueTask<bool> WaitForNextTickAsync(
            ControlledPeriodicTimer owner,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_activeWait)
                {
                    throw new InvalidOperationException(
                        "Only one WaitForNextTickAsync call may be active for a PeriodicTimer.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<bool>(cancellationToken);
                }

                if (_signaled)
                {
                    if (!_stopped)
                    {
                        _signaled = false;
                    }

                    return new ValueTask<bool>(!_stopped);
                }

                _owner = owner;
                _activeWait = true;
                _cancellationRegistration = cancellationToken.UnsafeRegister(
                    static (state, token) => ((TickState)state!).Signal(cancellationToken: token),
                    this);
                return new ValueTask<bool>(this, _source.Version);
            }
        }

        public void Signal(bool stopping = false, CancellationToken cancellationToken = default)
        {
            bool complete;
            lock (_lock)
            {
                _stopped |= stopping;
                complete = !_signaled && _activeWait;
                _signaled = true;
            }

            if (!complete)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                SimulationCompletion.Run(
                    ref _source,
                    new OperationCanceledException(cancellationToken),
                    static (ref ManualResetValueTaskSourceCore<bool> source, OperationCanceledException exception) => source.SetException(exception),
                    Api);
            }
            else
            {
                SimulationCompletion.Run(
                    ref _source,
                    true,
                    static (ref ManualResetValueTaskSourceCore<bool> source, bool result) => source.SetResult(result),
                    Api);
            }
        }

        bool IValueTaskSource<bool>.GetResult(short token)
        {
            _cancellationRegistration.Dispose();
            lock (_lock)
            {
                try
                {
                    _source.GetResult(token);
                }
                finally
                {
                    _source.Reset();
                    _cancellationRegistration = default;
                    _activeWait = false;
                    _owner = null;
                    if (!_stopped)
                    {
                        _signaled = false;
                    }
                }

                return !_stopped;
            }
        }

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _source.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);
    }
}
