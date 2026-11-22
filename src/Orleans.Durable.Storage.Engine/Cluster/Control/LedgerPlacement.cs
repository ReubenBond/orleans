namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Where newly opened ledgers are placed (orleans.md §5.14, §6): on nodes (the default, quorum-replicated onto
/// <c>Replicas</c> nodes) or in the cluster's object-ledger store (with no local replicas). Changing the setting rolls open
/// ledgers to the new location within the balancing budget.
/// </summary>
public enum LedgerPlacement
{
    /// <summary>Ledgers open on <c>Replicas</c> nodes.</summary>
    Nodes = 0,

    /// <summary>Ledgers open in the cluster's object-ledger store.</summary>
    ObjectStore = 1,
}
