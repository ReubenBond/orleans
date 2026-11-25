using System.Collections.Concurrent;
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
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// OR-I5 as properties (orleans.md §5.7): over generated registries, with retired nodes and failure domains, and
/// memberships in which nodes are live, dead, or absent, placement and whole passes name, for every transition, <c>N</c>
/// distinct nodes, its executor first, each active in the registry value at <c>ChosenAt</c> and live in the membership
/// view, or, only for a takeover or a leaving primary's hand-off with fewer than <c>N</c> such nodes, at least <c>Q</c>
/// of them followed by the replaced ledger's replicas dead in membership and active in the registry
/// (<see cref="ReplicaChoice"/>); placement answers no placement exactly when too few nodes may take the ledger.
/// </summary>
public sealed class ControllerPlacementPropertyTests : IDisposable
{
    private static readonly Gen<NodeState[]> Nodes =
        Gen.Select(Gen.Int[0, 2], Gen.Bool, Gen.Int[0, 3]).Select(static (liveness, retired, domain) => new NodeState(liveness, retired, domain)).Array[3, 7];

    private static readonly Gen<NodeState[]> EligibleNodes = Gen.Int[0, 3].Select(static domain => new NodeState(Liveness: 0, Retired: false, domain)).Array[3, 7];

    /// <summary>
    /// The degraded window: <c>N = 2 · Size + 1</c>, between <c>Q − 1</c> and <c>N</c> nodes which may be given work, then up
    /// to <c>N + 1</c> nodes of the other kinds, dead active ones most often; the order which ranks the nodes into the
    /// current replicas; the executor's pick; and whether placement is asked degraded, most often.
    /// </summary>
    private static readonly Gen<(int Size, NodeKind[] Kinds, int[] Order, int Pick, bool Degraded)> DegradedWindow =
        Gen.Int[1, 2].SelectMany(static size => Gen.Select(
            Gen.Int[size, 2 * size + 1],
            Gen.OneOfConst(NodeKind.DeadActive, NodeKind.DeadActive, NodeKind.DeadActive, NodeKind.DeadRetired, NodeKind.Absent, NodeKind.LiveRetired, NodeKind.HandingOff, NodeKind.DepartingActive, NodeKind.DepartingRetired, NodeKind.Leaving).Array[0, 2 * size + 2],
            Gen.Int[0, 1000].Array[16],
            Gen.Int[0, 100],
            Gen.Int[0, 3]).Select(sample => (size, Enumerable.Repeat(NodeKind.Live, sample.Item1).Concat(sample.Item2).ToArray(), sample.Item3, sample.Item4, sample.Item5 != 0)));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Placement names <c>N</c> distinct live active nodes, the executor first and none leaving, or answers none exactly
    /// when fewer than <c>N</c> live active nodes, the executor among them, are not leaving: it never chooses a node the
    /// generated cluster holds retired, dead, or absent from membership, as executor or replica (OR-I5). A degraded choice
    /// differs only with fewer than <c>N</c> such nodes and at least <c>Q</c>: it then keeps the current replicas which
    /// the cluster holds dead and active, after every live one, and answers none exactly when too few remain.
    /// </summary>
    [Fact]
    public void PlacementNamesNDistinctLiveActiveNodesExecutorFirst() =>
        Gen.Select(Nodes, Gen.Int[0, 2], Gen.Int[0, 100], Gen.Int[0, 7].Array[0, 4], Gen.Int[0, 7].Array[0, 3], Gen.Int[0, 7].Array[0, 4], Gen.Bool).Sample(
            static sample => AssertPlacement(sample.Item1, sample.Item2, sample.Item3, sample.Item4, sample.Item5, sample.Item6, sample.Item7),
            iter: 500);

    /// <summary>
    /// Over clusters whose every node is live and active, so each may fill a place, pressure never fails a placement and
    /// ranks below failure domains (orleans.md §5.6): a node under disk pressure fills a place only when no unchosen node
    /// under none could, with no domain or one the other chosen nodes do not use.
    /// </summary>
    [Fact]
    public void PlacementRanksFailureDomainsAboveDiskPressure() =>
        Gen.Select(EligibleNodes, Gen.Int[0, 2], Gen.Int[0, 100], Gen.Int[0, 7].Array[0, 4], Gen.Int[0, 7].Array[0, 1], Gen.Int[0, 7].Array[1, 4]).Sample(
            static sample => AssertPlacement(sample.Item1, sample.Item2, sample.Item3, sample.Item4, sample.Item5, sample.Item6, degraded: false),
            iter: 500);

