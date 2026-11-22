using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The arithmetic of the nodes' shares of the cluster's archive-log request budget, <c>ArchiveLogRequestsPerSecond</c>
/// (orleans.md §5.6): each node's floor, and its work-conserving share in proportion to its undrained keys.
/// </summary>
/// <remarks>
/// A share is <c>floor + (budget − N·floor)·keys/total</c> over the <c>N</c> nodes counted, so the shares of one snapshot
/// sum to exactly the budget, and to <c>budget/N</c> each when no node has undrained keys. The floor is the smaller of a
/// hundredth of the budget and <c>budget/(2N)</c>, so the floors of <c>N</c> nodes take at most half the budget, at any
/// <c>N</c>.
/// </remarks>
internal static class ArchiveLogRequestShare
{
    /// <summary>The floor's fraction of the budget while the cluster has at most 50 nodes: 1%.</summary>
    public const double FloorFraction = 0.01;

    /// <summary>
    /// The control intervals a node goes without a report reply before its share falls to its floor, and half those after
    /// which the controller leaves a silent node out of its totals (OR-I22).
    /// </summary>
    public const int ReplyIntervals = 5;

    /// <summary>The smallest budget, a request a second, so no floor is subnormal.</summary>
    public const double MinimumBudget = 1;

    /// <summary>Returns the floor of each of <paramref name="nodes"/> nodes: the smaller of a hundredth of the budget and <c>budget/(2N)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodes"/> is not positive.</exception>
    public static double Floor(double budget, int nodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodes);
        return Math.Min(budget * FloorFraction, budget / (2.0 * nodes));
    }

    /// <summary>
    /// Returns a node's share of <paramref name="budget"/> among <paramref name="nodes"/> nodes: its floor and, of the budget
    /// the floors leave, its proportion of the undrained keys, <paramref name="keys"/> of <paramref name="totalKeys"/>; an
    /// even share when no node has undrained keys.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="nodes"/> is not positive, or <paramref name="keys"/> is negative or above <paramref name="totalKeys"/>.
    /// </exception>
    public static double Assign(double budget, int nodes, long keys, long totalKeys)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keys);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keys, totalKeys);
        var floor = Floor(budget, nodes);
        if (totalKeys == 0)
        {
            return budget / nodes;
        }

        var share = floor + (budget - nodes * floor) * ((double)keys / totalKeys);
        Debug.Assert(share <= budget, "A share is at most the budget.");
        return share;
    }

    /// <summary>Returns whether <paramref name="requestsPerSecond"/> is a rate a node's drains can run at: positive and finite.</summary>
    public static bool IsRate(double requestsPerSecond) => double.IsFinite(requestsPerSecond) && requestsPerSecond > 0;

    /// <summary>Checks a request rate a node's drains run at: positive and finite.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive and finite.</exception>
    public static void CheckRate(double requestsPerSecond, string name)
    {
        if (!IsRate(requestsPerSecond))
        {
            throw new ArgumentOutOfRangeException(name, requestsPerSecond, "A request rate is positive and finite.");
        }
    }

    /// <summary>Checks the cluster's budget: finite and at least <see cref="MinimumBudget"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The budget is below a request a second or not finite.</exception>
    public static void CheckBudget(double budget, string name)
    {
        if (!double.IsFinite(budget) || budget < MinimumBudget)
        {
            throw new ArgumentOutOfRangeException(name, budget, "The archive-log request budget is finite and at least a request a second.");
        }
    }
}
