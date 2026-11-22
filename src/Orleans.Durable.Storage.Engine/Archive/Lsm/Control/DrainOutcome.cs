using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// A drain's answer (archive §2.3, conventions.md §15): <see cref="Drained"/>, the change and the committer's answer;
/// <see cref="Covered"/>, nothing to drain; <see cref="OpenSegment"/>, a segment not yet closed; or the ledger scan's
/// <see cref="ReplicaWriteOutcome.Dropped"/> or <see cref="ReplicaWriteOutcome.Backpressure"/>.
/// </summary>
public readonly union DrainOutcome(
    DrainOutcome.Drained,
    DrainOutcome.Covered,
    DrainOutcome.OpenSegment,
    ReplicaWriteOutcome.Dropped,
    ReplicaWriteOutcome.Backpressure) : IEquatable<DrainOutcome>
{
    /// <inheritdoc/>
    public bool Equals(DrainOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DrainOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The drain uploaded the rows of one whole closed segment, if it held any, and the manifest committer answered its
    /// change: after <see cref="ManifestCommitOutcome.Applied"/>, the LSM holds the ledger's rows below the change's
    /// <c>Below</c>, the next segment's start or the ledger's <c>T</c>, and the caller drains again while that lies below
    /// its bound.
    /// </summary>
    /// <param name="Change">The drain's change, from the segment's start, the watermark, to its end.</param>
    /// <param name="Outcome">The committer's answer.</param>
    public sealed record Drained(ManifestChange.Drain Change, ManifestCommitOutcome Outcome);

    /// <summary>
    /// The LSM already holds the ledger's rows below the drain's bound: a manifest value, or the worker's own applied
    /// drain, shows the ledger complete, or the watermark the drain would start from is at or past the bound and the
    /// drain would complete nothing. Nothing is uploaded or submitted.
    /// </summary>
    public sealed record Covered
    {
        private Covered()
        {
        }

        /// <summary>Gets the one covered answer.</summary>
        public static Covered Instance { get; } = new();
    }

    /// <summary>
    /// The segment which starts at the drain's start does not close below the drain's bound: no decided batch below the
    /// bound starts a later segment, and the bound is not the closed ledger's <c>T</c>. Nothing is uploaded or submitted;
    /// the caller drains again once a later segment starts below a tail it learns.
    /// </summary>
    /// <param name="From">The open segment's start, the ledger's watermark as the worker knows it.</param>
    public sealed record OpenSegment(Slot From);
}
