using System.Globalization;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

/// <summary>
/// One waiter parked on a <see cref="SimulationResource"/>: the pairing of a paused
/// <see cref="SimulationOperation"/> with the resource it is blocked on, a deterministic enqueue
/// sequence, the pause reason, and a single-assignment resolution. The scheduler creates exactly one
/// of these per <c>WaitOnResource</c> call, keeps it in the
/// resource's ordered wait queue while the operation is paused, and resolves it exactly once
/// (signal, timeout, or cancel) before making the operation runnable again.
/// </summary>
internal sealed class SimulationResourceWaiter
{
    internal SimulationResourceWaiter(
        SimulationOperation operation,
        SimulationResource resource,
        long enqueueSequence,
        SimulationPauseReason reason)
    {
        Operation = operation;
        Resource = resource;
        EnqueueSequence = enqueueSequence;
        Reason = reason;
    }

    /// <summary>Gets the paused operation this waiter represents.</summary>
    public SimulationOperation Operation { get; }

    /// <summary>Gets the resource this waiter is blocked on.</summary>
    public SimulationResource Resource { get; }

    /// <summary>
    /// Gets the scheduler-assigned, strictly increasing sequence this waiter was enqueued with. It
    /// is the deterministic, replayable tie-break for wait-queue order and for firing coincident
    /// virtual-time timeouts.
    /// </summary>
    public long EnqueueSequence { get; }

    /// <summary>Gets the reason recorded when the operation paused onto this resource.</summary>
    public SimulationPauseReason Reason { get; }

    /// <summary>
    /// Gets the resolution assigned to this waiter, or <see langword="null"/> while it is still
    /// pending. Set exactly once by <see cref="TryResolve"/>.
    /// </summary>
    public SimulationWaitOutcome? Resolution { get; private set; }

    /// <summary>Gets a value indicating whether this waiter has been resolved.</summary>
    public bool IsResolved => Resolution.HasValue;

    /// <summary>
    /// The virtual-time timeout registered for this waiter (finite timeout only), or
    /// <see langword="null"/> for an infinite or zero timeout. Owned/cleaned up by the scheduler.
    /// </summary>
    public SimulationResourceTimeoutRegistration? Timeout { get; set; }

    /// <summary>
    /// The synchronous cancellation registration disposed when this waiter resolves, so no callback
    /// leaks past the wait. <see langword="default"/> when the wait had no cancelable token.
    /// </summary>
    public CancellationTokenRegistration CancellationRegistration { get; set; }

    /// <summary>
    /// The exact vector clock published by the operation that resolved this waiter as signaled.
    /// Kept on the waiter so a later signal on the same resource cannot replace its happens-before edge.
    /// </summary>
    internal Racing.RaceTracker.Clock? RaceReleaseClock { get; set; }

    /// <summary>
    /// Attempts to assign the first (and only) resolution to this waiter. The first of
    /// signal/timeout/cancel to call this wins; every later attempt observes an already-resolved
    /// waiter and returns <see langword="false"/>, which is exactly how the scheduler makes
    /// release-vs-timeout-vs-cancel races deterministic and prevents double wakeups.
    /// </summary>
    /// <param name="outcome">The terminal outcome to record.</param>
    /// <returns><see langword="true"/> if this call assigned the resolution; <see langword="false"/> if it was already resolved.</returns>
    public bool TryResolve(SimulationWaitOutcome outcome)
    {
        if (Resolution.HasValue)
        {
            return false;
        }

        Resolution = outcome;
        return true;
    }

    /// <summary>
    /// Builds an immutable, deterministic diagnostic snapshot of this waiter.
    /// </summary>
    public SimulationResourceWaiterInfo ToInfo() => new(
        Operation.Id,
        Operation.WorkDescription,
        Resource.Id,
        Resource.Name,
        EnqueueSequence,
        Reason.ToString(),
        Timeout is { } t ? t.DueTime : null,
        Resolution);

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Operation.Id} waiting on {Resource.Id} (seq={EnqueueSequence}, {Reason})");
}

/// <summary>
/// An immutable, deterministic snapshot of one waiter parked on a resource, for diagnostics and
/// deadlock reports. Contains no non-deterministic data (no managed thread ids, timestamps, or hash
/// codes) - only stable identities, the deterministic enqueue order, and the stable reason text.
/// </summary>
/// <param name="OperationId">The paused operation's stable identity.</param>
/// <param name="WorkDescription">The operation's stable originating-work description.</param>
/// <param name="ResourceId">The resource being waited on.</param>
/// <param name="ResourceName">The resource's stable, human-readable name.</param>
/// <param name="EnqueueSequence">The deterministic wait-queue order.</param>
/// <param name="Reason">The stable pause-reason text.</param>
/// <param name="TimeoutDueTime">The virtual-time instant this wait times out, or <see langword="null"/> for an infinite wait.</param>
/// <param name="Resolution">The wait's resolution, or <see langword="null"/> if still pending.</param>
public sealed record SimulationResourceWaiterInfo(
    SimulationOperationId OperationId,
    string WorkDescription,
    SimulationResourceId ResourceId,
    string ResourceName,
    long EnqueueSequence,
    string Reason,
    TimeSpan? TimeoutDueTime,
    SimulationWaitOutcome? Resolution);
