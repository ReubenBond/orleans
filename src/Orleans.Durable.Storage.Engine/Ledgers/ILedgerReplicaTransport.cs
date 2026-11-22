namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Reaches the ledger replicas on a cluster's nodes, each addressed by <c>(node, ledger)</c> (ledger-replicas.md §2.2).
/// </summary>
/// <remarks>
/// Ledgers opens a ledger's replicas through it and builds the ledger's quorum over them. A node is named by its
/// durable identity, the node identity of the LedgerStore which holds its replicas, and a ledger by its ledger ID. A
/// node creates a replica with the first write, recovery write, or seal which reaches it unless it dropped the ledger, and a
/// dropped ledger keeps its tombstone for the node's life (LR-I3, LR-I7). Every implementation answers only from the
/// store of the node a call names (LR-I9). A node's own listing and drops go through its <see cref="ILedgerStore"/>.
/// </remarks>
public interface ILedgerReplicaTransport
{
    /// <summary>
    /// Opens a handle over the replica of <paramref name="ledger"/> on <paramref name="node"/>, without I/O.
    /// </summary>
    /// <param name="node">The node identity.</param>
    /// <param name="ledger">The ledger ID.</param>
    /// <param name="options">The replica's runtime options, with the ledger's window.</param>
    /// <returns>A replica the caller owns and disposes.</returns>
    /// <remarks>
    /// Opening creates nothing, so opening a quorum never waits on an unavailable minority. The replica's operations answer
    /// as the node does: a replica the node does not hold is created by a write, recovery write, or seal unless the node dropped
    /// the ledger, and reads as empty until then (LR-I7); a dropped ledger answers every operation <c>Dropped</c> (LR-I3);
    /// and an unreachable node fails as unavailable.
    /// </remarks>
    ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options);

    /// <summary>
    /// Sends one batched lease-renewal heartbeat for <paramref name="ledgers"/> to <paramref name="node"/>
    /// (ledger-replicas.md §2.2, LR-I10).
    /// </summary>
    /// <param name="node">The target replica node identity.</param>
    /// <param name="ledgers">The ledger IDs whose leases to renew on <paramref name="node"/>.</param>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <returns>
    /// A <see cref="ReplicaHeartbeatRead"/> holding per-ledger outcomes in request order with the node's granted lease
    /// duration, or <see cref="ReplicaWriteOutcome.Backpressure"/>. The node never creates replicas and writes nothing
    /// to its WAL.
    /// </returns>
    ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default);
}
