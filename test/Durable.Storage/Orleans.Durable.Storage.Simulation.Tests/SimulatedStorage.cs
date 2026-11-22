using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The constants of the simulated LedgerStore clusters which the tests share.
/// </summary>
internal static class SimulatedStorage
{
    /// <summary>
    /// Gets the cluster which claims every simulated node root.
    /// </summary>
    internal static Guid Cluster { get; } = Guid.ParseExact("5e7f1a2b3c4d5e6f708192a3b4c5d6e7", "N");

    /// <summary>
    /// Gets the ledger the tests replicate.
    /// </summary>
    internal static LedgerId Ledger { get; } = new(1);

    /// <summary>
    /// Returns the DATA payload the tests write at <paramref name="address"/>.
    /// </summary>
    internal static byte[] Payload(long address) => [.. BitConverter.GetBytes(address), 0xC1, 0x0C];

    /// <summary>
    /// Writes address 0 of <paramref name="ledger"/> on every node, retrying each until its node answers, so each node's
    /// first write creates the replica (LR-I7).
    /// </summary>
    internal static async Task WriteEverywhereAsync(SimulatedLedgerReplicaTransport replicas, LedgerId ledger, LedgerReplicaOptions options, CancellationToken cancellationToken = default)
    {
        foreach (var node in replicas.Nodes)
        {
            await using var replica = (SimulatedLedgerReplica)replicas.Open(node.NodeId, ledger, options);
            await RetryAsync(
                replicas,
                async () =>
                {
                    using var payload = ArcBuffer.Wrap(Payload(0));
                    _ = await replica.WriteAsync(Slot.Zero, payload, Slot.Zero, recovery: false, cancellationToken);
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// Drops <paramref name="ledger"/> on every node, as each node's reconciliation does on its own node once its view of
    /// the ledger map revokes the ledger, retrying while the node is down (ledger-replicas.md §2.3).
    /// </summary>
    internal static async Task DropEverywhereAsync(SimulatedLedgerReplicaTransport replicas, LedgerId ledger, CancellationToken cancellationToken = default)
    {
        foreach (var node in replicas.Nodes)
        {
            var handle = replicas.Node(node.NodeId);
            await RetryAsync(replicas, async () => await handle.DropReplicaAsync(ledger, cancellationToken), cancellationToken);
        }
    }

    /// <summary>
    /// Retries an idempotent request until its node answers, waiting a deadline between attempts.
    /// </summary>
    private static async Task RetryAsync(SimulatedLedgerReplicaTransport replicas, Func<Task> request, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await request();
                return;
            }
            catch (Exception exception) when (exception is TimeoutException or NodeDownException)
            {
                await Task.Delay(replicas.Deadline, SimulationEnvironment.Clock, cancellationToken);
            }
        }
    }
}
