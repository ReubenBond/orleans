using System.Numerics;
using CsCheck;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A bit-by-bit reading of ledgers.md §2.1, written apart from <see cref="KeyRange"/>: a range is its prefix as a string
/// of <c>0</c> and <c>1</c>, bit <c>i</c> of a key is bit <c>7 − (i mod 8)</c> of byte <c>i / 8</c>, and bits past a
/// key's end are 0.
/// </summary>
internal static class KeyBits
{
    /// <summary>Depths which stay small, cluster at byte boundaries, and now and then run deep.</summary>
    public static readonly Gen<int> Depths = Gen.Frequency(
        (6, Gen.Int[0, 20]),
        (3, Gen.Select(Gen.Int[1, 10], Gen.Int[-1, 1], static (bytes, offset) => 8 * bytes + offset)),
        (1, Gen.Int[0, 300]));

    /// <summary>Ranges, as their bits.</summary>
    public static readonly Gen<string> Ranges = Depths.SelectMany(static depth => Gen.Bool.Array[depth].Select(Text));

    /// <summary>Keys: mostly short, so ranges of every depth hold some and miss some, with now and then a long one.</summary>
    public static readonly Gen<byte[]> Keys = Gen.Frequency(
        (4, Gen.Byte.Array[1, 4]),
        (2, Gen.Byte.Array[1, 40]),
        (1, Gen.Byte.Array[1, 300]));

    public static int Bit(ReadOnlySpan<byte> key, int index) => index < key.Length * 8 ? (key[index / 8] >> (7 - index % 8)) & 1 : 0;

    public static string Of(KeyRange range) => new([.. Enumerable.Range(0, range.Depth).Select(index => Bit(range.Prefix, index) == 1 ? '1' : '0')]);

    public static KeyRange ToRange(string bits)
    {
        var prefix = new byte[(bits.Length + 7) / 8];
        for (var index = 0; index < bits.Length; index++)
        {
            if (bits[index] == '1')
            {
                prefix[index / 8] |= (byte)(0x80 >> (index % 8));
            }
        }

        return new KeyRange(prefix, bits.Length);
    }

    public static bool Contains(string range, ReadOnlySpan<byte> key)
    {
        for (var index = 0; index < range.Length; index++)
        {
            if (Bit(key, index) != range[index] - '0')
            {
                return false;
            }
        }

        return true;
    }

    public static bool Covers(string outer, string inner) => inner.StartsWith(outer, StringComparison.Ordinal);

    public static bool Disjoint(string left, string right) => !Covers(left, right) && !Covers(right, left);

    /// <summary>
    /// Returns a key of 1 to 65,535 bytes whose first bits are <paramref name="range"/>'s and whose later bits are
    /// <paramref name="tail"/>'s, padded with 0.
    /// </summary>
    public static byte[] KeyIn(string range, ReadOnlySpan<byte> tail)
    {
        var length = Math.Clamp(Math.Max((range.Length + 7) / 8, tail.Length), 1, KeyRange.MaxKeyLength);
        var key = new byte[length];
        for (var index = 0; index < length * 8; index++)
        {
            var bit = index < range.Length ? range[index] - '0' : Bit(tail, index);
            key[index / 8] |= (byte)(bit << (7 - index % 8));
        }

        return key;
    }

    /// <summary>
    /// Returns the least key in <paramref name="range"/>: its bits, then 0 bits to a whole byte, without the trailing zero
    /// bytes a shorter key leaves implied, but at least 1 byte.
    /// </summary>
    public static byte[] LowestKey(string range)
    {
        var key = KeyIn(range, []);
        var length = key.Length;
        while (length > 1 && key[length - 1] == 0)
        {
            length--;
        }

        return key[..length];
    }

    /// <summary>Returns the greatest key in <paramref name="range"/>: its bits, then 1 bits, to the longest key.</summary>
    public static byte[] HighestKey(string range)
    {
        var key = new byte[KeyRange.MaxKeyLength];
        key.AsSpan().Fill(byte.MaxValue);
        for (var index = 0; index < range.Length; index++)
        {
            if (range[index] == '0')
            {
                key[index / 8] ^= (byte)(0x80 >> (index % 8));
            }
        }

        return key;
    }

    /// <summary>
    /// Returns the key just before <paramref name="key"/> in the key order, bytes compared in turn and a key before its
    /// extensions, or null for the least key, <c>[0x00]</c>.
    /// </summary>
    public static byte[]? Predecessor(byte[] key)
    {
        if (key[^1] == 0)
        {
            return key.Length == 1 ? null : key[..^1];
        }

        var predecessor = new byte[KeyRange.MaxKeyLength];
        predecessor.AsSpan().Fill(byte.MaxValue);
        key.AsSpan(0, key.Length - 1).CopyTo(predecessor);
        predecessor[key.Length - 1] = (byte)(key[^1] - 1);
        return predecessor;
    }

    /// <summary>Returns the key just after <paramref name="key"/> in the key order, or null for the greatest key.</summary>
    public static byte[]? Successor(byte[] key)
    {
        if (key.Length < KeyRange.MaxKeyLength)
        {
            return [.. key, 0];
        }

        var last = key.AsSpan().LastIndexOfAnyExcept(byte.MaxValue);
        if (last < 0)
        {
            return null;
        }

        var successor = key[..(last + 1)];
        successor[last]++;
        return successor;
    }

    /// <summary>
    /// The oracle for <see cref="KeyRange.IsPartition"/>: the ranges are pairwise disjoint and their measures,
    /// <c>2^−depth</c>, sum to 1 in exact arithmetic.
    /// </summary>
    public static bool IsPartition(IReadOnlyList<string> ranges) => PairwiseDisjoint(ranges) && MeasuresSumToOne(ranges);

    /// <summary>
    /// Returns whether no range contains another. In the ordinal order of their bits, every range between a range and
    /// one it contains starts with the first's bits too, so neighbors suffice.
    /// </summary>
    public static bool PairwiseDisjoint(IReadOnlyList<string> ranges)
    {
        var ordered = ranges.Order(StringComparer.Ordinal).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (!Disjoint(ordered[index - 1], ordered[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool MeasuresSumToOne(IReadOnlyList<string> ranges)
    {
        if (ranges.Count == 0)
        {
            return false;
        }

        var depth = ranges.Max(static range => range.Length);
        var sum = BigInteger.Zero;
        foreach (var range in ranges)
        {
            sum += BigInteger.One << (depth - range.Length);
        }

        return sum == BigInteger.One << depth;
    }

    private static string Text(bool[] bits) => new([.. bits.Select(static bit => bit ? '1' : '0')]);
}
