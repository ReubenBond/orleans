namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A <see cref="Ledger"/>'s writer write's answer (ledger-protocol.md §2.7, conventions.md §15):
/// <see cref="ReplicaWriteOutcome.Written"/> once the storage decided the batch (LL-I2; the quorum's at <c>Q</c> written
/// answers, LP-I4); <see cref="ReplicaWriteOutcome.Sealed"/> once the write met the ledger's seal or drop (LL-I4); or
/// <see cref="ReplicaWriteOutcome.Backpressure"/> when the storage refused it for now and it could not decide, which
/// asserts nothing (ledger-protocol.md §6.4).
/// </summary>
/// <remarks>
/// A writer write answers a drop as a seal, so it never answers <c>Dropped</c>; every case is a shared
/// instance of <see cref="ReplicaWriteOutcome"/>'s.
/// </remarks>
internal readonly union LedgerWriteOutcome(
    ReplicaWriteOutcome.Written,
    ReplicaWriteOutcome.Sealed,
    ReplicaWriteOutcome.Backpressure) : IEquatable<LedgerWriteOutcome>
{
    /// <inheritdoc/>
    public bool Equals(LedgerWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
