using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// The node's pressure state (orleans.md §5.9, OR-I10), a data-plane type: whether the node is under disk pressure, and
/// whether the primary of a ledger on this node is throttled. A primary is throttled while its own node is under disk
/// pressure, or while a pressure report from one of its ledger's replica nodes lasts: <see cref="ReportLifetimeIntervals"/>
/// control intervals on this node's monotonic clock. The silo's Ledgers runtime records its node's own pressure each
/// control interval, and the node control grain records each report a replica node sends. The journal write path asks
/// <see cref="IsThrottled"/> for the key's open ledger before it calls the Journals, which counts each write it answers
/// <c>Throttled</c>.
/// </summary>
internal sealed partial class NodePressure
{
    /// <summary>The control intervals a replica node's pressure report lasts (OR-I10).</summary>
    internal const int ReportLifetimeIntervals = 5;

    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<LedgerId, long> _throttledWrites = new();
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // Each field is replaced whole, so a reader needs no lock: the timestamp of each replica node's last report; the throttle
    // causes of each open ledger this node is primary of, as the last observation found them; and the node's own pressure.
    private ImmutableDictionary<NodeId, long> _reports = [];
    private ImmutableDictionary<LedgerId, ImmutableArray<NodeId>> _throttleCauses = [];
    private Func<bool>? _isObjectStoreThrottled;
    private bool _underPressure;

