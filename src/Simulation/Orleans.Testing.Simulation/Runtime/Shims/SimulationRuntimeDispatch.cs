using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Shims;

/// <summary>Centralizes active-simulation assertion and runtime-environment access for rewrite targets.</summary>
public static class SimulationRuntimeDispatch
{
    /// <summary>
    /// Requires an active simulation, on its logical thread, and returns the ambient execution snapshot.
    /// </summary>
    /// <param name="apiName">The fully-qualified name of the controlled API.</param>
    /// <returns>The active simulation execution snapshot.</returns>
    /// <exception cref="SimulationNotActiveException">Thrown when no simulation is active.</exception>
    /// <exception cref="SimulationThreadAffinityException">
    /// Thrown when the calling thread carries the simulation's ambient context but is not its logical thread.
    /// </exception>
    public static SimulationExecutionSnapshot RequireActiveSimulation(string apiName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        SimulationExecutionSnapshot snapshot =
            SimulationExecutionContext.Current ?? throw new SimulationNotActiveException(apiName);
        if (!snapshot.Runtime.Scheduler.IsSimulationThread)
        {
            ThrowThreadAffinityViolation(snapshot.Runtime, apiName);
        }

        return snapshot;
    }

    /// <summary>
    /// Resolves the environment carried by the active simulation.
    /// </summary>
    /// <param name="apiName">The fully-qualified name of the controlled API, for diagnostics.</param>
    /// <returns>The active runtime, its environment, and the active node.</returns>
    public static (SimulationRuntimeIdentity Runtime, ISimulationRuntimeEnvironment Environment, SimulationNodeIdentity? Node) RequireEnvironment(string apiName)
    {
        var snapshot = RequireActiveSimulation(apiName);
        return (snapshot.Runtime, snapshot.Runtime.Environment, snapshot.Node);
    }

    /// <summary>
    /// Requires the simulation's logical thread when a simulation's ambient context is present, for a controlled API
    /// which also runs outside any simulation.
    /// </summary>
    /// <param name="api">The controlled API's declaring type.</param>
    /// <param name="member">The controlled API's member name.</param>
    internal static void RequireSimulationThreadIfActive(string api, string member)
    {
        SimulationExecutionContext.TryGetCurrentRuntime(out SimulationRuntimeIdentity? runtime);
        if (runtime is { Scheduler.IsSimulationThread: false })
        {
            ThrowThreadAffinityViolation(runtime, $"{api}.{member}");
        }
    }

    /// <summary>
    /// Rejects a controlled API called with <paramref name="runtime"/>'s ambient context off its logical thread. The
    /// scheduler records the violation so the thread driving it surfaces the failure even when the escaped work swallows
    /// the exception thrown here.
    /// </summary>
    /// <param name="runtime">The runtime whose ambient context the calling thread carries.</param>
    /// <param name="apiName">The fully-qualified name of the controlled API.</param>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowThreadAffinityViolation(SimulationRuntimeIdentity runtime, string apiName)
    {
        runtime.Scheduler.ReportThreadAffinityViolation(apiName);
        throw new SimulationThreadAffinityException(apiName, runtime);
    }
}
