using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's answer to a compaction assignment (orleans.md §5.6, §7.2, conventions.md §15): the commit's verdict on the
/// compaction it submitted; nothing to compact; or <see cref="Corrupt"/>, the partition's own corruption, which the
/// controller decides on. A failure of the call, the
/// manifest's refresh, or the commit is an unknown outcome, which <c>CompactionTimeout</c> ends.
/// </summary>
internal readonly union CompactionReply(
    CompactionReply.Compacted,
    CompactionReply.NothingToCompact,
    CompactionReply.Corrupt) : IEquatable<CompactionReply>
{
    /// <inheritdoc/>
    public bool Equals(CompactionReply other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CompactionReply other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>The node compacted the partition and submitted the change, which the manifest's committer decided.</summary>
    /// <param name="Outcome">The committer's verdict.</param>
    public sealed record Compacted(ManifestCommitOutcome Outcome);

    /// <summary>The manifest value the node reached holds no compaction of the partition.</summary>
    public sealed record NothingToCompact
    {
        private NothingToCompact()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static NothingToCompact Instance { get; } = new();
    }

    /// <summary>
    /// A section the compaction read was corrupt, on a read and again on a read past the caches (archive §8.2), or an
    /// input's object was gone while a manifest read afresh still listed it (archive §2.4): durable corruption, outside the
    /// fault model, which the controller holds the partition for in its activation.
    /// </summary>
    /// <param name="Partition">The partition.</param>
    /// <param name="Reason">What the compaction found corrupt, naming the object.</param>
    public sealed record Corrupt(KeyRange Partition, string Reason);
}
