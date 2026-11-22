namespace Orleans.Testing.Simulation.Runtime.Decisions;

/// <summary>
/// Broad categories of deterministic decision recorded via <see cref="ISimulationDecisionLog"/>.
/// This is deliberately a small, open-ended set: <see cref="Custom"/> covers anything not yet
/// worth a dedicated value, so adding a new decision-producing call site never requires adding a
/// new enum member first.
/// </summary>
public enum SimulationDecisionKind
{
    /// <summary>A draw from a random-number stream (e.g. <c>Random.Next()</c>-shaped).</summary>
    RandomDraw,

    /// <summary>A choice among a discrete, enumerable set of options.</summary>
    Choice,

    /// <summary>A decision about execution/scheduling order.</summary>
    SchedulingOrder,

    /// <summary>A choice of which pending resource waiter receives a signal.</summary>
    ResourceWinner,

    /// <summary>A simulated-network decision (delay, loss, jitter, partition).</summary>
    NetworkBehavior,

    /// <summary>A fault-injection ("Buggify"-style) activation decision.</summary>
    FaultActivation,

    /// <summary>Any decision not covered by the other kinds.</summary>
    Custom,
}
