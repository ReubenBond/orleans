using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Unicode;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A journal's row key (journals.md §4.1): <c>xxhash32</c> of the journal key's strict UTF-8, seed 0, as 4 big-endian
/// bytes, then the UTF-8 itself, so the midpoint splits and sibling merges of dyadic ranges divide journals evenly.
/// Immutable and compared by its bytes.
/// </summary>
public sealed class JournalRowKey : IEquatable<JournalRowKey>
{
    /// <summary>The bytes of the hash which leads each row key.</summary>
    public const int PrefixLength = sizeof(uint);

    /// <summary>The longest journal key's strict UTF-8, in bytes: a row key is at most <see cref="KeyRange.MaxKeyLength"/>.</summary>
    public const int MaxKeyBytes = KeyRange.MaxKeyLength - PrefixLength;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _bytes;
    private readonly int _hashCode;

    private JournalRowKey(byte[] bytes)
    {
        _bytes = bytes;
        var hash = default(HashCode);
        hash.AddBytes(bytes);
        _hashCode = hash.ToHashCode();
    }

    /// <summary>Gets the row key's bytes.</summary>
    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>Gets the row key's bytes.</summary>
    public ReadOnlyMemory<byte> Memory => _bytes;

    /// <summary>Gets the row key's length in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>Returns the row key of the journal <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty, is not well-formed UTF-16 (a lone surrogate), or encodes to more than
    /// <see cref="MaxKeyBytes"/> bytes of UTF-8.
    /// </exception>
    public static JournalRowKey Encode(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        int length;
        try
        {
            length = Strict.GetByteCount(key);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A journal key is well-formed: it encodes to strict UTF-8.", nameof(key), exception);
        }

        if (length > MaxKeyBytes)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A journal key is at most {MaxKeyBytes} bytes of UTF-8, not {length}."),
                nameof(key));
        }

        var rowKey = new byte[PrefixLength + length];
        var utf8 = rowKey.AsSpan(PrefixLength);
        _ = Strict.GetBytes(key, utf8);
        BinaryPrimitives.WriteUInt32BigEndian(rowKey, XxHash32.HashToUInt32(utf8));
        return new JournalRowKey(rowKey);
    }

    /// <summary>
    /// Returns the journal key whose row key is <paramref name="rowKey"/>, the inverse of <see cref="Encode"/>; or
    /// <see langword="null"/> when <paramref name="rowKey"/> is no journal's row key: no journal key follows the hash, the
    /// hash does not lead its UTF-8, the UTF-8 is not strict, or it is longer than <see cref="MaxKeyBytes"/>.
    /// </summary>
    public static string? TryDecode(ReadOnlySpan<byte> rowKey)
    {
        if (rowKey.Length is <= PrefixLength or > KeyRange.MaxKeyLength)
        {
            return null;
        }

        var utf8 = rowKey[PrefixLength..];
        if (BinaryPrimitives.ReadUInt32BigEndian(rowKey) != XxHash32.HashToUInt32(utf8))
        {
            return null;
        }

        try
        {
            return Strict.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates <paramref name="rowKey"/> as a journal row key and returns a <see cref="JournalRowKey"/> over its bytes,
    /// or <see langword="null"/> when <paramref name="rowKey"/> is no valid journal row key.
    /// </summary>
    internal static JournalRowKey? TryFromRowKeyBytes(ReadOnlySpan<byte> rowKey)
    {
        if (rowKey.Length is <= PrefixLength or > KeyRange.MaxKeyLength)
        {
            return null;
        }

        var utf8 = rowKey[PrefixLength..];
        if (BinaryPrimitives.ReadUInt32BigEndian(rowKey) != XxHash32.HashToUInt32(utf8) || !Utf8.IsValid(utf8))
        {
            return null;
        }

        return new JournalRowKey(rowKey.ToArray());
    }

    /// <inheritdoc/>
    public bool Equals(JournalRowKey? other) =>
        other is not null && _hashCode == other._hashCode && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as JournalRowKey);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <summary>Returns the journal key, the UTF-8 after the hash.</summary>
    public override string ToString() => Encoding.UTF8.GetString(_bytes.AsSpan(PrefixLength));
}
