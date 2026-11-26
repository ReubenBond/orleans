using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The controller's decisions (orleans.md §5.6, §5.7): it hosts the ledger map's committer and, on the LSM, the
/// manifest's, chooses every transition, and assigns compactions. Its host, the controller grain, performs its I/O: it
/// sends what a pass chooses without awaiting the replies, and reports each reply back (§5.6, "Control cores and their
/// hosts").
/// </summary>
/// <remarks>
/// <para>
/// The host calls every member from one logical thread, as a grain's turns are, and no member awaits anything but a
/// read of a view or a committer's answer, so slow work never delays a takeover. Its state changes only in the
/// synchronous segments of its members, and no invariant of it spans an await. <see cref="PassDue"/> tells the host when an
/// input warrants a pass before the next timed one. Its state is soft, rebuilt from the map,
/// the registry, membership, the manifest, and the nodes' reports and repeated requests: an activation which lacks it,
/// after a move or as a duplicate, may send a conflicting transition, and the commit rules let at most one of the two
/// apply (OR-I12). Each transition carries the revision of the registry value it chose its nodes from (<c>ChosenAt</c>)
/// and the epoch of the map value it chose from, and names <c>N</c> distinct nodes, active in that registry value and live
/// in the membership view, its executor first (OR-I5).
/// </para>
/// <para>
/// The ledger map's committer raises the controller's map view with every value it reads or writes, so the controller
/// chooses from the map it committed. A transition counts as in progress until its reply arrives and, when the reply names
/// a durable epoch, until the view reaches it; until the map shows its outcome; or until <c>TransitionTimeout</c> passes or
/// its executor is dead, either of which marks the executor failed for its ledgers. While one of a ledger is in progress,
/// the controller sends no other transition of it. A <c>Rejected</c> transition, a defect, is never sent again in the
/// activation (ledgers.md §2.6): a new activation may send it once more.
/// </para>
/// </remarks>
internal sealed partial class ClusterController
{
    private readonly ControlOptions _options;
    private readonly LedgerMapView _map;
    private readonly LedgerMapCommitter _mapCommitter;
    private (ManifestView View, ManifestCommitter Committer, ArchiveOptions Options)? _lsm;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _quorumRangeCapacity;
    private readonly int _objectStoreRangeCapacity;
    private readonly Dictionary<SentTransition, Pending> _inProgress = [with(ReferenceEqualityComparer.Instance)];
    private readonly Dictionary<LedgerId, PendingPrepare> _prepareInProgress = [];
    private readonly Dictionary<LedgerId, (NodeId Primary, Slot Tail)> _releasedByPrimary = [];
    private readonly HashSet<LedgerId> _prepareFallback = [];
    private readonly Dictionary<LedgerId, int> _ledgersInProgress = [];
    private readonly Dictionary<NodeId, int> _handOffs = [];
    private readonly HashSet<LedgerId> _rollRequests = [];
    private readonly HashSet<(TransitionPurpose Purpose, LedgerId First, LedgerId Second)> _rejected = [];
    private readonly Dictionary<LedgerId, HashSet<NodeId>> _failedTargets = [];

    // Discretionary primary balance backs off from a target whose cooperative replacement failed, timed out, or was
    // declined, so rescuing the already-released ledger onto another node does not trigger a PrepareRelease churn loop
    // while the target is transiently unreachable or starting up (orleans.md §5.6, OR-I24).
    private readonly Dictionary<NodeId, (DateTimeOffset Until, int Failures, int? Generation)> _cooperativeBalanceBackoff = [];
    private readonly Dictionary<NodeId, FrozenSet<LedgerId>> _unserved = [];

    // The ledgers whose replica each node's latest report lists lost: its store found it damaged (LR-I2).
    private readonly Dictionary<NodeId, FrozenSet<LedgerId>> _lostReplicas = [];

    // The nodes whose latest report shows them under disk pressure (orleans.md §5.9), which placement puts last.
    private readonly HashSet<NodeId> _underDiskPressure = [];

    // Whether each node's latest report says it drains: it serves and its silo's archive gate passed (orleans.md §5.6).
    private readonly Dictionary<NodeId, bool> _draining = [];

    // The generation for which each node reported its object-ledger gate passed (OL-I1, orleans.md §5.14).
    private readonly Dictionary<NodeId, int> _objectLedgersReady = [];

    // The newest silo generation each node has reported: a report from an older one, a process the node's restart
    // replaced, is ignored even while the registry view lags the restart.
    private readonly Dictionary<NodeId, int> _generations = [];

    // Whether each node's latest report indicates initial apply-loop catch-up is complete (gates routine BalancePrimaries).
    private readonly Dictionary<NodeId, bool> _catchUpReady = [];

    // The number of each node's last report applied, of its generation in _generations.
    private readonly Dictionary<NodeId, long> _sequences = [];
    private readonly Dictionary<LedgerId, long> _load = [];
    private readonly Dictionary<LedgerId, DateTimeOffset> _closedSince = [];
    private readonly Dictionary<LedgerId, DateTimeOffset> _drainProgress = [];

    // On archive logs, the nodes' undrained keys, rates, and shares, from which each reply assigns its share.
    private RequestBudgetShares? _shares;
    private bool _archiveAdopted;

    // When object-store ledgers are configured, the store's layout (which gates object-store placement and defaults K)
    // and the nodes' shares of ObjectStoreRequestsPerSecond (orleans.md §5.14).
    private ObjectLedgerLayout? _objectLedgerLayout;
    private RequestBudgetShares? _objectStoreShares;
    private readonly Dictionary<KeyRange, (CompactionAssignment Assignment, DateTimeOffset SentAt)> _compactions = [];
    private readonly HashSet<KeyRange> _corrupt = [];
    private readonly HashSet<NodeId> _retirements = [];
    private readonly HashSet<NodeId> _retiring = [];
    private NodeView _nodes = NodeView.Empty;
    private (ulong Manifest, ulong Map, NodeView Nodes)? _assignedFor;
    private bool _compactionsChanged = true;
    private DateTimeOffset _nextCompactionExpiry = DateTimeOffset.MaxValue;
    private MapCache _cache = new(LedgerMapValue.Empty);
    private FrozenDictionary<NodeId, ImmutableArray<LedgerId>> _drainToo = [];
    private ulong _knownEpoch;
    private (ulong Epoch, NodeView Nodes)? _balanced;
    private bool _changed = true;
    private DateTimeOffset _nextGrace = DateTimeOffset.MaxValue;
    private bool _warnedCapacity;
    private ulong? _floorRaising;
    private ulong? _completingDrainAll;

