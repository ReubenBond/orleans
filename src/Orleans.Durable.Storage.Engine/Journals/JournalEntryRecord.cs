using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// An encoded entry record of one journal (journals.md §7): the writing process encodes each entry once, computing its
/// CRC-32C over the row key and the record (JS-I5), and the record travels verbatim to the key's primary, which verifies
/// it and writes the entry's row. It owns a lease on its payload, which <see cref="Dispose"/> releases.
/// </summary>
/// <remarks>
/// The record's bytes are its head — the CRC-32C, the kind, the expected version, and a <c>Delete</c>'s deletion time —
/// then <see cref="Payload"/>, so encoding an entry copies no payload and allocates no head buffer: the primary copies
/// the payload once, into the batch (conventions.md §1). Only the key's primary writes an <c>Expired</c> (JS-I7), so
/// only Journals encodes one.
/// </remarks>
public readonly struct JournalEntryRecord : IDisposable
{
    internal const int MaxHeadLength = JournalEntry.MaxHeadLength;

    private readonly uint _crc32C;

    private JournalEntryRecord(JournalRowKey key, uint crc32C, ArcBuffer payload, JournalEntry entry)
    {
        Key = key;
        _crc32C = crc32C;
        Payload = payload;
        Entry = entry;
    }

    /// <summary>Gets the row key of the journal the record was written for, which its CRC-32C covers.</summary>
    public JournalRowKey Key { get; }

    /// <summary>Gets the record's fields.</summary>
    public JournalEntry Entry { get; }

    /// <summary>Gets the payload, whose lease the record owns; the default for every entry without one.</summary>
    public ArcBuffer Payload { get; }

    /// <summary>Gets the record's length in bytes: its head and its payload; 0 for the default record.</summary>
    public long Length => Entry.HeadLength + (long)Payload.Length;

    /// <summary>Gets the record's CRC-32C, its first four bytes.</summary>
    /// <exception cref="InvalidOperationException">The record is the default, which no encoding made.</exception>
    public uint Crc32C => Entry.HeadLength > 0 ? _crc32C : throw new InvalidOperationException("The default JournalEntryRecord holds no record.");

    /// <summary>
    /// Writes the record's head bytes (the CRC-32C, the kind, the expected version, and a <c>Delete</c>'s deletion time)
    /// into <paramref name="destination"/> and returns the number of bytes written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The record is the default, which no encoding made.</exception>
    internal int WriteHeadTo(Span<byte> destination)
    {
        if (Entry.HeadLength == 0)
        {
            throw new InvalidOperationException("The default JournalEntryRecord holds no record.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, _crc32C);
        var length = WriteHeadAfterCrc(destination, Entry.Kind, Entry.ExpectedVersion, Entry.Kind == JournalEntryKind.Delete ? Entry.DeletionTicks : null);
        Debug.Assert(length == Entry.HeadLength, "Written head length matches Entry.HeadLength.");
        return length;
    }

    /// <summary>
    /// Writes the bytes of the entry's row's body which precede the payload (journals.md §4.2): a <c>Delete</c>'s
    /// tombstone (8 bytes); empty for every other kind. Returns the slice written within <paramref name="destination"/>.
    /// </summary>
    internal ReadOnlySpan<byte> WriteRowBodyHead(Span<byte> destination)
    {
        if (Entry.Kind != JournalEntryKind.Delete)
        {
            return default;
        }

        if (Entry.HeadLength == 0)
        {
            throw new InvalidOperationException("The default JournalEntryRecord holds no record.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination, Entry.DeletionTicks);
        return destination[..JournalEntry.TombstoneLength];
    }

    /// <summary>Encodes an <c>Append</c> of <paramref name="payload"/> at <paramref name="expectedVersion"/>, retaining its own lease on it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is empty: an <c>Append</c> adds a non-empty payload (journals.md §2.1).</exception>
    /// <exception cref="InvalidOperationException"><paramref name="payload"/> has been released.</exception>
    public static JournalEntryRecord Append(JournalRowKey key, JournalVersion expectedVersion, ArcBuffer payload)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (payload.Length == 0)
        {
            throw new ArgumentException("An Append adds a non-empty payload.", nameof(payload));
        }

        return Encode(key, JournalEntryKind.Append, expectedVersion, deletionTicks: null, payload.Retain());
    }

    /// <summary>Encodes a <c>Replace</c> with <paramref name="snapshot"/>, possibly empty, at <paramref name="expectedVersion"/>, retaining its own lease on it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="snapshot"/> has been released.</exception>
    public static JournalEntryRecord Replace(JournalRowKey key, JournalVersion expectedVersion, ArcBuffer snapshot)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Encode(key, JournalEntryKind.Replace, expectedVersion, deletionTicks: null, snapshot.Retain());
    }

    /// <summary>Encodes a <c>Delete</c> at <paramref name="expectedVersion"/>, stamped with <paramref name="deletedAt"/> in UTC.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static JournalEntryRecord Delete(JournalRowKey key, JournalVersion expectedVersion, DateTimeOffset deletedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Encode(key, JournalEntryKind.Delete, expectedVersion, deletedAt.UtcTicks, payload: default);
    }

    /// <summary>
    /// Verifies an encoded record which arrived as bytes for the row key <paramref name="key"/>, as a forwarded entry
    /// does where it enters the primary (JS-I5), and returns it as a record which shares its payload's pages under a
    /// lease of its own; the caller keeps and disposes <paramref name="bytes"/>. A valid record allocates its head, which
    /// the returned record owns.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> with the record, which the caller disposes, when the bytes are a valid record for the key;
    /// otherwise the rejection <see cref="JournalEntry.Decode"/> names.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static InvalidDataException? Decode(JournalRowKey key, ArcBuffer bytes, out JournalEntryRecord record)
    {
        ArgumentNullException.ThrowIfNull(key);
        record = default;
        var sequence = bytes.AsReadOnlySequence();
        if (JournalEntry.Decode(key.Span, sequence, out var entry) is { } rejection)
        {
            return rejection;
        }

        Span<byte> crcBytes = stackalloc byte[JournalEntry.CrcLength];
        sequence.Slice(0, JournalEntry.CrcLength).CopyTo(crcBytes);
        var crc32C = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
        var payload = entry.PayloadLength == 0 ? default : bytes.Slice(entry.HeadLength);
        record = new JournalEntryRecord(key, crc32C, payload, entry);
        return null;
    }

    /// <summary>
    /// Writes the record's bytes (its head followed by its payload) into <paramref name="destination"/> and returns the
    /// number of bytes written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The record is the default, which no encoding made.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Length"/>.</exception>
    public int WriteTo(Span<byte> destination)
    {
        if (Entry.HeadLength == 0)
        {
            throw new InvalidOperationException("The default JournalEntryRecord holds no record.");
        }

        var total = Length;
        if (destination.Length < total)
        {
            throw new ArgumentException("The destination span is shorter than the encoded record.", nameof(destination));
        }

        var written = WriteHeadTo(destination);
        written += Payload.CopyTo(destination[written..]);
        Debug.Assert(written == total, "Written record length matches Length.");
        return written;
    }

    /// <summary>Returns the record's bytes in one owned buffer, which the caller disposes.</summary>
    public ArcBuffer ToArcBuffer()
    {
        if (Entry.HeadLength == 0)
        {
            throw new InvalidOperationException("The default JournalEntryRecord holds no record.");
        }

        using var writer = new ArcBufferWriter(checked((int)Length));
        Span<byte> head = stackalloc byte[MaxHeadLength];
        var written = WriteHeadTo(head);
        writer.Write(head[..written]);
        Payload.CopyTo(writer);
        return writer.ConsumeSlice(writer.Length);
    }

    /// <summary>Releases the record's lease on its payload.</summary>
    public void Dispose() => Payload.Dispose();

    /// <summary>
    /// Encodes an <c>Expired</c> of the deleted state at <paramref name="expectedVersion"/> (journals.md §2.1, §8.6). Only
    /// the key's primary writes one (JS-I7).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    internal static JournalEntryRecord Expired(JournalRowKey key, JournalVersion expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Encode(key, JournalEntryKind.Expired, expectedVersion, deletionTicks: null, payload: default);
    }

    /// <summary>Returns a record which holds its own lease on the payload; the caller disposes it.</summary>
    internal JournalEntryRecord Retain() => new(Key, _crc32C, Payload.Retain(), Entry);

    /// <summary>Returns this record's key, CRC-32C, and fields without its payload, which the caller still owns.</summary>
    internal JournalEntryRecord WithoutPayload() => new(Key, _crc32C, default, Entry);

    /// <summary>Encodes a record of <paramref name="kind"/> which takes <paramref name="payload"/>'s lease, sealing it under <paramref name="key"/>.</summary>
    private static JournalEntryRecord Encode(JournalRowKey key, JournalEntryKind kind, JournalVersion expectedVersion, long? deletionTicks, ArcBuffer payload)
    {
        Debug.Assert(deletionTicks is null != (kind == JournalEntryKind.Delete), "A record carries a deletion time exactly when it is a Delete.");
        Span<byte> head = stackalloc byte[MaxHeadLength];
        var headLength = WriteHeadAfterCrc(head, kind, expectedVersion, deletionTicks);
        var crc32C = ComputeCrc(key, head[JournalEntry.CrcLength..headLength], payload);
        var entry = new JournalEntry(kind, expectedVersion, deletionTicks ?? 0, headLength: headLength, payloadLength: payload.Length);
        return new JournalEntryRecord(key, crc32C, payload, entry);
    }

    /// <summary>Writes the kind, the expected version, and a <c>Delete</c>'s deletion time starting at <see cref="JournalEntry.KindOffset"/> and returns the total head length.</summary>
    private static int WriteHeadAfterCrc(Span<byte> head, JournalEntryKind kind, JournalVersion expectedVersion, long? deletionTicks)
    {
        head[JournalEntry.KindOffset] = (byte)kind;
        var offset = JournalEntry.VersionOffset + expectedVersion.Write(head[JournalEntry.VersionOffset..]);
        if (deletionTicks is { } ticks)
        {
            BinaryPrimitives.WriteInt64LittleEndian(head[offset..], ticks);
            offset += JournalEntry.TombstoneLength;
        }

        return offset;
    }

    /// <summary>Computes the CRC-32C of the row key, the head's bytes after the CRC field, and then the payload.</summary>
    private static uint ComputeCrc(JournalRowKey key, ReadOnlySpan<byte> headAfterCrc, ArcBuffer payload)
    {
        using var crc = JournalCrc.Start();
        crc.Append(key.Span);
        crc.Append(headAfterCrc);
        foreach (var span in payload)
        {
            crc.Append(span);
        }

        return crc.Hash;
    }
}
