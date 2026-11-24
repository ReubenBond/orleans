using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Encodes and validates ledger-local stream row keys for <see cref="RowKind.LocalAppend"/> rows (ledgers.md §2.2, §4,
/// §7, LG-I15).
/// </summary>
/// <remarks>
/// Layout: <c>prefixBytes (max(4, ceil(range.Depth / 8)), unused low bits 0) || 0xFF || ledger.Value (8B BE) ||
/// stream.Span (1..64 ASCII bytes)</c>. The encoded key always satisfies <see cref="KeyRange.Contains(ReadOnlySpan{byte})"/>,
/// is disjoint from every valid UTF-8 journal row key (which never holds <c>0xFF</c> at index &gt;= 4), and is unique
/// per <c>(LedgerId, LocalStreamName)</c>.
/// </remarks>
public static class LedgerLocalKey
{
    /// <summary>Maximum supported dyadic range depth for ledger-local keys (32 prefix bytes).</summary>
    public const int MaxRangeDepth = 256;

    /// <summary>Minimum number of leading range-prefix bytes (4 bytes, matching a 32-bit hash prefix).</summary>
    public const int MinPrefixBytes = 4;

    /// <summary>Disjointness sentinel byte (<c>0xFF</c>), invalid in UTF-8.</summary>
    public const byte DisjointSentinel = 0xFF;

    /// <summary>Maximum encoded key length in bytes (<c>32 + 1 + 8 + 64 = 105</c>).</summary>
    public const int MaxEncodedLength = MaxRangeDepth / 8 + 1 + sizeof(ulong) + LocalStreamName.MaxLength;

    internal const string ReasonRangeDepthTooDeep = "range-depth-too-deep";
    internal const string ReasonTruncated = "truncated";
    internal const string ReasonPrefixMismatch = "prefix-mismatch";
    internal const string ReasonBadSentinel = "bad-sentinel";
    internal const string ReasonLedgerMismatch = "ledger-mismatch";
    internal const string ReasonEmptyStream = "empty-stream";
    internal const string ReasonStreamTooLong = "stream-too-long";
    internal const string ReasonBadStream = "bad-stream";

    /// <summary>Returns the prefix byte length for <paramref name="depth"/>: <c>max(4, ceil(depth / 8))</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside <c>0..MaxRangeDepth</c>.</exception>
    public static int PrefixByteLength(int depth)
    {
        if ((uint)depth > MaxRangeDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depth),
                depth,
                string.Create(CultureInfo.InvariantCulture, $"Ledger-local keys support range depths 0 to {MaxRangeDepth}, not {depth}."));
        }

        return Math.Max(MinPrefixBytes, (depth + 7) / 8);
    }

    /// <summary>Returns the exact encoded length for <paramref name="range"/> and <paramref name="stream"/>.</summary>
    public static int EncodedLength(KeyRange range, LocalStreamName stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PrefixByteLength(range.Depth) + 1 + sizeof(ulong) + stream.Span.Length;
    }

    /// <summary>Encodes the ledger-local row key for <paramref name="stream"/> in <paramref name="ledger"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default <see cref="LedgerId"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="range"/> depth exceeds <see cref="MaxRangeDepth"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(LedgerId ledger, KeyRange range, LocalStreamName stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = EncodedLength(range, stream);
        var result = new byte[length];
        var written = Write(ledger, range, stream, result);
        Debug.Assert(written == length, "Write writes the exact EncodedLength.");
        return result;
    }

    /// <summary>Writes the ledger-local row key into <paramref name="destination"/> and returns the number of bytes written.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="ledger"/> is default, or <paramref name="destination"/> is shorter than <see cref="EncodedLength"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="range"/> depth exceeds <see cref="MaxRangeDepth"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static int Write(LedgerId ledger, KeyRange range, LocalStreamName stream, Span<byte> destination)
    {
        if (ledger == default)
        {
            throw new ArgumentException("Ledger ID must be non-zero.", nameof(ledger));
        }

        ArgumentNullException.ThrowIfNull(stream);
        var prefixLength = PrefixByteLength(range.Depth);
        var streamBytes = stream.Span;
        var totalLength = prefixLength + 1 + sizeof(ulong) + streamBytes.Length;
        if (destination.Length < totalLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Destination span of {destination.Length} bytes is shorter than the {totalLength}-byte ledger-local key."),
                nameof(destination));
        }

        var prefix = range.Prefix;
        prefix.CopyTo(destination);
        destination[prefix.Length..prefixLength].Clear();
        destination[prefixLength] = DisjointSentinel;
        BinaryPrimitives.WriteUInt64BigEndian(destination[(prefixLength + 1)..], ledger.Value);
        streamBytes.CopyTo(destination[(prefixLength + 1 + sizeof(ulong))..]);
        Debug.Assert(range.Contains(destination[..totalLength]), "Every LedgerLocalKey lies inside its ledger's range.");
        return totalLength;
    }

    /// <summary>
    /// Validates <paramref name="key"/> as a ledger-local key for <paramref name="ledger"/> over
    /// <paramref name="range"/>, outputting the raw ASCII stream name bytes when valid.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when valid; otherwise the <see cref="InvalidDataException"/> naming the first rule broken.
    /// </returns>
    public static InvalidDataException? Validate(ReadOnlySpan<byte> key, KeyRange range, LedgerId ledger, out ReadOnlySpan<byte> streamName)
    {
        streamName = default;
        if (ledger == default)
        {
            return DecodeRejection.Create(ReasonLedgerMismatch, "Ledger ID must be non-zero.");
        }

        if (range.Depth > MaxRangeDepth)
        {
            return DecodeRejection.Create(
                ReasonRangeDepthTooDeep,
                string.Create(CultureInfo.InvariantCulture, $"Ledger range depth {range.Depth} exceeds LedgerLocalKey.MaxRangeDepth ({MaxRangeDepth})."));
        }

        var prefixLength = Math.Max(MinPrefixBytes, (range.Depth + 7) / 8);
        var headerLength = prefixLength + 1 + sizeof(ulong);
        if (key.Length < headerLength)
        {
            return DecodeRejection.Create(
                ReasonTruncated,
                string.Create(CultureInfo.InvariantCulture, $"Ledger-local key of {key.Length} bytes is shorter than the {headerLength}-byte header."));
        }

        // Exact prefix bytes (including zeroed unused low bits in range.Prefix) and zeroed padding bytes up to prefixLength.
        var rangePrefix = range.Prefix;
        if (!key[..rangePrefix.Length].SequenceEqual(rangePrefix))
        {
            return DecodeRejection.Create(ReasonPrefixMismatch, "Ledger-local key prefix bytes do not match the ledger range.");
        }

        for (var i = rangePrefix.Length; i < prefixLength; i++)
        {
            if (key[i] != 0)
            {
                return DecodeRejection.Create(ReasonPrefixMismatch, "Ledger-local key prefix padding bytes past the range depth must be 0.");
            }
        }

        if (key[prefixLength] != DisjointSentinel)
        {
            return DecodeRejection.Create(
                ReasonBadSentinel,
                string.Create(CultureInfo.InvariantCulture, $"Ledger-local key sentinel at offset {prefixLength} is 0x{key[prefixLength]:x2}, expected 0xFF."));
        }

        var encodedLedger = BinaryPrimitives.ReadUInt64BigEndian(key[(prefixLength + 1)..]);
        if (encodedLedger != ledger.Value)
        {
            return DecodeRejection.Create(
                ReasonLedgerMismatch,
                string.Create(CultureInfo.InvariantCulture, $"Ledger-local key encodes ledger {encodedLedger}, expected {ledger.Value}."));
        }

        var candidate = key[headerLength..];
        if (candidate.IsEmpty)
        {
            return DecodeRejection.Create(ReasonEmptyStream, "Ledger-local key stream name is empty.");
        }

        if (candidate.Length > LocalStreamName.MaxLength)
        {
            return DecodeRejection.Create(
                ReasonStreamTooLong,
                string.Create(CultureInfo.InvariantCulture, $"Ledger-local key stream name of {candidate.Length} bytes exceeds {LocalStreamName.MaxLength}."));
        }

        if (!LocalStreamName.IsValid(candidate))
        {
            return DecodeRejection.Create(ReasonBadStream, "Ledger-local key stream name contains a byte outside ASCII [a-z0-9._-].");
        }

        streamName = candidate;
        return null;
    }

    /// <summary>
    /// Attempts to decode <paramref name="key"/> as a ledger-local key for <paramref name="ledger"/> over
    /// <paramref name="range"/>, returning <see langword="true"/> and the raw stream name bytes when valid.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> key, KeyRange range, LedgerId ledger, out ReadOnlySpan<byte> streamName) =>
        Validate(key, range, ledger, out streamName) is null;
}
