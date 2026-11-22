using System.Collections.Frozen;
using System.Collections.Immutable;
using CsCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The controller's decisions (orleans.md §5.6, §5.7), each path forced by construction: placement (OR-I5); takeovers,
/// their candidate order and failed targets; transitions in progress until the map shows them; <c>Rejected</c>'s ban;
/// hand-offs; repair, configuration, failure-domain, and WAL-release rolls; splits and merges toward the target the map's
/// limit holds; primary balance; the budgets and the map-size guard, which limit balancing and never takeovers; the epoch
/// report replies name (OR-I19); drains of closed ledgers; and compaction assignments (archive-lsm.md §8.8).
/// </summary>
public sealed class ClusterControllerTests : IAsyncLifetime
{
    // A compaction's plan takes up to 2 × F × s bytes, so this budget allows one compaction in progress per assignee.
    private static readonly ArchiveOptions Archive = new() { GridBits = 1, MaxSectionsPerPartition = 2, CompactionBudgetBytes = 4L * 64 * 1024 * 1024 };
    private static readonly KeyRange Root = KeyRange.Root;
    private const int Generation = 1;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _mapRegister = new();
    private readonly InMemoryObjectStore _objects = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly ControlOptions _options = new() { SequencerWindow = 4, MaxConcurrentTransitions = 8, LedgersPerNode = 1 };
    private readonly Dictionary<NodeId, NodeLiveness> _membership = [];
    private readonly Dictionary<NodeId, RegisteredNode> _nodes = [];
    private ulong _revision;
    private bool _hasObjectLedgerStore;
    private ulong _drainAllRevision;
    private long _reportSequence;
    private LedgerMapView _view = null!;
    private LedgerMapCommitter _mapCommitter = null!;
    private ManifestView _manifest = null!;
    private ManifestCommitter _manifestCommitter = null!;
    private ClusterController _controller = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _view = await LedgerMapView.OpenAsync(_mapRegister, Token);
        _manifest = new ManifestView(_objects);
        _manifestCommitter = new ManifestCommitter(_objects, _manifest, _time, TimeSpan.Zero);
        await CreateControllerAsync(1 << 20);
        Register(1, 2, 3, 4, 5);
    }

    public async ValueTask DisposeAsync()
    {
        await _mapCommitter.DisposeAsync();
        await _manifestCommitter.DisposeAsync();
        _leaks.Dispose();
    }

    /// <summary>
    /// OR-I5: a bootstrap names <c>N</c> distinct nodes, each active in the controller's registry value and live in its
    /// membership view, its executor first, chosen at that value's revision from the map's epoch; a dead node and a node
    /// the registry does not hold are never placed; it is not sent again while in progress, but is once its timeout passed.
    /// </summary>
    [Fact]
    public void ABootstrapPlacesNDistinctLiveActiveNodesAndIsSentAgainOnlyAfterItsTimeout()
    {
        _membership[Node(1)] = new NodeLiveness.Dead(_time.GetUtcNow());
        _membership[Node(9)] = NodeLiveness.Live.Instance;
        Observe();

        var bootstrap = Assert.Single(_controller.RunPass().Transitions);
        var choice = bootstrap.Transition.Choice;
        Assert.Equal(TransitionPurpose.Bootstrap, bootstrap.Purpose);
        Assert.Equal(3, choice.Replicas.Distinct().Count());
        Assert.DoesNotContain(Node(1), choice.Replicas);
        Assert.DoesNotContain(Node(9), choice.Replicas);
        Assert.Equal(choice.Executor, choice.Replicas[0]);
        Assert.Equal((_revision, 0ul), (choice.ChosenAt, choice.Epoch));
        Assert.Empty(_controller.RunPass().Transitions);

        _time.Advance(_options.TransitionTimeout);
        _ = Assert.Single(_controller.RunPass().Transitions);
    }

    /// <summary>
    /// orleans.md §5.14: the controller's placement policy chooses each opening ledger's location from
    /// <see cref="ControlOptions.LedgerPlacement"/>: under <see cref="LedgerPlacement.Nodes"/>, a dead primary's
    /// object-store ledger is taken over onto nodes and a live one rolls onto nodes; under
    /// <see cref="LedgerPlacement.ObjectStore"/>, openings wait until the object-ledger store is enabled, then place on
    /// any live active node with <see cref="ControlOptions.ObjectLedgerWindow"/> and roll existing node-backed ledgers
    /// into the object store.
    /// </summary>
    [Fact]
    public async Task AnObjectStoreLedgerIsTakenOverAndRolledByLedgerPlacement()
    {
        var primary = new PrimaryClaim(Node(1), SequencerInstanceId.New());
        var inTheObjectStore = new LedgerMetadata(new LedgerId(1), Root, LedgerLocation.ObjectStore.Instance, _options.EffectiveObjectLedgerWindow(ObjectLedgerLayout.PerSlot), primary, LedgerStatus.Open.Instance);
        var value = new LedgerMapValue(1, new LedgerId(2), 0, [inTheObjectStore]);
        var bytes = new byte[value.EncodedLength];
        value.Write(bytes);
        _mapRegister.Put(bytes);
        _ = await _view.ReadAsync(Token);

        // Without ObjectLedgersReady, no node can close or roll an object-store ledger.
        ReportEveryNode(objectLedgersReady: false);
        Assert.Empty(_controller.RunPass().Transitions);

        // LedgerPlacement.Nodes rolls a live object-store ledger onto nodes and takes over a dead one onto nodes
        // once executors report ObjectLedgersReady.
        ReportEveryNode(objectLedgersReady: true);
        var roll = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Roll, roll.Purpose);
        Assert.IsType<LedgerLocation.Nodes>(roll.Transition.Choice.Location.Value);
        Assert.Equal(_options.SequencerWindow, roll.Transition.Choice.K);

        await CreateControllerAsync(1 << 20);
        ReportEveryNode(objectLedgersReady: true);
        Kill(1);
        var takeover = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Takeover, takeover.Purpose);
        Assert.IsType<LedgerLocation.Nodes>(takeover.Transition.Choice.Location.Value);
        Assert.Equal(_options.SequencerWindow, takeover.Transition.Choice.K);

        // Switching to LedgerPlacement.ObjectStore waits until AdoptObjectLedgers before sending object-store transitions,
        // while still taking over existing Nodes-backed ledgers on Nodes during an outage.
        _options.LedgerPlacement = LedgerPlacement.ObjectStore;
        _options.ObjectLedgerWindow = 12;
        await CreateControllerAsync(1 << 20);
        Revive(1);
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(new LedgerId(1), Slot.Zero)], [Opening(Root, Node(1), Node(2), Node(3))], _revision));
        ReportEveryNode(objectLedgersReady: true);
        Assert.Empty(_controller.RunPass().Transitions);

        Kill(1);
        var nodesTakeover = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Takeover, nodesTakeover.Purpose);
        Assert.IsType<LedgerLocation.Nodes>(nodesTakeover.Transition.Choice.Location.Value);
        _controller.TransitionCompleted(nodesTakeover, TransitionReply.Declined.Instance);
        Revive(1);
        ReportEveryNode(objectLedgersReady: true);
        Assert.Empty(_controller.RunPass().Transitions);

        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        var toObjectStore = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Roll, toObjectStore.Purpose);
        Assert.IsType<LedgerLocation.ObjectStore>(toObjectStore.Transition.Choice.Location.Value);
        Assert.Equal(12, toObjectStore.Transition.Choice.K);
    }

    /// <summary>
    /// orleans.md §5.14: a diskless node hosts object-store ledgers only and is never placed on a node-backed ledger;
    /// disk pressure does not roll an object-store ledger; and retiring a node which holds only object-store ledgers
    /// hands them off and waits for no closed-ledger drain.
    /// </summary>
    [Fact]
    public async Task DisklessNodesAndObjectStoreGovernanceFollowSection514()
    {
        // Two disk-backed nodes plus one diskless node cannot satisfy N = 3 on nodes.
        _nodes.Clear();
        _nodes[Node(1)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: false);
        _nodes[Node(2)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: false);
        _nodes[Node(3)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: true);
        _membership.Clear();
        _membership[Node(1)] = NodeLiveness.Live.Instance;
        _membership[Node(2)] = NodeLiveness.Live.Instance;
        _membership[Node(3)] = NodeLiveness.Live.Instance;
        Observe();
        Assert.Empty(_controller.RunPass().Transitions);

        // Under LedgerPlacement.ObjectStore, a single diskless node can bootstrap an object-store ledger once ObjectLedgersReady.
        _options.LedgerPlacement = LedgerPlacement.ObjectStore;
        _options.ObjectLedgerWindow = 8;
        await CreateControllerAsync(1 << 20);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        _nodes.Clear();
        _nodes[Node(3)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: true);
        _membership.Clear();
        _membership[Node(3)] = NodeLiveness.Live.Instance;
        Observe();
        Assert.Empty(_controller.RunPass().Transitions);

        _ = _controller.Report(Idle(Node(3), Generation) with { ObjectLedgersReady = true });
        var bootstrap = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Bootstrap, bootstrap.Purpose);
        Assert.Equal(Node(3), bootstrap.Executor);
        Assert.IsType<LedgerLocation.ObjectStore>(bootstrap.Transition.Choice.Location.Value);
        Assert.Equal(8, bootstrap.Transition.Choice.K);

        // Install an object-store ledger on node 3, report disk pressure (ignored), then add node 4 and retire node 3.
        await CommitAsync(new LedgerMapChange.Install([], [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 8, new PrimaryClaim(Node(3), SequencerInstanceId.New()))], _revision));
        _ = _controller.Report(Idle(Node(3), Generation) with { DiskPressure = true, ObjectLedgersReady = true });
        Assert.Empty(_controller.RunPass().Transitions);

        _nodes[Node(4)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: true);
        _membership[Node(4)] = NodeLiveness.Live.Instance;
        _nodes[Node(3)] = _nodes[Node(3)] with { RetiredRevision = ++_revision };
        Observe();
        _ = _controller.Report(Idle(Node(4), Generation) with { ObjectLedgersReady = true });

        // Retirement hands off the object-store primary and its standing holds no ledgers to drain once closed.
        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal((TransitionPurpose.Handoff, Node(4)), (handOff.Purpose, handOff.Executor));
        Assert.Equal(PredecessorReleaseHint.Cooperative, Assert.IsType<Transition.Replace>(handOff.Transition.Value).PredecessorHint);
        Assert.IsType<LedgerLocation.ObjectStore>(handOff.Transition.Choice.Location.Value);
        var standing = Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(3)).Value);
        Assert.Single(standing.Ledgers);

        var open = _view.Current.Ledgers.Single(static ledger => ledger.IsOpen);
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(open.Id, Slot.Zero)], [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 8, new PrimaryClaim(Node(4), SequencerInstanceId.New()))], _revision));
        var retiredAfter = Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(3)).Value);
        Assert.Empty(retiredAfter.Ledgers);
    }

    /// <summary>
    /// orleans.md §5.14: a closed object-store ledger is assigned to its former primary first while draining and
    /// <c>ObjectLedgersReady</c>, then fans out to one more <c>ObjectLedgersReady</c> draining node per
    /// <c>ClosedDrainTimeout</c>, skipping unready nodes; a backward clock step never drops the drainer count to zero.
    /// </summary>
    [Fact]
    public async Task ClosedObjectStoreLedgersFanOutOnlyToReadyDrainersAndSurviveBackwardClockSteps()
    {
        _options.LedgerPlacement = LedgerPlacement.ObjectStore;
        await CreateControllerAsync(1 << 20);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        Register(1, 2, 3, 4);

        // Node 1, 2, 4 are ObjectLedgersReady and draining; node 3 is a diskful silo without ObjectLedgers configured.
        _ = _controller.Report(Idle(Node(1), Generation) with { Draining = true, ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(2), Generation) with { Draining = true, ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(3), Generation) with { Draining = true, ObjectLedgersReady = false });
        _ = _controller.Report(Idle(Node(4), Generation) with { Draining = true, ObjectLedgersReady = true });

        await CommitAsync(new LedgerMapChange.Install([], [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 8, new PrimaryClaim(Node(1), SequencerInstanceId.New()))], _revision));
        var first = _view.Current.Ledgers.Single();
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(first.Id, Slot.Zero)], [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 8, new PrimaryClaim(Node(2), SequencerInstanceId.New()))], _revision));

        _ = _controller.RunPass();
        Assert.Equal([first.Id], _controller.Report(Idle(Node(1), Generation) with { Draining = true, ObjectLedgersReady = true }).DrainToo);
        Assert.Empty(_controller.Report(Idle(Node(2), Generation) with { Draining = true, ObjectLedgersReady = true }).DrainToo);
        Assert.Empty(_controller.Report(Idle(Node(3), Generation) with { Draining = true, ObjectLedgersReady = false }).DrainToo);
        Assert.Empty(_controller.Report(Idle(Node(4), Generation) with { Draining = true, ObjectLedgersReady = true }).DrainToo);

        // A backward clock step (modeled as a progress timestamp ahead of the controller's clock) never drops the drainer count to zero.
        _ = _controller.Report(Idle(Node(1), Generation) with
        {
            Draining = true,
            ObjectLedgersReady = true,
            ClosedDrainProgress = ImmutableDictionary<LedgerId, DateTimeOffset>.Empty.Add(first.Id, _time.GetUtcNow() + TimeSpan.FromMinutes(5)),
        });
        _ = _controller.RunPass();
        Assert.Equal([first.Id], _controller.Report(Idle(Node(1), Generation) with { Draining = true, ObjectLedgersReady = true }).DrainToo);

        // Advance one ClosedDrainTimeout past the progress timestamp: fans out to one more ObjectLedgersReady node, skipping unready Node 3.
        _time.Advance(TimeSpan.FromMinutes(5) + _options.ClosedDrainTimeout);
        _ = _controller.RunPass();
        var afterOneTimeout = new[] { Node(1), Node(2), Node(3), Node(4) }
            .Where(node => _controller.Report(Idle(node, Generation) with { Draining = true, ObjectLedgersReady = node != Node(3) }).DrainToo.Contains(first.Id))
            .ToArray();
        Assert.Equal(2, afterOneTimeout.Length);
        Assert.Contains(Node(1), afterOneTimeout);
        Assert.DoesNotContain(Node(3), afterOneTimeout);

        // One more timeout adds the remaining ObjectLedgersReady node, never unready Node 3.
        _time.Advance(_options.ClosedDrainTimeout);
        _ = _controller.RunPass();
        var afterTwoTimeouts = new[] { Node(1), Node(2), Node(3), Node(4) }
            .Where(node => _controller.Report(Idle(node, Generation) with { Draining = true, ObjectLedgersReady = node != Node(3) }).DrainToo.Contains(first.Id))
            .ToArray();
        Assert.Equal([Node(1), Node(2), Node(4)], afterTwoTimeouts);
    }

    /// <summary>
    /// orleans.md §5.14: WAL-release rolls apply only to node-backed ledgers (<c>RequestRoll</c> on an open
    /// object-store ledger sends nothing), mixed-location cold siblings merge into the configured target placement,
    /// and takeovers under <see cref="LedgerPlacement.ObjectStore"/> select only <c>ObjectLedgersReady</c> executors.
    /// </summary>
    [Fact]
    public async Task ObjectStoreLedgersIgnoreWalReleaseRollsMergeAcrossLocationsAndTakeOverToReadyExecutors()
    {
        _options.LedgerPlacement = LedgerPlacement.ObjectStore;
        _options.LedgersPerNode = 1;
        await CreateControllerAsync(1 << 20);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        Register(1, 2, 3);

        // Only nodes 1 and 2 have ObjectLedgersReady; node 3 does not.
        _ = _controller.Report(Idle(Node(1), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(2), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(3), Generation) with { ObjectLedgersReady = false });

        var (lowerRange, upperRange) = Root.Children;
        var lower = new LedgerOpening(lowerRange, LedgerLocation.ObjectStore.Instance, _options.EffectiveObjectLedgerWindow(ObjectLedgerLayout.PerSlot), new PrimaryClaim(Node(1), SequencerInstanceId.New()));
        var upper = new LedgerOpening(upperRange, new LedgerLocation.Nodes([Node(1), Node(2), Node(3)]), _options.SequencerWindow, new PrimaryClaim(Node(1), SequencerInstanceId.New()));
        await CommitAsync(new LedgerMapChange.Install([], [lower, upper], _revision));
        var lowerMeta = _view.Current.Ledgers.Single(ledger => ledger.Range == lowerRange);

        // RequestRoll on the open object-store ledger produces no WalReleaseRoll.
        _controller.RequestRoll(lowerMeta.Id);
        var pass = _controller.RunPass().Transitions;
        Assert.DoesNotContain(pass, static sent => sent.Purpose == TransitionPurpose.WalReleaseRoll);
        foreach (var sent in pass)
        {
            _controller.TransitionCompleted(sent, TransitionReply.Declined.Instance);
        }

        // Dead primary of the object-store ledger is taken over onto ObjectLedgersReady Node 2, never unready Node 3.
        await CreateControllerAsync(1 << 20);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        _ = _controller.Report(Idle(Node(1), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(2), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(3), Generation) with { ObjectLedgersReady = false });
        Kill(1);
        var takeovers = _controller.RunPass().Transitions;
        var objectTakeover = Assert.Single(takeovers, sent => sent.Transition.Closes.Contains(lowerMeta.Id));
        Assert.Equal((TransitionPurpose.Takeover, Node(2)), (objectTakeover.Purpose, objectTakeover.Executor));
        Assert.IsType<LedgerLocation.ObjectStore>(objectTakeover.Transition.Choice.Location.Value);

        // Cold mixed-location siblings merge into the configured target placement when their roll is unavailable (banned),
        // and only when the primary is ObjectLedgersReady to close the object-store sibling.
        _options.LedgerPlacement = LedgerPlacement.Nodes;
        _options.LedgersPerNode = 1;
        await CreateControllerAsync(1 << 20);
        Revive(1);
        var (q1, q2) = lowerRange.Children;
        var (q3, q4) = upperRange.Children;
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(_view.Current.Ledgers[0].Id, Slot.Zero), new LedgerClosing(_view.Current.Ledgers[1].Id, Slot.Zero)],
            [
                new LedgerOpening(q1, LedgerLocation.ObjectStore.Instance, 8, new PrimaryClaim(Node(1), SequencerInstanceId.New())),
                new LedgerOpening(q2, new LedgerLocation.Nodes([Node(1), Node(2), Node(3)]), _options.SequencerWindow, new PrimaryClaim(Node(1), SequencerInstanceId.New())),
                new LedgerOpening(q3, new LedgerLocation.Nodes([Node(2), Node(1), Node(3)]), _options.SequencerWindow, new PrimaryClaim(Node(2), SequencerInstanceId.New())),
                new LedgerOpening(q4, new LedgerLocation.Nodes([Node(3), Node(1), Node(2)]), _options.SequencerWindow, new PrimaryClaim(Node(3), SequencerInstanceId.New())),
            ],
            _revision));
        // Report Node 1 as ObjectLedgersReady with cold load on q1 and q2 (3 nodes * 1 ledger/node = target 3 < 4 open ranges).
        var q1Id = _view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == q1).Id;
        var q2Id = _view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == q2).Id;
        _ = _controller.Report(Idle(Node(1), Generation) with
        {
            ObjectLedgersReady = true,
            ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(q1Id, 0).Add(q2Id, 0),
        });
        _ = _controller.Report(Idle(Node(2), Generation) with { ObjectLedgersReady = false });
        _ = _controller.Report(Idle(Node(3), Generation) with { ObjectLedgersReady = false });
        var firstPass = _controller.RunPass().Transitions;
        // Ban the roll of q1 so the next pass reaches SendMerges on the mixed-location cold sibling pair (q1, q2).
        var q1Roll = Assert.Single(firstPass, static sent => sent.Purpose == TransitionPurpose.Roll);
        _controller.TransitionCompleted(q1Roll, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap));
        foreach (var other in firstPass.Where(sent => sent != q1Roll))
        {
            _controller.TransitionCompleted(other, TransitionReply.Declined.Instance);
        }

        var mergePass = _controller.RunPass().Transitions;
        var mixedMerge = Assert.Single(mergePass, static sent => sent.Purpose == TransitionPurpose.Merge);
        var mergeTransition = Assert.IsType<Transition.Merge>(mixedMerge.Transition.Value);
        Assert.Equal(Node(1), mixedMerge.Executor);
        Assert.IsType<LedgerLocation.Nodes>(mergeTransition.Choice.Location.Value);
        Assert.Contains(_view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == q1).Id, mixedMerge.Transition.Closes);
        Assert.Contains(_view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == q2).Id, mixedMerge.Transition.Closes);

        // Verify MapCache.MaxTakeoverGrowth accounts for ObjectStore -> Nodes growth (+16 * N bytes per ledger).
        var map = _view.Current;
        var objectLedger = map.Ledgers.First(static ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore);
        var rollbackGrowth = map.GrowthOf(new Transition.Replace(objectLedger.Id, new TransitionChoice(Node(1), new LedgerLocation.Nodes([Node(1), Node(2), Node(3)]), _options.SequencerWindow, _revision, map.Epoch)));
        var cache = new MapCache(map, targetReplicas: _options.Replicas);
        Assert.True(cache.MaxTakeoverGrowth >= rollbackGrowth);
    }

    /// <summary>
    /// orleans.md §5.14: <see cref="ControlOptions.ObjectStoreRequestsPerSecond"/> shares the storage-account request
    /// budget among reporting nodes proportionally to their reported object-store demand, respecting the floor and total
    /// budget.
    /// </summary>
    [Fact]
    public async Task ObjectStoreRequestBudgetSharesAcrossReportingNodes()
    {
        _options.ObjectStoreRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        Register(1, 2);

        _ = _controller.Report(Idle(Node(1), Generation) with { ObjectStoreDemand = 900, ObjectStoreRequestsPerSecond = null });
        var second = _controller.Report(Idle(Node(2), Generation) with { ObjectStoreDemand = 100, ObjectStoreRequestsPerSecond = null }).ObjectStoreShare;
        Assert.NotNull(second);
        var floor = ArchiveLogRequestShare.Floor(1_000, 2);
        Assert.Equal(floor + (1_000 - 2 * floor) * 100 / 1_000.0, second.Value, 9);

        var first = _controller.Report(Idle(Node(1), Generation) with { ObjectStoreDemand = 900, ObjectStoreRequestsPerSecond = null }).ObjectStoreShare;
        Assert.NotNull(first);
        Assert.True(first.Value + second.Value <= 1_000 + 1e-6);
    }

    /// <summary>
    /// OR-I5: with fewer than <c>N</c> live active nodes the controller sends no bootstrap; once a node returns, it does.
    /// </summary>
    [Fact]
    public void FewerThanNLiveActiveNodesSendNoBootstrap()
    {
        Kill(3);
        Kill(4);
        Kill(5);
        Assert.Empty(_controller.RunPass().Transitions);

        _membership[Node(5)] = NodeLiveness.Live.Instance;
        Observe();
        Assert.Equal(TransitionPurpose.Bootstrap, Assert.Single(_controller.RunPass().Transitions).Purpose);
    }

    /// <summary>
    /// OR-I5: with fewer than <c>N</c> live active nodes, a takeover of a ledger whose primary died is sent degraded once
    /// <c>Q</c> of its successor's replicas are live, keeping the dead replicas last; with fewer than <c>Q</c>, none is.
    /// </summary>
    [Fact]
    public async Task FewerThanNLiveActiveNodesSendADegradedTakeoverOnlyWithQLive()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(1);
        Kill(2);
        Kill(4);
        Kill(5);
        Assert.Empty(_controller.RunPass().Transitions);

        Revive(2);
        var takeover = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Takeover, Node(2)), (takeover.Purpose, takeover.Executor));
        Assert.Equal([Node(2), Node(3), Node(1)], takeover.Transition.Choice.Replicas);
    }

    /// <summary>
    /// orleans.md §5.7, OR-I5: a cluster of exactly <c>N</c> nodes takes a dead primary's ledger over at once onto a
    /// degraded successor, which keeps the dead node as a replica; the replica keeps its place for <c>G</c> from the node's
    /// declaration, not from the successor's install, even once a live node joins, and is then repaired out onto it.
    /// </summary>
    [Fact]
    public async Task AClusterOfNNodesTakesOverAtOnceAndRepairsTheDeadReplicaAfterItsGraceOnceANodeJoins()
    {
        Register(1, 2, 3);
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(1);
        var delay = TimeSpan.FromMinutes(1);
        _time.Advance(delay);

        var takeover = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Takeover, Node(2)), (takeover.Purpose, takeover.Executor));
        Assert.Equal([Node(2), Node(3), Node(1)], takeover.Transition.Choice.Replicas);
        await ReplaceAsync(root, [.. takeover.Transition.Choice.Replicas]);
        Assert.Empty(_controller.RunPass().Transitions);

        _time.Advance(_options.ReplicaGrace - delay - TimeSpan.FromTicks(1));
        Register(1, 2, 3, 4);
        Assert.Empty(_controller.RunPass().Transitions);

        _time.Advance(TimeSpan.FromTicks(1));
        var repair = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Repair, Node(2)), (repair.Purpose, repair.Executor));
        Assert.Equal([Node(2), Node(3), Node(4)], repair.Transition.Choice.Replicas);
    }

    /// <summary>
    /// orleans.md §5.6, OR-I5: with <c>N = 5</c>, a takeover names every node which may be given work, then the dead
    /// replicas in the ledger's order: four live nodes keep the dead primary; after a second replica dies, three keep both,
    /// the primary before the later replica.
    /// </summary>
    [Fact]
    public async Task AFiveReplicaTakeoverNamesEveryLiveNodeThenTheDeadReplicasInTheirOrder()
    {
        _options.Replicas = 5;
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3), Node(4), Node(5));
        Kill(1);

        var first = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Takeover, Node(2)), (first.Purpose, first.Executor));
        Assert.Equal([Node(2), Node(3), Node(4), Node(5), Node(1)], first.Transition.Choice.Replicas);

        _controller.TransitionCompleted(first, reply: null);
        Kill(3);
        var second = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Takeover, Node(4)), (second.Purpose, second.Executor));
        Assert.Equal([Node(4), Node(2), Node(5), Node(1), Node(3)], second.Transition.Choice.Replicas);
    }

    /// <summary>
    /// OR-I5, OR-I4: a degraded choice never keeps a retired dead node. In a cluster of exactly <c>N</c>, a dead primary
    /// retired before its takeover leaves too few nodes, so the takeover waits for a live node and then names it; a
    /// leaving primary's hand-off with a retired dead replica sends nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARetiredDeadNodeIsNeverKept(bool takeover)
    {
        _options.MaxConcurrentTransitions = 0;
        if (takeover)
        {
            Register(1, 2, 3);
            _ = await InstallRootAsync(Node(1), Node(2), Node(3));
            Kill(1);
            Retire(1);
            Assert.Empty(_controller.RunPass().Transitions);

            _nodes[Node(4)] = new RegisteredNode(RetiredRevision: null, FailureDomain: null, Generation, Ordinal: null);
            Observe();
            var sent = Assert.Single(_controller.RunPass().Transitions);
            Assert.Equal(TransitionPurpose.Takeover, sent.Purpose);
            Assert.Equal([Node(2), Node(3), Node(4)], sent.Transition.Choice.Replicas);
        }
        else
        {
            Register(1, 2, 3, 4);
            _ = await InstallRootAsync(Node(1), Node(2), Node(3));
            Kill(3);
            Retire(3);
            _controller.RequestHandOff(Node(1), Generation);
            Assert.Empty(_controller.RunPass().Transitions);
        }
    }

    /// <summary>
    /// OR-I5: a retired or stopping primary's hand-off, like a takeover, keeps a dead replica while fewer than <c>N</c>
    /// live active nodes may hold the ledger and <c>Q</c> do; with fewer than <c>Q</c>, it sends nothing.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ALeavingPrimarysHandOffKeepsADeadReplicaWithFewerThanNLiveNodes(bool retired, bool belowQuorum)
    {
        Register(1, 2, 3, 4);
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(3);
        if (belowQuorum)
        {
            Kill(4);
        }

        if (retired)
        {
            Retire(1);
        }
        else
        {
            _controller.RequestHandOff(Node(1), Generation);
        }

        var pass = RunPassCompletingPrepareReleases().Transitions;
        if (belowQuorum)
        {
            Assert.Empty(pass);
            return;
        }

        var handOff = Assert.Single(pass);
        Assert.Equal((retired ? TransitionPurpose.Handoff : TransitionPurpose.Departure, Node(2)), (handOff.Purpose, handOff.Executor));
        Assert.Equal([Node(2), Node(4), Node(3)], handOff.Transition.Choice.Replicas);
    }

    /// <summary>
    /// OR-I5: only a takeover or a leaving primary's hand-off may keep a dead replica. With node 3 dead inside its grace and
    /// fewer than <c>N</c> nodes which may hold its ledgers, no other purpose sends a transition naming it: primary balance,
    /// a split toward the target, a merge of cold siblings, a roll for the window or for failure domains, the repair of a
    /// lost replica, a WAL-release roll, or replica evening. Each but evening, which rolls no ledger with a replica that
    /// cannot execute, is sent keeping node 3 once the controller places every transition degraded, the mutant the
    /// assertion catches.
    /// </summary>
    [Theory]
    [InlineData("balance")]
    [InlineData("split")]
    [InlineData("merge")]
    [InlineData("window")]
    [InlineData("domain")]
    [InlineData("repair")]
    [InlineData("release")]
    [InlineData("evening")]
    public async Task NoOtherPurposeKeepsADeadReplica(string purpose)
    {
        Register(1, 2, 3);
        TransitionPurpose expected;
        switch (purpose)
        {
            case "balance":
                _ = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)]);
                ReportEveryNode();
                expected = TransitionPurpose.Handoff;
                break;
            case "split":
                _ = await InstallRootAsync(Node(1), Node(2), Node(3));
                expected = TransitionPurpose.Split;
                break;
            case "merge":
                var quarters = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)], [Node(2), Node(1), Node(3)]);
                _ = _controller.Report(Idle(Node(1), Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(quarters[0].Id, 0).Add(quarters[1].Id, 0) });
                _ = _controller.Report(Idle(Node(2), Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(quarters[2].Id, 0).Add(quarters[3].Id, 0) });
                expected = TransitionPurpose.Merge;
                break;
            case "window":
                _ = await InstallRootAsync(Node(1), Node(2), Node(3));
                _options.SequencerWindow = 8;
                expected = TransitionPurpose.Roll;
                break;
            case "domain":
                Register((1, "a"), (2, "a"), (3, "b"), (4, "c"));
                _ = await InstallRootAsync(Node(1), Node(2), Node(3));
                expected = TransitionPurpose.Roll;
                break;
            case "repair":
                var lost = await InstallRootAsync(Node(1), Node(2), Node(3));
                _ = _controller.Report(Idle(Node(2), Generation) with { LostReplicas = [lost.Id] });
                expected = TransitionPurpose.Repair;
                break;
            case "release":
                _controller.RequestRoll((await InstallRootAsync(Node(1), Node(2), Node(3))).Id);
                expected = TransitionPurpose.WalReleaseRoll;
                break;
            default:
                Register(1, 2, 3, 4);
                ReportEveryNode();
                _ = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)], [Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
                expected = TransitionPurpose.Roll;
                break;
        }

        Kill(3);
        var pass = _controller.RunPass().Transitions;

        Assert.DoesNotContain(pass, static sent => sent.Transition.Choice.Replicas.Contains(Node(3)));
        Assert.DoesNotContain(pass, sent => sent.Purpose == expected);
        if (purpose == "evening")
        {
            return;
        }

        foreach (var sent in pass)
        {
            _controller.TransitionCompleted(sent, reply: null);
        }

        _controller.DegradedPlacementMutant = true;
        Observe();
        Assert.Contains(RunPassCompletingPrepareReleases().Transitions, sent => sent.Purpose == expected && sent.Transition.Choice.Replicas.Contains(Node(3)));
    }

    /// <summary>
    /// orleans.md §5.7: a takeover goes to the first replica which may execute and has not failed it, then the other
    /// replicas, then the other nodes by fewest primaries; once every candidate failed, to the first again. A lost reply
    /// marks its executor failed for the ledger.
    /// </summary>
    [Fact]
    public async Task ATakeoverTriesEveryCandidateInOrderBeforeAFailedOne()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(1);

        List<NodeId> executors = [];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var takeover = Assert.Single(_controller.RunPass().Transitions);
            Assert.Equal(new Transition.Replace(root.Id, takeover.Transition.Choice), takeover.Transition);
            Assert.DoesNotContain(Node(1), takeover.Transition.Choice.Replicas);
            executors.Add(takeover.Executor);
            _controller.TransitionCompleted(takeover, reply: null);
        }

        Assert.Equal([Node(2), Node(3), Node(4), Node(5), Node(2)], executors);
    }

    /// <summary>
    /// A transition in progress past <c>TransitionTimeout</c>, or whose executor membership shows dead, ends and marks its
    /// executor failed, so the next pass chooses another; one whose verdict the view has not reached stays in progress, and
    /// ends without a failure once the view reaches the epoch.
    /// </summary>
    [Fact]
    public async Task AnEntryEndsOnTimeoutOrADeadExecutorAndOtherwiseOnlyOnceTheViewShowsIt()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(1);
        var first = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(Node(2), first.Executor);

        _time.Advance(_options.TransitionTimeout);
        var second = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(Node(3), second.Executor);

        Kill(3);
        var third = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(Node(4), third.Executor);

        // The executor committed through another controller: the verdict names an epoch this view has not read.
        _controller.TransitionCompleted(third, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch + 1));
        Assert.Empty(_controller.RunPass().Transitions);
        await ReplaceAsync(root, Node(4), Node(2), Node(5));
        Assert.Empty(_controller.RunPass().Transitions);
    }

    /// <summary>
    /// orleans.md §5.7: the claim's own node takes over the ledger it reported <c>Unserved</c>, unless its hand-off is
    /// pending, when the first replica does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClaimsOwnNodeTakesOverWhatItReportedUnservedUnlessItHandsOff(bool handingOff)
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        if (handingOff)
        {
            _controller.RequestHandOff(Node(1), Generation);
        }

        _ = _controller.Report(Idle(Node(1), Generation) with { Unserved = [root.Id] });

        var takeover = Assert.Single(_controller.RunPass().Transitions, static sent => sent.Purpose == TransitionPurpose.Takeover);
        Assert.Equal(handingOff ? Node(2) : Node(1), takeover.Executor);
    }

    /// <summary>
    /// orleans.md §5.6: a report from a silo older than the newest the controller knows for its node, by its registry view
    /// or by the node's own reports while the view lags a restart, is a process the restart replaced: it is answered with
    /// the epoch alone, and its unserved ledgers start nothing. A newer silo's report is recorded.
    /// </summary>
    [Fact]
    public async Task AReportFromAReplacedProcessIsAnsweredAndIgnored()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = _controller.RunPass();
        _nodes[Node(1)] = _nodes[Node(1)] with { Generation = Generation + 1 };
        Observe();

        var stale = _controller.Report(Idle(Node(1), Generation) with { Unserved = [root.Id] });
        Assert.Equal((_controller.KnownEpoch, true), (stale.MapEpoch, stale.DrainToo.IsEmpty));
        Assert.False(_controller.PassDue);
        Assert.Empty(_controller.RunPass().Transitions);

        // The registry view lags the node's next restart: the newest silo's report sets the bar.
        _ = _controller.Report(Idle(Node(1), Generation + 2));
        _ = _controller.Report(Idle(Node(1), Generation + 1) with { Unserved = [root.Id] });
        Assert.False(_controller.PassDue);

        _ = _controller.Report(Idle(Node(1), Generation + 2) with { Unserved = [root.Id] });
        Assert.True(_controller.PassDue);
        Assert.Equal(TransitionPurpose.Takeover, Assert.Single(_controller.RunPass().Transitions).Purpose);
    }

    /// <summary>
    /// orleans.md §5.6: a report from a node the registry view does not yet name is recorded, and a restarted silo's report
    /// counts every ledger it lists unserved as newly unserved, though its predecessor reported the same ones: the new
    /// process holds none of the old one's claims.
    /// </summary>
    [Fact]
    public async Task AnUnregisteredNodesReportIsRecordedAndARestartedSilosUnservedLedgersAreNew()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = _controller.RunPass();

        _ = _controller.Report(Idle(Node(9), Generation) with { Unserved = [root.Id] });
        Assert.True(_controller.PassDue);
        _ = _controller.RunPass();

        _ = _controller.Report(Idle(Node(1), Generation) with { Unserved = [root.Id] });
        Assert.True(_controller.PassDue);
        _ = _controller.RunPass();
        _ = _controller.Report(Idle(Node(1), Generation) with { Unserved = [root.Id] });
        Assert.False(_controller.PassDue);

        _ = _controller.Report(Idle(Node(1), Generation + 1) with { Unserved = [root.Id] });
        Assert.True(_controller.PassDue);
    }

    /// <summary>
    /// ledgers.md §2.6: <c>Rejected</c> bans that transition in the activation: a rejected repair roll is not sent again,
    /// but a takeover of its ledger is; a rejected bootstrap is never sent again.
    /// </summary>
    [Fact]
    public async Task ARejectionBansOnlyThatTransition()
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(3, since: _time.GetUtcNow() - _options.ReplicaGrace);
        var repair = Assert.Single(_controller.RunPass().Transitions, static sent => sent.Purpose == TransitionPurpose.Repair);
        _controller.TransitionCompleted(repair, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap));
        _time.Advance(_options.TransitionTimeout);
        Assert.DoesNotContain(_controller.RunPass().Transitions, static sent => sent.Purpose == TransitionPurpose.Repair);

        Kill(1);
        var takeover = Assert.Single(_controller.RunPass().Transitions, static sent => sent.Purpose == TransitionPurpose.Takeover);
        Assert.Equal(root.Id, Assert.IsType<Transition.Replace>(takeover.Transition.Value).Ledger);
    }

    /// <summary>A rejected bootstrap is never sent again in the activation, past any number of timeouts.</summary>
    [Fact]
    public void ARejectedBootstrapIsNeverSentAgain()
    {
        var bootstrap = Assert.Single(_controller.RunPass().Transitions);
        _controller.TransitionCompleted(bootstrap, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap));
        for (var timeout = 0; timeout < 3; timeout++)
        {
            _time.Advance(_options.TransitionTimeout);
            Assert.Empty(_controller.RunPass().Transitions);
        }
    }

    /// <summary>
    /// orleans.md §5.6, OR-I3: a retired primary's ledgers are handed off to the first replica which may execute, within
    /// the balancing budget; a stopping node's depart the same way, within their own.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARetiredOrStoppingPrimaryHandsItsLedgersOff(bool retired)
    {
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        if (retired)
        {
            _nodes[Node(1)] = new RegisteredNode(RetiredRevision: _revision + 1, FailureDomain: null, Generation, Ordinal: null);
            Observe();
        }
        else
        {
            _controller.RequestHandOff(Node(1), Generation);
        }

        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal((retired ? TransitionPurpose.Handoff : TransitionPurpose.Departure, Node(2)), (handOff.Purpose, handOff.Executor));
    }

    /// <summary>
    /// orleans.md §5.3, OR-I5: a retired node is never placed in a bootstrap, a hand-off, or a takeover, nor chosen to
    /// execute one, while the open ledgers which name it keep naming it until a transition replaces it, so its silo serves
    /// those replicas until its gate opens.
    /// </summary>
    [Fact]
    public async Task ARetiredNodeIsNeverPlacedYetItsLedgersNameItUntilReplaced()
    {
        _options.MaxConcurrentTransitions = 1;
        Retire(4);
        var bootstrap = Assert.Single(_controller.RunPass().Transitions);
        Assert.DoesNotContain(Node(4), bootstrap.Transition.Choice.Replicas);
        _controller.TransitionCompleted(bootstrap, reply: null);

        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        Retire(1);
        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal((TransitionPurpose.Handoff, Node(2)), (handOff.Purpose, handOff.Executor));
        Assert.DoesNotContain(Node(4), handOff.Transition.Choice.Replicas);
        Assert.Equal(Node(1), _view.Current.Ledgers.Single(ledger => ledger.Id == root.Id).Primary.Node);
        Assert.Equal([root.Id], Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(1)).Value).Ledgers);

        // The hand-off's executor fails and the retired primary dies: every takeover candidate skips both retired nodes.
        _controller.TransitionCompleted(handOff, reply: null);
        _options.MaxConcurrentTransitions = 0;
        Kill(1);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var takeover = Assert.Single(_controller.RunPass().Transitions);
            Assert.Equal(TransitionPurpose.Takeover, takeover.Purpose);
            Assert.DoesNotContain(Node(4), takeover.Transition.Choice.Replicas);
            Assert.DoesNotContain(Node(1), takeover.Transition.Choice.Replicas);
            Assert.DoesNotContain(takeover.Executor, (NodeId[])[Node(1), Node(4)]);
            _controller.TransitionCompleted(takeover, reply: null);
        }
    }

    /// <summary>
    /// orleans.md §5.3: a recorded retirement makes a pass due, and a pass returns it in flight, once, in node order; one
    /// which failed is returned again, and an answer settles it, making a pass due, as does the registry view showing the
    /// node retired. A request for a node the view shows retired records nothing, and one for the default identity is
    /// refused.
    /// </summary>
    [Fact]
    public void ARetirementIsInFlightOnceAndReturnedAgainOnlyAfterItFailed()
    {
        _controller.RequestRetirement(Node(5));
        _controller.RequestRetirement(Node(4));

        Assert.True(_controller.PassDue);
        Assert.Equal([Node(4), Node(5)], _controller.RunPass().Retirements);
        Assert.Empty(_controller.RunPass().Retirements);

        _controller.RetirementFailed(Node(4));
        Assert.Equal([Node(4)], _controller.RunPass().Retirements);
        _controller.RetirementSettled(Node(4));
        Assert.True(_controller.PassDue);
        Retire(5);
        _controller.RetirementSettled(Node(5));
        Assert.Empty(_controller.RunPass().Retirements);

        _controller.RequestRetirement(Node(5));
        Assert.False(_controller.PassDue);
        Assert.Empty(_controller.RunPass().Retirements);
        _ = Assert.Throws<ArgumentException>(() => _controller.RequestRetirement(default));
    }

    /// <summary>
    /// orleans.md §5.3: a scale-down records the retirement of every active node at an ordinal of the new replica count or
    /// more, and answers every node at those ordinals whose gate is not open, a retired one included, with its ordinal and
    /// standing; it refuses a count below <c>N</c>. A deleted volume answers its ordinal's nodes whose gates are not open,
    /// and records only those membership shows dead: a live occupant is answered active, its retirement not recorded. A
    /// node whose gate opened is answered by neither.
    /// </summary>
    [Fact]
    public async Task AScaleDownAndADeletedVolumeAnswerEveryNodeAtTheirOrdinalsWhoseGateIsClosed()
    {
        for (var node = 1; node <= 5; node++)
        {
            _nodes[Node(node)] = _nodes[Node(node)] with { Ordinal = node - 1 };
        }

        var retiredAt = _revision + 1;
        Retire(5);
        NodeStanding retired = new NodeStanding.Retired(retiredAt, _view.Current.Epoch, 0, []);

        Assert.Equal([new OrdinalNode(Node(4), 3, NodeStanding.Active.Instance), new OrdinalNode(Node(5), 4, retired)], _controller.RequestScaleDown(3));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => _controller.RequestScaleDown(2));
        Assert.Equal([new OrdinalNode(Node(2), 1, NodeStanding.Active.Instance)], _controller.RequestVolumeRetirement(1));
        Assert.Equal([Node(4)], _controller.RunPass().Retirements);

        Kill(2);
        Assert.Equal([new OrdinalNode(Node(2), 1, NodeStanding.Active.Instance)], _controller.RequestVolumeRetirement(1));
        Assert.Equal([new OrdinalNode(Node(5), 4, retired)], _controller.RequestVolumeRetirement(4));
        Assert.Equal([Node(2)], _controller.RunPass().Retirements);

        await CommitAsync(new LedgerMapChange.RaiseFloor(retiredAt));
        Assert.Empty(_controller.RequestVolumeRetirement(4));
        Assert.Equal([Node(4)], _controller.RequestScaleDown(3).Select(static found => found.Node));
    }

    /// <summary>
    /// orleans.md §5.3, LG-I11: while the registry view's newest retirement lies above the map view's floor, a pass returns
    /// it as the floor to raise, once until the raise ends, and none once the map's floor covers it.
    /// </summary>
    [Fact]
    public async Task APassRaisesTheFloorOnceAtATimeUntilTheMapCoversTheNewestRetirement()
    {
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        Assert.Null(_controller.RunPass().FloorRaise);
        var retiredAt = _revision + 1;
        Retire(5);

        Assert.Equal(retiredAt, _controller.RunPass().FloorRaise);
        Assert.Null(_controller.RunPass().FloorRaise);
        _controller.FloorRaiseEnded();
        Assert.Equal(retiredAt, _controller.RunPass().FloorRaise);

        await CommitAsync(new LedgerMapChange.RaiseFloor(retiredAt));
        _controller.FloorRaiseEnded();
        Assert.Null(_controller.RunPass().FloorRaise);
    }

    /// <summary>
    /// LG-I11: while the map's floor lies above the registry view's revision, every transition a pass could choose carries a
    /// <c>ChosenAt</c> below it, so the pass sends none, a pending takeover included, and asks its host to read the
    /// registry; once the view reaches the floor, it sends the takeover, chosen at that revision.
    /// </summary>
    [Fact]
    public async Task APassSendsNoTransitionWhileTheFloorLiesAboveTheRegistryView()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(1);
        await CommitAsync(new LedgerMapChange.RaiseFloor(_revision + 1));

        var behind = _controller.RunPass();
        Assert.True(behind.RegistryBehind);
        Assert.Empty(behind.Transitions);

        Observe();
        var caughtUp = _controller.RunPass();
        Assert.False(caughtUp.RegistryBehind);
        var takeover = Assert.Single(caughtUp.Transitions);
        Assert.Equal((TransitionPurpose.Takeover, _revision), (takeover.Purpose, takeover.Transition.Choice.ChosenAt));
    }

    /// <summary>
    /// OR-I4: a node the registry holds active is <c>Active</c>, and one it does not hold <c>NotRegistered</c>; a retired
    /// node's gate stays closed while the map's floor is below its retirement, and while any ledger, open or closed, names
    /// it as a replica or as its primary; it opens once the floor covers the retirement and the last ledger naming it is
    /// dropped.
    /// </summary>
    [Fact]
    public async Task TheGateOpensOnlyOnceTheFloorCoversTheRetirementAndNoLedgerNamesTheNode()
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        Assert.Equal(NodeStanding.Active.Instance, _controller.Retirement(Node(3)));
        Assert.Equal(NodeStanding.NotRegistered.Instance, _controller.Retirement(Node(9)));
        var retiredAt = _revision + 1;
        Retire(3);

        var retired = Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(3)).Value);
        Assert.Equal((retiredAt, false, false), (retired.RetiredRevision, retired.FloorCovers, retired.IsReleasable));
        Assert.Equal([root.Id], retired.Ledgers);

        await CommitAsync(new LedgerMapChange.RaiseFloor(retiredAt));
        var covered = Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(3)).Value);
        Assert.Equal((true, false), (covered.FloorCovers, covered.IsReleasable));

        await ReplaceAsync(root, Node(1), Node(2), Node(4));
        var closed = Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(3)).Value);
        Assert.Equal([root.Id], closed.Ledgers);
        Assert.False(closed.IsReleasable);

        await CommitAsync(new LedgerMapChange.Drop(root.Id));
        var released = Assert.IsType<NodeStanding.Retired>(_controller.Retirement(Node(3)).Value);
        Assert.True(released.IsReleasable);
        Assert.Equal(_view.Current.Epoch, released.Epoch);
    }

    /// <summary>
    /// orleans.md §5.8 step 4: a retired node's standing reads the map register when the view does not show its gate open,
    /// so a single call answers releasable once another committer's writes opened it; a node which is not retired is
    /// answered from the view, with no read.
    /// </summary>
    [Fact]
    public async Task AStandingReadsTheMapOnlyWhileARetiredNodesGateIsClosedInTheView()
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        var retiredAt = _revision + 1;
        Retire(3);
        await using (var other = new LedgerMapCommitter(_mapRegister, 1 << 20))
        {
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await other.CommitAsync(new LedgerMapChange.RaiseFloor(retiredAt), Token)).Value);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await other.CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(Root, Node(1), Node(2), Node(4))], _revision), Token)).Value);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await other.CommitAsync(new LedgerMapChange.Drop(root.Id), Token)).Value);
        }

        Assert.False(_controller.Retirement(Node(3)).IsReleasable);
        Assert.True((await _controller.RetirementAsync(Node(3), Token)).IsReleasable);

        var reads = _mapRegister.Reads;
        Assert.Equal(NodeStanding.Active.Instance, await _controller.RetirementAsync(Node(2), Token));
        Assert.Equal(reads, _mapRegister.Reads);
    }

    /// <summary>A partition whose open ledger's primary is retired is assigned no compaction; an active primary's is.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOverfullPartitionIsAssignedOnlyToAnActivePrimary(bool retired)
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = await OverfillAsync(Root, 3);
        if (retired)
        {
            Retire(1);
        }

        Assert.Equal(retired ? 0 : 1, _controller.AssignCompactions().Length);
    }

    /// <summary>
    /// A stopping node's hand-off stays pending once the map names it primary of nothing: no takeover, hand-off, or other
    /// transition goes to it, nor names it a replica, so no ledger is ever given back to it while it stops.
    /// </summary>
    [Fact]
    public async Task AStoppingNodeReceivesNoLedgerEvenOncePrimaryOfNothing()
    {
        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
        _controller.RequestHandOff(Node(2), Generation);
        Kill(1);
        var pass = RunPassCompletingPrepareReleases().Transitions;
        Assert.Contains(pass, static sent => sent.Purpose == TransitionPurpose.Takeover);
        Assert.Contains(pass, static sent => sent.Purpose == TransitionPurpose.Departure);
        Assert.All(pass, static sent => Assert.DoesNotContain(Node(2), sent.Transition.Choice.Replicas));

        foreach (var sent in pass)
        {
            _controller.TransitionCompleted(sent, reply: null);
        }

        Revive(1);
        await ReplaceAsync(lower, Node(3), Node(1), Node(4));
        await ReplaceAsync(upper, Node(4), Node(3), Node(1));
        for (var round = 0; round < 4; round++)
        {
            _time.Advance(_options.TransitionTimeout);
            Assert.All(_controller.RunPass().Transitions, static sent => Assert.DoesNotContain(Node(2), sent.Transition.Choice.Replicas));
        }
    }

    /// <summary>
    /// A stopping node with no primaries is never primary balance's target; its request ends only once membership no
    /// longer shows it live or the registry names a newer generation of it, and a request from an older generation is
    /// ignored, so the restarted node, once it reports from its new generation, is balance's target again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoppingNodeIsNoBalanceTargetUntilItLeavesMembershipOrRegistersAgain(bool leavesMembership)
    {
        // A fourth node takes the stopping node's place among the replicas a hand-off places.
        Register(1, 2, 3, 4);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(4)], [Node(1), Node(2), Node(4)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(4)]);
        ReportEveryNode();
        Warm(quarters);
        _controller.RequestHandOff(Node(3), Generation);

        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal((TransitionPurpose.Handoff, Node(4)), (handOff.Purpose, handOff.Executor));
        Assert.DoesNotContain(Node(3), handOff.Transition.Choice.Replicas);
        _controller.TransitionCompleted(handOff, TransitionReply.Declined.Instance);
        Assert.All(RunPassCompletingPrepareReleases().Transitions, static sent => Assert.NotEqual(Node(3), sent.Executor));

        if (leavesMembership)
        {
            Kill(3);
            _ = _controller.RunPass();
        }

        _nodes[Node(3)] = _nodes[Node(3)] with { Generation = Generation + 1 };
        Revive(3);
        _ = _controller.Report(Idle(Node(3), Generation + 1));
        _controller.RequestHandOff(Node(3), Generation);
        _time.Advance(_options.TransitionTimeout);
        Assert.Contains(RunPassCompletingPrepareReleases().Transitions, static sent => sent is { Purpose: TransitionPurpose.Handoff, Executor: var executor } && executor == Node(3));
    }

    /// <summary>
    /// orleans.md §5.6: a controller activation makes a node a balance target only once it heard a report from the node's
    /// registered generation, so a new activation, which has not yet heard of a stopping or refused node's hand-off, hands
    /// it nothing. A report which says the node hands off makes it pending at once, however often it says so, and one which
    /// says it serves makes it a target.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewActivationBalancesOntoANodeOnlyOnceItReportsServing(bool handingOff)
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(3)]);
        Warm(quarters);
        Assert.DoesNotContain(_controller.RunPass().Transitions, static sent => sent.Executor == Node(3));

        _ = _controller.Report(Idle(Node(3), Generation) with { HandingOff = handingOff });
        _ = _controller.Report(Idle(Node(3), Generation) with { HandingOff = handingOff });
        var pass = RunPassCompletingPrepareReleases().Transitions;

        if (handingOff)
        {
            Assert.All(pass, static sent => Assert.DoesNotContain(Node(3), sent.Transition.Choice.Replicas));
            Assert.All(pass, static sent => Assert.NotEqual(Node(3), sent.Executor));
        }
        else
        {
            var handOff = Assert.Single(pass);
            Assert.Equal((TransitionPurpose.Handoff, Node(3)), (handOff.Purpose, handOff.Executor));
        }
    }

    /// <summary>
    /// ledgers.md §2.6: a rejected hand-off is banned, and primary balance hands the node's next ledger off rather than
    /// stalling on the banned one.
    /// </summary>
    [Fact]
    public async Task PrimaryBalanceSkipsARejectedHandOff()
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(4)], [Node(1), Node(2), Node(4)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(4)]);
        ReportEveryNode();
        Warm(quarters);
        var rejected = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        _controller.TransitionCompleted(rejected, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap));

        var next = Assert.Single(RunPassCompletingPrepareReleases().Transitions);

        Assert.Equal(TransitionPurpose.Handoff, next.Purpose);
        Assert.NotEqual(rejected.Transition.Closes, next.Transition.Closes);
    }

    /// <summary>
    /// orleans.md §5.6: a pass counts each hand-off in progress where it is going until the map shows it, so once one of two
    /// hand-offs lands, the pass its reply makes due hands no further ledger to the other's target while that one is in
    /// flight: passes which follow replies at once never over-correct primary balance into a cycle of hand-offs.
    /// </summary>
    [Fact]
    public async Task APassCountsTheHandOffsInProgressWhichTheMapDoesNotShowYet()
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)]);
        ReportEveryNode();
        Warm(quarters);
        var handOffs = RunPassCompletingPrepareReleases().Transitions;
        Assert.Equal([Node(2), Node(3)], handOffs.Select(static sent => sent.Executor).Order(NodeOrder.Instance));
        var landed = handOffs.Single(sent => sent.Executor == Node(2));

        await ReplaceAsync(quarters.Single(ledger => landed.Transition.Closes.Contains(ledger.Id)), Node(2), Node(1), Node(3));

        Assert.True(_controller.TransitionCompleted(landed, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));
        Assert.True(_controller.PassDue);
        var after = _controller.RunPass();
        Assert.Empty(after.Transitions);
        Assert.Empty(after.PrepareReleases);
    }

    /// <summary>
    /// orleans.md §5.6: a takeover of an ended claim is sent though the map's room is less than its own growth, which it
    /// overruns; only balancing waits for room.
    /// </summary>
    [Fact]
    public async Task ATakeoverIsSentWithLessRoomThanItsGrowth()
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        var map = _view.Current;
        var growth = map.GrowthOf(new Transition.Replace(root.Id, new TransitionChoice(Node(2), [Node(2), Node(3), Node(4)], 4, _revision, map.Epoch)));
        await CreateControllerAsync(map.EncodedLength + growth - 1);
        _options.MaxConcurrentTransitions = 0;
        Kill(1);

        var takeover = Assert.Single(_controller.RunPass().Transitions);

        Assert.Equal(TransitionPurpose.Takeover, takeover.Purpose);
    }

    /// <summary>
    /// orleans.md §5.6: with the open ranges at the target and every ledger warm, primary balance hands a ledger from the
    /// node with the most primaries to the one with the fewest, preferring one which names the target among its replicas,
    /// and never one the target failed.
    /// </summary>
    [Fact]
    public async Task PrimaryBalanceHandsALedgerToTheNodeWithTheFewestPrimaries()
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(4)], [Node(1), Node(2), Node(4)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(4)]);
        ReportEveryNode();
        Warm(quarters);

        var handOff = Assert.Single(RunPassWithPrepareFallback().Transitions);
        Assert.Equal((TransitionPurpose.Handoff, Node(3)), (handOff.Purpose, handOff.Executor));
        Assert.Equal(quarters[2].Id, Assert.IsType<Transition.Replace>(handOff.Transition.Value).Ledger);

        _controller.TransitionCompleted(handOff, TransitionReply.Declined.Instance);
        var next = Assert.Single(RunPassWithPrepareFallback().Transitions);
        Assert.Equal(Node(3), next.Executor);
        Assert.NotEqual(quarters[2].Id, Assert.IsType<Transition.Replace>(next.Transition.Value).Ledger);
    }

    /// <summary>
    /// orleans.md §5.6: with the open ranges at the target, every ledger warm, and each node's replicas even, a pass sends a
    /// hand-off only while two nodes' primaries differ by more than one: with one node primary of two ledgers and another
    /// of none, it hands one of the two to the node with none, and with every node primary of one, it sends nothing,
    /// so the map stays as it is. Tests which pick ledgers by their primary rely on the latter
    /// (OrleansControllerTests.AHeldDrainHoldsNoOtherLedgersDropAndTheStopWaitsForIt).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APassAtTheTargetWithBalancedPrimariesSendsNothing(bool balanced)
    {
        Register(1, 2, 3, 4);
        var quarters = balanced
            ? await QuartersAsync([Node(1), Node(2), Node(3)], [Node(2), Node(3), Node(4)], [Node(3), Node(4), Node(1)], [Node(4), Node(1), Node(2)])
            : await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(3), Node(4)], [Node(2), Node(4), Node(1)], [Node(3), Node(4), Node(2)]);
        ReportEveryNode();
        Warm(quarters);

        var sent = RunPassCompletingPrepareReleases().Transitions;

        if (balanced)
        {
            Assert.Empty(sent);
        }
        else
        {
            var handOff = Assert.Single(sent);
            Assert.Equal((TransitionPurpose.Handoff, Node(4)), (handOff.Purpose, handOff.Executor));
            Assert.Equal(Node(1), _view.Current.Get(Assert.IsType<Transition.Replace>(handOff.Transition.Value).Ledger)!.Value.Primary.Node);
        }
    }

    /// <summary>
    /// orleans.md §5.6: a target's failure is a preference, never an exclusion: once the node with the fewest primaries has
    /// failed every ledger the node with the most could hand it, as a node declines while its runtime starts, primary
    /// balance hands it one of them again rather than stalling with the counts two apart.
    /// </summary>
    [Fact]
    public async Task PrimaryBalanceRetriesATargetWhichFailedEveryCandidate()
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(4)], [Node(1), Node(2), Node(4)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(4)]);
        ReportEveryNode();
        Warm(quarters);
        var declined = new HashSet<LedgerId>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var handOff = Assert.Single(RunPassWithPrepareFallback().Transitions);
            Assert.Equal(Node(3), handOff.Executor);
            Assert.True(declined.Add(Assert.IsType<Transition.Replace>(handOff.Transition.Value).Ledger));
            Assert.False(_controller.TransitionCompleted(handOff, TransitionReply.Declined.Instance));
        }

        var again = Assert.Single(RunPassWithPrepareFallback().Transitions);

        Assert.Equal((TransitionPurpose.Handoff, Node(3)), (again.Purpose, again.Executor));
        Assert.Contains(Assert.IsType<Transition.Replace>(again.Transition.Value).Ledger, declined);
    }

    /// <summary>
    /// orleans.md §5.6, §5.9: placement ranks unused failure domains first, then nodes under no disk pressure, then fewest
    /// replicas: a repair fills the leaving replica's place with node 5 rather than node 4, under pressure, though node 4
    /// holds fewer replicas; with domains, node 4 is placed all the same, being the only node in a domain the stayers do not
    /// use. Once node 4's report shows the pressure ended, it is placed first either way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANodeUnderDiskPressureIsPlacedLast(bool domains)
    {
        _options.MaxConcurrentTransitions = 0;
        if (domains)
        {
            Register((1, "a"), (2, "a"), (3, "c"), (4, "b"), (5, "a"));
        }

        // Node 5 is primary of the upper half, so it holds one replica more than node 4.
        _ = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(5), Node(1), Node(2)]);
        _ = _controller.Report(Idle(Node(4), Generation) with { DiskPressure = true });
        Retire(3);

        var repair = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Repair, repair.Purpose);
        Assert.Equal([Node(1), Node(2), domains ? Node(4) : Node(5)], repair.Transition.Choice.Replicas);

        _controller.TransitionCompleted(repair, reply: null);
        _ = _controller.Report(Idle(Node(4), Generation));
        Assert.Equal([Node(1), Node(2), Node(4)], Assert.Single(_controller.RunPass().Transitions).Transition.Choice.Replicas);
    }

    /// <summary>
    /// orleans.md §5.6, §5.7: a discretionary roll adds no node under disk pressure: a domain roll whose only node in an
    /// unused domain is under pressure waits, and is sent once the pressure ends; a configuration roll which keeps its
    /// replicas, a pressured one among them, is sent.
    /// </summary>
    [Fact]
    public async Task ADiscretionaryRollAddsNoNodeUnderDiskPressure()
    {
        Register((1, "a"), (2, "a"), (3, "b"), (4, "c"));
        ReportEveryNode();
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _options.LedgersPerNode = 0;
        _ = _controller.Report(Idle(Node(4), Generation) with { DiskPressure = true });
        Assert.Empty(_controller.RunPass().Transitions);

        _ = _controller.Report(Idle(Node(4), Generation));
        var roll = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Roll, roll.Purpose);
        Assert.Equal([Node(1), Node(3), Node(4)], roll.Transition.Choice.Replicas);
        _controller.TransitionCompleted(roll, reply: null);

        Register(1, 2, 3, 4);
        _options.SequencerWindow = 8;
        _ = _controller.Report(Idle(Node(2), Generation) with { DiskPressure = true });
        var window = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Roll, 8), (window.Purpose, window.Transition.Choice.K));
        Assert.Equal([Node(1), Node(2), Node(3)], window.Transition.Choice.Replicas);
    }

    /// <summary>
    /// orleans.md §5.7: a takeover tries the ledger's replicas before the other nodes, and within each group the nodes under
    /// disk pressure last, since a primary's own pressure throttles it (OR-I10): a pressured replica still precedes a node
    /// outside the ledger, which would start with no copy of the ledger's entries.
    /// </summary>
    [Fact]
    public async Task ATakeoverTriesNodesUnderDiskPressureLastWithinEachGroup()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = _controller.Report(Idle(Node(2), Generation) with { DiskPressure = true });
        _ = _controller.Report(Idle(Node(4), Generation) with { DiskPressure = true });
        Kill(1);

        List<NodeId> executors = [];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var takeover = Assert.Single(_controller.RunPass().Transitions);
            Assert.Equal(TransitionPurpose.Takeover, takeover.Purpose);
            executors.Add(takeover.Executor);
            _controller.TransitionCompleted(takeover, reply: null);
        }

        Assert.Equal([Node(3), Node(2), Node(5), Node(4), Node(3)], executors);
    }

    /// <summary>
    /// orleans.md §5.7: a stopping primary's hand-off goes to a replica under no disk pressure ahead of an earlier one under
    /// pressure.
    /// </summary>
    [Fact]
    public async Task AHandOffGoesToAReplicaUnderNoDiskPressureFirst()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = _controller.Report(Idle(Node(2), Generation) with { DiskPressure = true });
        _controller.RequestHandOff(Node(1), Generation);

        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);

        Assert.Equal((TransitionPurpose.Departure, Node(3)), (handOff.Purpose, handOff.Executor));
    }

    /// <summary>
    /// orleans.md §5.6, OR-I10: primary balance hands no ledger to a node under disk pressure, whose primaries its pressure
    /// would throttle, and hands one to the node with the fewest primaries which is not.
    /// </summary>
    [Fact]
    public async Task PrimaryBalanceHandsNoLedgerToANodeUnderDiskPressure()
    {
        Register(1, 2, 3);
        ReportEveryNode();
        var quarters = await QuartersAsync([Node(1), Node(2), Node(4)], [Node(1), Node(2), Node(4)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(4)]);
        Warm(quarters);
        _ = _controller.Report(Idle(Node(3), Generation) with { DiskPressure = true });

        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);

        Assert.Equal((TransitionPurpose.Handoff, Node(2)), (handOff.Purpose, handOff.Executor));
    }

    /// <summary>
    /// orleans.md §5.6: balancing rolls a replica off the node with the most replicas, the ledger's primary executing, to a
    /// node with two fewer, until every count is within one of the others; a pass after the rolls applied sends none.
    /// </summary>
    [Fact]
    public async Task RollsEvenTheReplicaCounts()
    {
        Register(1, 2, 3, 4);
        ReportEveryNode();
        var quarters = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)], [Node(3), Node(1), Node(2)], [Node(4), Node(1), Node(2)]);
        Warm(quarters);

        var rolls = _controller.RunPass().Transitions;

        Assert.All(rolls, static roll => Assert.Equal(TransitionPurpose.Roll, roll.Purpose));
        Assert.Equal(
            [(quarters[1].Id, Node(2), [Node(2), Node(3), Node(4)]), (quarters[0].Id, Node(1), [Node(1), Node(3), Node(4)])],
            rolls.Select(static roll => (Assert.IsType<Transition.Replace>(roll.Transition.Value).Ledger, roll.Executor, roll.Transition.Choice.Replicas.ToArray())));
        foreach (var roll in rolls)
        {
            await ReplaceAsync(Assert.NotNull(_view.Current.Get(Assert.IsType<Transition.Replace>(roll.Transition.Value).Ledger)), [.. roll.Transition.Choice.Replicas]);
        }

        Assert.Empty(_controller.RunPass().Transitions);
    }

    /// <summary>
    /// orleans.md §5.6: replica evening sends no roll which would leave a ledger with more replicas sharing a failure
    /// domain, so no domain roll undoes it, and tries every node above the bound: nodes 1 and 3, with the most replicas,
    /// could give one up only to node 4, sharing its domain with node 2 in each of their ledgers, so node 2's replica of
    /// the first ledger goes to node 4 instead; after that roll applies, none remains.
    /// </summary>
    [Fact]
    public async Task RollsEvenTheReplicaCountsOnlyWhereNoDomainBecomesShared()
    {
        Register((1, "a"), (2, "b"), (3, "c"), (4, "b"));
        ReportEveryNode();
        var quarters = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)], [Node(3), Node(1), Node(2)], [Node(4), Node(1), Node(3)]);
        Warm(quarters);

        var roll = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Roll, quarters[0].Id), (roll.Purpose, Assert.IsType<Transition.Replace>(roll.Transition.Value).Ledger));
        Assert.Equal([Node(1), Node(3), Node(4)], roll.Transition.Choice.Replicas);
        await ReplaceAsync(quarters[0], [.. roll.Transition.Choice.Replicas]);
        Assert.True(_controller.TransitionCompleted(roll, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));

        Assert.Empty(_controller.RunPass().Transitions);
    }

    /// <summary>
    /// orleans.md §5.6: a replica dead past <c>G</c> is rolled out by a repair, not before; a pass in which nothing else
    /// changed sends it once the grace expires.
    /// </summary>
    [Fact]
    public async Task AReplicaDeadPastItsGraceIsRepairedOnlyOnceItExpires()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        Kill(3);
        Assert.Empty(_controller.RunPass().Transitions);

        _time.Advance(_options.ReplicaGrace);
        var repair = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(TransitionPurpose.Repair, repair.Purpose);
        Assert.Equal([Node(1), Node(2), Node(4)], repair.Transition.Choice.Replicas);
    }

    /// <summary>
    /// orleans.md §5.6, LR-I2: a replica its node reports lost is repaired at once, with no wait for <c>G</c>, within
    /// <c>MaxConcurrentRepairs</c>, onto a fresh replica of the successor on the same nodes, its primary executing, the
    /// primary's own lost replica among them; only a newly lost replica makes a pass due.
    /// </summary>
    [Fact]
    public async Task AReplicaItsNodeLostIsRepairedAtOnceInPlace()
    {
        _options.MaxConcurrentTransitions = 0;
        _options.MaxConcurrentRepairs = 1;
        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
        Assert.Empty(_controller.RunPass().Transitions);

        _ = _controller.Report(Idle(Node(3), Generation) with { LostReplicas = [lower.Id] });
        Assert.True(_controller.PassDue);
        var repair = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Repair, Node(1)), (repair.Purpose, repair.Executor));
        Assert.Equal(lower.Id, Assert.IsType<Transition.Replace>(repair.Transition.Value).Ledger);
        Assert.Equal(lower.Replicas, repair.Transition.Choice.Replicas);
        _ = _controller.Report(Idle(Node(3), Generation) with { LostReplicas = [lower.Id] });
        Assert.False(_controller.PassDue);

        // The primary's own replica: its repair waits for the budget, and the primary executes it.
        _ = _controller.Report(Idle(Node(2), Generation) with { LostReplicas = [upper.Id] });
        Assert.True(_controller.PassDue);
        Assert.Empty(_controller.RunPass().Transitions);
        await ReplaceAsync(lower, [.. repair.Transition.Choice.Replicas]);
        var own = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((TransitionPurpose.Repair, Node(2)), (own.Purpose, own.Executor));
        Assert.Equal(upper.Id, Assert.IsType<Transition.Replace>(own.Transition.Value).Ledger);
        Assert.Equal(upper.Replicas, own.Transition.Choice.Replicas);
    }

    /// <summary>
    /// orleans.md §5.6, LR-I2: with exactly <c>N</c> nodes which may be given work, a lost replica, the primary's own or
    /// another's, is repaired onto the same nodes; with fewer, each repair waits, counted and logged.
    /// </summary>
    [Fact]
    public async Task WithExactlyNNodesALostReplicaIsRepairedOntoTheSameNodes()
    {
        Register(1, 2, 3);
        _options.MaxConcurrentTransitions = 0;
        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
        _ = _controller.Report(Idle(Node(3), Generation) with { LostReplicas = [lower.Id] });
        _ = _controller.Report(Idle(Node(2), Generation) with { LostReplicas = [upper.Id] });

        var repairs = _controller.RunPass().Transitions;

        Assert.Equal(
            [(TransitionPurpose.Repair, Node(1), lower.Replicas), (TransitionPurpose.Repair, Node(2), upper.Replicas)],
            repairs.Select(static repair => (repair.Purpose, repair.Executor, repair.Transition.Choice.Replicas)));
        foreach (var repair in repairs)
        {
            _controller.TransitionCompleted(repair, reply: null);
        }

        _controller.RequestHandOff(Node(3), Generation);
        Assert.Empty(_controller.RunPass().Transitions);
        Assert.Equal(2, _controller.UnplacedRepairs);
    }

    /// <summary>
    /// orleans.md §5.6: a ledger whose <c>K</c> differs from the configuration rolls with no replica leaving; one whose
    /// replica other than the primary shares a failure domain with an earlier one rolls it out for a node with an unused
    /// domain; a requested WAL-release roll goes to the primary (OR-I13).
    /// </summary>
    [Theory]
    [InlineData(false, "window")]
    [InlineData(false, "domain")]
    [InlineData(true, "request")]
    public async Task RollsFollowTheConfigurationTheDomainsAndReleaseRequests(bool release, string cause)
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        switch (cause)
        {
            case "window":
                _options.SequencerWindow = 8;
                break;
            case "domain":
                Register((1, "a"), (2, "a"), (3, "b"), (4, "c"));
                break;
            default:
                _controller.RequestRoll(root.Id);
                break;
        }

        var roll = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal((release ? TransitionPurpose.WalReleaseRoll : TransitionPurpose.Roll, Node(1)), (roll.Purpose, roll.Executor));
        Assert.Equal(cause == "domain" ? [Node(1), Node(3), Node(4)] : [Node(1), Node(2), Node(3)], roll.Transition.Choice.Replicas);
        Assert.Equal(cause == "window" ? 8 : 4, roll.Transition.Choice.K);
    }

    /// <summary>
    /// orleans.md §5.6: the open ranges split toward <c>LedgersPerNode</c> per live active node, capped at the ranges the
    /// map's value limit holds while each lists a closed predecessor.
    /// </summary>
    [Fact]
    public async Task SplitsStopAtTheRangesTheMapsLimitHolds()
    {
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        await CreateControllerAsync(LimitFor(ranges: 3) - 1);
        var split = Assert.Single(_controller.RunPass().Transitions, static sent => sent.Purpose == TransitionPurpose.Split);
        _controller.TransitionCompleted(split, TransitionReply.Declined.Instance);

        var root = _view.Current.Ledgers[0];
        var (lower, upper) = Root.Children;
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lower, Node(1), Node(2), Node(3)), Opening(upper, Node(2), Node(1), Node(3))], _revision));
        Assert.DoesNotContain(_controller.RunPass().Transitions, static sent => sent.Purpose == TransitionPurpose.Split);
    }

    /// <summary>
    /// orleans.md §5.6: cold siblings sharing a primary merge while the open ranges exceed the target; none merges while one
    /// is warm, or while either's load is unknown, unreported, which is neither cold nor hot; a ledger whose load exceeds
    /// <c>SplitBytesPerSecond</c>, and none at it, splits beyond the target.
    /// </summary>
    [Fact]
    public async Task ColdSiblingsMergeAndHotLedgersSplitBeyondTheTarget()
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)], [Node(3), Node(1), Node(2)]);
        Assert.Empty(_controller.RunPass().Transitions);
        Warm(quarters[1]);
        Assert.Empty(_controller.RunPass().Transitions);
        _ = _controller.Report(Idle(Node(1), Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(quarters[1].Id, 0) });
        Assert.Empty(_controller.RunPass().Transitions);

        _ = _controller.Report(Idle(Node(1), Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(quarters[0].Id, 0).Add(quarters[1].Id, 0) });
        var merge = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(new Transition.Merge(quarters[0].Id, quarters[1].Id, merge.Transition.Choice), merge.Transition);

        _controller.TransitionCompleted(merge, TransitionReply.Declined.Instance);
        _ = _controller.Report(Idle(Node(1), Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(quarters[1].Id, _options.SplitBytesPerSecond) });
        Assert.Empty(_controller.RunPass().Transitions);
        _ = _controller.Report(Idle(Node(1), Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(quarters[1].Id, _options.SplitBytesPerSecond + 1) });
        var hot = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(new Transition.Split(quarters[1].Id, hot.Transition.Choice), hot.Transition);
    }

    /// <summary>
    /// orleans.md §5.6: <c>MaxConcurrentTransitions</c> limits balancing and never takeovers; and a takeover's growth is
    /// charged to the map's room, which it may overrun, so a split which the room held before waits once the takeover is
    /// sent.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBudgetAndTheMapsRoomLimitBalancingButNeverTakeovers(bool budget)
    {
        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
        var map = _view.Current;
        var choice = new TransitionChoice(Node(1), [Node(1), Node(2), Node(3)], 4, _revision, map.Epoch);
        var reserve = 1L * map.GrowthOf(new Transition.Replace(upper.Id, choice));
        // Before the takeover, the room beyond the reserve holds a split; once the takeover's growth is charged, not even a
        // hand-off fits.
        var replace = map.GrowthOf(new Transition.Replace(upper.Id, choice));
        Assert.True(map.GrowthOf(new Transition.Split(lower.Id, choice)) < 2 * replace);
        var limit = map.EncodedLength + reserve + 2L * replace - 1;
        await CreateControllerAsync(budget ? 1 << 20 : (int)limit);
        if (budget)
        {
            _options.MaxConcurrentTransitions = 0;
        }
        else
        {
            var probe = _controller.RunPass().Transitions;
            Assert.Contains(probe, static sent => sent.Purpose == TransitionPurpose.Split);
            foreach (var sent in probe)
            {
                _controller.TransitionCompleted(sent, TransitionReply.Declined.Instance);
            }
        }

        Kill(2);
        var pass = _controller.RunPass().Transitions;
        Assert.Equal([TransitionPurpose.Takeover], pass.Select(static sent => sent.Purpose));
    }

    /// <summary>
    /// OR-I19: a report reply names the newest epoch the controller knows durable: a verdict's epoch raises it, a lower one
    /// never lowers it, and <c>Refused</c>, <c>Declined</c>, and a lost reply leave it, even though the controller's view
    /// read none of them.
    /// </summary>
    [Fact]
    public void AReportReplyNamesTheNewestEpochTheControllerKnowsDurable()
    {
        var bootstrap = Assert.Single(_controller.RunPass().Transitions);
        Assert.Equal(0ul, Reply());

        _controller.TransitionCompleted(bootstrap, new LedgerMapCommitOutcome.Applied(5));
        Assert.Equal(5ul, Reply());
        _controller.TransitionCompleted(bootstrap, new LedgerMapCommitOutcome.Superseded(7));
        Assert.Equal(7ul, Reply());
        _controller.TransitionCompleted(bootstrap, new LedgerMapCommitOutcome.Applied(6));
        _controller.TransitionCompleted(bootstrap, LedgerMapCommitOutcome.Refused.Instance);
        _controller.TransitionCompleted(bootstrap, TransitionReply.Declined.Instance);
        _controller.TransitionCompleted(bootstrap, reply: null);
        Assert.Equal(7ul, Reply());
        Assert.Equal(0ul, _view.Current.Epoch);

        ulong Reply() => _controller.Report(Idle(Node(2), Generation)).MapEpoch;
    }

    /// <summary>
    /// orleans.md §5.6: on archive logs, a new activation's replies assign no share until every node its view lists live
    /// and active reported since it activated; then each share is the work-conserving one, the floor and the node's
    /// proportion of the undrained keys of the budget the floors leave, capped by the budget the others' reservations leave,
    /// so the shares sum to the budget exactly (OR-I21). A replaced process's reply, and the LSM's, assign none.
    /// </summary>
    [Fact]
    public async Task ANewActivationAssignsNoShareUntilEveryNodeReportedAndThenSharesTheWholeBudget()
    {
        Assert.Null(_controller.Report(Undrained(1, 100)).ArchiveLogShare);
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        long[] keys = [300, 100, 0, 0, 0];

        foreach (var node in new[] { 1, 2, 3, 4 })
        {
            Assert.Null(_controller.Report(Undrained(node, keys[node - 1])).ArchiveLogShare);
        }

        // Five nodes, a floor of 10, and 950 shared by 400 keys; each share is capped by what the others hold.
        Assert.Equal(10, Share(5, 0));
        Assert.Equal(722.5, Share(1, 300));
        Assert.Equal(247.5, Share(2, 100));
        Assert.Equal(10, Share(3, 0));
        Assert.Equal(10, Share(4, 0));
        Assert.Null(_controller.Report(Undrained(2, 0) with { Generation = Generation - 1 }).ArchiveLogShare);

        double Share(int node, long undrained) => _controller.Report(Undrained(node, undrained)).ArchiveLogShare!.Value;
    }

    /// <summary>
    /// OR-I22: a node which stops reporting leaves the controller's totals once its report is twice <c>ReplyIntervals</c>
    /// control intervals old, and its keys stop shrinking the others' shares; a node the view shows dead or retired whose
    /// reports still arrive is still running, so it stays reserved, counts no keys, and is assigned its floor.
    /// </summary>
    [Fact]
    public async Task ASilentNodeLeavesTheTotalsAndADeadOrRetiredNodeStillReportingStaysReservedAtItsFloor()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        Register(1, 2);
        _ = _controller.Report(Undrained(1, 900, rate: null));
        Assert.Equal(10 + 980 * 100 / 1_000.0, _controller.Report(Undrained(2, 100, rate: null)).ArchiveLogShare!.Value, 9);

        _time.Advance(_options.ControlInterval * (2 * ArchiveLogRequestShare.ReplyIntervals) + TimeSpan.FromTicks(1));
        _ = _controller.RunPass();
        Assert.Equal(1_000 - 10.0, _controller.Report(Undrained(2, 100, rate: null)).ArchiveLogShare!.Value, 9);
        Assert.Equal(10, _controller.Report(Undrained(1, 900, rate: null)).ArchiveLogShare);

        // Node 1 is dead in the view, but its reports arrive: it keeps its reservation, and its keys count nothing.
        Kill(1);
        _ = _controller.RunPass();
        Assert.Equal(990, _controller.Report(Undrained(2, 100, rate: 990)).ArchiveLogShare);
        Assert.Equal(10, _controller.Report(Undrained(1, 900, rate: 10)).ArchiveLogShare);

        _time.Advance(_options.ControlInterval * (2 * ArchiveLogRequestShare.ReplyIntervals) + TimeSpan.FromTicks(1));
        _ = _controller.RunPass();
        Assert.Equal(1_000, _controller.Report(Undrained(2, 100, rate: 990)).ArchiveLogShare);
    }

    /// <summary>
    /// OR-I21: the reviewer's stuck case. Three nodes run at a third of the budget each; one is retired but keeps reporting,
    /// as a retired node draining what it owes does. It stays reserved and decays through its floor share, and after each
    /// node's next report the rates sum to at most the budget and its floors, and after the next round to the budget.
    /// </summary>
    [Fact]
    public async Task ARetiredNodeWhichKeepsReportingStaysReservedSoTheRatesReturnWithinTheBudget()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        Register(1, 2, 3);
        var rates = new double?[4];
        Round();
        Round();
        Assert.Equal(1_000, rates.Sum(static rate => rate ?? 0), 6);

        Retire(1);
        for (var round = 0; round < 20; round++)
        {
            _ = _controller.RunPass();
            Round();
            var sum = rates.Sum(static rate => rate ?? 0);
            Assert.True(sum <= 1_000 + 3 * 10 + 1e-6, $"OR-I21: round {round}'s rates sum to {sum}.");
            if (round > 0)
            {
                Assert.True(sum <= 1_000 + 1e-6, $"OR-I21: round {round}'s rates sum to {sum}.");
            }
        }

        Assert.Equal(10, rates[1]);

        void Round()
        {
            foreach (var node in new[] { 1, 2, 3 })
            {
                if (_controller.Report(Undrained(node, 100, rates[node])).ArchiveLogShare is { } share)
                {
                    rates[node] = share;
                }
            }
        }
    }

    /// <summary>
    /// OR-I21: a report its node gave up on, which reaches the controller after a newer one of the same process, is answered
    /// with no share and ignored, so the node stays reserved at the rate it runs at. Applied, the stale report's low rate
    /// would let the other node take the difference, and the two would run at about 1479 of 1000 (the reviewed probe).
    /// </summary>
    [Fact]
    public async Task AReportOvertakenByANewerOneOfItsProcessIsIgnored()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        Register(1, 2);
        _ = _controller.Report(Undrained(1, 1_000));
        var second = _controller.Report(Undrained(2, 10)).ArchiveLogShare!.Value;
        var stale = Undrained(1, 10, rate: 20.8);
        var first = _controller.Report(Undrained(1, 1_000, rate: null)).ArchiveLogShare!.Value;

        Assert.Null(_controller.Report(stale).ArchiveLogShare);
        var share = _controller.Report(Undrained(2, 10, rate: second)).ArchiveLogShare!.Value;
        Assert.True(first + share <= 1_000 + 1e-6, $"OR-I21: the nodes run at {first} and {share}.");
    }

    /// <summary>
    /// OR-I21: a new activation waits for every node its view shows up, a retired one, which may run at an earlier
    /// activation's share while it drains what it owes, among them; it assigns once that node reported.
    /// </summary>
    [Fact]
    public async Task ANewActivationWaitsForEveryUpNodeRetiredOnesIncluded()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        Register(1, 2, 3);
        Retire(3);
        await CreateControllerAsync(1 << 20, archiveLogs: true);

        Assert.Null(_controller.Report(Undrained(1, 100, rate: 300)).ArchiveLogShare);
        Assert.Null(_controller.Report(Undrained(2, 100, rate: 300)).ArchiveLogShare);
        Assert.NotNull(_controller.Report(Undrained(3, 100, rate: 300)).ArchiveLogShare);
        Assert.NotNull(_controller.Report(Undrained(1, 100, rate: 300)).ArchiveLogShare);
    }

    /// <summary>
    /// OR-I21, #410: a node whose report finds the budget held by the others' reservations is assigned no share, so its
    /// drains have none, until a later reply finds room: here two nodes still run at an earlier activation's rates, which
    /// fill the budget, as a node whose blocked drain owes keys holds most of it; once that node reports its keys drained,
    /// its reply lowers it, but it stays reserved at the rate it runs at until it reports the lower rate, and only the third
    /// node's report after that is assigned a share.
    /// </summary>
    [Fact]
    public async Task ANodeTheBudgetHasNoRoomForIsAssignedAShareOnlyOnceAnotherReleasesIt()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        Register(1, 2, 3);
        _ = _controller.Report(Undrained(1, 1_000, rate: 600));
        _ = _controller.Report(Undrained(2, 1_000, rate: 400));

        Assert.Null(_controller.Report(Undrained(3, 0)).ArchiveLogShare);

        var drained = _controller.Report(Undrained(1, 0, rate: 600)).ArchiveLogShare!.Value;
        Assert.True(drained < 600, $"The drained node is lowered from 600 to {drained}.");
        Assert.Null(_controller.Report(Undrained(3, 0)).ArchiveLogShare);

        // The drained node runs at its lower share from its reply on, and its next report releases the rest.
        _ = _controller.Report(Undrained(1, 0, rate: drained));
        Assert.NotNull(_controller.Report(Undrained(3, 0)).ArchiveLogShare);
    }

    /// <summary>
    /// OR-I21: after a rolling restart lowers the budget, a new activation finds no room for nodes still running at the old
    /// activation's shares, and its replies lower each to its floor, so after one round the rates are within the new budget
    /// and the floors; once each reports its lower rate, the budget is shared again.
    /// </summary>
    [Fact]
    public void AReplyWithNoRoomLowersANodeAboveItsFloorToIt()
    {
        var shares = Shares(250, 2, out var view, out var time);
        var floor = ArchiveLogRequestShare.Floor(250, 2);
        Assert.Null(shares.Report(Node(1), 100, rate: 600, view, time.GetUtcNow()));
        Assert.Equal(floor, shares.Report(Node(2), 100, rate: 400, view, time.GetUtcNow()));
        Assert.Equal(floor, shares.Report(Node(1), 100, rate: 600, view, time.GetUtcNow()));

        Assert.Null(shares.Report(Node(2), 100, rate: floor, view, time.GetUtcNow()));
        Assert.Equal(125, shares.Report(Node(1), 100, rate: floor, view, time.GetUtcNow()));
        Assert.Equal(125, shares.Report(Node(2), 100, rate: floor, view, time.GetUtcNow()));
    }

    /// <summary>
    /// OR-I21, D1: an activation which has not observed its registry view knows of no node to wait for, so a report under
    /// the empty view is answered with no share, however long the activation waits; once the view is observed, it waits
    /// for every node it lists.
    /// </summary>
    [Fact]
    public void AnActivationAssignsNoShareUnderAnUnobservedView()
    {
        var shares = Shares(1_000, 3, out var view, out var time);
        Assert.Null(shares.Report(Node(1), 100, rate: 250, NodeView.Empty, time.GetUtcNow()));
        Assert.Null(shares.Report(Node(2), 100, rate: 250, view, time.GetUtcNow()));
        Assert.Null(shares.Report(Node(3), 100, rate: 250, view, time.GetUtcNow()));
        Assert.NotNull(shares.Report(Node(1), 100, rate: 250, view, time.GetUtcNow()));

        // However long an activation waits, an empty view never makes it ready; once observed, the wait has passed.
        shares = Shares(1_000, 3, out view, out time);
        time.Advance(_options.ControlInterval * (4 * ArchiveLogRequestShare.ReplyIntervals));
        Assert.Null(shares.Report(Node(1), 100, rate: 250, NodeView.Empty, time.GetUtcNow()));
        Assert.NotNull(shares.Report(Node(1), 100, rate: 250, view, time.GetUtcNow()));
    }

    /// <summary>
    /// orleans.md §5.6: a second drainer counts the ledger it drains too, which its first drainer counts as well, and the two
    /// come to share what the ledger's keys earn: the second gets only what the first's reservation leaves until the first
    /// reports the lower rate its reply gave it, so the rates never sum above the budget (OR-I21).
    /// </summary>
    [Fact]
    public async Task ASecondDrainerSharesTheLedgersBudgetOnceTheFirstReportsItsLowerRate()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        Register(1, 2, 3);

        // The ledger's one segment, which each drainer counts as MaxSegmentKeys until its scan learns it.
        const long Segment = 4_096;
        _ = _controller.Report(Undrained(1, Segment));
        _ = _controller.Report(Undrained(2, 0));
        Assert.Equal(10, _controller.Report(Undrained(3, 0)).ArchiveLogShare);
        Assert.Equal(980, _controller.Report(Undrained(1, Segment)).ArchiveLogShare);

        // Node 2 drains the ledger too: the work-conserving share is 495, but node 1 still runs at 980.
        Assert.Equal(10, _controller.Report(Undrained(2, Segment)).ArchiveLogShare);
        Assert.Equal(495, _controller.Report(Undrained(1, Segment, rate: 980)).ArchiveLogShare);
        Assert.Equal(10, _controller.Report(Undrained(2, Segment, rate: 10)).ArchiveLogShare);
        _ = _controller.Report(Undrained(1, Segment, rate: 495));
        Assert.Equal(495, _controller.Report(Undrained(2, Segment, rate: 10)).ArchiveLogShare);
    }

    /// <summary>
    /// orleans.md §5.6: a restarted node, which drains nothing until a reply gives it a share and reports no rate, replaces
    /// its predecessor's report and reservation, so its first reply gives it its share at once, which the predecessor's
    /// reservation would otherwise have taken.
    /// </summary>
    [Fact]
    public async Task ARestartedNodesReportReplacesItsPredecessorsReservation()
    {
        _options.ArchiveLogRequestsPerSecond = 1_000;
        await CreateControllerAsync(1 << 20, archiveLogs: true);
        Register(1, 2);
        _ = _controller.Report(Undrained(1, 900));
        Assert.Equal(108, _controller.Report(Undrained(2, 100)).ArchiveLogShare!.Value, 9);
        Assert.Equal(892, _controller.Report(Undrained(1, 900)).ArchiveLogShare!.Value, 9);

        _nodes[Node(1)] = _nodes[Node(1)] with { Generation = Generation + 1 };
        Observe();
        Assert.Equal(892, _controller.Report(Undrained(1, 900) with { Generation = Generation + 1 }).ArchiveLogShare!.Value, 9);
        Assert.Equal(108, _controller.Report(Undrained(2, 100, rate: 108)).ArchiveLogShare!.Value, 9);
    }

    /// <summary>
    /// OR-I21 over one snapshot: once every node reported, a round of reports assigns shares which sum to at most the budget,
    /// whatever the nodes' keys, each at most the work-conserving share.
    /// </summary>
    [Fact]
    public void TheSharesOfOneSnapshotSumToAtMostTheBudget() =>
        Gen.Select(Gen.Int[1, 60], Gen.Long[0, 1L << 20].Array[60], Gen.Double[1, 50_000])
            .Sample(
                sample =>
                {
                    var (count, keys, budget) = sample;
                    var shares = Shares(budget, count, out var view, out var time);
                    for (var node = 1; node <= count; node++)
                    {
                        _ = shares.Report(Node(node), keys[node - 1], rate: null, view, time.GetUtcNow());
                    }

                    var assigned = new double[count];
                    for (var node = 1; node <= count; node++)
                    {
                        assigned[node - 1] = shares.Report(Node(node), keys[node - 1], rate: null, view, time.GetUtcNow()) ?? 0;
                    }

                    Assert.True(assigned.Sum() <= budget * (1 + 1e-9), $"The shares sum to {assigned.Sum()} of {budget}.");
                    Assert.True(shares.Reserved <= budget * (1 + 1e-9));
                },
                iter: 200);

    /// <summary>
    /// OR-I21 and OR-I22 over histories, on the rates the nodes really run at: five nodes, one registered late, report in
    /// any order with random keys; replies are lost; nodes go silent, as a node whose report call hangs does; the view
    /// shows nodes dead and alive again, retires them, and registers the late one; the controller moves to new activations,
    /// some over a view not yet observed; and a rolling restart changes the budget. After every step the nodes above the higher
    /// of their floor and the floor of the ready activation they last heard from, by a reply with a share or without one, run
    /// at most at its budget together, so a node a newer activation waited for counts at the newer budget once it heard from
    /// it; no share comes
    /// under an unobserved view; and a node whose share no reply renewed for <c>ReplyIntervals</c> intervals runs at most its floor. After a settling phase, in
    /// which every node not silent reports each interval, the rates sum to at most the budget and the silent nodes' floors.
    /// </summary>
    [Fact]
    public void TheNodesRatesStayWithinTheBudgetAndASilentNodeFallsToItsFloor() =>
        Gen.Select(Gen.Int[0, 12], Gen.Int[0, ShareModel.Count - 1], Gen.Long[0, 1_000]).Array[20, 160]
            .Sample(
                steps =>
                {
                    var model = new ShareModel(_options.ControlInterval, _time.GetUtcNow());
                    foreach (var (kind, index, keys) in steps)
                    {
                        model.Step(kind, index, keys);
                        model.CheckStep();
                    }

                    model.Settle();
                    model.CheckSettled();
                },
                iter: 400);

    /// <summary>
    /// OR-I21, the reviewed case of seed <c>8eSveTwcS6xr</c>: a new activation does not wait for a node its view shows dead,
    /// so it gives the one live node the whole budget while the dead node, still running, keeps the floor an earlier
    /// activation assigned it; the dead node's report then finds no room and, being at the floor, is assigned nothing. The
    /// rates pass the budget by that floor, a rate above what now fits, until the live node reports again. A restart which
    /// lowers the budget leaves both rates in place, the dead node's now above its new floor, and the model's bound counts
    /// it at the floor of the activation it last heard from.
    /// </summary>
    [Fact]
    public void ARateNoRoomWasFoundForPassesTheBudgetByAtMostTheFloorUntilTheOthersReport()
    {
        var restarted = Transient();
        // A rolling restart to a budget of 250, whose floor of 2.5 is below the dead node's rate.
        restarted.Step(4, 0, keys: 0);
        restarted.CheckStep();
        Assert.Equal([10, 1_000, 0, 0, 0], restarted.Rates());
        restarted.Settle();
        restarted.CheckSettled();

        var reported = Transient();
        reported.Step(11, 1, keys: 100);
        reported.CheckStep();
        Assert.Equal([10, 990, 0, 0, 0], reported.Rates());

        ShareModel Transient()
        {
            var model = new ShareModel(_options.ControlInterval, _time.GetUtcNow());
            (int Kind, int Node)[] steps =
            [
                (6, 0), (6, 2), (6, 3),    // Only the second node is live.
                (11, 0), (11, 1), (11, 0), // The first activation assigns the live node 990 and the dead one its floor, 10.
                (2, 0), (3, 0),            // A new activation observes the same view, so it waits for the live node only,
                (11, 1), (11, 0),          // gives it the whole budget, and finds no room for the dead node at its floor.
            ];
            foreach (var (kind, node) in steps)
            {
                model.Step(kind, node, keys: 100);
                model.CheckStep();
            }

            Assert.Equal([10, 1_000, 0, 0, 0], model.Rates());
            return model;
        }
    }

    /// <summary>
    /// <see cref="ArchiveLogNodeShare"/>: a node runs at no rate until its first reply assigns one, and then at the share the
    /// latest assigning reply gave; a reply which assigns none keeps the share and does not renew its deadline, so the node
    /// falls to its floor <c>ReplyIntervals</c> control intervals after the last assigning reply, the floor over its
    /// current registry view, and never rises to it; and a share which is not positive and finite is refused.
    /// </summary>
    [Fact]
    public void ANodeRunsOnlyAtTheSharesItReceivedAndFallsToItsFloorWithoutReplies()
    {
        var floor = 10.0;
        var share = new ArchiveLogNodeShare(() => floor, _options.ControlInterval);
        var deadline = _options.ControlInterval * ArchiveLogRequestShare.ReplyIntervals;
        Assert.Null(share.RateAt(_time.GetUtcNow()));

        share.Observe(400, _time.GetUtcNow());
        _time.Advance(deadline - TimeSpan.FromTicks(1));
        share.Observe(null, _time.GetUtcNow());
        Assert.Equal(400, share.RateAt(_time.GetUtcNow()));
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(10, share.RateAt(_time.GetUtcNow()));
        floor = 5;
        Assert.Equal(5, share.RateAt(_time.GetUtcNow()));

        share.Observe(3, _time.GetUtcNow());
        _time.Advance(deadline);
        Assert.Equal(3, share.RateAt(_time.GetUtcNow()));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => share.Observe(0, _time.GetUtcNow()));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => share.Observe(double.NaN, _time.GetUtcNow()));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => share.Observe(double.PositiveInfinity, _time.GetUtcNow()));
        Assert.Equal(3, share.Assigned);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveLogRequestShare.Floor(1_000, nodes: 0));
        Assert.Equal(1_000 / 200.0, ArchiveLogRequestShare.Floor(1_000, nodes: 100));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ControlOptions { ArchiveLogRequestsPerSecond = 0.5 }.Validate());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ControlOptions { ArchiveLogRequestsPerSecond = double.PositiveInfinity }.Validate());
    }

    /// <summary>
    /// orleans.md §5.6: a closed ledger whose drain made no progress is drained by one more live replica after its first
    /// for each <c>ClosedDrainTimeout</c> which passed, dead ones skipped and capped at its replicas; its first live replica
    /// at once when its first replica is dead; and reported progress restarts the clock.
    /// </summary>
    [Fact]
    public async Task ClosedLedgersAreDrainedTooByMoreLiveReplicasAsTheirDrainsStall()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        await ReplaceAsync(root, Node(4), Node(2), Node(5));
        _ = _controller.RunPass();
        Assert.Empty(DrainToo());

        _time.Advance(_options.ClosedDrainTimeout);
        _ = _controller.RunPass();
        Assert.Equal([Node(2)], DrainToo());

        Kill(2);
        _ = _controller.RunPass();
        Assert.Equal([Node(3)], DrainToo());

        Revive(2);
        _time.Advance(_options.ClosedDrainTimeout * 5);
        _ = _controller.RunPass();
        Assert.Equal([Node(2), Node(3)], DrainToo());

        _ = _controller.Report(Idle(Node(2), Generation) with { ClosedDrainProgress = ImmutableDictionary<LedgerId, DateTimeOffset>.Empty.Add(root.Id, _time.GetUtcNow()), Draining = true });
        _ = _controller.RunPass();
        Assert.Empty(DrainToo());

        Kill(1);
        _ = _controller.RunPass();
        Assert.Equal([Node(2)], DrainToo());

        // A replaced process of the node is told to drain nothing.
        Assert.Empty(_controller.Report(Idle(Node(2), Generation - 1)).DrainToo);

        NodeId[] DrainToo() => [.. Enumerable.Range(1, 4).Select(Node).Where(node => _controller.Report(Idle(node, Generation) with { Draining = true }).DrainToo.Contains(root.Id))];
    }

    /// <summary>
    /// orleans.md §5.6: the controller follows each node's own report of whether it drains, whatever else the report says: a
    /// closed ledger whose first replica reports it drains nothing is drained by its next replica which drains at once,
    /// without waiting for <c>ClosedDrainTimeout</c>, a replica which reports it drains nothing skipped, and once the first
    /// replica reports it drains again, no other is named; a refused node handing off whose archive gate passed, which keeps
    /// draining, keeps its ledgers. Before its first report, the first replica counts as draining.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task AClosedLedgerWhoseFirstReplicaDrainsNothingIsDrainedByTheNextAtOnce(bool handingOff, bool gatePassed, bool draining)
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        await ReplaceAsync(root, Node(4), Node(2), Node(5));
        _ = _controller.RunPass();
        Assert.Empty(DrainToo());

        _ = _controller.Report(Idle(Node(1), Generation) with { HandingOff = handingOff, ArchiveGatePassed = gatePassed, Draining = draining });
        _ = _controller.RunPass();
        Assert.Equal(draining ? [] : [Node(3)], DrainToo());

        if (!draining)
        {
            _ = _controller.Report(Idle(Node(1), Generation) with { HandingOff = handingOff, ArchiveGatePassed = true, Draining = true });
            _ = _controller.RunPass();
            Assert.Empty(DrainToo());
        }

        NodeId[] DrainToo() => [.. new[] { (Node(2), false), (Node(3), true), (Node(4), true) }.Where(entry => _controller.Report(Idle(entry.Item1, Generation) with { Draining = entry.Item2 }).DrainToo.Contains(root.Id)).Select(static entry => entry.Item1)];
    }

    /// <summary>
    /// orleans.md §5.6: a node the activation has not heard from counts as draining, so a first replica which never reported
    /// gains no second drainer at the first pass; and a replica which reports it drains nothing is skipped among a stalled
    /// drain's extra drainers, the next replica which drains named after one <c>ClosedDrainTimeout</c>.
    /// </summary>
    [Fact]
    public async Task ANodeNotHeardFromDrainsAndOneWhichDrainsNothingIsSkippedAsAnExtraDrainer()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        await ReplaceAsync(root, Node(4), Node(2), Node(5));
        _ = _controller.Report(Idle(Node(2), Generation) with { Draining = true });
        _ = _controller.Report(Idle(Node(3), Generation) with { Draining = true });
        _ = _controller.RunPass();
        Assert.Empty(Named());

        _time.Advance(_options.ClosedDrainTimeout);
        _ = _controller.RunPass();
        Assert.Equal([Node(3)], Named());

        NodeId[] Named() => [.. new[] { (Node(2), false), (Node(3), true) }.Where(entry => _controller.Report(Idle(entry.Item1, Generation) with { Draining = entry.Item2 }).DrainToo.Contains(root.Id)).Select(static entry => entry.Item1)];
    }
    /// <summary>
    /// archive-lsm.md §8.8: an overfull partition is assigned once, to the primary of the open ledger containing its lowest
    /// key, with the manifest's epoch, until its outcome is decided or <c>CompactionTimeout</c> passes, after which it is
    /// sent again to the range's primary, the same live node or a new one; a late reply of an earlier assignment changes
    /// nothing; a failed call, reported as no reply, ends the assignment at once; and once decided, the partition is
    /// assigned again from the next manifest value which lists it overfull.
    /// </summary>
    [Fact]
    public async Task AnOverfullPartitionIsAssignedOnceUntilDecidedOrTimedOut()
    {
        _options.MaxConcurrentTransitions = 0;
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        var epoch = await OverfillAsync(Root, 3);

        var first = Assert.Single(_controller.AssignCompactions());
        Assert.Equal(new CompactionAssignment(Root, Node(1), epoch), first);
        Assert.Empty(_controller.AssignCompactions());
        _time.Advance(_options.CompactionTimeout);
        var again = Assert.Single(_controller.AssignCompactions());
        Assert.Equal(Node(1), again.Assignee);
        _controller.CompactionCompleted(first, CompactionReply.NothingToCompact.Instance);
        Assert.Empty(_controller.AssignCompactions());

        _controller.CompactionCompleted(again, reply: null);
        await ReplaceAsync(root, Node(2), Node(3), Node(4));
        var moved = Assert.Single(_controller.AssignCompactions());
        Assert.Equal(Node(2), moved.Assignee);

        _controller.CompactionCompleted(moved, CompactionReply.NothingToCompact.Instance);
        _ = await OverfillAsync(Root, 1, from: 3);
        _ = Assert.Single(_controller.AssignCompactions());
    }

    /// <summary>
    /// A compaction whose reply was lost holds its assignee's budget no longer than its partition stays overfull or its
    /// timeout: its node compacted the partition, and the next overfull partition of that node is assigned.
    /// </summary>
    [Fact]
    public async Task ALostCompactionReplyHoldsItsAssigneesBudgetNoLonger()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)]);
        var (lower, upper) = Root.Children;
        _ = await OverfillAsync(lower, 3);
        var lost = Assert.Single(_controller.AssignCompactions());
        Assert.Equal((lower, Node(1)), (lost.Partition, lost.Assignee));

        // The node compacts the partition and commits, and the reply is lost.
        ImmutableArray<SectionRef> inputs = [.. _manifest.Current!.Value.Sections.Where(section => section.Partition == lower)];
        var (first, last) = (inputs.Min(static section => section.MinTag), inputs.Max(static section => section.MaxTag));
        var output = new SectionRef(new ObjectName("c/1/0/compacted"), 0, 30, lower, first, last, new ObjectRange(30, 1));
        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await _controller.CommitManifestAsync(new ManifestChange.Compaction(inputs, [output]), Token)).Value);
        _ = await OverfillAsync(upper, 3, from: 3);
        _time.Advance(_options.CompactionTimeout);

        var next = Assert.Single(_controller.AssignCompactions());
        Assert.Equal((upper, Node(1)), (next.Partition, next.Assignee));
    }

    /// <summary>archive-lsm.md §8.8: an assignee has at most about <c>CompactionBudgetBytes</c>' worth of compactions in progress.</summary>
    [Fact]
    public async Task AnAssigneeHasAtMostItsBudgetOfCompactionsInProgress()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)]);
        var (lower, upper) = Root.Children;
        _ = await OverfillAsync(lower, 3);
        _ = await OverfillAsync(upper, 3, from: 3);

        var assigned = _controller.AssignCompactions();

        Assert.Equal(Math.Max(1, Archive.CompactionBudgetBytes / (2L * (1L << Archive.GridBits) * Archive.SectionBytes)), assigned.Length);
    }

    /// <summary>
    /// archive-lsm.md §8.8: a partition whose compaction answered <c>Corrupt</c> is assigned no more in this activation,
    /// past any number of timeouts, until an operator resolves it; durable corruption lies outside the fault model.
    /// </summary>
    [Fact]
    public async Task ACorruptPartitionIsNotAssignedAgainInTheActivationUntilResolved()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = await OverfillAsync(Root, 3);
        var corrupt = Assert.Single(_controller.AssignCompactions());

        _controller.CompactionCompleted(corrupt, new CompactionReply.Corrupt(Root, "The section of d/1/0/s at offset 0 breaks its format."));
        for (var timeout = 0; timeout < 3; timeout++)
        {
            _time.Advance(_options.CompactionTimeout);
            Assert.Empty(_controller.RunPass().Compactions);
        }

        Assert.True(_controller.ResolveCorruptPartition(Root));
        Assert.False(_controller.ResolveCorruptPartition(Root));
        _ = Assert.Single(_controller.AssignCompactions());
    }

    /// <summary>
    /// orleans.md §5.6: only a reply which freed budget, <c>Applied</c> or <c>Superseded</c>, makes a pass due before the
    /// host's timed one, so a refused, declined, rejected, or failed transition is sent again at most once a timed pass;
    /// a new hand-off request, a newly unserved ledger, and a node whose claims end make one due too, and a pass clears it.
    /// </summary>
    [Fact]
    public async Task OnlyAReplyWhichFreesBudgetOrANewClaimToEndMakesAPassDue()
    {
        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
        _options.MaxConcurrentTransitions = 0;
        Kill(1);
        Assert.True(_controller.PassDue);
        var takeover = Assert.Single(_controller.RunPass().Transitions);
        Assert.False(_controller.PassDue);

        foreach (TransitionReply? reply in new TransitionReply?[] { LedgerMapCommitOutcome.Refused.Instance, TransitionReply.Declined.Instance, new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap), null })
        {
            Assert.False(_controller.TransitionCompleted(takeover, reply));
            Assert.False(_controller.PassDue);
        }

        Assert.True(_controller.TransitionCompleted(takeover, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));
        Assert.True(_controller.PassDue);
        _ = _controller.RunPass();

        _controller.RequestHandOff(Node(3), Generation);
        Assert.True(_controller.PassDue);
        _ = _controller.RunPass();
        _controller.RequestHandOff(Node(3), Generation);
        Assert.False(_controller.PassDue);

        _ = _controller.Report(Idle(Node(2), Generation) with { Unserved = [upper.Id] });
        Assert.True(_controller.PassDue);
        _ = _controller.RunPass();
        _ = _controller.Report(Idle(Node(2), Generation) with { Unserved = [upper.Id] });
        Assert.False(_controller.PassDue);
        _ = lower;
    }

    /// <summary>
    /// orleans.md §5.7: a departing node, its silo shutting down, keeps its claims, so its ledgers are handed off rather
    /// than taken over; it executes no transition and is named no replica.
    /// </summary>
    [Fact]
    public async Task ADepartingNodesLedgersDepartAndItIsGivenNoWork()
    {
        Register(1, 2, 3, 4);
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _membership[Node(1)] = NodeLiveness.Departing.Instance;
        Observe();
        Assert.True(_controller.PassDue);

        var pass = RunPassCompletingPrepareReleases().Transitions;

        var departure = Assert.Single(pass, static sent => sent.Purpose == TransitionPurpose.Departure);
        Assert.Equal(Node(2), departure.Executor);
        Assert.DoesNotContain(pass, static sent => sent.Purpose == TransitionPurpose.Takeover);
        Assert.All(pass, static sent => Assert.DoesNotContain(Node(1), sent.Transition.Choice.Replicas));
    }

    /// <summary>
    /// orleans.md §5.6: departures run within <c>MaxConcurrentHandOffs</c>, apart from balancing, so a stop never waits
    /// behind it, even with balancing paused.
    /// </summary>
    [Fact]
    public async Task DeparturesRunWithinTheirOwnBudget()
    {
        Register(1, 2, 3, 4);
        _ = await QuartersAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)], [Node(2), Node(1), Node(3)]);
        _options.MaxConcurrentTransitions = 0;
        _options.MaxConcurrentHandOffs = 2;
        _controller.RequestHandOff(Node(1), Generation);

        Assert.Equal(2, RunPassCompletingPrepareReleases().Transitions.Count(static sent => sent.Purpose == TransitionPurpose.Departure));
        var idle = _controller.RunPass();
        Assert.Empty(idle.Transitions);
        Assert.Empty(idle.PrepareReleases);
    }

    /// <summary>
    /// orleans.md §5.6: the host reads the manifest through the controller's view apart from activation, so a quiet
    /// cluster still compacts after a move, and nothing is assigned until it has.
    /// </summary>
    [Fact]
    public async Task ReadingTheManifestLetsAQuietClusterCompact()
    {
        _options.MaxConcurrentTransitions = 0;
        _ = await InstallRootAsync(Node(1), Node(2), Node(3));
        _ = await OverfillAsync(Root, 3);
        await CreateControllerAsync(1 << 20, new ManifestView(_objects));
        Assert.Empty(_controller.AssignCompactions());

        await _controller.ReadManifestAsync(Token);

        _ = Assert.Single(_controller.AssignCompactions());
    }

    /// <summary>
    /// orleans.md §5.14: while any closed, undrained <c>ObjectStore</c> ledger overlaps a range, a diskful node without
    /// <c>ObjectLedgersReady</c> cannot become primary for that range (even when the target placement is <c>Nodes</c>)
    /// because serving the range requires reading the closed object-store history; once the closed ledger drops, it becomes
    /// eligible.
    /// </summary>
    [Fact]
    public async Task NodesWithoutObjectLedgersReadyDoNotBecomePrimaryWhileAnOverlappingObjectStoreLedgerExists()
    {
        Register(1, 2, 3, 4);
        var (lower, upper) = Root.Children;
        KeyRange[] quarters = [lower.Children.Lower, lower.Children.Upper, upper.Children.Lower, upper.Children.Upper];
        // Closed ObjectStore ledger over Root, with 4 open Nodes ledgers: 2 on Node(1), 1 on Node(2), 1 on Node(3), 0 on Node(4).
        await CommitAsync(new LedgerMapChange.Install([], [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 4, new PrimaryClaim(Node(1), SequencerInstanceId.New()))], _revision));
        var closedObj = Assert.Single(_view.Current.Ledgers);
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(closedObj.Id, new Slot(1))],
            [
                Opening(quarters[0], Node(1), Node(2), Node(4)),
                Opening(quarters[1], Node(1), Node(3), Node(4)),
                Opening(quarters[2], Node(2), Node(3), Node(4)),
                Opening(quarters[3], Node(3), Node(1), Node(2)),
            ],
            _revision));

        // Node(1..3) are ObjectLedgersReady; Node(4) has 0 primaries and is NOT ObjectLedgersReady.
        _ = _controller.Report(Idle(Node(1), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(2), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(3), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(4), Generation) with { ObjectLedgersReady = false });

        // Balancing cannot move a primary onto Node(4) while the closed ObjectStore ledger over Root still exists.
        Assert.Empty(_controller.RunPass().Transitions);

        // Once the closed ObjectStore ledger drops, Node(4) no longer needs ObjectLedgersReady to serve a Nodes ledger.
        await CommitAsync(new LedgerMapChange.Drop(closedObj.Id));
        var balanced = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal(Node(4), balanced.Executor);
        Assert.Equal(Node(4), balanced.Transition.Choice.Executor);
    }

    /// <summary>
    /// Audit #9 / orleans.md §5.14: <see cref="ClusterController"/>'s replica-balance roll-off skips any ledger whose
    /// primary cannot execute for an overlapping closed <c>ObjectStore</c> ledger (not <c>ObjectLedgersReady</c>), and
    /// rolls it once the closed <c>ObjectStore</c> ledger drops.
    /// </summary>
    [Fact]
    public async Task RollOffSkipsLedgersWhosePrimaryCannotExecuteForAnOverlappingObjectStoreLedger()
    {
        Register(1, 2, 3, 4);
        var (lower, upper) = Root.Children;
        KeyRange[] quarters = [lower.Children.Lower, lower.Children.Upper, upper.Children.Lower, upper.Children.Upper];

        // Closed ObjectStore ledger over Root; open Nodes ledgers have equal primary counts (1 each), while replica
        // counts are Node(1)=4, Node(2)=3, Node(3)=3, Node(4)=2. Only quarters[0] (primary Node(2)) carries Node(1)
        // without already carrying Node(4) or having Node(1) as primary.
        await CommitAsync(new LedgerMapChange.Install([], [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 4, new PrimaryClaim(Node(1), SequencerInstanceId.New()))], _revision));
        var closedObj = Assert.Single(_view.Current.Ledgers);
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(closedObj.Id, new Slot(1))],
            [
                Opening(quarters[0], Node(2), Node(1), Node(3)), // only RollOff candidate; primary Node(2) is NOT ObjectLedgersReady
                Opening(quarters[1], Node(1), Node(2), Node(3)), // primary is Node(1)
                Opening(quarters[2], Node(3), Node(1), Node(4)), // already carries Node(4)
                Opening(quarters[3], Node(4), Node(1), Node(2)), // already carries Node(4)
            ],
            _revision));
        Warm([.. _view.Current.Ledgers.Where(static l => l.IsOpen)]);

        _ = _controller.Report(Idle(Node(1), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(2), Generation) with { ObjectLedgersReady = false });
        _ = _controller.Report(Idle(Node(3), Generation) with { ObjectLedgersReady = true });
        _ = _controller.Report(Idle(Node(4), Generation) with { ObjectLedgersReady = true });

        // While closedObj overlaps quarters[0], Node(2) cannot execute for it so RollOff skips quarters[0].
        Assert.Empty(_controller.RunPass().Transitions);

        // Once closedObj drops, Node(2) can execute RollOff on quarters[0].
        await CommitAsync(new LedgerMapChange.Drop(closedObj.Id));
        var afterDrop = _controller.RunPass().Transitions;
        var roll = Assert.Single(afterDrop);
        Assert.Equal((TransitionPurpose.Roll, Node(2)), (roll.Purpose, roll.Executor));
    }

    /// <summary>
    /// orleans.md §5.14, §8 (row 4.8 B): <see cref="ClusterController.ValidateStartDrainAll"/> is the single authority over
    /// all three <see cref="DrainAllStartOutcome"/> cases: it returns <c>ObjectLedgersNotConfigured</c> when neither local
    /// configuration nor the registry enables object-store ledgers, <c>ObjectLedgersNotReady</c> when either enables them
    /// before the controller has adopted the layout, and <c>Started</c> once adopted.
    /// </summary>
    [Fact]
    public void ValidateStartDrainAllCoversConfiguredAndReadyOutcomes()
    {
        Assert.False(_controller.IsDrainingAll);
        _ = Assert.IsType<DrainAllStartOutcome.ObjectLedgersNotConfigured>(_controller.ValidateStartDrainAll(objectLedgersConfigured: false).Value);
        _ = Assert.IsType<DrainAllStartOutcome.ObjectLedgersNotReady>(_controller.ValidateStartDrainAll(objectLedgersConfigured: true).Value);

        // Even when the local silo lacks object-ledger config, CLNR's ObjectLedgerStore marks the cluster configured.
        _hasObjectLedgerStore = true;
        Observe();
        _ = Assert.IsType<DrainAllStartOutcome.ObjectLedgersNotReady>(_controller.ValidateStartDrainAll(objectLedgersConfigured: false).Value);

        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        _ = Assert.IsType<DrainAllStartOutcome.Started>(_controller.ValidateStartDrainAll(objectLedgersConfigured: false).Value);
        _ = Assert.IsType<DrainAllStartOutcome.Started>(_controller.ValidateStartDrainAll(objectLedgersConfigured: true).Value);
    }

    /// <summary>
    /// orleans.md §5.14, §8, LG-I11 (row 4.8 B): during Phase 1 (<c>DrainAllRevision &gt; 0</c>), the controller raises
    /// <c>LedgerMap.RetirementFloor</c> to <c>DrainAllRevision</c> (fencing pre-drain <c>Nodes</c> transitions), rolls open
    /// <c>Nodes</c> ledgers into <c>ObjectStore</c> under <see cref="TransitionPurpose.Departure"/> (so rolls proceed even
    /// when <c>MaxConcurrentTransitions == 0</c>), waits for <c>Floor &gt;= DrainAllRevision</c> and zero <c>Nodes</c>
    /// ledgers before emitting a single-flight <c>CompleteDrainAllRevision</c>, and reaches <c>IsReady</c> once atomic
    /// completion retires every diskful node, resets <c>DrainAllRevision</c> to <c>0</c>, and the floor covers it.
    /// </summary>
    [Fact]
    public async Task DrainAllPhase1RaisesFloorFenceRollsWithDepartureBudgetAndGatesPhase2()
    {
        Register(1, 2, 3);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);

        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(1), Node(2), Node(3)]);
        var closedRoot = _view.Current.Ledgers.Single(static l => !l.IsOpen);
        await CommitAsync(new LedgerMapChange.Drop(closedRoot.Id));
        Warm(lower, upper);

        // Zero discretionary balancing budget must not stall drain-all Phase 1 rolls.
        _options.MaxConcurrentTransitions = 0;
        var drainRev = StartDrainAllInRegistry();
        Assert.True(_controller.IsDrainingAll);

        var phase1 = RunPassCompletingPrepareReleases();
        Assert.Equal(drainRev, phase1.FloorRaise);
        Assert.Null(phase1.CompleteDrainAllRevision);
        Assert.Empty(phase1.Retirements);
        Assert.Equal(2, phase1.Transitions.Length);
        Assert.All(phase1.Transitions, static sent =>
        {
            Assert.Equal(TransitionPurpose.Departure, sent.Purpose);
            _ = Assert.IsType<LedgerLocation.ObjectStore>(sent.Transition.Choice.Location.Value);
        });

        // Commit both rolls (so open ledgers are ObjectStore) while Floor is still 0 and both closed Nodes ledgers exist.
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(lower.Id, Slot.Zero), new LedgerClosing(upper.Id, Slot.Zero)],
            [
                new LedgerOpening(lower.Range, LedgerLocation.ObjectStore.Instance, 2, new PrimaryClaim(Node(1), SequencerInstanceId.New())),
                new LedgerOpening(upper.Range, LedgerLocation.ObjectStore.Instance, 2, new PrimaryClaim(Node(2), SequencerInstanceId.New())),
            ],
            _revision));
        foreach (var sent in phase1.Transitions)
        {
            _ = _controller.TransitionCompleted(sent, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch));
        }

        // Drop both closed Nodes ledgers BEFORE committing FloorRaise: Phase 2 MUST still wait for Floor >= DrainAllRevision.
        await CommitAsync(new LedgerMapChange.Drop(lower.Id));
        Assert.True(_controller.PassDue);
        _ = _controller.RunPass();
        await CommitAsync(new LedgerMapChange.Drop(upper.Id));
        Assert.True(_controller.PassDue);
        var beforeFence = _controller.RunPass();
        Assert.Null(beforeFence.CompleteDrainAllRevision);
        Assert.Empty(beforeFence.Retirements);
        var waitingFenceStatus = _controller.DrainAll();
        Assert.True(waitingFenceStatus.Active);
        Assert.Empty(waitingFenceStatus.NodeLedgers);
        Assert.False(waitingFenceStatus.IsReady);

        // Commit the LG-I11 floor fence for DrainAllRevision.
        await CommitAsync(new LedgerMapChange.RaiseFloor(drainRev));
        _controller.FloorRaiseEnded();

        // Phase 2 now fires once (single-flight) with CompleteDrainAllRevision == drainRev.
        var phase2 = _controller.RunPass();
        Assert.Equal(drainRev, phase2.CompleteDrainAllRevision);
        Assert.Empty(phase2.Retirements);
        Assert.Empty(phase2.Transitions);
        // Repeated passes while completion is in flight do not re-emit CompleteDrainAllRevision.
        Assert.Null(_controller.RunPass().CompleteDrainAllRevision);

        // Atomic CompleteDrainAll retires every active diskful node and clears DrainAllRevision to 0.
        var retiredRev = CompleteDrainAllInRegistry();
        _controller.DrainAllCompletionEnded();
        Assert.False(_controller.IsDrainingAll);

        var beforeFinalFloor = _controller.DrainAll();
        Assert.False(beforeFinalFloor.Active);
        Assert.Empty(beforeFinalFloor.NodeLedgers);
        Assert.Empty(beforeFinalFloor.ActiveDiskfulNodes);
        Assert.Equal(retiredRev, beforeFinalFloor.LatestDiskfulRetiredRevision);
        Assert.False(beforeFinalFloor.FloorCovers);
        Assert.False(beforeFinalFloor.IsReady);

        var finalFloorPass = _controller.RunPass();
        Assert.Equal(retiredRev, finalFloorPass.FloorRaise);
        await CommitAsync(new LedgerMapChange.RaiseFloor(finalFloorPass.FloorRaise!.Value));
        _controller.FloorRaiseEnded();

        var ready = await _controller.DrainAllAsync(_ => Task.FromResult(CurrentRegisteredNodes()), Token);
        Assert.False(ready.Active);
        Assert.Empty(ready.NodeLedgers);
        Assert.Empty(ready.ActiveDiskfulNodes);
        Assert.True(ready.FloorCovers);
        Assert.True(ready.IsReady);
    }

    /// <summary>
    /// orleans.md §5.14, §8 (row 4.8 B): in a mixed cluster with diskful and diskless nodes, Phase 1 prefers live active
    /// diskless targets for <c>Nodes -&gt; ObjectStore</c> rolls, Phase 2 emits <c>CompleteDrainAllRevision</c> retiring
    /// only diskful nodes, and when fewer than <c>N</c> active diskful nodes exist (during mixed-cluster diskful replacement
    /// or a staggered empty-root restart) open <c>ObjectStore</c> ledgers still hand off and take over in
    /// <c>ObjectStore</c> rather than stalling.
    /// </summary>
    [Fact]
    public async Task DrainAllMixedClusterPrefersDisklessInPhase1AndServesObjectStoreWithFewerThanNDiskful()
    {
        _nodes.Clear();
        _nodes[Node(1)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: 0, Diskless: false);
        _nodes[Node(2)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: 1, Diskless: false);
        _nodes[Node(3)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: 2, Diskless: false);
        _nodes[Node(4)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: true);
        _membership[Node(1)] = NodeLiveness.Live.Instance;
        _membership[Node(2)] = NodeLiveness.Live.Instance;
        _membership[Node(3)] = NodeLiveness.Live.Instance;
        _membership[Node(4)] = NodeLiveness.Live.Instance;
        Observe();

        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));

        // Phase 1 prefers live active diskless Node(4) for the Nodes -> ObjectStore roll.
        var drainRev = StartDrainAllInRegistry();
        var phase1 = RunPassCompletingPrepareReleases();
        Assert.Equal(drainRev, phase1.FloorRaise);
        var roll = Assert.Single(phase1.Transitions);
        Assert.Equal(TransitionPurpose.Departure, roll.Purpose);
        Assert.Equal(Node(4), roll.Executor);
        _ = Assert.IsType<LedgerLocation.ObjectStore>(roll.Transition.Choice.Location.Value);

        await CommitAsync(new LedgerMapChange.RaiseFloor(drainRev));
        _controller.FloorRaiseEnded();
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(root.Id, Slot.Zero)],
            [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 2, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            _revision));
        _ = _controller.TransitionCompleted(roll, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch));
        await CommitAsync(new LedgerMapChange.Drop(root.Id));

        // Phase 2 retires only the 3 diskful nodes via CompleteDrainAll; diskless Node(4) remains active.
        var phase2 = _controller.RunPass();
        Assert.Equal(drainRev, phase2.CompleteDrainAllRevision);
        _ = CompleteDrainAllInRegistry();
        _controller.DrainAllCompletionEnded();
        Assert.True(_nodes[Node(1)].IsRetired);
        Assert.True(_nodes[Node(2)].IsRetired);
        Assert.True(_nodes[Node(3)].IsRetired);
        Assert.False(_nodes[Node(4)].IsRetired);

        // Even with 0 active diskful nodes (< N) under LedgerPlacement.Nodes, the open ObjectStore ledger held by
        // retired Node(1) hands off to live diskless Node(4) in ObjectStore.
        var handOffPass = RunPassCompletingPrepareReleases();
        Assert.Equal(_revision, handOffPass.FloorRaise);
        var handOff = Assert.Single(handOffPass.Transitions);
        Assert.Equal(Node(4), handOff.Executor);
        _ = Assert.IsType<LedgerLocation.ObjectStore>(handOff.Transition.Choice.Location.Value);

        await CommitAsync(new LedgerMapChange.RaiseFloor(handOffPass.FloorRaise!.Value));
        _controller.FloorRaiseEnded();
        var status = _controller.DrainAll();
        Assert.True(status.IsReady);
        Assert.Empty(status.ActiveDiskfulNodes);

        // Staggered post-restart availability: suppose diskless Node(4) also dies and 1 fresh diskful node (Node(5))
        // joins while the open ObjectStore ledger's primary (Node(1)) is dead. Even with only 1 < N active diskful
        // nodes, takeover succeeds in ObjectStore so the cluster serves immediately without waiting for all N silos.
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(_view.Current.Ledgers.Single(static l => l.IsOpen).Id, Slot.Zero)],
            [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 2, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            _revision));
        _ = _controller.TransitionCompleted(handOff, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch));
        Kill(1);
        Kill(4);
        _nodes[Node(5)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: 0, Diskless: false);
        _membership[Node(5)] = NodeLiveness.Live.Instance;
        Observe();
        _ = _controller.Report(Idle(Node(5), Generation) with { ObjectLedgersReady = true });

        var takeoverPass = _controller.RunPass();
        var takeover = Assert.Single(takeoverPass.Transitions);
        Assert.Equal(TransitionPurpose.Takeover, takeover.Purpose);
        Assert.Equal(Node(5), takeover.Executor);
        _ = Assert.IsType<LedgerLocation.ObjectStore>(takeover.Transition.Choice.Location.Value);
    }

    /// <summary>
    /// orleans.md §5.14: while <c>DrainAllRevision &gt; 0</c>, empty-map <c>Nodes</c> bootstrap and discretionary
    /// <c>ObjectStore</c> <c>K</c>-rolls are suppressed, and Phase 2 does not emit <c>CompleteDrainAllRevision</c> while
    /// the controller's registry view lags behind <c>LedgerMap.RetirementFloor</c>.
    /// </summary>
    [Fact]
    public async Task DrainAllSuppressesEmptyMapBootstrapObjectStoreKRollsAndBehindRegistryPhase2()
    {
        Register(1, 2, 3);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);

        // Empty map + DrainAllRevision > 0: no Nodes bootstrap is emitted.
        var drainRev = StartDrainAllInRegistry();
        var emptyPass = _controller.RunPass();
        Assert.Empty(emptyPass.Transitions);
        Assert.Equal(drainRev, emptyPass.FloorRaise);
        await CommitAsync(new LedgerMapChange.RaiseFloor(drainRev));
        _controller.FloorRaiseEnded();

        // Install an ObjectStore ledger with K = 4 != PerSlot.K (2): K-roll is suppressed while DrainAllRevision > 0.
        await CommitAsync(new LedgerMapChange.Install(
            [],
            [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 4, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            _revision));
        // Advance the floor above the controller's observed registry revision to verify !registryBehind gates Phase 2.
        await CommitAsync(new LedgerMapChange.RaiseFloor(_revision + 5));
        var behindPass = _controller.RunPass();
        Assert.Empty(behindPass.Transitions);
        Assert.Null(behindPass.CompleteDrainAllRevision);

        // Once the controller observes a registry revision >= floor, Phase 2 unblocks (and K-roll stays suppressed).
        _revision += 5;
        Observe();
        var caughtUp = _controller.RunPass();
        Assert.Empty(caughtUp.Transitions);
        Assert.Equal(drainRev, caughtUp.CompleteDrainAllRevision);
    }

    /// <summary>
    /// orleans.md §5.14, §8: <see cref="ClusterController.DrainAllAsync"/> reads <c>LedgerMap</c> before reading
    /// <c>CLNR</c>, so a fresh diskful node registered between the map read and the registry read is observed in
    /// <see cref="DrainAllStatus.ActiveDiskfulNodes"/> and prevents a false <see cref="DrainAllStatus.IsReady"/>.
    /// </summary>
    [Fact]
    public async Task DrainAllAsyncReadsMapBeforeRegistryForLinearizability()
    {
        Register(1, 2, 3);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);
        _hasObjectLedgerStore = true;
        _ = CompleteDrainAllInRegistry();
        await CommitAsync(new LedgerMapChange.RaiseFloor(_revision));

        var readsBefore = _mapRegister.Reads;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mapRegister.ReadGate = gate;
        var registryCalled = false;
        var readsAtRegistryCall = 0;

        var drainAllTask = _controller.DrainAllAsync(
            _ =>
            {
                registryCalled = true;
                readsAtRegistryCall = _mapRegister.Reads;
                // Simulate a fresh diskful node registering in CLNR right after the map read and before CLNR is read.
                _nodes[Node(4)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: 0, Diskless: false);
                _membership[Node(4)] = NodeLiveness.Live.Instance;
                _revision++;
                return Task.FromResult(CurrentRegisteredNodes());
            },
            Token);

        // While the map read is blocked at ReadGate, CLNR must not have been read yet.
        Assert.False(drainAllTask.IsCompleted);
        Assert.False(registryCalled);

        _mapRegister.ReadGate = null;
        gate.SetResult();

        var status = await drainAllTask;
        Assert.True(registryCalled);
        Assert.Equal(readsBefore + 1, readsAtRegistryCall);
        Assert.Equal(readsBefore + 1, _mapRegister.Reads);
        Assert.False(status.IsReady);
        Assert.Equal([Node(4)], status.ActiveDiskfulNodes);
    }

    /// <summary>
    /// orleans.md §5.14, §8: <see cref="DrainAllStatus.Of"/> boundary conditions: an unobserved registry
    /// (<c>Revision == 0</c>) is never ready; an active drain-all (<c>DrainAllRevision &gt; 0</c>) is never ready even
    /// with zero node ledgers and zero active diskful nodes; a retired diskless node at a higher revision does not push
    /// <c>LatestDiskfulRetiredRevision</c>; and an all-diskless cluster with zero node ledgers is ready once observed.
    /// </summary>
    [Fact]
    public async Task DrainAllStatusBoundariesHandleUnobservedActiveRetiredDisklessAndAllDiskless()
    {
        // 1. Unobserved registry (Revision == 0) is never ready.
        await using var freshCommitter = new LedgerMapCommitter(_mapRegister, 1 << 20, _view);
        var unobserved = new ClusterController(_options, _view, freshCommitter, ClusterArchive.Logs.Instance, _time, NullLogger.Instance);
        var unobservedStatus = unobserved.DrainAll();
        Assert.Equal(0ul, unobservedStatus.RegistryRevision);
        Assert.False(unobservedStatus.IsReady);

        // 2. All-diskless cluster at Revision > 0, DrainAllRevision == 0, zero node ledgers -> IsReady == true.
        var disklessOnly = new RegisteredNodes(
            Revision: 3,
            new Dictionary<NodeId, RegisteredNode>
            {
                [Node(1)] = new(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: true),
            }.ToFrozenDictionary(),
            HasObjectLedgerStore: true,
            DrainAllRevision: 0);
        var disklessStatus = DrainAllStatus.Of(disklessOnly, LedgerMapValue.Empty);
        Assert.Equal(0ul, disklessStatus.LatestDiskfulRetiredRevision);
        Assert.True(disklessStatus.FloorCovers);
        Assert.True(disklessStatus.IsReady);

        // 3. Active DrainAllRevision > 0 with zero node ledgers and zero active diskful nodes -> NOT ready until reset.
        var activeZero = disklessOnly with { DrainAllRevision = 3 };
        var activeZeroStatus = DrainAllStatus.Of(activeZero, LedgerMapValue.Empty);
        Assert.True(activeZeroStatus.Active);
        Assert.False(activeZeroStatus.IsReady);

        // 4. Retired diskless node at revision 10 does not affect LatestDiskfulRetiredRevision (revision 4).
        await CommitAsync(new LedgerMapChange.RaiseFloor(4));
        var retiredDisklessHigher = new RegisteredNodes(
            Revision: 10,
            new Dictionary<NodeId, RegisteredNode>
            {
                [Node(1)] = new(RetiredRevision: 4, null, Generation, Ordinal: 0, Diskless: false),
                [Node(2)] = new(RetiredRevision: 10, null, Generation, Ordinal: null, Diskless: true),
            }.ToFrozenDictionary(),
            HasObjectLedgerStore: true,
            DrainAllRevision: 0);
        var higherDisklessStatus = DrainAllStatus.Of(retiredDisklessHigher, _view.Current);
        Assert.Equal(4ul, higherDisklessStatus.LatestDiskfulRetiredRevision);
        Assert.True(higherDisklessStatus.FloorCovers);
        Assert.True(higherDisklessStatus.IsReady);
    }

    /// <summary>
    /// orleans.md §5.14, OR-I12, OR-I2 (row 4.8 B): because <c>DrainAllRevision</c> lives in <c>CLNR</c>, a controller
    /// failover mid-Phase-1 retains <c>IsDrainingAll == true</c> and never rolls ledgers back onto <c>Nodes</c>; cancelling
    /// drain-all in <c>CLNR</c> restores <see cref="ControlOptions.LedgerPlacement"/> without cancelling an explicit
    /// <see cref="ClusterController.RequestRetirement"/>.
    /// </summary>
    [Fact]
    public async Task DrainAllSurvivesFailoverDurablyAndCancelRestoresPlacementWithoutCancellingExplicitRetirements()
    {
        Register(1, 2, 3, 4);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);
        await CommitAsync(new LedgerMapChange.Install(
            [],
            [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 2, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            _revision));

        var drainRev = StartDrainAllInRegistry();
        // Fail over mid-Phase-1: fresh activation observes the same CLNR value and remains in drain-all automatically.
        await CreateControllerAsync(1 << 20);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);
        Assert.True(_controller.IsDrainingAll);
        var failoverPass = _controller.RunPass();
        Assert.Equal(drainRev, failoverPass.FloorRaise);
        Assert.Empty(failoverPass.Transitions);
        await CommitAsync(new LedgerMapChange.RaiseFloor(failoverPass.FloorRaise!.Value));
        _controller.FloorRaiseEnded();

        // Cancel drain-all alongside an explicit retirement of Node(4): only Node(4) is retired and placement returns to Nodes.
        _controller.RequestRetirement(Node(4));
        CancelDrainAllInRegistry();
        Assert.False(_controller.IsDrainingAll);

        var pass = _controller.RunPass();
        Assert.Equal([Node(4)], pass.Retirements);
        Assert.Null(pass.CompleteDrainAllRevision);
        var rollBack = Assert.Single(pass.Transitions);
        Assert.Equal(TransitionPurpose.Roll, rollBack.Purpose);
        var nodesLocation = Assert.IsType<LedgerLocation.Nodes>(rollBack.Transition.Choice.Location.Value);
        Assert.Equal(3, nodesLocation.Replicas.Length);

        // Once Node(4) settles retired and the roll-back supersedes, the reissued roll excludes retired Node(4).
        Retire(4);
        _controller.RetirementSettled(Node(4));
        _ = _controller.TransitionCompleted(rollBack, new LedgerMapCommitOutcome.Superseded(_view.Current.Epoch));
        var floorPass = _controller.RunPass();
        await CommitAsync(new LedgerMapChange.RaiseFloor(floorPass.FloorRaise!.Value));
        _controller.FloorRaiseEnded();

        var afterRetire = Assert.Single(floorPass.Transitions);
        Assert.Equal(TransitionPurpose.Roll, afterRetire.Purpose);
        var activeOnly = Assert.IsType<LedgerLocation.Nodes>(afterRetire.Transition.Choice.Location.Value);
        Assert.DoesNotContain(Node(4), activeOnly.Replicas);
        Assert.Equal(3, activeOnly.Replicas.Length);
    }

    /// <summary>
    /// orleans.md §5.14: when a retired diskful primary in a mixed cluster stops gracefully (hand-off or departing) while
    /// still hosting an open <c>ObjectStore</c> ledger, its hand-off is charged to <c>MaxConcurrentHandOffs</c>
    /// (<see cref="TransitionPurpose.Departure"/>) rather than <c>MaxConcurrentTransitions</c>, so setting
    /// <c>MaxConcurrentTransitions == 0</c> never stalls the stopping silo.
    /// </summary>
    [Fact]
    public async Task RetiredStoppingPrimaryHandsOffObjectStoreLedgerWithZeroTransitionBudget()
    {
        _options.MaxConcurrentTransitions = 0;
        _options.MaxConcurrentHandOffs = 1;
        await CreateControllerAsync(1 << 20);
        Register(1, 2, 3);
        RegisterDiskless(4);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);

        await CommitAsync(new LedgerMapChange.Install(
            [],
            [new LedgerOpening(Root, LedgerLocation.ObjectStore.Instance, 2, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            _revision));

        // Retire all diskful nodes (e.g. after drain-all Phase 2), leaving diskless Node(4) active.
        Retire(1);
        Retire(2);
        Retire(3);
        // Node(1) requests graceful hand-off as it stops.
        _controller.RequestHandOff(Node(1), Generation);

        var pass = RunPassCompletingPrepareReleases();
        var departure = Assert.Single(pass.Transitions);
        Assert.Equal(TransitionPurpose.Departure, departure.Purpose);
        Assert.Equal(Node(4), departure.Executor);
        _ = Assert.IsType<LedgerLocation.ObjectStore>(departure.Transition.Choice.Location.Value);
    }

    /// <summary>
    /// orleans.md §5.14: during drain-all in a mixed cluster with live diskless silos, a WAL-release roll of an open
    /// node-backed ledger picks a diskless <c>ObjectStore</c> executor rather than pinning the new <c>ObjectStore</c>
    /// ledger onto a retiring diskful primary.
    /// </summary>
    [Fact]
    public async Task DrainAllWalReleaseRollPrefersDisklessExecutorInMixedCluster()
    {
        Register(1, 2, 3);
        RegisterDiskless(4);
        _controller.AdoptObjectLedgers(ObjectLedgerLayout.PerSlot);
        ReportEveryNode(objectLedgersReady: true);

        await CommitAsync(new LedgerMapChange.Install(
            [],
            [new LedgerOpening(Root, new LedgerLocation.Nodes([Node(1), Node(2), Node(3)]), 2, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            _revision));
        var openId = _view.Current.Ledgers[0].Id;

        _ = StartDrainAllInRegistry();
        _controller.RequestRoll(openId);

        var pass = RunPassCompletingPrepareReleases();
        var roll = Assert.Single(pass.Transitions);
        Assert.Equal(TransitionPurpose.WalReleaseRoll, roll.Purpose);
        Assert.Equal(Node(4), roll.Executor);
        _ = Assert.IsType<LedgerLocation.ObjectStore>(roll.Transition.Choice.Location.Value);
    }

    /// <summary>
    /// orleans.md §5.14, outcomes.md: <see cref="ClusterController.ValidateStartDrainAll"/> is idempotent when
    /// <c>CLNR.DrainAllRevision &gt; 0</c> is already durable, returning <see cref="DrainAllStartOutcome.Started"/> even
    /// on an activation that has not yet adopted the local object-ledger layout.
    /// </summary>
    [Fact]
    public void ValidateStartDrainAllIsIdempotentWhenDrainAllIsAlreadyDurableOnUnreadyActivation()
    {
        Register(1, 2, 3);
        _ = StartDrainAllInRegistry();
        Assert.False(_controller.HasObjectLedgers);
        Assert.True(_controller.IsDrainingAll);

        _ = Assert.IsType<DrainAllStartOutcome.Started>(_controller.ValidateStartDrainAll(objectLedgersConfigured: false).Value);
        _ = Assert.IsType<DrainAllStartOutcome.Started>(_controller.ValidateStartDrainAll(objectLedgersConfigured: true).Value);
    }

    /// <summary>
    /// OR-I24: a planned hand-off off a live primary emits <see cref="ControllerPass.PrepareReleases"/> first without
    /// emitting <see cref="Transition.Replace"/>; once the primary replies <see cref="PrepareReleaseReply.Released"/>,
    /// the next pass emits <see cref="Transition.Replace"/> with <see cref="PredecessorReleaseHint.Cooperative"/>.
    /// </summary>
    [Fact]
    public async Task PlannedHandOffRunsCooperativePrepareReleaseBeforeCooperativeReplace()
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        _controller.RequestHandOff(Node(1), Generation);

        var first = _controller.RunPass();
        Assert.Empty(first.Transitions);
        var prepare = Assert.Single(first.PrepareReleases);
        Assert.Equal((Node(1), root.Id, root.Range, _options.PrepareReleaseTimeout, TransitionPurpose.Departure), (prepare.Node, prepare.Request.Ledger, prepare.Request.Range, prepare.Request.Deadline, prepare.Purpose));

        // While PrepareRelease is in flight, repeated passes do not re-emit PrepareRelease or Replace.
        var whileInFlight = _controller.RunPass();
        Assert.Empty(whileInFlight.Transitions);
        Assert.Empty(whileInFlight.PrepareReleases);

        Assert.True(_controller.PrepareReleaseCompleted(prepare, new PrepareReleaseReply.Released(new Slot(7))));
        Assert.True(_controller.PassDue);

        var second = _controller.RunPass();
        Assert.Empty(second.PrepareReleases);
        var sent = Assert.Single(second.Transitions);
        Assert.Equal((TransitionPurpose.Departure, Node(2)), (sent.Purpose, sent.Executor));
        var replace = Assert.IsType<Transition.Replace>(sent.Transition.Value);
        Assert.Equal(root.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.Cooperative, replace.PredecessorHint);
    }

    /// <summary>
    /// OR-I24: if <c>PrepareRelease</c> fails (RPC failure, timeout, <c>Sealed</c>, <c>NotServed</c>, or
    /// <c>Declined</c>), the controller does not claim cooperative hand-off and falls back immediately to
    /// <see cref="PredecessorReleaseHint.TakenOver"/>.
    /// </summary>
    [Theory]
    [InlineData("rpc-failure")]
    [InlineData("timeout")]
    [InlineData("sealed")]
    [InlineData("not-served")]
    [InlineData("declined")]
    [InlineData("clock-timeout")]
    public async Task PrepareReleaseFailureOrTimeoutFallsBackToTakenOver(string mode)
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        _controller.RequestHandOff(Node(1), Generation);

        var prepare = Assert.Single(_controller.RunPass().PrepareReleases);
        if (mode == "clock-timeout")
        {
            _time.Advance(_options.PrepareReleaseTimeout);
        }
        else
        {
            PrepareReleaseReply? reply = mode switch
            {
                "rpc-failure" => null,
                "timeout" => PrepareReleaseReply.TimedOut.Instance,
                "sealed" => PrepareReleaseReply.Sealed.Instance,
                "not-served" => PrepareReleaseReply.NotServed.Instance,
                "declined" => PrepareReleaseReply.Declined.Instance,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            Assert.True(_controller.PrepareReleaseCompleted(prepare, reply));
            Assert.True(_controller.PassDue);
        }

        var fallback = _controller.RunPass();
        Assert.Empty(fallback.PrepareReleases);
        var sent = Assert.Single(fallback.Transitions);
        Assert.Equal((TransitionPurpose.Departure, Node(2)), (sent.Purpose, sent.Executor));
        var replace = Assert.IsType<Transition.Replace>(sent.Transition.Value);
        Assert.Equal(root.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.TakenOver, replace.PredecessorHint);
    }

    /// <summary>
    /// OR-I24: an unplanned takeover (primary dead in membership or reported unserved) never emits
    /// <c>PrepareRelease</c> and sends <see cref="Transition.Replace"/> with
    /// <see cref="PredecessorReleaseHint.TakenOver"/> immediately; if a primary dies while a planned
    /// <c>PrepareRelease</c> is in flight, the pending prepare is cancelled immediately for a fast takeover.
    /// </summary>
    [Fact]
    public async Task UnplannedTakeoverBypassesPrepareReleaseAndPrimaryDeathCancelsInFlightPrepare()
    {
        _options.MaxConcurrentTransitions = 0;
        var (lower, upper) = await SplitRootAsync([Node(1), Node(2), Node(3)], [Node(4), Node(2), Node(3)]);

        // Dead primary on lower takes over directly with no PrepareRelease.
        Kill(1);
        var immediate = _controller.RunPass();
        Assert.Empty(immediate.PrepareReleases);
        var lowerTakeover = Assert.Single(immediate.Transitions);
        Assert.Equal(TransitionPurpose.Takeover, lowerTakeover.Purpose);
        Assert.Equal(PredecessorReleaseHint.TakenOver, Assert.IsType<Transition.Replace>(lowerTakeover.Transition.Value).PredecessorHint);

        // Planned hand-off on upper starts PrepareRelease; primary dies before replying -> immediate Takeover fallback.
        _controller.RequestHandOff(Node(4), Generation);
        var prepare = Assert.Single(_controller.RunPass().PrepareReleases);
        Assert.Equal((Node(4), upper.Id), (prepare.Node, prepare.Request.Ledger));

        Kill(4);
        var afterDeath = _controller.RunPass();
        Assert.Empty(afterDeath.PrepareReleases);
        var upperTakeover = Assert.Single(afterDeath.Transitions);
        Assert.Equal(TransitionPurpose.Takeover, upperTakeover.Purpose);
        var upperReplace = Assert.IsType<Transition.Replace>(upperTakeover.Transition.Value);
        Assert.Equal(upper.Id, upperReplace.Ledger);
        Assert.Equal(PredecessorReleaseHint.TakenOver, upperReplace.PredecessorHint);
    }

    /// <summary>
    /// OR-I24: across controller failover after the outgoing primary already drained and stopped admitting writes on an
    /// open ledger, the primary's <see cref="NodeReport.ReleasedLedgers"/> lets the new controller activation replace
    /// the ledger directly with <see cref="PredecessorReleaseHint.Cooperative"/> without stranding it or issuing another
    /// <c>PrepareRelease</c>.
    /// </summary>
    [Fact]
    public async Task ControllerFailoverUsesReportedReleasedLedgersToFinishCooperativeReplace()
    {
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));

        // Fresh controller activation with no hand-off or balance trigger still replaces a ledger the primary reports released.
        await CreateControllerAsync(1 << 20);
        _ = _controller.Report(Idle(Node(1), Generation) with
        {
            ReleasedLedgers = ImmutableDictionary<LedgerId, Slot>.Empty.Add(root.Id, new Slot(5)),
        });
        Assert.True(_controller.PassDue);

        var pass = _controller.RunPass();
        Assert.Empty(pass.PrepareReleases);
        var sent = Assert.Single(pass.Transitions);
        Assert.Equal((TransitionPurpose.Handoff, Node(2)), (sent.Purpose, sent.Executor));
        var replace = Assert.IsType<Transition.Replace>(sent.Transition.Value);
        Assert.Equal(root.Id, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.Cooperative, replace.PredecessorHint);
    }

    /// <summary>
    /// OR-I24: once a primary has cooperatively released a ledger for primary balance, if the chosen successor declines
    /// the replacement, the controller still replaces the already-released ledger on another healthy candidate rather
    /// than stranding it with a stopped writer.
    /// </summary>
    [Fact]
    public async Task ReleasedLedgerIsNotStrandedWhenSuccessorDeclines()
    {
        Register(1, 2, 3);
        var quarters = await QuartersAsync([Node(1), Node(2), Node(4)], [Node(1), Node(2), Node(4)], [Node(1), Node(3), Node(2)], [Node(2), Node(1), Node(4)]);
        ReportEveryNode();
        Warm(quarters);

        var handOff = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        var releasedLedger = Assert.IsType<Transition.Replace>(handOff.Transition.Value).Ledger;
        Assert.Equal(Node(3), handOff.Executor);

        _controller.TransitionCompleted(handOff, TransitionReply.Declined.Instance);

        var recovery = _controller.RunPass();
        Assert.Empty(recovery.PrepareReleases);
        var replacement = Assert.Single(recovery.Transitions, sent => Assert.IsType<Transition.Replace>(sent.Transition.Value).Ledger == releasedLedger);
        Assert.Equal(Node(2), replacement.Executor);
        Assert.Equal(PredecessorReleaseHint.Cooperative, Assert.IsType<Transition.Replace>(replacement.Transition.Value).PredecessorHint);
    }

    /// <summary>
    /// OR-I24: when a target fails or declines a cooperative balance replacement, rescuing the already-released ledger
    /// onto another node must not trigger an immediate <c>PrepareRelease</c> churn loop between the healthy nodes;
    /// discretionary balance backs off from the failing target (<c>ControlInterval</c>, doubling up to
    /// <c>TransitionTimeout</c>) and converges once the target accepts a replacement.
    /// </summary>
    [Fact]
    public async Task CooperativeBalanceFailureBacksOffInsteadOfChurningBetweenOtherNodes()
    {
        Register(1, 2, 3);
        var root = await InstallRootAsync(Node(1), Node(2), Node(3));
        var (lower, upper) = Root.Children;
        var (q0, q1) = lower.Children;
        await CommitAsync(new LedgerMapChange.Install(
            [new LedgerClosing(root.Id, Slot.Zero)],
            [Opening(q0, Node(1), Node(2), Node(3)), Opening(q1, Node(1), Node(2), Node(3)), Opening(upper, Node(1), Node(2), Node(3))],
            _revision));
        ReportEveryNode();
        Warm([.. _view.Current.Ledgers.Where(static ledger => ledger.IsOpen)]);

        // Node(1) holds 3 primaries; balance cooperatively releases two of them for Node(2) and Node(3).
        var initial = RunPassCompletingPrepareReleases();
        Assert.Equal(2, initial.Transitions.Length);
        var toNode2 = Assert.Single(initial.Transitions, static sent => sent.Executor == Node(2));
        var toNode3 = Assert.Single(initial.Transitions, static sent => sent.Executor == Node(3));

        // Node(2) succeeds while Node(3) fails transiently (e.g., placement refusal while starting up).
        var ledgerFor2 = _view.Current.Get(Assert.IsType<Transition.Replace>(toNode2.Transition.Value).Ledger)!.Value;
        await ReplaceAsync(ledgerFor2, Node(2), Node(1), Node(3));
        Assert.True(_controller.TransitionCompleted(toNode2, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));
        Assert.False(_controller.TransitionCompleted(toNode3, reply: null));

        // The next pass rescues the already-released ledger onto Node(2) without releasing Node(1)'s remaining live ledger.
        var rescuePass = _controller.RunPass();
        Assert.Empty(rescuePass.PrepareReleases);
        var rescue = Assert.Single(rescuePass.Transitions);
        Assert.Equal(Node(2), rescue.Executor);
        var rescuedId = Assert.IsType<Transition.Replace>(rescue.Transition.Value).Ledger;
        Assert.Equal(Assert.IsType<Transition.Replace>(toNode3.Transition.Value).Ledger, rescuedId);
        await ReplaceAsync(_view.Current.Get(rescuedId)!.Value, Node(2), Node(1), Node(3));
        Assert.True(_controller.TransitionCompleted(rescue, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));

        // Immediate follow-up passes during backoff must not ping-pong ledgers between Node(1) and Node(2).
        var quiet = _controller.RunPass();
        Assert.Empty(quiet.PrepareReleases);
        Assert.Empty(quiet.Transitions);

        // After ControlInterval, Node(3) is retried once; a second failure doubles the backoff to 2 * ControlInterval.
        _time.Advance(_options.ControlInterval);
        var retry = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal(Node(3), retry.Executor);
        Assert.False(_controller.TransitionCompleted(retry, TransitionReply.Declined.Instance));

        var rescueSecond = Assert.Single(_controller.RunPass().Transitions);
        var rescueReplicas = new[] { rescueSecond.Executor }.Concat(new[] { Node(1), Node(2), Node(3) }.Where(node => node != rescueSecond.Executor)).ToArray();
        await ReplaceAsync(_view.Current.Get(Assert.IsType<Transition.Replace>(rescueSecond.Transition.Value).Ledger)!.Value, rescueReplicas);
        Assert.True(_controller.TransitionCompleted(rescueSecond, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));

        _time.Advance(_options.ControlInterval);
        var stillBackedOff = _controller.RunPass();
        Assert.Empty(stillBackedOff.PrepareReleases);
        Assert.Empty(stillBackedOff.Transitions);

        // Once the doubled backoff expires and Node(3) accepts, balance converges cleanly to 1 primary per node.
        _time.Advance(_options.ControlInterval);
        var converged = Assert.Single(RunPassCompletingPrepareReleases().Transitions);
        Assert.Equal(Node(3), converged.Executor);
        var movedId = Assert.IsType<Transition.Replace>(converged.Transition.Value).Ledger;
        await ReplaceAsync(_view.Current.Get(movedId)!.Value, Node(3), Node(1), Node(2));
        Assert.True(_controller.TransitionCompleted(converged, new LedgerMapCommitOutcome.Applied(_view.Current.Epoch)));

        var openPrimaries = _view.Current.Ledgers
            .Where(static ledger => ledger.IsOpen)
            .Select(static ledger => ledger.Primary.Node)
            .Order(NodeOrder.Instance)
            .ToArray();
        Assert.Equal([Node(1), Node(2), Node(3)], openPrimaries);
    }

    /// <summary>
    /// OR-I24: when a discretionary domain roll on a ledger is blocked by disk pressure on the unused domain, releasing
    /// that ledger for primary balance proceeds as a cooperative <see cref="TransitionPurpose.Handoff"/> rather than
    /// being hijacked into a no-op <see cref="TransitionPurpose.Roll"/>.
    /// </summary>
    [Fact]
    public async Task ReleasedPrimaryBalanceLedgerIsNotHijackedByDiscretionaryDomainRoll()
    {
        Register((1, "b"), (2, "a"), (3, "a"), (4, "a"));
        ReportEveryNode();
        var quarters = await QuartersAsync([Node(3), Node(4), Node(2)], [Node(3), Node(2), Node(4)], [Node(3), Node(2), Node(4)], [Node(2), Node(3), Node(4)]);
        Warm(quarters);
        _ = _controller.Report(Idle(Node(1), Generation) with { DiskPressure = true });

        var first = _controller.RunPass();
        var prepare = Assert.Single(first.PrepareReleases);
        Assert.Equal(TransitionPurpose.Handoff, prepare.Purpose);
        Assert.Equal(Node(3), prepare.Node);

        Assert.True(_controller.PrepareReleaseCompleted(prepare, new PrepareReleaseReply.Released(Slot.Zero)));
        var second = _controller.RunPass();
        var handOff = Assert.Single(second.Transitions);
        Assert.Equal(TransitionPurpose.Handoff, handOff.Purpose);
        Assert.Equal(Node(4), handOff.Executor);
        var replace = Assert.IsType<Transition.Replace>(handOff.Transition.Value);
        Assert.Equal(prepare.Request.Ledger, replace.Ledger);
        Assert.Equal(PredecessorReleaseHint.Cooperative, replace.PredecessorHint);
    }

    /// <summary>
    /// Runs one pass and, when it emits cooperative <see cref="ControllerPass.PrepareReleases"/>, completes each as
    /// <see cref="PrepareReleaseReply.Released"/> and runs the follow-up pass, returning the combined pass.
    /// </summary>
    private ControllerPass RunPassCompletingPrepareReleases()
    {
        var pass = _controller.RunPass();
        if (pass.PrepareReleases.IsEmpty)
        {
            return pass;
        }

        foreach (var prepare in pass.PrepareReleases)
        {
            _ = _controller.PrepareReleaseCompleted(prepare, new PrepareReleaseReply.Released(Slot.Zero));
        }

        var followUp = _controller.RunPass();
        return followUp with
        {
            Transitions = pass.Transitions.AddRange(followUp.Transitions),
            Compactions = pass.Compactions.AddRange(followUp.Compactions),
            Retirements = pass.Retirements.AddRange(followUp.Retirements),
            FloorRaise = followUp.FloorRaise ?? pass.FloorRaise,
            CompleteDrainAllRevision = followUp.CompleteDrainAllRevision ?? pass.CompleteDrainAllRevision,
            RegistryBehind = pass.RegistryBehind || followUp.RegistryBehind,
        };
    }

    /// <summary>
    /// Runs one pass and completes any emitted <see cref="ControllerPass.PrepareReleases"/> with
    /// <see cref="PrepareReleaseReply.TimedOut"/> so the follow-up pass tests non-cooperative replacement behavior while
    /// the primary remains serving.
    /// </summary>
    private ControllerPass RunPassWithPrepareFallback()
    {
        var pass = _controller.RunPass();
        if (pass.PrepareReleases.IsEmpty)
        {
            return pass;
        }

        foreach (var prepare in pass.PrepareReleases)
        {
            _ = _controller.PrepareReleaseCompleted(prepare, PrepareReleaseReply.TimedOut.Instance);
        }

        var followUp = _controller.RunPass();
        return followUp with
        {
            Transitions = pass.Transitions.AddRange(followUp.Transitions),
            Compactions = pass.Compactions.AddRange(followUp.Compactions),
            Retirements = pass.Retirements.AddRange(followUp.Retirements),
            FloorRaise = followUp.FloorRaise ?? pass.FloorRaise,
            CompleteDrainAllRevision = followUp.CompleteDrainAllRevision ?? pass.CompleteDrainAllRevision,
            RegistryBehind = pass.RegistryBehind || followUp.RegistryBehind,
        };
    }

    /// <summary>Returns an idle report of <paramref name="node"/>, numbered after every report the test made before it.</summary>
    private NodeReport Idle(NodeId node, int generation) => NodeReport.Idle(node, generation, ++_reportSequence);

    private NodeReport Undrained(int node, long keys, double? rate = null) =>
        Idle(Node(node), Generation) with { UndrainedKeys = keys, ArchiveLogRequestsPerSecond = rate };

    /// <summary>Returns the accounting of a new activation over <paramref name="count"/> live, active nodes.</summary>
    private RequestBudgetShares Shares(double budget, int count, out NodeView view, out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(_time.GetUtcNow());
        var nodes = Enumerable.Range(1, count).ToFrozenDictionary(Node, static _ => new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null));
        view = new NodeView(new RegisteredNodes(1, nodes), nodes.Keys.ToFrozenDictionary(static node => node, static _ => (NodeLiveness)NodeLiveness.Live.Instance));
        return new RequestBudgetShares(budget, _options.ControlInterval, time.GetUtcNow());
    }

    private static LedgerOpening Opening(KeyRange range, params NodeId[] replicas) =>
        new(range, [.. replicas], 4, new PrimaryClaim(replicas[0], SequencerInstanceId.New()));

    /// <summary>Returns the least value limit whose map holds <paramref name="ranges"/> open ranges, each with a closed predecessor.</summary>
    private static int LimitFor(int ranges)
    {
        var limit = LedgerMapValue.Empty.EncodedLength;
        while (LedgerMapValue.RangeCapacity(limit, 3) < ranges)
        {
            limit++;
        }

        return limit;
    }

    private async Task CreateControllerAsync(int mapLimit, ManifestView? manifest = null, bool archiveLogs = false)
    {
        if (_mapCommitter is not null)
        {
            await _mapCommitter.DisposeAsync();
        }

        _mapCommitter = new LedgerMapCommitter(_mapRegister, mapLimit, _view);
        _controller = new ClusterController(_options, _view, _mapCommitter, archiveLogs ? ClusterArchive.Logs.Instance : new ClusterArchive.Lsm(manifest ?? _manifest, _manifestCommitter, Archive), _time, NullLogger.Instance);
        if (_revision > 0)
        {
            ObserveCurrent();
        }
    }

    /// <summary>Reports a load between cold and hot for each of <paramref name="ledgers"/> from its primary.</summary>
    private void Warm(params LedgerMetadata[] ledgers)
    {
        foreach (var ledger in ledgers)
        {
            _ = _controller.Report(Idle(ledger.Primary.Node, Generation) with { ProposedBytesPerSecond = ImmutableDictionary<LedgerId, long>.Empty.Add(ledger.Id, _options.SplitBytesPerSecond / 2) });
        }
    }

    /// <summary>Installs the root and replaces it with its four grandchildren's ledgers, with the given replicas, primary first.</summary>
    private async Task<LedgerMetadata[]> QuartersAsync(params NodeId[][] replicas)
    {
        var root = await InstallRootAsync(replicas[0]);
        var (lower, upper) = Root.Children;
        KeyRange[] ranges = [lower.Children.Lower, lower.Children.Upper, upper.Children.Lower, upper.Children.Upper];
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [.. ranges.Select((range, index) => Opening(range, replicas[index]))], _revision));
        return [.. ranges.Select(range => _view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == range))];
    }

    private void Register(params int[] nodes) => Register([.. nodes.Select(static node => (node, (string?)null))]);

    private void Register(params (int Node, string? Domain)[] nodes)
    {
        _nodes.Clear();
        foreach (var (node, domain) in nodes)
        {
            _nodes[Node(node)] = new RegisteredNode(RetiredRevision: null, domain, Generation, Ordinal: null);
            _ = _membership.TryAdd(Node(node), NodeLiveness.Live.Instance);
        }

        Observe();
    }

    private void RegisterDiskless(params int[] nodes)
    {
        foreach (var node in nodes)
        {
            _nodes[Node(node)] = new RegisteredNode(RetiredRevision: null, null, Generation, Ordinal: null, Diskless: true);
            _membership[Node(node)] = NodeLiveness.Live.Instance;
        }

        Observe();
    }

    /// <summary>Reports every registered node idle, as a running node does each control interval, so balancing may target it (orleans.md §5.6).</summary>
    private void ReportEveryNode(bool objectLedgersReady = false)
    {
        foreach (var (node, registered) in _nodes)
        {
            _ = _controller.Report(Idle(node, registered.Generation) with { ObjectLedgersReady = objectLedgersReady });
        }
    }

    private RegisteredNodes CurrentRegisteredNodes() =>
        new(_revision, _nodes.ToFrozenDictionary(), _hasObjectLedgerStore, _drainAllRevision);

    private void ObserveCurrent() => _controller.Observe(CurrentRegisteredNodes(), _membership.ToFrozenDictionary());

    private void Observe()
    {
        ++_revision;
        ObserveCurrent();
    }

    private ulong StartDrainAllInRegistry()
    {
        _hasObjectLedgerStore = true;
        _drainAllRevision = _revision + 1;
        Observe();
        return _drainAllRevision;
    }

    private void CancelDrainAllInRegistry()
    {
        _drainAllRevision = 0;
        Observe();
    }

    private ulong CompleteDrainAllInRegistry()
    {
        var retiredRev = _revision + 1;
        foreach (var (node, info) in _nodes.ToArray())
        {
            if (!info.IsRetired && !info.Diskless)
            {
                _nodes[node] = info with { RetiredRevision = retiredRev };
            }
        }

        _drainAllRevision = 0;
        Observe();
        return retiredRev;
    }

    private void Kill(int node, DateTimeOffset? since = null)
    {
        _membership[Node(node)] = new NodeLiveness.Dead(since ?? _time.GetUtcNow());
        Observe();
    }

    private void Revive(int node)
    {
        _membership[Node(node)] = NodeLiveness.Live.Instance;
        Observe();
    }

    /// <summary>Shows <paramref name="node"/> retired at the registry view's next revision.</summary>
    private void Retire(int node)
    {
        _nodes[Node(node)] = _nodes[Node(node)] with { RetiredRevision = _revision + 1 };
        Observe();
    }

    private async Task CommitAsync(LedgerMapChange change) =>
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await _controller.CommitLedgerMapAsync(change, Token)).Value);

    /// <summary>Installs a ledger over the whole keyspace through the controller's committer, as a bootstrap's executor does.</summary>
    private async Task<LedgerMetadata> InstallRootAsync(params NodeId[] replicas)
    {
        await CommitAsync(new LedgerMapChange.Install([], [Opening(Root, replicas)], _revision));
        return Assert.Single(_view.Current.Ledgers);
    }

    /// <summary>Installs the root and splits it into its children's ledgers, with the given replicas, primary first.</summary>
    private async Task<(LedgerMetadata Lower, LedgerMetadata Upper)> SplitRootAsync(NodeId[] lower, NodeId[] upper)
    {
        var root = await InstallRootAsync(lower);
        var (lowerRange, upperRange) = Root.Children;
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lowerRange, lower), Opening(upperRange, upper)], _revision));
        var open = _view.Current.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
        return (open.Single(ledger => ledger.Range == lowerRange), open.Single(ledger => ledger.Range == upperRange));
    }

    /// <summary>Closes <paramref name="ledger"/> and opens a successor over its range with <paramref name="replicas"/>, primary first, as a takeover does.</summary>
    private async Task ReplaceAsync(LedgerMetadata ledger, params NodeId[] replicas) =>
        await CommitAsync(new LedgerMapChange.Install([new LedgerClosing(ledger.Id, Slot.Zero)], [Opening(ledger.Range, replicas)], _revision));

    /// <summary>
    /// Commits <paramref name="count"/> drains of ledger 1, each a section of <paramref name="partition"/>, so the partition
    /// holds more than its <c>k</c> of two sections, and returns the manifest's epoch.
    /// </summary>
    private async Task<ulong> OverfillAsync(KeyRange partition, int count, int from = 0)
    {
        ManifestCommitOutcome outcome = default;
        for (var slot = from; slot < from + count; slot++)
        {
            var tag = new Tag(new LedgerId(1), new Slot(slot), 0);
            var section = new SectionRef(new ObjectName($"d/1/{slot}/s"), 0, 10, partition, tag, tag, new ObjectRange(10, 1));
            outcome = await _controller.CommitManifestAsync(new ManifestChange.Drain(new LedgerId(1), new Slot(slot), new Slot(slot + 1), [section], complete: false), Token);
        }

        return Assert.IsType<ManifestCommitOutcome.Applied>(outcome.Value).Epoch;
    }

    /// <summary>
    /// The model of <see cref="TheNodesRatesStayWithinTheBudgetAndASilentNodeFallsToItsFloor"/>: five nodes, each with the
    /// share it holds; the controller's accounting and its view; the registry and membership the view projects; and the
    /// budget's history.
    /// </summary>
    private sealed class ShareModel
    {
        public const int Count = 5;

        private const double Tolerance = 1e-9;
        private static readonly double[] Budgets = [250, 1_000, 4_000];

        private readonly TimeSpan _interval;
        private readonly FakeTimeProvider _time;
        private readonly bool[] _registered = [true, true, true, true, false];
        private readonly bool[] _retired = new bool[Count];
        private readonly bool[] _dead = new bool[Count];
        private readonly bool[] _silent = new bool[Count];
        private readonly DateTimeOffset?[] _assignedAt = new DateTimeOffset?[Count];
        private readonly ArchiveLogNodeShare[] _nodes;
        private readonly List<Activation> _activations = [];
        private readonly Activation?[] _heardFrom = new Activation?[Count];
        private double _budget = 1_000;
        private ulong _revision;
        private NodeView _view;
        private RequestBudgetShares _shares;

        public ShareModel(TimeSpan interval, DateTimeOffset start)
        {
            _interval = interval;
            _time = new FakeTimeProvider(start);
            _nodes = [.. Enumerable.Range(0, Count).Select(_ => new ArchiveLogNodeShare(Floor, interval))];
            _view = View();
            _shares = Activate();
        }

        private DateTimeOffset Now => _time.GetUtcNow();

        private TimeSpan Deadline => _interval * ArchiveLogRequestShare.ReplyIntervals;

        public void Step(int kind, int index, long keys)
        {
            switch (kind)
            {
                case 0:
                case 1:
                    _time.Advance(_interval);
                    _shares.Prune(_view, Now);
                    break;
                case 2:
                    // A new activation, which has not observed the registry yet.
                    _shares = Activate();
                    _view = NodeView.Empty;
                    break;
                case 3:
                    _view = View();
                    break;
                case 4:
                    // A rolling restart with another budget, which moves the controller too.
                    _budget = Budgets[index % Budgets.Length];
                    _shares = Activate();
                    _view = View();
                    break;
                case 5:
                    _retired[index] = _registered[index];
                    Relist();
                    break;
                case 6:
                    _dead[index] = !_dead[index];
                    Relist();
                    break;
                case 7:
                    _registered[index] = true;
                    Relist();
                    break;
                case 8:
                    // A node whose report call hangs sends no report, and receives no reply, until it ends.
                    _silent[index] = !_silent[index];
                    break;
                case 9:
                    Report(index, keys, delivered: false);
                    break;
                default:
                    Report(index, keys, delivered: true);
                    break;
            }
        }

        /// <summary>Observes the view, and runs every node which is not silent for long enough that each activation is ready and each share renewed.</summary>
        public void Settle()
        {
            _view = View();
            for (var round = 0; round < 2 * ArchiveLogRequestShare.ReplyIntervals + 3; round++)
            {
                _time.Advance(_interval);
                _shares.Prune(_view, Now);
                for (var node = 0; node < Count; node++)
                {
                    Report(node, 100, delivered: true);
                }

                CheckStep();
            }
        }

        public void CheckStep()
        {
            var rates = Rates();
            CheckFloors(rates);
            var floor = Floor();
            Assert.True(Enumerable.Range(0, Count).All(node => rates[node] <= floor * (1 + Tolerance) || _heardFrom[node] is not null), "A node runs above its floor only at a share a ready activation gave it.");
            // The nodes above their floor which last heard from one ready activation, by a reply with a share or without one,
            // run at most its budget together: a node a newer activation waited for runs at its budget once it heard from it,
            // and only nodes which have not, or which it did not wait for, still run at an earlier one's share. A node's floor
            // is the higher of its own and that activation's: a reply which finds no room leaves the node at or lowers it to
            // the activation's floor, a rate above what now fits, until the nodes holding the room report again, and a
            // restart which lowers the budget puts that floor above the node's new one (OR-I21).
            foreach (var group in Enumerable.Range(0, Count).Where(node => _heardFrom[node] is { } heard && rates[node] > Math.Max(floor, heard.Floor) * (1 + Tolerance)).GroupBy(node => _heardFrom[node]!))
            {
                var sum = group.Sum(node => rates[node]);
                Assert.True(sum <= group.Key.Budget * (1 + Tolerance), $"OR-I21: the nodes {string.Join(", ", group)} which last heard from an activation of budget {group.Key.Budget} run at {sum}: {string.Join(", ", rates)}.");
            }
        }

        public void CheckSettled()
        {
            var rates = Rates();
            CheckFloors(rates);
            var bound = _budget * (1 + Tolerance) + Enumerable.Range(0, Count).Where(node => _silent[node]).Sum(_ => Floor());
            Assert.True(rates.Sum() <= bound, $"OR-I21: settled, the nodes run at {string.Join(", ", rates)}, past {bound}.");
        }

        public double[] Rates() => [.. _nodes.Select(node => node.RateAt(Now) ?? 0)];

        private void CheckFloors(double[] rates)
        {
            for (var node = 0; node < Count; node++)
            {
                if (_assignedAt[node] is { } at && Now - at >= Deadline)
                {
                    Assert.True(rates[node] <= Floor() * (1 + Tolerance), $"OR-I22: node {node} runs at {rates[node]} with no share since {at}.");
                }
            }
        }

        /// <summary>Returns the floor each node falls to: over the active nodes its registry view, the current one, lists.</summary>
        private double Floor() => ArchiveLogRequestShare.Floor(_budget, Math.Max(1, Enumerable.Range(0, Count).Count(node => _registered[node] && !_retired[node])));

        private void Report(int node, long keys, bool delivered)
        {
            if (_silent[node])
            {
                return;
            }

            var share = _shares.Report(Node(node + 1), keys, _nodes[node].RateAt(Now), _view, Now);
            Assert.True(share is null || !ReferenceEquals(_view, NodeView.Empty), "OR-I21: an activation assigned a share under a view it has not observed.");
            if (delivered)
            {
                if (_shares.Ready)
                {
                    _heardFrom[node] = _activations[^1];
                }

                _nodes[node].Observe(share, Now);
                if (share is not null)
                {
                    _assignedAt[node] = Now;
                }
            }
        }

        private RequestBudgetShares Activate()
        {
            _activations.Add(new Activation(_budget));
            return new RequestBudgetShares(_budget, _interval, Now);
        }

        private void Relist()
        {
            if (!ReferenceEquals(_view, NodeView.Empty))
            {
                _view = View();
            }
        }

        private NodeView View()
        {
            var nodes = Enumerable.Range(0, Count).Where(node => _registered[node])
                .ToFrozenDictionary(static node => Node(node + 1), node => new RegisteredNode(RetiredRevision: _retired[node] ? 1UL : null, null, Generation, Ordinal: null));
            var liveness = Enumerable.Range(0, Count).Where(node => _registered[node]).ToFrozenDictionary(static node => Node(node + 1), node => _dead[node] ? new NodeLiveness.Dead(Now) : (NodeLiveness)NodeLiveness.Live.Instance);
            return new NodeView(new RegisteredNodes(++_revision, nodes), liveness);
        }

        /// <summary>A controller activation, by its budget.</summary>
        private sealed class Activation(double budget)
        {
            public double Budget { get; } = budget;

            /// <summary>Gets the highest floor the activation assigns or leaves a node at, over any count of nodes.</summary>
            public double Floor { get; } = ArchiveLogRequestShare.Floor(budget, nodes: 1);
        }
    }
}
