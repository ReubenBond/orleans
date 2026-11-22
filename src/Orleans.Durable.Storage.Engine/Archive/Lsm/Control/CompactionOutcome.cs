namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// A compaction's answer (archive §2.4, conventions.md §15): <see cref="Compacted"/>, the change to submit; or
/// <see cref="NothingToCompact"/>.
/// </summary>
public readonly union CompactionOutcome(
    CompactionOutcome.Compacted,
    CompactionOutcome.NothingToCompact) : IEquatable<CompactionOutcome>
{
    /// <inheritdoc/>
    public bool Equals(CompactionOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CompactionOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The worker uploaded its outputs, and the caller submits the change to the manifest committer, which applies it
    /// only while every input is listed (LSM-I5).
    /// </summary>
    /// <param name="Change">The change, which replaces the inputs with the outputs.</param>
    public sealed record Compacted(ManifestChange.Compaction Change);

    /// <summary>
    /// The manifest value holds no compaction of the partition: it holds at most <c>k</c> of its sections, or, at the
    /// deepest grid depth, where a partition never pushes, fewer than two of them smaller than <c>s</c>. Nothing is read
    /// or uploaded.
    /// </summary>
    public sealed record NothingToCompact
    {
        private NothingToCompact()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static NothingToCompact Instance { get; } = new();
    }
}
