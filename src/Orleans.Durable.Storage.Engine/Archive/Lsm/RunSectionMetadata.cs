using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A run object's tail (archive §7): its trailer and each section's metadata, which one read of the tail gives.
/// </summary>
/// <param name="Trailer">The object's trailer.</param>
/// <param name="Sections">Each section's metadata, in the trailer's order.</param>
internal sealed record RunTail(RunTrailer Trailer, ImmutableArray<RunSectionMetadata> Sections)
{
    /// <summary>
    /// Returns the metadata of the section whose blocks start at <paramref name="offset"/>, or <see langword="null"/>.
    /// </summary>
    public RunSectionMetadata? Find(long offset)
    {
        var index = Sections.AsSpan().BinarySearch(new MetadataOffset(offset));
        return index >= 0 ? Sections[index] : null;
    }

    /// <inheritdoc/>
    public bool Equals(RunTail? other) => other is not null && Trailer.Equals(other.Trailer) && Sections.SequenceEqual(other.Sections);

    /// <inheritdoc/>
    public override int GetHashCode() => Trailer.GetHashCode();

    /// <summary>Orders section metadata against an offset, for a binary search by offset.</summary>
    private readonly struct MetadataOffset(long offset) : IComparable<RunSectionMetadata>
    {
        public int CompareTo(RunSectionMetadata? other) => other is null ? 1 : offset.CompareTo(other.Entry.Offset);
    }
}

/// <summary>
/// A section's metadata (archive §7): its trailer entry, block index, and Bloom filter, which a read of its rows consults
/// before it reads a block.
/// </summary>
/// <param name="Entry">The section's entry in its object's trailer.</param>
/// <param name="Index">The section's block index.</param>
/// <param name="Filter">The section's Bloom filter over its keys.</param>
internal sealed record RunSectionMetadata(RunSectionEntry Entry, RunBlockIndex Index, RunBloomFilter Filter)
{
    /// <summary>
    /// Returns the blocks which may hold rows of <paramref name="key"/>: none when the section's partition or Bloom filter
    /// excludes the key, and otherwise those the block index names.
    /// </summary>
    public RunBlockSpan Candidates(ReadOnlySpan<byte> key) =>
        Entry.Partition.Contains(key) && Filter.MayContain(XxHash3.HashToUInt64(key)) ? Index.Candidates(key) : default;

    /// <summary>
    /// Returns the blocks which may hold rows of keys in <paramref name="range"/>: none when the section's partition
    /// shares no key with it, every block when it contains the partition, and otherwise those the block index names.
    /// </summary>
    public RunBlockSpan Covering(KeyRange range) =>
        !Entry.Partition.Overlaps(range) ? default
        : range.Contains(Entry.Partition) ? Index.Span(0, Index.Blocks.Length)
        : Index.Covering(range);
}

/// <summary>
/// Consecutive blocks of a section, which one ranged read fetches: the first and the number of blocks, and the place of
/// their bytes in the object. The default value holds no block.
/// </summary>
/// <param name="First">The first block's number in the section.</param>
/// <param name="Count">The number of blocks; 0 for none.</param>
/// <param name="Offset">The first block's offset in the object.</param>
/// <param name="Length">The blocks' bytes, back to back.</param>
internal readonly record struct RunBlockSpan(int First, int Count, long Offset, long Length);

/// <summary>
/// A section's block index (archive §7): each block's place, whether its first key continues the previous block's last
/// row, and its first key.
/// </summary>
/// <param name="Blocks">The blocks, back to back from the section's offset, in order.</param>
internal sealed record RunBlockIndex(ImmutableArray<RunBlockEntry> Blocks)
{

    /// <summary>
    /// Returns the blocks which may hold rows of <paramref name="key"/>. The blocks whose first key is the key hold its
    /// rows, and the block before them too when the first of them continues it; when no block starts with the key, only the
    /// last block whose first key lies below it may hold it.
    /// </summary>
    public RunBlockSpan Candidates(ReadOnlySpan<byte> key)
    {
        var below = CountBelow(FirstKeyOrder.Of(key), inclusive: false);
        var atOrBelow = CountBelow(FirstKeyOrder.Of(key), inclusive: true);
        if (atOrBelow == 0)
        {
            return default;
        }

        if (atOrBelow == below)
        {
            return Span(below - 1, 1);
        }

        var first = Blocks[below].Continues ? below - 1 : below;
        return Span(first, atOrBelow - first);
    }

