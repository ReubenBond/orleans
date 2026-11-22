using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests;

internal static class RuntimeTestHarness
{
    private static readonly DateTimeOffset Origin = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static SimulationRuntimeIdentity NewRuntime(int seed = 1, string? description = null)
    {
        var runtime = new SimulationRuntimeIdentity(Guid.NewGuid(), seed, description);
        _ = new SimulationScheduler(
            runtime,
            new SimulationSeedAuthority(seed),
            Origin,
            TimeZoneInfo.Utc);
        return runtime;
    }
}
