using Orleans.Consensus.CasPaxos.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// Runs scenarios on the shared upstream fixture through the seeded-simulation helper, which reports the seed, the
/// replay command, and the protocol trace of a failing run, not another scheduler.
/// </summary>
internal static class CasPaxosTestFixture
{
    internal static Guid Cluster { get; } = new("01234567-89ab-cdef-0123-456789abcdef");

    internal static Task RunAsync(int seed, Func<RegisterSimulationCluster, Task> scenario) =>
        SeededSimulation.RunAsync(seed, run => SimulateAsync(run, scenario));

    /// <summary>
    /// Runs the scenario twice from one seed and requires both runs to record the same protocol trace.
    /// </summary>
    internal static Task RunAndReplayAsync(int seed, Func<RegisterSimulationCluster, Task> scenario) =>
        SeededSimulation.RunAndReplayAsync(seed, run => SimulateAsync(run, scenario));

    private static async Task SimulateAsync(SimulationRun run, Func<RegisterSimulationCluster, Task> scenario)
    {
        await using var simulation = new RegisterSimulationCluster(run.Seed);
        run.UseHistory(() => simulation.Trace);
        simulation.Run(scenario, maxIterations: 100_000);
    }

    internal static async Task WaitForTraceAsync(
        RegisterSimulationCluster simulation,
        int start,
        string fragment)
    {
        // A protocol event is the phase barrier. Advancing the existing virtual scheduler is not
        // a wall-clock sleep, and elapsed time alone never establishes that the write committed.
        for (var step = 0; step < 1_000; step++)
        {
            if (simulation.Trace.Skip(start).Any(entry => entry.Contains(fragment, StringComparison.Ordinal)))
            {
                return;
            }

            await simulation.DelayAsync(TimeSpan.FromMilliseconds(1));
        }

        Assert.Fail($"The protocol did not reach '{fragment}' within 1,000 virtual steps.");
    }
}
