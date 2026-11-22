using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A 32-byte SHA-256 hash of an encoded <see cref="JournalRowKey"/>, stored inline as a value type so index lookups
/// and batches avoid per-key heap allocations.
/// </summary>
public readonly struct JournalKeyHash : IEquatable<JournalKeyHash>, IComparable<JournalKeyHash>
{
    /// <summary>The fixed byte length of a SHA-256 key hash.</summary>
    public const int Length = 32;

    private readonly ulong _v0;
    private readonly ulong _v1;
    private readonly ulong _v2;
    private readonly ulong _v3;

    private JournalKeyHash(ulong v0, ulong v1, ulong v2, ulong v3)
    {
        _v0 = v0;
        _v1 = v1;
        _v2 = v2;
        _v3 = v3;
    }

    /// <summary>Computes the SHA-256 hash of <paramref name="encodedRowKey"/> without heap allocation.</summary>
    public static JournalKeyHash Compute(ReadOnlySpan<byte> encodedRowKey)
    {
        Span<byte> digest = stackalloc byte[Length];
        _ = SHA256.HashData(encodedRowKey, digest);
        return FromSpan(digest);
    }

    /// <summary>Reads a 32-byte hash in big-endian byte order from <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than <see cref="Length"/> bytes.</exception>
    public static JournalKeyHash FromSpan(ReadOnlySpan<byte> source)
    {
        if (source.Length < Length)
        {
            throw new ArgumentException($"A journal key hash requires at least {Length} bytes.", nameof(source));
        }

        return new JournalKeyHash(
            BinaryPrimitives.ReadUInt64BigEndian(source[..8]),
            BinaryPrimitives.ReadUInt64BigEndian(source.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(source.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(source.Slice(24, 8)));
    }

    /// <summary>Writes the 32 hash bytes into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Length"/> bytes.</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"Destination must be at least {Length} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], _v0);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), _v1);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16, 8), _v2);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24, 8), _v3);
    }

    /// <summary>Returns whether <paramref name="bytes"/> starts with the same 32 bytes as this hash.</summary>
    public bool Matches(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Length
        && BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]) == _v0
        && BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8)) == _v1
        && BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16, 8)) == _v2
        && BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24, 8)) == _v3;

    /// <inheritdoc/>
    public int CompareTo(JournalKeyHash other)
    {
        var cmp = _v0.CompareTo(other._v0);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = _v1.CompareTo(other._v1);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = _v2.CompareTo(other._v2);
        return cmp != 0 ? cmp : _v3.CompareTo(other._v3);
    }

    /// <inheritdoc/>
    public bool Equals(JournalKeyHash other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2 && _v3 == other._v3;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalKeyHash other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_v0, _v1, _v2, _v3);

    /// <summary>Returns whether two hashes are equal.</summary>
    public static bool operator ==(JournalKeyHash left, JournalKeyHash right) => left.Equals(right);

    /// <summary>Returns whether two hashes differ.</summary>
    public static bool operator !=(JournalKeyHash left, JournalKeyHash right) => !left.Equals(right);

    /// <summary>Returns whether <paramref name="left"/> precedes <paramref name="right"/> in unsigned byte order.</summary>
    public static bool operator <(JournalKeyHash left, JournalKeyHash right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether <paramref name="left"/> follows <paramref name="right"/> in unsigned byte order.</summary>
    public static bool operator >(JournalKeyHash left, JournalKeyHash right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether <paramref name="left"/> precedes or equals <paramref name="right"/>.</summary>
    public static bool operator <=(JournalKeyHash left, JournalKeyHash right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether <paramref name="left"/> follows or equals <paramref name="right"/>.</summary>
    public static bool operator >=(JournalKeyHash left, JournalKeyHash right) => left.CompareTo(right) >= 0;
}
