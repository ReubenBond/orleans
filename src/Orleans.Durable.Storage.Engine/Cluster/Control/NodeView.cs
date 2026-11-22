using System.Collections.Frozen;
using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The controller's view of the nodes at one registry revision (orleans.md §5.6, §5.7): each registered node's standing
/// and its liveness in the membership view the host computed against that registry value, and the nodes both active and
/// live, which may be given work. A departing node's claims have not ended, but it is given no work.
/// </summary>
internal sealed class NodeView
{
    private readonly FrozenDictionary<NodeId, NodeLiveness> _liveness;

    public NodeView(RegisteredNodes registry, IReadOnlyDictionary<NodeId, NodeLiveness> liveness)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(liveness);
        Registry = registry;
        _liveness = liveness.ToFrozenDictionary();
        LiveActive = [.. registry.Nodes.Where(entry => !entry.Value.IsRetired && IsLive(entry.Key)).Select(static entry => entry.Key).Order(NodeOrder.Instance)];
        DiskfulLiveActive = [.. LiveActive.Where(node => !IsDiskless(node))];
    }

    /// <summary>Gets the view of no node.</summary>
    public static NodeView Empty { get; } = new(RegisteredNodes.Empty, FrozenDictionary<NodeId, NodeLiveness>.Empty);

    /// <summary>Gets the registry value's projection, whose revision each transition carries as <c>ChosenAt</c>.</summary>
    public RegisteredNodes Registry { get; }

    /// <summary>Gets the nodes active in the registry and live in the membership view, in node order.</summary>
    public ImmutableArray<NodeId> LiveActive { get; }

    /// <summary>Gets the non-diskless nodes active in the registry and live in the membership view, in node order.</summary>
    public ImmutableArray<NodeId> DiskfulLiveActive { get; }

    /// <summary>Returns whether membership shows <paramref name="node"/> live: it may be given work.</summary>
    public bool IsLive(NodeId node) => _liveness.TryGetValue(node, out var liveness) && liveness.Value is NodeLiveness.Live;

    /// <summary>Returns whether membership shows <paramref name="node"/> shutting down: its claims stand, and it is given no work.</summary>
    public bool IsDeparting(NodeId node) => _liveness.TryGetValue(node, out var liveness) && liveness.Value is NodeLiveness.Departing;

    /// <summary>Returns whether <paramref name="node"/>'s claims stand: membership shows it live or departing.</summary>
    public bool IsUp(NodeId node) => _liveness.TryGetValue(node, out var liveness) && liveness.Value is NodeLiveness.Live or NodeLiveness.Departing;

    public bool IsRetired(NodeId node) => Registry.Find(node) is { IsRetired: true };

    public bool IsDiskless(NodeId node) => Registry.Find(node) is { Diskless: true };

    public bool IsLiveActive(NodeId node) => Registry.Find(node) is { IsRetired: false } && IsLive(node);

    /// <summary>Returns since when the membership view shows <paramref name="node"/> dead, or <see langword="null"/>.</summary>
    public DateTimeOffset? DeadSince(NodeId node) => _liveness.TryGetValue(node, out var liveness) && liveness.Value is NodeLiveness.Dead dead ? dead.Since : null;

    public string? DomainOf(NodeId node) => Registry.Find(node)?.FailureDomain;

    /// <summary>Returns whether <paramref name="other"/> shows every node as this view does.</summary>
    public bool SameLiveness(NodeView other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other._liveness.Count != _liveness.Count)
        {
            return false;
        }

        foreach (var (node, liveness) in _liveness)
        {
            if (!other._liveness.TryGetValue(node, out var theirs) || !liveness.Equals(theirs))
            {
                return false;
            }
        }

        return true;
    }
}
