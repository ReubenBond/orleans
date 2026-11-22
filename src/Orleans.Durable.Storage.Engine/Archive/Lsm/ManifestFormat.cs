using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;

using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// The manifest's format, <c>CLAM</c> version 1 (archive §7): a 16-byte header, then the body.
/// </summary>
/// <remarks>
/// <para>
/// The header is the magic <c>CLAM</c>, then the version, the body's length, and the CRC-32C of the body, each a
/// little-endian <c>u32</c>. Every integer of the body is little-endian:
/// </para>
/// <code>
/// epoch              u64
/// drained below      u64, a ledger ID
/// watermark count    u32, then per entry in ascending ledger order:
///   ledger u64, watermark u64, complete u8 (0 or 1)
/// section count      u32, then per section in order of object name, then offset:
///   object           u8 length, then the name's ASCII bytes
///   offset u64, length u64
///   partition        u32 depth, then the ⌈depth / 8⌉ prefix bytes
///   min tag, max tag each u64 ledger, u64 slot, u32 index
///   tail             u64 offset, u64 length
/// </code>
/// <para>
/// The format is canonical: a value has one encoding, and decoding accepts only it. Every rejection is an
/// <see cref="InvalidDataException"/> from <see cref="DecodeRejection"/>, which carries the reason code of the first rule
/// the bytes break as a reader meets them (conventions.md §15): the header's rules in the order of their constants
/// below, then the body's, each integer field's rules checked as soon as it is read, with <see cref="BodyTruncated"/>
/// wherever the body ends inside a field. The shared manifest vectors and the fuzz target's reading of the rules share
/// this order.
/// </para>
/// <para>
/// A manifest of any size encodes and decodes: the encoding is written field by field across an
/// <see cref="ArcBufferWriter"/>'s pages, and the decoder reads a <see cref="ReadOnlySequence{T}"/>, such as an
/// <see cref="ArcBuffer"/>'s, in place.
/// </para>
/// </remarks>
internal static class ManifestFormat
{
    /// <summary>The format's version.</summary>
    public const uint Version = 1;

    /// <summary>The length of the header.</summary>
    public const int HeaderLength = 16;

    /// <summary>The bytes end before the header does.</summary>
    public const string Truncated = "truncated";

    /// <summary>The magic is not <c>CLAM</c>.</summary>
    public const string BadMagic = "bad-magic";

    /// <summary>The version is not 1.</summary>
    public const string BadVersion = "bad-version";

    /// <summary>The body's length is not the length the header gives.</summary>
    public const string LengthMismatch = "length-mismatch";

    /// <summary>The CRC-32C of the body is not the header's.</summary>
    public const string CrcMismatch = "crc-mismatch";

    /// <summary>A field ends past the body.</summary>
    public const string BodyTruncated = "body-truncated";

    /// <summary>The epoch is 0, which only the absent manifest has.</summary>
    public const string ZeroEpoch = "zero-epoch";

    /// <summary><c>DrainedBelow</c> is 0, which names no ledger.</summary>
    public const string ZeroDrainedBelow = "zero-drained-below";

    /// <summary>A watermark's ledger lies below <c>DrainedBelow</c>.</summary>
    public const string WatermarkBelowDrained = "watermark-below-drained";

    /// <summary>A watermark's ledger is not above the previous entry's.</summary>
    public const string WatermarksUnordered = "watermarks-unordered";

    /// <summary>A watermark lies past <c>2^63 - 1</c>.</summary>
    public const string SlotOutOfRange = "slot-out-of-range";

    /// <summary>A completion flag is neither 0 nor 1.</summary>
    public const string BadCompleteFlag = "bad-complete-flag";

    /// <summary>An entry is at slot 0 and incomplete, so it records nothing.</summary>
    public const string EmptyWatermark = "empty-watermark";

    /// <summary>The entry of <c>DrainedBelow</c> is complete, so <c>DrainedBelow</c> lies past it.</summary>
    public const string CompleteAtDrainedBelow = "complete-at-drained-below";

