namespace Orleans.Testing.Simulation.Runtime.Execution;

/// <summary>
/// An immutable snapshot of the ambient simulation execution context observed at a point in time -
/// what <see cref="SimulationExecutionContext.Current"/> returns. Snapshots are plain data; taking
/// one has no effect on the ambient context itself.
/// </summary>
/// <param name="Runtime">The active simulation runtime.</param>
/// <param name="Node">
/// The active node identity, or <see langword="null"/> if execution is not currently scoped to a
/// specific node (e.g. cluster-level work).
/// </param>
/// <param name="LogicalExecutionId">
/// The current logical execution identity placeholder; <see cref="SimulationLogicalExecutionId.None"/>
/// if no logical execution scope has been entered.
/// </param>
public sealed record SimulationExecutionSnapshot(
    SimulationRuntimeIdentity Runtime,
    SimulationNodeIdentity? Node,
    SimulationLogicalExecutionId LogicalExecutionId);
