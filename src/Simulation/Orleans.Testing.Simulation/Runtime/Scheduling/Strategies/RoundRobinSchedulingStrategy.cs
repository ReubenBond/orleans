namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// The default policy, identical to the controlled-operation scheduler: run the runnable operation whose id is
/// the smallest strictly greater than the last-selected id, wrapping to the smallest runnable id when
/// none is greater. This rotates fairly through concurrently-runnable operations without starving any
/// of them, and is fully deterministic given the runnable set and last-selected id, so it needs no
/// recording to reproduce.
/// </summary>
internal sealed class RoundRobinSchedulingStrategy : ISimulationSchedulingStrategy
{
    /// <inheritdoc/>
    public string Name => "round-robin";

    /// <inheritdoc/>
    public bool RecordsNondeterministicChoices => false;

    /// <inheritdoc/>
    public SimulationOperation ChooseNext(SimulationSchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.Runnable.Count; index++)
        {
            SimulationOperation operation = context.Runnable[index];
            if (operation.Id > context.LastSelected)
            {
                return operation;
            }
        }

        // Runnable is ascending by id and non-empty, so index 0 is the wrap target.
        return context.Runnable[0];
    }
}
