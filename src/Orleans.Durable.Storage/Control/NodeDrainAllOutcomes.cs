namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The node registry committer's answer to starting drain-all (orleans.md §5.3, §5.14, OR-I19):
/// <see cref="Started"/> once <c>DrainAllRevision</c> is durable, or <see cref="ObjectLedgersNotConfigured"/> when the
/// registry records no object-ledger store.
/// </summary>
internal readonly union NodeDrainAllStartOutcome(
    NodeDrainAllStartOutcome.Started,
    NodeDrainAllStartOutcome.ObjectLedgersNotConfigured) : IEquatable<NodeDrainAllStartOutcome>
{
    /// <inheritdoc/>
    public bool Equals(NodeDrainAllStartOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeDrainAllStartOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>Drain-all is active in the registry at <paramref name="Revision"/>.</summary>
    /// <param name="Revision">The durable <c>DrainAllRevision</c> recorded in the registry.</param>
    public sealed record Started(ulong Revision);

    /// <summary>The registry records no object-ledger store, so drain-all cannot start.</summary>
    public sealed record ObjectLedgersNotConfigured
    {
        private ObjectLedgersNotConfigured()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static ObjectLedgersNotConfigured Instance { get; } = new();
    }
}

/// <summary>
/// The node registry committer's answer to cancelling drain-all (orleans.md §5.3, §5.14, OR-I19):
/// <see cref="Cancelled"/> when the write cleared <c>DrainAllRevision</c>, or <see cref="NotActive"/> when no drain-all
/// was active.
/// </summary>
internal readonly union NodeDrainAllCancelOutcome(
    NodeDrainAllCancelOutcome.Cancelled,
    NodeDrainAllCancelOutcome.NotActive) : IEquatable<NodeDrainAllCancelOutcome>
{
    /// <inheritdoc/>
    public bool Equals(NodeDrainAllCancelOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeDrainAllCancelOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>Drain-all was cleared at <paramref name="Revision"/>.</summary>
    /// <param name="Revision">The revision of the write that cleared <c>DrainAllRevision</c>.</param>
    public sealed record Cancelled(ulong Revision);

    /// <summary>No drain-all was active in the registry.</summary>
    public sealed record NotActive
    {
        private NotActive()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static NotActive Instance { get; } = new();
    }
}

/// <summary>
/// The node registry committer's answer to atomically completing drain-all Phase 2 (orleans.md §5.3, §5.14, OR-I19):
/// <see cref="Completed"/> when the write retired all active diskful nodes and cleared <c>DrainAllRevision</c>, or
/// <see cref="Superseded"/> when <c>DrainAllRevision</c> no longer matched the expected episode.
/// </summary>
internal readonly union NodeDrainAllCompleteOutcome(
    NodeDrainAllCompleteOutcome.Completed,
    NodeDrainAllCompleteOutcome.Superseded) : IEquatable<NodeDrainAllCompleteOutcome>
{
    /// <inheritdoc/>
    public bool Equals(NodeDrainAllCompleteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeDrainAllCompleteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>All active diskful nodes were retired and <c>DrainAllRevision</c> was cleared at <paramref name="Revision"/>.</summary>
    /// <param name="Revision">The revision of the write that completed drain-all.</param>
    public sealed record Completed(ulong Revision);

    /// <summary>The registry's <c>DrainAllRevision</c> no longer matched the expected episode; no node was retired.</summary>
    public sealed record Superseded
    {
        private Superseded()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Superseded Instance { get; } = new();
    }
}
