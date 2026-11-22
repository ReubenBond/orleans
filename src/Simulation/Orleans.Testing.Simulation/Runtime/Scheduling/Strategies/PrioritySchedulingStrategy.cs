namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// Runs the runnable operation with the highest <see cref="SimulationOperation.Priority"/>, breaking
/// ties with round-robin so equal-priority operations still rotate fairly and none starves within its
/// priority band. Priority is a crisp caller-supplied integer, so this policy is fully deterministic
/// and needs no recording. It deliberately does not model BCL thread-priority semantics; it is a
/// determinism tool for steering which ready operation runs first.
/// </summary>
internal sealed class PrioritySchedulingStrategy : ISimulationSchedulingStrategy
{
    /// <inheritdoc/>
    public string Name => "priority";

    /// <inheritdoc/>
    public bool RecordsNondeterministicChoices => false;

    /// <inheritdoc/>
    public SimulationOperation ChooseNext(SimulationSchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        SimulationOperation wrapTarget = context.Runnable[0];
        var highest = wrapTarget.Priority;
        SimulationOperation? firstAfterLast =
            wrapTarget.Id > context.LastSelected ? wrapTarget : null;
        for (var index = 1; index < context.Runnable.Count; index++)
        {
            SimulationOperation operation = context.Runnable[index];
            if (operation.Priority > highest)
            {
                highest = operation.Priority;
                wrapTarget = operation;
                firstAfterLast = operation.Id > context.LastSelected ? operation : null;
            }
            else if (operation.Priority == highest
                && firstAfterLast is null
                && operation.Id > context.LastSelected)
            {
                firstAfterLast = operation;
            }
        }

        return firstAfterLast ?? wrapTarget;
    }
}
