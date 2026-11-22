using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Replay;

/// <summary>
/// Explicit scenario harness contract used by replay tooling. Implementations register controlled
/// operations and resources with the supplied scheduler; the runner owns scheduler driving.
/// </summary>
public interface IReplayScenario
{
    /// <summary>Registers a fresh scenario instance for one record, replay, or exploration iteration.</summary>
    void Configure(SimulationScheduler scheduler);
}
