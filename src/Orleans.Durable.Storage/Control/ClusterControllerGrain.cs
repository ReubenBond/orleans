using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The controller grain (orleans.md §5.6): it hosts the node registry's, the ledger map's, and, on the LSM, the manifest's
/// committers, and the cluster's <see cref="ClusterController"/>, whose passes it runs on a timer and whose transitions and
/// compaction assignments it sends to the node control grains.
/// </summary>
/// <remarks>
/// <para>
/// Its activation reads only the map, which gates its passes; it reads the manifest apart, retrying with backoff, so an
/// object store which does not answer delays only compaction and drops, never registrations, commits, reports, or
/// takeovers.
/// </para>
/// <para>
/// Its turns await nothing but enqueueing and sends: a commit call only submits to a committer and awaits its outcome, and
/// always interleaves; a pass sends what it chose without awaiting the replies, and each reply ends in a turn of its own.
/// The controller's state changes only in synchronous segments of its turns, and no invariant of it spans an await. A pass
/// runs on the timer, and once more, in a turn of its own which every input before it shares, when the controller says an
/// input made one due (<see cref="ClusterController.PassDue"/>): a reply which freed budget, a membership change which ends
/// or departs a node, a hand-off or retirement request, or a newly unserved ledger. It holds no state the committers'
/// registers or the silos' reports and the requests their callers repeat do not rebuild, so a second activation costs only
/// superseded work (OR-I12). It executes what a pass returns: retirements, each in flight once, to the node registry
/// committer, and the floor raise, which a pass returns while the registry view holds a retirement above the map view's
/// floor, to the ledger map committer, reporting each end to the controller. Its registry view is the newer of the silo
/// resolver's and the registry committer's, so a retirement it wrote shows at once.
/// </para>
/// <para>
/// Its committers, its timer, and its background reads run in no caller's execution context: Orleans runs
/// <c>OnActivateAsync</c> in the activating call's, its <c>RequestContext</c> and an activation <c>Activity</c>, so they
/// are created with flow suppressed (conventions.md §12). Once deactivating, it refuses a submission before handing it to a
/// committer with <see cref="ControllerInactiveException"/>, and its replies and timers run nothing.
/// </para>
/// </remarks>
[ControllerPlacement]
internal sealed partial class ClusterControllerGrain(
    IMetaStore metaStore,
    DurableStorageOptions node,
    ControlOptions control,
    NodeRegistryResolver registry,
    IClusterMembershipService membership,
    ILocalSiloDetails silo,
    SiloArchive archive,
    SiloObjectLedgers objectLedgers,
    SweepHost sweeps,
    TimeProvider timeProvider,
    ILoggerFactory loggers,
    LocalStreamRegistration? localStreams = null) : Grain, IClusterControllerGrain
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = loggers.CreateLogger<ClusterControllerGrain>();
    private readonly Dictionary<NodeId, DateTimeOffset> _deadSince = [];
    private readonly CancellationTokenSource _deactivating = new();

    // Orleans completes OnActivateAsync before it delivers any call.
    private NodeRegistryCommitter? _registryCommitter;
    private LedgerMapCommitter? _mapCommitter;
    private LedgerMapView? _map;
    private ulong? _lastObjectSweepEpoch;
    private DateTimeOffset _lastObjectSweepAt;
    private ManifestCommitter? _manifestCommitter;
    private ClusterController? _controller;
    private LedgerDropper? _dropper;
    private Task _drop = Task.CompletedTask;
    private CoalescedPass? _pass;

    // The activation's meter, which ends with it (NodeMetrics says why it is no factory's).
    private Meter? _meter;

    // Whether the controller has observed a registry value, which a report's reply needs.
    private bool _observed;

    // Whether the activation asked to migrate to a silo whose archive gate passed, while its own has not.
    private bool _migrating;

    private ClusterController Controller => _deactivating.IsCancellationRequested || _controller is not { } controller
        ? throw new ControllerInactiveException("The controller grain is not active.")
        : controller;

    private NodeRegistryCommitter RegistryCommitter => _deactivating.IsCancellationRequested || _registryCommitter is not { } committer
        ? throw new ControllerInactiveException("The controller grain is not active.")
        : committer;

    /// <inheritdoc/>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var registryRegister = await metaStore.GetRegisterAsync(node.Cluster, NodeRegistryValue.RegisterName, cancellationToken).ConfigureAwait(true);
        var mapRegister = await metaStore.GetRegisterAsync(node.Cluster, LedgerMapRegister.Name, cancellationToken).ConfigureAwait(true);
        var map = await LedgerMapView.OpenAsync(mapRegister, cancellationToken).ConfigureAwait(true);
        _map = map;
        using (ExecutionContext.SuppressFlow())
        {
            _registryCommitter = new NodeRegistryCommitter(registryRegister, NodeRegistryCodec.MaxValueLength, loggers.CreateLogger<NodeRegistryCommitter>());
            _mapCommitter = new LedgerMapCommitter(mapRegister, node.LedgerMapMaxBytes, map);

            // The archive store is taken once the silo's archive gate passes, by the kind the gate's check found the
            // registry's (OR-I20): until then the controller does no archive work, and a refused gate deactivates it.
            _controller = new ClusterController(control, map, _mapCommitter, archive: null, timeProvider, loggers.CreateLogger<ClusterController>(), hasLocalStreams: localStreams?.HasLocalStreams ?? false);
            _pass = new CoalescedPass(() => _ = PassAsync(CancellationToken.None));
            _ = this.RegisterGrainTimer(PassAsync, new GrainTimerCreationOptions(control.BalanceInterval, control.BalanceInterval) { KeepAlive = true });
            _ = FollowGateAsync(_controller, map, _deactivating.Token);
            if (objectLedgers.IsConfigured)
            {
                _ = FollowObjectLedgersGateAsync(_controller, map, _deactivating.Token);
                _ = FollowMapForObjectSweepsAsync(map, _deactivating.Token);
            }

            _ = FollowMembershipAsync(_deactivating.Token);
        }

        Observe();
        _meter = new Meter(DurableStorageMeter.Name);
        _ = _meter.CreateObservableGauge("orleans.durable.storage.cluster.ledgers", () => CountLedgers(map.Current), "{ledger}", "The ledgers in the map, by status.");
    }

    /// <inheritdoc/>
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await _deactivating.CancelAsync().ConfigureAwait(true);
        sweeps.CancelObjectLedgers();
        if (_registryCommitter is { } registryCommitter)
        {
            await registryCommitter.DisposeAsync().ConfigureAwait(true);
        }

        if (_mapCommitter is { } mapCommitter)
        {
            await mapCommitter.DisposeAsync().ConfigureAwait(true);
        }

        if (_manifestCommitter is { } manifestCommitter)
        {
            await manifestCommitter.DisposeAsync().ConfigureAwait(true);
        }

        _meter?.Dispose();
        _deactivating.Dispose();
    }

    /// <summary>Returns the open and the closed ledgers of <paramref name="map"/>, tagged by <c>status</c> (orleans.md §8).</summary>
    private static Measurement<int>[] CountLedgers(LedgerMapValue map)
    {
        var open = map.Ledgers.Count(static ledger => ledger.IsOpen);
        return [new(open, new KeyValuePair<string, object?>("status", "open")), new(map.Ledgers.Length - open, new KeyValuePair<string, object?>("status", "closed"))];
    }

    /// <inheritdoc/>
    public Task<NodeRegistrationOutcome> RegisterNodeAsync(NodeRegistryEntry entry, ArchiveStoreIdentity store, NodeId? nodeToReplace, ObjectLedgerStoreId? objectLedgerStore = null) =>
        RegistryCommitter.RegisterAsync(entry, store, nodeToReplace, objectLedgerStore);

    /// <inheritdoc/>
    /// <exception cref="ControllerInactiveException">
    /// The change is a drop, and the grain's silo has not passed its archive gate, by whose kind a drop is accepted.
    /// </exception>
    /// <exception cref="ArgumentException">On the LSM, the change is a drop, which only the grain's own dropper submits (orleans.md §5.6).</exception>
    public Task<LedgerMapCommitOutcome> CommitLedgerMapAsync(LedgerMapChange change)
    {
        if (change.Value is LedgerMapChange.Drop)
        {
            var kind = archive.Gate.PassedKind;
            if (kind == ArchiveStoreKind.Lsm)
            {
                throw new ArgumentException("On the LSM a drop is the controller grain's own: its dropper, which reads the manifest, is a drop's one authority (orleans.md §5.6).", nameof(change));
            }

            if (kind != ArchiveStoreKind.Logs)
            {
                // OR-I20: a drop rests on the archive's evidence, which a silo whose gate has not passed may not take.
                throw new ControllerInactiveException("The controller grain's silo has not passed its archive gate; the drop was not submitted.");
            }

            return CommitDropAsync(change);
        }

        return Controller.CommitLedgerMapAsync(change);
    }

    private async Task<LedgerMapCommitOutcome> CommitDropAsync(LedgerMapChange change)
    {
        var outcome = await Controller.CommitLedgerMapAsync(change).ConfigureAwait(true);
        if (!_deactivating.IsCancellationRequested)
        {
            PassIfDue();
        }

        return outcome;
    }

    /// <inheritdoc/>
    public async Task<ManifestCommitOutcome> CommitManifestAsync(ManifestChange change)
    {
        if (!Controller.KeepsManifest)
        {
            // OR-I20: the silo writes the LSM's manifest only once its check of the store's record passed and the controller
            // adopted the LSM, which the grain's turns order before this one: a change in the window between the gate's pass
            // and the adoption is refused as inactive, not as a store which keeps no manifest.
            throw new ControllerInactiveException("The controller grain has not adopted the LSM, whose manifest it commits only once its silo passed its archive gate; the manifest change was not submitted.");
        }

        var outcome = await Controller.CommitManifestAsync(change).ConfigureAwait(true);
        if (outcome.Value is ManifestCommitOutcome.Applied && !_deactivating.IsCancellationRequested)
        {
            // The value the committer applied may hold an overfull partition, or complete a ledger which drops.
            SendCompactions(Controller.AssignCompactions());
            Drop();
        }

        return outcome;
    }

    /// <inheritdoc/>
    public Task<NodeReportReply> ReportAsync(NodeReport report)
    {
        if (!_observed)
        {
            // A report answered over an unobserved registry would be judged against no node (orleans.md §5.6, OR-I21).
            Observe();
        }

        var reply = Controller.Report(report);
        PassIfDue();
        MigrateIfStranded(report);
        return Task.FromResult(reply);
    }

    /// <inheritdoc/>
    /// <remarks>*Not yet in code*: its caller, a silo's WAL retention, which repeats it until the map shows the roll (row 4.4).</remarks>
    public Task RequestRollAsync(LedgerId ledger)
    {
        Controller.RequestRoll(ledger);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task HandOffAsync(NodeId node, int generation)
    {
        Controller.RequestHandOff(node, generation);
        PassIfDue();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RetireNodeAsync(NodeId node)
    {
        Observe();
        Controller.RequestRetirement(node);
        PassIfDue();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<ImmutableArray<OrdinalNode>> ScaleDownAsync(int replicas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicas, control.Replicas);
        await ReadRegistryAsync().ConfigureAwait(true);
        var nodes = Controller.RequestScaleDown(replicas);
        PassIfDue();
        return nodes;
    }

    /// <inheritdoc/>
    public async Task<ImmutableArray<OrdinalNode>> RetireVolumeAsync(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        await ReadRegistryAsync().ConfigureAwait(true);
        var nodes = Controller.RequestVolumeRetirement(ordinal);
        PassIfDue();
        return nodes;
    }

    /// <inheritdoc/>
    public async Task<NodeStanding> GetRetirementAsync(NodeId node)
    {
        if (node == default)
        {
            throw new ArgumentException("A standing names a node.", nameof(node));
        }

        Observe();
        if (Controller.Retirement(node).Value is NodeStanding.NotRegistered or NodeStanding.Active)
        {
            // Only a value read now answers that the registry holds no entry of the node, or holds it active: a view older
            // than the value an earlier answer came from, as a moved activation's silo may hold, would take it back (OR-I19).
            await ReadRegistryAsync().ConfigureAwait(true);
        }

        return await Controller.RetirementAsync(node, _deactivating.Token).ConfigureAwait(true);
    }

    /// <inheritdoc/>
    public async Task<DrainAllStartOutcome> StartDrainAllAsync()
    {
        var controller = Controller;
        await ReadRegistryAsync().ConfigureAwait(true);
        var validation = controller.ValidateStartDrainAll(objectLedgers.IsConfigured);
        if (validation is not DrainAllStartOutcome.Started)
        {
            return validation;
        }

        if (controller.IsDrainingAll)
        {
            if (!_deactivating.IsCancellationRequested)
            {
                PassIfDue();
            }

            return DrainAllStartOutcome.Started.Instance;
        }

        var outcome = await RegistryCommitter.StartDrainAllAsync(_deactivating.Token).ConfigureAwait(true);
        if (!_deactivating.IsCancellationRequested)
        {
            Observe();
            PassIfDue();
        }

        return outcome switch
        {
            NodeDrainAllStartOutcome.Started => DrainAllStartOutcome.Started.Instance,
            NodeDrainAllStartOutcome.ObjectLedgersNotConfigured => DrainAllStartOutcome.ObjectLedgersNotConfigured.Instance,
            null => throw new UnreachableException("conventions.md §15: a drain-all start answers a union which holds a case."),
        };
    }

    /// <inheritdoc/>
    public async Task<bool> CancelDrainAllAsync()
    {
        _ = Controller;
        var outcome = await RegistryCommitter.CancelDrainAllAsync(_deactivating.Token).ConfigureAwait(true);
        if (!_deactivating.IsCancellationRequested)
        {
            Observe();
            PassIfDue();
        }

        return outcome switch
        {
            NodeDrainAllCancelOutcome.Cancelled => true,
            NodeDrainAllCancelOutcome.NotActive => false,
            null => throw new UnreachableException("conventions.md §15: a drain-all cancel answers a union which holds a case."),
        };
    }

    /// <inheritdoc/>
    public Task<DrainAllStatus> GetDrainAllAsync()
    {
        var controller = Controller;
        return controller.DrainAllAsync(ReadRegisteredNodesAsync, _deactivating.Token);
    }

    private async Task<RegisteredNodes> ReadRegisteredNodesAsync(CancellationToken cancellationToken)
    {
        var value = await RegistryCommitter.ReadAsync(cancellationToken).ConfigureAwait(true);
        if (!_deactivating.IsCancellationRequested)
        {
            Observe();
        }

        return value.ToRegisteredNodes();
    }

    /// <inheritdoc/>
    public Task<bool> ResolveCorruptPartitionAsync(KeyRange partition)
    {
        Observe();
        var held = Controller.ResolveCorruptPartition(partition);
        if (held)
        {
            SendCompactions(Controller.AssignCompactions());
        }

        return Task.FromResult(held);
    }

    /// <summary>The pass timer's turn: observes the nodes and runs a pass.</summary>
    private Task PassAsync(CancellationToken cancellationToken)
    {
        if (!_deactivating.IsCancellationRequested)
        {
            Observe();
            Pass();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves the activation off a silo whose archive gate—or object-ledger gate, when object-store ledgers are selected,
    /// locally configured, or recorded in the node registry—has not passed once a node reports that its own passed (OR-I20,
    /// OL-I1, orleans.md §5.6, §5.14): it hints that node's silo, which the registry view names, and migrates when idle, so
    /// a silo whose check of the store's record waits, as through a store outage which reaches it alone, or which lacks
    /// object-store ledger configuration in a cluster that has it, never holds the cluster's control work. A refused gate
    /// deactivates the activation instead (<see cref="FollowGateAsync"/>, <see cref="FollowObjectLedgersGateAsync"/>). The
    /// report must be of the silo the registry view names, whose generation is the report's, so a late report of a stopped
    /// silo never names its successor, and of a node which is not handing off, whose silo is stopping or refused. The
    /// placement director takes the hint only when the silo is compatible, and otherwise falls back to a silo whose gate it
    /// does not know, which may not have passed: the next report of a node whose gate passed then moves the activation again.
    /// </summary>
    private void MigrateIfStranded(NodeReport report)
    {
        var needsObjectLedgers = control.LedgerPlacement == LedgerPlacement.ObjectStore
            || objectLedgers.IsConfigured
            || registry.Current?.ObjectLedgerStore is not null;
        var localReady = archive.Gate.IsPassed && (!needsObjectLedgers || objectLedgers.IsConfigured && objectLedgers.Gate.IsPassed);
        var reportReady = report.ArchiveGatePassed && (!needsObjectLedgers || report.ObjectLedgersReady);
        if (_migrating || !reportReady || report.HandingOff || localReady
            || registry.Current?.Find(report.Node) is not { } entry || entry.Silo.Generation != report.Generation
            || entry.Silo.Equals(silo.SiloAddress))
        {
            return;
        }

        _migrating = true;
        LogMigrating(_logger, report.Node, entry.Silo);
        RequestContext.Set(IPlacementDirector.PlacementHintKey, entry.Silo);
        try
        {
            this.MigrateOnIdle();
        }
        finally
        {
            _ = RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }
    }

    /// <summary>
    /// Follows the silo's archive gate from the activation (OR-I20, orleans.md §5.13): once it passes, gives the controller
    /// the cluster's archive store by the kind the gate's check found the registry's, the LSM with the grain's manifest
    /// committer and dropper, whose manifest it then reads, or archive logs; a refused gate is logged, and the activation
    /// deactivates, so the next is placed on a silo whose gate passed (<see cref="ControllerPlacementDirector"/>).
    /// </summary>
    private async Task FollowGateAsync(ClusterController controller, LedgerMapView map, CancellationToken cancellationToken)
    {
        ArchiveGateEvidence evidence;
        try
        {
            evidence = await archive.Gate.Passed.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception refusal) when (refusal is not OutOfMemoryException)
        {
            // Critical once per silo: a refused silo which the placement director must still choose, when no other is
            // compatible, may host the grain's activations again and again.
            if (archive.ReportsControllerRefusal())
            {
                LogArchiveRefused(_logger, refusal);
            }
            else
            {
                LogArchiveRefusedAgain(_logger, refusal.Message);
            }

            this.DeactivateOnIdle();
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        switch (evidence)
        {
            case ArchiveGateEvidence.Logs:
                // Archive logs keep no manifest, so the controller commits none, assigns no compaction, and drops nothing
                // itself: each drain worker submits its ledger's drop (orleans.md §5.6).
                controller.AdoptArchive(ClusterArchive.Logs.Instance);
                break;
            case ArchiveGateEvidence.Lsm lsm:
                var manifest = new ManifestView(lsm.Objects);
                _manifestCommitter = new ManifestCommitter(lsm.Objects, manifest, timeProvider, control.ManifestCommitInterval);
                _dropper = new LedgerDropper(map, manifest, loggers.CreateLogger<LedgerDropper>());
                controller.AdoptArchive(new ClusterArchive.Lsm(manifest, _manifestCommitter, node.Lsm));
                await ReadManifestAsync(controller, cancellationToken).ConfigureAwait(true);
                break;
            case null:
            default:
                throw new UnreachableException("A passed gate holds its evidence.");
        }
    }

    /// <summary>
    /// Follows the silo's object-ledger gate from the activation (orleans.md §5.14, OL-I1): once it passes, adopts the
    /// object-ledger layout on the controller so passes place and manage object-store ledgers, and triggers a sweep; a
    /// refused gate deactivates the activation so a later activation is placed anew.
    /// </summary>
    private async Task FollowObjectLedgersGateAsync(ClusterController controller, LedgerMapView map, CancellationToken cancellationToken)
    {
        ObjectLedgerStore store;
        try
        {
            store = await objectLedgers.Gate.Passed.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception refusal) when (refusal is not OutOfMemoryException)
        {
            if (objectLedgers.ReportsControllerRefusal())
            {
                LogObjectLedgersRefused(_logger, refusal);
            }
            else
            {
                LogObjectLedgersRefusedAgain(_logger, refusal.Message);
            }

            this.DeactivateOnIdle();
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        controller.AdoptObjectLedgers(store.Layout);
        RequestObjectLedgerSweep(map.Current);
        PassIfDue();
    }

    /// <summary>Triggers an object-ledger sweep on every change of the controller grain's map view (orleans.md §5.14).</summary>
    private async Task FollowMapForObjectSweepsAsync(LedgerMapView map, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var current = map.Current;
                if (objectLedgers.Gate.IsPassed)
                {
                    RequestObjectLedgerSweep(current);
                }

                _ = await map.WaitForChangeAsync(current.Epoch, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RequestObjectLedgerSweep(LedgerMapValue map)
    {
        _lastObjectSweepEpoch = map.Epoch;
        _lastObjectSweepAt = timeProvider.GetUtcNow();
        sweeps.RequestObjectLedgers(map);
    }

    /// <summary>
    /// Reads the manifest apart from the activation, retrying with backoff until it succeeds, so compaction and drops start
    /// once the object store answers and nothing else waits for it.
    /// </summary>
    private async Task ReadManifestAsync(ClusterController controller, CancellationToken cancellationToken)
    {
        var backoff = FirstBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await controller.ReadManifestAsync(cancellationToken).ConfigureAwait(true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                LogManifestReadFailed(_logger, backoff, failure);
            }

            try
            {
                await Task.Delay(backoff, timeProvider, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, LongestBackoff.Ticks));
        }
    }

    /// <summary>Observes each membership change as it arrives, and runs a pass at once when it ends or departs a node.</summary>
    private async Task FollowMembershipAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in membership.MembershipUpdates.WithCancellation(cancellationToken).ConfigureAwait(true))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Observe();
                PassIfDue();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Reads the node registry through its committer, whose value the controller's view then takes: a value read after the
    /// call, from which ordinals resolve and <c>NotRegistered</c> is answered (orleans.md §5.3).
    /// </summary>
    private async Task ReadRegistryAsync()
    {
        _ = await RegistryCommitter.ReadAsync(_deactivating.Token).ConfigureAwait(true);
        Observe();
    }

    /// <summary>
    /// Raises the silo's resolver to the registry committer's last value, so the nodes the controller observes resolve on
    /// this silo, where its calls to node control grains are placed, and gives the controller the resolver's value and each
    /// registered node's liveness, which <see cref="MembershipProjection"/> computes against it.
    /// </summary>
    private void Observe()
    {
        if (_registryCommitter?.Latest is { } committed)
        {
            registry.Advance(committed);
        }

        if (registry.Current is not { } value)
        {
            return;
        }

        var members = membership.CurrentSnapshot.Members;
        var now = timeProvider.GetUtcNow();
        var liveness = new Dictionary<NodeId, NodeLiveness>();
        foreach (var entry in value.Nodes)
        {
            SiloStatus? status = members.TryGetValue(entry.Silo, out var member) ? member.Status : null;
            var standing = MembershipProjection.Standing(status, entry.Silo.Generation, silo.SiloAddress.Generation);
            switch (standing)
            {
                case MembershipStanding.Dead:
                    if (!_deadSince.TryGetValue(entry.Node, out var since))
                    {
                        _deadSince[entry.Node] = since = now;
                    }

                    liveness[entry.Node] = new NodeLiveness.Dead(since);
                    break;
                case MembershipStanding.Departing:
                    _ = _deadSince.Remove(entry.Node);
                    liveness[entry.Node] = NodeLiveness.Departing.Instance;
                    break;
                case MembershipStanding.Live:
                    _ = _deadSince.Remove(entry.Node);
                    liveness[entry.Node] = NodeLiveness.Live.Instance;
                    break;
                default:
                    throw new UnreachableException($"A node's standing is live, departing, or dead, not {(int)standing}.");
            }
        }

        foreach (var gone in _deadSince.Keys.Where(node => !liveness.ContainsKey(node)).ToList())
        {
            _ = _deadSince.Remove(gone);
        }

        Controller.Observe(value.ToRegisteredNodes(), liveness);
        _observed = true;
    }

    /// <summary>Runs a pass: sends its cooperative release requests, transitions, and compaction assignments, drops, and asks the sweep host to sweep.</summary>
    private void Pass()
    {
        var pass = Controller.RunPass();
        foreach (var prepare in pass.PrepareReleasesOrEmpty)
        {
            _ = SendPrepareReleaseAsync(prepare);
        }

        foreach (var sent in pass.Transitions)
        {
            _ = SendAsync(sent);
        }

        SendCompactions(pass.Compactions);
        foreach (var node in pass.Retirements)
        {
            _ = RetireAsync(node);
        }

        if (pass.FloorRaise is { } floor)
        {
            _ = RaiseFloorAsync(floor);
        }

        if (pass.CompleteDrainAllRevision is { } drainAllRevision)
        {
            _ = CompleteDrainAllAsync(drainAllRevision);
        }

        if (pass.RegistryBehind)
        {
            // The map's floor lies above the registry view, which the next read brings up to it.
            registry.RequestReread();
        }

        Drop();

        // OR-I20: the grain asks for a sweep only once its silo's archive gate passed.
        if (archive.Gate.IsPassed)
        {
            sweeps.Request();
        }

        // Request periodic object-ledger sweeps only when the map epoch changed or SweepInterval elapsed, avoiding
        // repeating unchanged-epoch requests on every 1 s controller pass.
        if (objectLedgers.Gate.IsPassed && _map is { } map)
        {
            var current = map.Current;
            if (_lastObjectSweepEpoch != current.Epoch || timeProvider.GetUtcNow() - _lastObjectSweepAt >= control.SweepInterval)
            {
                RequestObjectLedgerSweep(current);
            }
        }
    }

    /// <summary>
    /// Submits <paramref name="node"/>'s retirement, which a pass returned in flight, and reports its end to the controller
    /// in a turn of its own: an answer, <c>Retired</c> or <c>NotRegistered</c>, settles it, and a <c>Retired</c> one shows in
    /// the controller's view at once, through the committer's value, so the next pass raises the floor to it; an unknown
    /// outcome leaves it recorded, and the next pass submits it again; a defect of the committer is reported, and settles it.
    /// </summary>
    private async Task RetireAsync(NodeId node)
    {
        NodeRetirementOutcome outcome;
        try
        {
            outcome = await RegistryCommitter.RetireAsync(node).ConfigureAwait(true);
        }
        catch (Exception unknown) when (unknown is NodeRegistryUnavailableException or OperationCanceledException or ObjectDisposedException or ControllerInactiveException)
        {
            LogRetirementFailed(_logger, node, unknown);
            if (!_deactivating.IsCancellationRequested)
            {
                Controller.RetirementFailed(node);
            }

            return;
        }
        catch (Exception defect) when (defect is not OutOfMemoryException)
        {
            LogRetirementDefect(_logger, node, defect);
            Debug.Fail($"Retiring node {node} met a defect: {defect}");
            if (!_deactivating.IsCancellationRequested)
            {
                Controller.RetirementSettled(node);
            }

            return;
        }

        if (_deactivating.IsCancellationRequested)
        {
            return;
        }

        if (outcome.Value is NodeRetirementOutcome.NotRegistered)
        {
            LogNotRegistered(_logger, node);
        }
        else if (outcome.Value is not NodeRetirementOutcome.Retired)
        {
            throw new UnreachableException("conventions.md §15: a retirement answers a union which holds a case.");
        }

        Observe();
        Controller.RetirementSettled(node);
        PassIfDue();
    }

    /// <summary>
    /// Raises the ledger map's retirement floor to <paramref name="floor"/>, which a pass returned, through the controller's
    /// committer, and reports the raise's end: one which applied shows in the controller's map view, and the next pass
    /// returns another while the view's floor is below the registry view's newest retirement.
    /// </summary>
    private async Task RaiseFloorAsync(ulong floor)
    {
        try
        {
            var outcome = await Controller.CommitLedgerMapAsync(new LedgerMapChange.RaiseFloor(floor)).ConfigureAwait(true);
            if (outcome.Value is LedgerMapCommitOutcome.Refused)
            {
                LogFloorRaiseRefused(_logger, floor);
            }
            else if (outcome.Value is LedgerMapCommitOutcome.Rejected rejected)
            {
                LogFloorRaiseRejected(_logger, floor, rejected.Reason);
                Debug.Fail($"The ledger map committer rejected the floor raise to {floor} as a defect: {rejected.Reason}.");
            }
            else if (outcome.Value is not (LedgerMapCommitOutcome.Applied or LedgerMapCommitOutcome.Superseded))
            {
                throw new UnreachableException("conventions.md §15: a commit answers a union which holds a case.");
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not UnreachableException)
        {
            LogFloorRaiseFailed(_logger, floor, failure);
        }
        finally
        {
            if (!_deactivating.IsCancellationRequested)
            {
                Controller.FloorRaiseEnded();
                PassIfDue();
            }
        }
    }

    /// <summary>
    /// Completes Phase 2 of drain-all (orleans.md §5.14, §8): atomically retires every active diskful node and clears
    /// <c>DrainAllRevision</c> in one registry write guarded on <paramref name="expectedDrainAllRevision"/>.
    /// </summary>
    private async Task CompleteDrainAllAsync(ulong expectedDrainAllRevision)
    {
        try
        {
            var outcome = await RegistryCommitter.CompleteDrainAllAsync(expectedDrainAllRevision, _deactivating.Token).ConfigureAwait(true);
            if (_deactivating.IsCancellationRequested)
            {
                return;
            }

            if (outcome.Value is NodeDrainAllCompleteOutcome.Superseded)
            {
                LogDrainAllCompleteSuperseded(_logger, expectedDrainAllRevision);
            }
            else if (outcome.Value is not NodeDrainAllCompleteOutcome.Completed)
            {
                throw new UnreachableException("conventions.md §15: a drain-all completion answers a union which holds a case.");
            }

            Observe();
        }
        catch (Exception unknown) when (unknown is NodeRegistryUnavailableException or OperationCanceledException or ObjectDisposedException or ControllerInactiveException)
        {
            LogDrainAllCompleteFailed(_logger, expectedDrainAllRevision, unknown);
        }
        catch (Exception defect) when (defect is not OutOfMemoryException and not UnreachableException)
        {
            LogDrainAllCompleteDefect(_logger, expectedDrainAllRevision, defect);
            Debug.Fail($"Completing drain-all at revision {expectedDrainAllRevision} met a defect: {defect}");
        }
        finally
        {
            if (!_deactivating.IsCancellationRequested)
            {
                Controller.DrainAllCompletionEnded();
                PassIfDue();
            }
        }
    }

    /// <summary>Runs one pass in a turn of its own when the controller says one is due, however many inputs ask before it runs.</summary>
    private void PassIfDue()
    {
        if (_controller is { PassDue: true })
        {
            _pass?.Request();
        }
    }

    /// <summary>
    /// Sends a transition to its executor's node control grain and gives the controller its reply in a turn of its own: a
    /// failed call or a lost reply as none, an unknown outcome. A <c>Declined</c> reply asks the silo's resolver to read the
    /// registry again, since the executor may have found the registry naming another silo (§7.2).
    /// </summary>
    private async Task SendAsync(SentTransition sent)
    {
        TransitionReply? reply = null;
        try
        {
            reply = await GrainFactory.GetGrain<INodeControlGrain>(sent.Executor.Value).ExecuteAsync(sent.Transition).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            LogSendFailed(_logger, sent.Purpose, sent.Executor, failure);
        }

        if (_deactivating.IsCancellationRequested)
        {
            return;
        }

        if (reply?.Value is TransitionReply.Declined)
        {
            registry.RequestReread();
        }

        _ = Controller.TransitionCompleted(sent, reply);
        PassIfDue();
    }

    /// <summary>
    /// Sends a cooperative <c>PrepareRelease</c> request to the outgoing primary's node control grain and gives the
    /// controller its reply in a turn of its own: a failed call or a lost reply as none, causing fallback to
    /// non-cooperative takeover. A <c>Declined</c> reply asks the silo's resolver to read the registry again, since the
    /// primary may have found the registry naming another silo (§7.2, OR-I24).
    /// </summary>
    private async Task SendPrepareReleaseAsync(SentPrepareRelease prepare)
    {
        PrepareReleaseReply? reply = null;
        try
        {
            reply = await GrainFactory.GetGrain<INodeControlGrain>(prepare.Node.Value).PrepareReleaseAsync(prepare.Request).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            LogPrepareReleaseFailed(_logger, prepare.Purpose, prepare.Request.Ledger, prepare.Node, failure);
        }

        if (_deactivating.IsCancellationRequested)
        {
            return;
        }

        if (reply?.Value is PrepareReleaseReply.Declined)
        {
            registry.RequestReread();
        }

        _ = Controller.PrepareReleaseCompleted(prepare, reply);
        PassIfDue();
    }

    private void SendCompactions(IEnumerable<CompactionAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            _ = CompactAsync(assignment);
        }
    }

    /// <summary>Sends a compaction assignment to its assignee and gives the controller the reply, or none for a failed call.</summary>
    private async Task CompactAsync(CompactionAssignment assignment)
    {
        CompactionReply? reply = null;
        try
        {
            reply = await GrainFactory.GetGrain<INodeControlGrain>(assignment.Assignee.Value).CompactAsync(assignment).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            LogCompactionFailed(_logger, assignment.Partition, assignment.Assignee, failure);
        }

        if (!_deactivating.IsCancellationRequested)
        {
            Controller.CompactionCompleted(assignment, reply);
        }
    }

    /// <summary>Drops, through the controller's map committer, every closed ledger the manifest view shows complete; single-flight.</summary>
    private void Drop()
    {
        if (_dropper is { } dropper && _drop.IsCompleted && !_deactivating.IsCancellationRequested)
        {
            _drop = DropAsync(dropper);
        }
    }

    private async Task DropAsync(LedgerDropper dropper)
    {
        try
        {
            var dropped = await dropper.DropCompleteAsync((change, token) => Controller.CommitLedgerMapAsync(change, token)).ConfigureAwait(true);
            if (!dropped.IsEmpty && !_deactivating.IsCancellationRequested)
            {
                PassIfDue();
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Each pass drops again.
            LogDropFailed(_logger, failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sending a {Purpose} transition to node {Executor} ended with an unknown outcome.")]
    private static partial void LogSendFailed(ILogger logger, TransitionPurpose purpose, NodeId executor, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sending cooperative PrepareRelease ({Purpose}) for ledger {Ledger} to node {Node} failed; falling back to non-cooperative takeover.")]
    private static partial void LogPrepareReleaseFailed(ILogger logger, TransitionPurpose purpose, LedgerId ledger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The compaction of partition {Partition} on node {Assignee} ended with an unknown outcome.")]
    private static partial void LogCompactionFailed(ILogger logger, KeyRange partition, NodeId assignee, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retiring node {Node} ended with an unknown outcome; the next pass submits it again.")]
    private static partial void LogRetirementFailed(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Retiring node {Node} met a defect of the node registry committer; the request is settled, and its caller's repeat submits it again.")]
    private static partial void LogRetirementDefect(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The node registry holds no entry of node {Node}, whose retirement was requested; the request is dropped, and its standing answers NotRegistered.")]
    private static partial void LogNotRegistered(ILogger logger, NodeId node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Raising the ledger map's retirement floor to {Floor} failed; the next pass raises it again.")]
    private static partial void LogFloorRaiseFailed(ILogger logger, ulong floor, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ledger map committer refused the floor raise to {Floor}; the next pass raises it again.")]
    private static partial void LogFloorRaiseRefused(ILogger logger, ulong floor);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The ledger map committer rejected the floor raise to {Floor} as a defect: {Reason}.")]
    private static partial void LogFloorRaiseRejected(ILogger logger, ulong floor, LedgerMapRejection reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropping complete ledgers failed; the next pass drops again.")]
    private static partial void LogDropFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The controller grain could not read the manifest; compaction and drops wait, and it reads again in {Backoff}.")]
    private static partial void LogManifestReadFailed(ILogger logger, TimeSpan backoff, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The controller grain's silo has not passed its archive gate, and node {Node} reports its own passed: the activation migrates to silo {Silo}, so the cluster's archive work runs there.")]
    private static partial void LogMigrating(ILogger logger, NodeId node, SiloAddress silo);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The controller grain's silo refused its archive store, so the activation commits no manifest change, compacts, and drops nothing; it deactivates, and a later activation is placed anew.")]
    private static partial void LogArchiveRefused(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The controller grain activated again on a silo which refused its archive store ({Refusal}), and deactivates again.")]
    private static partial void LogArchiveRefusedAgain(ILogger logger, string refusal);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The controller grain's silo refused its object-ledger store, so the activation deactivates and a later activation is placed anew.")]
    private static partial void LogObjectLedgersRefused(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The controller grain activated again on a silo which refused its object-ledger store ({Refusal}), and deactivates again.")]
    private static partial void LogObjectLedgersRefusedAgain(ILogger logger, string refusal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Completing drain-all at registry revision {DrainAllRevision} ended with an unknown outcome; the next pass submits it again.")]
    private static partial void LogDrainAllCompleteFailed(ILogger logger, ulong drainAllRevision, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completing drain-all at registry revision {DrainAllRevision} was superseded by a concurrent registry change.")]
    private static partial void LogDrainAllCompleteSuperseded(ILogger logger, ulong drainAllRevision);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Completing drain-all at registry revision {DrainAllRevision} met a defect of the node registry committer.")]
    private static partial void LogDrainAllCompleteDefect(ILogger logger, ulong drainAllRevision, Exception exception);
}
