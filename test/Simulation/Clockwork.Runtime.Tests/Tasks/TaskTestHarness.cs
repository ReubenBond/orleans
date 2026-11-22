using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

/// <summary>
/// Test helpers for driving the controlled task machinery under a simulation scheduler and node,
/// so <see cref="SimulationTaskRuntime"/> takes its controlled path.
/// </summary>
internal static class TaskTestHarness
{
    public const string DefaultNodeAddress = "10.0.0.1";

    public static SimulationRuntimeIdentity NewRuntime(int seed = 12345, string? description = null) =>
        new(Guid.NewGuid(), seed, description);

    /// <summary>
    /// Runs <paramref name="body"/> inside an active simulation with <paramref name="host"/>
    /// installed and the default node entered.
    /// </summary>
    public static T RunInSimulation<T>(
        SimulationSchedulerTestHost host,
        Func<T> body,
        string? nodeAddress = DefaultNodeAddress,
        SimulationRuntimeIdentity? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var activeRuntime = runtime ?? host.Scheduler.Runtime;
        if (!ReferenceEquals(activeRuntime, host.Scheduler.Runtime))
        {
            throw new ArgumentException("The runtime must be owned by the supplied scheduler.", nameof(runtime));
        }

        // The test thread is the simulation's controlling thread: it calls controlled APIs and drives the scheduler.
        using (host.Scheduler.EnterControlScope())
        using (SimulationExecutionContext.EnterRuntime(activeRuntime))
        {
            if (nodeAddress is null)
            {
                return body();
            }

            using (SimulationExecutionContext.EnterNode(new SimulationNodeIdentity(nodeAddress)))
            {
                return body();
            }
        }
    }

    public static void RunInSimulation(
        SimulationSchedulerTestHost host,
        Action body,
        string? nodeAddress = DefaultNodeAddress,
        SimulationRuntimeIdentity? runtime = null) =>
        RunInSimulation<object?>(
            host,
            () =>
            {
                body();
                return null;
            },
            nodeAddress,
            runtime);

}
