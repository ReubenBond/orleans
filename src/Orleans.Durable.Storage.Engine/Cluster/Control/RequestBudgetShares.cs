using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The controller's accounting of a cluster request budget (orleans.md §5.6, §5.14, OR-I21, OR-I22): each node's latest
/// reported demand (undrained keys for archive logs, or open object-store primary ledgers for object-store ledgers) and
/// the rate it reports running at, the share last assigned to it, and from those each report's reply.
/// </summary>
/// <remarks>
/// <para>
/// The nodes counted are those its registry view lists live and active, and every other node with a fresh report: one the
/// view shows dead or retired, which still runs, and one it does not list yet. A node's reservation is the larger of the
/// rate it reported and the share last assigned to it, or its floor while a listed node has not reported: a node takes a
/// rate only from a reply, and lowers it on its own, so a reservation bounds what it runs at. Each assignment is capped at
/// the budget less the others' reservations (OR-I21); the reservations themselves may pass the budget, until the nodes
/// holding the room report again, by the floors of nodes newly listed, by reported rates above what now fits, and after
/// the budget is lowered. A reply which finds no room lowers a node running above its floor to the floor, which only
/// lowers what it runs at, and otherwise assigns nothing, so a rate above what fits stays at most the floor; a reply
/// assigns nothing to a node until it is assigned a share by a later one.
/// </para>
/// <para>
/// A node the view shows dead or retired, whose reports still arrive, is still running: it stays reserved, counts no
/// demand, and is assigned its floor, so its rate decays through a share. A node leaves the totals only once its latest
/// report is older than twice <see cref="ArchiveLogRequestShare.ReplyIntervals"/> control intervals, by which time it runs
/// at most its floor (OR-I22).
/// </para>
/// <para>
/// A new activation knows no reservation, so it assigns none, and every node keeps its rate, until it has observed a
/// registry view listing a live active node and every node that view shows up, retired or not, has reported, or until twice
/// <see cref="ArchiveLogRequestShare.ReplyIntervals"/> control intervals passed, after which a silent node runs at most its
/// floor.
/// </para>
/// <para>
/// The totals are kept running against one view, recounted when the view changes and on each pass.
/// </para>
/// </remarks>
internal sealed class RequestBudgetShares
{
    /// <summary>The most demand one report counts, so the totals of any cluster fit a <see cref="long"/>.</summary>
    internal const long MaxReportedDemand = 1L << 48;

    // The relative tolerance of the sums' rounding.
    private const double Tolerance = 1e-9;

    private readonly double _budget;
    private readonly TimeSpan _staleAfter;
    private readonly DateTimeOffset _activated;
    private readonly Dictionary<NodeId, Entry> _entries = [];
    private readonly HashSet<NodeId> _reportedSinceActivation = [];
    private NodeView? _countedIn;
    private long _demand;
    private double _reserved;
    private int _entriesListed;
    private bool _ready;

    /// <summary>Initializes a new instance of the <see cref="RequestBudgetShares"/> class for an activation at <paramref name="activated"/>.</summary>
    /// <param name="budget">The cluster's request budget, such as <c>ArchiveLogRequestsPerSecond</c> or <c>ObjectStoreRequestsPerSecond</c>.</param>
    /// <param name="controlInterval">The nodes' control interval, at which each reports.</param>
    /// <param name="activated">When the activation began.</param>
    public RequestBudgetShares(double budget, TimeSpan controlInterval, DateTimeOffset activated)
    {
        ArchiveLogRequestShare.CheckBudget(budget, nameof(budget));
        _budget = budget;
        _staleAfter = controlInterval * (2 * ArchiveLogRequestShare.ReplyIntervals);
        _activated = activated;
    }

    /// <summary>Gets whether the activation assigns shares: it observed a view and heard every node the view shows up, or waited them out; for tests.</summary>
    internal bool Ready => _ready;

    /// <summary>Gets the sum of the counted nodes' reservations under the last view counted; for tests.</summary>
    internal double Reserved => _reserved + Unreported(_countedIn ?? NodeView.Empty) * ArchiveLogRequestShare.Floor(_budget, Math.Max(1, Counted(_countedIn ?? NodeView.Empty)));