    /// <summary>
    /// Places a ledger over the generated nodes, of <c>2 · size + 1</c> replicas, the executor the <paramref name="pick"/>-th
    /// node which may execute, and judges the placement by OR-I5 and by the rank of failure domains above disk pressure.
    /// </summary>
    private static void AssertPlacement(NodeState[] nodes, int size, int pick, int[] current, int[] leaving, int[] pressured, bool degraded)
    {
        var replicas = 2 * size + 1;
        var underDiskPressure = pressured.Where(index => index < nodes.Length).Select(static index => Node(index + 1)).ToFrozenSet();
        var state = new ClusterState(new MapCache(LedgerMapValue.Empty), View(nodes, 1), FrozenDictionary<NodeId, int>.Empty, FrozenDictionary<NodeId, int>.Empty, FrozenDictionary<LedgerId, long>.Empty, unserved: FrozenDictionary<NodeId, FrozenSet<LedgerId>>.Empty, lost: FrozenDictionary<NodeId, FrozenSet<LedgerId>>.Empty, underDiskPressure: underDiskPressure, draining: FrozenDictionary<NodeId, bool>.Empty, now: DateTimeOffset.UnixEpoch);
        if (state.Executors.IsEmpty)
        {
            return;
        }

        var executor = state.Executors[pick % state.Executors.Length];
        ImmutableArray<NodeId> currentNodes = [.. current.Distinct().Where(index => index < nodes.Length).Select(static index => Node(index + 1))];
        ImmutableArray<NodeId> leavingNodes = [.. leaving.Distinct().Where(index => index < nodes.Length).Select(static index => Node(index + 1)).Where(node => node != executor)];

        var placed = Engine.Cluster.Control.Placement.Place(state, executor, currentNodes, leavingNodes, replicas, degraded);

        bool Eligible(NodeId node) => nodes[Index(node)] is { Liveness: 0, Retired: false } && !leavingNodes.Contains(node);
        bool Keepable(NodeId node) => nodes[Index(node)] is { Liveness: 1, Retired: false } && currentNodes.Contains(node) && !leavingNodes.Contains(node);
        var available = Enumerable.Range(1, nodes.Length).Select(static index => Node(index)).Count(Eligible);
        var keepable = currentNodes.Count(Keepable);
        var degrades = degraded && available < replicas && available >= (replicas + 1) / 2;
        Assert.Equal(degrades ? available + keepable < replicas : available < replicas, placed is null);
        if (placed is not { } chosen)
        {
            return;
        }

        Assert.Equal(replicas, chosen.Distinct().Count());
        Assert.Equal(executor, chosen[0]);
        var live = Math.Min(available, replicas);
        Assert.All(chosen.Take(live), node => Assert.True(Eligible(node), $"Placement chose {node}, generated {nodes[Index(node)]}."));
        Assert.All(chosen.Skip(live), node => Assert.True(degrades && Keepable(node), $"Placement kept {node}, generated {nodes[Index(node)]}, degraded {degraded}."));
        Assert.Equal(currentNodes.Where(Keepable).Take(replicas - live), chosen.Skip(live));
        foreach (var filler in chosen.Skip(1).Where(node => !currentNodes.Contains(node) && underDiskPressure.Contains(node)))
        {
            var used = chosen.Where(node => node != filler).Select(node => nodes[Index(node)].Domain).ToHashSet();
            var passed = Enumerable.Range(1, nodes.Length).Select(static index => Node(index))
                .Where(node => Eligible(node) && !chosen.Contains(node) && !underDiskPressure.Contains(node))
                .Where(node => nodes[Index(node)].Domain is 0 || !used.Contains(nodes[Index(node)].Domain));
            Assert.Empty(passed);
        }
    }

    /// <summary>
    /// Over generated nodes in the degraded window, <c>N</c> of 3 or 5 with between <c>Q − 1</c> and <c>N</c> nodes which may
    /// be given work, beside dead active, dead retired, absent, live retired, handing-off, and leaving ones, each possibly a
    /// current replica in a generated order, placement takes the exact shape of orleans.md §5.6: with <c>N</c> nodes which
    /// may be given work, <c>N</c> of them; degraded, with at least <c>Q</c>, every one of them, the executor first, then the
    /// current replicas dead in membership and active in the registry, in their order, or none when too few remain; and
    /// otherwise none. Each outcome is reached for both <c>N</c>, two kept replicas among them.
    /// </summary>
    [Fact]
    public void DegradedPlacementTakesEveryNodeGivenWorkThenTheDeadReplicasInTheirOrder()
    {
        var classes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        DegradedWindow.Sample(
            sample =>
            {
                var outcome = AssertDegradedWindow(sample.Size, sample.Kinds, sample.Order, sample.Pick, sample.Degraded);
                _ = classes.AddOrUpdate($"{2 * sample.Size + 1}:{outcome}", 1, static (_, count) => count + 1);
            },
            iter: 2000);

        string[] expected = ["full", "below-quorum", "kept-1", "short", "not-degraded"];
        foreach (var replicas in new[] { 3, 5 })
        {
            Assert.All(expected, outcome => Assert.True(classes.GetValueOrDefault($"{replicas}:{outcome}") >= 10, $"{replicas}:{outcome} reached {classes.GetValueOrDefault($"{replicas}:{outcome}")} times: {string.Join(", ", classes)}."));
        }

        Assert.True(classes.GetValueOrDefault("5:kept-2") >= 10, $"5:kept-2 reached {classes.GetValueOrDefault("5:kept-2")} times.");
    }

