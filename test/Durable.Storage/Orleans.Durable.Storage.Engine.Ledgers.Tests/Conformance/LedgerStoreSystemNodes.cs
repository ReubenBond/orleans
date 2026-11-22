using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Conformance;

/// <summary>
/// A <see cref="LedgerSystem"/>'s nodes as LedgerStores, one per node over its own simulated WAL storage, whose replicas
/// each process opens through its store and whose drops its store records (ledgerstore.md §2.2, §8.6).
/// </summary>
internal sealed class LedgerStoreSystemNodes : ILedgerSystemNodes
{
    private readonly Dictionary<NodeId, LedgerStore> _stores;

    private LedgerStoreSystemNodes(Dictionary<NodeId, LedgerStore> stores)
    {
        _stores = stores;
    }

    /// <summary>Creates a store which holds no replica on each of <paramref name="nodes"/>.</summary>
    internal static async Task<LedgerStoreSystemNodes> CreateAsync(IEnumerable<NodeId> nodes)
    {
        Dictionary<NodeId, LedgerStore> stores = [];
        try
        {
            foreach (var node in nodes)
            {
                stores.Add(node, await LedgerStoreReplicaTests.Simulated().OpenAsync(create: true));
            }

            return new LedgerStoreSystemNodes(stores);
        }
        catch
        {
            await new LedgerStoreSystemNodes(stores).DisposeAsync();
            throw;
        }
    }

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => _stores[node].OpenReplica(ledger, options);

    public ValueTask DropAsync(NodeId node, LedgerId ledger) => _stores[node].DropReplicaAsync(ledger);

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores.Values)
        {
            await store.DisposeAsync();
        }
    }
}
