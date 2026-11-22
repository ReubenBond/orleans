using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// Diagnostic information about a single scheduled item found in a cluster or node queue while
/// building a <see cref="SimulationPendingWorkSummary"/>. Captures only information that is
/// already available on the item and its queue - no speculative call-site or creation tracing.
/// </summary>
/// <param name="QueueIdentity">
/// The identity of the queue the item is waiting in: <c>"cluster"</c> for the cluster-level queue,
/// or the owning node's <see cref="SimulationNode.NetworkAddress"/> for a node queue.
/// </param>
/// <param name="Kind">A stable category for the scheduled work, such as <c>"action"</c> or <c>"timer"</c>.</param>
/// <param name="Description">A human-readable description which does not expose implementation instances.</param>
/// <param name="DueTime">The absolute simulated time at which the item is due.</param>
/// <param name="SequenceNumber">The item's scheduling sequence number, used to break due-time ties.</param>
/// <param name="IsReady">Whether the item's due time has already passed (it is ready to execute).</param>
/// <param name="IsBlocked">
/// Whether the item is ready but cannot currently execute because its owning node is suspended.
/// Always <see langword="false"/> for cluster-queue items and for items that are not yet ready.
/// </param>
public sealed record SimulationScheduledItemDiagnostic(
    string QueueIdentity,
    string Kind,
    string Description,
    DateTimeOffset DueTime,
    long SequenceNumber,
    bool IsReady,
    bool IsBlocked)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var readiness = IsBlocked ? "blocked" : IsReady ? "ready" : "waiting";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{QueueIdentity}] {Kind}: {Description} due={DueTime:O} seq={SequenceNumber} ({readiness})");
    }
}