    /// <summary>Initializes a new instance of the <see cref="NodePressure"/> class.</summary>
    /// <param name="controlInterval">The control interval, <c>ControlInterval</c>, equal on every silo, by which a report's
    /// lifetime is measured.</param>
    /// <param name="timeProvider">The clock whose timestamps time the reports.</param>
    /// <param name="logger">Logs each throttle which starts and ends (events 6204, 6205).</param>
    public NodePressure(TimeSpan controlInterval, TimeProvider timeProvider, ILogger<NodePressure> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(controlInterval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _lifetime = ReportLifetimeIntervals * controlInterval;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether the node itself is under disk pressure, as its last measurement found.</summary>
    public bool UnderPressure => Volatile.Read(ref _underPressure);

    /// <summary>
    /// Gets, for each open ledger this node was primary of at the last <see cref="Observe"/>, the nodes whose pressure
    /// throttled it then: empty when it was not throttled.
    /// </summary>
    public ImmutableDictionary<LedgerId, ImmutableArray<NodeId>> ThrottleCauses => Volatile.Read(ref _throttleCauses);

    /// <summary>
    /// Gets the writes answered <c>Throttled</c> for each open ledger whose primary is this node, since its first; each
    /// evaluation (<see cref="Observe"/>) ends the count of a ledger which closed or moved.
    /// </summary>
    public IReadOnlyDictionary<LedgerId, long> ThrottledWrites => _throttledWrites;

    /// <summary>Counts a journal write for <paramref name="ledger"/>, whose primary is this node, answered <c>Throttled</c>.</summary>
    /// <remarks>
    /// A write counted while <see cref="Observe"/> ends its ledger's count starts the count again; the next evaluation ends
    /// it, so the series stay bounded by the ledgers the node is primary of.
    /// </remarks>
    public void CountThrottledWrite(LedgerId ledger) => _ = _throttledWrites.AddOrUpdate(ledger, 1, static (_, count) => count + 1);

    /// <summary>Records whether the node itself is under disk pressure, as its last measurement found.</summary>
    public void SetUnderPressure(bool underPressure) => Volatile.Write(ref _underPressure, underPressure);

    /// <summary>
    /// Sets the predicate that reports whether the node's object-store request budget is currently exhausted, so primary
    /// admission for object-store ledgers answers <c>Throttled</c> (orleans.md §5.14).
    /// </summary>
    public void SetObjectStoreThrottled(Func<bool>? isThrottled) => Volatile.Write(ref _isObjectStoreThrottled, isThrottled);

    /// <summary>Records a pressure report from <paramref name="node"/>, a replica node of a ledger this node is primary of.</summary>
    public void Report(NodeId node)
    {
        var now = _time.GetTimestamp();
        lock (_lock)
        {
            _reports = WithoutExpired(_reports, now).SetItem(node, now);
        }
    }

    /// <summary>
    /// Returns whether <paramref name="ledger"/>'s primary, this node, is throttled: for a ledger on nodes, the node is
    /// under disk pressure or a replica node's report lasts (OR-I10); for a ledger in the object store, disk pressure
    /// ignores it and only an exhausted object-store request budget throttles it (orleans.md §5.14).
    /// </summary>
    public bool IsThrottled(LedgerMetadata ledger)
    {
        if (ledger.Location.Value is LedgerLocation.ObjectStore)
        {
            return Volatile.Read(ref _isObjectStoreThrottled)?.Invoke() ?? false;
        }

        var reports = Volatile.Read(ref _reports);
        var underPressure = UnderPressure;
        var now = reports.IsEmpty ? 0 : _time.GetTimestamp();
        if (Throttles(ledger.Primary.Node, ledger, underPressure, reports, now))
        {
            return true;
        }

        foreach (var replica in ledger.Replicas)
        {
            if (Throttles(replica, ledger, underPressure, reports, now))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates the throttle of each open ledger in <paramref name="map"/> whose primary is <paramref name="node"/>, this
    /// node, with the nodes which cause it, and logs each throttle which started or ended since the last evaluation.
    /// </summary>
    public void Observe(NodeId node, LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var now = _time.GetTimestamp();
        ImmutableDictionary<NodeId, long> reports;
        lock (_lock)
        {
            reports = _reports = WithoutExpired(_reports, now);
        }

        var underPressure = UnderPressure;
        var before = ThrottleCauses;
        var throttleCauses = ImmutableDictionary.CreateBuilder<LedgerId, ImmutableArray<NodeId>>();
        HashSet<LedgerId>? openObjectStorePrimaries = null;
        foreach (var ledger in map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == node))
        {
            // Disk-pressure causes apply only to Nodes-backed ledgers (orleans.md §5.14), while open ObjectStore primaries
            // are tracked separately so _throttledWrites retains their request-budget throttle counts until close.
            if (ledger.Location.Value is not LedgerLocation.Nodes)
            {
                _ = (openObjectStorePrimaries ??= []).Add(ledger.Id);
                continue;
            }

            ImmutableArray<NodeId> causes = [.. ledger.Replicas.Prepend(node).Distinct().Where(candidate => Throttles(candidate, ledger, underPressure, reports, now))];
            throttleCauses[ledger.Id] = causes;
            var was = before.TryGetValue(ledger.Id, out var earlier) && !earlier.IsEmpty;
            if (!was && !causes.IsEmpty)
            {
                LogThrottled(_logger, ledger.Id, node, string.Join(", ", causes));
            }
            else if (was && causes.IsEmpty)
            {
                LogUnthrottled(_logger, ledger.Id, node);
            }
        }

        foreach (var (ledger, causes) in before)
        {
            if (!causes.IsEmpty && !throttleCauses.ContainsKey(ledger))
            {
                LogUnthrottled(_logger, ledger, node);
            }
        }

        var current = throttleCauses.ToImmutable();
        Volatile.Write(ref _throttleCauses, current);
        foreach (var count in _throttledWrites)
        {
            if (!current.ContainsKey(count.Key) && (openObjectStorePrimaries is null || !openObjectStorePrimaries.Contains(count.Key)))
            {
                // Only the count read: one a write raised since then stays, for the next evaluation.
                _ = _throttledWrites.TryRemove(count);
            }
        }
    }

    /// <summary>
    /// The one throttle predicate (OR-I10): whether <paramref name="candidate"/> throttles <paramref name="ledger"/>'s
    /// primary. The primary's own node does while it is under disk pressure, whether or not it is among the replicas; every
    /// other node does while its last report lasts. A node never reports to itself.
    /// </summary>
    private bool Throttles(NodeId candidate, LedgerMetadata ledger, bool underPressure, ImmutableDictionary<NodeId, long> reports, long now) =>
        candidate == ledger.Primary.Node
            ? underPressure
            : reports.TryGetValue(candidate, out var reportedAt) && Lasts(reportedAt, now);

    private bool Lasts(long reportedAt, long now) => _time.GetElapsedTime(reportedAt, now) < _lifetime;

    private ImmutableDictionary<NodeId, long> WithoutExpired(ImmutableDictionary<NodeId, long> reports, long now) =>
        reports.RemoveRange(reports.Where(report => !Lasts(report.Value, now)).Select(static report => report.Key));

    [LoggerMessage(EventId = 6204, Level = LogLevel.Warning, Message = "Ledger {Ledger}'s primary on node {Node} is throttled: nodes {Causes} are under disk pressure.")]
    private static partial void LogThrottled(ILogger logger, LedgerId ledger, NodeId node, string causes);

    [LoggerMessage(EventId = 6205, Level = LogLevel.Information, Message = "Ledger {Ledger}'s primary on node {Node} is no longer throttled.")]
    private static partial void LogUnthrottled(ILogger logger, LedgerId ledger, NodeId node);
}
