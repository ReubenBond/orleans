using System.Collections.Immutable;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Chooses the replicas of the ledgers a transition opens (orleans.md §5.7, OR-I5): <c>N</c> distinct nodes, each active
/// in the controller's registry value and live in its membership view, the executor first. A node with its hand-off
/// pending holds no new replica, and a node under disk pressure comes last. A degraded choice, which only a takeover or a
/// hand-off off a leaving primary asks for, may also keep the ledger's dead replicas while <c>Q</c> chosen nodes are live.
/// </summary>
internal static class Placement
{
    /// <summary>
    /// Returns the replicas: <paramref name="executor"/>, which every caller chose among the nodes which may be given work;
    /// then the ledger's current replicas which stay, those which may be given work and are not <paramref name="leaving"/>,
    /// in their order; then the nodes which may be given work, ordered with the nodes under no disk pressure first, then by
    /// fewest replicas, then by node order, each preferring a failure domain the chosen nodes do not use: an unused domain
    /// ranks above pressure, since spreading domains protects acknowledged entries and pressure only slows the node.
    /// When fewer than <paramref name="replicas"/> nodes may, a <paramref name="degraded"/> choice, once every node which may
    /// is chosen and they are at least <c>Q = (replicas + 1) / 2</c>, fills the remaining places with the current replicas
    /// which membership shows dead and the registry active, not <paramref name="leaving"/>, in their order: the
    /// successor then runs as a ledger whose replicas died after it opened, and the grace and repair roll them out
    /// (orleans.md §5.7). <see langword="null"/> when no such choice exists.
    /// </summary>
    public static ImmutableArray<NodeId>? Place(ClusterState state, NodeId executor, ImmutableArray<NodeId> current, ImmutableArray<NodeId> leaving, int replicas, bool degraded = false)
    {
        Debug.Assert(state.CanExecuteQuorum(executor), "A node-ledger transition's executor is live, active, non-diskless, and hands nothing off (OR-I5, orleans.md §5.14).");
        if (!degraded && state.QuorumExecutors.Length < replicas)
        {
            return null;
        }

        var chosen = new List<NodeId>(replicas) { executor };
        foreach (var node in current)
        {
            if (chosen.Count < replicas && !chosen.Contains(node) && state.CanExecuteQuorum(node) && !leaving.Contains(node))
            {
                chosen.Add(node);
            }
        }

        if (chosen.Count < replicas)
        {
            var remaining = state.QuorumExecutors
                .Where(node => !chosen.Contains(node) && !leaving.Contains(node))
                .OrderBy(state.IsUnderPressure)
                .ThenBy(state.Replicas)
                .ThenBy(static node => node, NodeOrder.Instance)
                .ToList();
            while (chosen.Count < replicas && remaining.Count > 0)
            {
                var used = chosen.Select(state.DomainOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var index = remaining.FindIndex(node => state.DomainOf(node) is not { } domain || !used.Contains(domain));
                var next = remaining[Math.Max(index, 0)];
                chosen.Add(next);
                remaining.RemoveAt(Math.Max(index, 0));
            }
        }

        if (degraded && chosen.Count < replicas && chosen.Count >= (replicas + 1) / 2)
        {
            var live = chosen.Count;
            foreach (var node in current)
            {
                if (chosen.Count < replicas && !chosen.Contains(node) && state.IsDeadActive(node) && !leaving.Contains(node))
                {
                    chosen.Add(node);
                }
            }

            Debug.Assert(
                chosen.Count < replicas
                || live == state.QuorumExecutors.Count(node => !leaving.Contains(node))
                    && chosen.Take(live).All(state.CanExecuteQuorum)
                    && chosen.Skip(live).All(node => state.IsDeadActive(node) && current.Contains(node)),
                "A degraded choice names every non-diskless node which may be given work, at least Q, then current replicas dead in membership and active in the registry (OR-I5).");
        }

        return chosen.Count == replicas ? [.. chosen] : null;
    }
}
