using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's control work (orleans.md §5.6): it executes the transitions the controller sends it, drains the ledgers it
/// owes the archive, and runs the compactions it is assigned, submitting every change to the controller's committers. Its
/// host, the silo's Ledgers runtime, calls it from its control loop and on the node control grain's calls, which relay the
/// controller's.
/// </summary>
/// <remarks>
/// <para>
/// Every change goes through a commit delegate, which the host binds to the controller's <c>CommitLedgerMapAsync</c> or
/// <c>CommitManifestAsync</c>, so the node writes no control register itself (OR-I12); a transition's change is submitted
/// only while the node's registry entry is active, and answered <c>Refused</c> otherwise (OR-I3). Each report reply names
/// the newest map epoch the controller knows durable, and the node's view reaches it before the node's next work (OR-I19).
/// </para>
/// <para>
/// The host calls every member on one exclusive scheduler, and the members continue on it after each await, so their
/// state changes only in synchronous segments, one at a time, and calls interleave at awaits; no invariant of it spans an
/// await. A compaction of a partition already running is joined, not run again. Each drain it starts runs on beside the
/// call which started it, and <see cref="DrainsEnded"/>, which a stop awaits, may be read from any thread.
/// </para>
/// </remarks>
internal sealed partial class NodeController
{
    private readonly int _generation;

    // The number of the process's last report, which only grows (NodeReport.Sequence).
    private long _reportSequence;
    private readonly LedgerMapView _map;
    private readonly TransitionExecutor _executor;
    private readonly LedgerReader _reader;
    private readonly LedgerWriterRegistry _registry;
    private readonly Func<bool> _active;
    private readonly Func<IReadOnlyDictionary<NodeId, int>> _dead;
    private readonly ILogger _logger;
    private readonly Histogram<double>? _exposure;
    private readonly (DrainWorker Drains, CompactionWorker Compactions, ManifestView Manifest)? _lsm;
    private readonly ArchiveLogNodeDrains? _logs;
    private readonly ArchiveLogNodeShare? _objectStoreShare;
    private readonly TimeProvider _time;
    private readonly int _drainConcurrency;
    private readonly LedgerLoadSampler _load;
    private readonly Dictionary<LedgerId, DateTimeOffset> _drainProgress = [];
    private readonly Dictionary<LedgerId, Slot> _drainedBelow = [];
    private readonly Dictionary<KeyRange, Task<CompactionReply>> _compacting = [];

    // The failure-triggered drain of each open ledger the node is primary of whose live replicas fell to Q (orleans.md
    // §5.6, OR-I23); the deaths the node knew as the execution which created each writer's lifetime began, which make no
    // drain due; the intervals each exposed ledger has waited without a start; when the oldest unfinished failure-triggered
    // drain was found due, in ticks, which the gauge reads from any thread; and the backoff of each exposed ledger whose
    // drain made no progress.
    private readonly Dictionary<LedgerId, FailureDrain> _failureDrains = [];
    private readonly ConcurrentDictionary<SequencerInstanceId, ImmutableHashSet<Death>> _openedWith = new();
    private readonly Dictionary<LedgerId, int> _exposedWaits = [];
    private long _oldestExposure;
    private readonly Dictionary<LedgerId, Retry> _exposedRetries = [];

    private readonly Func<bool>? _objectLedgersReady;
    private readonly Func<LedgerId, KeyRange, TimeSpan, CancellationToken, Task<LedgerPrepareReleaseOutcome>>? _prepareRelease;
    private readonly Dictionary<LedgerId, Slot> _releasedLedgers = [];
    private readonly Dictionary<LedgerId, Task<PrepareReleaseReply>> _prepareInFlight = [];

    // The ledgers whose drains are in flight, under _drainSync, since a drain ends on whichever thread its last await
    // resumes on; the wait for the last of them to end; and when each ledger's drain last started, by number.
    private readonly Lock _drainSync = new();
    private readonly HashSet<LedgerId> _draining = [];
    private readonly Dictionary<LedgerId, long> _drainStarts = [];
    private TaskCompletionSource? _drainsEnded;
    private long _drainsStarted;
    private ImmutableArray<LedgerId> _drainToo = [];

    /// <summary>Initializes a new instance of the <see cref="NodeController"/> class.</summary>
    /// <param name="node">The node.</param>
    /// <param name="generation">The generation of the node's silo, which its reports carry.</param>
    /// <param name="map">The process's map view, which its registry follows.</param>
    /// <param name="registry">The process's writer registry.</param>
    /// <param name="reader">The process's ledger reader, through which drains learn open ledgers' contiguous tails.</param>
    /// <param name="executor">The process's transition executor.</param>
    /// <param name="active">Answers whether the node's registry entry is active: a retired node executes no transition (OR-I3).</param>
    /// <param name="dead">Answers the registered nodes membership shows dead (orleans.md §5.7), each with the generation of the silo the registry lists for it, which the failure-triggered drains count out of each ledger's live replicas.</param>
    /// <param name="archive">The cluster's archive store: the LSM, with the node's drain worker, compaction worker, and manifest view; or archive logs, with the node's archive-log drains.</param>
    /// <param name="timeProvider">Times the progress of closed ledgers' drains and the samples of their load.</param>
    /// <param name="loadWindow">The window over which the node measures each primary ledger's load, <c>LoadWindow</c>.</param>
    /// <param name="drainConcurrency">The drains in flight at once, each scanning its ledger and holding its rows within the drain worker's memory.</param>
    /// <param name="logger">Logs the failure-triggered drains' events.</param>
    /// <param name="meter">The runtime's meter, on which the node records each failure-triggered drain's exposure.</param>
    /// <param name="objectStoreShare">The node's object-store request budget share, or <see langword="null"/> when object-store ledgers are not budgeted.</param>
    /// <param name="objectLedgersReady">
    /// Answers whether the node's object-ledger gate has passed (OL-I1), or <see langword="null"/> when not gated: while
    /// <see langword="false"/>, transitions opening or closing an object-store ledger are declined.
    /// </param>
    /// <param name="prepareRelease">
    /// Optional cooperative drain callback (wired by the host to <c>JournalStore.PrepareReleaseAsync</c>) that drains
    /// staged and in-flight journal writes before stopping the ledger's writer; when <see langword="null"/>,
    /// <see cref="PrepareReleaseAsync"/> drains the writer directly.
    /// </param>
    public NodeController(
        NodeId node,
        int generation,
        LedgerMapView map,
        LedgerWriterRegistry registry,
        LedgerReader reader,
        TransitionExecutor executor,
        Func<bool> active,
        Func<IReadOnlyDictionary<NodeId, int>> dead,
        NodeArchive archive,
        TimeProvider timeProvider,
        TimeSpan loadWindow,
        int drainConcurrency = 1,
        ILogger? logger = null,
        Meter? meter = null,
        ArchiveLogNodeShare? objectStoreShare = null,
        Func<bool>? objectLedgersReady = null,
        Func<LedgerId, KeyRange, TimeSpan, CancellationToken, Task<LedgerPrepareReleaseOutcome>>? prepareRelease = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(dead);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(drainConcurrency);
        switch (archive.Value)
        {
            case NodeArchive.Lsm lsm:
                _lsm = (lsm.Drains, lsm.Compactions, lsm.Manifest);
                break;
            case NodeArchive.Logs logs:
                _logs = logs.Drains;
                break;
            case null:
            default:
                throw new ArgumentException("conventions.md §15: a node's archive store is the LSM or archive logs.", nameof(archive));
        }

        Node = node;
        _generation = generation;
        _map = map;
        _registry = registry;
        _reader = reader;
        _executor = executor;
        _active = active;
        _dead = dead;
        _logger = logger ?? NullLogger.Instance;
        _exposure = meter?.CreateHistogram<double>("orleans.durable.storage.ledger.exposure", "s", "The time from a node first finding a replica's death leaves an open ledger it is primary of exactly Q live replicas to its failure-triggered drain reaching the marker.");
        _ = meter?.CreateObservableGauge("orleans.durable.storage.ledger.exposure.oldest", OldestExposure, "s", "The time since the node found its oldest unfinished failure-triggered drain due, 0 when none is unfinished.");
        _time = timeProvider;
        _drainConcurrency = drainConcurrency;
        _load = new LedgerLoadSampler(loadWindow, timeProvider);
        _objectStoreShare = objectStoreShare;
        _objectLedgersReady = objectLedgersReady;
        _prepareRelease = prepareRelease;
    }

    /// <summary>
    /// Gets the object-store requests a second the node is assigned at most now, or <see langword="null"/> when no budget is
    /// configured or before the controller's first share (orleans.md §5.14).
    /// </summary>
    internal double? ObjectStoreRequestsPerSecond => _objectStoreShare?.RateAt(_time.GetUtcNow());

    /// <summary>Gets the node.</summary>
    public NodeId Node { get; }

