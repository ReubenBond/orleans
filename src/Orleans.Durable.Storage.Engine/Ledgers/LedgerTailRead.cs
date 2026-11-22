namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A <see cref="Ledger"/>'s seal check's answer (ledger-protocol.md §2.1, conventions.md §15): its
/// <see cref="LedgerTail"/>, the known contiguous tail and whether a replica answered sealed or dropped (LP-I5, LP-I9), or
/// <see cref="ReplicaWriteOutcome.Backpressure"/> when a replica answered it and the check could not decide, which
/// asserts nothing (ledger-protocol.md §6.4).
/// </summary>
/// <remarks>
/// A seal check counts a dropped answer as a sealed one, so it never answers <c>Dropped</c>.
/// </remarks>
public readonly union LedgerTailRead(LedgerTail, ReplicaWriteOutcome.Backpressure) : IEquatable<LedgerTailRead>
{
    /// <inheritdoc/>
    public bool Equals(LedgerTailRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerTailRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
