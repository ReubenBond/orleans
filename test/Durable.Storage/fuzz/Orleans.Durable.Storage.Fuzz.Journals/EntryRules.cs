using System.Buffers.Binary;
using System.IO.Hashing;

namespace Orleans.Durable.Storage.Fuzz.Journals;

/// <summary>
/// One reading of the entry record's rules (journals.md §7), written apart from the decoder over contiguous bytes: the
/// oracle of the <c>journals-entry</c> target and of the Journals tests' codec properties.
/// </summary>
/// <remarks>
/// It checks the rules in the format's order and names the first rule the bytes break by its reason code; otherwise it
/// gives the fields the bytes hold. A record's CRC-32C covers the row key it was written for, which the record does not
/// hold, so a fuzz input frames the two: the key's length (<c>u16</c>, little-endian), the key, then the record.
/// </remarks>
public static class EntryRules
{
    private const int KeyLengthField = sizeof(ushort);
    private const int MinLength = 6;
    private const int MaxBase = 32;
    private const long LastTick = 3_155_378_975_999_999_999;

    /// <summary>Returns a fuzz input of <paramref name="rowKey"/> and <paramref name="record"/>.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> rowKey, ReadOnlySpan<byte> record)
    {
        var input = new byte[KeyLengthField + rowKey.Length + record.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(input, checked((ushort)rowKey.Length));
        rowKey.CopyTo(input.AsSpan(KeyLengthField));
        record.CopyTo(input.AsSpan(KeyLengthField + rowKey.Length));
        return input;
    }

    /// <summary>Splits a fuzz input into its row key and record, or returns <see langword="false"/> when it frames no key.</summary>
    public static bool TrySplit(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> rowKey, out ReadOnlySpan<byte> record)
    {
        rowKey = default;
        record = default;
        if (input.Length < KeyLengthField)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(input);
        if (input.Length - KeyLengthField < length)
        {
            return false;
        }

        rowKey = input.Slice(KeyLengthField, length);
        record = input[(KeyLengthField + length)..];
        return true;
    }

    /// <summary>Reads <paramref name="record"/>, written for the row key <paramref name="rowKey"/>, as the format's rules give it.</summary>
    public static EntryReading Read(ReadOnlySpan<byte> rowKey, ReadOnlySpan<byte> record)
    {
        // 1. The CRC, the kind, and the base's length.
        if (record.Length < MinLength)
        {
            return EntryReading.Rejected("truncated");
        }

        // 2. The CRC-32C of the row key and the bytes after the CRC.
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Crc(rowKey, record))
        {
            return EntryReading.Rejected("crc-mismatch");
        }

        // 3. The kind.
        var kind = record[4];
        if (kind > 3)
        {
            return EntryReading.Rejected("unknown-kind");
        }

        // 4. The expected version: the base's length, the base, and the count.
        int baseLength = record[5];
        if (baseLength > MaxBase)
        {
            return EntryReading.Rejected("base-too-long");
        }

        var position = 6;
        if (record.Length - position < baseLength)
        {
            return EntryReading.Rejected("field-truncated");
        }

        var versionBase = record.Slice(position, baseLength).ToArray();
        position += baseLength;
        if (!TryReadCount(record[position..], out var count, out var countLength))
        {
            return EntryReading.Rejected("bad-varint");
        }

        position += countLength;

        // 5. A Delete's deletion time.
        long ticks = 0;
        if (kind == 2)
        {
            if (record.Length - position < 8)
            {
                return EntryReading.Rejected("field-truncated");
            }

            ticks = BinaryPrimitives.ReadInt64LittleEndian(record[position..]);
            if (ticks is < 0 or > LastTick)
            {
                return EntryReading.Rejected("bad-deletion-time");
            }

            position += 8;
        }

        // 6. The body.
        var body = record[position..];
        if (kind == 0 && body.IsEmpty)
        {
            return EntryReading.Rejected("empty-append");
        }

        if (kind is 2 or 3 && !body.IsEmpty)
        {
            return EntryReading.Rejected("trailing-bytes");
        }

        return EntryReading.Accepted(new RuleEntry(kind, versionBase, count, ticks, body.ToArray()));
    }

    /// <summary>
    /// Returns the fuzz input <paramref name="input"/> with its record's first 4 bytes replaced by the CRC-32C of its row
    /// key and the record's bytes after them, or <see langword="null"/> when it frames no key or its record is too short
    /// to hold a CRC.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (!TrySplit(input, out var rowKey, out var record) || record.Length < 4)
        {
            return null;
        }

        var bytes = input.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(input.Length - record.Length), Crc(rowKey, record));
        return bytes;
    }

    /// <summary>
    /// Reads an unsigned LEB128 count from <paramref name="bytes"/>: at most 10 bytes, its value within 64 bits, and in its
    /// shortest form, whose last byte is not zero unless it is the only byte.
    /// </summary>
    internal static bool TryReadCount(ReadOnlySpan<byte> bytes, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        var shift = 0;
        UInt128 accumulated = 0;
        for (var index = 0; index < bytes.Length && index < 10; index++)
        {
            var b = bytes[index];
            accumulated |= (UInt128)(b & 0x7f) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (accumulated > ulong.MaxValue || index > 0 && b == 0)
                {
                    return false;
                }

                value = (ulong)accumulated;
                length = index + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the CRC-32C of <paramref name="rowKey"/> followed by <paramref name="record"/>'s bytes after its CRC field.</summary>
    private static uint Crc(ReadOnlySpan<byte> rowKey, ReadOnlySpan<byte> record)
    {
        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(rowKey);
        crc.Append(record[4..]);
        return crc.GetCurrentHashAsUInt32();
    }
}

/// <summary>What the format's rules give for some bytes: the reason code of the first rule they break, or the entry they hold.</summary>
public sealed record EntryReading(string? Reason, RuleEntry? Entry)
{
    internal static EntryReading Rejected(string reason) => new(reason, null);

    internal static EntryReading Accepted(RuleEntry entry) => new(null, entry);
}

/// <summary>
/// An entry as the format's rules give it: its kind byte, its expected version's base and count, its deletion ticks, and
/// its payload.
/// </summary>
public sealed record RuleEntry(byte Kind, byte[] Base, ulong Count, long Ticks, byte[] Payload);
