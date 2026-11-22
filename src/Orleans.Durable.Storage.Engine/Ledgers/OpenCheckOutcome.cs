namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The primary's open check (ledgers.md §2.4, §8.3): <see cref="Open"/> with its contiguous tail, <see cref="Sealed"/>,
/// or the replica contract's <see cref="ReplicaWriteOutcome.Backpressure"/> when backpressure kept the seal check from
/// deciding, which asserts nothing (outcomes.md §8).
/// </summary>
public readonly union OpenCheckOutcome(
    OpenCheckOutcome.Open,
    OpenCheckOutcome.Sealed,
    ReplicaWriteOutcome.Backpressure) : IEquatable<OpenCheckOutcome>
{
    /// <summary>
    /// A seal check of <c>Q</c> replicas begun after the call found none sealed or dropped (LP-I9): no recovery of the
    /// ledger completed before the call.
    /// </summary>
    /// <param name="ContiguousTail">
    /// The primary's contiguous tail after the check, which covers every proposal it reported
    /// <see cref="ProposalOutcome.Durable"/> before the call.
    /// </param>
    public sealed record Open(Slot ContiguousTail);

    /// <summary>
    /// A replica answered sealed or dropped: the writer's lifetime is stopped, and the primary proposes nothing more.
    /// </summary>
    public sealed record Sealed
    {
        private Sealed()
        {
        }

        /// <summary>
        /// Gets the one sealed outcome.
        /// </summary>
        public static Sealed Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(OpenCheckOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is OpenCheckOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
