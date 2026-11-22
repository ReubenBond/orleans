namespace Orleans.Durable.Storage.Engine.Ledgers.Memory;

/// <summary>
/// What a node holds of a ledger for a read (ledger-replicas.md §2.1, LR-I7): its replica's storage, the ledger's
/// tombstone (<see cref="ReplicaWriteOutcome.Dropped"/>), or <see cref="Absent"/>.
/// </summary>
internal readonly union StorageLookup(InMemoryReplicaStorage, ReplicaWriteOutcome.Dropped, StorageLookup.Absent)
{
    /// <summary>
    /// The node neither holds the ledger's replica nor dropped the ledger, so a read answers as an empty, unsealed replica.
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
