using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's archive-log drains (orleans.md §5.6, §5.9, §8): its share of the cluster's request budget, which each report
/// reply reassigns; the keys of the segments it has yet to drain, which it reports; its drain worker, which it builds over
/// the silo's archive gate; and the drains blocked at a key no retry makes succeed, which it counts in
/// <c>orleans.durable.storage.archive.logs.drain.blocked</c>, logs as a critical event when one starts, and retries only every
/// <see cref="BlockedRetryInterval"/>.
/// </summary>
/// <remarks>
/// <para>
/// The node counts the keys of the ledgers it owes a drain, its controller's <see cref="NodeController.DrainsDue"/>: the
/// node which drains a ledger counts it, the ledger's first replica, a second drainer, or a restarted node alike. A
/// segment's key count is its writer's (<see cref="OnSegmentEnded"/>, which the node's writers call) when this process wrote
/// it, or the drain worker's once its scan learned it; a ledger owed whose undrained segments no count covers counts as one
/// segment of <see cref="UncountedSegmentKeys"/> keys until a scan learns it: the running average of the counts the node
/// learned, <see cref="DefaultSegmentKeys"/> before the first, and at most <c>MaxSegmentKeys</c>. A ledger whose last
/// drain answered <c>Waiting</c> counts nothing, since its keys wait for an older ledger's drop. A segment below the
/// ledger's drained watermark, or of a ledger whose drain completed or which dropped, is never counted again.
/// </para>
/// <para>
/// Its node controller calls it on one exclusive scheduler; the writers' sink and the worker's observer only enqueue, and
/// the gauge reads only the blocked drains' snapshot, which is replaced whole. The gauge is an instrument of the runtime's
/// meter, so it ends with the runtime.
/// </para>
/// </remarks>
internal sealed partial class ArchiveLogNodeDrains
{
    /// <summary>The name of the gauge of the keys which block the node's drains.</summary>
    public const string BlockedName = "orleans.durable.storage.archive.logs.drain.blocked";

    /// <summary>The keys a segment no count covers counts as before the node learns any count, at most <c>MaxSegmentKeys</c>.</summary>
    public const int DefaultSegmentKeys = 4096;

    // The weight of each learned count in the running average: about the last 16 segments' counts carry it.
    private const double AverageWeight = 1.0 / 16;

    /// <summary>The time a blocked ledger waits between its drain's attempts: about a minute, so a block costs no rescans.</summary>
    public static readonly TimeSpan BlockedRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>The wait after a ledger's first failed drain which no block explains, doubling with each failure after it.</summary>
    public static readonly TimeSpan FirstFailureBackoff = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between a failing ledger's drains.</summary>
    public static readonly TimeSpan LongestFailureBackoff = TimeSpan.FromMinutes(1);

    private readonly NodeId _node;
    private readonly int _maxSegmentKeys;
    private readonly ArchiveLogOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ArchiveLogNodeShare _share;
    private readonly KeyValuePair<string, object?> _nodeTag;
    private readonly ConcurrentQueue<LedgerSegmentKeys> _learned = new();
    private readonly Dictionary<LedgerId, Backlog> _backlogs = [];
    private readonly Dictionary<LedgerId, long> _progress = [];
    private readonly Dictionary<LedgerId, DateTimeOffset> _blockedAt = [];
    private readonly Dictionary<LedgerId, Failing> _failing = [];

