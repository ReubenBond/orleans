namespace Orleans.Testing.Simulation.Runtime.Scheduling;

/// <summary>
/// An optional observer of <see cref="SimulationOperation"/> lifecycle transitions within a
/// <see cref="SimulationScheduler"/>. The scheduler invokes the listener exactly once per
/// applied state transition, in the deterministic order those transitions occur - transitions are
/// serialized with their publication, so external cancellation and signaling cannot publish a later
/// state before an earlier transition. A listener never observes two notifications concurrently, and
/// the sequence it sees is a stable function of the scheduling decisions made.
/// </summary>
/// <remarks>
/// Notifications are delivered <em>after</em> the transition has been applied and <em>outside</em>
/// the scheduler's internal lock, so a listener may safely read the operation's public state and
/// call back into the scheduler for non-driving operations (for example, to resume another operation).
/// Reentrant <see cref="SimulationScheduler.RunStep(CancellationToken)"/>/<see cref="SimulationScheduler.Drain(CancellationToken)"/>
/// is rejected because worker handoff cannot occur until the current transition publication completes.
/// A listener must not throw: an exception from a listener is a diagnostics bug and would corrupt the
/// scheduling sequence.
/// </remarks>
public interface ISimulationOperationListener
{
    /// <summary>
    /// Called after <paramref name="operation"/> has transitioned into <paramref name="newState"/>.
    /// </summary>
    /// <param name="operation">The operation whose state changed.</param>
    /// <param name="newState">The state the operation just entered.</param>
    void OnStateChanged(SimulationOperation operation, SimulationOperationState newState);
}

/// <summary>
/// An immutable, deterministic snapshot of one operation's identity and status, suitable for
/// diagnostics and for folding controlled-operation state into higher-level pending-work summaries.
/// Contains no non-deterministic data (no managed thread ids, timestamps, or hash codes).
/// </summary>
/// <param name="Id">The operation's stable identity.</param>
/// <param name="ParentId">The creator/parent operation, or <see cref="SimulationOperationId.None"/>.</param>
/// <param name="State">The operation's current lifecycle state.</param>
/// <param name="PauseReason">Why the operation is paused, or <see langword="null"/> if not paused.</param>
/// <param name="LogicalExecutionId">The operation's logical execution identity value.</param>
/// <param name="NodeAddress">The node address the operation is scoped to, or <see langword="null"/>.</param>
/// <param name="WorkDescription">The stable originating-work description.</param>
public sealed record SimulationOperationStatus(
    SimulationOperationId Id,
    SimulationOperationId ParentId,
    SimulationOperationState State,
    SimulationPauseReason? PauseReason,
    long LogicalExecutionId,
    string? NodeAddress,
    string WorkDescription);
