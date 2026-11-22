using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// At most <see cref="MaxLength"/> opaque bytes, held inline without allocation: a key's token for a segment, or a
/// version's base (ledgers.md §2.2, §4; identity.md §9.1). The default is the empty token.
/// </summary>
/// <remarks>
/// Ledgers stores and lends a token verbatim and never interprets it; its meaning belongs to the caller which supplies
/// it. Equality is by bytes.
/// </remarks>
public readonly struct RowToken : IEquatable<RowToken>
{
    /// <summary>The most bytes a token holds.</summary>
    public const int MaxLength = 32;

    private readonly Bytes _bytes;
    private readonly byte _length;

    /// <summary>Initializes a new instance of the <see cref="RowToken"/> struct, copying <paramref name="bytes"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> holds more than <see cref="MaxLength"/> bytes.</exception>
    public RowToken(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A token holds at most {MaxLength} bytes, not {bytes.Length}."),
                nameof(bytes));
        }

        bytes.CopyTo(_bytes);
        _length = (byte)bytes.Length;
    }

    /// <summary>Gets the token's length in bytes, 0 to <see cref="MaxLength"/>.</summary>
    public int Length => _length;

    /// <summary>Gets a value indicating whether the token is empty.</summary>
    public bool IsEmpty => _length == 0;

    public static bool operator ==(RowToken left, RowToken right) => left.Equals(right);

    public static bool operator !=(RowToken left, RowToken right) => !left.Equals(right);

    /// <summary>Returns the token's bytes, a view of this token's storage.</summary>
    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() => ((ReadOnlySpan<byte>)_bytes)[.._length];

    /// <summary>Returns a copy of the token's bytes.</summary>
    public byte[] ToArray() => AsSpan().ToArray();

    /// <inheritdoc/>
    public bool Equals(RowToken other) => AsSpan().SequenceEqual(other.AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RowToken other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.AddBytes(AsSpan());
        return hash.ToHashCode();
    }

    /// <summary>Returns the token's bytes in lowercase hexadecimal.</summary>
    public override string ToString() => Convert.ToHexStringLower(AsSpan());

    [InlineArray(MaxLength)]
    private struct Bytes
    {
        private byte _first;
    }
}

/// <summary>
/// A key's version, <c>(Base, Count)</c>: an opaque base and a count which every row of the key advances by one
/// (ledgers.md §2.2, §4, LG-I13; identity.md §9.1). Equal by base and count, with no order.
/// </summary>
/// <param name="Base">The version's base, which every row of the key keeps.</param>
/// <param name="Count">The version's count, which every row of the key advances by one.</param>
public readonly record struct RowVersion(RowToken Base, ulong Count)
{
    /// <summary>Returns the version after one more row of the key: the same base, and the count advanced by one.</summary>
    /// <exception cref="OverflowException">The count is at the <c>u64</c> maximum (conventions.md §8).</exception>
    public RowVersion Next() => this with { Count = checked(Count + 1) };

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({Base}, {Count})");
}

/// <summary>
/// A segment's identity, <c>(Ledger, Start)</c>: its ledger and its first slot, which carries the start flag
/// (ledgers.md §2.2; identity.md §6).
/// </summary>
/// <param name="Ledger">The segment's ledger; a segment never spans ledgers.</param>
/// <param name="Start">The segment's first slot.</param>
public readonly record struct LedgerSegmentId(LedgerId Ledger, Slot Start);

/// <summary>
/// A segment its writer ended or a drain scanned, with the number of keys it defined (ledgers.md §2.4): each key a drain
/// of the segment writes once, which the Orleans composition's nodes report as their undrained keys (orleans.md §5.6).
/// </summary>
/// <param name="Segment">The segment.</param>
/// <param name="KeyCount">The keys the segment defined, at least 1.</param>
public readonly record struct LedgerSegmentKeys(LedgerSegmentId Segment, int KeyCount);