    /// <summary>
    /// Records a node's report, its demand and the rate it runs at, if any, and returns its share, or
    /// <see langword="null"/> while the activation awaits its view or its nodes' reports, or when the budget has no room
    /// for a node running at or below its floor.
    /// </summary>
    public double? Report(NodeId node, long demand, double? rate, NodeView view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);
        Debug.Assert(demand >= 0, "A report's demand is never negative; its decoder refuses it.");
        Count(view);
        Remove(node);
        var entry = new Entry(Math.Clamp(demand, 0, MaxReportedDemand), rate ?? 0, Assigned: 0, now);
        Add(node, entry);
        if (!_ready)
        {
            // An activation which has not observed its registry knows of no node to wait for: an empty view is no evidence
            // that every node reported.
            if (view.LiveActive.IsEmpty)
            {
                return null;
            }

            _ = _reportedSinceActivation.Add(node);
            // Every node the view shows up may run at a share an earlier activation gave it, retired ones draining what they owe
            // among them, so each is waited for; a node the view shows dead, or does not list, is not (OR-I21's activation bound).
            _ready = view.Registry.Nodes.Keys.Where(view.IsUp).All(_reportedSinceActivation.Contains) || now - _activated >= _staleAfter;
            if (!_ready)
            {
                return null;
            }

            _reportedSinceActivation.Clear();
        }

        var nodes = Counted(view);
        var floor = ArchiveLogRequestShare.Floor(_budget, nodes);
        var room = _budget - (_reserved - entry.Reserved + Unreported(view) * floor);
        var target = Eligible(node, view) ? ArchiveLogRequestShare.Assign(_budget, nodes, Demand(node, entry, view), _demand) : floor;
        var share = Math.Min(target, room);
        if (!(share > 0))
        {
            if (!(entry.Rate > floor))
            {
                return null;
            }

            // No room, but the node runs above its floor: lowering it to the floor only lowers what it runs at.
            share = floor;
        }

        Debug.Assert(share <= room + _budget * Tolerance || share < entry.Rate, "OR-I21: a share fits within the budget less the others' reservations, or only lowers its node.");
        Remove(node);
        Add(node, entry with { Assigned = share });
        return share;
    }

    /// <summary>Leaves out every node whose latest report is older than twice <see cref="ArchiveLogRequestShare.ReplyIntervals"/> control intervals at <paramref name="now"/>, and recounts the totals.</summary>
    public void Prune(NodeView view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);
        foreach (var (node, entry) in _entries.ToList())
        {
            if (now - entry.At > _staleAfter)
            {
                _ = _entries.Remove(node);
            }
        }

        // Recounted from scratch once a pass, so the running totals carry no drift.
        _countedIn = null;
        Count(view);
    }

    /// <summary>Returns whether <paramref name="node"/>'s demand counts: a node the view shows dead or retired counts none.</summary>
    private static bool Eligible(NodeId node, NodeView view) => view.DeadSince(node) is null && !view.IsRetired(node);

    private static long Demand(NodeId node, Entry entry, NodeView view) => Eligible(node, view) ? entry.Demand : 0;

    /// <summary>Recounts the totals against <paramref name="view"/> when it is not the view they were counted against.</summary>
    private void Count(NodeView view)
    {
        if (ReferenceEquals(view, _countedIn))
        {
            return;
        }

        _countedIn = view;
        (_demand, _reserved, _entriesListed) = (0, 0, 0);
        foreach (var (node, entry) in _entries)
        {
            Tally(node, entry, +1);
        }
    }

    /// <summary>Returns the nodes counted: those the view lists live and active, and those outside it with a fresh report.</summary>
    private int Counted(NodeView view) => view.LiveActive.Length + _entries.Count - _entriesListed;

    /// <summary>Returns the nodes the view lists live and active with no fresh report, each reserved at its floor.</summary>
    private int Unreported(NodeView view) => view.LiveActive.Length - _entriesListed;

    private void Remove(NodeId node)
    {
        if (_entries.Remove(node, out var entry))
        {
            Tally(node, entry, -1);
        }
    }

    private void Add(NodeId node, Entry entry)
    {
        _entries[node] = entry;
        Tally(node, entry, +1);
    }

    /// <summary>Adds an entry to the totals counted against the current view, or takes it out.</summary>
    private void Tally(NodeId node, Entry entry, int sign)
    {
        var view = _countedIn!;
        _demand = checked(_demand + sign * Demand(node, entry, view));
        _reserved += sign * entry.Reserved;
        if (view.IsLiveActive(node))
        {
            _entriesListed += sign;
        }
    }

    /// <summary>A node's latest report and the share its reply assigned.</summary>
    private sealed record Entry(long Demand, double Rate, double Assigned, DateTimeOffset At)
    {
        public double Reserved => Math.Max(Rate, Assigned);
    }
}