    /// <summary>
    /// Returns the blocks which may hold rows of keys in <paramref name="range"/>: those whose first key lies in it, and
    /// the last block whose first key lies below it, whose rows may run into it.
    /// </summary>
    public RunBlockSpan Covering(KeyRange range)
    {
        var below = CountBelow(FirstKeyOrder.Of(range), inclusive: false);
        var atOrBelow = CountBelow(FirstKeyOrder.Of(range), inclusive: true);
        if (atOrBelow == 0)
        {
            return default;
        }

        var first = below > 0 ? below - 1 : 0;
        return Span(first, atOrBelow - first);
    }

    /// <summary>Returns the span of <paramref name="count"/> consecutive blocks from <paramref name="first"/>, at least one.</summary>
    public RunBlockSpan Span(int first, int count)
    {
        var last = Blocks[first + count - 1];
        var offset = Blocks[first].Offset;
        return new RunBlockSpan(first, count, offset, last.Offset + last.Length - offset);
    }

    /// <inheritdoc/>
    public bool Equals(RunBlockIndex? other) => other is not null && Blocks.SequenceEqual(other.Blocks);

    /// <inheritdoc/>
    public override int GetHashCode() => Blocks.Length;

    /// <summary>
    /// Returns the number of blocks whose first key lies below what <paramref name="order"/> seeks, or at or in it too
    /// when <paramref name="inclusive"/>; the first keys never decrease.
    /// </summary>
    private int CountBelow(FirstKeyOrder order, bool inclusive)
    {
        var low = 0;
        var high = Blocks.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var comparison = order.Compare(Blocks[middle].FirstKey.Span);
            if (comparison < 0 || inclusive && comparison == 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Orders a block's first key against what a lookup seeks, one key bytewise or a dyadic range's interval of keys:
    /// negative below it, 0 at or in it, positive above it.
    /// </summary>
    private readonly ref struct FirstKeyOrder
    {
        private readonly ReadOnlySpan<byte> _key;
        private readonly KeyRange? _range;

        private FirstKeyOrder(ReadOnlySpan<byte> key, KeyRange? range)
        {
            _key = key;
            _range = range;
        }

        public static FirstKeyOrder Of(ReadOnlySpan<byte> key) => new(key, null);

        public static FirstKeyOrder Of(KeyRange range) => new(default, range);

        public int Compare(ReadOnlySpan<byte> firstKey) =>
            _range is { } range ? KeyRangeOrder.Compare(firstKey, range) : firstKey.SequenceCompareTo(_key);
    }
}
/// <summary>
/// A block's entry in its section's block index: its place in the object, whether its first row's key is the previous
/// block's last row's, and its first row's key.
/// </summary>
/// <param name="Offset">The block's first byte in the object.</param>
/// <param name="Length">The block's length in bytes, its CRC-32C included.</param>
/// <param name="Continues">Whether the block's first key is the previous block's last row's key.</param>
/// <param name="FirstKey">The key of the block's first row.</param>
internal readonly record struct RunBlockEntry(long Offset, long Length, bool Continues, ReadOnlyMemory<byte> FirstKey)
{
    /// <inheritdoc/>
    public bool Equals(RunBlockEntry other) =>
        Offset == other.Offset && Length == other.Length && Continues == other.Continues && FirstKey.Span.SequenceEqual(other.FirstKey.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Offset, Length, Continues, FirstKey.Length);
}

/// <summary>
/// A section's Bloom filter over its keys (archive §7): <c>m</c> bits, of which each key sets <c>k</c>, at positions
/// <c>(h₁ + i·h₂) mod m</c> from the low and high 32 bits of its XXH3 64-bit hash.
/// </summary>
internal sealed class RunBloomFilter : IEquatable<RunBloomFilter>
{
    /// <summary>The most hashes a filter records.</summary>
    public const int MaxHashCount = 64;

    /// <summary>The most bits per key a writer sets.</summary>
    public const int MaxBitsPerKey = 64;

    private readonly byte[] _bits;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunBloomFilter"/> class, taking <paramref name="bits"/> as they are.
    /// </summary>
    /// <param name="hashCount">The number of bits each key sets, 1 to <see cref="MaxHashCount"/>.</param>
    /// <param name="bits">The filter's bytes, at least one.</param>
    public RunBloomFilter(int hashCount, byte[] bits)
    {
        Debug.Assert(hashCount is >= 1 and <= MaxHashCount, "A filter sets 1 to 64 bits per key.");
        Debug.Assert(bits.Length > 0, "A filter holds at least one byte.");
        HashCount = hashCount;
        _bits = bits;
    }

    /// <summary>Gets the number of bits each key sets.</summary>
    public int HashCount { get; }

    /// <summary>Gets the filter's bytes.</summary>
    public ReadOnlySpan<byte> Bits => _bits;

    /// <summary>
    /// Returns the hash count a writer records for <paramref name="bitsPerKey"/> bits per key: <c>max(1, round(b · ln 2))</c>.
    /// </summary>
    public static int HashCountFor(int bitsPerKey) => Math.Max(1, (int)Math.Round(bitsPerKey * Math.Log(2)));

    /// <summary>
    /// Returns the filter over the keys whose XXH3 hashes <paramref name="hashes"/> holds, each key once, at
    /// <paramref name="bitsPerKey"/> bits per key: <c>⌈n · b / 8⌉</c> bytes.
    /// </summary>
    public static RunBloomFilter Create(IReadOnlyList<ulong> hashes, int bitsPerKey)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var bits = new byte[checked(hashes.Count * (long)bitsPerKey + 7) / 8];
        var filter = new RunBloomFilter(HashCountFor(bitsPerKey), bits);
        foreach (var hash in hashes)
        {
            filter.Set(hash);
        }

        return filter;
    }

    /// <summary>
    /// Returns the filter over the keys whose XXH3 hashes <paramref name="hashes"/> holds, each key once, at
    /// <paramref name="bitsPerKey"/> bits per key: <c>⌈n · b / 8⌉</c> bytes.
    /// </summary>
    public static RunBloomFilter Create(ReadOnlySpan<ulong> hashes, int bitsPerKey)
    {
        var bits = new byte[checked(hashes.Length * (long)bitsPerKey + 7) / 8];
        var filter = new RunBloomFilter(HashCountFor(bitsPerKey), bits);
        foreach (var hash in hashes)
        {
            filter.Set(hash);
        }

        return filter;
    }

    /// <summary>
    /// Returns whether the filter admits <paramref name="key"/>: every bit the key sets is set. A key the filter was
    /// built over is always admitted.
    /// </summary>
    public bool MayContain(ReadOnlySpan<byte> key) => MayContain(XxHash3.HashToUInt64(key));

    /// <summary>
    /// Returns whether the filter admits the key whose XXH3 hash is <paramref name="hash"/>.
    /// </summary>
    public bool MayContain(ulong hash)
    {
        var bitCount = (ulong)_bits.Length * 8;
        var bit = (hash & uint.MaxValue) % bitCount;
        var step = (hash >> 32) % bitCount;
        for (var index = 0; index < HashCount; index++)
        {
            if ((_bits[(int)(bit / 8)] & (1 << (int)(bit % 8))) == 0)
            {
                return false;
            }

            bit = Next(bit, step, bitCount);
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Equals(RunBloomFilter? other) => other is not null && HashCount == other.HashCount && Bits.SequenceEqual(other.Bits);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RunBloomFilter);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(HashCount, _bits.Length);

    /// <summary>
    /// Returns the bit after <paramref name="bit"/>: <c>(h₁ + i·h₂) mod m</c> reduced once and stepped by addition, which
    /// gives the same bits.
    /// </summary>
    private static ulong Next(ulong bit, ulong step, ulong bitCount)
    {
        bit += step;
        return bit >= bitCount ? bit - bitCount : bit;
    }

    private void Set(ulong hash)
    {
        var bitCount = (ulong)_bits.Length * 8;
        var bit = (hash & uint.MaxValue) % bitCount;
        var step = (hash >> 32) % bitCount;
        for (var index = 0; index < HashCount; index++)
        {
            _bits[(int)(bit / 8)] |= (byte)(1 << (int)(bit % 8));
            bit = Next(bit, step, bitCount);
        }
    }
}
