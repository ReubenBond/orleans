using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Verifies batched per-node <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> end-to-end across in-process local
/// nodes and remote <see cref="ILedgerStoreGrain"/> activations in <c>Orleans.TestingHost</c> (orleans.md §5.12,
/// OR-I9, OR-I15, LR-I10): live replicas renew their lease in RAM with zero WAL writes, sealed/dropped/absent ledgers
/// report their exact statuses without creating replicas, and empty batches return cleanly.
/// </summary>
[Collection(TestingHostCollection.Name)]
public sealed class OrleansReplicaHeartbeatTests : IAsyncLifetime
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly List<ILedgerReplica> _replicas = [];
    private ReplicaCluster? _cluster;

    private ReplicaCluster Cluster => _cluster!;

    private string Caller => Cluster.SiloNames[0];

    private string Callee => Cluster.SiloNames[1];

    public async ValueTask InitializeAsync()
    {
        _cluster = await ReplicaCluster.DeployAsync(2);
        _cluster.CalleeAccounting = _leaks;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var replica in _replicas)
        {
            await replica.DisposeAsync();
        }

        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }

        _leaks.Dispose();
    }

    /// <summary>
    /// OR-I9, OR-I15, LR-I10: a single batched <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> call to both the
    /// local node (in-process, zero grain calls) and a remote node (through <see cref="ILedgerStoreGrain"/>) returns
    /// exact per-ledger <c>Granted</c>/<c>Sealed</c>/<c>Dropped</c>/<c>Absent</c> outcomes without creating absent
    /// replicas, and updates the replica's remaining lease reported by a subsequent seal.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task BatchedHeartbeatAnswersGrantedSealedDroppedAndAbsentLocallyAndRemotely()
    {
        var token = TestContext.Current.CancellationToken;
        var liveA = new LedgerId(101);
        var liveB = new LedgerId(102);
        var sealedLedger = new LedgerId(103);
        var droppedLedger = new LedgerId(104);
        var absentLedger = new LedgerId(105);

        foreach (var silo in new[] { Caller, Callee })
        {
            var node = Cluster.Node(silo);
            var transport = Cluster.Transport(Caller);

            var rA = Open(silo, liveA);
            var rB = Open(silo, liveB);
            var rS = Open(silo, sealedLedger);
            var rD = Open(silo, droppedLedger);

            await WriteAsync(rA, 0, [1], token);
            await WriteAsync(rB, 0, [2], token);
            await WriteAsync(rS, 0, [3], token);
            await WriteAsync(rD, 0, [4], token);
            _ = await rS.ReadTailAsync(new Slot(1), seal: true, token);
            await node.Store.DropReplicaAsync(droppedLedger, token);

            // Empty batch is rejected before any RPC is issued.
            _ = await Assert.ThrowsAsync<ArgumentException>(
                () => transport.HeartbeatAsync(node.Node, ReadOnlyMemory<LedgerId>.Empty, token).AsTask());

            var grainCallsBefore = Cluster.CalleeCalls(silo);
            var batch = await transport.HeartbeatAsync(
                node.Node,
                new[] { liveA, liveB, sealedLedger, droppedLedger, absentLedger },
                token);

            Assert.True(batch.TryGetValue(out ReplicaHeartbeatReply reply));
            Assert.Equal(LedgerLeaseOptions.DefaultLeaseDuration, reply.LeaseGrant);
            Assert.Equal(
                [
                    ReplicaHeartbeatStatus.Granted,
                    ReplicaHeartbeatStatus.Granted,
                    ReplicaHeartbeatStatus.Sealed,
                    ReplicaHeartbeatStatus.Dropped,
                    ReplicaHeartbeatStatus.Absent,
                ],
                reply.Outcomes.ToArray());

            // Absent ledger was NOT created by the heartbeat.
            var present = await node.Store.ListReplicasAsync(token);
            Assert.DoesNotContain(absentLedger, present);

            // Local silo never calls grain; remote silo uses exactly one grain RPC for the whole 5-ledger batch.
            if (silo == Caller)
            {
                Assert.Equal(0, Cluster.CalleeCalls(Caller));
            }
            else
            {
                Assert.Equal(grainCallsBefore + 1, Cluster.CalleeCalls(Callee));
            }

            // Sealing liveA immediately reports a positive LeaseRemaining bounded by LeaseDuration.
            var sealedTail = Assert.IsType<LedgerReplicaTail>((await rA.ReadTailAsync(new Slot(1), seal: true, token)).Value);
            Assert.True(sealedTail.IsSealed);
            Assert.True(sealedTail.LeaseRemaining > TimeSpan.Zero && sealedTail.LeaseRemaining <= LedgerLeaseOptions.DefaultLeaseDuration);
        }
    }

    private ILedgerReplica Open(string silo, LedgerId ledger)
    {
        var replica = Cluster.Transport(Caller).Open(Cluster.Node(silo).Node, ledger, Window);
        _replicas.Add(replica);
        return replica;
    }

    private static async Task WriteAsync(ILedgerReplica replica, long slot, byte[] payload, CancellationToken token)
    {
        using var buffer = ArcBuffer.Wrap(payload);
        var outcome = await replica.WriteAsync(new Slot(slot), buffer, new Slot(slot), recovery: false, token);
        _ = Assert.IsType<ReplicaWriteOutcome.Written>(outcome.Value);
    }
}
