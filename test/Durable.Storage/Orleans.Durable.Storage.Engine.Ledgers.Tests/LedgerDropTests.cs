using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins LP-I5 (ledger-protocol.md): a ledger's outcomes which meet replicas their nodes dropped, whose dropped answers
/// count as sealed ones.
/// </summary>
public sealed class LedgerDropTests : IDisposable
{
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LP-I5: a writer write which meets a dropped replica before <c>Q</c> written answers reports sealed, since the
    /// ledger has left the map, and a read finds a batch on the replicas which remain; once two replicas are dropped, a
    /// read which finds no batch reaches no <c>Q</c> missing answers and reports the drop.
    /// </summary>
    [Fact]
    public async Task AWriterWriteWhichMeetsADroppedReplicaIsSealed()
    {
        var token = TestContext.Current.CancellationToken;
        await using var nodes = new DroppingNodes();
        using var payload = Payload("value");
        Assert.Equal(ReplicaWriteCase.Written, (await nodes.Ledger.WriteAsync(0, payload, token)).Case);

        // The replicas answer in order, so the dropped one answers first.
        await nodes.DropAsync(0);
        Assert.Equal(ReplicaWriteCase.Sealed, (await nodes.Ledger.WriteAsync(1, payload, token)).Case);
        using (var read = (await nodes.Ledger.ReadAsync(0, token)).Batch)
        {
            Assert.Equal("value"u8.ToArray(), read.ToArray());
        }

        await nodes.DropAsync(1);
        DroppedAnswers.AssertDropped(await nodes.Ledger.ReadAsync(3, token));
        Assert.Equal(ReplicaWriteCase.Sealed, (await nodes.Ledger.WriteAsync(2, payload, token)).Case);
    }

    /// <summary>
    /// LP-I5, LP-I9: a seal check counts dropped answers as sealed ones, which carry no contiguous tail, so it reports
    /// sealed however many replicas their nodes dropped.
    /// </summary>
    [Fact]
    public async Task ASealCheckCountsDroppedAnswersAsSealed()
    {
        var token = TestContext.Current.CancellationToken;
        await using var nodes = new DroppingNodes();
        using var payload = Payload("value");
        var tail = new Slot(1);
        Assert.Equal(ReplicaWriteCase.Written, (await nodes.Ledger.WriteAsync(0, payload, token)).Case);
        Assert.Equal(new LedgerTail(tail, IsSealed: false), (await nodes.Ledger.ReadTailAsync(tail, token)).Tail);

        await nodes.DropAsync(0);
        Assert.Equal(new LedgerTail(tail, IsSealed: true), (await nodes.Ledger.ReadTailAsync(token)).Tail);

        await nodes.DropAsync(1);
        await nodes.DropAsync(2);
        Assert.Equal(new LedgerTail(tail, IsSealed: true), (await nodes.Ledger.ReadTailAsync(token)).Tail);
    }

    /// <summary>
    /// LP-I5: a recovery which meets a dropped replica answers <see cref="ReplicaWriteOutcome.Dropped"/>, since the ledger has
    /// left the map and no close can apply, even when the other replicas could decide it.
    /// </summary>
    [Fact]
    public async Task ARecoveryWhichMeetsADroppedReplicaReportsTheDrop()
    {
        var token = TestContext.Current.CancellationToken;
        await using var nodes = new DroppingNodes();
        using var payload = Payload("value");
        Assert.Equal(ReplicaWriteCase.Written, (await nodes.Ledger.WriteAsync(0, payload, token)).Case);
        await nodes.DropAsync(2);

        DroppedAnswers.AssertDropped(await nodes.Ledger.RecoverAsync(token));
    }

    private static ArcBuffer Payload(string value) => LedgerReplicaTestData.Buffer(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// A <see cref="Ledger"/> over one ledger's replicas on three memory nodes, each of which drops the ledger on demand.
    /// </summary>
    private sealed class DroppingNodes : IAsyncDisposable
    {
        private readonly InMemoryLedgerReplicaTransport _nodes = new();
        private readonly NodeId[] _nodeIds = [.. Enumerable.Range(0, 3).Select(static _ => NodeId.New())];

        public DroppingNodes() =>
            Ledger = LedgerTestData.Create([.. _nodeIds.Select(node => _nodes.Open(node, LedgerReplicaTestData.Ledger, Options))], quorum: 2, Options);

        public Ledger Ledger { get; }

        /// <summary>
        /// Drops the ledger on replica <paramref name="replica"/>'s node, which leaves its tombstone.
        /// </summary>
        public Task DropAsync(int replica) =>
            _nodes.Node(_nodeIds[replica]).DropReplicaAsync(LedgerReplicaTestData.Ledger, TestContext.Current.CancellationToken).AsTask();

        public async ValueTask DisposeAsync()
        {
            await Ledger.DisposeAsync();
            _nodes.Dispose();
        }
    }
}
