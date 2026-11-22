using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// An independent reading of the chunk layout's base, <c>CLAB</c> 1 (archive-logs.md §7.4), the oracle of the
/// <c>archive-log-chunk-base</c> target: a 60-byte header, the magic <c>CLAB</c>, version 1, the opener's kind (0 none,
/// 1 <c>Replace</c>, 2 <c>Delete</c>), flags whose bit 0 marks a tombstone, a reserved byte of 0, then the key hash, the
/// base rows, the tombstone, and the content's length as little-endian 64-bit integers, the content's MD5, and the
/// CRC-32C of the 56 bytes before it; then the content. A rule broken earlier in this order names the rejection.
/// </summary>
public static class ArchiveLogChunkBaseRules
{
    private const int Header = 60;

    /// <summary>Returns the first rule <paramref name="data"/> breaks, or none, with the fields the base holds.</summary>
    public static (string? Reason, (ulong KeyHash, int Kind, ulong? Tombstone, ulong Rows, long ContentLength) Base) Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < Header)
        {
            return ("truncated", default);
        }

        if (data[0] != (byte)'C' || data[1] != (byte)'L' || data[2] != (byte)'A' || data[3] != (byte)'B')
        {
            return ("bad-magic", default);
        }

        if (data[4] != 1)
        {
            return ("bad-version", default);
        }

        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(data[..56]);
        if (crc.GetCurrentHashAsUInt32() != BinaryPrimitives.ReadUInt32LittleEndian(data[56..]))
        {
            return ("bad-crc", default);
        }

        int kind = data[5];
        if (kind is not (0 or 1 or 2))
        {
            return ("bad-kind", default);
        }

        if (data[6] is not (0 or 1) || data[7] != 0)
        {
            return ("bad-flags", default);
        }

        var tombstone = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]);
        var flagged = data[6] == 1;
        if (flagged && kind != 2 || !flagged && tombstone != 0)
        {
            return ("tomb-mismatch", default);
        }

        var rows = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
        if (kind == 0 && rows != 0 || kind != 0 && (rows == 0 || rows >= 1UL << 35))
        {
            return ("bad-rows", default);
        }

        var length = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
        if (kind == 0 && length != 0)
        {
            return ("none-with-content", default);
        }

        if (length != (ulong)(data.Length - Header))
        {
            return ("bad-length", default);
        }

        Span<byte> md5 = stackalloc byte[16];
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
        {
            hash.AppendData(data[Header..]);
            _ = hash.GetHashAndReset(md5);
        }

        return md5.SequenceEqual(data[40..56])
            ? (null, (BinaryPrimitives.ReadUInt64LittleEndian(data[8..]), kind, flagged ? tombstone : null, rows, (long)length))
            : ("bad-md5", default);
    }
}
