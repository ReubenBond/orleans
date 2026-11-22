namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Per-ledger outcome reported by <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> (ledger-replicas.md §2.2, §4,
/// LR-I10).
/// </summary>
public enum ReplicaHeartbeatStatus : byte
{
    /// <summary>
    /// The node holds a live, unsealed replica of the ledger and extended its RAM-only lease until at least
    /// <c>now + LeaseGrant</c>.
    /// </summary>
    Granted = 0,

    /// <summary>
    /// The node holds a durably sealed replica of the ledger; no lease was granted (LR-I8, LR-I10).
    /// </summary>
    Sealed = 1,

    /// <summary>
    /// The node dropped the ledger (tombstone present); counts as sealed and no lease was granted (LR-I3, LP-I5).
    /// </summary>
    Dropped = 2,

    /// <summary>
    /// The node holds neither a live replica nor a tombstone for the ledger; nothing was created (LR-I7, LR-I10).
    /// </summary>
    Absent = 3,
}
