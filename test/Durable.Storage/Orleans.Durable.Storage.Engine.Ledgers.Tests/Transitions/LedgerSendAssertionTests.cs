using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions.TransitionCluster;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions;

/// <summary>
/// The sender's side of LG-I7 (ledgers.md §8.5): each send path asserts that the map it acts from lists the ledger with
/// every node it reaches. Each test builds a process's <see cref="Ledger"/> over a node its map does not list, as a
/// defect would, and the Debug leg's assertion fails at the send (verification.md §11.2). Release builds compile the
/// assertions away, so these run in the Debug leg alone. A reader's repair writes only to a node of the map entry its
/// quorum was built from, whose location is fixed for the ledger's life, so it needs no check of its own.
/// </summary>
public sealed class LedgerSendAssertionTests : IDisposable
{
    private const int Window = 4;
    private static readonly LedgerId Id = new(7);
    private static readonly ImmutableArray<NodeId> Listed = [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(4)];
    private static readonly ImmutableArray<NodeId> Unlisted = [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(3)];
    private static readonly SequencerInstanceId Claimed = new(Guid.Parse("00000000-0000-0000-0000-0000000000b7"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task APrimaryWriteToANodeItsMapDoesNotListFails()
    {
        SkipUnlessDebug();
        var cancellationToken = TestContext.Current.CancellationToken;
        var since = DebugAssertions.RaisedCount;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        var lifetime = new Sequencer(Window);
        await map.PublishAsync(Map(Listed, lifetime.InstanceId), cancellationToken);
        await using var owner = new ReaderOwner(map.View, transport);
        var reader = owner.Reader;
        var lease = reader.LeaseLedger(Ledger(Unlisted, lifetime.InstanceId));
        var writer = lease.CreateWriter(lifetime, map.View, WriterTestData.Host(), new FakeTimeProvider());

        var proposal = ProposeAsync(writer, 0x10, cancellationToken);

        // The write's failure stops the lifetime, so the writer helps the recovery, whose seals reach the node too.
        await WaitUntilAsync(() => DebugAssertions.FirstRaisedBy(test: null, since) is not null, "the send fails");
        await AssertHelperFaultSurfacesAsync(writer, lease, since);

        // The writer released its lease though its helper failed, so its reader disposes.
        await owner.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => proposal);
        AssertSendFailed(DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since));
    }

