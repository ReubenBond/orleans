using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>What decoding a chunk layout's base gives (conventions.md §15): its header, or the rejection naming the rule it breaks.</summary>
internal readonly union ArchiveLogChunkBaseDecoding(ArchiveLogChunkBase, InvalidDataException);

/// <summary>
/// The header of a generation's base on the chunk layout (archive-logs.md §7.4, format <c>CLAB</c> 1): the key hash, the
/// opener which created the generation, none for a generation a key's first append run created, and the length and MD5
/// of the base's content, which follows the header.
/// </summary>
/// <param name="KeyHash">The key's xxHash64, which every reader checks against its log's.</param>
/// <param name="Opener">The opener's kind, tombstone, and rows, or <see langword="null"/> for a generation 1 a key's first append run created.</param>
/// <param name="ContentLength">The base's content length, in bytes.</param>
/// <param name="ContentMd5">The MD5 of the base's content, 16 bytes.</param>
internal sealed record ArchiveLogChunkBase(ulong KeyHash, ArchiveLogOpener? Opener, long ContentLength, byte[] ContentMd5)
{
    /// <summary>The header's length: the content starts here.</summary>
    internal const int HeaderLength = 60;

    internal const byte Version = 1;

    internal const string ReasonTruncated = "truncated";
    internal const string ReasonBadMagic = "bad-magic";
    internal const string ReasonBadVersion = "bad-version";
    internal const string ReasonBadCrc = "bad-crc";
    internal const string ReasonBadKind = "bad-kind";
    internal const string ReasonBadFlags = "bad-flags";
    internal const string ReasonTombMismatch = "tomb-mismatch";
    internal const string ReasonBadRows = "bad-rows";
    internal const string ReasonNoneWithContent = "none-with-content";
    internal const string ReasonBadLength = "bad-length";
    internal const string ReasonBadMd5 = "bad-md5";

    /// <summary>A base above generation 1 which records no opener, which the store rejects, knowing the generation.</summary>
    internal const string ReasonOpenerMissing = "opener-missing";

    private const byte KindNone = 0;
    private const byte KindReplace = 1;
    private const byte KindDelete = 2;
    private const byte TombstoneFlag = 1;

    private static ReadOnlySpan<byte> Magic => "CLAB"u8;