    /// <summary>
    /// Places over the generated degraded window and judges the exact shape (orleans.md §5.6, OR-I5); returns the outcome's
    /// class: <c>full</c>, <c>below-quorum</c>, <c>not-degraded</c>, <c>short</c> of keepable replicas, or <c>kept-k</c>.
    /// </summary>
    private static string AssertDegradedWindow(int size, NodeKind[] kinds, int[] order, int pick, bool degraded)
    {
        var replicas = 2 * size + 1;
        var quorum = size + 1;
        var all = Enumerable.Range(1, kinds.Length).Select(static index => Node(index)).ToArray();
        NodeKind KindOf(NodeId node) => kinds[Index(node)];
        var registry = new RegisteredNodes(1, all.ToFrozenDictionary(static node => node, node => new RegisteredNode(KindOf(node) is NodeKind.DeadRetired or NodeKind.LiveRetired or NodeKind.DepartingRetired ? 1UL : null, null, Generation: 1, Ordinal: null)));
        var liveness = all.Where(node => KindOf(node) != NodeKind.Absent).ToFrozenDictionary(
            static node => node,
            node => KindOf(node) switch
            {
                NodeKind.DeadActive or NodeKind.DeadRetired => (NodeLiveness)new NodeLiveness.Dead(DateTimeOffset.UnixEpoch),
                NodeKind.DepartingActive or NodeKind.DepartingRetired => NodeLiveness.Departing.Instance,
                NodeKind.Live or NodeKind.Absent or NodeKind.LiveRetired or NodeKind.HandingOff or NodeKind.Leaving => NodeLiveness.Live.Instance,
                _ => throw new System.Diagnostics.UnreachableException(),
            });
        var handingOff = all.Where(node => KindOf(node) == NodeKind.HandingOff).ToFrozenDictionary(static node => node, static _ => 1);
        var state = new ClusterState(new MapCache(LedgerMapValue.Empty), new NodeView(registry, liveness), handingOff, FrozenDictionary<NodeId, int>.Empty, FrozenDictionary<LedgerId, long>.Empty, unserved: FrozenDictionary<NodeId, FrozenSet<LedgerId>>.Empty, lost: FrozenDictionary<NodeId, FrozenSet<LedgerId>>.Empty, underDiskPressure: FrozenSet<NodeId>.Empty, draining: FrozenDictionary<NodeId, bool>.Empty, now: DateTimeOffset.UnixEpoch);
        var givenWork = all.Where(node => KindOf(node) == NodeKind.Live).ToArray();
        var executor = givenWork[pick % givenWork.Length];
        ImmutableArray<NodeId> current = [.. all.OrderBy(node => order[Index(node)]).Take(replicas)];
        ImmutableArray<NodeId> leaving = [.. all.Where(node => KindOf(node) == NodeKind.Leaving)];
        ImmutableArray<NodeId> keepable =
        [
            .. current.Where(node => KindOf(node) == NodeKind.DeadActive),
            .. current.Where(node => KindOf(node) is NodeKind.HandingOff or NodeKind.DepartingActive),
        ];

        var placed = Engine.Cluster.Control.Placement.Place(state, executor, current, leaving, replicas, degraded);

        string Expect(bool placesNone, string outcome)
        {
            Assert.Equal(placesNone, placed is null);
            return outcome;
        }

        if (givenWork.Length >= replicas)
        {
            var chosen = Assert.NotNull(placed);
            Assert.Equal(executor, chosen[0]);
            Assert.Equal(replicas, chosen.Distinct().Count());
            Assert.All(chosen, node => Assert.Contains(node, givenWork));
            return "full";
        }

        if (givenWork.Length < quorum)
        {
            return Expect(placesNone: true, "below-quorum");
        }

        if (!degraded)
        {
            return Expect(placesNone: true, "not-degraded");
        }

        if (givenWork.Length + keepable.Length < replicas)
        {
            return Expect(placesNone: true, "short");
        }

        var degradedChoice = Assert.NotNull(placed);
        Assert.Equal(executor, degradedChoice[0]);
        Assert.Equal(givenWork.Order(NodeOrder.Instance), degradedChoice.Take(givenWork.Length).Order(NodeOrder.Instance));
        Assert.Equal(keepable.Take(replicas - givenWork.Length), degradedChoice.Skip(givenWork.Length));
        return $"kept-{replicas - givenWork.Length}";
    }

