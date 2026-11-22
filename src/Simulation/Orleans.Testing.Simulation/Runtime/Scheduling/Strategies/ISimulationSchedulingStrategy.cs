namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// <para>
/// A pluggable policy that chooses which of the currently runnable operations the scheduler grants
/// the permission baton to next. A strategy is a pure selection function over the
/// <see cref="SimulationSchedulingContext"/>: it must not mutate operations, start threads, or take
/// the scheduler lock - the scheduler owns all of that and calls <see cref="ChooseNext"/> while holding
/// its own gate.
/// </para>
/// <para>
/// A strategy must be <em>deterministic given its own state and the context</em>: for a fixed
/// construction (including any seed) and a fixed sequence of contexts, it must always make the same
/// sequence of choices, so a simulation is reproducible. Any strategy that consumes a seeded random
/// stream advances that stream only inside <see cref="ChooseNext"/>, and only when there is a real
/// choice to make (see <see cref="RecordsNondeterministicChoices"/>).
/// </para>
/// </summary>
public interface ISimulationSchedulingStrategy
{
    /// <summary>
    /// Gets a short, stable name for this policy (e.g. <c>"round-robin"</c>). It is recorded as the
    /// decision source when the scheduler logs a scheduling choice, so it must be stable across runs.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether this strategy's choices carry information that must be
    /// recorded to reproduce the schedule (i.e. the strategy is <em>not</em> a pure function of the
    /// operation set and last-selected id alone - it consults a random stream, external input, or
    /// other hidden state). The scheduler always records a choice when a decision log is attached and
    /// there is more than one candidate; this flag lets a strategy declare that its choices are
    /// inherently nondeterministic even to a reader that knows the full context.
    /// </summary>
    bool RecordsNondeterministicChoices { get; }

    /// <summary>
    /// Chooses the next operation to run from the non-empty runnable set in
    /// <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The current runnable set and last-selected id.</param>
    /// <returns>One operation drawn from <see cref="SimulationSchedulingContext.Runnable"/>.</returns>
    SimulationOperation ChooseNext(SimulationSchedulingContext context);
}

internal interface ISimulationSchedulingStrategyRuntimeHooks
{
    int ChooseResourceWaiter(IReadOnlyList<Resources.SimulationResourceWaiterInfo> waiters);

    string DecisionSourceId { get; }

    void ValidateComplete();
}
