using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Tests;

// Owns a scheduler for tests that need an active simulation runtime.
internal sealed class SimulationSchedulerTestHost : IDisposable
{
    public SimulationSchedulerTestHost(int seed = 12345, string? description = null)
    {
        var runtime = new SimulationRuntimeIdentity(Guid.NewGuid(), seed, description);
        Scheduler = new SimulationScheduler(
            runtime,
            new SimulationSeedAuthority(seed),
            DateTimeOffset.UnixEpoch,
            TimeZoneInfo.Utc);
    }

    public SimulationScheduler Scheduler { get; }

    public void Dispose() => Scheduler.Dispose();
}