    // Each ledger whose last drain answered Waiting, with the older ledger it waits for.
    private readonly Dictionary<LedgerId, LedgerId> _waiting = [];
    private double? _averageSegmentKeys;
    private ImmutableDictionary<LedgerId, ArchiveLogDrainBlockedException> _blocked = [];
    private (ArchiveLogGate Gate, ArchiveLogDrainWorker Worker, LedgerMapView Map)? _opened;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogNodeDrains"/> class, with no share until the controller's first reply.</summary>
    /// <param name="node">The node.</param>
    /// <param name="budget">The cluster's budget, <c>ArchiveLogRequestsPerSecond</c>.</param>
    /// <param name="nodes">
    /// The active nodes the node's current registry view lists, itself among them, which its floor divides the budget by,
    /// read each time the floor applies (orleans.md §5.6); it must be cheap and safe from any thread.
    /// </param>
    /// <param name="maxSegmentKeys">The writers' <c>MaxSegmentKeys</c>, the most a segment no count covers counts as.</param>
    /// <param name="controlInterval">The control interval, at which the node reports.</param>
    /// <param name="options">The drain worker's options.</param>
    /// <param name="timeProvider">The clock of the replies and the blocked drains' retries.</param>
    /// <param name="meter">The runtime's meter, on which the blocked drains' gauge is published.</param>
    /// <param name="logger">Logs each drain which starts to block.</param>
    public ArchiveLogNodeDrains(NodeId node, double budget, Func<int> nodes, int maxSegmentKeys, TimeSpan controlInterval, ArchiveLogOptions options, TimeProvider timeProvider, Meter meter, ILogger logger)
    {
        ArchiveLogRequestShare.CheckBudget(budget, nameof(budget));
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentKeys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _node = node;
        _maxSegmentKeys = maxSegmentKeys;
        _options = options;
        _time = timeProvider;
        _logger = logger;
        // A node whose registry lists no active node, its own retired, still divides the budget by itself.
        _share = new ArchiveLogNodeShare(() => ArchiveLogRequestShare.Floor(budget, Math.Max(1, nodes())), controlInterval);
        _nodeTag = new("node", node.ToString());
        _ = meter.CreateObservableGauge(BlockedName, ObserveBlocked, "{key}", "The keys which block the node's archive-log drains, by ledger and cause.");
    }

    /// <summary>
    /// Gets the requests a second the node's drains make at most now, or <see langword="null"/> before the controller's
    /// first share: its latest share, or at most its floor once replies stopped assigning one (OR-I22).
    /// </summary>
    public double? RequestsPerSecond => _share.RateAt(_time.GetUtcNow());

    /// <summary>
    /// Gets the keys a segment no count covers counts as (orleans.md §5.6): the running average of the counts the node
    /// learned from its writers and scans, each moving it a sixteenth of the way to the count, or
    /// <see cref="DefaultSegmentKeys"/> before the first; rounded, at most <c>MaxSegmentKeys</c>, and at least 1.
    /// </summary>
    public int UncountedSegmentKeys => (int)Math.Clamp(Math.Round(_averageSegmentKeys ?? DefaultSegmentKeys), 1, _maxSegmentKeys);

    /// <summary>Gets the drains blocked now, each by the exception its last attempt threw.</summary>
    public ImmutableDictionary<LedgerId, ArchiveLogDrainBlockedException> Blocked => Volatile.Read(ref _blocked);

    /// <summary>Gets the time a blocked ledger waits between its drain's attempts: <see cref="BlockedRetryInterval"/>, shorter in tests.</summary>
    internal TimeSpan RetryBlockedAfter { get; init; } = BlockedRetryInterval;

    /// <summary>Gets the node's drain worker, once the gate passed; for tests.</summary>
    internal ArchiveLogDrainWorker? Worker => _opened?.Worker;

    /// <summary>
    /// Records a segment one of the node's writers ended, with the keys it defined (ledgers.md §2.4): the sink the node's
    /// writer registry gives its writers. Called under a writer's lock, so it only enqueues.
    /// </summary>
    public void OnSegmentEnded(LedgerSegmentKeys segment) => _learned.Enqueue(segment);

    /// <summary>
    /// Builds the node's drain worker over <paramref name="gate"/>, at the node's share, on the first round once the silo's
    /// archive gate passed; later calls name the same gate.
    /// </summary>
    /// <exception cref="ArgumentException">Another gate than the first.</exception>
    public void Open(ArchiveLogGate gate, LedgerMapView map, LedgerReader reader)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (_opened is { } open)
        {
            if (!ReferenceEquals(open.Gate, gate))
            {
                throw new ArgumentException("A node's drains run over the one archive gate its silo passed.", nameof(gate));
            }

            return;
        }

        // The pacer reads the share before each request, so its deadline applies whether or not a report is in flight;
        // no drain runs before the first share (Drains).
        var worker = new ArchiveLogDrainWorker(gate, map, reader, _options, _time)
        {
            OnSegmentScanned = _learned.Enqueue,
            RequestsPerSecondSource = () => RequestsPerSecond ?? throw new UnreachableException("A node drains only once a reply gave it a share, which it never loses."),
        };

