namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A listed ledger's status in the map (ledgers.md §4): <see cref="Open"/>, or <see cref="Closed"/> at its recovered
/// tail.
/// </summary>
public readonly union LedgerStatus(
    LedgerStatus.Open,
    LedgerStatus.Closed) : IEquatable<LedgerStatus>
{
    /// <summary>
    /// The ledger is open: its primary may still append.
    /// </summary>
    public sealed record Open
    {
        private Open()
        {
        }

        /// <summary>
        /// Gets the one open status.
        /// </summary>
        public static Open Instance { get; } = new();
    }

    /// <summary>
    /// The ledger is closed at <paramref name="T"/>, a candidate end of a recovery of the ledger which completed its seal
    /// coverage, which the map write closing it recorded (LG-I9): its decided rows are exactly its batches below
    /// <paramref name="T"/> (LG-I4).
    /// </summary>
    /// <param name="T">The recorded tail.</param>
    public sealed record Closed(Slot T);

    /// <inheritdoc/>
    public bool Equals(LedgerStatus other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerStatus other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
