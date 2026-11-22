using System.Collections.Immutable;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Each node's reconciliation (ledgers.md §8.5, LG-I7): it drops exactly the replicas its map view revokes, through its
/// own <see cref="ILedgerStore"/>, over the in-memory transport's handles and the LedgerStore alike; a stale view drops
/// a subset; and a view which predates a ledger's installation never drops it.
/// </summary>
public sealed class ReplicaReconcilerTests : IDisposable
{
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly NodeId Other = LedgerMapSamples.Node(9);
    private static readonly NodeId Third = LedgerMapSamples.Node(10);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Providers => ["memory", "store"];

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The view revokes a removed ledger (1), a ledger listed without the node, a stray replica (3), and a removed one
    /// below the view's <c>NextLedgerId</c> (5), and keeps the listed open (2) and closed (4) ledgers and one at or above
    /// <c>NextLedgerId</c> (7).
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ReconciliationDropsExactlyTheReplicasItsViewRevokes(string provider)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var node = await NodeUnderTest.CreateAsync(provider, [1, 2, 3, 4, 5, 7], cancellationToken);
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await map.PublishAsync(
            Value(epoch: 4, next: 6, [Listed(2, node.Id, Lower, LedgerStatus.Open.Instance), Listed(3, Other, Upper, LedgerStatus.Open.Instance), Listed(4, node.Id, KeyRange.Root, new LedgerStatus.Closed(new Slot(9)))]),
            cancellationToken);

        var dropped = await new ReplicaReconciler(node.Store, map.View).ReconcileAsync(cancellationToken);

