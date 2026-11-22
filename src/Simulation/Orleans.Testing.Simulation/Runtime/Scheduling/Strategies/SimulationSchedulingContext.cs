namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// The immutable input an <see cref="ISimulationSchedulingStrategy"/> reasons over when picking the
/// next operation to run: the current runnable set and the id chosen on the previous step. The
/// scheduler builds one of these per selection while holding its lock and never exposes mutable
/// scheduler state through it.
/// </summary>
public sealed class SimulationSchedulingContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationSchedulingContext"/> class.
    /// </summary>
    /// <param name="runnable">
    /// The runnable operations, in ascending id order. Must be non-empty; the scheduler never asks a
    /// strategy to choose from an empty set.
    /// </param>
    /// <param name="lastSelected">
    /// The id selected on the previous step, or <see cref="SimulationOperationId.None"/> if no
    /// operation has been selected yet. Round-robin-style policies use this to advance fairly.
    /// </param>
    public SimulationSchedulingContext(IReadOnlyList<SimulationOperation> runnable, SimulationOperationId lastSelected)
    {
        ArgumentNullException.ThrowIfNull(runnable);
        if (runnable.Count == 0)
        {
            throw new ArgumentException("A scheduling context must contain at least one runnable operation.", nameof(runnable));
        }

        Runnable = runnable;
        LastSelected = lastSelected;
    }

    /// <summary>
    /// Gets the runnable operations, in ascending <see cref="SimulationOperation.Id"/> order. Always
    /// contains at least one element. A strategy must return one of exactly these instances.
    /// </summary>
    public IReadOnlyList<SimulationOperation> Runnable { get; }

    /// <summary>
    /// Gets the operation id selected on the previous step, or <see cref="SimulationOperationId.None"/>
    /// if none has been selected yet.
    /// </summary>
    public SimulationOperationId LastSelected { get; }
}
