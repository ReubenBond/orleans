namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One node's own replicas: its listing and its drops (ledger-replicas.md §2.3).
/// </summary>
/// <remarks>
/// A node's host holds the handle of its own node, through which Ledgers reconciles the node's replicas with the map. A
/// node creates a replica with the first write, recovery write, or seal which reaches it unless it dropped the ledger, and a
/// dropped ledger keeps its tombstone for the node's life (LR-I3, LR-I7).
/// </remarks>
public interface ILedgerStore
{
    /// <summary>
    /// Gets the node's durable identity, the node identity of the store which holds its replicas.
    /// </summary>
    NodeId NodeId { get; }

    /// <summary>
    /// Returns the ledgers whose replicas the node holds, ascending.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ledger IDs; dropped ledgers are absent.</returns>
    ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops <paramref name="ledger"/>: stops admitting operations to its replica, waits for those admitted, and in one
    /// step removes the replica, with every value, and leaves the ledger's tombstone (LR-I3).
    /// </summary>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task which completes once the drop is durable.</returns>
    /// <remarks>
    /// A ledger the node does not hold gets a tombstone too, and one the node already dropped changes nothing. Afterwards
    /// every operation on the ledger answers <c>Dropped</c>, for the node's life.
    /// </remarks>
    ValueTask DropReplicaAsync(LedgerId ledger, CancellationToken cancellationToken = default);
}
