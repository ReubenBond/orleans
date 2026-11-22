using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System;

/// <summary>Controlled replacement entry points for <see cref="TimeProvider"/> timer creation.</summary>
public sealed class ControlledTimeProvider : TimeProvider
{
    private static readonly ControlledTimeProvider Instance = new();

    private ControlledTimeProvider()
    {
    }

    /// <summary>Gets the simulation's controlled provider identity.</summary>
    public new static TimeProvider System
    {
        get
        {
            SimulationRuntimeDispatch.RequireActiveSimulation("System.TimeProvider.System");
            _ = SimulationTaskRuntime.RequireScheduler("System.TimeProvider.System");
            return Instance;
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        var (_, environment, node) = SimulationRuntimeDispatch.RequireEnvironment("System.TimeProvider.GetUtcNow");
        return environment.GetUtcNow(node);
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        var (_, environment, node) = SimulationRuntimeDispatch.RequireEnvironment("System.TimeProvider.GetTimestamp");
        return environment.GetTimestamp(node);
    }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone
    {
        get
        {
            var (_, environment, node) = SimulationRuntimeDispatch.RequireEnvironment("System.TimeProvider.LocalTimeZone");
            return environment.GetLocalTimeZone(node);
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.TimeProvider.CreateTimer");
        return new ControlledTimer(callback, state, dueTime, period);
    }

    /// <summary>Redirect target for instance <see cref="TimeProvider.CreateTimer"/> calls.</summary>
    public static ITimer CreateTimer(
        TimeProvider provider,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.TimeProvider.CreateTimer");
        ArgumentNullException.ThrowIfNull(provider);
        ValidateProvider(provider, "System.TimeProvider.CreateTimer");
        return new ControlledTimer(callback, state, dueTime, period);
    }

    internal static void ValidateProvider(TimeProvider provider, string apiName)
    {
        if (ReferenceEquals(provider, Instance) || ReferenceEquals(provider, TimeProvider.System))
        {
            return;
        }

        throw new SimulationApiException(
            SimulationApiCategory.Timer,
            apiName,
            $"the TimeProvider implementation '{provider.GetType().FullName}' is not registered with Orleans.Testing.Simulation.");
    }
}
