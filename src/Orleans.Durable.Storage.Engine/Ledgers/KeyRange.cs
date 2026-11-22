using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;


namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A dyadic range of keys, <c>(Prefix, Depth)</c>: the keys whose first <see cref="Depth"/> bits equal the prefix
/// (ledgers.md §2.1, identity.md §4).
/// </summary>
/// <remarks>
/// <para>
/// A key is 1 to <see cref="MaxKeyLength"/> bytes. Bit <c>i</c> of a key is bit <c>7 − (i mod 8)</c> of byte
/// <c>i / 8</c>, and bits past the key's end are 0 for containment. The prefix holds <see cref="Depth"/> bits in
/// <c>⌈Depth / 8⌉</c> bytes with the unused low bits 0, so each range has one representation, and equality is by value.
/// The default value is <see cref="Root"/>, the whole keyspace.
/// </para>
/// <para>
/// Two ranges are disjoint or one contains the other, and the keys of each range form an interval of the key order.
/// The children of <c>(p, d)</c> are <c>(2p, d + 1)</c> and <c>(2p + 1, d + 1)</c>, which are siblings; a range at
/// <see cref="MaxDepth"/>, the bits of the longest key, has none.
/// </para>
/// </remarks>
public readonly struct KeyRange : IEquatable<KeyRange>
{
    /// <summary>The length of the longest key, in bytes.</summary>
    public const int MaxKeyLength = 65_535;

    /// <summary>The depth of the deepest range: the bits of the longest key.</summary>
    public const int MaxDepth = 8 * MaxKeyLength;

    internal const string ReasonRangeTruncated = "range-truncated";
    internal const string ReasonRangeDepth = "range-depth";
    internal const string ReasonRangePrefixBits = "range-prefix-bits";

    // ⌈Depth / 8⌉ bytes with the bits past Depth zero; null at depth 0.
    private readonly byte[]? _prefix;

    /// <summary>Initializes a new instance of the <see cref="KeyRange"/> struct.</summary>
    /// <param name="prefix">The range's <paramref name="depth"/> bits, in <c>⌈depth / 8⌉</c> bytes with the unused low bits 0.</param>
    /// <param name="depth">The number of leading key bits the range fixes, from 0 to <see cref="MaxDepth"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside 0 to <see cref="MaxDepth"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="prefix"/> is not <c>⌈depth / 8⌉</c> bytes, or sets a bit past <paramref name="depth"/>.
    /// </exception>
    public KeyRange(ReadOnlySpan<byte> prefix, int depth)
    {
        CheckDepth(depth);
        if (prefix.Length != ByteLength(depth))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A range of depth {depth} has a prefix of {ByteLength(depth)} bytes, not {prefix.Length}."),
                nameof(prefix));
        }

        if (depth > 0 && (prefix[^1] & ~LastByteMask(depth) & byte.MaxValue) != 0)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A range of depth {depth} has a prefix whose bits past its depth are not 0."),
                nameof(prefix));
        }

        _prefix = depth == 0 ? null : prefix.ToArray();
        Depth = depth;
    }

    private KeyRange(int depth, byte[] prefix)
    {
        Debug.Assert(depth is > 0 and <= MaxDepth && prefix.Length == ByteLength(depth), "A range's prefix holds its depth's bytes.");
        Debug.Assert((prefix[^1] & ~LastByteMask(depth) & byte.MaxValue) == 0, "A range's bits past its depth are 0.");
        _prefix = prefix;
        Depth = depth;
    }

    /// <summary>Gets the whole keyspace, <c>(0, 0)</c>.</summary>
    public static KeyRange Root => default;

    /// <summary>Gets the number of leading key bits the range fixes.</summary>
    public int Depth { get; }

    /// <summary>Gets the range's bits, in <c>⌈Depth / 8⌉</c> bytes with the unused low bits 0.</summary>
    public ReadOnlySpan<byte> Prefix => _prefix;

    /// <summary>Gets the length of the range's binary form: its depth (<c>u32</c>) and its prefix bytes (ledgers.md §7).</summary>
    internal int EncodedLength => sizeof(uint) + ByteLength(Depth);

    /// <summary>Gets the range's two children: the keys whose next bit is 0, and those whose next bit is 1.</summary>
    /// <exception cref="InvalidOperationException">The range is at <see cref="MaxDepth"/> (conventions.md §8).</exception>
    public (KeyRange Lower, KeyRange Upper) Children
    {
        get
        {
            if (Depth == MaxDepth)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"A range at depth {MaxDepth}, the bits of the longest key, has no children."));
            }

            var depth = Depth + 1;
            var lower = new byte[ByteLength(depth)];
            Prefix.CopyTo(lower);
            var upper = lower.AsSpan().ToArray();
            upper[Depth / 8] |= BitMask(Depth);
            return (Adopt(lower, depth), Adopt(upper, depth));
        }
    }

    /// <summary>Gets the range whose child this range is.</summary>
    /// <exception cref="InvalidOperationException">The range is the whole keyspace (conventions.md §8).</exception>
    public KeyRange Parent
    {
        get
        {
            CheckNotRoot("parent");
            var depth = Depth - 1;
            if (depth == 0)
            {
                return Root;
            }

            var prefix = Prefix[..ByteLength(depth)].ToArray();
            prefix[^1] &= LastByteMask(depth);
            return Adopt(prefix, depth);
        }
    }

    /// <summary>Gets the other child of this range's parent.</summary>
    /// <exception cref="InvalidOperationException">The range is the whole keyspace (conventions.md §8).</exception>
    public KeyRange Sibling
    {
        get
        {
            CheckNotRoot("sibling");
            var prefix = Prefix.ToArray();
            prefix[(Depth - 1) / 8] ^= BitMask(Depth - 1);
            return Adopt(prefix, Depth);
        }
    }

    /// <summary>Returns the range at <paramref name="depth"/> which contains <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="MaxKeyLength"/> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside 0 to <see cref="MaxDepth"/>.</exception>
    public static KeyRange Containing(ReadOnlySpan<byte> key, int depth)
    {
        CheckKey(key);
        CheckDepth(depth);
        if (depth == 0)
        {
            return Root;
        }

        var prefix = new byte[ByteLength(depth)];
        key[..Math.Min(key.Length, prefix.Length)].CopyTo(prefix);
        prefix[^1] &= LastByteMask(depth);
        return Adopt(prefix, depth);
    }

    /// <summary>
    /// Returns the hash code of <see cref="Containing"/><c>(key, depth)</c> without constructing it, so a lookup by
    /// range finds a key's range at a depth without allocating.
    /// </summary>
    /// <remarks>
    /// The hash covers the depth and the first 8 bytes of the prefix only, so it spreads keys well only when they lead
    /// with a hash, as journal row keys do with their <c>xxhash32</c> prefix (journals.md §4.1). Keys which share their
    /// first 8 bytes share one bucket per depth; a lookup stays correct, and slows with the bucket.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="MaxKeyLength"/> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside 0 to <see cref="MaxDepth"/>.</exception>
    internal static int GetContainingHashCode(ReadOnlySpan<byte> key, int depth)
    {
        CheckKey(key);
        CheckDepth(depth);
        return GetContainingHashCodeUnchecked(key, depth);
    }

    /// <summary>
    /// Returns <see cref="GetContainingHashCode"/> for a key and depth the caller has already validated.
    /// </summary>
    internal static int GetContainingHashCodeUnchecked(ReadOnlySpan<byte> key, int depth)
    {
        Debug.Assert(!key.IsEmpty && key.Length <= MaxKeyLength && depth is >= 0 and <= MaxDepth, "The caller validated the key and depth.");
        var leading = LeadingBytes(key);
        if (depth < 64)
        {
            // C# takes a ulong's shift count mod 64, so a shift by 64 at depth 0 would keep every bit.
            leading &= depth == 0 ? 0 : ulong.MaxValue << (64 - depth);
        }

        return Hash(depth, leading);
    }

    /// <summary>
    /// Reads one range's binary form from the front of <paramref name="source"/>: its depth (<c>u32</c>, little-endian)
    /// and its <c>⌈depth / 8⌉</c> prefix bytes (ledgers.md §7). A per-record decoder (conventions.md §15): the caller
    /// advances by <paramref name="range"/>'s <see cref="EncodedLength"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> with the range, or the rejection naming the first rule the bytes break: <c>range-truncated</c>,
    /// <c>range-depth</c>, or <c>range-prefix-bits</c>.
    /// </returns>
    internal static InvalidDataException? Read(ReadOnlySpan<byte> source, out KeyRange range)
    {
        range = default;
        if (source.Length < sizeof(uint))
        {
            return Truncated();
        }

        var depth = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (depth > MaxDepth)
        {
            return DecodeRejection.Create(
                ReasonRangeDepth,
                string.Create(CultureInfo.InvariantCulture, $"A range's depth is at most {MaxDepth}, not {depth}."));
        }

        var length = ByteLength((int)depth);
        if (source.Length - sizeof(uint) < length)
        {
            return Truncated();
        }

        var prefix = source.Slice(sizeof(uint), length);
        if (depth > 0 && (prefix[^1] & ~LastByteMask((int)depth) & byte.MaxValue) != 0)
        {
            return DecodeRejection.Create(
                ReasonRangePrefixBits,
                string.Create(CultureInfo.InvariantCulture, $"A range of depth {depth} has a prefix whose bits past its depth are not 0."));
        }

        range = depth == 0 ? Root : Adopt(prefix.ToArray(), (int)depth);
        return null;

        static InvalidDataException Truncated() =>
            DecodeRejection.Create(ReasonRangeTruncated, "A range runs past the bytes which hold it.");
    }

    /// <summary>Writes the range's binary form, <see cref="EncodedLength"/> bytes, to the front of <paramref name="destination"/>.</summary>
    internal void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)Depth);
        Prefix.CopyTo(destination[sizeof(uint)..]);
    }

    /// <summary>
    /// Returns whether <paramref name="ranges"/> partitions the keyspace: every key lies in exactly one of its members,
    /// so none repeats and its members are the leaves of one full binary trie rooted at <see cref="Root"/> (ledgers.md
    /// §2.1, LG-I1).
    /// </summary>
    internal static bool IsPartition(ReadOnlySpan<KeyRange> ranges)
    {
        var merged = Coalesce(ranges);
        return merged.Count == 1 && merged[0].Depth == 0;
    }

    /// <summary>
    /// Returns whether <paramref name="parts"/> and <paramref name="whole"/>, each a list of pairwise disjoint ranges,
    /// hold exactly the same keys: whether the parts tile the whole.
    /// </summary>
    internal static bool Tiles(ReadOnlySpan<KeyRange> parts, ReadOnlySpan<KeyRange> whole) =>
        Coalesce(parts).SequenceEqual(Coalesce(whole));

    /// <summary>
    /// Merges <paramref name="ranges"/>, sibling with sibling, in key order: for pairwise disjoint ranges, the result is
    /// the maximal ranges their union holds, in key order, one list for each union.
    /// </summary>
    private static List<KeyRange> Coalesce(ReadOnlySpan<KeyRange> ranges)
    {
        // In key order, the leaves of a full binary trie merge, sibling with sibling, into its root; any other list
        // leaves a gap, an overlap, or a repeat which never merges.
        var ordered = ranges.ToArray();
        Array.Sort(ordered, CompareFirstKeys);
        var merged = new List<KeyRange>(ordered.Length);
        foreach (var range in ordered)
        {
            var top = range;
            while (top.Depth > 0 && top.IsUpperChild && merged.Count > 0 && merged[^1] == top.Sibling)
            {
                merged.RemoveAt(merged.Count - 1);
                top = top.Parent;
            }

            merged.Add(top);
        }

        return merged;
    }

    /// <summary>Returns whether the range contains <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="MaxKeyLength"/> bytes.</exception>
    public bool Contains(ReadOnlySpan<byte> key)
    {
        CheckKey(key);
        return LeadsWithPrefix(key);
    }

    /// <summary>Returns whether the range contains <paramref name="key"/>, a key the caller has already validated.</summary>
    internal bool ContainsUnchecked(ReadOnlySpan<byte> key)
    {
        Debug.Assert(!key.IsEmpty && key.Length <= MaxKeyLength, "The caller validated the key.");
        return LeadsWithPrefix(key);
    }

    /// <summary>Returns whether the range contains every key of <paramref name="range"/>.</summary>
    public bool Contains(KeyRange range) => range.Depth >= Depth && LeadsWithPrefix(range.Prefix);

    /// <summary>Returns whether the range and <paramref name="range"/> share a key: whether one contains the other.</summary>
    public bool Overlaps(KeyRange range) => Contains(range) || range.Contains(this);

    /// <inheritdoc/>
    public bool Equals(KeyRange other) => Depth == other.Depth && Prefix.SequenceEqual(other.Prefix);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is KeyRange other && Equals(other);

    /// <summary>Returns a hash code of the depth and at most the first 8 bytes of the prefix.</summary>
    public override int GetHashCode() => Hash(Depth, LeadingBytes(Prefix));

    /// <summary>Returns the text form: the prefix's bits, <c>/</c>, and the depth, as <c>10/2</c>, or <c>/0</c> for the whole keyspace.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(Depth + 8);
        for (var bit = 0; bit < Depth; bit++)
        {
            _ = text.Append((Prefix[bit / 8] & BitMask(bit)) == 0 ? '0' : '1');
        }

        return text.Append('/').Append(Depth.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    public static bool operator ==(KeyRange left, KeyRange right) => left.Equals(right);

    public static bool operator !=(KeyRange left, KeyRange right) => !left.Equals(right);

    private bool IsUpperChild => (Prefix[(Depth - 1) / 8] & BitMask(Depth - 1)) != 0;

    /// <summary>
    /// Returns whether the first <see cref="Depth"/> bits of <paramref name="bits"/>, which are 0 past its end, equal the
    /// prefix.
    /// </summary>
    private bool LeadsWithPrefix(ReadOnlySpan<byte> bits)
    {
        var prefix = Prefix;
        var whole = Depth / 8;
        var present = Math.Min(whole, bits.Length);
        if (!bits[..present].SequenceEqual(prefix[..present]) || prefix[present..whole].ContainsAnyExcept((byte)0))
        {
            return false;
        }

        var partial = Depth % 8;
        if (partial == 0)
        {
            return true;
        }

        var last = whole < bits.Length ? bits[whole] : (byte)0;
        return (last & LeadingMask(partial)) == prefix[whole];
    }

    private void CheckNotRoot(string relative)
    {
        if (Depth == 0)
        {
            throw new InvalidOperationException($"The whole keyspace has no {relative}.");
        }
    }

    /// <summary>
    /// Orders ranges by their first keys, the prefix followed by zeros, and a range before those it contains.
    /// </summary>
    private static int CompareFirstKeys(KeyRange left, KeyRange right)
    {
        var a = left.Prefix;
        var b = right.Prefix;
        var common = Math.Min(a.Length, b.Length);
        var order = a[..common].SequenceCompareTo(b[..common]);
        if (order != 0)
        {
            return order;
        }

        if (a[common..].ContainsAnyExcept((byte)0))
        {
            return 1;
        }

        return b[common..].ContainsAnyExcept((byte)0) ? -1 : left.Depth.CompareTo(right.Depth);
    }

    /// <summary>Makes a range of a prefix the caller has built in canonical form, which it takes as it is.</summary>
    private static KeyRange Adopt(byte[] prefix, int depth) => new(depth, prefix);

    private static int Hash(int depth, ulong leading) => HashCode.Combine(depth, leading);

    /// <summary>Returns the first 8 bytes of <paramref name="bytes"/>, zero past its end, as a big-endian integer.</summary>
    private static ulong LeadingBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> leading = stackalloc byte[sizeof(ulong)];
        leading.Clear();
        bytes[..Math.Min(bytes.Length, sizeof(ulong))].CopyTo(leading);
        return BinaryPrimitives.ReadUInt64BigEndian(leading);
    }

    private static int ByteLength(int depth) => (depth + 7) / 8;

    /// <summary>Returns the mask of bit <paramref name="bit"/> within its byte.</summary>
    private static byte BitMask(int bit) => (byte)(0x80 >> (bit % 8));

    /// <summary>Returns the mask of a byte's first <paramref name="bits"/> bits, from 1 to 8.</summary>
    private static byte LeadingMask(int bits) => (byte)((0xff << (8 - bits)) & 0xff);

    /// <summary>Returns the mask of the bits a range of <paramref name="depth"/>, at least 1, fixes in its prefix's last byte.</summary>
    private static byte LastByteMask(int depth) => LeadingMask((depth - 1) % 8 + 1);

    private static void CheckDepth(int depth, [CallerArgumentExpression(nameof(depth))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, MaxDepth, name);
    }

    /// <summary>Checks that <paramref name="key"/> is 1 to <see cref="MaxKeyLength"/> bytes, which every key is.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="MaxKeyLength"/> bytes.</exception>
    internal static void CheckKey(ReadOnlySpan<byte> key, [CallerArgumentExpression(nameof(key))] string? name = null)
    {
        if (key.IsEmpty || key.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A key is 1 to {MaxKeyLength} bytes, not {key.Length}."),
                name);
        }
    }
}