    /// <summary>
    /// Cooperatively drains in-flight writes and stops admitting new batches on the open ledger named by
    /// <paramref name="request"/> within its deadline (orleans.md §5.6, §5.7, OR-I24).
    /// </summary>
    /// <remarks>
    /// Cleanly drained open ledgers are remembered until the ledger closes in the map view and reported in
    /// <see cref="NodeReport.ReleasedLedgers"/>, so repeated calls (including after controller failover) return
    /// <see cref="PrepareReleaseReply.Released"/> idempotently. Concurrent calls for the same ledger join the in-flight
    /// drain. If another node concurrently seals the ledger, draining aborts immediately with
    /// <see cref="PrepareReleaseReply.Sealed"/>.
    /// </remarks>
    public Task<PrepareReleaseReply> PrepareReleaseAsync(PrepareReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var map = _map.Current;
        if (_releasedLedgers.TryGetValue(request.Ledger, out var releasedTail)
            && map.Get(request.Ledger) is { IsOpen: true } openReleased
            && openReleased.Primary.Node == Node
            && openReleased.Range == request.Range)
        {
            return Task.FromResult<PrepareReleaseReply>(new PrepareReleaseReply.Released(releasedTail));
        }

        if (_prepareInFlight.TryGetValue(request.Ledger, out var running))
        {
            return cancellationToken.CanBeCanceled ? running.WaitAsync(cancellationToken) : running;
        }

        if (map.Get(request.Ledger) is not { IsOpen: true } listed
            || listed.Primary.Node != Node
            || listed.Range != request.Range)
        {
            return Task.FromResult<PrepareReleaseReply>(PrepareReleaseReply.NotServed.Instance);
        }

        var writer = _registry.TryGet(request.Ledger);
        if (writer is null)
        {
            return Task.FromResult<PrepareReleaseReply>(
                _registry.Unserved.Contains(request.Ledger)
                    ? PrepareReleaseReply.Sealed.Instance
                    : PrepareReleaseReply.NotServed.Instance);
        }

        var task = RunPrepareReleaseAsync(request, writer, cancellationToken);
        if (!task.IsCompleted)
        {
            _prepareInFlight[request.Ledger] = task;
        }

        return task;

        async Task<PrepareReleaseReply> RunPrepareReleaseAsync(PrepareReleaseRequest req, LedgerWriter writer, CancellationToken token)
        {
            try
            {
                var outcome = _prepareRelease is not null
                    ? await _prepareRelease(req.Ledger, req.Range, req.Deadline, token).ConfigureAwait(true)
                    : await writer.PrepareReleaseAsync(req.Deadline, token).ConfigureAwait(true);
                return outcome.Value switch
                {
                    LedgerPrepareReleaseOutcome.Released released => RecordReleased(req.Ledger, released.ContiguousTail),
                    LedgerPrepareReleaseOutcome.Sealed => PrepareReleaseReply.Sealed.Instance,
                    LedgerPrepareReleaseOutcome.TimedOut => PrepareReleaseReply.TimedOut.Instance,
                    null => throw new UnreachableException("conventions.md §15: a prepare-release outcome holds a case."),
                    _ => throw new UnreachableException(),
                };
            }
            finally
            {
                _ = _prepareInFlight.Remove(req.Ledger);
            }
        }

        PrepareReleaseReply RecordReleased(LedgerId ledger, Slot tail)
        {
            _releasedLedgers[ledger] = tail;
            return new PrepareReleaseReply.Released(tail);
        }
    }

    /// <summary>
    /// Executes <paramref name="transition"/> and answers the committer's verdict; <see cref="TransitionReply.Declined"/>,
    /// with nothing done, when another node is its executor or this node's registry entry is retired. The executor reads
    /// the map at least as new as the one the controller chose from.
    /// </summary>
    /// <param name="transition">The transition the controller sent.</param>
    /// <param name="commit">Submits the change once to the controller's ledger map committer.</param>
    /// <param name="cancellationToken">As <see cref="TransitionExecutor.ExecuteAsync"/>'s.</param>
    /// <remarks>Any exception leaves the transition's outcome to the map, from which the controller chooses again.</remarks>
    public async Task<TransitionReply> ExecuteAsync(
        Transition transition,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (transition.Choice.Executor != Node || !_active())
        {
            return TransitionReply.Declined.Instance;
        }

        if (_objectLedgersReady is not null && !_objectLedgersReady())
        {
            // Fast path when opening directly in the object store; otherwise refresh the map view to the choice's epoch
            // so ObjectStore -> Nodes transitions whose closed ledger is newer than _map.Current decline cleanly (OL-I1).
            if (transition.Choice.Location.Value is LedgerLocation.ObjectStore)
            {
                return TransitionReply.Declined.Instance;
            }

            if (!transition.Closes.IsEmpty)
            {
                _ = await _map.RefreshAsync(transition.Choice.Epoch, cancellationToken).ConfigureAwait(true);
                if (TouchesObjectStore(transition))
                {
                    return TransitionReply.Declined.Instance;
                }
            }
        }

        // OR-I3: the node reads its own entry active before it submits, however long the recovery took.
        Task<LedgerMapCommitOutcome> CommitIfActive(LedgerMapChange change, CancellationToken token) =>
            _active() ? commit(change, token) : Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance);

