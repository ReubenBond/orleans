using System.Collections.Frozen;
using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The controller's view of the cluster for one pass (orleans.md §5.6, §5.7): the ledger map, the nodes at one registry
/// revision with their liveness, the nodes whose hand-off is pending, the generation each node last reported from, and
/// the reports' loads, unserved ledgers, lost
/// replicas, disk pressure, and drains, with the counts placement reads. Placement adjusts the counts as a pass chooses, so the
/// pass spreads the transitions it sends.
/// </summary>
internal sealed class ClusterState(
    MapCache map,
    NodeView nodes,
    IReadOnlyDictionary<NodeId, int> handingOff,
    IReadOnlyDictionary<NodeId, int> reported,
    IReadOnlyDictionary<LedgerId, long> load,
    IReadOnlyDictionary<NodeId, FrozenSet<LedgerId>> unserved,
    IReadOnlyDictionary<NodeId, FrozenSet<LedgerId>> lost,
    IReadOnlySet<NodeId> underDiskPressure,
    IReadOnlyDictionary<NodeId, bool> draining,
    DateTimeOffset now,
    IReadOnlyDictionary<NodeId, int>? objectLedgersReady = null)
{
    private readonly Dictionary<NodeId, int> _primaries = [with(map.Primaries)];
    private readonly Dictionary<NodeId, int> _replicas = [with(map.Replicas)];
    private readonly IReadOnlyDictionary<NodeId, int> _objectLedgersReady = objectLedgersReady ?? FrozenDictionary<NodeId, int>.Empty;

    /// <summary>Gets the ledger map the pass chooses from.</summary>
    public LedgerMapValue Map => map.Value;

    /// <summary>Gets what the controller derived from the map.</summary>
    public MapCache Cache => map;

    /// <summary>Gets the map's open ledgers, in ascending ID order.</summary>
    public ImmutableArray<LedgerMetadata> Open => map.Open;

    /// <summary>Gets the nodes at the registry revision each transition carries as <c>ChosenAt</c>.</summary>
    public NodeView Nodes { get; } = nodes;

    /// <summary>Gets the time of the pass.</summary>
    public DateTimeOffset Now { get; } = now;

    /// <summary>Gets the nodes active in the registry and live in the membership view, in node order.</summary>
    public ImmutableArray<NodeId> LiveActive => Nodes.LiveActive;

    /// <summary>
    /// Gets the nodes which may be given work: live and active, with no hand-off pending, in node order. Includes diskless
    /// nodes (orleans.md §5.14).
    /// </summary>
    public ImmutableArray<NodeId> Executors =>
        field.IsDefault ? field = [.. Nodes.LiveActive.Where(node => !handingOff.ContainsKey(node))] : field;

    /// <summary>
    /// Gets the nodes which may execute an object-store ledger transition or hold an object-store ledger's primaryship:
    /// live, active, with no hand-off pending, and reporting their object-ledger gate passed for their registered generation
    /// (OL-I1, orleans.md §5.14).
    /// </summary>
    public ImmutableArray<NodeId> ObjectStoreExecutors =>
        field.IsDefault ? field = [.. Executors.Where(IsObjectLedgersReady)] : field;

    /// <summary>
    /// Gets the nodes which may execute a node-ledger transition or hold a new node ledger's replica: live, active,
    /// non-diskless, with no hand-off pending, in node order (orleans.md §5.14).
    /// </summary>
    public ImmutableArray<NodeId> QuorumExecutors =>
        field.IsDefault ? field = [.. Nodes.DiskfulLiveActive.Where(node => !handingOff.ContainsKey(node))] : field;

    /// <summary>Gets the failure domains of the non-diskless nodes which may hold a new node ledger's replica.</summary>
    public FrozenSet<string> LiveDomains =>
        field ??= QuorumExecutors.Select(Nodes.DomainOf).OfType<string>().ToFrozenSet(StringComparer.Ordinal);

    public bool IsLive(NodeId node) => Nodes.IsLive(node);

    /// <summary>Returns whether <paramref name="node"/>'s claims stand: membership shows it live or departing.</summary>
    public bool IsUp(NodeId node) => Nodes.IsUp(node);

    public bool IsDeparting(NodeId node) => Nodes.IsDeparting(node);

    public bool IsRetired(NodeId node) => Nodes.IsRetired(node);

    public bool IsDiskless(NodeId node) => Nodes.IsDiskless(node);

    public bool IsLiveActive(NodeId node) => Nodes.IsLiveActive(node);

    public DateTimeOffset? DeadSince(NodeId node) => Nodes.DeadSince(node);

    /// <summary>
    /// Returns whether membership shows <paramref name="node"/> dead while the registry holds it active and non-diskless:
    /// a degraded choice may keep it among a successor's replicas (OR-I5), and a retired or diskless node it never keeps,
    /// so the retirement gate holds (OR-I4).
    /// </summary>
    public bool IsDeadActive(NodeId node) => DeadSince(node) is not null && Nodes.Registry.Find(node) is { IsRetired: false, Diskless: false };

    /// <summary>
    /// Returns whether <paramref name="node"/> is active and non-diskless in the registry while gracefully leaving
    /// (membership shows it <see cref="NodeLiveness.Departing"/>, or it is live with a hand-off pending): a degraded
    /// choice may keep it among a successor's replicas after any dead active replicas while at least <c>Q</c> live
    /// replicas exist (OR-I5), and a retired or diskless node it never keeps, so the retirement gate holds (OR-I4).
    /// </summary>
    public bool IsDepartingOrHandingOffActive(NodeId node) =>
        IsUp(node) && (IsDeparting(node) || IsHandingOff(node)) && Nodes.Registry.Find(node) is { IsRetired: false, Diskless: false };

    /// <summary>
    /// Returns whether a degraded choice may keep <paramref name="node"/> among a successor's replicas when fewer than
    /// <c>N</c> nodes may be given work (at least <c>Q</c> live): active and non-diskless in the registry, and either
    /// dead in membership or gracefully leaving (departing or handing off) (OR-I5, OR-I4).
    /// </summary>
    public bool IsKeepableDegradedReplica(NodeId node) => IsDeadActive(node) || IsDepartingOrHandingOffActive(node);

    public string? DomainOf(NodeId node) => Nodes.DomainOf(node);

    /// <summary>
    /// Returns whether <paramref name="node"/>'s hand-off is pending: it asked, and membership still shows the same
    /// generation of it live.
    /// </summary>
    public bool IsHandingOff(NodeId node) => handingOff.ContainsKey(node);

    /// <summary>
    /// Returns whether <paramref name="node"/> may be given work: live and active, with no hand-off pending.
    /// </summary>
    public bool CanExecute(NodeId node) => IsLiveActive(node) && !IsHandingOff(node);

    /// <summary>
    /// Returns whether <paramref name="node"/> has reported its object-ledger gate passed for its registered generation
    /// (OL-I1, orleans.md §5.14).
    /// </summary>
    public bool IsObjectLedgersReady(NodeId node) =>
        _objectLedgersReady.TryGetValue(node, out var generation) && generation >= (Nodes.Registry.Find(node)?.Generation ?? int.MaxValue);

    /// <summary>
    /// Returns whether <paramref name="node"/> may execute an object-store ledger transition or hold an object-store ledger:
    /// live, active, with no hand-off pending, and <see cref="IsObjectLedgersReady"/>.
    /// </summary>
    public bool CanExecuteObjectStore(NodeId node) => CanExecute(node) && IsObjectLedgersReady(node);

    /// <summary>
    /// Returns whether <paramref name="node"/> may execute a node-ledger transition or hold a new node ledger's replica:
    /// live, active, non-diskless, with no hand-off pending (orleans.md §5.14).
    /// </summary>
    public bool CanExecuteQuorum(NodeId node) => CanExecute(node) && !IsDiskless(node);

    /// <summary>
    /// Returns whether <paramref name="node"/> may gain primaries or replicas by balancing: it may be given work, and its
    /// controller activation heard a report from its registered generation, which tells whether it hands off, so a new
    /// activation balances onto no stopping or refused node it has not heard from (orleans.md §5.6).
    /// </summary>
    public bool IsBalanceTarget(NodeId node) =>
        CanExecute(node) && reported.TryGetValue(node, out var generation) && generation >= (Nodes.Registry.Find(node)?.Generation ?? int.MaxValue);

    /// <summary>Returns whether <paramref name="node"/> is leaving: retired, departing, or with its hand-off pending, so its primaries are handed off.</summary>
    public bool IsLeaving(NodeId node) => IsRetired(node) || IsDeparting(node) || IsHandingOff(node);

    /// <summary>Returns whether <paramref name="node"/>'s latest report lists <paramref name="ledger"/> unserved.</summary>
    public bool ReportedUnserved(NodeId node, LedgerId ledger) => unserved.TryGetValue(node, out var ledgers) && ledgers.Contains(ledger);

    /// <summary>
    /// Returns whether <paramref name="node"/> drains (orleans.md §5.6): it is live, and its latest report says it drains,
    /// serving with its silo's archive gate passed, or the activation has heard no report from it, so an activation names
    /// no second drainer for a node only because it has not heard from it.
    /// </summary>
    public bool Drains(NodeId node) => IsLive(node) && draining.GetValueOrDefault(node, true);

    /// <summary>Returns whether <paramref name="node"/> can drain a closed object-store ledger: <see cref="Drains"/> and <see cref="IsObjectLedgersReady"/>.</summary>
    public bool DrainsObjectStore(NodeId node) => Drains(node) && IsObjectLedgersReady(node);

    /// <summary>
    /// Returns whether the ledger map currently holds any object-store ledger (open or closed) overlapping
    /// <paramref name="range"/>: a primary serving <paramref name="range"/> must be <see cref="IsObjectLedgersReady"/>
    /// to read that history until it drains and drops.
    /// </summary>
    public bool HasOverlappingObjectStoreLedger(KeyRange range)
    {
        if (!Cache.HasObjectStoreLedgers)
        {
            return false;
        }

        foreach (var ledger in Map.Ledgers)
        {
            if (ledger.Location.Value is LedgerLocation.ObjectStore && ledger.Range.Overlaps(range))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether <paramref name="node"/>'s latest report shows it under disk pressure (orleans.md §5.9).</summary>
    public bool IsUnderPressure(NodeId node) => underDiskPressure.Contains(node);

    /// <summary>Returns whether <paramref name="node"/>'s latest report lists its replica of <paramref name="ledger"/> lost (LR-I2).</summary>
    public bool ReportedLost(NodeId node, LedgerId ledger) => lost.Count != 0 && lost.TryGetValue(node, out var ledgers) && ledgers.Contains(ledger);

    /// <summary>
    /// Returns the load the ledger's primary last reported for <paramref name="ledger"/>, or <see langword="null"/> when it
    /// reported none: an unknown load, neither hot nor cold.
    /// </summary>
    public long? Load(LedgerId ledger) => load.TryGetValue(ledger, out var reported) ? reported : null;

    public int Primaries(NodeId node) => _primaries.GetValueOrDefault(node);

    public int Replicas(NodeId node) => _replicas.GetValueOrDefault(node);

    /// <summary>Counts a sent transition's moves: its executor gains the primaries its opens take from <paramref name="from"/>.</summary>
    public void MovePrimaries(NodeId? from, NodeId to, int count)
    {
        if (from is { } previous)
        {
            Add(_primaries, previous, -count);
        }

        Add(_primaries, to, count);
    }

    /// <summary>Counts a sent transition's replicas.</summary>
    public void CountReplicas(ImmutableArray<NodeId> removed, ImmutableArray<NodeId> added)
    {
        foreach (var node in removed)
        {
            Add(_replicas, node, -1);
        }

        foreach (var node in added)
        {
            Add(_replicas, node, 1);
        }
    }

    private static void Add(Dictionary<NodeId, int> counts, NodeId node, int delta) => counts[node] = counts.GetValueOrDefault(node) + delta;
}
