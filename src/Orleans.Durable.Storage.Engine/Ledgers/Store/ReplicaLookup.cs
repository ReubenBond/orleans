namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// What a store holds of a ledger for a read (ledgerstore.md §8.5, LS-I1, LS-I2): its live replica, the ledger's
/// tombstone (<see cref="ReplicaWriteOutcome.Dropped"/>), or <see cref="Absent"/>.
/// </summary>
internal readonly union ReplicaLookup(LiveReplica, ReplicaWriteOutcome.Dropped, ReplicaLookup.Absent)
{
    /// <summary>
    /// The catalog neither lists the ledger's replica nor holds its tombstone, so a read answers as an empty, unsealed
    /// replica.
    /// </summary>
    internal sealed record Absent
    {
        private Absent()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Absent Instance { get; } = new();
    }
}