        // The deaths the node knows as the execution begins are those each lifetime it creates opens with: none of them makes
        // a failure-triggered drain due, since every row the writer decides lies on its live replicas (orleans.md §5.6). The
        // record is keyed to the lifetime, so a writer served late, after an unknown outcome, keeps its own execution's. The
        // executor records it on its own thread once the registry holds the lifetime, before any view can name it.
        var openedWith = Deaths(_dead());
        void Created(SequencerInstanceId lifetime) => _openedWith[lifetime] = openedWith;
        var keyed = FailureDrainMutant != FailureDrainMutant.RecordsAtAnyExecutionsEnd;
        try
        {
            // Stays on the host's turn; members are single-threaded.
            return TransitionReply.Of(await _executor.ExecuteAsync(transition, CommitIfActive, cancellationToken, keyed ? Created : null).ConfigureAwait(true));
        }
        finally
        {
            if (!keyed)
            {
                // The mutant records, as any execution ends, its deaths for each served writer with no record.
                foreach (var ledger in OpenPrimaries(_map.Current).Where(ledger => _registry.TryGet(ledger.Id) is not null))
                {
                    _ = _openedWith.TryAdd(ledger.Primary.Sequencer, openedWith);
                }
            }
        }
    }

    private bool TouchesObjectStore(Transition transition)
    {
        if (transition.Choice.Location.Value is LedgerLocation.ObjectStore)
        {
            return true;
        }

        var map = _map.Current;
        foreach (var id in transition.Closes)
        {
            if (map.Get(id)?.Location.Value is LedgerLocation.ObjectStore)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the ledgers the node drains now (orleans.md §5.6): each closed ledger on nodes whose first replica it is, or
    /// which the controller named in <paramref name="drainToo"/>, unless its manifest view shows it complete; and each open ledger it
    /// is primary of and serves whose failure-triggered drain is due, or whose writer has closed a segment above the
    /// ledger's watermark and below the contiguous tail it knows. The ledgers a dead replica exposes come first, closed
    /// ledgers naming a dead node and then open ledgers whose failure-triggered drain is due, then the other closed ledgers,
    /// then the other open ones; an exposed ledger whose last drain made no progress counts as exposed again only once its
    /// backoff passed. The checks read the writer's local state and the node's membership view, with no I/O, and record
    /// when the node first finds a failure-triggered drain due; the drain itself checks the tail with the quorum. A segment
    /// ends at <c>MaxSegmentBytes</c>, <c>MaxSegmentKeys</c>, or its age, so an open ledger drains about once per segment.
    /// </summary>
    public ImmutableArray<LedgerMetadata> DrainsDue(ImmutableArray<LedgerId> drainToo) => [.. Due(drainToo).Select(static due => due.Ledger)];

    /// <summary>
    /// Returns the ledgers <see cref="DrainsDue"/> returns, in its order, each with whether a dead replica exposes it: a
    /// closed ledger which names a dead node, or an open one whose failure-triggered drain is due, neither backing off.
    /// </summary>
    private ImmutableArray<(LedgerMetadata Ledger, bool Exposed)> Due(ImmutableArray<LedgerId> drainToo)
    {
        var map = _map.Current;
        var manifest = _lsm?.Manifest.Current?.Value;
        var dead = _dead();
        var now = _time.GetUtcNow();
        var closed = map.Ledgers
            .Where(ledger => !ledger.IsOpen
                && (ledger.Location.Value is LedgerLocation.Nodes nodes && nodes.Replicas[0] == Node || !drainToo.IsDefault && drainToo.Contains(ledger.Id))
                && manifest?.IsComplete(ledger.Id) is not true)
            .Select(ledger => (Ledger: ledger, Exposed: ledger.Location.Value is LedgerLocation.Nodes nodes && nodes.Replicas.Any(dead.ContainsKey) && !BackingOff(ledger.Id, now)));
        var open = OpenPrimaries(map)
            .Select(ledger => (Ledger: ledger, Exposed: FailureDrainOf(ledger, dead, now) is not null))
            .Where(due => due.Exposed || HasClosedSegment(due.Ledger.Id, manifest));
        ImmutableArray<(LedgerMetadata Ledger, bool Exposed)> due = [.. closed, .. open];
        return [.. due.Where(static due => due.Exposed), .. due.Where(static due => !due.Exposed)];
    }

    /// <summary>
    /// Returns the failure-triggered drain of open <paramref name="ledger"/> when it is due now (orleans.md §5.6, OR-I23),
    /// recording a new one, and when the node first found it, for a set of deaths it has not drained for; otherwise
    /// <see langword="null"/>. A drain whose marker is decided stays due until its watermark reaches the marker, whatever
    /// the replicas do meanwhile: its bound needs no quorum read. Otherwise it is due while the node serves the ledger's
    /// writer, exactly <c>Q</c> of its replicas are live, the others dead in <paramref name="dead"/>, and one of the dead
    /// was live when the writer opened, so a ledger of one replica, or a degraded successor whose replicas were dead before
    /// it opened, is never due; a drain which ended is not due again for the same deaths, each a node at its silo's
    /// generation. A drain which made no progress at its last start is due again only once its backoff passed.
    /// </summary>
    /// <remarks>
    /// <c>N</c> is odd, so any <c>Q</c> replicas which acknowledged a row and the <c>Q</c> live ones share at least
    /// <c>2Q − N = 1</c> replica (orleans.md §6): with exactly <c>Q</c> live, a decided row may lie on one live replica
    /// alone, while with more it lies on two. With fewer, no marker can be decided, and the ledger waits for a replica's
    /// return or its takeover.
    /// </remarks>
    private FailureDrain? FailureDrainOf(LedgerMetadata ledger, IReadOnlyDictionary<NodeId, int> dead, DateTimeOffset now)
    {
        // A ledger in the object store keeps its one copy there, with no replica to die (ledgers.md §4).
        if (FailureDrainMutant == FailureDrainMutant.NeverDue || ledger.Location.Value is not LedgerLocation.Nodes nodes || _registry.TryGet(ledger.Id) is null)
        {
            return null;
        }

        var backingOff = BackingOff(ledger.Id, now);
        if (_failureDrains.TryGetValue(ledger.Id, out var drain) && drain is { Marker: not null, Ended: false })
        {
            return backingOff ? null : drain;
        }

        Debug.Assert(nodes.Replicas.Length % 2 == 1, "N is odd (orleans.md §6), so exactly Q live replicas share one replica with every quorum.");
        var down = Deaths(nodes, dead);
        var live = nodes.Replicas.Length - down.Count;
        var quorate = FailureDrainMutant == FailureDrainMutant.LiveAtLeastQ ? live >= nodes.Quorum : live == nodes.Quorum;
        if (!quorate || down.Except(OpenedWith(ledger)).IsEmpty)
        {
            return null;
        }

        if (drain is not null && drain.Dead.SetEquals(down))
        {
            return drain.Ended || backingOff ? null : drain;
        }

        _failureDrains[ledger.Id] = drain = new FailureDrain(down, now);
        LogFailureDrainDue(_logger, Node, ledger.Id, string.Join(", ", down.Select(static death => death.Node)), live, nodes.Replicas.Length);
        return backingOff ? null : drain;
    }

    /// <summary>
    /// Returns the replicas of a ledger on <paramref name="nodes"/> which <paramref name="dead"/> holds, each at the
    /// generation of the silo the registry lists for it, so a replica which returns and dies again is another death.
    /// </summary>
    private ImmutableHashSet<Death> Deaths(LedgerLocation.Nodes nodes, IReadOnlyDictionary<NodeId, int> dead) =>
        [.. nodes.Replicas.Where(dead.ContainsKey).Select(node => new Death(node, FailureDrainMutant == FailureDrainMutant.NodeOnly ? 0 : dead[node]))];

    /// <summary>Returns every death <paramref name="dead"/> holds.</summary>
    private ImmutableHashSet<Death> Deaths(IReadOnlyDictionary<NodeId, int> dead) =>
        [.. dead.Select(entry => new Death(entry.Key, FailureDrainMutant == FailureDrainMutant.NodeOnly ? 0 : entry.Value))];

    /// <summary>
    /// Returns the deaths the node knew as the execution which created the writer lifetime <paramref name="ledger"/>'s claim
    /// names began; none, so every death counts, for a writer this process recorded nothing for, which errs toward draining.
    /// </summary>
    private ImmutableHashSet<Death> OpenedWith(LedgerMetadata ledger) =>
        _openedWith.TryGetValue(ledger.Primary.Sequencer, out var deaths) ? deaths : [];

    /// <summary>Returns whether the drain of exposed <paramref name="ledger"/> backs off at <paramref name="now"/>.</summary>
    private bool BackingOff(LedgerId ledger, DateTimeOffset now) => _exposedRetries.TryGetValue(ledger, out var retry) && now < retry.At;

    /// <summary>
    /// Records how the start of the drain of exposed <paramref name="ledger"/> ended: one which made progress clears its
    /// backoff; one which stalled, its last step draining nothing or failing, backs it off, from
    /// <see cref="FirstExposedBackoff"/>, doubling to a minute while its starts stall without advancing, so a drain which
    /// fails fast holds no slot ahead of every other drain each interval, and logs a warning every
    /// <see cref="ExposedWarnStarts"/> such starts.
    /// </summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="stalled">Whether the start stalled.</param>
    /// <param name="advanced">Whether the start moved the watermark before it stalled, which starts the doubling over.</param>
    /// <param name="reason">Why the start stalled, which the node logs.</param>
    private void Progress(LedgerId ledger, bool stalled, bool advanced, string reason = "its drain made no progress")
    {
        if (!stalled)
        {
            _ = _exposedRetries.Remove(ledger);
            return;
        }

        var starts = (!advanced && _exposedRetries.TryGetValue(ledger, out var retry) ? retry.Starts : 0) + 1;
        var backoff = TimeSpan.FromTicks(Math.Min(LongestExposedBackoff.Ticks, FirstExposedBackoff.Ticks << Math.Min(starts - 1, 30)));
        _exposedRetries[ledger] = new Retry(starts, _time.GetUtcNow() + backoff);
        LogExposedNoProgress(_logger, Node, ledger, reason, starts, backoff);
        if (starts % ExposedWarnStarts == 0)
        {
            LogExposedStalled(_logger, Node, ledger, starts, backoff);
        }
    }

    /// <summary>
    /// Forgets the failure-triggered drain of each ledger the node no longer serves as primary, and of each more than
    /// <c>Q</c> of whose replicas are live again unless its decided marker's drain still runs to it; the deaths each lifetime
    /// opened with once the registry no longer holds it; and the backoff of each ledger the map dropped (orleans.md §5.6).
    /// </summary>
    private void ObserveFailures(LedgerMapValue map, IReadOnlyDictionary<NodeId, int> dead)
    {
        LedgerLocation.Nodes? Served(LedgerId ledger) =>
            map.Get(ledger) is { IsOpen: true } open && open.Primary.Node == Node && _registry.TryGet(ledger) is not null && open.Location.Value is LedgerLocation.Nodes nodes ? nodes : null;

        if (FailureDrainMutant != FailureDrainMutant.NeverForgets)
        {
            foreach (var (ledger, drain) in _failureDrains.ToList())
            {
                if (Served(ledger) is not { } nodes
                    || nodes.Replicas.Length - Deaths(nodes, dead).Count > nodes.Quorum && drain is not { Marker: not null, Ended: false })
                {
                    _ = _failureDrains.Remove(ledger);
                }
            }
        }

        foreach (var lifetime in _openedWith.Keys.Where(lifetime => !_executor.Holds(lifetime)).ToList())
        {
            _ = _openedWith.TryRemove(lifetime, out _);
        }

        foreach (var ledger in _exposedRetries.Keys.Where(ledger => map.Get(ledger) is null).ToList())
        {
            _ = _exposedRetries.Remove(ledger);
        }
    }

    /// <summary>Gets the failure-triggered drains the node tracks, which it forgets once their ledgers leave or recover.</summary>
    internal int TrackedFailureDrains => _failureDrains.Count;

    /// <summary>Gets or sets a mutant of the failure-triggered drains, for tests.</summary>
    internal FailureDrainMutant FailureDrainMutant { get; set; }

    /// <summary>Gets or sets the first backoff of an exposed drain which made no progress, which doubles to a minute.</summary>
    internal TimeSpan FirstExposedBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the longest backoff of an exposed drain which made no progress.</summary>
    internal static TimeSpan LongestExposedBackoff { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the starts without progress of an exposed drain after which, and after each as many again, the node warns.</summary>
    internal const int ExposedWarnStarts = 3;

    /// <summary>Gets the intervals an exposed ledger due waits without a start after which, and after each as many again, the node warns.</summary>
    internal const int ExposedWaitWarnIntervals = 5;

    /// <summary>
    /// Gets a task which completes once no drain the node started is in flight: at once when none is, otherwise when the
    /// last of those in flight ends, however each ended. A stop awaits it after it closed the node's admission, so no drain
    /// outlives the runtime it uses (orleans.md §5.2, §5.6).
    /// </summary>
    public Task DrainsEnded
    {
        get
        {
            lock (_drainSync)
            {
                return _draining.Count == 0 ? Task.CompletedTask : (_drainsEnded ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    /// <summary>
    /// Starts the drain of each ledger <see cref="DrainsDue"/> returns whose drain is not in flight, and returns the ledgers
    /// it started: those a dead replica exposes first, the failure-triggered drains among them, while fewer than
    /// <c>drainConcurrency</c> plus one of the node's drains are in flight, the one beyond the concurrency reserved for
    /// them; then the others while fewer than <c>drainConcurrency</c> are; least recently started first among each. An
    /// exposed ledger which waits <see cref="ExposedWaitWarnIntervals"/> calls without a start logs a warning. Each drain
    /// ends on its own: a drain which fails reports its failure to <paramref name="failed"/> as it ends, and its ledger
    /// drains again at a later start; <see cref="DrainsEnded"/> follows them all (orleans.md §5.6).
    /// </summary>
    /// <param name="drainToo">The closed ledgers the controller's last reply named the node to drain too.</param>
    /// <param name="commit">Submits a drain's change once to the controller's manifest committer.</param>
    /// <param name="failed">Told of each started drain which failed, other than by <paramref name="cancellationToken"/>, as it ends.</param>
    /// <param name="cancellationToken">Stops the drains it starts.</param>
    public ImmutableArray<LedgerId> StartDrains(
        ImmutableArray<LedgerId> drainToo,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        Action<LedgerId, Exception> failed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(failed);
        return Start(
            () => Due(drainToo),
            static _ => true,
            due => due switch
            {
                { Exposed: true, Ledger.IsOpen: true } => FailureDrainAsync(due.Ledger, async (below, token) => Step(await DrainBelowAsync(due.Ledger, below, commit, token).ConfigureAwait(true)), fail: null, cancellationToken),
                { Exposed: true } => ExposedAsync(due.Ledger.Id, async () => Progressed(await DrainAsync(due.Ledger, commit, cancellationToken).ConfigureAwait(true)), cancellationToken),
                _ => DrainAsync(due.Ledger, commit, cancellationToken),
            },
            failed,
            cancellationToken);
    }

    /// <summary>
    /// On archive logs, starts the drains due as <see cref="StartDrains"/> does on the LSM, submitting each completed
    /// ledger's drop through <paramref name="commit"/> (orleans.md §5.6): only once the silo's archive gate passed, which
    /// the call takes, and a reply assigned the node a share; a blocked ledger only every
    /// <see cref="ArchiveLogNodeDrains.BlockedRetryInterval"/>, and a ledger whose drain failed or waits for an older
    /// ledger only as <see cref="ArchiveLogNodeDrains.Drains"/> says, a failure-triggered drain's marker failure included.
    /// </summary>
    /// <param name="gate">The silo's archive gate, over which the node's drain worker is built on the first call.</param>
    /// <param name="drainToo">The closed ledgers the controller's last reply named the node to drain too.</param>
    /// <param name="commit">Submits a drop once to the controller's ledger map committer, its <c>CommitLedgerMapAsync</c>.</param>
    /// <param name="failed">Told of each started drain which failed, other than by <paramref name="cancellationToken"/>, as it ends.</param>
    /// <param name="cancellationToken">Stops the drains it starts.</param>
    /// <exception cref="InvalidOperationException">The cluster's archive store is not archive logs.</exception>
    public ImmutableArray<LedgerId> StartLogDrains(
        ArchiveLogGate gate,
        ImmutableArray<LedgerId> drainToo,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        Action<LedgerId, Exception> failed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(failed);
        var logs = Logs;
        logs.Open(gate, _map, _reader);
        return Start(
            () => Promote(Due(drainToo), logs),
            ledger => logs.Drains(ledger.Id),
            due => due switch
            {
                { Exposed: true, Ledger.IsOpen: true } => FailureDrainAsync(due.Ledger, async (below, token) => Step(await logs.DrainAsync(due.Ledger, below, commit, token).ConfigureAwait(true)), failure => logs.Fail(due.Ledger.Id, failure), cancellationToken),
                { Exposed: true } => ExposedAsync(due.Ledger.Id, async () => Progressed(await DrainLogAsync(due.Ledger, commit, cancellationToken).ConfigureAwait(true)), cancellationToken),
                _ => DrainLogAsync(due.Ledger, commit, cancellationToken),
            },
            failed,
            cancellationToken);
    }

    /// <summary>Gets the ledgers whose drains' last starts the node remembers, until its reports find them dropped.</summary>
    internal IReadOnlyCollection<LedgerId> StartedLedgers => _drainStarts.Keys;

    /// <summary>
    /// On archive logs, returns <paramref name="due"/> with each ledger an exposed ledger's drain waits for, an older
    /// overlapping ledger listed, in the exposed tier too: its drop is what the exposed ledger's drain waits on.
    /// </summary>
    private ImmutableArray<(LedgerMetadata Ledger, bool Exposed)> Promote(ImmutableArray<(LedgerMetadata Ledger, bool Exposed)> due, ArchiveLogNodeDrains logs)
    {
        var dead = _dead();
        var exposed = due.Where(due => due.Exposed || due.Ledger.Location.Value is LedgerLocation.Nodes nodes && nodes.Replicas.Any(dead.ContainsKey))
            .Select(static due => due.Ledger.Id)
            .Concat(_failureDrains.Where(static drain => !drain.Value.Ended).Select(static drain => drain.Key));
        HashSet<LedgerId> older = FailureDrainMutant == FailureDrainMutant.WaitedOnNotExposed ? [] : [.. exposed.Select(logs.WaitingFor).OfType<LedgerId>()];
        ImmutableArray<(LedgerMetadata Ledger, bool Exposed)> promoted = [.. due.Select(due => (due.Ledger, due.Exposed || older.Contains(due.Ledger.Id)))];
        return [.. promoted.Where(static due => due.Exposed), .. promoted.Where(static due => !due.Exposed)];
    }

    /// <summary>
    /// Starts <paramref name="drain"/> for each ledger <paramref name="due"/> returns which <paramref name="startable"/>
    /// admits and whose drain is not in flight, those a dead replica exposes first, while fewer than <c>drainConcurrency</c>
    /// plus one drains are in flight, the one beyond the concurrency reserved for them, and then the others while fewer than
    /// <c>drainConcurrency</c> are, least recently started first among each, the order of <paramref name="due"/> among
    /// equals, so closed ledgers precede open ones among those never started. A due ledger it leaves waiting, or one whose
    /// drain is still in flight, starts at a later call, so a slow drain holds no other ledger's. Each call counts an
    /// interval each exposed ledger due waits without a start, its drain not in flight, and warns every
    /// <see cref="ExposedWaitWarnIntervals"/>; the reads are the node's own state, with no I/O.
    /// </summary>
    private ImmutableArray<LedgerId> Start(
        Func<ImmutableArray<(LedgerMetadata Ledger, bool Exposed)>> due,
        Func<LedgerMetadata, bool> startable,
        Func<(LedgerMetadata Ledger, bool Exposed), Task> drain,
        Action<LedgerId, Exception> failed,
        CancellationToken cancellationToken)
    {
        var map = _map.Current;
        var dead = _dead();
        ObserveFailures(map, dead);

        // The node finds each failure-triggered drain due, and when, whether or not a slot is free: no I/O.
        var now = _time.GetUtcNow();
        foreach (var ledger in OpenPrimaries(map))
        {
            _ = FailureDrainOf(ledger, dead, now);
        }

        var unfinished = _failureDrains.Values.Where(static drain => !drain.Ended).Select(static drain => drain.DetectedAt.UtcTicks).DefaultIfEmpty(0).Min();
        Volatile.Write(ref _oldestExposure, unfinished);

        int inFlight;
        HashSet<LedgerId> draining;
        lock (_drainSync)
        {
            inFlight = _draining.Count;
            draining = [.. _draining];
        }

        var waiting = due().Where(due => !draining.Contains(due.Ledger.Id)).ToList();
        var reserved = FailureDrainMutant == FailureDrainMutant.NoReservedSlot ? 0 : 1;
        var starting = new List<(LedgerMetadata Ledger, bool Exposed)>();
        foreach (var candidate in waiting
            .Where(due => startable(due.Ledger))
            .OrderBy(due => due.Exposed && FailureDrainMutant != FailureDrainMutant.ExposedNotFirst ? 0 : 1)
            .ThenBy(due => _drainStarts.GetValueOrDefault(due.Ledger.Id)))
        {
            if (inFlight + starting.Count < _drainConcurrency + (candidate.Exposed ? reserved : 0))
            {
                starting.Add(candidate);
            }
        }

        CountExposedWaits(waiting.Where(static due => due.Exposed).Select(static due => due.Ledger.Id).Except(starting.Select(static started => started.Ledger.Id)));
        foreach (var started in starting)
        {
            lock (_drainSync)
            {
                var added = _draining.Add(started.Ledger.Id);
                Debug.Assert(added, "A ledger's drain starts only while none of it is in flight.");
                Debug.Assert(_draining.Count <= _drainConcurrency + 1, "At most drainConcurrency drains, and one more exposed one, are in flight.");
            }

            _drainStarts[started.Ledger.Id] = ++_drainsStarted;
            _ = DrainOneAsync(started.Ledger, () => drain(started), failed, cancellationToken);
        }

        return [.. starting.Select(static started => started.Ledger.Id)];
    }

    /// <summary>
    /// Counts one more interval for each exposed ledger in <paramref name="waiting"/>, due with no drain of it in flight and
    /// not started, and forgets every other, warning every <see cref="ExposedWaitWarnIntervals"/> intervals a ledger waits.
    /// </summary>
    private void CountExposedWaits(IEnumerable<LedgerId> waiting)
    {
        HashSet<LedgerId> still = [.. waiting];
        foreach (var ledger in _exposedWaits.Keys.Where(ledger => !still.Contains(ledger)).ToList())
        {
            _ = _exposedWaits.Remove(ledger);
        }

        foreach (var ledger in still)
        {
            var intervals = _exposedWaits.GetValueOrDefault(ledger) + 1;
            _exposedWaits[ledger] = intervals;
            if (intervals % ExposedWaitWarnIntervals == 0)
            {
                LogExposedWaiting(_logger, Node, ledger, intervals);
            }
        }
    }

    /// <summary>Returns the gauge's measurement: the time since the oldest unfinished failure-triggered drain was found due.</summary>
    private double OldestExposure()
    {
        var oldest = Volatile.Read(ref _oldestExposure);
        return oldest == 0 ? 0 : Math.Max(0, (_time.GetUtcNow().UtcTicks - oldest) / (double)TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Runs the drain of closed <paramref name="ledger"/>, which names a dead node, recording whether it made progress, so
    /// one which made none backs off; a start the drains' cancellation ended records nothing.
    /// </summary>
    private async Task ExposedAsync(LedgerId ledger, Func<Task<bool>> drain, CancellationToken cancellationToken)
    {
        var progressed = false;
        try
        {
            progressed = await drain().ConfigureAwait(true);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Progress(ledger, stalled: !progressed, advanced: false);
            }
        }
    }

    /// <summary>
    /// Runs the failure-triggered drain of open <paramref name="ledger"/> (orleans.md §5.6, OR-I23, archive-lsm.md §8.1):
    /// its writer ends the open segment with a marker batch, and once the marker at slot <c>m</c> is decided, the drain
    /// calls <paramref name="drainBelow"/> with the bound <c>m + 1</c>, a contiguous tail the marker's durability shows
    /// (LG-I8), until the node's watermark reaches <c>m</c>, reading no batch past the marker. A step which moved the
    /// watermark nothing stalls the start, which ends without a failure and backs the drain off, and a later start resumes
    /// from the decided marker, writing none again; the marker's decision alone is no progress. A marker answered
    /// <c>Fenced</c> or <c>Ambiguous</c> ends the drain: the ledger is closing, and drains to <c>T</c> as a closed ledger
    /// naming a dead node. A writer the registry no longer serves, or disposes meanwhile, ends the start, the ledger
    /// leaving. A failure other than the drains' cancellation stalls the start too; the marker's own failure goes to
    /// <paramref name="fail"/>, which on archive logs backs the ledger off there too, whose drains record their steps' own.
    /// </summary>
    private async Task FailureDrainAsync(
        LedgerMetadata ledger,
        Func<Slot, CancellationToken, Task<DrainStep>> drainBelow,
        Action<Exception>? fail,
        CancellationToken cancellationToken)
    {
        if (FailureDrainOf(ledger, _dead(), _time.GetUtcNow()) is not { } drain)
        {
            return;
        }

        LogFailureDrainStarts(_logger, Node, ledger.Id, drain.Marker);

        // A failure, or a step which drains nothing, stalls the start; the drain's end, or the ledger leaving, does not.
        var stalled = true;
        var advanced = false;
        var reason = "it failed";
        try
        {
            if (drain.Marker is not { } marker)
            {
                if (_registry.TryGet(ledger.Id) is not { } writer)
                {
                    stalled = false;
                    return;
                }

                if (FailureDrainMutant == FailureDrainMutant.DrainsBeforeTheMarker)
                {
                    _ = await drainBelow(writer.KnownContiguousTail, cancellationToken).ConfigureAwait(true);
                }

                ProposalOutcome outcome;
                try
                {
                    outcome = await writer.EndSegmentAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (ObjectDisposedException)
                {
                    // The registry disposed the writer: the ledger leaves the node.
                    stalled = false;
                    return;
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is not OutOfMemoryException)
                {
                    // The drain steps record their own failures; the marker's alone goes to the caller's.
                    fail?.Invoke(failure);
                    throw;
                }

                switch (outcome.Value)
                {
                    case ProposalOutcome.Durable durable:
                        drain.Marker = marker = durable.Slot;
                        Debug.Assert(writer.KnownContiguousTail > marker, "A decided marker lies below the writer's known contiguous tail (LG-I8).");
                        LogMarkerDecided(_logger, Node, ledger.Id, marker);
                        break;
                    case ProposalOutcome.Fenced or ProposalOutcome.Ambiguous:
                        drain.Ended = true;
                        stalled = false;
                        LogMarkerUndecided(_logger, Node, ledger.Id, outcome.Value.GetType().Name);
                        return;
                    case null:
                    default:
                        throw new InvalidOperationException("conventions.md §15: a proposal answers a union which holds a case.");
                }
            }

            var bound = marker + 1;
            if (FailureDrainMutant == FailureDrainMutant.BoundAtTheMarker)
            {
                bound = marker;
            }
            else if (FailureDrainMutant == FailureDrainMutant.DrainsPastTheMarker && (await _reader.ReadTailAsync(ledger, cancellationToken).ConfigureAwait(true)).Value is LedgerTail tail)
            {
                bound = tail.ContiguousTail;
            }

            for (var watermark = FailureWatermark(ledger.Id); watermark < marker;)
            {
                var step = await drainBelow(bound, cancellationToken).ConfigureAwait(true);
                var reached = FailureWatermark(ledger.Id);
                Debug.Assert(reached <= marker + 1 || FailureDrainMutant == FailureDrainMutant.DrainsPastTheMarker, "A failure-triggered drain reads no batch past its marker.");
                if (step == DrainStep.Left || reached > watermark && FailureDrainMutant == FailureDrainMutant.OneDrainAStart)
                {
                    stalled = false;
                    return;
                }

                if (reached <= watermark)
                {
                    // Below m + 1 the segment at the watermark ends at a start at or below the decided marker, so it is never
                    // open: only backpressure, or a wait for an older ledger, stalls a step.
                    Debug.Assert(step != DrainStep.Open || FailureDrainMutant == FailureDrainMutant.BoundAtTheMarker, "The segment before a decided marker is closed below the marker's slot plus one.");
                    reason = step == DrainStep.Open ? "the segment at its watermark is open below its bound" : "its drain made no progress";
                    return;
                }

                advanced = true;
                watermark = reached;
            }

            stalled = false;
            if (FailureDrainMutant != FailureDrainMutant.NeverEnds)
            {
                drain.Ended = true;
            }

            var exposure = _time.GetUtcNow() - drain.DetectedAt;
            // Tagged by the runtime meter's node alone, so the series stay bounded; event 6249 names the ledger.
            _exposure?.Record(exposure.TotalSeconds);
            LogFailureDrained(_logger, Node, ledger.Id, marker, exposure);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Progress(ledger.Id, stalled, advanced, reason);
            }
        }
    }

    /// <summary>The watermark of open <paramref name="ledger"/> the node knows, which a failure-triggered drain drains to its marker.</summary>
    private Slot FailureWatermark(LedgerId ledger) => Watermark(ledger, _lsm?.Manifest.Current?.Value);

    /// <summary>Maps an LSM drain's outcome to a failure-triggered drain's step.</summary>
    private static DrainStep Step(DrainOutcome outcome) => outcome switch
    {
        DrainOutcome.Drained or DrainOutcome.Covered => DrainStep.Drained,
        DrainOutcome.OpenSegment => DrainStep.Open,
        ReplicaWriteOutcome.Backpressure => DrainStep.Stalled,
        ReplicaWriteOutcome.Dropped => DrainStep.Left,
        null => throw new InvalidOperationException("conventions.md §15: a drain answers a union which holds a case."),
    };

    /// <summary>Maps an archive-log drain's outcome to a failure-triggered drain's step.</summary>
    private static DrainStep Step(ArchiveLogDrainOutcome outcome) => outcome switch
    {
        ArchiveLogDrainOutcome.Drained or ArchiveLogDrainOutcome.Complete => DrainStep.Drained,
        ArchiveLogDrainOutcome.OpenSegment => DrainStep.Open,
        ArchiveLogDrainOutcome.Waiting => DrainStep.Stalled,
        ArchiveLogDrainOutcome.Dropped => DrainStep.Left,
        null => throw new InvalidOperationException("conventions.md §15: a drain answers a union which holds a case."),
    };

    /// <summary>Returns whether an LSM drain of a closed ledger made progress.</summary>
    private static bool Progressed(DrainOutcome outcome) => outcome switch
    {
        DrainOutcome.Drained or DrainOutcome.Covered or ReplicaWriteOutcome.Dropped => true,
        DrainOutcome.OpenSegment or ReplicaWriteOutcome.Backpressure => false,
        null => throw new InvalidOperationException("conventions.md §15: a drain answers a union which holds a case."),
    };

    /// <summary>Returns whether an archive-log drain of a closed ledger made progress; a tail check which met backpressure made none.</summary>
    private static bool Progressed(ArchiveLogDrainOutcome? outcome) => outcome is { } answered && answered switch
    {
        ArchiveLogDrainOutcome.Drained or ArchiveLogDrainOutcome.Complete or ArchiveLogDrainOutcome.Dropped => true,
        ArchiveLogDrainOutcome.OpenSegment or ArchiveLogDrainOutcome.Waiting => false,
        null => throw new InvalidOperationException("conventions.md §15: a drain answers a union which holds a case."),
    };

    /// <summary>
    /// Drains <paramref name="ledger"/> once, letting its writer, while it is open, forget the segments the drain passed;
    /// reports a failure to <paramref name="failed"/>, unless the drains' cancellation caused it, and ends the ledger's
    /// drain in flight however it ended.
    /// </summary>
    private async Task DrainOneAsync(LedgerMetadata ledger, Func<Task> drain, Action<LedgerId, Exception> failed, CancellationToken cancellationToken)
    {
        try
        {
            await drain().ConfigureAwait(true);
            ForgetDrained(ledger);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                failed(ledger.Id, failure);
            }
        }
        finally
        {
            LogDrainEnded(_logger, Node, ledger.Id);
            TaskCompletionSource? ended = null;
            lock (_drainSync)
            {
                _ = _draining.Remove(ledger.Id);
                if (_draining.Count == 0)
                {
                    (ended, _drainsEnded) = (_drainsEnded, null);
                }
            }

            _ = ended?.TrySetResult();
        }
    }

    /// <summary>
    /// Returns whether the writer of <paramref name="ledger"/> on this node has closed a segment above the watermark the node
    /// knows and below the contiguous tail it knows.
    /// </summary>
    private bool HasClosedSegment(LedgerId ledger, ManifestValue? manifest) =>
        _registry.TryGet(ledger) is { } writer && ClosesASegment(Watermark(ledger, manifest), writer.OpenSegmentStart, writer.KnownContiguousTail);

    /// <summary>The open ledgers in <paramref name="map"/> the node is primary of.</summary>
    private IEnumerable<LedgerMetadata> OpenPrimaries(LedgerMapValue map) => map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == Node);

    /// <summary>
    /// Lets the writer of <paramref name="ledger"/>, while it is open, forget the segments below the watermark its drains
    /// reached, so the writer's entries follow the drains (orleans.md §5.9).
    /// </summary>
    private void ForgetDrained(LedgerMetadata ledger)
    {
        if (ledger.IsOpen && _registry.TryGet(ledger.Id) is { } writer)
        {
            writer.ForgetDrainedBelow(Watermark(ledger.Id, _lsm?.Manifest.Current?.Value));
        }
    }

    /// <summary>The watermark of <paramref name="ledger"/> the node knows: its own drains' progress, else the store's record of it.</summary>
    private Slot Watermark(LedgerId ledger, ManifestValue? manifest) =>
        _logs is { } logs
            ? logs.DrainedBelow(ledger)
            : _drainedBelow.TryGetValue(ledger, out var drained) ? drained : manifest?.RecordedWatermark(ledger) ?? Slot.Zero;

    /// <summary>
    /// Returns whether the open segment's start, <paramref name="openSegmentStart"/>, closes a segment above
    /// <paramref name="watermark"/> which a drain can take: the batch at the start is decided only below the contiguous
    /// tail, so a drain which read up to it would find its segment open.
    /// </summary>
    internal static bool ClosesASegment(Slot watermark, Slot openSegmentStart, Slot knownContiguousTail) =>
        openSegmentStart > watermark && knownContiguousTail > openSegmentStart;

    /// <summary>
    /// Observes each open ledger the node is primary of whose writer its registry holds (orleans.md §5.9), with no I/O:
    /// first the slot below which its Journals applied every batch, <paramref name="appliedBelow"/>'s answer, then the
    /// contiguous tail its writer knows, the watermark the node knows, and what the writer proposed at or above it. The
    /// tail only grows and covers every batch the Journals applied, so the applied slot is at most the tail.
    /// </summary>
    /// <param name="appliedBelow">Answers a ledger's applied slot, or <see langword="null"/> before its first write.</param>
    public ImmutableArray<PrimaryObservation> ObservePrimaries(Func<LedgerId, Slot?> appliedBelow)
    {
        ArgumentNullException.ThrowIfNull(appliedBelow);
        var manifest = _lsm?.Manifest.Current?.Value;
        var observed = ImmutableArray.CreateBuilder<PrimaryObservation>();
        foreach (var ledger in OpenPrimaries(_map.Current))
        {
            if (_registry.TryGet(ledger.Id) is { } writer)
            {
                var applied = appliedBelow(ledger.Id);
                var watermark = Watermark(ledger.Id, manifest);
                observed.Add(new PrimaryObservation(ledger.Id, applied, writer.KnownContiguousTail, watermark, writer.ObserveUndrained(watermark)));
            }
        }

        return observed.ToImmutable();
    }

    /// <summary>
    /// Drains the closed segment of <paramref name="ledger"/> at its watermark once, below its end, a closed ledger's <c>T</c>
    /// or the contiguous tail the node's reader learns of an open one, and records the progress of a closed ledger's
    /// applied drain for its reports.
    /// </summary>
    /// <param name="ledger">The ledger, as a map value the node read lists it.</param>
    /// <param name="commit">Submits the change once to the controller's manifest committer.</param>
    /// <param name="cancellationToken">As <see cref="DrainWorker.DrainAsync"/>'s.</param>
    /// <returns>The drain worker's outcome; an open ledger whose tail check met backpressure answers it, with nothing drained.</returns>
    /// <exception cref="InvalidOperationException">The cluster's archive store keeps no manifest.</exception>
    public async Task<DrainOutcome> DrainAsync(
        LedgerMetadata ledger,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        _ = Lsm;
        if (await BelowAsync(ledger, cancellationToken).ConfigureAwait(true) is not { } below)
        {
            return ReplicaWriteOutcome.Backpressure.Instance;
        }

        return await DrainBelowAsync(ledger, below, commit, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Drains the closed segment of <paramref name="ledger"/> at its watermark once, below <paramref name="below"/>, and
    /// records the watermark the drain shows and the progress of a closed ledger's applied drain.
    /// </summary>
    private async Task<DrainOutcome> DrainBelowAsync(
        LedgerMetadata ledger,
        Slot below,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        CancellationToken cancellationToken)
    {
        var outcome = await Lsm.Drains.DrainAsync(ledger, below, commit, cancellationToken).ConfigureAwait(true);
        if (outcome.Value is DrainOutcome.Drained { Outcome.Value: ManifestCommitOutcome.Applied } drained)
        {
            _drainedBelow[ledger.Id] = drained.Change.Below;
            if (!ledger.IsOpen)
            {
                _drainProgress[ledger.Id] = _time.GetUtcNow();
            }
        }
        else if (outcome.Value is DrainOutcome.Covered)
        {
            _drainedBelow[ledger.Id] = below;
        }
        else if (outcome.Value is DrainOutcome.OpenSegment open)
        {
            _drainedBelow[ledger.Id] = open.From;
        }

        return outcome;
    }

    /// <summary>
    /// On archive logs, drains the closed segments of <paramref name="ledger"/> below its end, a closed ledger's <c>T</c> or
    /// the contiguous tail the node's reader learns of an open one, once (archive-logs.md §2.3), through the node's
    /// <see cref="ArchiveLogNodeDrains.DrainAsync"/>, which submits a completed ledger's drop and records a blocked drain.
    /// </summary>
    /// <param name="ledger">The ledger, as a map value the node read lists it.</param>
    /// <param name="commit">Submits a drop once to the controller's ledger map committer.</param>
    /// <param name="cancellationToken">As <see cref="ArchiveLogDrainWorker.DrainAsync"/>'s.</param>
    /// <returns>The drain worker's outcome, or <see langword="null"/> when an open ledger's tail check met backpressure, with nothing drained.</returns>
    /// <exception cref="InvalidOperationException">The cluster's archive store is not archive logs, or the silo's archive gate has not passed.</exception>
    public async Task<ArchiveLogDrainOutcome?> DrainLogAsync(
        LedgerMetadata ledger,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var logs = Logs;
        Slot? tail;
        try
        {
            tail = await BelowAsync(ledger, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is not OutOfMemoryException)
        {
            // The node's drains back the ledger off and log the failure, as they do their own.
            logs.Fail(ledger.Id, failure);
            throw;
        }

        if (tail is not { } below)
        {
            return null;
        }

        return await logs.DrainAsync(ledger, below, commit, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Returns the slot below which a drain of <paramref name="ledger"/> may read: a closed ledger's <c>T</c>, or the
    /// contiguous tail the node's reader learns of an open one; <see langword="null"/> when the tail check met backpressure.
    /// </summary>
    private async Task<Slot?> BelowAsync(LedgerMetadata ledger, CancellationToken cancellationToken)
    {
        if (ledger.Status.Value is LedgerStatus.Closed closed)
        {
            return closed.T;
        }

        return (await _reader.ReadTailAsync(ledger, cancellationToken).ConfigureAwait(true)).Value is LedgerTail tail ? tail.ContiguousTail : null;
    }

    /// <summary>
    /// Compacts <paramref name="assignment"/>'s partition from a manifest value at or after the epoch the controller found
    /// it overfull in, and submits the compaction's change (archive §8.8); joins the compaction of the partition already
    /// running. The worker decides an input gone or corrupt (archive §2.4, §8.2); the node relays its corruption.
    /// </summary>
    /// <param name="assignment">The controller's assignment.</param>
    /// <param name="commit">Submits the change once to the controller's manifest committer.</param>
    /// <param name="cancellationToken">As <see cref="CompactionWorker.CompactAsync(KeyRange, ManifestValue, LedgerMapValue, CancellationToken)"/>'s.</param>
    /// <returns>The reply the host relays: the commit's verdict, nothing to compact, or the partition's corruption.</returns>
    /// <exception cref="ArchiveCorruptException">The manifest is corrupt: a failure, never the partition's.</exception>
    /// <exception cref="InvalidOperationException">The cluster's archive store keeps no manifest.</exception>
    public Task<CompactionReply> CompactAsync(
        CompactionAssignment assignment,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(commit);
        if (_compacting.TryGetValue(assignment.Partition, out var running))
        {
            return running;
        }

        // The host's turn adds the running compaction before any of its continuations runs, which removes it.
        var compaction = RunAsync();
        if (!compaction.IsCompleted)
        {
            _compacting[assignment.Partition] = compaction;
        }

        return compaction;

        async Task<CompactionReply> RunAsync()
        {
            try
            {
                var manifest = Lsm.Manifest;
                var snapshot = await manifest.RefreshAsync(assignment.ManifestEpoch, cancellationToken).ConfigureAwait(true);
                return await CompactOnceAsync(assignment.Partition, snapshot.Value, commit, cancellationToken).ConfigureAwait(true);
            }
            finally
            {
                _ = _compacting.Remove(assignment.Partition);
            }
        }
    }

    /// <summary>
    /// Returns the node's report: its silo's generation, the ledgers its registry leaves unserved, the load of each ledger
    /// it is primary of and serves, measured over <c>LoadWindow</c> from this report's sample, its disk pressure as its
    /// host measures it, when each closed ledger's drain last advanced, the replicas its store lists corrupt, which it lost,
    /// and, on archive logs, the keys of the closed segments it has yet to drain, from the segments its writers recorded;
    /// and whether it hands its ledgers off, as its host knows it. A node whose silo refused its archive store or its
    /// Journals hands its ledgers off and reports no undrained keys and no rate: the controller still counts it while its
    /// registry entry is active, with no keys, so it is assigned at most its floor (orleans.md §5.6, OR-I21). It carries
    /// whether the node's silo passed its archive gate, which the controller grain follows (OR-I20), and whether the node
    /// drains, its host's one condition for starting drains, which the controller's drainers follow (§5.6).
    /// </summary>
    /// <param name="diskPressure">Whether the node is under disk pressure, as its host measures it.</param>
    /// <param name="lostReplicas">The ledgers whose replica the node's store lists corrupt.</param>
    /// <param name="handingOff">Whether the node hands its ledgers off.</param>
    /// <param name="refused">Whether the node's silo refused its archive store or its Journals.</param>
    /// <param name="archiveGatePassed">Whether the node's silo passed its archive gate.</param>
    /// <param name="draining">Whether the node drains: it serves and its silo's archive gate passed.</param>
    public NodeReport Report(bool diskPressure, ImmutableArray<LedgerId> lostReplicas, bool handingOff, bool refused, bool archiveGatePassed, bool draining)
    {
        var map = _map.Current;
        var objectStoreDemand = 0L;
        var load = _load.Sample(SampleOpenPrimaries(map, refused, ref objectStoreDemand));
        var (undrainedKeys, requestsPerSecond) = (0L, (double?)null);
        if (_logs is { } logs)
        {
            var owed = DrainsDue(_drainToo);
            foreach (var ledger in owed.Where(ledger => !ledger.IsOpen && logs.Progressed(ledger.Id)))
            {
                // A drain which holds keys within one long segment makes progress before the segment completes.
                _drainProgress[ledger.Id] = _time.GetUtcNow();
            }

            (undrainedKeys, requestsPerSecond) = logs.Report(map, owed);
            if (refused)
            {
                (undrainedKeys, requestsPerSecond) = (0L, null);
            }
        }

        foreach (var ledger in _drainProgress.Keys.Where(ledger => map.Get(ledger) is null).ToList())
        {
            _ = _drainProgress.Remove(ledger);
        }

        foreach (var ledger in _drainedBelow.Keys.Where(ledger => map.Get(ledger) is null).ToList())
        {
            _ = _drainedBelow.Remove(ledger);
        }

        foreach (var ledger in _drainStarts.Keys.Where(ledger => map.Get(ledger) is null).ToList())
        {
            _ = _drainStarts.Remove(ledger);
        }

        foreach (var ledger in _releasedLedgers.Keys.Where(ledger => map.Get(ledger) is not { IsOpen: true } open || open.Primary.Node != Node).ToList())
        {
            _ = _releasedLedgers.Remove(ledger);
        }

        // On an LSM parameter mismatch, Journals is refused while archive.Gate and objectLedgers.Gate remain passed so
        // drains of closed object-store ledgers stay active and keep receiving object-store request budget shares (OR-I8, OL-I1).
        var objectLedgersActive = (!refused || draining) && (_objectLedgersReady?.Invoke() ?? false);
        if (objectLedgersActive && !_drainToo.IsDefault)
        {
            foreach (var id in _drainToo)
            {
                if (map.Get(id) is { IsOpen: false, Location.Value: LedgerLocation.ObjectStore })
                {
                    objectStoreDemand++;
                }
            }
        }

        var objectStoreRequestsPerSecond = objectLedgersActive ? _objectStoreShare?.RateAt(_time.GetUtcNow()) : null;
        var objectLedgersReady = objectLedgersActive;

        return new NodeReport(
            Node,
            _generation,
            ++_reportSequence,
            _registry.Unserved,
            load,
            diskPressure,
            _drainProgress.ToImmutableDictionary(),
            lostReplicas,
            undrainedKeys,
            requestsPerSecond,
            handingOff,
            archiveGatePassed,
            draining,
            objectStoreDemand,
            objectStoreRequestsPerSecond,
            objectLedgersReady,
            _releasedLedgers.ToImmutableDictionary());
    }

    /// <summary>
    /// Samples the writer lifetime and proposed bytes of each open ledger in <paramref name="map"/> the node is primary of
    /// and serves, accumulating object-store demand in the same pass.
    /// </summary>
    private List<(LedgerId Ledger, SequencerInstanceId Writer, long ProposedBytes)> SampleOpenPrimaries(LedgerMapValue map, bool refused, ref long objectStoreDemand)
    {
        var samples = new List<(LedgerId Ledger, SequencerInstanceId Writer, long ProposedBytes)>();
        foreach (var ledger in OpenPrimaries(map))
        {
            if (!refused && ledger.Location.Value is LedgerLocation.ObjectStore)
            {
                objectStoreDemand++;
            }

            if (_registry.TryGet(ledger.Id) is { } writer)
            {
                samples.Add((ledger.Id, writer.InstanceId, writer.ProposedBytes));
            }
        }

        return samples;
    }

    /// <summary>
    /// Brings the node's map view to the epoch the controller's reply names (OR-I19), and then, on archive logs, records the
    /// closed ledgers it names the node to drain too, which the node's undrained keys count, and sets its drains' rate to
    /// the share the reply assigns, keeping the last when it assigns none (orleans.md §5.6).
    /// </summary>
    public async Task ObserveAsync(NodeReportReply reply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (_map.Current.Epoch < reply.MapEpoch)
        {
            _ = await _map.RefreshAsync(reply.MapEpoch, cancellationToken).ConfigureAwait(true);
        }

        _drainToo = reply.DrainToo;
        _logs?.Observe(reply);
        _objectStoreShare?.Observe(reply.ObjectStoreShare, _time.GetUtcNow());
    }

    /// <summary>
    /// Compacts <paramref name="partition"/> from <paramref name="manifest"/> and submits its change: the partition's own
    /// corruption answers <see cref="CompactionReply.Corrupt"/>.
    /// </summary>
    private async Task<CompactionReply> CompactOnceAsync(
        KeyRange partition,
        ManifestValue manifest,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        CancellationToken cancellationToken)
    {
        CompactionOutcome outcome;
        try
        {
            outcome = await Lsm.Compactions.CompactAsync(partition, manifest, _map.Current, cancellationToken).ConfigureAwait(true);
        }
        catch (ArchiveCorruptException corrupt)
        {
            return new CompactionReply.Corrupt(partition, corrupt.Message);
        }

        return outcome switch
        {
            CompactionOutcome.Compacted compacted => new CompactionReply.Compacted(await commit(compacted.Change, cancellationToken).ConfigureAwait(true)),
            CompactionOutcome.NothingToCompact => CompactionReply.NothingToCompact.Instance,
            null => throw new InvalidOperationException("conventions.md §15: a compaction answers a union which holds a case."),
        };
    }

    private (DrainWorker Drains, CompactionWorker Compactions, ManifestView Manifest) Lsm =>
        _lsm ?? throw new InvalidOperationException("The cluster's archive store keeps no manifest.");

    private ArchiveLogNodeDrains Logs =>
        _logs ?? throw new InvalidOperationException("The cluster's archive store is not archive logs.");

    [LoggerMessage(EventId = 6247, Level = LogLevel.Information, Message = "Node {Node} finds replicas {Dead} of ledger {Ledger} dead, leaving {Live} of its {Replicas} live, its quorum: a row decided before may lie on one live replica, so the node ends the open segment with a marker and drains to it (failure-triggered drain).")]
    private static partial void LogFailureDrainDue(ILogger logger, NodeId node, LedgerId ledger, string dead, int live, int replicas);

    [LoggerMessage(EventId = 6248, Level = LogLevel.Information, Message = "Node {Node} ended ledger {Ledger}'s open segment with a marker decided at slot {Marker}; its failure-triggered drain drains to it.")]
    private static partial void LogMarkerDecided(ILogger logger, NodeId node, LedgerId ledger, Slot marker);

    [LoggerMessage(EventId = 6249, Level = LogLevel.Information, Message = "Node {Node} drained ledger {Ledger} to its marker at slot {Marker}, {Exposure} after it found the replica dead.")]
    private static partial void LogFailureDrained(ILogger logger, NodeId node, LedgerId ledger, Slot marker, TimeSpan exposure);

    [LoggerMessage(EventId = 6250, Level = LogLevel.Warning, Message = "Node {Node}'s drain of ledger {Ledger}, which a dead replica exposes, made no progress in {Starts} starts; it starts again after {Backoff}.")]
    private static partial void LogExposedStalled(ILogger logger, NodeId node, LedgerId ledger, int starts, TimeSpan backoff);

    [LoggerMessage(EventId = 6252, Level = LogLevel.Debug, Message = "Node {Node} starts ledger {Ledger}'s failure-triggered drain, its marker at slot {Marker}.")]
    private static partial void LogFailureDrainStarts(ILogger logger, NodeId node, LedgerId ledger, Slot? marker);

    [LoggerMessage(EventId = 6253, Level = LogLevel.Debug, Message = "Node {Node}'s drain of ledger {Ledger}, which a dead replica exposes, stalled: {Reason}, {Starts} starts in a row; it starts again after {Backoff}.")]
    private static partial void LogExposedNoProgress(ILogger logger, NodeId node, LedgerId ledger, string reason, int starts, TimeSpan backoff);

    [LoggerMessage(EventId = 6255, Level = LogLevel.Warning, Message = "Node {Node}'s drain of ledger {Ledger}, which a dead replica exposes, has waited {Intervals} control intervals without a start: every drain slot is held, or its drain waits for an older ledger, a blocked segment, or a failure's backoff.")]
    private static partial void LogExposedWaiting(ILogger logger, NodeId node, LedgerId ledger, int intervals);

    [LoggerMessage(EventId = 6254, Level = LogLevel.Debug, Message = "Node {Node}'s drain of ledger {Ledger} ended, freeing its slot.")]
    private static partial void LogDrainEnded(ILogger logger, NodeId node, LedgerId ledger);

    [LoggerMessage(EventId = 6251, Level = LogLevel.Information, Message = "Node {Node}'s marker for ledger {Ledger} was {Outcome}: the ledger is closing, and drains to its end as a closed ledger.")]
    private static partial void LogMarkerUndecided(ILogger logger, NodeId node, LedgerId ledger, string outcome);

    /// <summary>A replica's death: its node, at the generation of the silo the registry listed for it.</summary>
    private readonly record struct Death(NodeId Node, int Generation);

    /// <summary>The backoff of an exposed drain: its starts without progress, and when it is due again.</summary>
    private readonly record struct Retry(int Starts, DateTimeOffset At);

    /// <summary>A failure-triggered drain's step: it drained, made no progress, or found the ledger gone.</summary>
    private enum DrainStep
    {
        Drained,
        Open,
        Stalled,
        Left,
    }

    /// <summary>
    /// The failure-triggered drain of one open ledger: the deaths it answers, when the node first found it due, its marker's
    /// slot once decided, and whether it ended, its watermark at the marker or its marker not decided.
    /// </summary>
    private sealed class FailureDrain(ImmutableHashSet<Death> dead, DateTimeOffset detectedAt)
    {
        public ImmutableHashSet<Death> Dead { get; } = dead;

        public DateTimeOffset DetectedAt { get; } = detectedAt;

        public Slot? Marker { get; set; }

        public bool Ended { get; set; }
    }
}

/// <summary>A mutant of the node's failure-triggered drains (orleans.md §5.6), for tests.</summary>
internal enum FailureDrainMutant
{
    /// <summary>The drains as specified.</summary>
    None,

    /// <summary>No failure-triggered drain is ever due.</summary>
    NeverDue,

    /// <summary>The drain drains below the writer's known contiguous tail before its marker is decided.</summary>
    DrainsBeforeTheMarker,

    /// <summary>The drain's bound is the contiguous tail the reader learns, not the marker's slot plus one.</summary>
    DrainsPastTheMarker,

    /// <summary>The drain's bound is the marker's slot, below which its segment does not close.</summary>
    BoundAtTheMarker,

    /// <summary>A start drains once and ends, though the watermark lies below the marker.</summary>
    OneDrainAStart,

    /// <summary>The drain is due at <c>Q</c> live replicas or more, not exactly <c>Q</c>.</summary>
    LiveAtLeastQ,

    /// <summary>A drain which reached its marker never records its end, so it stays due.</summary>
    NeverEnds,

    /// <summary>The node never forgets a drain whose ledger left it or recovered.</summary>
    NeverForgets,

    /// <summary>A death is its node alone, so a replica which returns and dies again is the death already drained for.</summary>
    NodeOnly,

    /// <summary>The starts order every due ledger least recently started first, the exposed ones among them.</summary>
    ExposedNotFirst,

    /// <summary>No drain slot is reserved for exposed ledgers beyond the concurrency.</summary>
    NoReservedSlot,

    /// <summary>On archive logs, the older ledger an exposed ledger's drain waits for stays out of the exposed tier.</summary>
    WaitedOnNotExposed,

    /// <summary>
    /// The deaths a writer opened with are those of whichever execution ends while the writer is served with no record,
    /// not those of the execution which created its lifetime.
    /// </summary>
    RecordsAtAnyExecutionsEnd,
}
