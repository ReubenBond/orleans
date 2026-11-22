namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The per-ledger outcomes of a batched <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> call, in request order,
/// paired with the node's granted RAM-only lease duration (ledger-replicas.md §2.2, §4, LR-I10).
/// </summary>
/// <param name="LeaseGrant">
/// The lease duration granted for every ledger whose outcome is <see cref="ReplicaHeartbeatStatus.Granted"/>, or
/// <see cref="TimeSpan.Zero"/> when leases are disabled on the node.
/// </param>
/// <param name="Outcomes">
/// One <see cref="ReplicaHeartbeatStatus"/> per requested ledger, in the exact order of the request.
/// </param>
public readonly record struct ReplicaHeartbeatReply(TimeSpan LeaseGrant, ReadOnlyMemory<ReplicaHeartbeatStatus> Outcomes);