    /// <summary>A section's object name is not an object name.</summary>
    public const string BadObjectName = "bad-object-name";

    /// <summary>A section's bytes are empty or end past <c>2^63 - 1</c>.</summary>
    public const string BadSectionRange = "bad-section-range";

    /// <summary>A partition is deeper than a key's bits, or sets a bit past its depth.</summary>
    public const string BadPartition = "bad-partition";

    /// <summary>A tag's ledger is 0, its slot past <c>2^63 - 2</c>, or its index past <c>2^31 - 1</c>.</summary>
    public const string BadTag = "bad-tag";

    /// <summary>A section's lowest tag is above its highest.</summary>
    public const string TagsUnordered = "tags-unordered";

    /// <summary>A tail's bytes are empty or end past <c>2^63 - 1</c>.</summary>
    public const string BadTailRange = "bad-tail-range";

    /// <summary>A section's position is not after the previous section's.</summary>
    public const string SectionsUnordered = "sections-unordered";

    /// <summary>Bytes follow the last section.</summary>
    public const string TrailingBytes = "trailing-bytes";

    private const int TagLength = sizeof(ulong) + sizeof(ulong) + sizeof(uint);

    /// <summary>
    /// The fewest bytes a section takes: a one-character object name, its byte range, a partition of depth 0, its tags,
    /// and its tail's byte range.
    /// </summary>
    private const int MinSectionLength = 1 + 1 + sizeof(ulong) + sizeof(ulong) + sizeof(uint) + TagLength + TagLength + sizeof(ulong) + sizeof(ulong);

    /// <summary>
    /// Gets the magic, <c>CLAM</c>.
    /// </summary>
    internal static ReadOnlySpan<byte> Magic => "CLAM"u8;

    /// <summary>
    /// Encodes <paramref name="value"/>, whose epoch is at least 1, with the CRC-32C of the whole encoding, which its upload
    /// carries so the object store refuses any other bytes (archive §3).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>
    /// The encoding, which the caller owns, across as many pages as it takes, and its CRC-32C, header included, computed
    /// over the finished bytes.
    /// </returns>
    /// <exception cref="ArgumentException">The epoch is 0: only the absent manifest has it, and nothing encodes it.</exception>
    public static EncodedManifest Encode(ManifestValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Epoch == 0)
        {
            throw new ArgumentException("Only the absent manifest has epoch 0, and it has no encoding.", nameof(value));
        }

        var length = EncodedLength(value);
        using var writer = new ArcBufferWriter(length);

        // The header's place, which it fills once the body's CRC-32C is known.
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        writer.Write(header);
        WriteUInt64(writer, value.Epoch);
        WriteUInt64(writer, value.DrainedBelow.Value);
        WriteUInt32(writer, (uint)value.Watermarks.Count);
        foreach (var (ledger, watermark) in value.Watermarks)
        {
            WriteUInt64(writer, ledger.Value);
            WriteUInt64(writer, (ulong)watermark.Below.Value);
            WriteByte(writer, watermark.Complete ? (byte)1 : (byte)0);
        }

        WriteUInt32(writer, (uint)value.Sections.Length);
        foreach (var section in value.Sections)
        {
            WriteName(writer, section.Object);
            WriteUInt64(writer, (ulong)section.Offset);
            WriteUInt64(writer, (ulong)section.Length);
            WriteUInt32(writer, (uint)section.Partition.Depth);
            writer.Write(section.Partition.Prefix);
            WriteTag(writer, section.MinTag);
            WriteTag(writer, section.MaxTag);
            WriteUInt64(writer, (ulong)section.Tail.Offset);
            WriteUInt64(writer, (ulong)section.Tail.Length);
        }