    /// <summary>
    /// Encodes the base of a generation of the key <paramref name="keyHash"/>: the header, then <paramref name="content"/>.
    /// </summary>
    /// <param name="keyHash">The key's xxHash64.</param>
    /// <param name="opener">The opener which created the generation, or <see langword="null"/> for a key's first append run's generation 1, whose base is empty.</param>
    /// <param name="content">The base's content.</param>
    /// <exception cref="ArgumentException">A generation no opener created has content.</exception>
    public static byte[] Encode(ulong keyHash, ArchiveLogOpener? opener, ReadOnlySequence<byte> content)
    {
        if (opener is null && !content.IsEmpty)
        {
            throw new ArgumentException("A generation a key's first append run created has an empty base.", nameof(content));
        }

        var encoded = new byte[checked(HeaderLength + (int)content.Length)];
        content.CopyTo(encoded.AsSpan(HeaderLength));
        var header = encoded.AsSpan(0, HeaderLength);
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = opener?.Kind switch
        {
            null => KindNone,
            ArchiveLogOpenerKind.Replace => KindReplace,
            ArchiveLogOpenerKind.Delete => KindDelete,
            _ => throw new ArgumentOutOfRangeException(nameof(opener)),
        };
        header[6] = opener?.Tombstone is null ? (byte)0 : TombstoneFlag;
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], keyHash);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], opener?.Rows ?? 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], opener?.Tombstone ?? 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)content.Length);
        using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
        {
            md5.AppendData(encoded, HeaderLength, encoded.Length - HeaderLength);
            _ = md5.GetHashAndReset(header[40..56]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, header[..56]));

        // A base is created once and kept, so one its decoder would reject could never be repaired.
        Debug.Assert(Decode(new ReadOnlySequence<byte>(encoded)).Value is ArchiveLogChunkBase decoded && decoded.KeyHash == keyHash && decoded.Opener == opener, "An encoded base decodes to its fields.");
        return encoded;
    }

    /// <summary>
    /// Decodes a whole base, its header and content, and checks the content against the MD5 the header records.
    /// </summary>
    /// <returns>The header, or the rejection naming the first rule the bytes break.</returns>
    public static ArchiveLogChunkBaseDecoding Decode(ReadOnlySequence<byte> encoded)
    {
        Span<byte> headerBytes = stackalloc byte[HeaderLength];
        var headerLength = (int)Math.Min(encoded.Length, HeaderLength);
        encoded.Slice(0, headerLength).CopyTo(headerBytes);
        var decoded = DecodeHeader(headerBytes[..headerLength], encoded.Length);
        if (decoded.Value is not ArchiveLogChunkBase header)
        {
            return decoded;
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var segment in encoded.Slice(HeaderLength))
        {
            md5.AppendData(segment.Span);
        }

        return md5.GetHashAndReset().AsSpan().SequenceEqual(header.ContentMd5)
            ? header
            : DecodeRejection.Create(ReasonBadMd5, "A chunk layout's base does not hash to the MD5 its header records.");
    }

    /// <summary>
    /// Returns whether <paramref name="header"/> is a well-formed header of a base no opener created, a key's first append
    /// run's, whatever its object's length.
    /// </summary>
    public static bool RecordsNoOpener(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderLength && DecodeHeader(header[..HeaderLength], HeaderLength).Value is ArchiveLogChunkBase { Opener: null };

    /// <summary>
    /// Decodes a base's header, the first <see cref="HeaderLength"/> bytes of an object of <paramref name="objectLength"/>
    /// bytes, without its content.
    /// </summary>
    /// <returns>The header, or the rejection naming the first rule the bytes break.</returns>
    public static ArchiveLogChunkBaseDecoding DecodeHeader(ReadOnlySpan<byte> header, long objectLength)
    {
        if (header.Length < HeaderLength || objectLength < HeaderLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"A chunk layout's base holds {objectLength} bytes, fewer than its {HeaderLength}-byte header."));
        }

        if (!header[..4].SequenceEqual(Magic))
        {
            return DecodeRejection.Create(ReasonBadMagic, "A chunk layout's base does not begin with CLAB.");
        }

        if (header[4] != Version)
        {
            return DecodeRejection.Create(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"A chunk layout's base is of version {header[4]}, not {Version}."));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header[56..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, header[..56]))
        {
            return DecodeRejection.Create(ReasonBadCrc, "A chunk layout's base header fails its CRC-32C.");
        }

        var kind = header[5];
        if (kind > KindDelete)
        {
            return DecodeRejection.Create(ReasonBadKind, string.Create(CultureInfo.InvariantCulture, $"A chunk layout's base records the opener kind {kind}, not 0, 1, or 2."));
        }

        if ((header[6] & ~TombstoneFlag) != 0 || header[7] != 0)
        {
            return DecodeRejection.Create(ReasonBadFlags, "A chunk layout's base sets a flag or a reserved byte its version defines none of.");
        }

        var hasTombstone = header[6] == TombstoneFlag;
        var tombstone = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        if (hasTombstone ? kind != KindDelete : tombstone != 0)
        {
            return DecodeRejection.Create(ReasonTombMismatch, "A chunk layout's base records a tombstone other than a Delete's, or a value with no tombstone.");
        }

        var rows = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if (kind == KindNone ? rows != 0 : rows is 0 or > ArchiveLogOpener.MaxRows)
        {
            return DecodeRejection.Create(ReasonBadRows, string.Create(CultureInfo.InvariantCulture, $"A chunk layout's base records {rows} base rows, against its opener kind {kind}."));
        }

        var contentLength = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        if (kind == KindNone && contentLength != 0)
        {
            return DecodeRejection.Create(ReasonNoneWithContent, "A chunk layout's base which no opener created has content.");
        }

        if (contentLength != (ulong)(objectLength - HeaderLength))
        {
            return DecodeRejection.Create(ReasonBadLength, string.Create(CultureInfo.InvariantCulture, $"A chunk layout's base records {contentLength} bytes of content in an object of {objectLength} bytes."));
        }

        ArchiveLogOpener? opener = kind switch
        {
            KindReplace => new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, rows),
            KindDelete => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, hasTombstone ? tombstone : null, rows),
            _ => null,
        };
        return new ArchiveLogChunkBase(BinaryPrimitives.ReadUInt64LittleEndian(header[8..]), opener, (long)contentLength, header[40..56].ToArray());
    }
}