        Assert.Equal([new LedgerId(1), new LedgerId(3), new LedgerId(5)], dropped);
        Assert.Equal([new LedgerId(2), new LedgerId(4), new LedgerId(7)], await node.Store.ListReplicasAsync(cancellationToken));
        Assert.Empty(await new ReplicaReconciler(node.Store, map.View).ReconcileAsync(cancellationToken));
    }

    /// <summary>A view which lags drops a subset of what a newer view drops, and the newer view then drops the rest.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AStaleViewDropsASubset(string provider)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var node = await NodeUnderTest.CreateAsync(provider, [1, 2, 3], cancellationToken);
        var stale = await TestLedgerMap.OpenAsync(cancellationToken);
        await stale.PublishAsync(Value(epoch: 3, next: 3, [Listed(2, node.Id, KeyRange.Root, LedgerStatus.Open.Instance)]), cancellationToken);

        Assert.Equal([new LedgerId(1)], await new ReplicaReconciler(node.Store, stale.View).ReconcileAsync(cancellationToken));

        var current = await TestLedgerMap.OpenAsync(cancellationToken);
        await current.PublishAsync(Value(epoch: 6, next: 4, [Listed(3, node.Id, KeyRange.Root, LedgerStatus.Open.Instance)]), cancellationToken);
        Assert.Equal([new LedgerId(2)], await new ReplicaReconciler(node.Store, current.View).ReconcileAsync(cancellationToken));
        Assert.Equal([new LedgerId(3)], await node.Store.ListReplicasAsync(cancellationToken));
    }

    /// <summary>
    /// LG-I3, LG-I7: a view whose <c>NextLedgerId</c> is at or below a replica's ledger predates its installation, so it
    /// revokes nothing of it, even with no ledger listed at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AViewWhichPredatesALedgersInstallationNeverDropsIt(string provider)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var node = await NodeUnderTest.CreateAsync(provider, [5, 6], cancellationToken);
        var map = await TestLedgerMap.OpenAsync(cancellationToken);

        Assert.Empty(await new ReplicaReconciler(node.Store, map.View).ReconcileAsync(cancellationToken));
        await map.PublishAsync(Value(epoch: 2, next: 5, []), cancellationToken);
        Assert.Empty(await new ReplicaReconciler(node.Store, map.View).ReconcileAsync(cancellationToken));
        Assert.Equal([new LedgerId(5), new LedgerId(6)], await node.Store.ListReplicasAsync(cancellationToken));
    }

    /// <summary>
    /// A reconciler's later reconciliations look only at what the view's change revokes: the ledgers which lost the node
    /// (1) and the replicas of ledgers installed since its last view, a stray (6) and one listed without the node (7). A
    /// stray created afterwards below the earlier view's <c>NextLedgerId</c> (5) is left to the sweep, which judges every
    /// replica.
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ALaterReconciliationDropsOnlyWhatTheViewsChangeRevokes(string provider)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var node = await NodeUnderTest.CreateAsync(provider, [1, 2, 3], cancellationToken);
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await map.PublishAsync(
            Value(epoch: 2, next: 6, [Listed(1, node.Id, KeyRange.Root, new LedgerStatus.Closed(new Slot(9))), Listed(2, node.Id, Lower, LedgerStatus.Open.Instance), Listed(3, node.Id, Upper, LedgerStatus.Open.Instance)]),
            cancellationToken);
        var reconciler = new ReplicaReconciler(node.Store, map.View);
        Assert.Empty(await reconciler.ReconcileAsync(cancellationToken));

        await node.AddAsync([5, 6, 7], cancellationToken);
        await map.PublishAsync(
            Value(epoch: 3, next: 8, [Listed(2, node.Id, Lower, LedgerStatus.Open.Instance), Listed(3, node.Id, Upper, new LedgerStatus.Closed(new Slot(4))), Listed(7, Other, Upper, LedgerStatus.Open.Instance)]),
            cancellationToken);

        Assert.Equal([new LedgerId(1), new LedgerId(6), new LedgerId(7)], await reconciler.ReconcileAsync(cancellationToken));
        Assert.Empty(await reconciler.ReconcileAsync(cancellationToken));
        Assert.Equal([new LedgerId(5)], await reconciler.SweepAsync(cancellationToken));
        Assert.Equal([new LedgerId(2), new LedgerId(3)], await node.Store.ListReplicasAsync(cancellationToken));
    }

    /// <summary>
    /// Over generated histories of transitions and drops, a node holding the replicas every value listed with it, and
    /// stray replicas of any ledger, reconciles with any value of the history, older than some replicas' installation
    /// among them; it never drops a replica the view or any later value lists with the node, and drops every replica the
    /// view revokes.
    /// </summary>
    [Fact]
    public async Task ReconciliationNeverDropsAReplicaALaterMapListsWithTheNode() =>
        await Gen.Select(LedgerMapSamples.Steps, Gen.Int[0, 1_000], Gen.Int[0, LedgerMapWorld.NodeCount - 1], Gen.Int[0, 1_000].Array[0, 3])
            .SampleAsync(sample => _leaks.InScopeAsync(() => CheckAsync(sample.Item1, sample.Item2, sample.Item3, sample.Item4)), iter: 200);

    private static async Task CheckAsync(LedgerMapStep[] steps, int viewChoice, int nodeChoice, int[] strays)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var history = History(steps);
        var node = LedgerMapWorld.Node(1 + nodeChoice);
        using var transport = new InMemoryLedgerReplicaTransport();
        var maxId = history[^1].NextLedgerId.Value;
        var held = new SortedSet<ulong>();
        foreach (var value in history)
        {
            foreach (var ledger in value.Ledgers.Where(ledger => ledger.Replicas.Contains(node)))
            {
                _ = held.Add(ledger.Id.Value);
            }
        }

        foreach (var stray in strays)
        {
            _ = held.Add(1 + (ulong)stray % (maxId + 2));
        }

        foreach (var id in held)
        {
            await SealAsync(transport, node, id, cancellationToken);
        }

        var viewIndex = viewChoice % history.Count;
        var view = history[viewIndex];
        var register = new ScriptedConditionalRegister();
        if (view.Epoch > 0)
        {
            register.Put(LedgerMapSamples.Encoded(view));
        }

        var map = await LedgerMapView.OpenAsync(register, cancellationToken);

        var dropped = await new ReplicaReconciler(transport.Node(node), map).ReconcileAsync(cancellationToken);

        foreach (var id in dropped)
        {
            for (var later = viewIndex; later < history.Count; later++)
            {
                Assert.False(
                    history[later].Get(id) is { } listed && listed.Replicas.Contains(node),
                    $"Dropped ledger {id} which value {later} lists with the node.");
            }
        }

        Assert.Equal([.. held.Where(id => Revokes(view, new LedgerId(id), node)).Select(static id => new LedgerId(id))], dropped);
    }

    /// <summary>
    /// ledgers.md §8.5, restated: a view revokes a node's replica of a ledger whose ID lies below the view's
    /// <c>NextLedgerId</c> and which the view does not list with the node.
    /// </summary>
    private static bool Revokes(LedgerMapValue view, LedgerId ledger, NodeId node) =>
        ledger.Value < view.NextLedgerId.Value && !(view.Get(ledger) is { } listed && listed.Replicas.Contains(node));

    private static List<LedgerMapValue> History(LedgerMapStep[] steps)
    {
        var world = new LedgerMapWorld();
        foreach (var step in steps)
        {
            if (world.Derive(step) is not { Defective: false } derived)
            {
                continue;
            }

            var current = world.History[^1];
            var (result, _, applied) = LedgerMapRules.Fold(current, [derived.Change], int.MaxValue);
            if (!applied.IsEmpty)
            {
                world.History.Add(new LedgerMapValue(current.Epoch + 1, result.NextLedgerId, result.RetirementFloor, result.Ledgers));
            }
        }

        return world.History;
    }

    private static async Task SealAsync(ILedgerReplicaTransport transport, NodeId node, ulong id, CancellationToken cancellationToken)
    {
        // A seal creates the replica, as the first write, recovery write, or seal which reaches a node does (LR-I7).
        await using var replica = transport.Open(node, new LedgerId(id), Options);
        _ = await replica.ReadTailAsync(Slot.Zero, seal: true, cancellationToken);
    }

    private static LedgerMapValue Value(ulong epoch, ulong next, ImmutableArray<LedgerMetadata> ledgers) =>
        new(epoch, new LedgerId(next), 0, ledgers);

    private static KeyRange Lower => KeyRange.Root.Children.Lower;

    private static KeyRange Upper => KeyRange.Root.Children.Upper;

    private static LedgerMetadata Listed(ulong id, NodeId replica, KeyRange range, LedgerStatus status) =>
        new(
            new LedgerId(id),
            range,
            [replica, replica == Other ? Third : Other, LedgerMapSamples.Node(11)],
            Options.WindowSize,
            new PrimaryClaim(replica, new SequencerInstanceId(Guid.CreateVersion7())),
            status);

    /// <summary>One node's store, in memory or a LedgerStore, holding a replica of each of the given ledgers.</summary>
    private sealed class NodeUnderTest : IAsyncDisposable
    {
        private readonly IAsyncDisposable? _owner;
        private readonly InMemoryLedgerReplicaTransport? _transport;

        private NodeUnderTest(ILedgerStore store, IAsyncDisposable? owner, InMemoryLedgerReplicaTransport? transport)
        {
            Store = store;
            _owner = owner;
            _transport = transport;
        }

        public ILedgerStore Store { get; }

        public NodeId Id => Store.NodeId;

        /// <summary>Creates a replica of each of <paramref name="ledgers"/>, as a write or seal reaching the node does.</summary>
        public async Task AddAsync(ulong[] ledgers, CancellationToken cancellationToken)
        {
            foreach (var id in ledgers)
            {
                if (_transport is not null)
                {
                    await SealAsync(_transport, Id, id, cancellationToken);
                }
                else
                {
                    _ = await ((LedgerStore)Store).CreateReplicaAsync(new LedgerId(id), cancellationToken);
                }
            }
        }

        public static async Task<NodeUnderTest> CreateAsync(string provider, ulong[] ledgers, CancellationToken cancellationToken)
        {
            if (provider == "memory")
            {
                var transport = new InMemoryLedgerReplicaTransport();
                var node = LedgerMapSamples.Node(1);
                foreach (var id in ledgers)
                {
                    await SealAsync(transport, node, id, cancellationToken);
                }

                return new NodeUnderTest(transport.Node(node), owner: null, transport);
            }

            var store = await LedgerStoreReplicaTests.Simulated().OpenAsync(create: true);
            foreach (var id in ledgers)
            {
                _ = await store.CreateReplicaAsync(new LedgerId(id), cancellationToken);
            }

            return new NodeUnderTest(store, store, transport: null);
        }

        public async ValueTask DisposeAsync()
        {
            _transport?.Dispose();
            if (_owner is not null)
            {
                await _owner.DisposeAsync();
            }
        }
    }
}