        Debug.Assert(writer.Length == length, "The encoding takes the length computed for it.");
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)(length - HeaderLength));
        using (var written = writer.PeekSlice(length))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], ObjectStoreChecksums.Crc32C(written.AsReadOnlySequence().Slice(HeaderLength)));
        }

        writer.WriteAt(0, header);
        var encoded = writer.ConsumeSlice(length);
        return new EncodedManifest(encoded, ObjectStoreChecksums.Crc32C(encoded));
    }

    /// <summary>
    /// Decodes a manifest's bytes, which the caller keeps, reading them in place across their segments.
    /// </summary>
    /// <returns>The manifest's value, or the <see cref="InvalidDataException"/> naming the first rule the bytes break.</returns>
    public static ManifestDecoding Decode(ReadOnlySequence<byte> encoded)
    {
        if (encoded.Length < HeaderLength)
        {
            return DecodeRejection.Create(Truncated, string.Create(CultureInfo.InvariantCulture, $"The manifest holds {encoded.Length} bytes, fewer than its {HeaderLength}-byte header."));
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        encoded.Slice(0, HeaderLength).CopyTo(header);
        if (!header[..4].SequenceEqual(Magic))
        {
            return DecodeRejection.Create(BadMagic, "The manifest's magic is not CLAM.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != Version)
        {
            return DecodeRejection.Create(BadVersion, string.Create(CultureInfo.InvariantCulture, $"The manifest's version is {version}, not {Version}."));
        }

        var body = encoded.Slice(HeaderLength);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (bodyLength != body.Length)
        {
            return DecodeRejection.Create(LengthMismatch, string.Create(CultureInfo.InvariantCulture, $"The manifest's header gives a {bodyLength}-byte body, but {body.Length} bytes follow it."));
        }

        if (ObjectStoreChecksums.Crc32C(body) != BinaryPrimitives.ReadUInt32LittleEndian(header[12..]))
        {
            return DecodeRejection.Create(CrcMismatch, "The manifest's body does not match its CRC-32C.");
        }

        var reader = new BodyReader(body);
        return DecodeBody(ref reader);
    }

    private static ManifestDecoding DecodeBody(ref BodyReader reader)
    {
        if (!reader.TryUInt64(out var epoch))
        {
            return reader.Truncation("the epoch");
        }

        if (epoch == 0)
        {
            return DecodeRejection.Create(ZeroEpoch, "The manifest's epoch is 0, which only the absent manifest has.");
        }

        if (!reader.TryUInt64(out var drainedValue))
        {
            return reader.Truncation("DrainedBelow");
        }

        if (drainedValue == 0)
        {
            return DecodeRejection.Create(ZeroDrainedBelow, "The manifest's DrainedBelow is 0, which names no ledger.");
        }

        var drainedBelow = new LedgerId(drainedValue);
        if (DecodeWatermarks(ref reader, drainedBelow, out var watermarks) is { } watermarkRejection)
        {
            return watermarkRejection;
        }

        if (DecodeSections(ref reader, out var sections) is { } sectionRejection)
        {
            return sectionRejection;
        }

        if (reader.Remaining > 0)
        {
            return DecodeRejection.Create(TrailingBytes, string.Create(CultureInfo.InvariantCulture, $"The manifest's body runs {reader.Remaining} {(reader.Remaining == 1 ? "byte" : "bytes")} past its last section."));
        }

        return new ManifestValue(epoch, drainedBelow, watermarks, sections);
    }

    private static InvalidDataException? DecodeWatermarks(ref BodyReader reader, LedgerId drainedBelow, out ImmutableSortedDictionary<LedgerId, ManifestWatermark> watermarks)
    {
        watermarks = ImmutableSortedDictionary<LedgerId, ManifestWatermark>.Empty;
        if (!reader.TryUInt32(out var count))
        {
            return reader.Truncation("the watermark count");
        }

        var entries = ImmutableSortedDictionary.CreateBuilder<LedgerId, ManifestWatermark>();
        ulong previous = 0;
        for (var entry = 0u; entry < count; entry++)
        {
            if (!reader.TryUInt64(out var ledger))
            {
                return reader.Truncation("watermark", entry, "ledger");
            }

            if (ledger < drainedBelow.Value)
            {
                return DecodeRejection.Create(WatermarkBelowDrained, string.Create(CultureInfo.InvariantCulture, $"Watermark {entry} is of ledger {ledger}, below DrainedBelow {drainedBelow}."));
            }

            if (entry > 0 && ledger <= previous)
            {
                return DecodeRejection.Create(WatermarksUnordered, string.Create(CultureInfo.InvariantCulture, $"Watermark {entry} is of ledger {ledger}, not above the previous entry's {previous}."));
            }

            if (!reader.TryUInt64(out var slot))
            {
                return reader.Truncation("watermark", entry, "slot");
            }

            if (slot > long.MaxValue)
            {
                return DecodeRejection.Create(SlotOutOfRange, string.Create(CultureInfo.InvariantCulture, $"Watermark {entry} lies at slot {slot}, past 2^63 - 1."));
            }

            if (!reader.TryByte(out var complete))
            {
                return reader.Truncation("watermark", entry, "completion flag");
            }

            if (complete > 1)
            {
                return DecodeRejection.Create(BadCompleteFlag, string.Create(CultureInfo.InvariantCulture, $"Watermark {entry}'s completion flag is {complete}, neither 0 nor 1."));
            }

            if (slot == 0 && complete == 0)
            {
                return DecodeRejection.Create(EmptyWatermark, string.Create(CultureInfo.InvariantCulture, $"Watermark {entry}, of ledger {ledger}, is at slot 0 and incomplete, so it records nothing."));
            }

            if (complete == 1 && ledger == drainedBelow.Value)
            {
                return DecodeRejection.Create(CompleteAtDrainedBelow, string.Create(CultureInfo.InvariantCulture, $"Ledger {ledger} is complete, so DrainedBelow lies past it."));
            }

            entries.Add(new LedgerId(ledger), new ManifestWatermark(new Slot((long)slot), complete == 1));
            previous = ledger;
        }

        watermarks = entries.ToImmutable();
        return null;
    }

    private static InvalidDataException? DecodeSections(ref BodyReader reader, out ImmutableArray<SectionRef> sections)
    {
        sections = [];
        if (!reader.TryUInt32(out var count))
        {
            return reader.Truncation("the section count");
        }

        // A count the body cannot hold reserves no more than the sections the body could hold.
        var decoded = ImmutableArray.CreateBuilder<SectionRef>((int)Math.Min(count, reader.Remaining / MinSectionLength));
        SectionRef? previous = null;
        for (var index = 0u; index < count; index++)
        {
            if (DecodeSection(ref reader, index, previous?.Object, out var section) is { } rejection)
            {
                return rejection;
            }

            Debug.Assert(section is not null, "A section which decodes without a rejection is its value.");
            if (previous is not null && SectionRef.CompareByPosition(previous, section) >= 0)
            {
                return DecodeRejection.Create(SectionsUnordered, string.Create(CultureInfo.InvariantCulture, $"Section {index}, at offset {section.Offset} of {section.Object}, is not after the previous section."));
            }

            decoded.Add(section);
            previous = section;
        }

        sections = decoded.DrainToImmutable();
        return null;
    }

    /// <summary>
    /// Decodes section <paramref name="index"/>, reusing <paramref name="previousObject"/>, the previous section's object
    /// name, when the section names the same object, as consecutive sections of one object do.
    /// </summary>
    private static InvalidDataException? DecodeSection(ref BodyReader reader, uint index, ObjectName? previousObject, out SectionRef? section)
    {
        section = null;
        if (DecodeObjectName(ref reader, index, previousObject, out var objectName) is { } nameRejection)
        {
            return nameRejection;
        }

        if (!reader.TryUInt64(out var offset))
        {
            return reader.Truncation("section", index, "offset");
        }

        if (offset > long.MaxValue)
        {
            return DecodeRejection.Create(BadSectionRange, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s offset, {offset}, lies past 2^63 - 1."));
        }

        if (!reader.TryUInt64(out var length))
        {
            return reader.Truncation("section", index, "length");
        }

        if (!IsRange(offset, length))
        {
            return DecodeRejection.Create(BadSectionRange, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s bytes, {length} at offset {offset}, are empty or end past 2^63 - 1."));
        }

        if (DecodePartition(ref reader, index, out var partition) is { } partitionRejection)
        {
            return partitionRejection;
        }

        if (DecodeTag(ref reader, index, "lowest tag", out var minTag) is { } minRejection)
        {
            return minRejection;
        }

        if (DecodeTag(ref reader, index, "highest tag", out var maxTag) is { } maxRejection)
        {
            return maxRejection;
        }

        if (minTag > maxTag)
        {
            return DecodeRejection.Create(TagsUnordered, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s lowest tag is above its highest."));
        }

        if (!reader.TryUInt64(out var tailOffset))
        {
            return reader.Truncation("section", index, "tail's offset");
        }

        if (tailOffset > long.MaxValue)
        {
            return DecodeRejection.Create(BadTailRange, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s tail offset, {tailOffset}, lies past 2^63 - 1."));
        }

        if (!reader.TryUInt64(out var tailLength))
        {
            return reader.Truncation("section", index, "tail's length");
        }

        if (!IsRange(tailOffset, tailLength))
        {
            return DecodeRejection.Create(BadTailRange, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s tail, {tailLength} bytes at offset {tailOffset}, is empty or ends past 2^63 - 1."));
        }

        section = new SectionRef(objectName!, (long)offset, (long)length, partition, minTag, maxTag, new ObjectRange((long)tailOffset, (long)tailLength));
        return null;
    }

    private static InvalidDataException? DecodeObjectName(ref BodyReader reader, uint index, ObjectName? previous, out ObjectName? name)
    {
        name = null;
        Span<byte> bytes = stackalloc byte[byte.MaxValue];
        if (!reader.TryByte(out var length) || !reader.TryCopy(bytes[..length]))
        {
            return reader.Truncation("section", index, "object");
        }

        if (previous is not null && Ascii.Equals(bytes[..length], previous.Value))
        {
            name = previous;
            return null;
        }

        // Latin-1 keeps one character per byte, so a byte outside an object name's alphabet fails the name's check.
        var value = Encoding.Latin1.GetString(bytes[..length]);
        if (!ObjectName.IsValid(value))
        {
            return DecodeRejection.Create(BadObjectName, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s object, '{value}', is not an object name."));
        }

        name = new ObjectName(value);
        return null;
    }

    private static InvalidDataException? DecodePartition(ref BodyReader reader, uint index, out KeyRange partition)
    {
        partition = default;
        if (!reader.TryUInt32(out var depth))
        {
            return reader.Truncation("section", index, "partition depth");
        }

        if (depth > KeyRange.MaxDepth)
        {
            return DecodeRejection.Create(BadPartition, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s partition is {depth} bits deep, deeper than a key's {KeyRange.MaxDepth}."));
        }

        if (!reader.TryBytes((int)((depth + 7) / 8), out var prefixBytes))
        {
            return reader.Truncation("section", index, "partition prefix");
        }

        // A prefix which spans segments, which only a long one does, is read from a copy.
        ReadOnlySpan<byte> prefix = prefixBytes.IsSingleSegment ? prefixBytes.FirstSpan : prefixBytes.ToArray();

        // The last byte holds depth % 8 bits of the prefix in its high bits, and its low bits are 0.
        var used = (int)(depth % 8);
        if (used != 0 && (prefix[^1] & (0xff >> used)) != 0)
        {
            return DecodeRejection.Create(BadPartition, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s partition sets a bit past its depth, {depth}."));
        }

        partition = new KeyRange(prefix, (int)depth);
        return null;
    }

    private static InvalidDataException? DecodeTag(ref BodyReader reader, uint index, string which, out Tag tag)
    {
        tag = default;
        if (!reader.TryUInt64(out var ledger))
        {
            return reader.Truncation("section", index, which + "'s ledger");
        }

        if (ledger == 0)
        {
            return DecodeRejection.Create(BadTag, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s {which} is of ledger 0, which names no ledger."));
        }

        if (!reader.TryUInt64(out var slot))
        {
            return reader.Truncation("section", index, which + "'s slot");
        }

        if (slot > SectionRef.MaxTagSlot)
        {
            return DecodeRejection.Create(BadTag, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s {which} lies at slot {slot}, past 2^63 - 2, the last slot a ledger has."));
        }

        if (!reader.TryUInt32(out var row))
        {
            return reader.Truncation("section", index, which + "'s index");
        }

        if (row > int.MaxValue)
        {
            return DecodeRejection.Create(BadTag, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s {which} has index {row}, past 2^31 - 1."));
        }

        tag = new Tag(new LedgerId(ledger), new Slot((long)slot), (int)row);
        return null;
    }

    private static bool IsRange(ulong offset, ulong length) => offset <= long.MaxValue && length >= 1 && length <= long.MaxValue - offset;

    private static int EncodedLength(ManifestValue value)
    {
        var length = HeaderLength + sizeof(ulong) + sizeof(ulong) + sizeof(uint) + value.Watermarks.Count * (sizeof(ulong) + sizeof(ulong) + 1) + sizeof(uint);
        foreach (var section in value.Sections)
        {
            length = checked(length + 1 + section.Object.Value.Length + sizeof(ulong) + sizeof(ulong)
                + sizeof(uint) + section.Partition.Prefix.Length
                + TagLength + TagLength
                + sizeof(ulong) + sizeof(ulong));
        }

        return length;
    }

    private static void WriteByte(ArcBufferWriter writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.AdvanceWriter(1);
    }

    private static void WriteUInt64(ArcBufferWriter writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(sizeof(ulong)), value);
        writer.AdvanceWriter(sizeof(ulong));
    }

    private static void WriteUInt32(ArcBufferWriter writer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(sizeof(uint)), value);
        writer.AdvanceWriter(sizeof(uint));
    }

    private static void WriteName(ArcBufferWriter writer, ObjectName name)
    {
        var span = writer.GetSpan(1 + name.Value.Length);
        span[0] = (byte)name.Value.Length;
        var written = Encoding.ASCII.GetBytes(name.Value, span[1..]);
        writer.AdvanceWriter(1 + written);
    }

    private static void WriteTag(ArcBufferWriter writer, Tag tag)
    {
        WriteUInt64(writer, tag.Ledger.Value);
        WriteUInt64(writer, (ulong)tag.Slot.Value);
        WriteUInt32(writer, (uint)tag.Index);
    }

    /// <summary>
    /// Reads the body's fields in order, each only when it ends within the body, across the body's segments.
    /// </summary>
    private ref struct BodyReader(ReadOnlySequence<byte> body)
    {
        private SequenceReader<byte> _reader = new(body);

        public readonly long Remaining => _reader.Remaining;

        public bool TryByte(out byte value) => _reader.TryRead(out value);

        public bool TryUInt32(out uint value)
        {
            var read = _reader.TryReadLittleEndian(out int signed);
            value = unchecked((uint)signed);
            return read;
        }

        public bool TryUInt64(out ulong value)
        {
            var read = _reader.TryReadLittleEndian(out long signed);
            value = unchecked((ulong)signed);
            return read;
        }

        public bool TryCopy(scoped Span<byte> destination)
        {
            if (!_reader.TryCopyTo(destination))
            {
                return false;
            }

            _reader.Advance(destination.Length);
            return true;
        }

        public bool TryBytes(int length, out ReadOnlySequence<byte> bytes)
        {
            bytes = default;
            if (_reader.Remaining < length)
            {
                return false;
            }

            bytes = _reader.UnreadSequence.Slice(0, length);
            _reader.Advance(length);
            return true;
        }

        public readonly InvalidDataException Truncation(string field) =>
            DecodeRejection.Create(BodyTruncated, string.Create(CultureInfo.InvariantCulture, $"The manifest's body ends before {field}, at byte {_reader.Consumed} of {_reader.Length}."));

        public readonly InvalidDataException Truncation(string item, uint index, string field) =>
            Truncation(string.Create(CultureInfo.InvariantCulture, $"{item} {index}'s {field}"));
    }
}