        _opened = (gate, worker, map);
    }

    /// <summary>
    /// Takes the share a report reply assigns, if any, as the drains' rate, restarting its deadline; a reply which assigns
    /// none keeps the last and its deadline.
    /// </summary>
    public void Observe(NodeReportReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        _share.Observe(reply.ArchiveLogShare, _time.GetUtcNow());
    }

    /// <summary>
    /// Returns the node's undrained keys over the ledgers it owes a drain, <paramref name="owed"/>, after it takes the
    /// segments its writers ended and its scans learned, unblocks every ledger it no longer owes, and forgets every ledger
    /// <paramref name="map"/> no longer lists; a blocked ledger, and one whose last drain answered <c>Waiting</c>, count
    /// nothing; and the rate its drains run at now.
    /// </summary>
    public (long UndrainedKeys, double? RequestsPerSecond) Report(LedgerMapValue map, IEnumerable<LedgerMetadata> owed)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(owed);
        Learn();
        var owedLedgers = owed as IReadOnlyCollection<LedgerMetadata> ?? [.. owed];
        var owedIds = owedLedgers.Select(static ledger => ledger.Id).ToHashSet();
        foreach (var ledger in Blocked.Keys.Concat(_blockedAt.Keys).Where(ledger => !owedIds.Contains(ledger)).Distinct().ToList())
        {
            // The node no longer owes the ledger's drain, so its block is no longer its to report.
            Unblock(ledger);
        }

        ForgetUnowed(owedIds);

        foreach (var ledger in _backlogs.Keys.Concat(Blocked.Keys).Concat(_blockedAt.Keys).Concat(_progress.Keys).Distinct().Where(ledger => map.Get(ledger) is null).ToList())
        {
            Forget(ledger);
        }

        var (keys, uncounted) = (0L, UncountedSegmentKeys);
        foreach (var ledger in owedLedgers)
        {
            if (Blocked.ContainsKey(ledger.Id) || _waiting.ContainsKey(ledger.Id))
            {
                // A blocked ledger's keys wait for an operator, and a waiting one's for an older ledger's drop, so they take
                // no share.
                continue;
            }

            keys = checked(keys + (_backlogs.TryGetValue(ledger.Id, out var backlog) ? backlog.Undrained(uncounted) : uncounted));
        }

        return (keys, RequestsPerSecond);
    }

    /// <summary>Returns the slot below which every segment of <paramref name="ledger"/> is complete, as this node's drains learned it.</summary>
    public Slot DrainedBelow(LedgerId ledger) => _backlogs.TryGetValue(ledger, out var backlog) ? backlog.DrainedBelow : Slot.Zero;

    /// <summary>
    /// Returns whether the node drains <paramref name="ledger"/> now: once the gate passed and a reply assigned a share; for
    /// a blocked ledger, once <see cref="BlockedRetryInterval"/> passed since its last attempt; for a ledger whose drain
    /// failed otherwise, once its backoff passed since its last failure; and for a ledger whose last drain answered
    /// <c>Waiting</c>, once the node's map view no longer lists the older ledger it waits for, which a drain would only
    /// find listed again.
    /// </summary>
    public bool Drains(LedgerId ledger)
    {
        if (_opened is not { } opened || _share.Assigned is null)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        return !(_blockedAt.TryGetValue(ledger, out var at) && now - at < RetryBlockedAfter)
            && !(_failing.TryGetValue(ledger, out var failing) && now - failing.At < failing.Backoff)
            && !(_waiting.TryGetValue(ledger, out var older) && opened.Map.Current.Get(older) is not null);
    }

    /// <summary>Returns the older overlapping ledger whose listing <paramref name="ledger"/>'s last drain answered it waits for, if any.</summary>
    public LedgerId? WaitingFor(LedgerId ledger) => _waiting.TryGetValue(ledger, out var older) ? older : null;

    /// <summary>
    /// Returns whether the worker's progress on <paramref name="ledger"/>, its keys held and segments completed, grew since
    /// the last call, which the node reports as its closed ledger's drain progress (orleans.md §5.6).
    /// </summary>
    public bool Progressed(LedgerId ledger)
    {
        if (_opened is not { } open)
        {
            return false;
        }

        var progress = open.Worker.Progress(ledger);
        var before = _progress.GetValueOrDefault(ledger);
        _progress[ledger] = progress;
        return progress > before;
    }

    /// <summary>
    /// Drains <paramref name="ledger"/> below <paramref name="below"/> once (archive-logs.md §2.3), and records what the drain
    /// learned: its watermark, its completion, whose drop it submits through <paramref name="commit"/> (§2.4), or its drop.
    /// A drain which throws <see cref="ArchiveLogDrainBlockedException"/> it records as blocked, logging a critical event
    /// when the drain starts to block there, before rethrowing it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The silo's archive gate has not passed: <see cref="Open"/> was not called.</exception>
    public async Task<ArchiveLogDrainOutcome> DrainAsync(
        LedgerMetadata ledger,
        Slot below,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var (_, worker, map) = _opened ?? throw new InvalidOperationException("A node drains archive logs only once its silo's archive gate passed.");
        ArchiveLogDrainOutcome outcome;
        try
        {
            outcome = await worker.DrainAsync(ledger, below, cancellationToken).ConfigureAwait(true);
        }
        catch (ArchiveLogDrainBlockedException blocked)
        {
            _ = _waiting.Remove(ledger.Id);
            Block(ledger, blocked, map.Current);
            throw;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is not OutOfMemoryException)
        {
            _ = _waiting.Remove(ledger.Id);
            Fail(ledger.Id, failure);
            throw;
        }

        // A drain which answered made progress past any failure before it, so the ledger's backoff starts over; the ledger
        // waits only while its last drain answered Waiting.
        _ = _failing.Remove(ledger.Id);
        _ = _waiting.Remove(ledger.Id);
        switch (outcome.Value)
        {
            case ArchiveLogDrainOutcome.Drained drained:
                Advance(ledger.Id, drained.Below);
                break;
            case ArchiveLogDrainOutcome.OpenSegment open:
                Advance(ledger.Id, open.From);
                break;
            case ArchiveLogDrainOutcome.Complete:
                BacklogOf(ledger.Id).Complete();
                Unblock(ledger.Id);
                await SubmitDropAsync(worker, ledger.Id, commit, cancellationToken).ConfigureAwait(true);
                break;
            case ArchiveLogDrainOutcome.Dropped:
                // The ledger counts nothing while the view still lists it; the report forgets it once the view does not.
                BacklogOf(ledger.Id).Complete();
                _ = _progress.Remove(ledger.Id);
                Unblock(ledger.Id);
                break;
            case ArchiveLogDrainOutcome.Waiting waiting:
                _waiting[ledger.Id] = waiting.Older;
                break;
            case null:
            default:
                throw new InvalidOperationException("conventions.md §15: a drain answers a union which holds a case.");
        }

        return outcome;
    }

    /// <summary>Records that <paramref name="ledger"/>'s segments are complete below <paramref name="below"/>, which ends a block below it.</summary>
    private void Advance(LedgerId ledger, Slot below)
    {
        BacklogOf(ledger).Advance(below);
        if (Blocked.TryGetValue(ledger, out var block) && block.Segment.Start < below)
        {
            Unblock(ledger);
        }
    }

    /// <summary>
    /// Submits the drop of <paramref name="ledger"/>, whose drain answered <c>Complete</c>; a submission which fails is
    /// recorded as the drain's failure, logged and backed off as any other, before it is rethrown.
    /// </summary>
    private async Task SubmitDropAsync(ArchiveLogDrainWorker worker, LedgerId ledger, Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit, CancellationToken cancellationToken)
    {
        try
        {
            _ = await worker.SubmitDropAsync(ledger, commit, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is not OutOfMemoryException)
        {
            Fail(ledger, failure);
            throw;
        }
    }

    /// <summary>
    /// Forgets the backoff and the wait of every ledger the node no longer owes, <paramref name="owed"/>: a drain it owes
    /// again starts with no backoff, and waits only once a drain of it answers <c>Waiting</c> again.
    /// </summary>
    private void ForgetUnowed(HashSet<LedgerId> owed)
    {
        foreach (var ledger in _failing.Keys.Where(ledger => !owed.Contains(ledger)).ToList())
        {
            _ = _failing.Remove(ledger);
        }

        foreach (var ledger in _waiting.Keys.Where(ledger => !owed.Contains(ledger)).ToList())
        {
            _ = _waiting.Remove(ledger);
        }
    }

    /// <summary>Returns the gauge's tag of <paramref name="cause"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The cause is no cause a drain blocks with.</exception>
    internal static string Cause(ArchiveLogDrainBlockedCause cause) => cause switch
    {
        ArchiveLogDrainBlockedCause.Corrupt => "corrupt",
        ArchiveLogDrainBlockedCause.Damaged => "damaged",
        ArchiveLogDrainBlockedCause.Refused => "refused",
        ArchiveLogDrainBlockedCause.CorruptMarker => "corrupt_marker",
        ArchiveLogDrainBlockedCause.Unspecified => throw new ArgumentOutOfRangeException(nameof(cause), cause, "No drain blocks without a cause."),
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "No drain blocks with an undefined cause."),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s drain of ledger {Ledger} failed; it drains the ledger again in {Backoff}, backing off to a minute while its drains fail.")]
    private static partial void LogDrainFailed(ILogger logger, NodeId node, LedgerId ledger, TimeSpan backoff, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s drain of ledger {Ledger} failed again, {Failures} times since it last answered; it drains the ledger again in {Backoff}.")]
    private static partial void LogDrainFailedAgain(ILogger logger, NodeId node, LedgerId ledger, int failures, TimeSpan backoff, Exception exception);

    [LoggerMessage(EventId = 6244, Level = LogLevel.Critical, Message = "Node {Node}'s drain of ledger {Ledger} is blocked at its segment from slot {Start} ({Cause}) by key {Key}, log '{Log}', which holds the drops of ledgers {Drops}; follow the blocked archive-log drain procedure, after which the drain resumes within a minute.")]
    private static partial void LogBlocked(ILogger logger, NodeId node, LedgerId ledger, Slot start, string cause, string key, string log, string drops, Exception exception);

    private static bool SameKey(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right) =>
        left is { } l ? right is { } r && l.Span.SequenceEqual(r.Span) : right is null;

    /// <summary>
    /// Takes the segments the writers ended and the scans learned, each counted unless already drained, and each count into
    /// the running average of the segments' keys.
    /// </summary>
    private void Learn()
    {
        while (_learned.TryDequeue(out var segment))
        {
            BacklogOf(segment.Segment.Ledger).Learn(segment);
            _averageSegmentKeys = _averageSegmentKeys is { } average ? average + AverageWeight * (segment.KeyCount - average) : segment.KeyCount;
        }
    }

    /// <summary>
    /// Records a drain blocked by <paramref name="blocked"/>, and logs the critical event when the drain was not blocked at
    /// the same segment, cause, and key before: the ledgers whose drops it holds are the ledger's and every listed ledger
    /// above it which overlaps it.
    /// </summary>
    private void Block(LedgerMetadata ledger, ArchiveLogDrainBlockedException blocked, LedgerMapValue map)
    {
        _blockedAt[ledger.Id] = _time.GetUtcNow();
        if (Blocked.TryGetValue(ledger.Id, out var before) && before.Segment == blocked.Segment && before.Cause == blocked.Cause && SameKey(before.Key, blocked.Key))
        {
            return;
        }

        Volatile.Write(ref _blocked, Blocked.SetItem(ledger.Id, blocked));
        var drops = map.Ledgers.Where(other => other.Id >= ledger.Id && other.Range.Overlaps(ledger.Range)).Select(static other => other.Id);
        LogBlocked(
            _logger,
            _node,
            ledger.Id,
            blocked.Segment.Start,
            Cause(blocked.Cause),
            blocked.Key is { } key ? Convert.ToHexStringLower(key.Span) : "none",
            blocked.Log?.ToString() ?? "none",
            string.Join(", ", drops),
            blocked);
    }

    /// <summary>
    /// Records a drain of <paramref name="ledger"/> which failed without a block, backing off its next drain by
    /// <see cref="Backoff.Delay"/> from <see cref="FirstFailureBackoff"/>, doubling to <see cref="LongestFailureBackoff"/>,
    /// each wait jittered up to twice its step: the first failure is logged with
    /// its exception, and each later one, at most one a backoff window, with the count since the drain last answered.
    /// </summary>
    internal void Fail(LedgerId ledger, Exception failure)
    {
        var failing = _failing.TryGetValue(ledger, out var before)
            ? new Failing(_time.GetUtcNow(), Backoff.Delay(before.Failures, FirstFailureBackoff, LongestFailureBackoff, Random.Shared.NextDouble()), before.Failures + 1)
            : new Failing(_time.GetUtcNow(), Backoff.Delay(0, FirstFailureBackoff, LongestFailureBackoff, Random.Shared.NextDouble()), 1);
        _failing[ledger] = failing;
        if (failing.Failures == 1)
        {
            LogDrainFailed(_logger, _node, ledger, failing.Backoff, failure);
        }
        else
        {
            LogDrainFailedAgain(_logger, _node, ledger, failing.Failures, failing.Backoff, failure);
        }
    }

    private void Unblock(LedgerId ledger)
    {
        _ = _blockedAt.Remove(ledger);
        if (Blocked.ContainsKey(ledger))
        {
            Volatile.Write(ref _blocked, Blocked.Remove(ledger));
        }
    }

    private void Forget(LedgerId ledger)
    {
        _ = _failing.Remove(ledger);
        _ = _waiting.Remove(ledger);
        _ = _backlogs.Remove(ledger);
        _ = _progress.Remove(ledger);
        Unblock(ledger);
    }

    private Backlog BacklogOf(LedgerId ledger)
    {
        if (!_backlogs.TryGetValue(ledger, out var backlog))
        {
            _backlogs[ledger] = backlog = new Backlog();
        }

        return backlog;
    }

    private IEnumerable<Measurement<int>> ObserveBlocked() =>
        Blocked.Select(blocked => new Measurement<int>(1, _nodeTag, new("ledger", blocked.Key.ToString()), new("cause", Cause(blocked.Value.Cause))));

    /// <summary>A ledger whose drains fail without a block: its last failure, its backoff from it, and its failures since it last answered.</summary>
    private readonly record struct Failing(DateTimeOffset At, TimeSpan Backoff, int Failures);

    /// <summary>
    /// A ledger's segments this node has yet to drain, by start, with the keys each defined; the watermark below which every
    /// segment is complete; and whether the whole ledger is, while its drop is pending.
    /// </summary>
    private sealed class Backlog
    {
        private readonly SortedDictionary<Slot, int> _segments = [];
        private bool _complete;

        public Slot DrainedBelow { get; private set; }

        /// <summary>Counts <paramref name="segment"/> unless it lies below the watermark or the ledger is complete.</summary>
        public void Learn(LedgerSegmentKeys segment)
        {
            if (!_complete && segment.Segment.Start >= DrainedBelow)
            {
                _segments[segment.Segment.Start] = segment.KeyCount;
            }
        }

        public void Advance(Slot below)
        {
            if (below <= DrainedBelow)
            {
                return;
            }

            DrainedBelow = below;
            foreach (var start in _segments.Keys.TakeWhile(start => start < below).ToList())
            {
                _ = _segments.Remove(start);
            }
        }

        public void Complete()
        {
            _complete = true;
            _segments.Clear();
        }

        /// <summary>
        /// Returns the undrained keys the counts cover, and one segment of <paramref name="uncountedSegmentKeys"/> for the
        /// segments between the watermark and the lowest counted one, which no count covers, or when no segment is counted.
        /// </summary>
        public long Undrained(int uncountedSegmentKeys)
        {
            if (_complete)
            {
                return 0;
            }

            Debug.Assert(_segments.Keys.All(start => start >= DrainedBelow), "No counted segment lies below the watermark.");
            if (_segments.Count == 0)
            {
                return uncountedSegmentKeys;
            }

            // A takeover's or a restarted node's writer counts only the segments it ended, above those it did not write.
            var known = _segments.Values.Sum(static keys => (long)keys);
            return _segments.Keys.First() > DrainedBelow ? known + uncountedSegmentKeys : known;
        }
    }
}
