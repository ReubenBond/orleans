using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The cluster's progress and stop readiness for a planned whole-cluster drain off node-backed ledgers before an
/// instance-releasing stop (orleans.md §5.14, §8), decided from one durable ledger map value and one durable node
/// registry value read after it.
/// </summary>
/// <param name="Active">
/// Whether drain-all is currently active in the durable node registry (<see cref="RegisteredNodes.DrainAllRevision"/> &gt; 0).
/// </param>
/// <param name="Epoch">The epoch of the ledger map value the status was decided from.</param>
/// <param name="Floor">That ledger map value's retirement floor.</param>
/// <param name="RegistryRevision">The revision of the node registry value the status was decided from.</param>
/// <param name="LatestDiskfulRetiredRevision">
/// The greatest retirement revision among retired diskful nodes in that registry value, or <c>0</c> if none is retired.
/// </param>
/// <param name="NodeLedgers">
/// The ledgers on <see cref="LedgerLocation.Nodes"/> (open or closed) that the ledger map value still lists, in ascending
/// ID order.
/// </param>
/// <param name="ActiveDiskfulNodes">The active non-diskless nodes in that registry value, in ascending node order.</param>
internal sealed record DrainAllStatus(
    bool Active,
    ulong Epoch,
    ulong Floor,
    ulong RegistryRevision,
    ulong LatestDiskfulRetiredRevision,
    ImmutableArray<LedgerId> NodeLedgers,
    ImmutableArray<NodeId> ActiveDiskfulNodes)
{
    /// <summary>
    /// Gets whether the map's retirement floor covers every retired diskful node's retirement revision, so no stale
    /// transition chosen from an earlier registry value can install a node-backed ledger (LG-I11, OR-I5).
    /// </summary>
    public bool FloorCovers => Floor >= LatestDiskfulRetiredRevision;

    /// <summary>
    /// Gets whether the cluster is durably ready for an instance-releasing stop: the node registry has been observed
    /// (<see cref="RegistryRevision"/> &gt; 0), drain-all has completed its atomic phase 2 retirement
    /// (<see cref="Active"/> is <see langword="false"/>), the ledger map lists zero ledgers on nodes (open or closed),
    /// the node registry lists zero active diskful nodes (so every StatefulSet ordinal is free for a restart over empty
    /// roots), and the map's retirement floor covers every diskful node's retirement (orleans.md §5.14, §8).
    /// </summary>
    public bool IsReady => RegistryRevision > 0 && !Active && NodeLedgers.IsEmpty && ActiveDiskfulNodes.IsEmpty && FloorCovers;

    /// <summary>Decides the drain-all status from <paramref name="registry"/> and <paramref name="map"/>.</summary>
    public static DrainAllStatus Of(RegisteredNodes registry, LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(map);
        var nodeLedgers = ImmutableArray.CreateBuilder<LedgerId>();
        foreach (var ledger in map.Ledgers)
        {
            if (ledger.Location.Value is LedgerLocation.Nodes)
            {
                nodeLedgers.Add(ledger.Id);
            }
        }

        var activeDiskful = ImmutableArray.CreateBuilder<NodeId>();
        ulong latestDiskfulRetired = 0;
        foreach (var (node, entry) in registry.Nodes)
        {
            if (entry.Diskless)
            {
                continue;
            }

            if (entry.RetiredRevision is { } retired)
            {
                if (retired > latestDiskfulRetired)
                {
                    latestDiskfulRetired = retired;
                }
            }
            else
            {
                activeDiskful.Add(node);
            }
        }

        activeDiskful.Sort(NodeOrder.Instance);
        return new DrainAllStatus(
            registry.DrainAllRevision > 0,
            map.Epoch,
            map.RetirementFloor,
            registry.Revision,
            latestDiskfulRetired,
            nodeLedgers.ToImmutable(),
            activeDiskful.ToImmutable());
    }

    /// <inheritdoc/>
    public bool Equals(DrainAllStatus? other) =>
        other is not null
        && (Active, Epoch, Floor, RegistryRevision, LatestDiskfulRetiredRevision) == (other.Active, other.Epoch, other.Floor, other.RegistryRevision, other.LatestDiskfulRetiredRevision)
        && NodeLedgers.SequenceEqual(other.NodeLedgers)
        && ActiveDiskfulNodes.SequenceEqual(other.ActiveDiskfulNodes);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Active, Epoch, Floor, RegistryRevision, LatestDiskfulRetiredRevision, NodeLedgers.Length, ActiveDiskfulNodes.Length);
}
