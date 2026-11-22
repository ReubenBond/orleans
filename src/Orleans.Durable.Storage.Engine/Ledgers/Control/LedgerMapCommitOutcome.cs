namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// The verdict on a ledger map change (ledgers.md §2.6): <see cref="Applied"/> and <see cref="Superseded"/> carry the
/// epoch of the map value, read or written after the change's submission, which decided them.
/// </summary>
public readonly union LedgerMapCommitOutcome(
    LedgerMapCommitOutcome.Applied,
    LedgerMapCommitOutcome.Superseded,
    LedgerMapCommitOutcome.Refused,
    LedgerMapCommitOutcome.Rejected) : IEquatable<LedgerMapCommitOutcome>
{
    /// <summary>The map at <paramref name="Epoch"/> holds the change's goal.</summary>
    /// <param name="Epoch">The epoch of the deciding value.</param>
    public sealed record Applied(ulong Epoch);

    /// <summary>
    /// The change's goal is absent from the map at <paramref name="Epoch"/> and its commit rule fails against it; no
    /// attempt of it applies from then on.
    /// </summary>
    /// <param name="Epoch">The epoch of the deciding value.</param>
    public sealed record Superseded(ulong Epoch);

    /// <summary>
    /// The change would make the map exceed the register's value limit; it was not applied, and submitting it again may
    /// apply it.
    /// </summary>
    public sealed record Refused
    {
        private Refused()
        {
        }

        /// <summary>
        /// Gets the one refusal.
        /// </summary>
        public static Refused Instance { get; } = new();
    }

    /// <summary>
    /// The change is a defect: it breaks <paramref name="Reason"/>'s rule. Nothing applied, no write carries the
    /// submission, and a later submission is decided afresh; the submitter stops and reports it.
    /// </summary>
    /// <param name="Reason">The rule the change breaks.</param>
    public sealed record Rejected(LedgerMapRejection Reason);

    /// <inheritdoc/>
    public bool Equals(LedgerMapCommitOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerMapCommitOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}

/// <summary>The defect rule a rejected ledger map change breaks (ledgers.md §2.6).</summary>
public enum LedgerMapRejection
{
    /// <summary>A drop names a ledger the map lists open.</summary>
    DropNotClosed,

    /// <summary>A drop names a ledger at or above the map's <c>NextLedgerId</c>, which no install created.</summary>
    DropNeverInstalled,

    /// <summary>A drop names a ledger which a listed ledger with a lower ID overlaps (LG-I10).</summary>
    DropBlockedByLineage,

    /// <summary>An install closes a ledger at or above the map's <c>NextLedgerId</c>, which no install created.</summary>
    InstallClosesNeverInstalled,

    /// <summary>An install's result would leave the open ranges not partitioning the keyspace (LG-I1).</summary>
    OpensLeaveGap,
}