    /// <summary>Initializes a new instance of the <see cref="ClusterController"/> class.</summary>
    /// <param name="options">The cluster's control options (orleans.md §6).</param>
    /// <param name="map">The controller's map view, which <paramref name="mapCommitter"/> raises with every value it reads or writes.</param>
    /// <param name="mapCommitter">The ledger map's committer, whose value limit bounds the map.</param>
    /// <param name="archive">The cluster's archive store: the LSM, with the controller's manifest view, through which its
    /// committer reads and to which it gives each value it writes, the committer, and the archive's options; archive logs,
    /// whose request budget the controller shares among the nodes; or <see langword="null"/> until its host's archive gate
    /// passes, when the host gives it through <see cref="AdoptArchive"/>: until then the controller does no archive work,
    /// assigning no compaction and no share.</param>
    /// <param name="timeProvider">The clock of timeouts.</param>
    /// <param name="logger">Logs defects, corruption, and the transitions sent.</param>
    /// <param name="objectLedgerLayout">The cluster's object-ledger store layout when already known, or <see langword="null"/>
    /// until the host's object-ledger gate passes and calls <see cref="AdoptObjectLedgers"/> (orleans.md §5.14).</param>
    public ClusterController(
        ControlOptions options,
        LedgerMapView map,
        LedgerMapCommitter mapCommitter,
        ClusterArchive? archive,
        TimeProvider timeProvider,
        ILogger logger,
        ObjectLedgerLayout? objectLedgerLayout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mapCommitter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;
        _map = map;
        _mapCommitter = mapCommitter;
        _time = timeProvider;
        _logger = logger;
        _quorumRangeCapacity = mapCommitter.RangeCapacity(options.Replicas);
        _objectStoreRangeCapacity = mapCommitter.RangeCapacity(0);
        if (archive is { } adopted)
        {
            AdoptArchive(adopted);
        }

        if (objectLedgerLayout is { } layout)
        {
            AdoptObjectLedgers(layout);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the controller has adopted the cluster's object-ledger store layout.
    /// </summary>
    public bool HasObjectLedgers => _objectLedgerLayout is not null;

    /// <summary>
    /// Gets a value indicating whether a planned whole-cluster drain off node-backed ledgers is active in the durable
    /// node registry (<see cref="RegisteredNodes.DrainAllRevision"/> &gt; 0, orleans.md §5.14, §8).
    /// </summary>
    public bool IsDrainingAll => _nodes.Registry.DrainAllRevision > 0;

    /// <summary>
    /// Validates whether this controller can start a planned whole-cluster drain off node-backed ledgers into
    /// object-store ledgers (orleans.md §5.14, §8): returns <see cref="DrainAllStartOutcome.Started"/> immediately when
    /// drain-all is already active in the observed node registry (<see cref="IsDrainingAll"/>, idempotent per
    /// outcomes.md); otherwise fails closed with <see cref="DrainAllStartOutcome.ObjectLedgersNotConfigured"/> when neither
    /// <paramref name="objectLedgersConfigured"/> nor the observed node registry names an object-ledger store, and with
    /// <see cref="DrainAllStartOutcome.ObjectLedgersNotReady"/> until the controller has adopted the object-ledger
    /// store layout (<see cref="HasObjectLedgers"/>).
    /// </summary>
    public DrainAllStartOutcome ValidateStartDrainAll(bool objectLedgersConfigured)
    {
        if (IsDrainingAll)
        {
            return DrainAllStartOutcome.Started.Instance;
        }

        if (!objectLedgersConfigured && !_nodes.Registry.HasObjectLedgerStore)
        {
            return DrainAllStartOutcome.ObjectLedgersNotConfigured.Instance;
        }

        if (!HasObjectLedgers)
        {
            return DrainAllStartOutcome.ObjectLedgersNotReady.Instance;
        }

        return DrainAllStartOutcome.Started.Instance;
    }

    /// <summary>
    /// Ends the drain-all phase 2 completion a pass returned, whatever its outcome: a completion which applied shows in
    /// the registry view once observed, and the next pass returns another while phase 2 remains eligible.
    /// </summary>
    public void DrainAllCompletionEnded() => _completingDrainAll = null;

    /// <summary>
    /// Returns the cluster's drain-all status from the controller's registry view and map view, each a value read from its
    /// register or written by a write which landed (orleans.md §5.14, §8).
    /// </summary>
    public DrainAllStatus DrainAll() => DrainAllStatus.Of(_nodes.Registry, _map.Current);

    /// <summary>
    /// Returns the cluster's linearizable drain-all status by reading the ledger map register first (<c>t1</c>) and then
    /// reading the node registry through <paramref name="readRegistry"/> (<c>t2 &gt; t1</c>), deciding from that exact
    /// ordered pair so a diskful node registered between the reads is never missed and a concurrent map update after
    /// <c>t1</c> never produces a torn snapshot (orleans.md §5.14, §8).
    /// </summary>
    public async Task<DrainAllStatus> DrainAllAsync(Func<CancellationToken, Task<RegisteredNodes>> readRegistry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readRegistry);
        var map = await _map.ReadAsync(cancellationToken).ConfigureAwait(true);
        var registry = await readRegistry(cancellationToken).ConfigureAwait(true);
        return DrainAllStatus.Of(registry, map);
    }

    /// <summary>
    /// Takes the cluster's object-ledger store layout once its host's object-ledger gate passed with the store's evidence
    /// (orleans.md §5.14): enables object-store ledger placement, resolves the default window <c>K</c> for that layout, and
    /// begins sharing <see cref="ControlOptions.ObjectStoreRequestsPerSecond"/> among the nodes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The controller already adopted the object-ledger store.</exception>
    public void AdoptObjectLedgers(ObjectLedgerLayout layout)
    {
        if (_objectLedgerLayout is not null)
        {
            throw new InvalidOperationException("A controller adopts its cluster's object-ledger store once.");
        }

        _objectLedgerLayout = layout;
        _objectStoreShares = new RequestBudgetShares(_options.ObjectStoreRequestsPerSecond, _options.ControlInterval, _time.GetUtcNow());
        _changed = true;
        PassDue = true;
    }

    /// <summary>
    /// Takes the cluster's archive store, once its host's archive gate passed with the store's evidence (orleans.md §5.6,
    /// §5.13): the LSM, over which the controller then reads the manifest, assigns compaction, and drops, or archive logs,
    /// whose request budget it then shares among the nodes. The controller takes one, once.
    /// </summary>
    /// <exception cref="ArgumentException">The store is neither the LSM nor archive logs.</exception>
    /// <exception cref="InvalidOperationException">The controller already took its archive store.</exception>
    public void AdoptArchive(ClusterArchive archive)
    {
        if (_archiveAdopted)
        {
            throw new InvalidOperationException("A controller takes its cluster's archive store once.");
        }

        switch (archive.Value)
        {
            case ClusterArchive.Lsm lsm:
                _lsm = (lsm.View, lsm.Committer, lsm.Options);
                break;
            case ClusterArchive.Logs:
                _shares = new RequestBudgetShares(_options.ArchiveLogRequestsPerSecond, _options.ControlInterval, _time.GetUtcNow());
                break;
            case null:
            default:
                throw new ArgumentException("conventions.md §15: the cluster's archive store is the LSM or archive logs.", nameof(archive));
        }

        _archiveAdopted = true;
    }

    /// <summary>Gets how many transitions in progress ended by <c>TransitionTimeout</c>; for tests.</summary>
    internal int TimeoutExpiries { get; private set; }

    /// <summary>
    /// Gets how many repairs balancing passes could not place, with fewer than <c>N</c> nodes which may be given work; each
    /// is logged.
    /// </summary>
    public long UnplacedRepairs { get; private set; }

    /// <summary>Gets how many passes ran; for tests.</summary>
    internal int Passes { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether the gate ignores the map's floor, opening once no ledger names a retired
    /// node: the mutant of OR-I4 which the controller simulation sets to show its gate checker catches it.
    /// </summary>
    internal bool GateIgnoresTheFloorMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a pass sends transitions while the map's floor lies above its registry view:
    /// the mutant of the pause below the floor which the controller simulation sets to show its LG-I11 check catches it.
    /// </summary>
    internal bool RegistryBehindIgnoredMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether reports' lost replicas are ignored, so none is repaired: the mutant of the
    /// durability repair which the controller simulation sets to show its lost-replica step catches it.
    /// </summary>
    internal bool LostReplicasIgnoredMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether replica evening tries only the node with the most replicas, stalling when
    /// that node can give up none: the mutant of evening's search which the placement property sets to show its quiescence
    /// check catches it.
    /// </summary>
    internal bool ReplicaEveningMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every transition the controller places may keep dead replicas, not only a
    /// takeover's and a leaving primary's hand-off: the mutant of OR-I5's scope which the controller tests, the placement
    /// property, and the controller simulation set to show their oracles catch it.
    /// </summary>
    internal bool DegradedPlacementMutant { get; set; }

    /// <summary>
    /// Gets whether an input since the last pass warrants a pass before the next timed one: a reply which freed budget, a
    /// node whose claims ended or which departs, a new hand-off or retirement request, or a newly unserved ledger. Every
    /// other change waits for the host's timed pass, so a reply which frees nothing never drives passes at the speed of a
    /// round trip.
    /// </summary>
    public bool PassDue { get; private set; }

    /// <summary>
    /// Gets the newest map epoch the controller knows durable: read by its view, which its committer raises with each value
    /// it writes, or named by an executor's verdict (OR-I19).
    /// </summary>
    public ulong KnownEpoch => Math.Max(_knownEpoch, _map.Current.Epoch);

    /// <summary>
    /// Reads the manifest through the controller's manifest view on the LSM, so a quiet cluster still compacts and drops after
    /// the controller moves (orleans.md §5.6); nothing on archive logs. Its host calls it after activation, retrying it apart
    /// from every pass: until it succeeds, compaction assignment and drops wait, and passes do not. The host's map view
    /// gates the first pass.
    /// </summary>
    public async Task ReadManifestAsync(CancellationToken cancellationToken = default)
    {
        if (_lsm is { } lsm)
        {
            _ = await lsm.View.ReadAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Takes a registry value's projection and each registered node's liveness, which the host computed against that
    /// registry value, as the controller's view of the nodes when the registry value is at least as new as its last.
    /// </summary>
    public void Observe(RegisteredNodes registry, IReadOnlyDictionary<NodeId, NodeLiveness> liveness)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(liveness);
        if (registry.Revision < _nodes.Registry.Revision)
        {
            return;
        }

        var nodes = new NodeView(registry, liveness);
        if (registry.Revision != _nodes.Registry.Revision || !nodes.SameLiveness(_nodes))
        {
            var previousDrainAll = _nodes.Registry.DrainAllRevision;
            if (previousDrainAll == 0 && registry.DrainAllRevision > 0)
            {
                LogDrainAllStarted(_logger, registry.DrainAllRevision);
            }
            else if (previousDrainAll > 0 && registry.DrainAllRevision == 0)
            {
                LogDrainAllEnded(_logger);
            }

            // A node whose claims ended or which departs has ledgers to take over or hand off at once; a change to
            // DrainAllRevision starts or ends drain-all (or raises the floor after Phase 2 completion) at once.
            PassDue |= registry.DrainAllRevision != previousDrainAll
                || registry.Nodes.Keys.Any(node => nodes.DeadSince(node) is not null && _nodes.IsUp(node) || nodes.IsDeparting(node) && _nodes.IsLive(node));
            _nodes = nodes;
            _changed = true;
        }
    }

    /// <summary>
    /// Records a stopping or refused node's request to hand off each open ledger it is primary of (orleans.md §7.2), from
    /// its silo's <paramref name="generation"/>; a request from a generation older than the registry's entry is stale and
    /// ignored. The request is pending, and the node neither executes a transition, receives a ledger, nor holds a new
    /// replica, until membership shows the node dead or the registry names a newer generation of it: a node primary of
    /// nothing stays pending, so it is given no ledger back while it stops or is refused, and a restarted node serves again.
    /// The node repeats the request each control interval while its runtime runs, so a new activation rebuilds the set
    /// within one interval (orleans.md §5.6).
    /// </summary>
    public void RequestHandOff(NodeId node, int generation)
    {
        // A request from a generation older than the registry's ends at the next pass's expiry.
        if (!_handOffs.TryGetValue(node, out var requested) || requested < generation)
        {
            _handOffs[node] = generation;
            _changed = true;
            PassDue = true;
        }
    }

    /// <summary>
    /// Records a node's request for a WAL-release roll of <paramref name="ledger"/> (OR-I13); the node repeats it until
    /// the map shows the roll, and a closed ledger needs none.
    /// </summary>
    public void RequestRoll(LedgerId ledger) => _changed |= _rollRequests.Add(ledger);

    /// <summary>
    /// Records an operator's request to retire <paramref name="node"/> (orleans.md §5.3), which a pass returns for the host
    /// to submit to the node registry, once at a time, until an answer settles it or the controller's registry view shows
    /// the node retired. The caller repeats it until <see cref="Retirement"/> reports the node retired, so a new activation
    /// learns of it again.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="node"/> is the default identity.</exception>
    public void RequestRetirement(NodeId node)
    {
        if (node == default)
        {
            throw new ArgumentException("A retirement names a node.", nameof(node));
        }

        if (_nodes.Registry.Find(node) is not { IsRetired: true } && _retirements.Add(node))
        {
            PassDue = true;
        }
    }

    /// <summary>
    /// For a StatefulSet scale-down to <paramref name="replicas"/>, records the retirement of every node the controller's
    /// registry view holds active at an ordinal of <paramref name="replicas"/> or more, and returns every node at those
    /// ordinals whose retirement gate is not open, retired already or not, with its ordinal and standing: the nodes whose
    /// gates must open before their ordinals' volumes are released, which the caller follows up by identity (orleans.md
    /// §5.3, §8). The view is a registry value read after the call.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="replicas"/> is below <c>N</c>: the retired nodes' ledgers could not be placed on the rest, and no
    /// gate would open. A node at exactly <c>N</c> is retired by identity alone.
    /// </exception>
    public ImmutableArray<OrdinalNode> RequestScaleDown(int replicas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicas, _options.Replicas);
        return RequestRetirements(ordinal => ordinal >= replicas, static (_, _) => true);
    }

    /// <summary>
    /// For a deleted volume at <paramref name="ordinal"/>, returns every node the controller's registry view holds at the
    /// ordinal whose retirement gate is not open, with its standing, and records the retirement of each one active whose
    /// silo membership shows dead, since a volume a running pod uses cannot be deleted (orleans.md §5.3). A live occupant
    /// is returned active, unretired, for its caller to retire by identity once its pod has stopped. The caller follows up
    /// by identity alone, through <see cref="Retirement"/> and <see cref="RequestRetirement"/>: a repeat by ordinal would
    /// resolve it afresh, and could name a successor which registered there since. The view is a registry value read
    /// after the call.
    /// </summary>
    public ImmutableArray<OrdinalNode> RequestVolumeRetirement(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return RequestRetirements(candidate => candidate == ordinal, (node, _) => _nodes.DeadSince(node) is not null);
    }

    /// <summary>
    /// Settles <paramref name="node"/>'s retirement in flight with the node registry's answer, <c>Retired</c> or
    /// <c>NotRegistered</c>, or a defect its host reported: no pass returns it again until it is requested again, and a
    /// pass is due, to raise the map's floor to the retirement.
    /// </summary>
    public void RetirementSettled(NodeId node)
    {
        _ = _retiring.Remove(node);
        _ = _retirements.Remove(node);
        PassDue = true;
    }

    /// <summary>
    /// Ends <paramref name="node"/>'s retirement in flight with an unknown outcome: it stays requested, and the next pass
    /// returns it again.
    /// </summary>
    public void RetirementFailed(NodeId node) => _ = _retiring.Remove(node);

    /// <summary>
    /// Ends the floor raise a pass returned, whatever its answer: a raise which applied shows in the map view, and the next
    /// pass returns another while the view's floor is below the registry view's newest retirement.
    /// </summary>
    public void FloorRaiseEnded() => _floorRaising = null;

    /// <summary>
    /// Returns <paramref name="node"/>'s standing and, once retired, its gate (OR-I4), from the controller's registry view and
    /// its map view, each a value read from its register or written by a write which landed. A node the view does not hold
    /// is <c>NotRegistered</c> in that view only: the host reads the registry before it answers so.
    /// </summary>
    public NodeStanding Retirement(NodeId node)
    {
        var standing = NodeStanding.Of(node, _nodes.Registry, _map.Current);
        return GateIgnoresTheFloorMutant && standing.Value is NodeStanding.Retired retired
            ? retired with { Floor = Math.Max(retired.Floor, retired.RetiredRevision) }
            : standing;
    }

    /// <summary>
    /// Returns <paramref name="node"/>'s standing as <see cref="Retirement"/> does, reading the map register first when the
    /// node is retired and its gate is not open in the view, whose value may predate the drops and the floor raise which
    /// open it (§5.8 step 4). A node which is not retired is answered without the read: no map value opens its gate.
    /// </summary>
    public async Task<NodeStanding> RetirementAsync(NodeId node, CancellationToken cancellationToken = default)
    {
        if (Retirement(node).Value is not NodeStanding.Retired { IsReleasable: false })
        {
            return Retirement(node);
        }

        _ = await _map.ReadAsync(cancellationToken).ConfigureAwait(true);
        return Retirement(node);
    }

    /// <summary>
    /// Records a node's report and answers it with the newest map epoch the controller knows durable and the closed ledgers
    /// the node is to drain too, which the last pass computed (orleans.md §5.6, OR-I19). A report from a silo older than
    /// the newest the controller knows for the node, by its registry view or the node's reports, is a process the node's
    /// restart replaced: it is answered with the epoch alone and otherwise ignored. A node the registry view does not yet
    /// name is recorded. A newer silo's report replaces the node's unserved ledgers, each of which it lists counting as
    /// newly unserved, and its lost replicas. A newly unserved ledger or a newly lost replica makes a pass due. On archive
    /// logs, the reply assigns the node its share of <c>ArchiveLogRequestsPerSecond</c> (<see cref="RequestBudgetShares"/>). A
    /// replaced process's report, and one of the generation last heard which is not newer than the last applied, are
    /// answered with the epoch alone and ignored.
    /// </summary>
    public NodeReportReply Report(NodeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var registered = _nodes.Registry.Find(report.Node) is { } entry ? entry.Generation : 0;
        var hasReported = _generations.TryGetValue(report.Node, out var reported);
        if (report.Generation < Math.Max(registered, reported)
            || hasReported && report.Generation == reported && report.Sequence <= _sequences.GetValueOrDefault(report.Node))
        {
            // A replaced process's report, or one its process gave up on which arrives after a newer one, is answered and
            // ignored: applying it would record a rate the node no longer runs at (OR-I21).
            return new NodeReportReply(KnownEpoch, [], ArchiveLogShare: null);
        }

        // A node's first report from its generation makes it a balance target (ClusterState.IsBalanceTarget).
        _changed |= !hasReported || report.Generation > reported;
        _generations[report.Node] = report.Generation;
        _sequences[report.Node] = report.Sequence;
        var wasCatchUpReady = _catchUpReady.GetValueOrDefault(report.Node, true);
        _catchUpReady[report.Node] = report.CatchUpReady;
        if (wasCatchUpReady != report.CatchUpReady)
        {
            _changed = true;
            if (report.CatchUpReady)
            {
                PassDue = true;
            }
        }
        if (report.HandingOff)
        {
            // The report carries the node's hand-off request, so a new activation knows it pending from its first report.
            RequestHandOff(report.Node, report.Generation);
        }

        var unserved = report.Unserved.ToFrozenSet();
        var restarted = hasReported && report.Generation > reported;
        var newlyUnserved = !restarted && _unserved.TryGetValue(report.Node, out var previouslyUnserved)
            ? !unserved.IsSubsetOf(previouslyUnserved)
            : unserved.Count > 0;
        if (newlyUnserved)
        {
            // A newly unserved ledger's claim ended: it is taken over at once.
            PassDue = true;
        }

        _unserved[report.Node] = unserved;
        FrozenSet<LedgerId> lost = LostReplicasIgnoredMutant ? [] : report.LostReplicas.ToFrozenSet();
        if (!lost.IsSubsetOf(_lostReplicas.GetValueOrDefault(report.Node, [])))
        {
            // A replica its node lost is repaired at once (LR-I2), by balancing, which _changed gates.
            PassDue = true;
            _changed = true;
        }

        // Only nodes which lost a replica are kept, so a pass's check of every replica finds the map empty.
        if (lost.Count == 0)
        {
            _ = _lostReplicas.Remove(report.Node);
        }
        else
        {
            _lostReplicas[report.Node] = lost;
        }
        _changed |= report.DiskPressure ? _underDiskPressure.Add(report.Node) : _underDiskPressure.Remove(report.Node);

        // A node which drains nothing has its closed ledgers named to others at the next pass.
        _draining[report.Node] = report.Draining;
        if (report.ObjectLedgersReady)
        {
            _changed |= !_objectLedgersReady.TryGetValue(report.Node, out var readyGeneration) || readyGeneration < report.Generation;
            _objectLedgersReady[report.Node] = report.Generation;
        }
        else
        {
            _changed |= _objectLedgersReady.Remove(report.Node);
        }
        var map = _map.Current;
        foreach (var (ledger, load) in report.ProposedBytesPerSecond)
        {
            // Only the ledger's primary reports its load; a ledger it never reported has an unknown load.
            if (map.Get(ledger) is { IsOpen: true } open && open.Primary.Node == report.Node)
            {
                int? before = _load.TryGetValue(ledger, out var previous) ? LoadClass(previous) : null;
                _load[ledger] = load;
                _changed |= before != LoadClass(load);
            }
        }

        foreach (var (ledger, at) in report.ClosedDrainProgress)
        {
            if (!_drainProgress.TryGetValue(ledger, out var known) || at > known)
            {
                _drainProgress[ledger] = at;
            }
        }

        foreach (var (ledger, tail) in report.ReleasedLedgersOrEmpty)
        {
            // A primary reports open ledgers it already drained via PrepareRelease so a new controller activation
            // after failover observes the released state idempotently (orleans.md §5.6, OR-I24).
            if (map.Get(ledger) is { IsOpen: true } open && open.Primary.Node == report.Node)
            {
                var updated = !_releasedByPrimary.TryGetValue(ledger, out var known) || known.Primary != report.Node || known.Tail != tail;
                _releasedByPrimary[ledger] = (report.Node, tail);
                _ = _prepareFallback.Remove(ledger);
                if (_prepareInProgress.ContainsKey(ledger))
                {
                    EndPrepareInProgress(ledger);
                    updated = true;
                }

                if (updated)
                {
                    _changed = true;
                    PassDue = true;
                }
            }
        }

        var now = _time.GetUtcNow();
        return new NodeReportReply(
            KnownEpoch,
            _drainToo.GetValueOrDefault(report.Node, []),
            _shares?.Report(report.Node, report.UndrainedKeys, report.ArchiveLogRequestsPerSecond, _nodes, now),
            _objectStoreShares?.Report(report.Node, report.ObjectStoreDemand, report.ObjectStoreRequestsPerSecond, _nodes, now));
    }

    /// <summary>
    /// Submits <paramref name="change"/> to the ledger map's committer and answers its verdict (orleans.md §5.6): an
    /// executor's change, or a drop.
    /// </summary>
    public async Task<LedgerMapCommitOutcome> CommitLedgerMapAsync(LedgerMapChange change, CancellationToken cancellationToken = default)
    {
        var outcome = await _mapCommitter.CommitAsync(change, cancellationToken).ConfigureAwait(true);
        RaiseKnownEpoch(DurableEpoch(outcome.Value));
        if (IsDrainingAll && outcome.Value is LedgerMapCommitOutcome.Applied)
        {
            if (change.Value is LedgerMapChange.Drop)
            {
                // Dropping a closed ledger unblocks Phase 2 once zero node-backed ledgers remain, or frees map room to
                // roll remaining open node-backed ledgers.
                var hasNodeLedgers = false;
                var hasOpenNodeLedgers = false;
                foreach (var ledger in _map.Current.Ledgers)
                {
                    if (ledger.Location.Value is LedgerLocation.Nodes)
                    {
                        hasNodeLedgers = true;
                        if (ledger.IsOpen)
                        {
                            hasOpenNodeLedgers = true;
                            break;
                        }
                    }
                }

                if (!hasNodeLedgers || hasOpenNodeLedgers)
                {
                    PassDue = true;
                }
            }
            else if (change.Value is LedgerMapChange.RaiseFloor && _map.Current.RetirementFloor >= _nodes.Registry.DrainAllRevision)
            {
                // Raising the floor to DrainAllRevision unblocks Phase 2 once zero node-backed ledgers remain.
                PassDue = true;
            }
        }

        return outcome;
    }

    /// <summary>
    /// Gets a value indicating whether the controller adopted the LSM, whose manifest it commits: false before its host's
    /// archive gate passed and gave it the store, and on archive logs, which keep no manifest (orleans.md §5.6).
    /// </summary>
    public bool KeepsManifest => _lsm is not null;

    /// <summary>Submits <paramref name="change"/> to the manifest's committer and answers its verdict: a drain or a compaction.</summary>
    /// <exception cref="InvalidOperationException">The controller keeps no manifest: <see cref="KeepsManifest"/> is false.</exception>
    public Task<ManifestCommitOutcome> CommitManifestAsync(ManifestChange change, CancellationToken cancellationToken = default) =>
        (_lsm?.Committer ?? throw new InvalidOperationException("The cluster's archive store keeps no manifest.")).CommitAsync(change, cancellationToken);

    /// <summary>
    /// Runs one pass: takeovers of every ended claim, then balancing within <c>MaxConcurrentTransitions</c> and repairs
    /// within <c>MaxConcurrentRepairs</c>, and compaction assignments; computes each node's closed ledgers to drain too; and
    /// returns the recorded retirements not in flight, each now in flight, and the retirement floor the map has yet to
    /// reach, unless a raise is in flight (orleans.md §5.3). Each transition and assignment it returns is in progress. A
    /// pass whose map epoch, registry revision, and membership are unchanged, with no reply, request, or report since, and no
    /// replica's grace expired, skips balancing. While the map's floor is above the registry view's revision, every
    /// transition it would choose carries a <c>ChosenAt</c> below the floor, which the commit rules refuse (LG-I11): it
    /// sends none, and asks its host to read the registry.
    /// </summary>
    public ControllerPass RunPass()
    {
        PassDue = false;
        Passes++;
        var now = _time.GetUtcNow();
        _shares?.Prune(_nodes, now);
        _objectStoreShares?.Prune(_nodes, now);
        var map = _map.Current;
        var targetReplicas = TargetIsObjectStore ? 0 : _options.Replicas;
        if (_cache.Value.Epoch != map.Epoch || _cache.TargetReplicas != targetReplicas)
        {
            _cache = new MapCache(map, targetReplicas);
            _changed = true;
        }

        Expire(now);
        var state = new ClusterState(_cache, _nodes, _handOffs, _generations, _load, unserved: _unserved, lost: _lostReplicas, underDiskPressure: _underDiskPressure, draining: _draining, now: now, objectLedgersReady: _objectLedgersReady);
        ChargeInProgress(state);
        var sent = ImmutableArray.CreateBuilder<SentTransition>();
        var prepares = ImmutableArray.CreateBuilder<SentPrepareRelease>();
        var drainingAll = IsDrainingAll;
        var registryBehind = !RegistryBehindIgnoredMutant && map.RetirementFloor > _nodes.Registry.Revision;
        var canCompleteDrainAll = drainingAll && !registryBehind
            && map.RetirementFloor >= _nodes.Registry.DrainAllRevision
            && !state.Cache.HasNodeLedgers;
        var preferDiskless = drainingAll && state.ObjectStoreExecutors.Any(state.IsDiskless);
        if (!registryBehind)
        {
            var room = _mapCommitter.Room(map)
                - _inProgress.Values.Sum(static entry => (long)entry.Growth)
                - _prepareInProgress.Values.Sum(static entry => (long)entry.EstimatedGrowth);
            SendTakeovers(state, sent, prepares, ref room, preferDiskless);
            SendReleaseRolls(state, sent, prepares, ref room, preferDiskless);
            if (_changed || _balanced != (map.Epoch, _nodes) || now >= _nextGrace)
            {
                _changed = false;
                _balanced = (map.Epoch, _nodes);
                _nextGrace = DateTimeOffset.MaxValue;
                Balance(state, sent, prepares, ref room, preferDiskless);
            }

            SendReleasedReplacements(state, sent, prepares, ref room, preferDiskless);
        }

        foreach (var transition in sent)
        {
            LogSent(_logger, transition.Purpose, transition.Transition, transition.Executor);
        }

        foreach (var prepare in prepares)
        {
            LogPrepareReleaseSent(_logger, prepare.Purpose, prepare.Request.Ledger, prepare.Node, prepare.Request.Deadline);
        }

        _drainToo = DrainToo(state);
        _ = _retirements.RemoveWhere(node => _nodes.Registry.Find(node) is { IsRetired: true } && !_retiring.Contains(node));
        ImmutableArray<NodeId> retirements = _retirements.Count == 0
            ? []
            : [.. _retirements.Where(node => !_retiring.Contains(node)).Order(NodeOrder.Instance)];
        _retiring.UnionWith(retirements);
        var floorTarget = Math.Max(_nodes.Registry.LatestRetiredRevision, _nodes.Registry.DrainAllRevision);
        ulong? floorRaise = floorTarget > map.RetirementFloor && _floorRaising is null ? floorTarget : null;
        _floorRaising ??= floorRaise;
        ulong? completeDrainAll = canCompleteDrainAll && _completingDrainAll is null ? _nodes.Registry.DrainAllRevision : null;
        _completingDrainAll ??= completeDrainAll;
        return new ControllerPass(sent.ToImmutable(), AssignCompactions(), retirements, floorRaise, registryBehind, completeDrainAll, prepares.ToImmutable());
    }

    /// <summary>
    /// Returns every node the controller's registry view holds at an ordinal <paramref name="atOrdinal"/> accepts whose
    /// retirement gate is not open, in node order, with its ordinal and standing, after recording the retirement of each
    /// one active which <paramref name="retires"/>; a node it records is answered active, its retirement pending.
    /// </summary>
    private ImmutableArray<OrdinalNode> RequestRetirements(Func<int, bool> atOrdinal, Func<NodeId, RegisteredNode, bool> retires)
    {
        var map = _map.Current;
        var found = ImmutableArray.CreateBuilder<OrdinalNode>();
        foreach (var (node, entry) in _nodes.Registry.Nodes.OrderBy(static entry => entry.Key, NodeOrder.Instance))
        {
            if (entry.Ordinal is not { } ordinal || !atOrdinal(ordinal))
            {
                continue;
            }

            if (!entry.IsRetired && retires(node, entry))
            {
                PassDue |= _retirements.Add(node);
            }

            var standing = NodeStanding.Of(node, _nodes.Registry, map);
            if (!standing.IsReleasable)
            {
                found.Add(new OrdinalNode(node, ordinal, standing));
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Charges each transition in progress whose closed ledger the map still lists open with its moves, as the pass which
    /// sent it did, so a pass which follows a reply while another transition is in flight counts that transition's
    /// primaries and replicas where they are going: primary balance never hands a second ledger to a node whose first has
    /// not landed, which would over-correct passes run reply by reply into a cycle of hand-offs.
    /// </summary>
    private void ChargeInProgress(ClusterState state)
    {
        foreach (var entry in _inProgress.Keys)
        {
            if (entry.Transition.Closes is [var first, ..] && state.Map.Get(first) is { IsOpen: true } closing)
            {
                state.MovePrimaries(closing.Primary.Node, entry.Executor, entry.Transition.Value is Transition.Split ? 2 : 1);
                state.CountReplicas(ReplicasOf(closing), ReplicasOf(entry.Transition.Choice.Location));
            }
        }

        foreach (var (ledger, pending) in _prepareInProgress)
        {
            if (state.Map.Get(ledger) is { IsOpen: true } closing)
            {
                state.MovePrimaries(closing.Primary.Node, pending.TargetExecutor, pending.TargetOpens);
            }
        }
    }

    /// <summary>
    /// Takes the reply to a sent <see cref="SentPrepareRelease"/> (or <see langword="null"/> when the RPC failed or timed
    /// out): on <see cref="PrepareReleaseReply.Released"/>, records the cooperative release so the next pass sends
    /// <see cref="Transition.Replace"/> with <see cref="PredecessorReleaseHint.Cooperative"/>; on any other reply or
    /// failure, marks the ledger for immediate non-cooperative fallback (<see cref="PredecessorReleaseHint.TakenOver"/>)
    /// (orleans.md §5.6, §5.7, OR-I24).
    /// </summary>
    /// <returns>Whether the reply makes an immediate pass due.</returns>
    public bool PrepareReleaseCompleted(SentPrepareRelease sent, PrepareReleaseReply? reply)
    {
        ArgumentNullException.ThrowIfNull(sent);
        var ledger = sent.Request.Ledger;
        if (_prepareInProgress.TryGetValue(ledger, out var pending) && ReferenceEquals(pending.Sent, sent))
        {
            EndPrepareInProgress(ledger);
            _changed = true;
            PassDue = true;
            if (reply is { Value: PrepareReleaseReply.Released released })
            {
                _releasedByPrimary[ledger] = (sent.Node, released.ContiguousTail);
                _ = _prepareFallback.Remove(ledger);
            }
            else if (reply is { Value: null })
            {
                throw new ArgumentException("conventions.md §15: a reply holds a case.", nameof(reply));
            }
            else
            {
                _ = _prepareFallback.Add(ledger);
            }

            return true;
        }

        if (reply is { Value: PrepareReleaseReply.Released lateReleased }
            && _map.Current.Get(ledger) is { IsOpen: true } open
            && open.Primary.Node == sent.Node
            && !_ledgersInProgress.ContainsKey(ledger))
        {
            _releasedByPrimary[ledger] = (sent.Node, lateReleased.ContiguousTail);
            _ = _prepareFallback.Remove(ledger);
            _changed = true;
            PassDue = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Takes <paramref name="sent"/>'s reply: <see langword="null"/> when the call failed or its reply was lost, which marks
    /// its executor failed for its ledgers. A verdict naming an epoch keeps it in progress until the map view reaches the
    /// epoch; <c>Rejected</c>, a defect, bans it; <c>Declined</c> marks its executor failed.
    /// </summary>
    /// <returns>
    /// Whether the reply freed budget for new work, <c>Applied</c> or <c>Superseded</c>, which makes a pass due; every other
    /// reply waits for the host's timed pass, so a refused or declined transition is sent again at most once a pass.
    /// </returns>
    public bool TransitionCompleted(SentTransition sent, TransitionReply? reply)
    {
        ArgumentNullException.ThrowIfNull(sent);
        _changed = true;
        if (reply is not { } answer)
        {
            MarkFailed(sent, _time.GetUtcNow());
            EndInProgress(sent);
            return false;
        }

        if (answer.Value is LedgerMapCommitOutcome.Applied or LedgerMapCommitOutcome.Superseded)
        {
            _ = _cooperativeBalanceBackoff.Remove(sent.Executor);
            var epoch = DurableEpoch(answer.Value);
            RaiseKnownEpoch(epoch);
            if (_inProgress.TryGetValue(sent, out var pending))
            {
                pending.DecidedAt = epoch;
            }

            PassDue = true;
            return true;
        }
        else if (answer.Value is LedgerMapCommitOutcome.Rejected rejected)
        {
            LogRejected(_logger, sent.Transition, rejected.Reason);
            _ = _rejected.Add(BanOf(sent.Purpose, sent.Transition.Closes));
            EndInProgress(sent);
        }
        else if (answer.Value is LedgerMapCommitOutcome.Refused)
        {
            EndInProgress(sent);
        }
        else if (answer.Value is TransitionReply.Declined)
        {
            MarkFailed(sent, _time.GetUtcNow());
            EndInProgress(sent);
        }
        else
        {
            throw new ArgumentException("conventions.md §15: a reply holds a case.", nameof(reply));
        }

        return false;
    }

    /// <summary>
    /// Assigns the compaction of each partition the manifest view's value shows overfull (archive §8.8), to the live primary
    /// of the open ledger containing its lowest key: once until its outcome is decided, and again after
    /// <c>CompactionTimeout</c> only when that primary changed or is not live; at most about
    /// <c>CompactionBudgetBytes</c>' worth in progress per assignee; and never a partition a compaction found corrupt in
    /// this activation. The host calls it after each manifest value its committer applies, and each pass does.
    /// </summary>
    public ImmutableArray<CompactionAssignment> AssignCompactions()
    {
        if (_lsm is not { } lsm || lsm.View.Current is not { } manifest)
        {
            return [];
        }

        var archive = lsm.Options;
        var now = _time.GetUtcNow();
        var map = _map.Current;

        // Assignment derives from the manifest, the map, the nodes, and what is in progress: unchanged, it assigns nothing.
        var inputs = (manifest.Value.Epoch, map.Epoch, _nodes);
        if (_assignedFor == inputs && !_compactionsChanged && now < _nextCompactionExpiry)
        {
            return [];
        }

        _assignedFor = inputs;
        _compactionsChanged = false;
        var overfull = CompactionWorker.OverfullPartitions(manifest.Value, archive);
        foreach (var partition in _compactions.Keys.Where(partition => !overfull.Contains(partition)).ToList())
        {
            _ = _compactions.Remove(partition);
        }

        // An assignment past its timeout holds no budget: its call failed, or its reply was lost.
        var perAssignee = Math.Max(1, archive.CompactionBudgetBytes / (2L * (1L << archive.GridBits) * archive.SectionBytes));
        var assignedTo = new Dictionary<NodeId, long>();
        foreach (var (assignment, sentAt) in _compactions.Values)
        {
            if (now - sentAt < _options.CompactionTimeout)
            {
                assignedTo[assignment.Assignee] = assignedTo.GetValueOrDefault(assignment.Assignee) + 1;
            }
        }

        var assigned = ImmutableArray.CreateBuilder<CompactionAssignment>();
        foreach (var partition in overfull)
        {
            if (_corrupt.Contains(partition))
            {
                continue;
            }

            if (map.Find(LowestKey(partition)) is not { } lookup || !_nodes.IsLiveActive(lookup.Open.Primary.Node))
            {
                // The range's primary is gone; its takeover's executor is assigned at a later pass.
                continue;
            }

            // Past the timeout the partition is sent again, to the same assignee too, which joins its compaction of the
            // partition if one still runs.
            var assignee = lookup.Open.Primary.Node;
            if (_compactions.TryGetValue(partition, out var current) && now - current.SentAt < _options.CompactionTimeout)
            {
                continue;
            }

            if (assignedTo.GetValueOrDefault(assignee) >= perAssignee)
            {
                continue;
            }

            var assignment = new CompactionAssignment(partition, assignee, manifest.Value.Epoch);
            _compactions[partition] = (assignment, now);
            assignedTo[assignee] = assignedTo.GetValueOrDefault(assignee) + 1;
            assigned.Add(assignment);
        }

        _nextCompactionExpiry = _compactions.Values.Select(entry => entry.SentAt + _options.CompactionTimeout).Where(expiry => expiry > now).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        return assigned.ToImmutable();
    }

    /// <summary>
    /// Ends <paramref name="assignment"/>'s time in progress with its assignee's reply, when it is the partition's current
    /// assignment: after <see cref="CompactionReply.Corrupt"/>, the partition is assigned never again in this activation
    /// (archive §8.8), and a critical event names the partition and what its compaction found corrupt. A
    /// <see langword="null"/> reply, for a call which failed or whose reply was lost, ends it too, so the partition is
    /// assigned again at the next assignment while it is overfull.
    /// </summary>
    public void CompactionCompleted(CompactionAssignment assignment, CompactionReply? reply)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!_compactions.TryGetValue(assignment.Partition, out var current) || !ReferenceEquals(current.Assignment, assignment))
        {
            return;
        }

        _ = _compactions.Remove(assignment.Partition);
        _compactionsChanged = true;
        if (reply is not { } answer)
        {
            return;
        }

        if (answer.Value is CompactionReply.Corrupt corrupt)
        {
            if (_corrupt.Add(assignment.Partition))
            {
                LogCorruptPartition(_logger, assignment.Partition, assignment.Assignee, corrupt.Reason);
            }
        }
        else if (answer.Value is null)
        {
            throw new ArgumentException("conventions.md §15: a reply holds a case.", nameof(reply));
        }
    }

    /// <summary>
    /// Releases <paramref name="partition"/>, held in this activation since a compaction found it corrupt, once an operator
    /// repaired it; the next assignment pass assigns it again while it is overfull.
    /// </summary>
    /// <returns>Whether the partition was held.</returns>
    public bool ResolveCorruptPartition(KeyRange partition)
    {
        var held = _corrupt.Remove(partition);
        _compactionsChanged |= held;
        return held;
    }

    /// <summary>Returns the epoch a verdict names durable, the deciding value's for <c>Applied</c> and <c>Superseded</c>, or 0.</summary>
    private static ulong DurableEpoch(object? verdict) => verdict switch
    {
        LedgerMapCommitOutcome.Applied applied => applied.Epoch,
        LedgerMapCommitOutcome.Superseded superseded => superseded.Epoch,
        LedgerMapCommitOutcome.Refused or LedgerMapCommitOutcome.Rejected or TransitionReply.Declined => 0,
        null => throw new ArgumentException("conventions.md §15: a verdict holds a case.", nameof(verdict)),
        _ => throw new ArgumentException("A verdict is a ledger map commit outcome or a declined reply.", nameof(verdict)),
    };

    /// <summary>Returns the lowest key of <paramref name="range"/>: its prefix, or the one-byte key 0 for the whole keyspace.</summary>
    private static ReadOnlySpan<byte> LowestKey(KeyRange range) => range.Prefix.IsEmpty ? [0] : range.Prefix;

    /// <summary>
    /// Returns what a <c>Rejected</c> transition bans: its purpose and the ledgers it closes, so a rejected roll never
    /// blocks a takeover of its ledger, a rejected bootstrap is never sent again, and a rejected merge blocks that merge.
    /// </summary>
    private static (TransitionPurpose, LedgerId, LedgerId) BanOf(TransitionPurpose purpose, ImmutableArray<LedgerId> closes) =>
        (purpose, closes.IsEmpty ? default : closes[0], closes.Length > 1 ? closes[1] : default);

    private void RaiseKnownEpoch(ulong epoch) => _knownEpoch = Math.Max(_knownEpoch, epoch);

    private int LoadClass(long load) => load > _options.SplitBytesPerSecond ? 1 : load < _options.SplitBytesPerSecond / 4 ? -1 : 0;

    /// <summary>
    /// Ends the time in progress of every transition whose decided epoch the view reached or whose outcome the map shows,
    /// and, marking the executor failed, of every one whose timeout passed or whose executor is dead; then drops the soft
    /// state of ledgers and nodes the map no longer gives it to.
    /// </summary>
    private void Expire(DateTimeOffset now)
    {
        var map = _cache.Value;
        foreach (var (sent, pending) in _inProgress.ToList())
        {
            var shown = pending.DecidedAt is { } decided
                ? map.Epoch >= decided
                : pending.Closes.IsEmpty ? !map.Ledgers.IsEmpty : pending.Closes.All(ledger => map.Get(ledger) is not { IsOpen: true });
            var timedOut = now - pending.SentAt >= _options.TransitionTimeout;
            var failed = !shown && (timedOut || _nodes.DeadSince(sent.Executor) is not null);
            if (shown || failed)
            {
                if (failed)
                {
                    MarkFailed(sent, now);
                    TimeoutExpiries += timedOut ? 1 : 0;
                }

                EndInProgress(sent);
            }
        }

        foreach (var (ledger, pending) in _prepareInProgress.ToList())
        {
            if (map.Get(ledger) is not { IsOpen: true } open || open.Primary.Node != pending.Sent.Node)
            {
                EndPrepareInProgress(ledger);
                continue;
            }

            var timedOut = now - pending.SentAt >= _options.PrepareReleaseTimeout;
            var primaryUnavailable = _nodes.DeadSince(pending.Sent.Node) is not null
                || _unserved.GetValueOrDefault(pending.Sent.Node, []).Contains(ledger);
            if (timedOut || primaryUnavailable)
            {
                EndPrepareInProgress(ledger);
                _ = _prepareFallback.Add(ledger);
                PassDue = true;
            }
        }

        Prune(_failedTargets, map);
        Prune(_load, map);
        Prune(_releasedByPrimary, map);
        _ = _prepareFallback.RemoveWhere(ledger => map.Get(ledger) is not { IsOpen: true });
        _ = _rollRequests.RemoveWhere(ledger => map.Get(ledger) is not { IsOpen: true });
        foreach (var (node, generation) in _handOffs.ToList())
        {
            if (!_nodes.IsUp(node) || _nodes.Registry.Find(node)?.Generation > generation)
            {
                _ = _handOffs.Remove(node);
            }
        }

        foreach (var (node, backoff) in _cooperativeBalanceBackoff.ToList())
        {
            var currentGeneration = _nodes.Registry.Find(node)?.Generation;
            if (!_nodes.IsUp(node)
                || backoff.Generation is { } gen && currentGeneration is { } current && current > gen
                || now - backoff.Until >= _options.TransitionTimeout)
            {
                _ = _cooperativeBalanceBackoff.Remove(node);
            }
        }
        foreach (var ledger in _cache.Closed)
        {
            _ = _closedSince.TryAdd(ledger.Id, now);
        }

        foreach (var ledger in _closedSince.Keys.Where(ledger => map.Get(ledger) is null).ToList())
        {
            _ = _closedSince.Remove(ledger);
            _ = _drainProgress.Remove(ledger);
        }

        static void Prune<T>(Dictionary<LedgerId, T> entries, LedgerMapValue map)
        {
            foreach (var ledger in entries.Keys.Where(ledger => map.Get(ledger) is not { IsOpen: true }).ToList())
            {
                _ = entries.Remove(ledger);
            }
        }
    }

    private void MarkFailed(SentTransition sent, DateTimeOffset now)
    {
        foreach (var ledger in sent.Transition.Closes)
        {
            if (!_failedTargets.TryGetValue(ledger, out var failed))
            {
                _failedTargets[ledger] = failed = [];
            }

            _ = failed.Add(sent.Executor);
        }

        if (sent.Transition.Value is Transition.Replace { PredecessorHint: PredecessorReleaseHint.Cooperative })
        {
            var failures = _cooperativeBalanceBackoff.TryGetValue(sent.Executor, out var existing) ? existing.Failures + 1 : 1;
            var maxDelay = _options.TransitionTimeout > _options.ControlInterval ? _options.TransitionTimeout : _options.ControlInterval;
            var delay = _options.ControlInterval;
            for (var step = 1; step < failures && delay < maxDelay; step++)
            {
                delay = delay.Ticks > maxDelay.Ticks / 2 ? maxDelay : TimeSpan.FromTicks(delay.Ticks * 2);
            }

            _cooperativeBalanceBackoff[sent.Executor] = (now + delay, failures, _nodes.Registry.Find(sent.Executor)?.Generation);
        }
    }

    private void EndInProgress(SentTransition sent)
    {
        if (!_inProgress.Remove(sent, out var pending))
        {
            return;
        }

        _changed = true;
        foreach (var ledger in pending.Closes)
        {
            if (--_ledgersInProgress[ledger] == 0)
            {
                _ = _ledgersInProgress.Remove(ledger);
            }
        }
    }

    private void EndPrepareInProgress(LedgerId ledger)
    {
        if (!_prepareInProgress.Remove(ledger))
        {
            return;
        }

        _changed = true;
        if (--_ledgersInProgress[ledger] == 0)
        {
            _ = _ledgersInProgress.Remove(ledger);
        }
    }

    private bool IsEligible(LedgerMetadata ledger) => ledger.IsOpen && !_ledgersInProgress.ContainsKey(ledger.Id);

    private bool IsReleasedByPrimary(LedgerMetadata ledger) =>
        _releasedByPrimary.TryGetValue(ledger.Id, out var released) && released.Primary == ledger.Primary.Node;

    private bool HasFailed(LedgerId ledger, NodeId node) => _failedTargets.TryGetValue(ledger, out var failed) && failed.Contains(node);

    private bool TargetIsObjectStore => IsDrainingAll || _options.LedgerPlacement == LedgerPlacement.ObjectStore;

    private bool TargetReady => !TargetIsObjectStore || HasObjectLedgers;

    private int TargetWindow => TargetIsObjectStore && _objectLedgerLayout is { } layout
        ? _options.EffectiveObjectLedgerWindow(layout)
        : _options.SequencerWindow;

    private static ImmutableArray<NodeId> ReplicasOf(LedgerMetadata? ledger) =>
        ledger is { Location.Value: LedgerLocation.Nodes nodes } ? nodes.Replicas : [];

    private static ImmutableArray<NodeId> ReplicasOf(LedgerLocation location) =>
        location.Value is LedgerLocation.Nodes nodes ? nodes.Replicas : [];

    /// <summary>
    /// Returns whether a transition replacing or creating <paramref name="ledger"/> targets <see cref="LedgerLocation.ObjectStore"/>:
    /// always during drain-all (new node-backed placement is strictly suppressed); when configured for object-store
    /// placement, whenever the ledger is absent (bootstrap) or already in the object store, or for an existing
    /// node-backed ledger when the controller adopted the object-ledger layout and at least one
    /// <see cref="ClusterState.ObjectStoreExecutors"/> node is ready (so an object-store outage or un-upgraded cluster
    /// never blocks takeovers, hand-offs, or WAL-release rolls of existing node-backed ledgers); and when configured for
    /// node placement, for an already-object-store ledger whenever fewer than <c>N</c> quorum executors exist and an
    /// object-store executor is ready (so mixed-cluster diskful replacement and staggered empty-root restart serve open
    /// object-store ledgers immediately until <c>N</c> diskful silos are live to roll them back to nodes).
    /// </summary>
    private bool WillPlaceInObjectStore(ClusterState state, LedgerMetadata? ledger) =>
        IsDrainingAll
        || (_options.LedgerPlacement == LedgerPlacement.ObjectStore
            ? ledger is null or { Location.Value: LedgerLocation.ObjectStore } || HasObjectLedgers && !state.ObjectStoreExecutors.IsEmpty
            : ledger is { Location.Value: LedgerLocation.ObjectStore } && state.QuorumExecutors.Length < _options.Replicas && HasObjectLedgers && !state.ObjectStoreExecutors.IsEmpty);

    private static bool CanExecuteFor(ClusterState state, NodeId node, bool placeInObjectStore, bool needsObjectLedgers) =>
        placeInObjectStore
            ? state.CanExecuteObjectStore(node)
            : state.CanExecuteQuorum(node) && (!needsObjectLedgers || state.IsObjectLedgersReady(node));

    private static ImmutableArray<NodeId> ExecutorsFor(ClusterState state, bool placeInObjectStore, bool needsObjectLedgers) =>
        placeInObjectStore
            ? state.ObjectStoreExecutors
            : needsObjectLedgers
                ? [.. state.QuorumExecutors.Where(state.IsObjectLedgersReady)]
                : state.QuorumExecutors;

    private ImmutableArray<NodeId> TargetExecutors(ClusterState state) =>
        TargetIsObjectStore ? state.ObjectStoreExecutors : state.QuorumExecutors;

    /// <summary>
    /// Returns the node which takes over or is handed <paramref name="ledger"/> from <paramref name="from"/> (orleans.md
    /// §5.7): the first of the candidates which has not failed a transition of the ledger, else the first candidate. The
    /// candidates, each able to execute, in order: the claim's own node, for a takeover it reported itself; the ledger's
    /// other replicas, in their order; then every other node, fewest primaries first. On node-backed ledgers, nodes under
    /// disk pressure come last, since a primary's own pressure throttles it (OR-I10); object-store ledgers ignore disk
    /// pressure (orleans.md §5.14).
    /// </summary>
    private NodeId? Successor(ClusterState state, LedgerMetadata ledger, NodeId from, bool selfTakeover, bool placeInObjectStore, bool disklessOnly = false)
    {
        NodeId? first = null;
        foreach (var candidate in Candidates(state, ledger, from, selfTakeover, placeInObjectStore, disklessOnly))
        {
            if (!HasFailed(ledger.Id, candidate))
            {
                return candidate;
            }

            first ??= candidate;
        }

        return first;
    }

    private static IEnumerable<NodeId> Candidates(ClusterState state, LedgerMetadata ledger, NodeId from, bool selfTakeover, bool placeInObjectStore, bool disklessOnly = false)
    {
        var needsObjectLedgers = state.HasOverlappingObjectStoreLedger(ledger.Range);
        if (selfTakeover && (!disklessOnly || state.IsDiskless(from)) && CanExecuteFor(state, from, placeInObjectStore, needsObjectLedgers))
        {
            yield return from;
        }

        var currentReplicas = ReplicasOf(ledger);
        var replicas = currentReplicas.Where(replica => replica != from && (!disklessOnly || state.IsDiskless(replica)) && CanExecuteFor(state, replica, placeInObjectStore, needsObjectLedgers));
        var orderedReplicas = placeInObjectStore
            ? replicas.OrderBy(state.Primaries).ThenBy(static node => node, NodeOrder.Instance)
            : replicas.OrderBy(state.IsUnderPressure).ThenBy(state.Primaries).ThenBy(static node => node, NodeOrder.Instance);
        foreach (var replica in orderedReplicas)
        {
            yield return replica;
        }

        var others = ExecutorsFor(state, placeInObjectStore, needsObjectLedgers).Where(node => node != from && !currentReplicas.Contains(node) && (!disklessOnly || state.IsDiskless(node)));
        var ordered = placeInObjectStore
            ? others.OrderBy(state.Primaries).ThenBy(static node => node, NodeOrder.Instance)
            : others.OrderBy(state.IsUnderPressure).ThenBy(state.Primaries).ThenBy(static node => node, NodeOrder.Instance);
        foreach (var node in ordered)
        {
            yield return node;
        }
    }

    /// <summary>
    /// Sends <c>Replace(S)</c> for every open ledger whose primary claim has ended: its node is dead in the membership
    /// view, or reported the ledger <c>Unserved</c> itself (orleans.md §5.7). Takeovers are never limited and never wait for
    /// room in the map, since their ranges have no primary; their growth is charged to it.
    /// </summary>
    private void SendTakeovers(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref long room, bool preferDiskless)
    {
        foreach (var ledger in state.Open)
        {
            var claim = ledger.Primary.Node;
            var selfTakeover = state.ReportedUnserved(claim, ledger.Id);
            var ended = selfTakeover || state.DeadSince(claim) is not null;
            if (!ended)
            {
                continue;
            }

            if (_prepareInProgress.ContainsKey(ledger.Id))
            {
                EndPrepareInProgress(ledger.Id);
                _ = _prepareFallback.Add(ledger.Id);
            }

            if (!IsEligible(ledger))
            {
                continue;
            }

            var placeInObjectStore = WillPlaceInObjectStore(state, ledger);
            if (placeInObjectStore && !HasObjectLedgers)
            {
                continue;
            }

            if (Successor(state, ledger, claim, selfTakeover, placeInObjectStore, disklessOnly: placeInObjectStore && preferDiskless) is { } executor)
            {
                _ = Send(state, sent, prepares, Shape.Replace(ledger), TransitionPurpose.Takeover, executor, ledger, placeInObjectStore, ref room, reserve: 0, degraded: true);
            }
        }
    }

    /// <summary>Sends each requested WAL-release roll of an open node-backed ledger whose primary (or diskless successor during drain-all) may execute it (OR-I13); never limited in number.</summary>
    private void SendReleaseRolls(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref long room, bool preferDiskless)
    {
        foreach (var ledger in state.Open)
        {
            if (ledger.Location.Value is LedgerLocation.Nodes && _rollRequests.Contains(ledger.Id) && IsEligible(ledger))
            {
                var placeInObjectStore = WillPlaceInObjectStore(state, ledger);
                var executor = placeInObjectStore && preferDiskless && !state.IsDiskless(ledger.Primary.Node)
                    ? Successor(state, ledger, ledger.Primary.Node, selfTakeover: false, placeInObjectStore: true, disklessOnly: true)
                    : CanExecuteFor(state, ledger.Primary.Node, placeInObjectStore, state.HasOverlappingObjectStoreLedger(ledger.Range))
                        ? ledger.Primary.Node
                        : null;
                if (executor is { } runner)
                {
                    _ = Send(state, sent, prepares, Shape.Replace(ledger), TransitionPurpose.WalReleaseRoll, runner, ledger, placeInObjectStore, ref room, TakeoverReserve(state));
                }
            }
        }
    }

    /// <summary>
    /// Ensures no open ledger whose primary already cooperatively released it via <c>PrepareRelease</c> is left
    /// stranded if balancing did not replace it in this pass (for example after controller failover or a budget/load
    /// change after release, orleans.md §5.6, OR-I24).
    /// </summary>
    private void SendReleasedReplacements(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref long room, bool preferDiskless)
    {
        if (_releasedByPrimary.Count == 0)
        {
            return;
        }

        foreach (var ledger in state.Open)
        {
            if (!_releasedByPrimary.TryGetValue(ledger.Id, out var released)
                || released.Primary != ledger.Primary.Node
                || !IsEligible(ledger))
            {
                continue;
            }

            var placeInObjectStore = WillPlaceInObjectStore(state, ledger);
            if (placeInObjectStore && !HasObjectLedgers)
            {
                continue;
            }

            var executor = Successor(state, ledger, ledger.Primary.Node, selfTakeover: false, placeInObjectStore, disklessOnly: placeInObjectStore && preferDiskless)
                ?? Successor(state, ledger, ledger.Primary.Node, selfTakeover: true, placeInObjectStore);
            if (executor is { } target)
            {
                _ = Send(
                    state,
                    sent,
                    prepares,
                    Shape.Replace(ledger, PredecessorReleaseHint.Cooperative),
                    TransitionPurpose.Handoff,
                    target,
                    ledger,
                    placeInObjectStore,
                    ref room,
                    reserve: 0,
                    degraded: state.IsLeaving(ledger.Primary.Node));
            }
        }
    }

    /// <summary>
    /// Returns the room balancing leaves in the map for the takeovers of the node with the most primaries, so one node's
    /// failure never finds the map full (orleans.md §5.6).
    /// </summary>
    private static long TakeoverReserve(ClusterState state) => (long)state.Cache.MaxPrimaries * state.Cache.MaxTakeoverGrowth;

    /// <summary>
    /// Sends the balancing transitions, each within its budget and the room the map's value limit leaves beyond the
    /// takeover reserve: a bootstrap; hand-offs of retired primaries (OR-I3), and of departing nodes' and stopping nodes'
    /// ledgers, within <c>MaxConcurrentHandOffs</c>; repair rolls of a
    /// replica dead past <c>G</c>, retired, or lost, within <c>MaxConcurrentRepairs</c>; rolls for the configuration's
    /// location, <c>N</c>, and <c>K</c> and for shared failure domains; splits toward the target and of hot ledgers; merges
    /// of cold siblings; primary balance; and replica evening (orleans.md §5.6, §5.14).
    /// </summary>
    private void Balance(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref long room, bool preferDiskless)
    {
        var budget = _options.MaxConcurrentTransitions
            - _inProgress.Keys.Count(static entry => entry.IsBalancing)
            - _prepareInProgress.Values.Count(static entry => entry.Sent.IsBalancing);
        var repairs = _options.MaxConcurrentRepairs
            - _inProgress.Keys.Count(static entry => entry.Purpose == TransitionPurpose.Repair)
            - _prepareInProgress.Values.Count(static entry => entry.Sent.Purpose == TransitionPurpose.Repair);
        var departures = _options.MaxConcurrentHandOffs
            - _inProgress.Keys.Count(static entry => entry.Purpose == TransitionPurpose.Departure)
            - _prepareInProgress.Values.Count(static entry => entry.Sent.Purpose == TransitionPurpose.Departure);
        var reserve = TakeoverReserve(state);
        if (state.Map.Ledgers.IsEmpty)
        {
            if (IsDrainingAll)
            {
                return;
            }

            var executors = TargetExecutors(state);
            var minimum = TargetIsObjectStore ? 1 : _options.Replicas;
            if (budget > 0 && TargetReady && !_inProgress.Values.Any(static entry => entry.Closes.IsEmpty) && executors.Length >= minimum)
            {
                _ = Send(state, sent, prepares, Shape.Bootstrap, TransitionPurpose.Bootstrap, executors[0], ledger: null, TargetIsObjectStore, ref room, reserve);
            }

            return;
        }

        foreach (var ledger in state.Open)
        {
            var primary = ledger.Primary.Node;
            if (!IsEligible(ledger) || !state.IsUp(primary))
            {
                continue;
            }

            if (state.IsLeaving(primary))
            {
                // A retired primary's hand-off balances (OR-I3), except when the primary is actively stopping/handing
                // off (including a retired diskful node shutting down in a mixed cluster) or drain-all moves an open
                // node-backed ledger off nodes: those depart within MaxConcurrentHandOffs so MaxConcurrentTransitions == 0
                // never stalls a stopping node or drain-all. Either may keep the ledger's dead replicas (OR-I5).
                var purpose = state.IsRetired(primary) && !state.IsHandingOff(primary) && !state.IsDeparting(primary) && !(IsDrainingAll && ledger.Location.Value is LedgerLocation.Nodes)
                    ? TransitionPurpose.Handoff
                    : TransitionPurpose.Departure;
                var placeInObjectStore = WillPlaceInObjectStore(state, ledger);
                if ((purpose == TransitionPurpose.Handoff ? budget : departures) > 0
                    && (!placeInObjectStore || HasObjectLedgers)
                    && Successor(state, ledger, primary, selfTakeover: false, placeInObjectStore, disklessOnly: placeInObjectStore && preferDiskless) is { } target
                    && Send(state, sent, prepares, Shape.Replace(ledger), purpose, target, ledger, placeInObjectStore, ref room, reserve, degraded: true))
                {
                    _ = purpose == TransitionPurpose.Handoff ? budget-- : departures--;
                }
            }
            else if (RollOut(state, ledger) is var (leaving, purpose)
                && (purpose == TransitionPurpose.Repair ? repairs : purpose == TransitionPurpose.Departure ? departures : budget) > 0)
            {
                var placeInObjectStore = TargetIsObjectStore && WillPlaceInObjectStore(state, ledger);
                if (placeInObjectStore)
                {
                    var primaryCanExecute = (!preferDiskless || state.IsDiskless(primary)) && state.CanExecuteObjectStore(primary);
                    if (purpose == TransitionPurpose.Roll && primaryCanExecute && IsReleasedByPrimary(ledger))
                    {
                        continue;
                    }

                    var executor = !IsReleasedByPrimary(ledger) && primaryCanExecute
                        ? primary
                        : Successor(state, ledger, primary, selfTakeover: false, placeInObjectStore: true, disklessOnly: preferDiskless);
                    if (_objectLedgerLayout is { } layout && executor is { } runner
                        && Record(state, sent, prepares, Shape.Replace(ledger), purpose, runner, ledger, LedgerLocation.ObjectStore.Instance, _options.EffectiveObjectLedgerWindow(layout), ref room, reserve))
                    {
                        if (purpose == TransitionPurpose.Repair)
                        {
                            repairs--;
                        }
                        else if (purpose == TransitionPurpose.Departure)
                        {
                            departures--;
                        }
                        else
                        {
                            budget--;
                        }
                    }
                }
                else
                {
                    var needsObjectLedgers = state.HasOverlappingObjectStoreLedger(ledger.Range);
                    var primaryCanExecute = CanExecuteFor(state, primary, placeInObjectStore: false, needsObjectLedgers);
                    if (purpose == TransitionPurpose.Roll && primaryCanExecute && IsReleasedByPrimary(ledger))
                    {
                        continue;
                    }

                    var executor = !IsReleasedByPrimary(ledger) && primaryCanExecute
                        ? primary
                        : Successor(state, ledger, primary, selfTakeover: false, placeInObjectStore: false);
                    if (executor is { } runner)
                    {
                        var currentReplicas = ReplicasOf(ledger);
                        if (Placement.Place(state, runner, currentReplicas, leaving, _options.Replicas, DegradedPlacementMutant) is not { } replicas)
                        {
                            if (purpose == TransitionPurpose.Repair)
                            {
                                UnplacedRepairs++;
                                LogRepairUnplaced(_logger, ledger.Id, state.QuorumExecutors.Length, _options.Replicas);
                            }
                        }
                        else if (Record(state, sent, prepares, Shape.Replace(ledger), purpose, runner, ledger, new LedgerLocation.Nodes(replicas), _options.SequencerWindow, ref room, reserve))
                        {
                            _ = purpose == TransitionPurpose.Repair ? repairs-- : budget--;
                        }
                    }
                }
            }
        }

        if (IsDrainingAll)
        {
            return;
        }

        SendSplits(state, sent, prepares, ref budget, ref room, reserve);
        SendMerges(state, sent, prepares, ref budget, ref room, reserve);
        BalancePrimaries(state, sent, prepares, ref budget, ref room, reserve);
        EvenReplicas(state, sent, prepares, ref budget, ref room, reserve);
    }

    /// <summary>
    /// Returns the replicas a roll of <paramref name="ledger"/> replaces and its purpose, or <see langword="null"/> when it
    /// needs no roll: a repair of those dead for <c>G</c> or retired, which leave, and of a replica its node reports lost
    /// (LR-I2), at once, the primary's own among them: damage is a replica's, not its node's, so the successor's replica on
    /// the same node is fresh, and the lost replica's node leaves only the ledger which closes; a roll with none leaving,
    /// an empty set, when its location, <c>N</c>, or <c>K</c> differs from the configuration; and on node-backed ledgers, a
    /// roll of a replica other than the primary which shares a failure domain with an earlier one, while a live active node
    /// outside the ledger has a domain it does not use (orleans.md §5.6, §5.14).
    /// </summary>
    private (ImmutableArray<NodeId> Leaving, TransitionPurpose Purpose)? RollOut(ClusterState state, LedgerMetadata ledger)
    {
        if (TargetIsObjectStore)
        {
            if (ledger.Location.Value is LedgerLocation.Nodes nodes)
            {
                var repair = NeedsRepair(state, ledger.Id, nodes.Replicas);
                if (!HasObjectLedgers || state.ObjectStoreExecutors.IsEmpty)
                {
                    // During an object-store outage or before any silo has ObjectLedgersReady, still repair damaged
                    // node-backed ledgers on Nodes rather than stalling durability — except during drain-all, which
                    // strictly suppresses new node-backed placement.
                    return !IsDrainingAll && repair.NeedsRepair ? (repair.Leaving, TransitionPurpose.Repair) : null;
                }

                // Drain-all rolls open node-backed ledgers off nodes using Departure (MaxConcurrentHandOffs) so
                // MaxConcurrentTransitions == 0 never stalls drain-all.
                var purpose = IsDrainingAll
                    ? TransitionPurpose.Departure
                    : repair.NeedsRepair ? TransitionPurpose.Repair : TransitionPurpose.Roll;
                return (repair.Leaving, purpose);
            }

            return !IsDrainingAll && HasObjectLedgers && ledger.K != TargetWindow ? ([], TransitionPurpose.Roll) : null;
        }

        if (ledger.Location.Value is not LedgerLocation.Nodes current)
        {
            return state.QuorumExecutors.Length >= _options.Replicas ? ([], TransitionPurpose.Roll) : null;
        }

        var check = NeedsRepair(state, ledger.Id, current.Replicas);
        if (check.NeedsRepair)
        {
            return (check.Leaving, TransitionPurpose.Repair);
        }

        if (current.Replicas.Length != _options.Replicas || ledger.K != _options.SequencerWindow)
        {
            return ([], TransitionPurpose.Roll);
        }

        // A duplicate domain among the ledger's few replicas first, with no allocation; then whether a live domain is unused.
        for (var index = 1; index < current.Replicas.Length; index++)
        {
            var replica = current.Replicas[index];
            if (replica != ledger.Primary.Node && SharesEarlierDomain(state, current.Replicas, index))
            {
                return HasUnusedDomain(state, current.Replicas) ? ([replica], TransitionPurpose.Roll) : null;
            }
        }

        return null;
    }

    private (ImmutableArray<NodeId> Leaving, bool NeedsRepair) NeedsRepair(ClusterState state, LedgerId ledgerId, ImmutableArray<NodeId> replicas)
    {
        var leaving = ImmutableArray.CreateBuilder<NodeId>();
        var lost = false;
        foreach (var replica in replicas)
        {
            lost |= state.ReportedLost(replica, ledgerId);
            if (state.IsRetired(replica))
            {
                leaving.Add(replica);
            }
            else if (state.DeadSince(replica) is { } since)
            {
                var graceEnds = since + _options.ReplicaGrace;
                if (state.Now >= graceEnds)
                {
                    leaving.Add(replica);
                }
                else if (graceEnds < _nextGrace)
                {
                    _nextGrace = graceEnds;
                }
            }
        }

        return (leaving.ToImmutable(), leaving.Count > 0 || lost);
    }

    /// <summary>Returns whether the replica at <paramref name="index"/> shares a failure domain with an earlier one.</summary>
    private static bool SharesEarlierDomain(ClusterState state, ImmutableArray<NodeId> replicas, int index)
    {
        if (state.DomainOf(replicas[index]) is not { } domain)
        {
            return false;
        }

        for (var earlier = 0; earlier < index; earlier++)
        {
            if (string.Equals(state.DomainOf(replicas[earlier]), domain, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnusedDomain(ClusterState state, ImmutableArray<NodeId> replicas)
    {
        foreach (var domain in state.LiveDomains)
        {
            if (!replicas.Any(replica => string.Equals(state.DomainOf(replica), domain, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the open ranges balancing aims for: <c>LedgersPerNode</c> per live active node eligible for the configured
    /// placement, capped at what the map's value limit holds while each range also lists a closed predecessor (orleans.md §5.6).
    /// </summary>
    private long TargetRanges(ClusterState state)
    {
        var eligible = TargetIsObjectStore
            ? state.LiveActive.Count(state.IsObjectLedgersReady)
            : state.Nodes.DiskfulLiveActive.Length;
        var wanted = (long)_options.LedgersPerNode * eligible;
        var capacity = TargetIsObjectStore && state.Cache.Replicas.Count == 0 ? _objectStoreRangeCapacity : _quorumRangeCapacity;
        if (wanted > capacity && !_warnedCapacity)
        {
            _warnedCapacity = true;
            LogCapacity(_logger, wanted, capacity, _mapCommitter.MaxValueLength);
        }

        return Math.Min(wanted, capacity);
    }

    /// <summary>Splits ledgers, shallowest first, toward the target, and hot ledgers whatever the count.</summary>
    private void SendSplits(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref int budget, ref long room, long reserve)
    {
        if (TargetIsObjectStore && !TargetReady)
        {
            return;
        }

        var target = TargetRanges(state);
        var open = state.Open.Length + _inProgress.Keys.Count(static entry => entry.Purpose == TransitionPurpose.Split);
        var candidates = open < target ? state.Cache.BySplitOrder : [.. state.Open.Where(ledger => state.Load(ledger.Id) > _options.SplitBytesPerSecond)];
        foreach (var ledger in candidates)
        {
            if (budget <= 0)
            {
                return;
            }

            var hot = state.Load(ledger.Id) > _options.SplitBytesPerSecond;
            var needsObjectLedgers = state.HasOverlappingObjectStoreLedger(ledger.Range);
            if ((open < target || hot) && ledger.Range.Depth < KeyRange.MaxDepth && IsEligible(ledger)
                && !IsReleasedByPrimary(ledger)
                && CanExecuteFor(state, ledger.Primary.Node, TargetIsObjectStore, needsObjectLedgers)
                && Send(state, sent, prepares, Shape.Split(ledger), TransitionPurpose.Split, ledger.Primary.Node, ledger, TargetIsObjectStore, ref room, reserve))
            {
                budget--;
                open++;
            }
        }
    }

    /// <summary>Merges cold sibling ledgers which share a primary while the open ranges exceed the target.</summary>
    private void SendMerges(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref int budget, ref long room, long reserve)
    {
        if (TargetIsObjectStore && !TargetReady)
        {
            return;
        }

        var target = TargetRanges(state);
        var open = state.Open.Length;
        var cold = _options.SplitBytesPerSecond / 4;
        foreach (var ledger in state.Open)
        {
            if (budget <= 0 || open <= target)
            {
                return;
            }

            if (ledger.Range.Depth == 0 || state.Map.Find(ledger.Range.Sibling.Prefix) is not { } lookup)
            {
                continue;
            }

            // Each pair is considered once, from its lower ID.
            var sibling = lookup.Open;
            var needsObjectLedgers = state.HasOverlappingObjectStoreLedger(ledger.Range.Parent);
            if (sibling.Range == ledger.Range.Sibling && sibling.Id.Value > ledger.Id.Value && sibling.Primary.Node == ledger.Primary.Node
                && IsEligible(ledger) && IsEligible(sibling)
                && !IsReleasedByPrimary(ledger) && !IsReleasedByPrimary(sibling)
                && CanExecuteFor(state, ledger.Primary.Node, TargetIsObjectStore, needsObjectLedgers)
                && state.Load(ledger.Id) < cold && state.Load(sibling.Id) < cold
                && Send(state, sent, prepares, Shape.Merge(ledger, sibling), TransitionPurpose.Merge, ledger.Primary.Node, ledger, TargetIsObjectStore, ref room, reserve))
            {
                budget--;
                open--;
            }
        }
    }

    /// <summary>
    /// Hands a ledger off from a node with the most primaries to the one with the fewest among those eligible (and under no
    /// disk pressure on node-backed placement), while they differ by more than one, preferring a ledger the target has not
    /// failed, then one which names the target among its replicas. A target's failure is a preference, as for every choice
    /// (orleans.md §5.6): a node which declined while its runtime started, having failed every candidate, is handed one again
    /// rather than balance stalling.
    /// </summary>
    private void BalancePrimaries(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref int budget, ref long room, long reserve)
    {
        if (TargetIsObjectStore && !TargetReady)
        {
            return;
        }

        var executors = TargetExecutors(state);
        while (budget > 0 && executors.Length > 1)
        {
            var nodes = executors.OrderBy(state.Primaries).ThenBy(static node => node, NodeOrder.Instance).ToList();
            var most = nodes[^1];
            var fewest = nodes.FindIndex(node =>
                (TargetIsObjectStore || !state.IsUnderPressure(node))
                && state.IsBalanceTarget(node)
                && _catchUpReady.GetValueOrDefault(node, true)
                && !IsCooperativeBalanceBackedOff(node, state.Now));
            if (fewest < 0)
            {
                return;
            }

            var target = nodes[fewest];
            if (state.Primaries(most) - state.Primaries(target) <= 1)
            {
                return;
            }

            if (state.Open
                .Where(ledger => ledger.Primary.Node == most
                    && IsEligible(ledger)
                    && (TargetIsObjectStore || !state.HasOverlappingObjectStoreLedger(ledger.Range) || state.IsObjectLedgersReady(target))
                    && !_rejected.Contains(BanOf(TransitionPurpose.Handoff, [ledger.Id])))
                .OrderBy(ledger => HasFailed(ledger.Id, target))
                .ThenBy(ledger => !IsReleasedByPrimary(ledger))
                .ThenBy(ledger => !ledger.IsOnNode(target))
                .Select(static ledger => (LedgerMetadata?)ledger)
                .FirstOrDefault() is not { } ledger)
            {
                return;
            }

            if (!Send(state, sent, prepares, Shape.Replace(ledger), TransitionPurpose.Handoff, target, ledger, TargetIsObjectStore, ref room, reserve))
            {
                return;
            }

            budget--;
        }
    }

    private bool IsCooperativeBalanceBackedOff(NodeId node, DateTimeOffset now)
    {
        if (_cooperativeBalanceBackoff.TryGetValue(node, out var backoff) && now < backoff.Until)
        {
            if (backoff.Until < _nextGrace)
            {
                _nextGrace = backoff.Until;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Evens the nodes' replica counts for node-backed ledgers (orleans.md §5.6), best effort, within the balancing budget:
    /// while any non-diskless node holds more than one replica above the fewest any node under no disk pressure holds, it
    /// rolls one replica off such a node, trying each, most replicas first, until one rolls. Skipped when object-store
    /// placement is configured, as node-backed ledgers roll to object storage instead.
    /// </summary>
    private void EvenReplicas(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref int budget, ref long room, long reserve)
    {
        if (TargetIsObjectStore)
        {
            return;
        }

        while (budget > 0 && EvenOnce(state, sent, prepares, ref room, reserve))
        {
            budget--;
        }
    }

    /// <summary>
    /// Sends one evening roll, from the first node above the bound which can give up a replica (<see cref="EvenReplicas"/>).
    /// The destinations, the non-diskless nodes under no disk pressure by fewest replicas, are ordered once: each node above
    /// the bound takes those holding at least two fewer than it, a prefix, and the first index of each domain among them
    /// tells which domains the prefix holds.
    /// </summary>
    private bool EvenOnce(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, ref long room, long reserve)
    {
        var destinations = state.QuorumExecutors
            .Where(node => !state.IsUnderPressure(node) && state.IsBalanceTarget(node))
            .OrderBy(state.Replicas)
            .ThenBy(static node => node, NodeOrder.Instance)
            .ToList();
        if (destinations.Count == 0)
        {
            return false;
        }

        var firstWithoutDomain = int.MaxValue;
        var firstOfDomain = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < destinations.Count; index++)
        {
            if (state.DomainOf(destinations[index]) is { } domain)
            {
                _ = firstOfDomain.TryAdd(domain, index);
            }
            else
            {
                firstWithoutDomain = Math.Min(firstWithoutDomain, index);
            }
        }

        var fewest = state.Replicas(destinations[0]);
        foreach (var source in state.QuorumExecutors.Where(node => state.Replicas(node) > fewest + 1).OrderByDescending(state.Replicas).ThenBy(static node => node, NodeOrder.Instance))
        {
            var bound = state.Replicas(source) - 2;
            var count = destinations.FindLastIndex(node => state.Replicas(node) <= bound) + 1;
            if (RollOff(state, sent, prepares, source, destinations, count, firstWithoutDomain, firstOfDomain, ref room, reserve))
            {
                return true;
            }

            if (ReplicaEveningMutant)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Rolls one of <paramref name="source"/>'s replicas onto the first of the <paramref name="count"/> leading
    /// <paramref name="destinations"/> which keeps its ledger's shared domains, if any ledger allows it.
    /// </summary>
    private bool RollOff(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, NodeId source, List<NodeId> destinations, int count, int firstWithoutDomain, Dictionary<string, int> firstOfDomain, ref long room, long reserve)
    {
        foreach (var ledger in state.Cache.ReplicatedBy(source))
        {
            if (ledger.Primary.Node == source || !IsEligible(ledger) || IsReleasedByPrimary(ledger) || !AllExecute(state, ledger.Replicas, source)
                || !CanExecuteFor(state, ledger.Primary.Node, placeInObjectStore: false, state.HasOverlappingObjectStoreLedger(ledger.Range))
                || !MayTake(state, ledger.Replicas, source, count, firstWithoutDomain, firstOfDomain))
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var target = destinations[index];
                if (!ledger.Replicas.Contains(target) && KeepsSharedDomains(state, ledger.Replicas, source, target))
                {
                    if (Record(state, sent, prepares, Shape.Replace(ledger), TransitionPurpose.Roll, ledger.Primary.Node, ledger, new LedgerLocation.Nodes(Replace(ledger.Replicas, source, target)), _options.SequencerWindow, ref room, reserve))
                    {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether every one of <paramref name="replicas"/> but <paramref name="source"/> may be given quorum work.</summary>
    private static bool AllExecute(ClusterState state, ImmutableArray<NodeId> replicas, NodeId source)
    {
        foreach (var replica in replicas)
        {
            if (replica != source && !state.CanExecuteQuorum(replica))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether some one of the <paramref name="count"/> leading destinations, by the domains they hold, could take
    /// <paramref name="source"/>'s place among <paramref name="replicas"/> without more of them sharing a failure domain
    /// (<see cref="KeepsSharedDomains"/>), so a ledger none could take costs no scan of the destinations.
    /// </summary>
    private static bool MayTake(ClusterState state, ImmutableArray<NodeId> replicas, NodeId source, int count, int firstWithoutDomain, Dictionary<string, int> firstOfDomain)
    {
        if (firstWithoutDomain < count || state.DomainOf(source) is { } leaving && HeldByOther(state, replicas, source, leaving))
        {
            return true;
        }

        foreach (var (domain, first) in firstOfDomain)
        {
            if (first < count && !HeldByOther(state, replicas, source, domain))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether <paramref name="target"/> taking <paramref name="source"/>'s place among <paramref name="replicas"/>
    /// leaves no more of them sharing a failure domain: the target has no domain, or one no other replica uses, or the
    /// source's own domain was shared already.
    /// </summary>
    private static bool KeepsSharedDomains(ClusterState state, ImmutableArray<NodeId> replicas, NodeId source, NodeId target) =>
        state.DomainOf(target) is not { } domain
        || !HeldByOther(state, replicas, source, domain)
        || state.DomainOf(source) is { } leaving && HeldByOther(state, replicas, source, leaving);

    /// <summary>Returns whether a replica other than <paramref name="source"/> is in <paramref name="domain"/>.</summary>
    private static bool HeldByOther(ClusterState state, ImmutableArray<NodeId> replicas, NodeId source, string domain)
    {
        foreach (var replica in replicas)
        {
            if (replica != source && string.Equals(state.DomainOf(replica), domain, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns <paramref name="replicas"/> without <paramref name="source"/>, in their order, then <paramref name="target"/>.</summary>
    private static ImmutableArray<NodeId> Replace(ImmutableArray<NodeId> replicas, NodeId source, NodeId target)
    {
        var replaced = ImmutableArray.CreateBuilder<NodeId>(replicas.Length);
        foreach (var replica in replicas)
        {
            if (replica != source)
            {
                replaced.Add(replica);
            }
        }

        replaced.Add(target);
        return replaced.MoveToImmutable();
    }

    /// <summary>Places the transition's target location (node replicas per OR-I5, or object storage per orleans.md §5.14), degraded only when asked, and records it (<see cref="Record"/>).</summary>
    private bool Send(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, Shape shape, TransitionPurpose purpose, NodeId executor, LedgerMetadata? ledger, bool placeInObjectStore, ref long room, long reserve, bool degraded = false)
    {
        if (placeInObjectStore)
        {
            return _objectLedgerLayout is { } layout
                && Record(state, sent, prepares, shape, purpose, executor, ledger, LedgerLocation.ObjectStore.Instance, _options.EffectiveObjectLedgerWindow(layout), ref room, reserve);
        }

        if (Placement.Place(state, executor, ReplicasOf(ledger), [], _options.Replicas, degraded || DegradedPlacementMutant) is not { } replicas
            || !Record(state, sent, prepares, shape, purpose, executor, ledger, new LedgerLocation.Nodes(replicas), _options.SequencerWindow, ref room, reserve))
        {
            return false;
        }

        if (ledger is { } replaced && replicas.Any(state.IsKeepableDegradedReplica))
        {
            LogDegraded(_logger, purpose, replaced.Id, replicas.Count(state.IsKeepableDegradedReplica), state.QuorumExecutors.Length, _options.Replicas);
        }

        return true;
    }

    /// <summary>
    /// Unless the transition is banned, a roll which changes nothing or adds a node under disk pressure, or past the room
    /// the map leaves beyond <paramref name="reserve"/>, records it with <paramref name="location"/> and <paramref name="k"/>
    /// in progress with its growth, which it charges to <paramref name="room"/>, and adds it to the pass. For a planned
    /// replacement off a live serving primary to another executor, emits a cooperative <see cref="SentPrepareRelease"/>
    /// first and emits <see cref="Transition.Replace"/> with <see cref="PredecessorReleaseHint.Cooperative"/> once
    /// released (or <see cref="PredecessorReleaseHint.TakenOver"/> on fallback, orleans.md §5.6, §5.7, OR-I24).
    /// </summary>
    private bool Record(ClusterState state, ImmutableArray<SentTransition>.Builder sent, ImmutableArray<SentPrepareRelease>.Builder prepares, Shape shape, TransitionPurpose purpose, NodeId executor, LedgerMetadata? ledger, LedgerLocation location, int k, ref long room, long reserve)
    {
        if (_rejected.Contains(BanOf(purpose, shape.Closes)))
        {
            return false;
        }

        if (purpose == TransitionPurpose.Roll && ledger is { } rolled && !IsDiscretionaryRollAllowed(state, rolled, location, k))
        {
            return false;
        }

        var choice = new TransitionChoice(executor, location, k, state.Nodes.Registry.Revision, state.Map.Epoch);
        var needsCooperativeRelease = shape.IsReplace
            && ledger is { } closing
            && purpose != TransitionPurpose.Takeover
            && executor != closing.Primary.Node
            && state.IsUp(closing.Primary.Node)
            && !state.ReportedUnserved(closing.Primary.Node, closing.Id);

        Transition transition;
        if (needsCooperativeRelease && ledger is { } closingLedger)
        {
            if (_releasedByPrimary.TryGetValue(closingLedger.Id, out var released) && released.Primary == closingLedger.Primary.Node)
            {
                transition = new Transition.Replace(closingLedger.Id, choice, PredecessorReleaseHint.Cooperative);
            }
            else if (_prepareFallback.Contains(closingLedger.Id))
            {
                transition = new Transition.Replace(closingLedger.Id, choice, PredecessorReleaseHint.TakenOver);
            }
            else
            {
                var estimatedGrowth = state.Map.GrowthOf(shape.Make(choice));
                if (estimatedGrowth > room - reserve)
                {
                    return false;
                }

                room -= estimatedGrowth;
                var prepare = new SentPrepareRelease(
                    closingLedger.Primary.Node,
                    new PrepareReleaseRequest(closingLedger.Id, closingLedger.Range, _options.PrepareReleaseTimeout),
                    purpose);
                _prepareInProgress.Add(closingLedger.Id, new PendingPrepare(prepare, state.Now, executor, shape.Opens.Length, estimatedGrowth));
                _ledgersInProgress[closingLedger.Id] = _ledgersInProgress.GetValueOrDefault(closingLedger.Id) + 1;
                prepares.Add(prepare);
                state.MovePrimaries(closingLedger.Primary.Node, executor, shape.Opens.Length);
                return true;
            }
        }
        else
        {
            transition = shape.Make(choice);
        }

        var growth = state.Map.GrowthOf(transition);
        if (purpose != TransitionPurpose.Takeover && growth > room - reserve)
        {
            return false;
        }

        room -= growth;
        var entry = new SentTransition(transition, purpose);
        _inProgress.Add(entry, new Pending(shape.Closes, state.Now, growth));
        foreach (var closed in shape.Closes)
        {
            _ledgersInProgress[closed] = _ledgersInProgress.GetValueOrDefault(closed) + 1;
        }

        sent.Add(entry);
        state.MovePrimaries(ledger?.Primary.Node, executor, shape.Opens.Length);
        state.CountReplicas(ReplicasOf(ledger), ReplicasOf(location));
        return true;
    }

    /// <summary>
    /// Returns whether a roll of <paramref name="ledger"/> onto <paramref name="location"/> with window <paramref name="k"/>
    /// changes it and adds no node under disk pressure: a roll is discretionary, so it waits for an unpressured node rather
    /// than adding to a pressured one's WAL (orleans.md §5.6, §5.9); takeovers and repairs place on pressured nodes when only
    /// those remain.
    /// </summary>
    private static bool IsDiscretionaryRollAllowed(ClusterState state, LedgerMetadata ledger, LedgerLocation location, int k)
    {
        if (location.Equals(ledger.Location) && ledger.K == k)
        {
            return false;
        }

        if (location.Value is LedgerLocation.Nodes newNodes)
        {
            foreach (var replica in newNodes.Replicas)
            {
                if (state.IsUnderPressure(replica) && !ledger.IsOnNode(replica))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns, for each node, the closed ledgers it is to drain too (orleans.md §5.6, §5.14): a closed node-backed ledger
    /// whose drain has made no progress for <c>ClosedDrainTimeout</c> is drained by one more replica which drains after its
    /// first for each timeout which passed, and by its first replica which drains at once when its first replica does not
    /// drain. A closed object-store ledger has no replicas and is assigned to its former primary first (while live and
    /// draining, so its local cache serves the drain), then to one more live draining node for each timeout which passed.
    /// </summary>
    private FrozenDictionary<NodeId, ImmutableArray<LedgerId>> DrainToo(ClusterState state)
    {
        var drains = new Dictionary<NodeId, ImmutableArray<LedgerId>.Builder>();
        ImmutableArray<NodeId>? objectStoreDrainers = null;
        foreach (var ledger in state.Cache.Closed)
        {
            var since = _closedSince.GetValueOrDefault(ledger.Id, state.Now);
            var progress = _drainProgress.TryGetValue(ledger.Id, out var at) && at > since ? at : since;
            var elapsed = Math.Max(0L, (state.Now - progress).Ticks);
            if (ledger.Location.Value is LedgerLocation.Nodes nodes)
            {
                var extra = (int)Math.Min(nodes.Replicas.Length, elapsed / _options.ClosedDrainTimeout.Ticks);
                NodeId? replacement = null;
                if (!state.Drains(nodes.Replicas[0]))
                {
                    // Its first replica drains it first, and does not drain: the first replica which drains does at once.
                    foreach (var replica in nodes.Replicas)
                    {
                        if (state.Drains(replica))
                        {
                            replacement = replica;
                            AddDrain(drains, replica, ledger.Id);
                            break;
                        }
                    }
                }

                var emitted = 0;
                for (var index = 1; index < nodes.Replicas.Length && emitted < extra; index++)
                {
                    var replica = nodes.Replicas[index];
                    if (!state.Drains(replica))
                    {
                        continue;
                    }

                    emitted++;
                    if (replica != replacement)
                    {
                        AddDrain(drains, replica, ledger.Id);
                    }
                }
            }
            else
            {
                objectStoreDrainers ??= [.. state.LiveActive.Where(state.DrainsObjectStore)];
                var candidates = objectStoreDrainers.Value;
                if (candidates.IsEmpty)
                {
                    continue;
                }

                var count = (int)Math.Min(candidates.Length, 1L + elapsed / _options.ClosedDrainTimeout.Ticks);
                var primaryIndex = candidates.BinarySearch(ledger.Primary.Node, NodeOrder.Instance);
                var emitted = 0;
                if (primaryIndex >= 0)
                {
                    AddDrain(drains, ledger.Primary.Node, ledger.Id);
                    emitted++;
                }

                if (emitted < count)
                {
                    var otherCount = primaryIndex >= 0 ? candidates.Length - 1 : candidates.Length;
                    if (otherCount > 0)
                    {
                        var start = (int)(ledger.Id.Value % (ulong)otherCount);
                        for (var step = 0; step < otherCount && emitted < count; step++)
                        {
                            var index = (start + step) % otherCount;
                            if (primaryIndex >= 0 && index >= primaryIndex)
                            {
                                index++;
                            }

                            AddDrain(drains, candidates[index], ledger.Id);
                            emitted++;
                        }
                    }
                }
            }
        }

        return drains.ToFrozenDictionary(static entry => entry.Key, static entry => entry.Value.ToImmutable());
    }

    private static void AddDrain(Dictionary<NodeId, ImmutableArray<LedgerId>.Builder> drains, NodeId node, LedgerId ledger)
    {
        if (!drains.TryGetValue(node, out var list))
        {
            drains[node] = list = ImmutableArray.CreateBuilder<LedgerId>();
        }

        list.Add(ledger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Controller sends {Purpose} {Transition} to node {Executor}.")]
    private static partial void LogSent(ILogger logger, TransitionPurpose purpose, Transition transition, NodeId executor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Controller sends cooperative PrepareRelease ({Purpose}) for ledger {Ledger} to primary {Node} with deadline {Deadline}.")]
    private static partial void LogPrepareReleaseSent(ILogger logger, TransitionPurpose purpose, LedgerId ledger, NodeId node, TimeSpan deadline);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The compaction of partition {Partition} on node {Assignee} found it corrupt: {Reason}. Corruption of a verified archive object lies outside the fault model (system-model §4); the controller assigns the partition no more in this activation.")]
    private static partial void LogCorruptPartition(ILogger logger, KeyRange partition, NodeId assignee, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The ledger map committer rejected {Transition} as a defect: {Reason}. The controller sends it no more in this activation.")]
    private static partial void LogRejected(ILogger logger, Transition transition, LedgerMapRejection reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The repair of ledger {Ledger} found {Nodes} nodes which may be given work, fewer than its {Replicas} replicas; it waits for more.")]
    private static partial void LogRepairUnplaced(ILogger logger, LedgerId ledger, int nodes, int replicas);

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Purpose} of ledger {Ledger} keeps {Degraded} dead or leaving replicas: {Nodes} nodes may be given work, fewer than its {Replicas} replicas; repair rolls them out after their grace if they do not return.")]
    private static partial void LogDegraded(ILogger logger, TransitionPurpose purpose, LedgerId ledger, int degraded, int nodes, int replicas);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The configured {Wanted} open ranges do not fit the ledger map's value limit of {Limit} bytes; the controller balances toward {Capacity}.")]
    private static partial void LogCapacity(ILogger logger, long wanted, int capacity, int limit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drain-all active at registry revision {DrainAllRevision}: rolling open node-backed ledgers onto the object store and suppressing new node-backed placement.")]
    private static partial void LogDrainAllStarted(ILogger logger, ulong drainAllRevision);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drain-all ended: restoring configured ledger placement.")]
    private static partial void LogDrainAllEnded(ILogger logger);

    /// <summary>A transition in progress: the ledgers it closes, when it was sent, its growth of the map, and the epoch its verdict named.</summary>
    private sealed class Pending(ImmutableArray<LedgerId> closes, DateTimeOffset sentAt, int growth)
    {
        public ImmutableArray<LedgerId> Closes { get; } = closes;

        public DateTimeOffset SentAt { get; } = sentAt;

        public int Growth { get; } = growth;

        public ulong? DecidedAt { get; set; }
    }

    /// <summary>A cooperative <c>PrepareRelease</c> in progress before a planned replacement handoff.</summary>
    private sealed record PendingPrepare(
        SentPrepareRelease Sent,
        DateTimeOffset SentAt,
        NodeId TargetExecutor,
        int TargetOpens,
        int EstimatedGrowth);
}
