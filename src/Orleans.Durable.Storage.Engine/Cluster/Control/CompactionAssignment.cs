using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A compaction the controller assigns (archive-lsm.md §8.8, orleans.md §5.6): the overfull partition, the primary of the
/// open ledger containing its lowest key, which compacts it, and the epoch of the manifest value the controller found it
/// overfull in, which the assignee's manifest view reaches (<c>ManifestView.RefreshAsync</c>) before it compacts.
/// </summary>
/// <param name="Partition">The overfull partition.</param>
/// <param name="Assignee">The node which compacts it.</param>
/// <param name="ManifestEpoch">The epoch of the manifest value which showed it overfull.</param>
internal sealed record CompactionAssignment(KeyRange Partition, NodeId Assignee, ulong ManifestEpoch);