    [Fact]
    public async Task AnExecutorsSealOrRecoveryWriteToANodeItsMapDoesNotListFails()
    {
        SkipUnlessDebug();
        var cancellationToken = TestContext.Current.CancellationToken;
        var since = DebugAssertions.RaisedCount;
        await using var cluster = new TransitionCluster();
        cluster.Write(Map(Listed));
        var b = await cluster.StartAsync(LedgerMapSamples.Node(2), cancellationToken);
        using var forged = b.Reader.LeaseLedger(Ledger(Unlisted));

        _ = await Assert.ThrowsAsync<DebugAssertionException>(() => b.ExecuteAsync(
            cluster,
            new Transition.Replace(Id, new TransitionChoice(b.Node, Listed, Window, 0, 1)),
            cancellationToken));

        AssertSendFailed(DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since));
        Assert.Equal(0, b.Registry.PendingLifetimes);
    }

    [Fact]
    public async Task APrimarysHelpingSealToANodeItsMapDoesNotListFails()
    {
        SkipUnlessDebug();
        var cancellationToken = TestContext.Current.CancellationToken;
        var since = DebugAssertions.RaisedCount;
        using var transport = new InMemoryLedgerReplicaTransport();
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        var lifetime = new Sequencer(Window);
        await map.PublishAsync(Map(Listed, lifetime.InstanceId), cancellationToken);
        await using var owner = new ReaderOwner(map.View, transport);
        var reader = owner.Reader;
        var lease = reader.LeaseLedger(Ledger(Unlisted, lifetime.InstanceId));
        var writer = lease.CreateWriter(lifetime, map.View, WriterTestData.Host(), new FakeTimeProvider());
        await using (var executor = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null))
        {
            using var sealing = executor.LeaseLedger(Ledger(Listed));
            _ = await sealing.Ledger.RecoverAsync(cancellationToken);
        }

        // The open check finds the ledger sealed and stops the lifetime, and the writer then helps the recovery from its
        // own process, outside the test's execution context.
        Assert.Same(OpenCheckOutcome.Sealed.Instance, (await writer.ConfirmOpenAsync(cancellationToken)).Value);

        await WaitUntilAsync(() => DebugAssertions.FirstRaisedBy(test: null, since) is not null, "the send fails");
        await AssertHelperFaultSurfacesAsync(writer, lease, since);

        // The writer released its lease though its helper failed, so its reader disposes.
        await owner.DisposeAsync();
    }

    public enum SendMap
    {
        /// <summary>The map lists the ledger with every node its sends reach.</summary>
        ListsEveryNode,

        /// <summary>The map lists the ledger without one of the nodes its sends reach.</summary>
        ListsWithoutANode,

        /// <summary>The map no longer lists the ledger, whose ID lies below its <c>NextLedgerId</c>: it revoked it.</summary>
        Revoked,

        /// <summary>The map does not yet list the ledger, whose ID lies at or above its <c>NextLedgerId</c>.</summary>
        NotYetListed,

        /// <summary>The ledger lies in the object store, and the map lists it there.</summary>
        ListsInTheObjectStore,

        /// <summary>The ledger lies on nodes, and the map lists it in the object store.</summary>
        ListsOnNodesAsInTheObjectStore,

        /// <summary>The ledger lies in the object store, and the map lists it on nodes.</summary>
        ListsInTheObjectStoreAsOnNodes,
    }

    /// <summary>
    /// Every branch of the sender's check (ledgers.md §8.5, LG-I7): it passes when the map lists the ledger at the
    /// location it was built with or has revoked it, and fails when the map lists it elsewhere, without a node or in the
    /// other kind of location, or does not yet list it. The front checks only its location, so the object-store cases
    /// build it over a quorum.
    /// </summary>
    [Theory]
    [InlineData(SendMap.ListsEveryNode, true)]
    [InlineData(SendMap.ListsWithoutANode, false)]
    [InlineData(SendMap.Revoked, true)]
    [InlineData(SendMap.NotYetListed, false)]
    [InlineData(SendMap.ListsInTheObjectStore, true)]
    [InlineData(SendMap.ListsOnNodesAsInTheObjectStore, false)]
    [InlineData(SendMap.ListsInTheObjectStoreAsOnNodes, false)]
    public async Task TheSendCheckPassesExactlyWhenTheMapListsEveryNodeOrRevokedTheLedger(SendMap map, bool passes)
    {
        SkipUnlessDebug();
        var since = DebugAssertions.RaisedCount;
        using var transport = new InMemoryLedgerReplicaTransport();
        LedgerLocation location = map is SendMap.ListsInTheObjectStore or SendMap.ListsInTheObjectStoreAsOnNodes
            ? LedgerLocation.ObjectStore.Instance
            : new LedgerLocation.Nodes(Listed);
        var options = new LedgerReplicaOptions { WindowSize = Window };
        await using var ledger = new Ledger(Id, LedgerQuorum.ForNodes(Id, Listed, Listed.Select(node => transport.Open(node, Id, options)), quorum: 2, options, new FakeTimeProvider(), localNode: null), location);
        var value = map switch
        {
            SendMap.ListsEveryNode or SendMap.ListsInTheObjectStoreAsOnNodes => Map(Listed),
            SendMap.ListsWithoutANode => Map(Unlisted),
            SendMap.ListsInTheObjectStore or SendMap.ListsOnNodesAsInTheObjectStore => new LedgerMapValue(1, new LedgerId(Id.Value + 1), 0, [InTheObjectStore()]),
            SendMap.Revoked => new LedgerMapValue(2, new LedgerId(Id.Value + 1), 0, []),
            SendMap.NotYetListed => new LedgerMapValue(2, Id, 0, []),
            _ => throw new ArgumentOutOfRangeException(nameof(map)),
        };

        if (passes)
        {
            ledger.AssertSendsFrom(value);
            Assert.Empty(DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since));
        }
        else
        {
            _ = Assert.Throws<DebugAssertionException>(() => ledger.AssertSendsFrom(value));
            AssertSendFailed(DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since));
        }
    }

    private static void SkipUnlessDebug() =>
        Assert.SkipUnless(TestDiagnostics.Configuration == "Debug", "Release builds compile the send paths' debug assertions away.");

    /// <summary>
    /// Asserts that the helper's failed send, an unexpected failure of its recovery, failed a second assertion and faulted
    /// it, and that the writer's disposal surfaces the fault; then releases the test's lease.
    /// </summary>
    private static async Task AssertHelperFaultSurfacesAsync(LedgerWriter writer, LedgerReader.LedgerLease lease, long since)
    {
        _ = await Assert.ThrowsAsync<DebugAssertionException>(async () => await writer.DisposeAsync());
        var raised = DebugAssertions.TakeRaisedBy(test: null, since);
        Assert.Equal(2, raised.Count);
        AssertSendFailed([raised[0]]);
        Assert.Contains("failed unexpectedly", raised[1].Message, StringComparison.Ordinal);
        lease.Dispose();
    }


    private static void AssertSendFailed(List<DebugAssertionException> raised)
    {
        var assertion = Assert.Single(raised);
        Assert.Contains("LG-I7", assertion.Message, StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(Ledger).FullName}.{nameof(Ledgers.Ledger.AssertSendsFrom)}+IL_", assertion.SiteKey, StringComparison.Ordinal);
    }

    private static LedgerMetadata Ledger(ImmutableArray<NodeId> replicas, SequencerInstanceId? sequencer = null) =>
        new(Id, KeyRange.Root, replicas, Window, new PrimaryClaim(replicas[0], sequencer ?? Claimed), LedgerStatus.Open.Instance);

    private static LedgerMetadata InTheObjectStore() =>
        new(Id, KeyRange.Root, LedgerLocation.ObjectStore.Instance, Window, new PrimaryClaim(Listed[0], Claimed), LedgerStatus.Open.Instance);

    private static LedgerMapValue Map(ImmutableArray<NodeId> replicas, SequencerInstanceId? sequencer = null) => new(1, new LedgerId(Id.Value + 1), 0, [Ledger(replicas, sequencer)]);

    /// <summary>
    /// Owns a reader and disposes it once, with a bound, so a lease which is never released fails the test rather than
    /// hanging it.
    /// </summary>
    private sealed class ReaderOwner(LedgerMapView map, ILedgerReplicaTransport transport) : IAsyncDisposable
    {
        private Task? _disposal;

        public LedgerReader Reader { get; } = new(map, transport, new FakeTimeProvider(), localNode: null);

        public async ValueTask DisposeAsync() =>
            await (_disposal ??= Reader.DisposeAsync().AsTask()).WaitAsync(Bound, CancellationToken.None);
    }
}
