namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The outcome of <see cref="Ledger.RecoverAsync(CancellationToken)"/> (ledger-protocol.md §8.3, conventions.md §15):
/// <see cref="Recovered"/> with the candidate end, or <see cref="ReplicaWriteOutcome.Dropped"/> once a replica answered
/// that its node dropped the ledger (LP-I5). A recovery repeats a <c>Backpressure</c> answer rather than giving one.
/// </summary>
internal readonly union RecoveryOutcome(RecoveryOutcome.Recovered, ReplicaWriteOutcome.Dropped) : IEquatable<RecoveryOutcome>
{
    /// <summary>
    /// The recovery ended at the candidate end <paramref name="End"/>: every slot below it holds a batch on <c>Q</c>
    /// replicas, and no writer write at it ever reaches <c>Q</c> written answers (LP-I7). Only the ledger map's
    /// conditional write makes it the ledger's <c>T</c>.
    /// </summary>
    /// <param name="End">The candidate end.</param>
    /// <param name="LeaseRemaining">
    /// The maximum remaining replica lease duration across the sealing quorum (LP-I18), or <see cref="TimeSpan.Zero"/>
    /// when leases are disabled or expired.
    /// </param>
    public sealed record Recovered(Slot End, TimeSpan LeaseRemaining = default);

    /// <inheritdoc/>
    public bool Equals(RecoveryOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RecoveryOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
