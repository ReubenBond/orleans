using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The nodes a <see cref="LedgerSystem"/>'s ledger has its replicas on: each opens a handle over its replica of a ledger,
/// and drops the ledger as its node's reconciliation does once the map revoked it (ledger-replicas.md §2.3, LR-I3).
/// </summary>
internal interface ILedgerSystemNodes : IAsyncDisposable
{
    /// <summary>Opens a handle over <paramref name="node"/>'s replica of <paramref name="ledger"/>.</summary>
    ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options);

    /// <summary>Drops <paramref name="ledger"/> on <paramref name="node"/>, leaving its tombstone (LR-I7).</summary>
    ValueTask DropAsync(NodeId node, LedgerId ledger);
}

/// <summary>
/// Memory nodes, whose replicas in a simulation yield to the scheduler as each request lands and before each answer
/// returns (<c>memory-replica-yields</c>, verification.md §7.2).
/// </summary>
internal sealed class InMemoryLedgerSystemNodes : ILedgerSystemNodes
{
#if DURABLE_STORAGE_SIMULATION_TESTS
    private readonly InMemoryLedgerReplicaTransport _transport = new() { YieldsAtLandingAndAnswer = true };
#else
    private readonly InMemoryLedgerReplicaTransport _transport = new();
#endif

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => _transport.Open(node, ledger, options);

    public ValueTask DropAsync(NodeId node, LedgerId ledger) => _transport.Node(node).DropReplicaAsync(ledger);

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
