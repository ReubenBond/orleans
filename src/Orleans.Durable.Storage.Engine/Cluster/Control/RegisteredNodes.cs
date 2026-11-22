using System.Collections.Frozen;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// What the controller decides from in a node registry value (orleans.md §5.3, §5.6, §5.14): its revision, which each
/// transition carries as <c>ChosenAt</c>, each registered node's retirement, failure domain, silo generation, ordinal, and
/// diskless flag, whether the cluster recorded an object-ledger store, and the active drain-all revision when a planned
/// whole-cluster drain off node-backed ledgers is in progress. The controller grain takes it from each registry value it
/// reads, so the controller's decisions name no silo.
/// </summary>
/// <param name="Revision">The registry value's revision.</param>
/// <param name="Nodes">Each registered node's standing.</param>
/// <param name="HasObjectLedgerStore">Whether the registry records an object-ledger store ID.</param>
/// <param name="DrainAllRevision">The active drain-all revision, or <c>0</c> when no drain-all is active.</param>
internal sealed record RegisteredNodes(
    ulong Revision,
    FrozenDictionary<NodeId, RegisteredNode> Nodes,
    bool HasObjectLedgerStore = false,
    ulong DrainAllRevision = 0)
{
    /// <summary>Gets the projection of the empty registry.</summary>
    public static RegisteredNodes Empty { get; } = new(0, []);

    /// <summary>
    /// Gets the revision which retired the node retired last, or 0 when the value records no retirement: the retirement
    /// floor the ledger map reaches once every retirement has been carried to it (orleans.md §5.3).
    /// </summary>
    public ulong LatestRetiredRevision
    {
        get
        {
            var latest = 0UL;
            foreach (var node in Nodes.Values)
            {
                latest = Math.Max(latest, node.RetiredRevision ?? 0);
            }

            return latest;
        }
    }

    /// <summary>Returns <paramref name="node"/>'s standing, or <see langword="null"/> when the registry holds no entry of it.</summary>
    public RegisteredNode? Find(NodeId node) => Nodes.GetValueOrDefault(node);
}

/// <summary>A registered node's standing: its retirement, its failure domain, its silo's generation, its ordinal, and whether it is diskless.</summary>
/// <param name="RetiredRevision">The revision of the registry write which retired the node, or <see langword="null"/> while
/// it is active.</param>
/// <param name="FailureDomain">The node's failure domain, or <see langword="null"/>.</param>
/// <param name="Generation">The generation of the silo the entry names, which a new registration of the node raises (OR-I2).</param>
/// <param name="Ordinal">The node's StatefulSet ordinal, or <see langword="null"/> when it has none.</param>
/// <param name="Diskless">Whether the node holds no local ledger store: eligible as an object-store ledger's primary, never as a node ledger's replica or primary (orleans.md §5.14).</param>
internal sealed record RegisteredNode(ulong? RetiredRevision, string? FailureDomain, int Generation, int? Ordinal, bool Diskless = false)
{
    /// <summary>Gets whether the entry is retired.</summary>
    public bool IsRetired => RetiredRevision is not null;
}
