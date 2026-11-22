using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// Creates the built-in simulation scheduling strategies.
/// </summary>
public static class SimulationSchedulingStrategies
{
    /// <summary>
    /// Creates a strategy which always selects the runnable operation with the smallest id.
    /// </summary>
    public static ISimulationSchedulingStrategy Fifo() => new FifoSchedulingStrategy();

    /// <summary>
    /// Creates a strategy which rotates fairly through runnable operations.
    /// </summary>
    public static ISimulationSchedulingStrategy RoundRobin() => new RoundRobinSchedulingStrategy();

    /// <summary>
    /// Creates a strategy which selects the highest-priority runnable operation and uses
    /// round-robin ordering to break ties.
    /// </summary>
    public static ISimulationSchedulingStrategy Priority() => new PrioritySchedulingStrategy();

    /// <summary>
    /// Creates a deterministic random strategy from a raw scheduling seed.
    /// </summary>
    /// <param name="seed">The scheduling random-stream seed.</param>
    public static ISimulationSchedulingStrategy SeededRandom(int seed) =>
        new SeededRandomSchedulingStrategy(seed);

    /// <summary>
    /// Creates a deterministic random strategy whose scheduling seed is derived from a runtime's
    /// simulation seed in the scheduler decision domain.
    /// </summary>
    /// <param name="runtime">The runtime whose simulation seed derives the scheduling seed.</param>
    public static ISimulationSchedulingStrategy SeededRandom(SimulationRuntimeIdentity runtime) =>
        SeededRandomSchedulingStrategy.ForRuntime(runtime);

    /// <summary>
    /// Creates a strategy which exactly replays scheduling and resource-winner decisions.
    /// </summary>
    /// <param name="recordedDecisions">The recorded decisions to replay.</param>
    public static ISimulationSchedulingStrategy Replay(
        IReadOnlyList<SimulationDecisionRecord> recordedDecisions) =>
        new ReplaySchedulingStrategy(recordedDecisions);
}