    /// <summary>
    /// Every transition a pass sends, from a bootstrap or a root ledger over generated nodes whose primary may be dead,
    /// retired, handing off, or report the ledger unserved, satisfies OR-I5 against the registry value at its
    /// <c>ChosenAt</c>, and neither its executor nor any replica it names has a hand-off pending.
    /// </summary>
    [Fact]
    public Task EveryTransitionAPassSendsSatisfiesORI5() =>
        SampleAsync(Environment.GetEnvironmentVariable("DURABLE_STORAGE_TEST_SEED") is { Length: > 0 } seed ? seed : null, iter: 150);

    /// <summary>
    /// The seed which found the hand-off oracle stale: a node asks to hand off while membership does not list it, and a later
    /// revision shows it live and active, so its request has ended and it may execute again.
    /// </summary>
    [Fact]
    public Task AHandOffEndsWhenItsNodeLeavesMembership() => SampleAsync("9vHPrzu6ObOn", iter: 150);

    /// <summary>
    /// OR-I5 over passes in the degraded window: <c>N</c> of 3 or 5, the root's primary serving, asking to hand off,
    /// reporting the root unserved, dead, dead and retired, retired, or departing, and between <c>Q − 1</c> and <c>N − 1</c>
    /// other nodes which may be given work beside dead active, dead retired, absent, live retired, and departing ones. Every
    /// transition satisfies <see cref="ReplicaChoice"/>, and degraded takeovers, hand-offs of a retired primary, and
    /// departures are each sent for both <c>N</c>.
    /// </summary>
    [Fact]
    public async Task EveryTransitionAPassSendsInTheDegradedWindowSatisfiesORI5()
    {
        var degradedSends = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var nonLive = Gen.OneOfConst(new NodeState(1, false, 0), new NodeState(1, false, 0), new NodeState(1, false, 0), new NodeState(1, true, 0), new NodeState(2, false, 0), new NodeState(0, true, 0), new NodeState(3, false, 0));
        var window = Gen.Int[1, 2].SelectMany(size => Gen.Select(
            Gen.Int[size, 2 * size],
            nonLive.Array[0, 2 * size + 2],
            Gen.Int[0, 1000].Array[16],
            Gen.OneOfConst(0, 1, 2, 4, 5, 6, 7),
            Gen.Int[0, 7].Array[0, 2]).Select(sample =>
            {
                var others = Enumerable.Repeat(new NodeState(0, false, 0), sample.Item1).Concat(sample.Item2).ToList();
                while (others.Count < 2 * size)
                {
                    others.Add(new NodeState(1, false, 0));
                }

                NodeState[] nodes = [new NodeState(0, false, 0), .. others.Select((node, index) => (node, index)).OrderBy(entry => sample.Item3[entry.index]).Select(static entry => entry.node)];
                return (Size: size, Nodes: nodes, PrimaryState: sample.Item4, Changes: sample.Item5);
            }));

        await window.SampleAsync(
            async sample => await _leaks.InScopeAsync(() => PassAsync(sample.Nodes, bootstrap: false, [.. Enumerable.Range(0, 2 * sample.Size + 1)], sample.PrimaryState, sample.Changes, replicaCount: 2 * sample.Size + 1, degradedSends: degradedSends)),
            iter: 500);

        foreach (var key in new[] { "3:Takeover", "3:Handoff", "3:Departure", "5:Takeover", "5:Handoff", "5:Departure" })
        {
            Assert.True(degradedSends.GetValueOrDefault(key) >= 1, $"No degraded {key} was sent: {string.Join(", ", degradedSends)}.");
        }

        Assert.Equal(["Departure", "Handoff", "Takeover"], degradedSends.Keys.Select(static key => key[2..]).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The pass oracle catches a controller which places degraded beyond takeovers and leaving primaries' hand-offs: over
    /// three nodes, one dead and a replica of the root, the split toward the ledger target keeps the dead replica, while
    /// the controller sends nothing it could not place on live nodes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePassOracleCatchesADegradedSplit(bool degradedMutant)
    {
        NodeState[] nodes = [new(0, false, 0), new(0, false, 0), new(1, false, 0)];

        var pass = _leaks.InScopeAsync(() => PassAsync(nodes, bootstrap: false, rootReplicas: [0, 1, 2], primaryState: 0, changes: [], degradedMutant));

        if (degradedMutant)
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => pass);
            Assert.Contains("OR-I5", failure.Message, StringComparison.Ordinal);
            Assert.Contains("Split", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            await pass;
        }
    }

    /// <summary>
    /// orleans.md §5.6: over generated clusters, every node live and active, in a failure domain or none, and some under
    /// disk pressure, which hold eight open ledgers on generated replicas, passes whose transitions all apply reach a pass
    /// which sends none. Each balancing transition strictly lowers (sum of the squared primary counts, replicas sharing a
    /// failure domain, sum of the squared replica counts), so balancing never cycles; no roll adds a node under pressure and
    /// no hand-off goes to one. At quiescence, no node holds more than one primary above the fewest any node under no
    /// pressure holds, and no evening roll the policy allows remains.
    /// </summary>
    [Fact]
    public Task BalancingConvergesAndLeavesNoAllowedRoll() =>
        Gen.Select(Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3].Select(static value => value == 0)).Array[4, 8], Gen.Int[0, 7].Array[3].Array[8]).SampleAsync(
            async sample => await _leaks.InScopeAsync(() => ConvergeAsync(sample.Item1, sample.Item2, eveningMutant: false)),
            iter: 100);

    /// <summary>
    /// The shrunk input which found evening stalling on the node with the most replicas when that node could give up none,
    /// though another node above the bound could: evening converges on it, and the quiescence check flags the mutant which
    /// tries only the node with the most.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveningTriesEveryNodeAboveTheBound(bool eveningMutant)
    {
        (int Domain, bool UnderPressure)[] nodes = [(0, false), (2, false), (2, false), (0, false)];
        int[][] triples = [[0, 5, 0], [5, 0, 5], [2, 6, 7], [3, 3, 3], [5, 7, 3], [7, 3, 7], [5, 4, 7], [7, 7, 7]];

        var converge = _leaks.InScopeAsync(() => ConvergeAsync(nodes, triples, eveningMutant));

        if (eveningMutant)
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => converge);
            Assert.Contains("evening roll", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            await converge;
        }
    }

    /// <summary>
    /// Installs eight ledgers over the generated nodes, the <c>i</c>-th's replicas from the <c>i</c>-th triple of node
    /// indexes, and applies every transition each pass sends, until a pass sends none, judging each transition and the
    /// quiescent map.
    /// </summary>
    private static async Task ConvergeAsync((int Domain, bool UnderPressure)[] nodes, int[][] triples, bool eveningMutant)
    {
        var register = new ScriptedConditionalRegister();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var view = await LedgerMapView.OpenAsync(register);
        await using var committer = new LedgerMapCommitter(register, 1 << 20, view);
        var objects = new InMemoryObjectStore();
        var manifest = new ManifestView(objects);
        await using var manifestCommitter = new ManifestCommitter(objects, manifest, time, TimeSpan.Zero);
        var options = new ControlOptions { SequencerWindow = 4, LedgersPerNode = 1 };
        var controller = new ClusterController(options, view, committer, new ClusterArchive.Lsm(manifest, manifestCommitter, new ArchiveOptions()), time, NullLogger.Instance) { ReplicaEveningMutant = eveningMutant };
        var registry = new RegisteredNodes(1, nodes.Select(static (node, index) => (node, index)).ToFrozenDictionary(static entry => Node(entry.index + 1), static entry => new RegisteredNode(null, entry.node.Domain == 0 ? null : $"domain-{entry.node.Domain}", Generation: 1, Ordinal: null)));
        controller.Observe(registry, registry.Nodes.Keys.ToFrozenDictionary(static node => node, static _ => (NodeLiveness)NodeLiveness.Live.Instance));
        var all = Enumerable.Range(1, nodes.Length).Select(static index => Node(index)).ToList();
        var unpressured = all.Where(node => !nodes[Index(node)].UnderPressure).ToList();
        foreach (var node in all)
        {
            _ = controller.Report(NodeReport.Idle(node, generation: 1, sequence: 1) with { DiskPressure = nodes[Index(node)].UnderPressure });
        }

        ImmutableArray<KeyRange> ranges = [KeyRange.Root];
        for (var depth = 0; depth < 3; depth++)
        {
            ranges = [.. ranges.SelectMany(static range => new[] { range.Children.Lower, range.Children.Upper })];
        }

        _ = await controller.CommitLedgerMapAsync(new LedgerMapChange.Install([], [Opening(KeyRange.Root, [Node(1), Node(2), Node(3)])], 1));
        var root = view.Current.Ledgers[0];
        _ = await controller.CommitLedgerMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [.. ranges.Select((range, index) => Opening(range, Replicas(triples[index])))], 1));

        for (var pass = 0; pass < 200; pass++)
        {
            var result = controller.RunPass();
            if (!result.PrepareReleases.IsEmpty)
            {
                foreach (var prepare in result.PrepareReleases)
                {
                    _ = controller.PrepareReleaseCompleted(prepare, new PrepareReleaseReply.Released(Slot.Zero));
                }

                if (result.Transitions.IsEmpty)
                {
                    continue;
                }
            }

            var sent = result.Transitions;
            if (sent.IsEmpty)
            {
                AssertQuiescent();
                return;
            }

            foreach (var transition in sent)
            {
                var replace = Assert.IsType<Transition.Replace>(transition.Transition.Value);
                var before = Assert.NotNull(view.Current.Get(replace.Ledger));
                var measure = Measure();
                var choice = transition.Transition.Choice;
                var opening = new LedgerOpening(before.Range, choice.Replicas, choice.K, new PrimaryClaim(choice.Executor, SequencerInstanceId.New()));
                var outcome = await controller.CommitLedgerMapAsync(new LedgerMapChange.Install([new LedgerClosing(before.Id, Slot.Zero)], [opening], choice.ChosenAt));
                _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
                Assert.True(transition.Purpose is TransitionPurpose.Roll or TransitionPurpose.Handoff, $"{transition} is no balancing transition.");
                Assert.True(Measure().CompareTo(measure) < 0, $"{transition} of s{before.Id} on {string.Join(", ", before.Replicas)} lowers no measure from {measure}.");
                Assert.False(
                    transition.Purpose == TransitionPurpose.Roll && choice.Replicas.Any(node => nodes[Index(node)].UnderPressure && !before.Replicas.Contains(node)),
                    $"{transition} adds a node under disk pressure.");
                Assert.False(transition.Purpose == TransitionPurpose.Handoff && nodes[Index(choice.Executor)].UnderPressure, $"{transition} hands s{before.Id} to a node under disk pressure.");
                _ = controller.TransitionCompleted(transition, TransitionReply.Of(outcome));
            }
        }

        Assert.Fail("Balancing sent transitions for 100 passes whose transitions all applied: it cycles.");

        // The node indexes from the triple, each taking the next node not yet chosen.
        ImmutableArray<NodeId> Replicas(int[] triple)
        {
            List<NodeId> chosen = [];
            while (chosen.Count < 3)
            {
                var candidate = triple[chosen.Count];
                while (chosen.Contains(Node(candidate % nodes.Length + 1)))
                {
                    candidate++;
                }

                chosen.Add(Node(candidate % nodes.Length + 1));
            }

            return [.. chosen];
        }

        (long Primaries, int Shared, long Replicas) Measure()
        {
            var open = view.Current.Ledgers.Where(static ledger => ledger.IsOpen).ToList();
            return (
                all.Sum(node => Square(open.Count(ledger => ledger.Primary.Node == node))),
                open.Sum(ledger => Shared(ledger.Replicas)),
                all.Sum(node => Square(open.Count(ledger => ledger.Replicas.Contains(node)))));
        }

        // Evening's bound and allowed rolls, from the generated input and the quiescent map alone.
        void AssertQuiescent()
        {
            var open = view.Current.Ledgers.Where(static ledger => ledger.IsOpen).ToList();
            int Count(NodeId node, bool primary) => open.Count(ledger => primary ? ledger.Primary.Node == node : ledger.Replicas.Contains(node));
            if (unpressured.Count == 0)
            {
                return;
            }

            var fewestPrimaries = unpressured.Min(node => Count(node, primary: true));
            Assert.All(all, node => Assert.True(Count(node, primary: true) <= fewestPrimaries + 1, $"{node} holds {Count(node, primary: true)} primaries, the fewest {fewestPrimaries}."));
            var fewest = unpressured.Min(node => Count(node, primary: false));
            foreach (var source in all.Where(node => Count(node, primary: false) > fewest + 1))
            {
                foreach (var ledger in open.Where(ledger => ledger.Primary.Node != source && ledger.Replicas.Contains(source)))
                {
                    var allowed = unpressured.FirstOrDefault(target => !ledger.Replicas.Contains(target) && Count(target, primary: false) <= Count(source, primary: false) - 2
                        && Shared([.. ledger.Replicas.Where(node => node != source), target]) <= Shared(ledger.Replicas));
                    Assert.True(allowed == default, $"At quiescence an evening roll of s{ledger.Id} from {source} to {allowed} remains.");
                }
            }
        }

        int Shared(ImmutableArray<NodeId> replicas) =>
            replicas.Select((node, index) => (Domain: nodes[Index(node)].Domain, index))
                .Count(entry => entry.Domain != 0 && replicas.Take(entry.index).Any(earlier => nodes[Index(earlier)].Domain == entry.Domain));

        static long Square(int count) => (long)count * count;

        static LedgerOpening Opening(KeyRange range, ImmutableArray<NodeId> replicas) =>
            new(range, replicas, 4, new PrimaryClaim(replicas[0], SequencerInstanceId.New()));
    }

    private Task SampleAsync(string? seed, int iter) =>
        Gen.Select(Nodes, Gen.Bool, Gen.Int[0, 6].Array[3], Gen.Int[0, 3], Gen.Int[0, 7].Array[0, 4]).SampleAsync(
            async sample =>
            {
                var (nodes, bootstrap, rootReplicas, primaryState, changes) = sample;
                await _leaks.InScopeAsync(() => PassAsync(nodes, bootstrap, rootReplicas, primaryState, changes));
            },
            iter: iter,
            seed: seed);

    private static NodeView View(NodeState[] nodes, ulong revision) => new(Registry(nodes, revision), Liveness(nodes));

    /// <summary>Returns the index into the generated nodes of <paramref name="node"/>, which <see cref="Node"/> numbered from 1.</summary>
    private static int Index(NodeId node) => Enumerable.Range(0, 64).Single(index => Node(index + 1) == node);

    private static RegisteredNodes Registry(NodeState[] nodes, ulong revision) =>
        new(revision, nodes.Select(static (node, index) => (node, index)).ToFrozenDictionary(static entry => Node(entry.index + 1), entry => new RegisteredNode(entry.node.Retired ? Math.Max(revision, 1UL) : null, entry.node.Domain == 0 ? null : $"domain-{entry.node.Domain}", Generation: 1, Ordinal: null)));

    private static FrozenDictionary<NodeId, NodeLiveness> Liveness(NodeState[] nodes) =>
        nodes.Select(static (node, index) => (node, index)).Where(static entry => entry.node.Liveness != 2)
            .ToFrozenDictionary(static entry => Node(entry.index + 1), static entry => entry.node.Liveness switch
            {
                0 => NodeLiveness.Live.Instance,
                3 => NodeLiveness.Departing.Instance,
                _ => (NodeLiveness)new NodeLiveness.Dead(DateTimeOffset.UnixEpoch),
            });

    /// <summary>
    /// Runs passes over the generated cluster, changing one node's standing between them, and judges each transition
    /// against the registry value whose revision it carries. The root's primary serves (0 or 3), asks to hand off (1),
    /// reports the root unserved (2), is dead (4), dead and retired (5), retired (6), or departing (7); each degraded
    /// transition is counted in <paramref name="degradedSends"/> by <c>N</c> and purpose.
    /// </summary>
    private static async Task PassAsync(NodeState[] nodes, bool bootstrap, int[] rootReplicas, int primaryState, int[] changes, bool degradedMutant = false, int replicaCount = 3, ConcurrentDictionary<string, int>? degradedSends = null)
    {
        var register = new ScriptedConditionalRegister();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var view = await LedgerMapView.OpenAsync(register);
        await using var committer = new LedgerMapCommitter(register, 1 << 20, view);
        var objects = new InMemoryObjectStore();
        var manifest = new ManifestView(objects);
        await using var manifestCommitter = new ManifestCommitter(objects, manifest, time, TimeSpan.Zero);
        var options = new ControlOptions { SequencerWindow = 4, LedgersPerNode = 2, Replicas = replicaCount };
        var controller = new ClusterController(options, view, committer, new ClusterArchive.Lsm(manifest, manifestCommitter, new ArchiveOptions()), time, NullLogger.Instance) { DegradedPlacementMutant = degradedMutant };
        for (var node = 1; node <= nodes.Length; node++)
        {
            _ = controller.Report(NodeReport.Idle(Node(node), generation: 1, sequence: 1));
        }

        NodeId? handingOff = null;
        ImmutableArray<NodeId> replicas = [.. rootReplicas.Select(index => index % nodes.Length).Distinct().Select(static index => Node(index + 1))];
        var current = (NodeState[])nodes.Clone();
        if (!bootstrap && replicas.Length == replicaCount)
        {
            var primary = Index(replicas[0]);
            current[primary] = primaryState switch
            {
                4 => current[primary] with { Liveness = 1, Retired = false },
                5 => current[primary] with { Liveness = 1, Retired = true },
                6 => current[primary] with { Liveness = 0, Retired = true },
                7 => current[primary] with { Liveness = 3, Retired = false },
                _ => current[primary],
            };
            _ = await controller.CommitLedgerMapAsync(new LedgerMapChange.Install([], [new LedgerOpening(KeyRange.Root, replicas, 4, new PrimaryClaim(replicas[0], SequencerInstanceId.New()))], 0));
            if (primaryState == 1)
            {
                controller.RequestHandOff(replicas[0], generation: 1);
                handingOff = replicas[0];
            }
            else if (primaryState == 2)
            {
                _ = controller.Report(NodeReport.Idle(replicas[0], generation: 1, sequence: 2) with { Unserved = [view.Current.Ledgers[0].Id] });
            }
        }

        var revisions = new Dictionary<ulong, NodeView>();
        for (var revision = 1ul; revision <= (ulong)changes.Length + 1; revision++)
        {
            var nodeView = View(current, revision);
            revisions[revision] = nodeView;
            var liveness = Liveness(current);
            controller.Observe(nodeView.Registry, liveness);

            // A hand-off request ends once membership no longer shows its node live (orleans.md §5.6), after which the
            // node may execute again.
            if (handingOff is { } leaving && liveness.GetValueOrDefault(leaving) is not NodeLiveness.Live)
            {
                handingOff = null;
            }

            var passResult = controller.RunPass();
            var sentTransitions = passResult.Transitions;
            if (!passResult.PrepareReleases.IsEmpty)
            {
                foreach (var prepare in passResult.PrepareReleases)
                {
                    _ = controller.PrepareReleaseCompleted(prepare, new PrepareReleaseReply.Released(Slot.Zero));
                }

                sentTransitions = sentTransitions.AddRange(controller.RunPass().Transitions);
            }

            foreach (var sent in sentTransitions)
            {
                var choice = sent.Transition.Choice;
                var chosenAt = revisions[choice.ChosenAt];
                var predecessor = sent.Transition.Value is Transition.Replace replace ? view.Current.Get(replace.Ledger) : null;
                var givenWork = chosenAt.LiveActive.Where(node => node != handingOff).ToHashSet();
                var standing = predecessor is not { } closed ? PredecessorPrimary.Serving
                    : chosenAt.DeadSince(closed.Primary.Node) is not null || primaryState == 2 ? PredecessorPrimary.Ended
                    : chosenAt.IsRetired(closed.Primary.Node) || chosenAt.IsDeparting(closed.Primary.Node) || closed.Primary.Node == handingOff ? PredecessorPrimary.Leaving
                    : PredecessorPrimary.Serving;
                Assert.Null(ReplicaChoice.Violation(
                    sent,
                    options.Replicas,
                    predecessor,
                    standing,
                    givenWork,
                    node => chosenAt.DeadSince(node) is not null && chosenAt.Registry.Find(node) is { IsRetired: false },
                    node => chosenAt.IsUp(node) && (chosenAt.IsDeparting(node) || node == handingOff) && chosenAt.Registry.Find(node) is { IsRetired: false }));
                if (!choice.Replicas.All(givenWork.Contains))
                {
                    _ = degradedSends?.AddOrUpdate($"{replicaCount}:{sent.Purpose}", 1, static (_, count) => count + 1);
                }

                Assert.False(choice.Executor == handingOff, $"{sent} is executed by a node whose hand-off is pending.");
                if (givenWork.Count >= options.Replicas)
                {
                    Assert.False(handingOff is { } pending && choice.Replicas.Contains(pending), $"{sent} names a replica whose hand-off is pending while {givenWork.Count} nodes may be given work.");
                }
                controller.TransitionCompleted(sent, reply: null);
            }

            if (revision <= (ulong)changes.Length)
            {
                var changed = changes[(int)revision - 1] % current.Length;
                current[changed] = current[changed] with { Liveness = (current[changed].Liveness + 1) % 3, Retired = !current[changed].Retired };
            }

            time.Advance(options.TransitionTimeout);
        }
    }

    /// <summary>A generated node: live (0), dead (1), absent (2) from membership, or departing (3); retired or active; and its domain, none at 0.</summary>
    private readonly record struct NodeState(int Liveness, bool Retired, int Domain);

    /// <summary>A node of the degraded window, by what placement may do with it.</summary>
    private enum NodeKind
    {
        /// <summary>Live, active, and with no hand-off pending: it may be given work.</summary>
        Live,

        /// <summary>Dead in membership and active in the registry: a degraded choice may keep it as a current replica.</summary>
        DeadActive,

        /// <summary>Dead and retired: never placed (OR-I4).</summary>
        DeadRetired,

        /// <summary>Absent from membership.</summary>
        Absent,

        /// <summary>Live and retired.</summary>
        LiveRetired,

        /// <summary>Live and active with its hand-off pending: a degraded choice may keep it after dead active replicas.</summary>
        HandingOff,

        /// <summary>Departing in membership and active in the registry: a degraded choice may keep it after dead active replicas.</summary>
        DepartingActive,

        /// <summary>Departing and retired: never placed (OR-I4).</summary>
        DepartingRetired,

        /// <summary>Live and active, and leaving the ledger the placement is for.</summary>
        Leaving,
    }
}
