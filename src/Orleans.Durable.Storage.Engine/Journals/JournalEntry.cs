using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// The fields of a decoded entry record (journals.md §7): its kind, its expected version, a <c>Delete</c>'s deletion
/// time, and the length of an <c>Append</c>'s or a <c>Replace</c>'s payload, the rest of the record.
/// </summary>
/// <remarks>
/// <para>
/// A record is the CRC-32C (<c>u32</c>) of its row key and then every byte of the record after the CRC; the kind
/// (<c>u8</c>); the expected version: the base's length (<c>u8</c>, at most 32), the base's bytes, and the count as a
/// shortest varint; a <c>Delete</c>'s deletion time in UTC ticks (<c>i64</c>); and the body: an <c>Append</c>'s payload,
/// non-empty, or a <c>Replace</c>'s snapshot, the rest of the record, and nothing for a <c>Delete</c> or an
/// <c>Expired</c>. Integers are little-endian, and each value has one encoding.
/// </para>
/// <para>
/// A record is never stored: the primary verifies it and writes the entry's row (journals.md §4.2), so it carries no
/// format version. The row key carries the journal key, which the record does not repeat, but its CRC-32C covers it, so
/// a record verifies only under the row key it was written for (JS-I5).
/// </para>
/// </remarks>
public readonly struct JournalEntry
{
    /// <summary>The most bytes before a record's payload: the CRC, kind, a longest expected version, and a deletion time.</summary>
    internal const int MaxHeadLength = VersionOffset + RowVersionCodec.MaxLength + TombstoneLength;

    /// <summary>The ticks of <see cref="DateTime.MaxValue"/>, the latest deletion time.</summary>
    internal const long MaxTicks = 3_155_378_975_999_999_999;

    /// <summary>The length of a <c>Delete</c> row's body, its tombstone: the deletion time's ticks (journals.md §7).</summary>
    internal const int TombstoneLength = sizeof(long);

    internal const int CrcLength = sizeof(uint);
    internal const int KindOffset = CrcLength;
    internal const int VersionOffset = KindOffset + 1;

    internal const string ReasonTruncated = "truncated";
    internal const string ReasonCrcMismatch = "crc-mismatch";
    internal const string ReasonUnknownKind = "unknown-kind";
    internal const string ReasonBaseTooLong = "base-too-long";
    internal const string ReasonFieldTruncated = "field-truncated";
    internal const string ReasonBadVarint = "bad-varint";
    internal const string ReasonBadDeletionTime = "bad-deletion-time";
    internal const string ReasonEmptyAppend = "empty-append";
    internal const string ReasonTrailingBytes = "trailing-bytes";

    /// <summary>The fewest bytes a record holds: its CRC, its kind, and its base's length.</summary>
    private const int MinLength = VersionOffset + 1;

    /// <summary>Initializes a new instance of the <see cref="JournalEntry"/> struct with an entry's fields, as its encoding writes them.</summary>
    internal JournalEntry(JournalEntryKind kind, JournalVersion expectedVersion, long deletionTicks, int headLength, long payloadLength)
    {
        Kind = kind;
        ExpectedVersion = expectedVersion;
        DeletionTicks = deletionTicks;
        HeadLength = headLength;
        PayloadLength = payloadLength;
    }

    /// <summary>Gets the entry's kind.</summary>
    public JournalEntryKind Kind { get; }

    /// <summary>Gets the version of the state the entry applies to.</summary>
    public JournalVersion ExpectedVersion { get; }

    /// <summary>Gets a <c>Delete</c>'s deletion time in UTC ticks; 0 for every other kind.</summary>
    public long DeletionTicks { get; }

    /// <summary>Gets the length of the payload after the head: 0 for a <c>Delete</c> and an <c>Expired</c>.</summary>
    public long PayloadLength { get; }

    /// <summary>Gets the length of the record's bytes before its payload.</summary>
    internal int HeadLength { get; }

    /// <summary>Gets the kind of the entry's row (journals.md §4.2).</summary>
    internal RowKind RowKind => JournalEntryKinds.RowKindOf(Kind);

    /// <summary>Gets the length of the entry's row's body: its payload, a <c>Delete</c>'s tombstone, or nothing for an <c>Expired</c>.</summary>
    internal long RowBodyLength => Kind switch
    {
        JournalEntryKind.Delete => TombstoneLength,
        JournalEntryKind.Expired => 0,
        JournalEntryKind.Append or JournalEntryKind.Replace => PayloadLength,
        _ => throw new UnreachableException("An entry's kind is defined."),
    };

    /// <summary>
    /// Verifies the whole entry record in <paramref name="record"/>, written for the row key <paramref name="rowKey"/>,
    /// which the caller keeps and which are read only during the call, and gives its fields (journals.md §7). It allocates
    /// nothing once its thread has decoded or encoded one record, which allocates the thread's CRC-32C.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the record is valid; otherwise the <see cref="InvalidDataException"/> naming the first
    /// rule the bytes break, its reason code in <see cref="Exception.Data"/> under <see cref="DecodeRejection.ReasonKey"/>
    /// (conventions.md §15). The primary reports it as corruption (JS-I5).
    /// </returns>
    public static InvalidDataException? Decode(ReadOnlySpan<byte> rowKey, ReadOnlySequence<byte> record, out JournalEntry entry)
    {
        entry = default;
        var length = record.Length;
        if (length < MinLength)
        {
            return DecodeRejection.Create(
                ReasonTruncated,
                string.Create(CultureInfo.InvariantCulture, $"The entry record's {length} bytes do not hold its CRC-32C, kind, and base length, {MinLength} bytes."));
        }

        Span<byte> head = stackalloc byte[MaxHeadLength];
        head = head[..(int)Math.Min(length, MaxHeadLength)];
        record.Slice(0, head.Length).CopyTo(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Crc(rowKey, record))
        {
            return DecodeRejection.Create(ReasonCrcMismatch, "The entry record's CRC-32C does not match its row key and bytes.");
        }

        var kind = (JournalEntryKind)head[KindOffset];
        if (!JournalEntryKinds.IsDefined(kind))
        {
            return DecodeRejection.Create(
                ReasonUnknownKind,
                string.Create(CultureInfo.InvariantCulture, $"The entry record has kind {head[KindOffset]}, which is not 0 to 3."));
        }

        if (ReadVersion(head[VersionOffset..], out var expected, out var versionLength) is { } versionRejection)
        {
            return versionRejection;
        }

        var offset = VersionOffset + versionLength;
        var ticks = 0L;
        if (kind == JournalEntryKind.Delete)
        {
            if (length - offset < TombstoneLength)
            {
                return DecodeRejection.Create(
                    ReasonFieldTruncated,
                    string.Create(CultureInfo.InvariantCulture, $"The Delete entry record ends {length - offset} bytes after its expected version, before its 8-byte deletion time."));
            }

            ticks = BinaryPrimitives.ReadInt64LittleEndian(head[offset..]);
            if (ticks is < 0 or > MaxTicks)
            {
                return DecodeRejection.Create(
                    ReasonBadDeletionTime,
                    string.Create(CultureInfo.InvariantCulture, $"The Delete entry record has deletion time {ticks}, not 0 to {MaxTicks} ticks."));
            }

            offset += TombstoneLength;
        }

        var bodyLength = length - offset;
        if (kind == JournalEntryKind.Append && bodyLength == 0)
        {
            return DecodeRejection.Create(ReasonEmptyAppend, "The Append entry record has an empty body.");
        }

        if (kind is JournalEntryKind.Delete or JournalEntryKind.Expired && bodyLength != 0)
        {
            return DecodeRejection.Create(
                ReasonTrailingBytes,
                string.Create(CultureInfo.InvariantCulture, $"The {kind} entry record has {bodyLength} bytes after its last field."));
        }

        entry = new JournalEntry(kind, expected, ticks, offset, bodyLength);
        return null;
    }

    /// <summary>Returns the CRC-32C of a record's row key and then its bytes after its CRC field.</summary>
    private static uint Crc(ReadOnlySpan<byte> rowKey, ReadOnlySequence<byte> record)
    {
        using var crc = JournalCrc.Start();
        crc.Append(rowKey);
        foreach (var segment in record.Slice(CrcLength))
        {
            crc.Append(segment.Span);
        }

        return crc.Hash;
    }

    /// <summary>Reads the expected version, checking its rules in layout order (journals.md §7, rule 4).</summary>
    private static InvalidDataException? ReadVersion(ReadOnlySpan<byte> bytes, out JournalVersion version, out int length)
    {
        var status = RowVersionCodec.Read(bytes, out var read, out length);
        version = JournalVersion.FromRowVersion(read);
        return status switch
        {
            RowVersionCodec.Status.Valid => null,
            RowVersionCodec.Status.BaseTooLong => DecodeRejection.Create(
                ReasonBaseTooLong,
                string.Create(CultureInfo.InvariantCulture, $"The entry record's expected version has a base of {bytes[0]} bytes, longer than 32.")),
            RowVersionCodec.Status.FieldTruncated => DecodeRejection.Create(
                ReasonFieldTruncated,
                string.Create(CultureInfo.InvariantCulture, $"The entry record ends within its expected version's {bytes[0]}-byte base.")),
            RowVersionCodec.Status.BadVarint => DecodeRejection.Create(
                ReasonBadVarint,
                "The entry record's expected version has a count which is not a shortest varint of at most 10 bytes which fits a u64 and ends within the record."),
            _ => throw new UnreachableException("The codec answers one of its statuses."),
        };
    }
}
