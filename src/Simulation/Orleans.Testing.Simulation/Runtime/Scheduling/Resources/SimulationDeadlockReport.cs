namespace Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

/// <summary>
/// The scheduler's deterministic classification of whether outstanding work can still make progress.
/// It deliberately separates a genuine resource-ownership deadlock from the several benign "nothing
/// is running right now" states so diagnostics never cry deadlock at a simulation that is merely
/// waiting for modeled time to advance or for an external signal.
/// </summary>
public enum SimulationLivenessState
{
    /// <summary>No non-terminal operations remain; the schedule has run to completion.</summary>
    Quiescent,

    /// <summary>
    /// At least one operation is runnable, so the schedule can advance immediately by driving another
    /// step. (When the controlling thread is momentarily idle this is the "idle with pending work"
    /// case - there is work ready, it simply is not being driven at this instant.)
    /// </summary>
    Progressing,

    /// <summary>
    /// No operation is runnable, but at least one wait has a pending virtual-time timeout, so
    /// advancing modeled time (see <see cref="SimulationScheduler.TryAdvanceVirtualTime"/>)
    /// will resume an operation. This is a paused-until-time state, not a deadlock.
    /// </summary>
    PausedUntilTime,

    /// <summary>
    /// No operation is runnable and no timeout is pending, but the blocked operations are waiting on
    /// resources that no operation in a cycle owns (an ownerless event/semaphore or a task-completion
    /// resource). Such a wait can still be completed from outside the blocked set, so it is not a
    /// resource-ownership deadlock.
    /// </summary>
    ExternallyCompletable,

    /// <summary>
    /// No operation is runnable, no timeout is pending, and a true wait-for cycle exists in which every
    /// operation waits indefinitely on a resource owned by the next operation in the cycle. No amount
    /// of time advancement or internal signaling can break it: this is a genuine deadlock.
    /// </summary>
    Deadlocked,
}

/// <summary>
/// One node in a detected wait-for cycle: an operation waiting indefinitely on a resource owned by the
/// next operation in the cycle. Contains only deterministic identities and metadata (no thread ids,
/// timestamps, or hash codes) so cycle reports are stable and replayable.
/// </summary>
/// <param name="OperationId">The waiting operation's stable identity.</param>
/// <param name="WorkDescription">The waiting operation's stable originating-work description.</param>
/// <param name="ResourceId">The resource the operation is blocked on.</param>
/// <param name="ResourceName">The resource's stable, human-readable name.</param>
/// <param name="OwnerId">The operation that owns <paramref name="ResourceId"/> - the next node in the cycle.</param>
/// <param name="EnqueueSequence">The deterministic wait-queue position the waiter holds on the resource.</param>
/// <param name="Reason">The stable pause-reason text recorded when the operation began waiting.</param>
public sealed record SimulationWaitCycleEntry(
    SimulationOperationId OperationId,
    string WorkDescription,
    SimulationResourceId ResourceId,
    string ResourceName,
    SimulationOperationId OwnerId,
    long EnqueueSequence,
    string Reason);

/// <summary>
/// A single detected wait-for cycle, given as an ordered ring of <see cref="SimulationWaitCycleEntry"/>
/// starting at the smallest operation id in the cycle (a stable, deterministic rotation). Following
/// each entry's <see cref="SimulationWaitCycleEntry.OwnerId"/> leads to the next entry's operation,
/// and the last entry's owner is the first entry's operation.
/// </summary>
/// <param name="Entries">The cycle's nodes, in owner-follows order from the smallest operation id.</param>
public sealed record SimulationWaitCycle(IReadOnlyList<SimulationWaitCycleEntry> Entries);

/// <summary>
/// The deterministic result of a deadlock analysis: the overall <see cref="Liveness"/> classification,
/// every detected wait-for <see cref="Cycles"/>, and supporting counts. Intended to fold into
/// execution diagnostics so a stuck simulation reports <em>why</em> it is stuck rather than simply
/// hanging.
/// </summary>
/// <param name="Liveness">The overall progress classification.</param>
/// <param name="Cycles">
/// Detected wait-for cycles. A cycle can be present while <see cref="Liveness"/> is
/// <see cref="SimulationLivenessState.PausedUntilTime"/> when another live deadline can still advance
/// the schedule and break it; it is terminal only when <see cref="Liveness"/> is
/// <see cref="SimulationLivenessState.Deadlocked"/>.
/// </param>
/// <param name="RunnableCount">The number of runnable operations at analysis time.</param>
/// <param name="BlockedCount">The number of operations paused on a resource at analysis time.</param>
/// <param name="PendingTimeoutCount">The number of live pending virtual-time timeouts at analysis time.</param>
public sealed record SimulationDeadlockReport(
    SimulationLivenessState Liveness,
    IReadOnlyList<SimulationWaitCycle> Cycles,
    int RunnableCount,
    int BlockedCount,
    int PendingTimeoutCount)
{
    /// <summary>Gets a value indicating whether a genuine resource-ownership deadlock was detected.</summary>
    public bool IsDeadlocked => Liveness == SimulationLivenessState.Deadlocked;
}
