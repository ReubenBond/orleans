using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// One entry of a node's per-ledger key index (journals.md §8.3, archive.md §8.1): the row's tag, the slot of the batch
/// that defined the row's key in its segment, and the key's ordinal in that segment so a point read resolves the defining
/// batch by ordinal without scanning the ledger.
/// </summary>
/// <param name="Tag">The row's tag: its ledger, batch slot, and index in the batch.</param>
/// <param name="DefiningSlot">The slot of the batch that defined the key in the row's segment.</param>
/// <param name="Ordinal">The key's ordinal in the row's segment.</param>
public readonly record struct KeyIndexEntry(Tag Tag, Slot DefiningSlot, ulong Ordinal);

/// <summary>
/// The outcome of reading a node's per-ledger key index for a key below a decided slot (archive.md §8.1, journals.md §8.3,
/// JS-I9): <see cref="Indexed"/> once the index has caught up to at least the requested slot, <see cref="Unindexed"/> when
/// the node maintains no index for the ledger and the reader falls back to scanning, or <see cref="Dropped"/> when the
/// ledger left the map while waiting or scanning.
/// </summary>
public readonly union KeyIndexRead(KeyIndexRead.Indexed, KeyIndexRead.Unindexed, KeyIndexRead.Dropped)
{
    /// <summary>The caught-up index entries for the key below the requested slot, in ascending tag order.</summary>
    public sealed record Indexed
    {
        /// <summary>Initializes a new instance of the <see cref="Indexed"/> class.</summary>
        /// <param name="entries">The key's index entries below the requested slot.</param>
        public Indexed(ImmutableArray<KeyIndexEntry> entries) => Entries = entries.IsDefault ? [] : entries;

        /// <summary>Gets the key's index entries below the requested slot.</summary>
        public ImmutableArray<KeyIndexEntry> Entries { get; }

        /// <inheritdoc/>
        public bool Equals(Indexed? other) =>
            other is not null && Entries.AsSpan().SequenceEqual(other.Entries.AsSpan());

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (var entry in Entries)
            {
                hash.Add(entry);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>The node keeps no index for this ledger (such as an open ledger it does not replicate); scan it instead.</summary>
    public sealed record Unindexed
    {
        private Unindexed()
        {
        }

        /// <summary>Gets the singleton unindexed outcome.</summary>
        public static Unindexed Instance { get; } = new();
    }

    /// <summary>The ledger was dropped while waiting for catch-up or building its predecessor index.</summary>
    public sealed record Dropped
    {
        private Dropped()
        {
        }

        /// <summary>Gets the singleton dropped outcome.</summary>
        public static Dropped Instance { get; } = new();
    }
}

/// <summary>
/// A node's per-ledger key-index hook for <see cref="ArchiveReader"/> (archive.md §8.1, journals.md §8.3, JS-I9), wrapped
/// as a concrete callback so Archive stays free of Journals dependencies.
/// </summary>
public sealed class ArchiveKeyIndex
{
    private readonly Func<LedgerMetadata, Slot, ReadOnlyMemory<byte>, CancellationToken, ValueTask<KeyIndexRead>> _read;

    /// <summary>Initializes a new instance of the <see cref="ArchiveKeyIndex"/> class.</summary>
    /// <param name="read">
    /// Waits for <c>ledger</c>'s index to catch up to at least <c>below</c> (JS-I9) and returns the key's entries below
    /// <c>below</c>, <see cref="KeyIndexRead.Unindexed"/>, or <see cref="KeyIndexRead.Dropped"/>.
    /// </param>
    public ArchiveKeyIndex(Func<LedgerMetadata, Slot, ReadOnlyMemory<byte>, CancellationToken, ValueTask<KeyIndexRead>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>
    /// Returns the caught-up index entries for <paramref name="key"/> in <paramref name="ledger"/> below
    /// <paramref name="below"/> (JS-I9).
    /// </summary>
    public ValueTask<KeyIndexRead> ReadAsync(LedgerMetadata ledger, Slot below, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        _read(ledger, below, key, cancellationToken);
}
