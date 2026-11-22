using System.ComponentModel;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Timers;

/// <summary>Deterministic virtual-time replacement for <see cref="global::System.Timers.Timer"/>.</summary>
[DefaultProperty(nameof(Interval))]
[DefaultEvent(nameof(Elapsed))]
public class ControlledTimer : Component, ISupportInitialize
{
    private const string Api = "System.Timers.Timer";
    private readonly ShimTimerRegistration _registration;
    private double _interval = 100;
    private bool _autoReset = true;
    private bool _enabled;
    private bool _initializing;
    private bool _delayedEnable;
    private bool _disposed;
    private global::System.Timers.ElapsedEventHandler? _elapsed;
    private ISynchronizeInvoke? _synchronizingObject;

    /// <summary>Creates a disabled timer with a 100 millisecond interval.</summary>
    public ControlledTimer()
    {
        SimulationExecutionSnapshot snapshot = RequireRuntime(".ctor");
        _registration = new ShimTimerRegistration(
            snapshot,
            _ => OnElapsed(),
            null,
            ExecutionContext.Capture());
    }

    /// <summary>Creates a disabled timer with the supplied interval.</summary>
    public ControlledTimer(double interval)
        : this()
    {
        double rounded = ValidateConstructorInterval(interval);
        _interval = (int)rounded;
    }

    /// <summary>Creates a disabled timer with the supplied interval.</summary>
    public ControlledTimer(TimeSpan interval)
        : this(interval.TotalMilliseconds)
    {
    }

    /// <summary>Gets or sets whether the timer automatically rearms after an elapsed event.</summary>
    public bool AutoReset
    {
        get
        {
            RequireRuntime("get_AutoReset");
            return _autoReset;
        }

        set
        {
            RequireRuntime("set_AutoReset");
            if (_autoReset == value)
            {
                return;
            }

            _autoReset = value;
            if (_enabled)
            {
                Arm();
            }
        }
    }

    /// <summary>Gets or sets whether the timer is enabled.</summary>
    public bool Enabled
    {
        get
        {
            RequireRuntime("get_Enabled");
            return _enabled;
        }

        set
        {
            RequireRuntime("set_Enabled");
            if (_initializing)
            {
                _delayedEnable = value;
                return;
            }

            if (_enabled == value)
            {
                return;
            }

            if (value)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _enabled = true;
                Arm();
            }
            else
            {
                _enabled = false;
                _registration.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Gets or sets the interval, in milliseconds.</summary>
    public double Interval
    {
        get
        {
            RequireRuntime("get_Interval");
            return _interval;
        }

        set
        {
            RequireRuntime("set_Interval");
            if (value <= 0)
            {
                throw new ArgumentException("The timer interval must be greater than zero.", nameof(value));
            }

            _interval = value;
            if (_enabled)
            {
                Arm();
            }
        }
    }

    /// <summary>Occurs when the virtual interval elapses.</summary>
    public event global::System.Timers.ElapsedEventHandler Elapsed
    {
        add
        {
            RequireRuntime("add_Elapsed");
            _elapsed += value;
        }

        remove
        {
            RequireRuntime("remove_Elapsed");
            _elapsed -= value;
        }
    }

    /// <summary>
    /// Gets or sets designer site metadata. Non-null sites are rejected because designer services are
    /// outside the simulation runtime.
    /// </summary>
    public override ISite? Site
    {
        get
        {
            RequireRuntime("get_Site");
            return base.Site;
        }

        set
        {
            RequireRuntime("set_Site");
            if (value is not null)
            {
                throw new SimulationApiException(
                    SimulationApiCategory.Timer,
                    $"{Api}.Site",
                    "component designer sites can invoke host services outside the simulation.");
            }

            base.Site = null;
        }
    }

    /// <summary>
    /// Gets or sets callback marshaling. Non-null marshaling objects are rejected because their
    /// <see cref="ISynchronizeInvoke.BeginInvoke"/> implementation is not controlled.
    /// </summary>
    public ISynchronizeInvoke? SynchronizingObject
    {
        get
        {
            RequireRuntime("get_SynchronizingObject");
            return _synchronizingObject;
        }

        set
        {
            RequireRuntime("set_SynchronizingObject");
            if (value is not null)
            {
                throw new SimulationApiException(
                    SimulationApiCategory.Timer,
                    $"{Api}.SynchronizingObject",
                    "ISynchronizeInvoke callback marshaling can escape to an uncontrolled UI or native thread.");
            }

            _synchronizingObject = null;
        }
    }

    /// <summary>Begins component initialization and temporarily disables the timer.</summary>
    public void BeginInit()
    {
        RequireRuntime("BeginInit");
        Close();
        _initializing = true;
    }

    /// <summary>Completes component initialization and applies the delayed enabled value.</summary>
    public void EndInit()
    {
        RequireRuntime("EndInit");
        _initializing = false;
        Enabled = _delayedEnable;
    }

    /// <summary>Disables the timer without permanently disposing it.</summary>
    public void Close()
    {
        RequireRuntime("Close");
        _initializing = false;
        _delayedEnable = false;
        _enabled = false;
        _registration.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Enables the timer.</summary>
    public void Start()
    {
        RequireRuntime("Start");
        Enabled = true;
    }

    /// <summary>Disables the timer.</summary>
    public void Stop()
    {
        RequireRuntime("Stop");
        Enabled = false;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RequireRuntime("Dispose");
            if (!_disposed)
            {
                _disposed = true;
                _enabled = false;
                _registration.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static SimulationExecutionSnapshot RequireRuntime(string member) =>
        SimulationRuntimeDispatch.RequireActiveSimulation($"{Api}.{member}");

    private static double ValidateConstructorInterval(double interval)
    {
        if (interval <= 0)
        {
            throw new ArgumentException("The timer interval must be greater than zero.", nameof(interval));
        }

        double rounded = Math.Ceiling(interval);
        if (rounded > int.MaxValue || rounded <= 0)
        {
            throw new ArgumentException("The timer interval must not exceed Int32.MaxValue milliseconds.", nameof(interval));
        }

        return rounded;
    }

    private void Arm()
    {
        int milliseconds = checked((int)Math.Ceiling(_interval));
        TimeSpan interval = TimeSpan.FromMilliseconds(milliseconds);
        _registration.Change(interval, _autoReset ? interval : Timeout.InfiniteTimeSpan);
    }

    private void OnElapsed()
    {
        if (!_autoReset)
        {
            _enabled = false;
        }

        global::System.Timers.ElapsedEventHandler? handler = _elapsed;
        if (handler is null)
        {
            return;
        }

        var (_, environment, node) = SimulationRuntimeDispatch.RequireEnvironment($"{Api}.Elapsed");
        DateTime signalTime = TimeZoneInfo.ConvertTime(
            environment.GetUtcNow(node),
            environment.GetLocalTimeZone(node)).DateTime;
        try
        {
            handler(this, new global::System.Timers.ElapsedEventArgs(signalTime));
        }
        catch
        {
            // System.Timers.Timer intentionally suppresses exceptions raised by elapsed handlers.
        }
    }
}
