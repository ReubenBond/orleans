namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The outcome of a cooperative <see cref="LedgerWriter.PrepareReleaseAsync"/> on an open ledger's primary
/// (orleans.md §5.6, OR-I24).
/// </summary>
public readonly union LedgerPrepareReleaseOutcome(
    LedgerPrepareReleaseOutcome.Released,
    LedgerPrepareReleaseOutcome.Sealed,
    LedgerPrepareReleaseOutcome.TimedOut) : IEquatable<LedgerPrepareReleaseOutcome>
{
    /// <inheritdoc/>
    public bool Equals(LedgerPrepareReleaseOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LedgerPrepareReleaseOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// Every in-flight proposal drained cleanly, replicas confirmed the ledger unsealed, and the writer stopped
    /// admitting new batches at <see cref="ContiguousTail"/>.
    /// </summary>
    /// <param name="ContiguousTail">The primary's contiguous tail when admission stopped.</param>
    public sealed record Released(Slot ContiguousTail);

    /// <summary>
    /// Another node concurrently sealed the ledger, or the ledger closed or its writer stopped before release finished;
    /// draining aborted immediately.
    /// </summary>
    public sealed record Sealed
    {
        /// <summary>Gets the singleton instance.</summary>
        public static Sealed Instance { get; } = new();

        private Sealed()
        {
        }
    }

    /// <summary>In-flight proposals or replica confirmation did not complete within the release deadline.</summary>
    public sealed record TimedOut
    {
        /// <summary>Gets the singleton instance.</summary>
        public static TimedOut Instance { get; } = new();

        private TimedOut()
        {
        }
    }
}
