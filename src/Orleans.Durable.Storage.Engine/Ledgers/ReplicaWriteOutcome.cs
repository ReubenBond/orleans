namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A replica's answer to <see cref="ILedgerReplica.WriteAsync"/> (ledger-replicas.md §2.1, conventions.md §15):
/// <see cref="Written"/>, <see cref="Sealed"/>, <see cref="Dropped"/>, or <see cref="Backpressure"/>.
/// </summary>
/// <remarks>
/// Every case except a non-zero lease grant is a shared instance, and a store caches its configured
/// <see cref="Written"/> value, so an answer never allocates on the hot path. A read's answer (<see cref="ReplicaRead"/>)
/// and a contiguous-tail read's (<see cref="ReplicaTailRead"/>) share <see cref="Dropped"/> and
/// <see cref="Backpressure"/>, as do the ledger protocol's answers.
/// </remarks>
public readonly union ReplicaWriteOutcome(
    ReplicaWriteOutcome.Written,
    ReplicaWriteOutcome.Sealed,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure) : IEquatable<ReplicaWriteOutcome>
{
    /// <summary>
    /// DATA durably occupies the slot: this write's, or an identical earlier one (LR-I4), with an optional RAM-only
    /// lease grant (LR-I10).
    /// </summary>
    public sealed record Written
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Written"/> record with a RAM-only lease grant of
        /// <paramref name="leaseGrant"/>.
        /// </summary>
        /// <param name="leaseGrant">
        /// The lease duration granted by the replica for an unsealed non-recovery writer write, or
        /// <see cref="TimeSpan.Zero"/> for recovery writes or when leases are disabled.
        /// </param>
        public Written(TimeSpan leaseGrant)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(leaseGrant, TimeSpan.Zero);
            LeaseGrant = leaseGrant;
        }

        private Written()
        {
        }

        /// <summary>
        /// Gets the RAM-only lease duration granted by the replica for a non-recovery writer write, or
        /// <see cref="TimeSpan.Zero"/> for recovery writes or when leases are disabled (LR-I10).
        /// </summary>
        public TimeSpan LeaseGrant { get; }

        /// <summary>
        /// Gets the one value with no lease grant (<see cref="TimeSpan.Zero"/>).
        /// </summary>
        public static Written Instance { get; } = new();

        /// <summary>
        /// Returns <see cref="Instance"/> when <paramref name="leaseGrant"/> is zero, or a <see cref="Written"/>
        /// carrying <paramref name="leaseGrant"/>.
        /// </summary>
        public static Written FromGrant(TimeSpan leaseGrant) =>
            leaseGrant == TimeSpan.Zero ? Instance : new(leaseGrant);
    }

    /// <summary>
    /// A writer write met a durable seal and stored nothing (LR-I8). A <c>Ledger</c>'s write which meets one is unknown: the slot
    /// may hold a batch on <c>Q</c> replicas, and only the ledger's recorded tail decides it (LP-I8).
    /// </summary>
    public sealed record Sealed
    {
        private Sealed()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Sealed Instance { get; } = new();
    }

    /// <summary>
    /// The node dropped the ledger, which its tombstone records durably: it holds none of the ledger's values, never
    /// creates its replica again, and the ledger is sealed there (LR-I3, LR-I7, LP-I5). Every operation of a dropped
    /// ledger answers it.
    /// </summary>
    public sealed record Dropped
    {
        private Dropped()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Dropped Instance { get; } = new();
    }

    /// <summary>
    /// The replica was saturated, and repeating the operation may succeed: it refused the operation before retaining or
    /// writing anything, so the operation had no effect (ledger-replicas.md §2.1).
    /// </summary>
    public sealed record Backpressure
    {
        private Backpressure()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Backpressure Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ReplicaWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ReplicaWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
