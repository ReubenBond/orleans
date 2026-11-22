using System.Collections.Frozen;
using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// What the controller derives from one ledger map value, once per epoch: its open and closed ledgers across both
/// locations (on nodes and in the object store), each node's primaries and node-ledger replicas, the open ledgers in split
/// order and by replica node, and the most a takeover adds to the map.
/// </summary>
internal sealed class MapCache
{
    public MapCache(LedgerMapValue value, int targetReplicas = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        TargetReplicas = targetReplicas;
        var targetLocation = targetReplicas > 0 ? DummyNodes(targetReplicas) : null;
        var open = ImmutableArray.CreateBuilder<LedgerMetadata>();
        var closed = ImmutableArray.CreateBuilder<LedgerMetadata>();
        var primaries = new Dictionary<NodeId, int>();
        var replicas = new Dictionary<NodeId, int>();
        var hasObjectStoreLedgers = false;
        var hasNodeLedgers = false;
        foreach (var ledger in value.Ledgers)
        {
            switch (ledger.Location.Value)
            {
                case LedgerLocation.ObjectStore:
                    hasObjectStoreLedgers = true;
                    break;
                case LedgerLocation.Nodes:
                    hasNodeLedgers = true;
                    break;
                case null:
                default:
                    throw new System.Diagnostics.UnreachableException("conventions.md §15: a ledger location holds a case.");
            }

            if (!ledger.IsOpen)
            {
                closed.Add(ledger);
                continue;
            }

            open.Add(ledger);
            primaries[ledger.Primary.Node] = primaries.GetValueOrDefault(ledger.Primary.Node) + 1;
            int growth;
            switch (ledger.Location.Value)
            {
                case LedgerLocation.Nodes nodes:
                    foreach (var replica in nodes.Replicas)
                    {
                        replicas[replica] = replicas.GetValueOrDefault(replica) + 1;
                    }

                    growth = value.GrowthOf(new Transition.Replace(ledger.Id, new TransitionChoice(nodes.Replicas[0], ledger.Location, ledger.K, 0, value.Epoch)));
                    break;
                case LedgerLocation.ObjectStore:
                    growth = value.GrowthOf(new Transition.Replace(ledger.Id, new TransitionChoice(ledger.Primary.Node, ledger.Location, ledger.K, 0, value.Epoch)));
                    break;
                case null:
                default:
                    throw new System.Diagnostics.UnreachableException("conventions.md §15: a ledger location holds a case.");
            }

            if (targetLocation is { } target)
            {
                // Account for the larger of current and target placement so rollback (ObjectStore -> Nodes) or replica
                // growth reserves enough room for a takeover adding 16 * N bytes per ledger.
                var targetGrowth = value.GrowthOf(new Transition.Replace(ledger.Id, new TransitionChoice(target.Replicas[0], target, ledger.K, 0, value.Epoch)));
                growth = Math.Max(growth, targetGrowth);
            }

            MaxTakeoverGrowth = Math.Max(MaxTakeoverGrowth, growth);
        }

        Open = open.ToImmutable();
        Closed = closed.ToImmutable();
        HasObjectStoreLedgers = hasObjectStoreLedgers;
        HasNodeLedgers = hasNodeLedgers;
        Primaries = primaries.ToFrozenDictionary();
        Replicas = replicas.ToFrozenDictionary();
        MaxPrimaries = primaries.Count == 0 ? 0 : primaries.Values.Max();
    }

    public LedgerMapValue Value { get; }

    /// <summary>Gets whether the map holds any object-store ledger, open or closed.</summary>
    public bool HasObjectStoreLedgers { get; }

    /// <summary>Gets whether the map holds any node-backed ledger, open or closed.</summary>
    public bool HasNodeLedgers { get; }

    /// <summary>Gets the target replica count used when computing <see cref="MaxTakeoverGrowth"/> (0 for object-store placement).</summary>
    public int TargetReplicas { get; }

    private static LedgerLocation.Nodes DummyNodes(int count)
    {
        var nodes = ImmutableArray.CreateBuilder<NodeId>(count);
        for (var i = 1; i <= count; i++)
        {
            nodes.Add(new NodeId(new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));
        }

        return new LedgerLocation.Nodes(nodes.MoveToImmutable());
    }

    /// <summary>Gets the open ledgers, in ascending ID order.</summary>
    public ImmutableArray<LedgerMetadata> Open { get; }

    /// <summary>Gets the closed ledgers, in ascending ID order.</summary>
    public ImmutableArray<LedgerMetadata> Closed { get; }

    /// <summary>Gets the open ledgers each node is primary of.</summary>
    public FrozenDictionary<NodeId, int> Primaries { get; }

    /// <summary>Gets the open ledgers on nodes each node is a replica of.</summary>
    public FrozenDictionary<NodeId, int> Replicas { get; }

    /// <summary>Gets the most open ledgers one node is primary of: the most takeovers one node's failure starts.</summary>
    public int MaxPrimaries { get; }

    /// <summary>Gets the most bytes a takeover of an open ledger adds to the map, at its current location.</summary>
    public int MaxTakeoverGrowth { get; }

    /// <summary>Gets the open ledgers in the order splits consider them: shallowest range first, then ID.</summary>
    public ImmutableArray<LedgerMetadata> BySplitOrder =>
        field.IsDefault ? field = [.. Open.OrderBy(static ledger => ledger.Range.Depth).ThenBy(static ledger => ledger.Id.Value)] : field;

    /// <summary>
    /// Returns the open ledgers on nodes which name <paramref name="node"/> among their replicas, in ascending ID order,
    /// indexed on the first call, so replica evening reads a node's ledgers without a scan of the map.
    /// </summary>
    public ImmutableArray<LedgerMetadata> ReplicatedBy(NodeId node) => ByReplica.GetValueOrDefault(node, []);

    private FrozenDictionary<NodeId, ImmutableArray<LedgerMetadata>> ByReplica => field ??= IndexByReplica(Open);

    private static FrozenDictionary<NodeId, ImmutableArray<LedgerMetadata>> IndexByReplica(ImmutableArray<LedgerMetadata> open)
    {
        var byReplica = new Dictionary<NodeId, ImmutableArray<LedgerMetadata>.Builder>();
        foreach (var ledger in open)
        {
            if (ledger.Location.Value is not LedgerLocation.Nodes nodes)
            {
                continue;
            }

            foreach (var replica in nodes.Replicas)
            {
                if (!byReplica.TryGetValue(replica, out var ledgers))
                {
                    byReplica[replica] = ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>();
                }

                ledgers.Add(ledger);
            }
        }

        return byReplica.ToFrozenDictionary(static entry => entry.Key, static entry => entry.Value.ToImmutable());
    }
}
