using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The replica transport honors its callers' cancellation and releases what it carries: a request's copy of its payload
/// and an answer which never reaches its caller.
/// </summary>
public sealed class SimulatedLedgerReplicaTransportTests
{
    private const int Addresses = 8;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 64 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(1, 4);

    /// <summary>
    /// Every page a replica exchange rents returns by teardown, whether its request is served once, twice, or not at
    /// all, its answer is delivered or lost, or its node crashes while serving it (BUF-I1, BUF-I2).
    /// </summary>
    /// <param name="seed">The seed.</param>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ReplicaExchangesReturnEveryPageTheyRent(int seed)
    {
        var run = SeededSimulation.Run(seed, environment => ExchangeAsync(environment));

        Assert.Contains(run.Trace, static entry => entry.Contains("request duplicated", StringComparison.Ordinal));
        Assert.Contains(run.Trace, static entry => entry.Contains("crash replica-0 (", StringComparison.Ordinal));
    }

    /// <summary>
    /// A caller's cancellation of a replica's writer write, recovery write, read, or seal after its request is sent ends
    /// the caller's wait at that instant and settles the exchange, so the replica may be disposed at once, while the request
    /// still lands: the node serves it, its ledger records the writes it acknowledged, the ledger trace logs the node's
    /// answers, and the read's late answer is released (ledger-replicas §6.3).
    /// </summary>
    [Fact]
    public void CancellationAfterDispatchEndsTheWaitWhileTheRequestLands() =>
        SeededSimulation.Run(5, static async environment =>
        {
            environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(5);
            environment.Network.MaxJitter = TimeSpan.Zero;
            environment.Network.MessageDropRate = 0;
            var client = environment.Cluster.AddNode("client");
            var node = (await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 1))[0];
            var replicas = new SimulatedLedgerReplicaTransport(environment, client, [node], Deadline);
            var audit = replicas.Audit(node);
            var served = 0;
            node.OnPublished(_ => served++);

            async Task CancelAfterDispatchAsync(Func<ILedgerReplica, CancellationToken, Task> exchange)
            {
                var servedBefore = served;
                await using (var replica = (SimulatedLedgerReplica)replicas.Open(node.NodeId, SimulatedStorage.Ledger, Options))
                {
                    using var cancellation = new CancellationTokenSource();
                    var call = exchange(replica, cancellation.Token);

                    // The request is on the wire, and the node has not served it.
                    await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider);
                    Assert.Equal(servedBefore, served);
                    var cancelledAt = environment.TimeProvider.GetUtcNow();
                    Cancel(cancellation);
                    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
                    Assert.Equal(cancelledAt, environment.TimeProvider.GetUtcNow());

                    // The replica is disposed here, which the transport's check after every step reports unless the
                    // cancelled exchange has settled.
                }

                await Task.Delay(Deadline, environment.TimeProvider);
                Assert.Equal(servedBefore + 1, served);
            }

            await CancelAfterDispatchAsync(static async (replica, cancellationToken) =>
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(0));
                _ = await replica.WriteAsync(new Slot(0), payload, Slot.Zero, recovery: false, cancellationToken);
            });
            await CancelAfterDispatchAsync(static async (replica, cancellationToken) =>
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(1));
                _ = await replica.WriteAsync(new Slot(1), payload, Slot.Zero, recovery: true, cancellationToken);
            });
            Assert.Equal(
                [(SimulatedStorage.Ledger, new Slot(0)), (SimulatedStorage.Ledger, new Slot(1))],
                audit.AcknowledgedEntries());
            await CancelAfterDispatchAsync(static async (replica, cancellationToken) =>
                (await replica.ReadAsync(new Slot(0), Slot.Zero, cancellationToken)).Release());
            await CancelAfterDispatchAsync(static async (replica, cancellationToken) =>
                _ = await replica.ReadTailAsync(Slot.Zero, seal: true, cancellationToken));
            Assert.Equal([(SimulatedStorage.Ledger, true, "seal")], audit.SealAnswers.Skip(1));
            Assert.Equal(
                [LedgerOperation.WriterWrite, LedgerOperation.RecoveryWrite, LedgerOperation.Read, LedgerOperation.Seal],
                replicas.LedgerTrace.Events.Where(static entry => entry.Kind == LedgerEventKind.Served).Select(static entry => entry.Operation!.Value));
        });

    private static void Cancel(CancellationTokenSource source) => source.Cancel();

    private static async Task ExchangeAsync(SimulationEnvironment environment)
    {
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        environment.Network.MaxJitter = TimeSpan.FromMilliseconds(3);
        environment.Network.MessageDropRate = 0.1;
        environment.Rpc.DuplicationRate = 0.2;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 2);
        foreach (var node in nodes)
        {
            node.MaxDiskLatency = TimeSpan.FromMilliseconds(5);
        }

        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        await SimulationTasks.WhenAll(nodes.Select(node => ExchangeWithAsync(replicas, node)).Append(CrashAsync(environment, nodes[0])));
    }

    private static async Task ExchangeWithAsync(SimulatedLedgerReplicaTransport replicas, LedgerStoreNode node)
    {
        await using var replica = (SimulatedLedgerReplica)replicas.Open(node.NodeId, SimulatedStorage.Ledger, Options);
        for (var address = 0; address < Addresses; address++)
        {
            await AttemptAsync(async () =>
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
                _ = await replica.WriteAsync(new Slot(address), payload, Slot.Zero, recovery: false);
            });
        }

        for (var address = 0; address < Addresses; address++)
        {
            await AttemptAsync(async () => (await replica.ReadAsync(new Slot(address), Slot.Zero)).Release());
            await AttemptAsync(async () => (await replica.ReadAsync(new Slot(address), 0, 1, Slot.Zero)).Release());
        }

        await AttemptAsync(async () =>
        {
            using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(Addresses));
            _ = await replica.WriteAsync(new Slot(Addresses), payload, Slot.Zero, recovery: true);
        });
        await AttemptAsync(async () => _ = await replica.ReadTailAsync(Slot.Zero, seal: true));
    }

    private static async Task CrashAsync(SimulationEnvironment environment, LedgerStoreNode node)
    {
        await Task.Delay(environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(30)), environment.TimeProvider);
        await node.CrashAsync(operatingSystem: environment.Random.Chance(0.5));
        await Task.Delay(TimeSpan.FromMilliseconds(20), environment.TimeProvider);
        await node.StartAsync();
    }

    private static async Task AttemptAsync(Func<Task> exchange)
    {
        try
        {
            await exchange();
        }
        catch (TimeoutException)
        {
            // The exchange timed out, at its deadline or because its node crashed; what it rented is released all the same.
        }
    }
}
