using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// Answers what a silo's node observes of itself (orleans.md §5.9, §8): the state of its Journals as it stands, and the
/// governance loop's last record of the node's resources and of each open ledger it is primary of.
/// </summary>
internal sealed class DurableStorageStatus(LocalJournals journals, NodeLedgers ledgers)
{
    /// <summary>Gets the state of the node's Journals.</summary>
    public LocalJournals.JournalsState Journals => journals.State;

    /// <summary>
    /// Gets the governance loop's last record of the node, once each control interval while its runtime runs;
    /// <see langword="null"/> before the first record and once the runtime stops.
    /// </summary>
    public DurableStorageNodeStatus? NodeStatus => ledgers.Runtime?.Metrics.LastStatus;
}

/// <summary>A node's resources and its primaries, as the governance loop recorded them at one control interval (orleans.md §5.9).</summary>
/// <param name="Node">The node.</param>
/// <param name="ObservedAt">When the governance loop recorded it.</param>
/// <param name="Resources">The node's last measured resources, or <see langword="null"/> before the first measurement.</param>
/// <param name="Primaries">
/// Each open ledger the node's map view makes it primary of whose writer its registry holds; the throttle gauges, which
/// read the pressure state as it stands, count the ledgers the map makes it primary of whether or not it serves them.
/// </param>
internal sealed record DurableStorageNodeStatus(
    NodeId Node,
    DateTimeOffset ObservedAt,
    NodeResources? Resources,
    ImmutableArray<DurableStorageLedgerStatus> Primaries);

/// <summary>A node's resources, as one disk measurement found them (orleans.md §5.9).</summary>
/// <param name="MeasuredAt">When the node measured them.</param>
/// <param name="DiskUsedBytes">The bytes of the node's LedgerStore files.</param>
/// <param name="DiskFreeBytes">The least free space of the volumes holding the store.</param>
/// <param name="DiskBudgetBytes">The node's disk budget.</param>
/// <param name="UnderPressure">Whether the measurement put the node under disk pressure.</param>
/// <param name="RetainedWalFiles">The WAL files the node keeps, from the oldest live replica's first file.</param>
/// <param name="LiveReplicas">The node's live replicas.</param>
internal sealed record NodeResources(
    DateTimeOffset MeasuredAt,
    long DiskUsedBytes,
    long DiskFreeBytes,
    long DiskBudgetBytes,
    bool UnderPressure,
    ulong RetainedWalFiles,
    int LiveReplicas);

/// <summary>An open ledger a node is primary of, at one control interval (orleans.md §5.9).</summary>
/// <param name="Ledger">The ledger.</param>
/// <param name="AppliedBelow">
/// The slot below which the node's Journals applied every batch to warm state, or <see langword="null"/> before they
/// admitted a write to the ledger. It is read before <paramref name="ContiguousTail"/>, which covers every applied batch
/// and only grows, so it is at most that tail.
/// </param>
/// <param name="ContiguousTail">The contiguous tail the ledger's writer knows.</param>
/// <param name="Watermark">
/// The drain watermark the node knows. A drain follows the tail the node's reader learns, and may pass batches the
/// Journals have yet to apply, so the watermark is ordered with neither slot above.
/// </param>
/// <param name="Undrained">What the writer proposed in the segments at or above the watermark: their bytes and the age of the oldest batch with rows.</param>
/// <param name="ThrottledBy">The nodes whose disk pressure throttles the ledger, the node's own among them; empty when it is not throttled (OR-I10).</param>
internal sealed record DurableStorageLedgerStatus(
    LedgerId Ledger,
    Slot? AppliedBelow,
    Slot ContiguousTail,
    Slot Watermark,
    LedgerUndrained Undrained,
    ImmutableArray<NodeId> ThrottledBy);
