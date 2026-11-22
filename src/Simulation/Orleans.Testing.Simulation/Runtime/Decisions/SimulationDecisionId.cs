namespace Orleans.Testing.Simulation.Runtime.Decisions;

/// <summary>
/// A monotonically increasing identifier assigned by <see cref="ISimulationDecisionLog"/> to each
/// recorded <see cref="SimulationDecisionRecord"/>, in the exact order <c>Record</c> was called -
/// regardless of which domain the decision belongs to. This total order across domains is what
/// lets a replay reader detect "the Nth decision overall diverged", not just "the Nth decision
/// within some domain diverged".
/// </summary>
/// <param name="Sequence">The zero-based position of this decision in the log.</param>
public readonly record struct SimulationDecisionId(long Sequence)
{
    /// <inheritdoc/>
    public override string ToString() => Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
