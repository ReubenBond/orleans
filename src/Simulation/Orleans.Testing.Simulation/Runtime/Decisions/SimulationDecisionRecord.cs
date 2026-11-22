using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation.Runtime.Decisions;

/// <summary>
/// The information a caller supplies to <see cref="ISimulationDecisionLog.Record"/> to record one
/// deterministic decision. <see cref="ISimulationDecisionLog"/> assigns the monotonic
/// <see cref="SimulationDecisionRecord.Id"/> - callers do not (and cannot) choose it.
/// </summary>
/// <param name="Domain">Which independent seed/decision domain this decision belongs to.</param>
/// <param name="Kind">The broad category of decision.</param>
/// <param name="SourceId">
/// An optional stable identifier for the call site/source that made this decision (e.g. a node
/// address, a stable tag for the code path) - used to correlate decisions across runs
/// independently of registration/call order.
/// </param>
/// <param name="InputMetadata">
/// An optional, human-readable description of the input/range the decision was drawn from (e.g.
/// <c>"[0, 10)"</c> for a bounded random draw, or the candidate set for a <see cref="SimulationDecisionKind.Choice"/>).
/// </param>
/// <param name="SelectedResult">
/// A stable string representation of the value that was actually selected/decided. This is what
/// replay validation compares against a prior recording.
/// </param>
/// <param name="RuntimeId">The active simulation runtime's identity at the time of the decision.</param>
/// <param name="NodeId">
/// The active node's address at the time of the decision, or <see langword="null"/> for
/// cluster-level (non-node-scoped) decisions.
/// </param>
/// <param name="LogicalExecutionId">
/// The active logical execution identity placeholder at the time of the decision; see
/// <see cref="SimulationLogicalExecutionId"/>.
/// </param>
public readonly record struct SimulationDecisionRequest(
    SimulationSeedDomain Domain,
    SimulationDecisionKind Kind,
    string? SourceId,
    string? InputMetadata,
    string SelectedResult,
    Guid RuntimeId,
    string? NodeId,
    SimulationLogicalExecutionId LogicalExecutionId)
{
    /// <summary>
    /// Builds a decision request whose <see cref="RuntimeId"/>, <see cref="NodeId"/>, and
    /// <see cref="LogicalExecutionId"/> are taken from the ambient
    /// <see cref="SimulationExecutionContext.Current"/> at the call site. This is the intended way to
    /// record a decision from inside controlled work (for example a running
    /// <see cref="Orleans.Testing.Simulation.Runtime.Scheduling.SimulationOperation"/>, which installs its logical
    /// execution identity into the ambient context): the correct runtime/node/logical identity is
    /// captured automatically instead of being threaded through by hand. It is purely additive - the
    /// primary constructor remains available for callers that already hold the identity values.
    /// </summary>
    /// <param name="domain">Which independent seed/decision domain this decision belongs to.</param>
    /// <param name="kind">The broad category of decision.</param>
    /// <param name="selectedResult">A stable string representation of the value that was decided.</param>
    /// <param name="sourceId">An optional stable identifier for the call site/source.</param>
    /// <param name="inputMetadata">An optional, human-readable description of the input/range.</param>
    /// <returns>A request carrying the ambient runtime, node, and logical execution identity.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no simulation runtime scope is currently active - a deterministic decision must be
    /// made within an ambient runtime scope so it can be attributed and replayed.
    /// </exception>
    public static SimulationDecisionRequest FromAmbientContext(
        SimulationSeedDomain domain,
        SimulationDecisionKind kind,
        string selectedResult,
        string? sourceId = null,
        string? inputMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(selectedResult);
        var current = SimulationExecutionContext.Current ?? throw new InvalidOperationException(
            "Cannot record a decision from ambient context: no simulation runtime scope is currently active. " +
            "Record decisions from within a controlled operation or an explicit EnterRuntime scope.");

        return new SimulationDecisionRequest(
            domain,
            kind,
            sourceId,
            inputMetadata,
            selectedResult,
            current.Runtime.Id,
            current.Node?.Address,
            current.LogicalExecutionId);
    }
}

/// <summary>
/// One recorded, immutable deterministic decision: everything in <see cref="SimulationDecisionRequest"/>
/// plus the monotonic <see cref="Id"/> assigned by the log that recorded it.
/// </summary>
/// <param name="Id">The decision's position in the log that recorded it.</param>
/// <param name="Domain">Which independent seed/decision domain this decision belongs to.</param>
/// <param name="Kind">The broad category of decision.</param>
/// <param name="SourceId">An optional stable identifier for the call site/source.</param>
/// <param name="InputMetadata">An optional, human-readable description of the input/range.</param>
/// <param name="SelectedResult">A stable string representation of the selected value.</param>
/// <param name="RuntimeId">The active simulation runtime's identity at the time of the decision.</param>
/// <param name="NodeId">The active node's address, or <see langword="null"/> if not node-scoped.</param>
/// <param name="LogicalExecutionId">The active logical execution identity placeholder.</param>
public sealed record SimulationDecisionRecord(
    SimulationDecisionId Id,
    SimulationSeedDomain Domain,
    SimulationDecisionKind Kind,
    string? SourceId,
    string? InputMetadata,
    string SelectedResult,
    Guid RuntimeId,
    string? NodeId,
    SimulationLogicalExecutionId LogicalExecutionId);
