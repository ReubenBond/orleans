namespace Orleans.Testing.Simulation.Runtime.Execution;

/// <summary>
/// Identifies one simulated node's identity/context for the ambient
/// <see cref="SimulationExecutionContext"/>, keyed by its stable network address rather than by
/// registration order - two <see cref="SimulationNodeIdentity"/> instances are the same node if and
/// only if their <see cref="Address"/>s are equal (ordinal comparison).
/// </summary>
/// <param name="Address">The node's stable network address.</param>
public sealed record SimulationNodeIdentity(string Address)
{
    /// <summary>
    /// Gets the node's stable network address.
    /// </summary>
    public string Address { get; } = Address ?? throw new ArgumentNullException(nameof(Address));
}
