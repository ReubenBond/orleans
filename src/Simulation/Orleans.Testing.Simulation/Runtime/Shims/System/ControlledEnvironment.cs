using System.ComponentModel;

namespace Orleans.Testing.Simulation.Shims.System;

/// <summary>Controlled rewrite targets for <see cref="Environment"/> APIs.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ControlledEnvironment
{
    /// <summary>Controlled replacement for <see cref="Environment.CurrentManagedThreadId"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int GetCurrentManagedThreadId()
    {
        _ = SimulationRuntimeDispatch.RequireEnvironment("System.Environment.CurrentManagedThreadId");
        return Orleans.Testing.Simulation.Runtime.Scheduling.SimulationScheduler.SimulationLogicalThreadOwnerId;
    }

    /// <summary>
    /// Controlled replacement for <see cref="Environment.ProcessorCount"/>: the simulation's processor count (see
    /// <see cref="ISimulationRuntimeEnvironment.GetProcessorCount"/>), which is the same on every host, so code that
    /// sizes its work by the processor count replays on any machine.
    /// </summary>
    /// <returns>The simulated processor count.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int GetProcessorCount()
    {
        var (_, environment, node) =
            SimulationRuntimeDispatch.RequireEnvironment("System.Environment.ProcessorCount");
        return environment.GetProcessorCount(node);
    }

    /// <summary>Controlled replacement for <see cref="Environment.TickCount"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int GetTickCount()
    {
        var (_, environment, node) =
            SimulationRuntimeDispatch.RequireEnvironment("System.Environment.TickCount");
        return unchecked((int)environment.GetTickCount64(node));
    }

    /// <summary>Controlled replacement for <see cref="Environment.TickCount64"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static long GetTickCount64()
    {
        var (_, environment, node) =
            SimulationRuntimeDispatch.RequireEnvironment("System.Environment.TickCount64");
        return environment.GetTickCount64(node);
    }
}
