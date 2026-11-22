using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The writer's dictionary of its open segment (ledgers.md §8.2): each key the segment defined, with its ordinal and its
/// current version. It decides whether a row defines or references its key, and is released at each segment end.
/// </summary>
/// <remarks>
/// It is the one authority on whether a key is defined in its segment (LG-I12) and on each row's version (LG-I13). Only
/// the batch being written changes it, one row at a time, so it takes no lock: its writer creates batches one at a time,
/// and each waits until the one before it is proposed. An entry holds the version's base only when it is not empty, so a
/// key costs its bytes, one entry, and its share of the table (ledgers.md §8.2).
/// </remarks>
internal sealed class LedgerSegmentDictionary
{
    private readonly Dictionary<byte[], Entry> _keys;
    private readonly Dictionary<byte[], Entry>.AlternateLookup<ReadOnlySpan<byte>> _lookup;

    /// <summary>Initializes a new instance of the <see cref="LedgerSegmentDictionary"/> class, empty.</summary>
    public LedgerSegmentDictionary()
    {
        _keys = [with(KeyComparer.Instance)];
        _lookup = _keys.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    /// <summary>Gets the number of keys the segment defined, which is the next key's ordinal.</summary>
    public int Count => _keys.Count;

    /// <summary>
    /// Returns a reference to <paramref name="key"/>'s entry when the segment defined it, through which the batch advances
    /// its count, or a null reference.
    /// </summary>
    public ref Entry Find(ReadOnlySpan<byte> key) => ref CollectionsMarshal.GetValueRefOrNullRef(_lookup, key);

    /// <summary>Returns whether the segment defined <paramref name="key"/>.</summary>
    public bool Contains(ReadOnlySpan<byte> key) => _lookup.ContainsKey(key);

    /// <summary>Returns the version <paramref name="key"/> holds in the segment, or the default when it defined none.</summary>
    public RowVersion VersionOf(ReadOnlySpan<byte> key) => _lookup.TryGetValue(key, out var entry) ? entry.Version : default;

    /// <summary>
    /// Defines <paramref name="key"/>, which the segment has not, at <paramref name="ordinal"/>, the keys its segment
    /// defined before it, with <paramref name="version"/>.
    /// </summary>
    public void Define(ReadOnlySpan<byte> key, ulong ordinal, RowVersion version)
    {
        Debug.Assert(!_lookup.ContainsKey(key), "A segment defines each key at most once (LG-I12).");
        _keys.Add(key.ToArray(), new Entry(ordinal, version.Count, version.Base.IsEmpty ? null : version.Base.ToArray()));
    }

    /// <summary>
    /// Releases the keys at a segment's end, so the next segment starts empty, keeping the table's capacity for a busy
    /// ledger's next segment or returning it for a quiet one's.
    /// </summary>
    public void Release(bool keepCapacity)
    {
        _keys.Clear();
        if (!keepCapacity)
        {
            _keys.TrimExcess();
        }
    }

    /// <summary>A key's ordinal in its segment, and its version after its last row there: its count and its base.</summary>
    internal struct Entry(ulong ordinal, ulong count, byte[]? versionBase)
    {
        /// <summary>Gets the key's ordinal in its segment.</summary>
        public readonly ulong Ordinal => ordinal;

        /// <summary>Gets or sets the count of the key's version after its last row in the segment.</summary>
        public ulong Count { readonly get; set; } = count;

        /// <summary>Gets the key's version after its last row in the segment.</summary>
        public readonly RowVersion Version => new(versionBase is null ? default : new RowToken(versionBase), Count);
    }

    /// <summary>Compares keys by their bytes, as arrays and as spans.</summary>
    internal sealed class KeyComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var hash = default(HashCode);
            hash.AddBytes(alternate);
            return hash.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }
}
