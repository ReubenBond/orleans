using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>A deterministic trace entry emitted by one injected scheduling point.</summary>
/// <param name="Sequence">The scheduler-local, monotonically increasing trace sequence.</param>
/// <param name="OperationId">The operation that reached the point.</param>
/// <param name="Kind">The access or control-flow kind.</param>
/// <param name="Location">The stable member/array/control-flow description.</param>
/// <param name="Source">The original IL and source location.</param>
public readonly record struct RaceSchedulingPoint(
    long Sequence,
    SimulationOperationId OperationId,
    RaceAccessKind Kind,
    string Location,
    RaceSourceLocation Source);
