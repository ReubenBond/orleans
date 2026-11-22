using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// A time provider for simulation testing that integrates with <see cref="SimulationSchedulerLane"/>
/// for deterministic timer execution.
/// </para>
/// <para>
/// Timer callbacks are scheduled through the lane instead of being executed immediately.
/// This enables fully deterministic simulation testing where task execution order is controlled.
/// </para>
/// <para>
/// Time is tracked centrally by the scheduler which owns the lane. Time queries continue to use
/// that scheduler after the lane is detached, while timer creation retains the lane's detachment
/// checks.
/// </para>
/// </summary>
public sealed class SimulationTimeProvider : TimeProvider
{
    private readonly SimulationSchedulerLane _schedulerLane;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTimeProvider"/> class with a scheduler lane.
    /// </summary>
    /// <param name="schedulerLane">The scheduler lane for timer callbacks.</param>
    public SimulationTimeProvider(SimulationSchedulerLane schedulerLane)
    {
        ArgumentNullException.ThrowIfNull(schedulerLane);
        _schedulerLane = schedulerLane;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _schedulerLane.Scheduler.UtcNow;

    /// <inheritdoc />
    public override long GetTimestamp() => _schedulerLane.Scheduler.UtcNow.Ticks;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override string ToString() => GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new SimulationTimer(_schedulerLane, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }
}
