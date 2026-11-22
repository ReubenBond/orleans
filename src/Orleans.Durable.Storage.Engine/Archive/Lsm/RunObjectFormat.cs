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
/// The run object's format, <c>CLAR</c> version 1 (archive §7): every section's CRC-32C-checked data blocks, then each
/// section's block index and Bloom filter, then a trailer which lists the sections and records the object's reference
/// condition.
/// </summary>
/// <remarks>
/// <para>
/// Every integer is little-endian, and every part (a data block, a block index, a Bloom filter, the trailer) ends with
/// the CRC-32C of its own bytes before it:
/// </para>
/// <code>
/// row              key (u16 length, bytes), tag (u64 ledger, u64 slot, u32 index), kind u8,
///                  u32 payload length and bytes
/// data block       rows, CRC-32C
/// block index      u32 block count, each block's u32 length, u8 continuation flag, and first key, CRC-32C
/// Bloom filter     u8 hash count, the filter's bytes, CRC-32C
/// trailer          CLAR, u32 version, u32 section count, u64 metadata offset, then per section its u64 offset,
///                  u64 length, partition (u32 depth, prefix bytes), lowest and highest tags, u64 row count,
///                  u64 offsets of its block index and Bloom filter; the condition: u8 kind, then a drain's u64 ledger
///                  and u64 watermark, or a compaction's u32 input count and each input's object name (u8 length,
///                  ASCII) and u64 offset;
///                  u32 trailer length, CRC-32C
/// </code>
/// <para>
/// Every rejection is an <see cref="InvalidDataException"/> from <see cref="DecodeRejection"/> carrying the reason code of
/// the first rule the bytes break as a reader of the whole object meets them (archive §7): the trailer, then each
/// section's block index and Bloom filter, then each section's blocks and totals. A part's CRC-32C is checked before its
/// fields, each field is checked to end within its part (<see cref="BodyTruncated"/>) and then against its own rules as
/// soon as it is read, and a row's checks against its section follow its last field. The partial decoders,
/// <see cref="DecodeTrailerAt"/> and <see cref="DecodeMetadata"/>, check a prefix of those rules, and
/// <see cref="DecodeKey"/> and <see cref="DecodeRange"/> the rules of the rows they read.
/// </para>
/// </remarks>
internal static class RunObjectFormat
{
    /// <summary>The format's version.</summary>
    public const uint Version = 1;

    /// <summary>The fewest bytes a trailer holds: its magic, version, length, and CRC-32C.</summary>
    public const int MinTrailerLength = 16;

    /// <summary>
    /// The most bytes a trailer holds, 1 MiB: far above any trailer a writer writes, whose sections and inputs take tens of
    /// bytes to a few hundred each, so a reader which knows only an object's length never reads more for its trailer.
    /// </summary>
    public const int MaxTrailerLength = 1 << 20;

    /// <summary>
    /// The longest object a writer writes, <c>2^31 - 1</c> bytes; a section past it is a breach of the bound, which a
    /// reader reports as <see cref="InvalidOperationException"/> (archive §7).
    /// </summary>
    public const int MaxObjectLength = int.MaxValue;

    /// <summary>The trailer holds fewer than <see cref="MinTrailerLength"/> bytes.</summary>
    public const string Truncated = "truncated";

    /// <summary>The trailer's magic is not <c>CLAR</c>.</summary>
    public const string BadMagic = "bad-magic";

    /// <summary>The trailer's version is not 1.</summary>
    public const string BadVersion = "bad-version";

    /// <summary>The trailer's length is not its own, or does not fit the bytes which hold it.</summary>
    public const string LengthMismatch = "length-mismatch";

    /// <summary>A part's CRC-32C does not match its bytes.</summary>
    public const string CrcMismatch = "crc-mismatch";

    /// <summary>A field ends past its part.</summary>
    public const string BodyTruncated = "body-truncated";

    /// <summary>The trailer lists no section.</summary>
    public const string NoSections = "no-sections";

    /// <summary>The metadata, a block index, or a Bloom filter does not lie in order between the blocks and the trailer.</summary>
    public const string BadSectionLayout = "bad-section-layout";

    /// <summary>A section does not start where the previous one ended, or the last does not end at the metadata.</summary>
    public const string SectionsNotContiguous = "sections-not-contiguous";

    /// <summary>A section's blocks are empty or run past the metadata.</summary>
    public const string BadSectionRange = "bad-section-range";

    /// <summary>A partition is deeper than a key's bits, or sets a bit past its depth.</summary>
    public const string BadPartition = "bad-partition";

    /// <summary>A tag's ledger is 0, its slot past <c>2^63 - 2</c>, or its index past <c>2^31 - 1</c>.</summary>
    public const string BadTag = "bad-tag";

    /// <summary>A section's lowest tag lies above its highest.</summary>
    public const string TagsUnordered = "tags-unordered";

    /// <summary>A section counts no row.</summary>
    public const string NoRows = "no-rows";

    /// <summary>The reference condition's kind or fields are out of range.</summary>
    public const string BadCondition = "bad-condition";

    /// <summary>An input's object name is not an object name.</summary>
    public const string BadObjectName = "bad-object-name";

    /// <summary>An input does not lie after the previous one.</summary>
    public const string InputsUnordered = "inputs-unordered";

    /// <summary>A drain output's section holds tags of another ledger, or below the drain's watermark.</summary>
    public const string ConditionMismatch = "condition-mismatch";

    /// <summary>Bytes follow a part's last field.</summary>
    public const string TrailingBytes = "trailing-bytes";

    /// <summary>The block index lists no block.</summary>
    public const string NoBlocks = "no-blocks";

    /// <summary>A block holds fewer than 5 bytes, or the blocks do not fill their section.</summary>
    public const string BlocksMismatch = "blocks-mismatch";

    /// <summary>A continuation flag is neither 0 nor 1, or the first block's is 1.</summary>
    public const string BadContinuation = "bad-continuation";

    /// <summary>A key is empty.</summary>
    public const string EmptyKey = "empty-key";

    /// <summary>A block's first key lies below the previous block's.</summary>
    public const string KeysUnordered = "keys-unordered";

    /// <summary>A block's first key in the index lies outside its section's partition.</summary>
    public const string FirstKeyOutsidePartition = "first-key-outside-partition";

    /// <summary>The Bloom filter's hash count is out of range, or it holds no byte.</summary>
    public const string BadFilter = "bad-filter";

    /// <summary>A row's kind is not 0, 1, or 2.</summary>
    public const string UnknownKind = "unknown-kind";

    /// <summary>A block's first row does not have its first key in the index, or disagrees with its continuation flag.</summary>
    public const string IndexMismatch = "index-mismatch";

    /// <summary>A row does not lie after the previous row of its section.</summary>
    public const string RowsUnordered = "rows-unordered";

    /// <summary>A row's key lies outside its section's partition.</summary>
    public const string KeyOutsidePartition = "key-outside-partition";

    /// <summary>A row's tag lies outside its section's tags.</summary>
    public const string TagOutsideSpan = "tag-outside-span";

    /// <summary>The Bloom filter excludes a key of its section.</summary>
    public const string FilterMiss = "filter-miss";

    /// <summary>A section does not hold its row count.</summary>
    public const string RowCountMismatch = "row-count-mismatch";

    /// <summary>A section's rows' lowest or highest tag is not its trailer entry's.</summary>
    public const string TagSpanMismatch = "tag-span-mismatch";

    internal const int CrcLength = sizeof(uint);
    internal const int TagLength = sizeof(ulong) + sizeof(ulong) + sizeof(uint);

    /// <summary>The bytes a drain's condition takes in the trailer: its kind, ledger, and watermark.</summary>
    private const int DrainConditionLength = 1 + 2 * sizeof(ulong);

    /// <summary>The fewest bytes a block holds: a byte of a row, and its CRC-32C.</summary>
    internal const int MinBlockLength = CrcLength + 1;

    /// <summary>The fewest bytes a row takes: a one-byte key, its tag, kind, and payload length.</summary>
    internal const int MinRowLength = sizeof(ushort) + 1 + TagLength + 1 + sizeof(uint);

    /// <summary>The fewest bytes a trailer's section entry takes.</summary>
    private const int MinEntryLength = sizeof(ulong) + sizeof(ulong) + sizeof(uint) + TagLength + TagLength + 3 * sizeof(ulong);

    /// <summary>The fewest bytes a block index's entry takes: a length, a flag, and a one-byte key.</summary>
    private const int MinIndexEntryLength = sizeof(uint) + 1 + sizeof(ushort) + 1;

    /// <summary>The fewest bytes a compaction's input takes: a one-character name and its offset.</summary>
    private const int MinInputLength = 2 + sizeof(ulong);

    /// <summary>
    /// Gets the magic, <c>CLAR</c>.
    /// </summary>
    internal static ReadOnlySpan<byte> Magic => "CLAR"u8;

    /// <summary>
    /// Writes <paramref name="sections"/> as one run object whose trailer records <paramref name="condition"/>, under
    /// <paramref name="settings"/>, through a <see cref="RunObjectWriter"/> presized for the object.
    /// </summary>
    /// <returns>The object, which the caller owns, with its trailer.</returns>
    /// <exception cref="ArgumentException">There is no section, or <see cref="RunObjectWriter"/> refuses the input.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    /// <exception cref="InvalidOperationException">The object would pass <see cref="MaxObjectLength"/>, or does not read
    /// back as its input.</exception>
    public static EncodedRunObject Encode(ReadOnlySpan<RunSectionInput> sections, RunObjectCondition condition, RunSectionSettings settings)
    {
        if (sections.IsEmpty)
        {
            throw new ArgumentException("A run object holds at least one section.", nameof(sections));
        }

        using var writer = new RunObjectWriter(condition, settings, EstimatedLength(sections));
        foreach (var section in sections)
        {
            writer.BeginSection(section.Partition);
            foreach (var row in section.Rows.Span)
            {
                writer.Add(row);
            }

            writer.EndSection();
        }

        return writer.Complete();
    }

    /// <summary>
    /// Decodes a whole run object, reading its trailer from its last bytes and checking every part under every rule.
    /// </summary>
    /// <returns>The object, whose rows hold leases on <paramref name="content"/>'s pages, or the rejection.</returns>
    public static RunObjectDecoding Decode(ArcBuffer content)
    {
        var bytes = content.AsReadOnlySequence();
        var trailerDecoding = DecodeTrailerAt(bytes, 0);
        if (trailerDecoding.Value is not RunTrailer trailer)
        {
            return (InvalidDataException)trailerDecoding.Value;
        }

        var tailDecoding = DecodeMetadata(bytes.Slice(trailer.MetadataOffset, trailer.TrailerOffset - trailer.MetadataOffset), trailer);
        if (tailDecoding.Value is not RunTail tail)
        {
            return (InvalidDataException)tailDecoding.Value;
        }

        var sections = ImmutableArray.CreateBuilder<RunSection>(tail.Sections.Length);
        foreach (var metadata in tail.Sections)
        {
            var decoding = DecodeSectionCore(
                content.UnsafeSlice((int)metadata.Entry.Offset, (int)metadata.Entry.Length),
                bytes.Slice(metadata.Entry.Offset, metadata.Entry.Length),
                metadata);
            if (decoding.Value is not RunRows rows)
            {
                foreach (var section in sections)
                {
                    section.Dispose();
                }

                return (InvalidDataException)decoding.Value;
            }

            sections.Add(new RunSection(metadata, rows));
        }

        return new RunObject(tail, sections.MoveToImmutable());
    }

    /// <summary>
    /// Returns the trailer length an object's last 8 bytes record, or <see langword="null"/> when
    /// <paramref name="end"/>, the bytes which end an object, holds fewer than 8.
    /// </summary>
    public static uint? TrailerLength(ReadOnlySequence<byte> end) =>
        end.Length < 2 * sizeof(uint) ? null : ReadUInt32(end.Slice(end.Length - 2 * sizeof(uint), sizeof(uint)));

    /// <summary>
    /// Decodes the trailer which ends <paramref name="bytes"/>, the bytes of an object from <paramref name="offset"/> to
    /// its end: the whole object, or its tail.
    /// </summary>
    /// <returns>The trailer, or the rejection.</returns>
    public static RunTrailerDecoding DecodeTrailerAt(ReadOnlySequence<byte> bytes, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (bytes.Length < MinTrailerLength)
        {
            return DecodeRejection.Create(Truncated, string.Create(CultureInfo.InvariantCulture, $"The run object's last {bytes.Length} bytes are fewer than the {MinTrailerLength} of the smallest trailer."));
        }

        var length = TrailerLength(bytes)!.Value;
        if (length < MinTrailerLength || length > bytes.Length || length > MaxTrailerLength)
        {
            return DecodeRejection.Create(LengthMismatch, string.Create(CultureInfo.InvariantCulture, $"The run object's trailer gives its length as {length}, outside {MinTrailerLength} to the {bytes.Length} bytes which end the object, or past {MaxTrailerLength}."));
        }

        return DecodeTrailer(bytes.Slice(bytes.Length - length), offset + bytes.Length - length);
    }

    /// <summary>
    /// Decodes a run object's trailer, which lies at <paramref name="trailerOffset"/> in its object and ends it.
    /// </summary>
    /// <returns>The trailer, or the rejection.</returns>
    public static RunTrailerDecoding DecodeTrailer(ReadOnlySequence<byte> trailer, long trailerOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trailerOffset);
        if (trailer.Length < MinTrailerLength)
        {
            return DecodeRejection.Create(Truncated, string.Create(CultureInfo.InvariantCulture, $"The trailer holds {trailer.Length} bytes, fewer than its {MinTrailerLength} fixed bytes."));
        }

        Span<byte> header = stackalloc byte[2 * sizeof(uint)];
        trailer.Slice(0, header.Length).CopyTo(header);
        if (!header[..4].SequenceEqual(Magic))
        {
            return DecodeRejection.Create(BadMagic, "The trailer's magic is not CLAR.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != Version)
        {
            return DecodeRejection.Create(BadVersion, string.Create(CultureInfo.InvariantCulture, $"The trailer's version is {version}, not {Version}."));
        }

        var recordedLength = TrailerLength(trailer)!.Value;
        if (recordedLength != trailer.Length || recordedLength > MaxTrailerLength)
        {
            return DecodeRejection.Create(LengthMismatch, string.Create(CultureInfo.InvariantCulture, $"The trailer gives its length as {recordedLength}, not its {trailer.Length} bytes, or past {MaxTrailerLength}."));
        }

        var part = PartName.Trailer;
        if (CheckPart(trailer, part, out _) is { } crcRejection)
        {
            return crcRejection;
        }

        var reader = new FieldReader(trailer.Slice(header.Length, trailer.Length - MinTrailerLength), part);
        return CheckTrailerBody(ref reader, trailerOffset, out var value) is { } rejection ? rejection : value!;
    }

    /// <summary>
    /// Decodes every section's block index and Bloom filter from <paramref name="metadata"/>, the bytes of
    /// <paramref name="trailer"/>'s object from its metadata offset to the trailer.
    /// </summary>
    /// <returns>The object's tail, or the rejection.</returns>
    public static RunTailDecoding DecodeMetadata(ReadOnlySequence<byte> metadata, RunTrailer trailer)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        if (metadata.Length != trailer.TrailerOffset - trailer.MetadataOffset)
        {
            throw new ArgumentException("A run object's metadata runs from its metadata offset to its trailer.", nameof(metadata));
        }

        var sections = ImmutableArray.CreateBuilder<RunSectionMetadata>(trailer.Sections.Length);
        foreach (var entry in trailer.Sections)
        {
            var index = metadata.Slice(entry.IndexOffset - trailer.MetadataOffset, entry.FilterOffset - entry.IndexOffset);
            if (CheckIndex(index, entry, out var blockIndex) is { } indexRejection)
            {
                return indexRejection;
            }

            var filter = metadata.Slice(entry.FilterOffset - trailer.MetadataOffset, entry.FilterEnd - entry.FilterOffset);
            if (CheckFilter(filter, entry, out var bloomFilter) is { } filterRejection)
            {
                return filterRejection;
            }

            sections.Add(new RunSectionMetadata(entry, blockIndex!, bloomFilter!));
        }

        return new RunTail(trailer, sections.MoveToImmutable());
    }

    /// <summary>
    /// Decodes a whole section's blocks, <paramref name="blocks"/>, checking every block, row, and total.
    /// </summary>
    /// <returns>The section's rows, which hold leases on <paramref name="blocks"/>'s pages, or the rejection.</returns>
    public static RunRowsDecoding DecodeSection(ArcBuffer blocks, RunSectionMetadata section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (blocks.Length != section.Entry.Length)
        {
            throw new ArgumentException("A section's blocks are its entry's bytes.", nameof(blocks));
        }

        return DecodeSectionCore(blocks, blocks.AsReadOnlySequence(), section);
    }

    private static RunRowsDecoding DecodeSectionCore(ArcBuffer blocks, ReadOnlySequence<byte> bytes, RunSectionMetadata section)
    {
        // A section of valid rows holds its entry's row count, so the rows fill the buffer exactly.
        var rows = new RowBuffer((int)Math.Min(section.Entry.RowCount, (ulong)(blocks.Length / MinRowLength)));
        var cursor = new RowCursor(section, checkFilter: true);
        var sink = RowSink.Collect(rows);
        var rejection = CheckBlocks(blocks, bytes, section, 0, section.Index.Blocks.Length, cursor, in sink) ?? cursor.CheckTotals();
        if (rejection is not null)
        {
            rows.Release();
            return rejection;
        }

        return rows.ToRows();
    }

    /// <summary>
    /// Decodes the rows of <paramref name="key"/> from <paramref name="count"/> consecutive blocks of a section from
    /// <paramref name="first"/>, whose bytes <paramref name="blocks"/> holds back to back, checking each block's CRC-32C
    /// and every rule of each row it reads, through the first row past the key, where it stops.
    /// </summary>
    /// <returns>The key's rows in tag order, which hold leases on <paramref name="blocks"/>'s pages, or the rejection.</returns>
    public static RunRowsDecoding DecodeKey(ArcBuffer blocks, RunSectionMetadata section, int first, int count, ReadOnlyMemory<byte> key)
    {
        var rows = new RowBuffer(0);

        // The Bloom filter admitted the key before the read, so a key's read checks no filter.
        return DecodeBlocks(blocks, section, first, count, RowSink.Key(key, rows), rows, checkFilter: false);
    }

    /// <summary>
    /// Decodes the rows whose keys lie in <paramref name="range"/> from <paramref name="count"/> consecutive blocks of a
    /// section from <paramref name="first"/>, whose bytes <paramref name="blocks"/> holds back to back, checking each
    /// block's CRC-32C and every rule of each row it reads, the Bloom filter among them, through the first row past the
    /// range, where it stops. The section's totals need every block, which a caller reading the section in ranges checks
    /// with <see cref="SectionTotals"/>.
    /// </summary>
    /// <returns>The range's rows in order, which hold leases on <paramref name="blocks"/>'s pages, or the rejection.</returns>
    public static RunRowsDecoding DecodeRange(ArcBuffer blocks, RunSectionMetadata section, int first, int count, KeyRange range)
    {
        ArgumentNullException.ThrowIfNull(section);

        // The blocks' share of the section's rows, which the range's rows mostly fill.
        var estimate = (int)Math.Min(blocks.Length / MinRowLength, (long)Math.Ceiling((double)section.Entry.RowCount * blocks.Length / section.Entry.Length));
        var rows = new RowBuffer(estimate);
        return DecodeBlocks(blocks, section, first, count, RowSink.Range(range, rows), rows, checkFilter: true);
    }

    /// <summary>
    /// Checks <paramref name="count"/> consecutive blocks of <paramref name="section"/> from <paramref name="first"/>,
    /// passing each row to <paramref name="sink"/>, which collects into <paramref name="rows"/>.
    /// </summary>
    private static RunRowsDecoding DecodeBlocks(ArcBuffer blocks, RunSectionMetadata section, int first, int count, in RowSink sink, RowBuffer rows, bool checkFilter)
    {
        ArgumentNullException.ThrowIfNull(section);
        CheckBlockSpan(blocks, section, first, count);
        if (CheckBlocks(blocks, section, first, count, new RowCursor(section, checkFilter), in sink) is { } rejection)
        {
            rows.Release();
            return rejection;
        }

        return rows.ToRows();
    }

    /// <summary>
    /// Checks a whole run object as <see cref="Decode"/> does, feeding each row's fields to <paramref name="digest"/>
    /// without building rows: the writer's check of its own output.
    /// </summary>
    /// <returns>The object's tail, or the rejection.</returns>
    internal static RunTailDecoding Verify(ArcBuffer content, XxHash128 digest)
    {
        var bytes = content.AsReadOnlySequence();
        var trailerDecoding = DecodeTrailerAt(bytes, 0);
        if (trailerDecoding.Value is not RunTrailer trailer)
        {
            return trailerDecoding.Value is InvalidDataException trailerRejection
                ? trailerRejection
                : throw new UnreachableException("conventions.md §15: a decoding union holds a case.");
        }

        var tailDecoding = DecodeMetadata(bytes.Slice(trailer.MetadataOffset, trailer.TrailerOffset - trailer.MetadataOffset), trailer);
        if (tailDecoding.Value is not RunTail tail)
        {
            return tailDecoding;
        }

        var sink = RowSink.Digest(digest);
        foreach (var section in tail.Sections)
        {
            var sectionBytes = bytes.Slice(section.Entry.Offset, section.Entry.Length);
            var cursor = new RowCursor(section, checkFilter: true);
            if ((CheckBlocks(default, sectionBytes, section, 0, section.Index.Blocks.Length, cursor, in sink) ?? cursor.CheckTotals()) is { } rejection)
            {
                return rejection;
            }
        }

        return tail;
    }

    /// <summary>
    /// Appends a row's fields to <paramref name="digest"/>: its key, tag, kind, and payload, each with its length, so
    /// the writer's check compares the rows it was given with the rows it reads back.
    /// </summary>
    internal static void AppendRow(XxHash128 digest, ReadOnlySpan<byte> key, Tag tag, RowKind kind, ReadOnlySequence<byte> payload)
    {
        AppendRowHeader(digest, key, tag, kind, payload.Length);
        foreach (var segment in payload)
        {
            digest.Append(segment.Span);
        }
    }

    /// <summary>
    /// Appends a row's fields to <paramref name="digest"/> when the payload lies in one contiguous span.
    /// </summary>
    internal static void AppendRow(XxHash128 digest, ReadOnlySpan<byte> key, Tag tag, RowKind kind, ReadOnlySpan<byte> payload)
    {
        AppendRowHeader(digest, key, tag, kind, payload.Length);
        digest.Append(payload);
    }

    /// <summary>
    /// Appends a row's fields to <paramref name="digest"/>, reading <paramref name="payload"/> page by page without
    /// building a <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    internal static void AppendRow(XxHash128 digest, ReadOnlySpan<byte> key, Tag tag, RowKind kind, ArcBuffer payload)
    {
        AppendRowHeader(digest, key, tag, kind, payload.Length);
        foreach (var segment in payload)
        {
            digest.Append(segment);
        }
    }

    private static void AppendRowHeader(XxHash128 digest, ReadOnlySpan<byte> key, Tag tag, RowKind kind, long payloadLength)
    {
        Span<byte> fields = stackalloc byte[TagLength + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(fields, (ushort)key.Length);
        digest.Append(fields[..sizeof(ushort)]);
        digest.Append(key);
        BinaryPrimitives.WriteUInt64LittleEndian(fields, tag.Ledger.Value);
        BinaryPrimitives.WriteInt64LittleEndian(fields[8..], tag.Slot.Value);
        BinaryPrimitives.WriteInt32LittleEndian(fields[16..], tag.Index);
        fields[20] = (byte)kind;
        digest.Append(fields);
        BinaryPrimitives.WriteInt64LittleEndian(fields, payloadLength);
        digest.Append(fields[..sizeof(long)]);
    }

    /// <summary>Returns the bytes a row takes in a block.</summary>
    internal static long RowLength(Row row) => RowLength(row.Key.Length, row.Body.Length);

    /// <summary>Returns the bytes a row with <paramref name="keyLength"/> and <paramref name="bodyLength"/> takes in a block.</summary>
    internal static long RowLength(int keyLength, int bodyLength) =>
        sizeof(ushort) + keyLength + TagLength + 1 + sizeof(uint) + (long)bodyLength;

    /// <summary>
    /// Returns the most bytes a one-section drain output of <paramref name="partition"/> takes beyond its rows': its
    /// section's fixed parts and the trailer with a drain's condition.
    /// </summary>
    internal static long DrainOutputFixedLength(KeyRange partition) =>
        SectionFixedLength(partition.Prefix.Length) + TrailerFixedLength(DrainConditionLength);

    /// <summary>
    /// Returns the bytes a section of <paramref name="partition"/> takes beyond its rows' footprints: the block index's
    /// count and CRC-32C, the Bloom filter's hash count, last partial byte, and CRC-32C, and its entry in the trailer.
    /// </summary>
    internal static long SectionFixedLength(KeyRange partition) => SectionFixedLength(partition.Prefix.Length);

    /// <summary>
    /// Returns the bytes a compaction output's trailer takes beyond its sections' entries: its fixed fields and the
    /// condition naming <paramref name="inputs"/>, as the trailer's layout in the remarks gives them.
    /// </summary>
    internal static long CompactionTrailerFixedLength(ImmutableArray<SectionPosition> inputs)
    {
        // The condition's kind u8 and input count u32, then each input's name (a u8 length and its ASCII bytes, one per
        // character of an object name) and offset u64.
        var conditionLength = 1L + sizeof(uint);
        foreach (var input in inputs)
        {
            conditionLength += 1 + input.Object.Value.Length + sizeof(ulong);
        }

        return TrailerFixedLength(conditionLength);
    }

    /// <summary>
    /// Returns the most bytes <paramref name="row"/> adds to a section, whatever its place among the section's rows: its
    /// own, the CRC-32C of a block it may start, that block's index entry, and its key's Bloom filter bits at
    /// <paramref name="bloomBitsPerKey"/>.
    /// </summary>
    internal static long MaxRowFootprint(Row row, int bloomBitsPerKey) =>
        MaxRowFootprint(row.Key.Length, row.Body.Length, bloomBitsPerKey);

    internal static long MaxRowFootprint(int keyLength, int bodyLength, int bloomBitsPerKey) =>
        RowLength(keyLength, bodyLength) + CrcLength + MinIndexEntryLength - 1 + keyLength + (bloomBitsPerKey + 7) / 8;


    /// <summary>Gets the order of rows by key, compared bytewise, then by tag, <see cref="CompareRows"/>.</summary>
    internal static IComparer<TaggedRow> RowOrder { get; } = Comparer<TaggedRow>.Create(
        static (left, right) => CompareRows(left.Row.Key.Span, left.Tag, right.Row.Key.Span, right.Tag));

    /// <summary>
    /// Orders rows by key, compared bytewise, then by tag.
    /// </summary>
    internal static int CompareRows(ReadOnlySpan<byte> key, Tag tag, ReadOnlySpan<byte> otherKey, Tag otherTag)
    {
        var order = key.SequenceCompareTo(otherKey);
        return order != 0 ? order : tag.CompareTo(otherTag);
    }

    /// <summary>
    /// Returns the bytes a section whose partition's prefix holds <paramref name="prefixLength"/> bytes takes beyond its
    /// rows' footprints: the block index's count and CRC-32C, the Bloom filter's hash count, last partial byte, and CRC-32C,
    /// and its trailer entry (offset, length, partition, tags, row count, and its index's and filter's offsets).
    /// </summary>
    private static long SectionFixedLength(int prefixLength) =>
        2 * CrcLength + sizeof(uint)
        + 1 + 1 + CrcLength
        + 2 * sizeof(ulong) + sizeof(uint) + prefixLength + 2 * TagLength + sizeof(ulong) + 2 * sizeof(ulong);

    /// <summary>
    /// Returns the trailer's length beyond its sections' entries: its magic, version, section count, and metadata offset,
    /// then a condition of <paramref name="conditionLength"/> bytes, then its own length and CRC-32C.
    /// </summary>
    private static long TrailerFixedLength(long conditionLength) =>
        Magic.Length + sizeof(uint) + sizeof(uint) + sizeof(ulong) + conditionLength + sizeof(uint) + CrcLength;

    private static int EstimatedLength(ReadOnlySpan<RunSectionInput> sections)
    {
        // The rows, a CRC-32C per row at worst, and the metadata and trailer at a few hundred bytes per section.
        var length = 0L;
        foreach (var section in sections)
        {
            foreach (var row in section.Rows.Span)
            {
                length += RowLength(row.Row) + CrcLength + MinIndexEntryLength + row.Row.Key.Length;
            }

            length += 256;
        }

        return (int)Math.Min(length + 64, MaxObjectLength);
    }

    private static InvalidDataException? CheckTrailerBody(ref FieldReader reader, long trailerOffset, out RunTrailer? trailer)
    {
        trailer = null;
        if (!reader.TryUInt32(out var count))
        {
            return reader.Truncation("the section count");
        }

        if (count == 0)
        {
            return DecodeRejection.Create(NoSections, "The trailer lists no section.");
        }

        if (!reader.TryUInt64(out var metadataOffset))
        {
            return reader.Truncation("the metadata offset");
        }

        if (metadataOffset > (ulong)trailerOffset)
        {
            return DecodeRejection.Create(BadSectionLayout, string.Create(CultureInfo.InvariantCulture, $"The metadata offset, {metadataOffset}, lies past the trailer at {trailerOffset}."));
        }

        var layout = new Layout((long)metadataOffset, trailerOffset);
        var entries = ImmutableArray.CreateBuilder<RunSectionEntry>((int)Math.Min(count, reader.Remaining / MinEntryLength));
        for (var index = 0u; index < count; index++)
        {
            if (CheckSectionEntry(ref reader, index, ref layout, out var entry) is { } rejection)
            {
                return rejection;
            }

            entries.Add(entry!);
        }

        if (layout.BlocksEnd != layout.MetadataOffset)
        {
            return DecodeRejection.Create(SectionsNotContiguous, string.Create(CultureInfo.InvariantCulture, $"The last section's blocks end at {layout.BlocksEnd}, not at the metadata, {layout.MetadataOffset}."));
        }

        // Each Bloom filter runs to the next section's block index, and the last to the trailer.
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index] = entries[index] with { FilterEnd = index + 1 < entries.Count ? entries[index + 1].IndexOffset : trailerOffset };
        }

        var sections = entries.DrainToImmutable();
        if (CheckCondition(ref reader, sections, out var condition) is { } conditionRejection)
        {
            return conditionRejection;
        }

        if (reader.Remaining > 0)
        {
            return reader.Trailing();
        }

        trailer = new RunTrailer(layout.MetadataOffset, trailerOffset, trailerOffset + reader.PartLength + MinTrailerLength, sections, condition);
        return null;
    }

    private static InvalidDataException? CheckSectionEntry(ref FieldReader reader, uint index, ref Layout layout, out RunSectionEntry? entry)
    {
        entry = null;
        if (!reader.TryUInt64(out var offset))
        {
            return reader.Truncation("section", index, "offset");
        }

        if (offset != (ulong)layout.BlocksEnd)
        {
            return DecodeRejection.Create(SectionsNotContiguous, string.Create(CultureInfo.InvariantCulture, $"Section {index} starts at {offset}, not where the previous section's blocks ended, {layout.BlocksEnd}."));
        }

        if (!reader.TryUInt64(out var length))
        {
            return reader.Truncation("section", index, "length");
        }

        if (length == 0 || length > (ulong)(layout.MetadataOffset - layout.BlocksEnd))
        {
            return DecodeRejection.Create(BadSectionRange, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s {length} bytes of blocks at offset {offset} are empty or run past the metadata at {layout.MetadataOffset}."));
        }

        if (CheckPartition(ref reader, "section", index, out var partition) is { } partitionRejection)
        {
            return partitionRejection;
        }

        if (CheckTag(ref reader, "section", index, "lowest tag", out var minTag) is { } minRejection)
        {
            return minRejection;
        }

        if (CheckTag(ref reader, "section", index, "highest tag", out var maxTag) is { } maxRejection)
        {
            return maxRejection;
        }

        if (minTag > maxTag)
        {
            return DecodeRejection.Create(TagsUnordered, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s lowest tag lies above its highest."));
        }

        if (!reader.TryUInt64(out var rowCount))
        {
            return reader.Truncation("section", index, "row count");
        }

        if (rowCount == 0)
        {
            return DecodeRejection.Create(NoRows, string.Create(CultureInfo.InvariantCulture, $"Section {index} counts no row."));
        }

        if (CheckParts(ref reader, index, ref layout, out var indexOffset, out var filterOffset) is { } layoutRejection)
        {
            return layoutRejection;
        }

        layout.BlocksEnd = (long)(offset + length);
        entry = new RunSectionEntry((long)offset, (long)length, partition, minTag, maxTag, rowCount, indexOffset, filterOffset, 0);
        return null;
    }

    /// <summary>
    /// Checks the offsets of section <paramref name="index"/>'s block index and Bloom filter: the first section's block
    /// index starts the metadata, each later one starts at least 4 bytes after the previous section's Bloom filter, each
    /// Bloom filter at least 4 bytes after its block index, and every part fits before the trailer.
    /// </summary>
    private static InvalidDataException? CheckParts(ref FieldReader reader, uint index, ref Layout layout, out long indexOffset, out long filterOffset)
    {
        indexOffset = 0;
        filterOffset = 0;
        if (!reader.TryUInt64(out var indexValue))
        {
            return reader.Truncation("section", index, "block index offset");
        }

        var lowest = index == 0 ? (ulong)layout.MetadataOffset : (ulong)layout.FilterOffset + CrcLength;
        if (index == 0 && indexValue != lowest || indexValue < lowest || indexValue > (ulong)layout.TrailerOffset || (ulong)layout.TrailerOffset - indexValue < 2 * CrcLength)
        {
            return Layout(index, "block index", indexValue);
        }

        if (!reader.TryUInt64(out var filterValue))
        {
            return reader.Truncation("section", index, "Bloom filter offset");
        }

        if (filterValue < indexValue + CrcLength || filterValue > (ulong)layout.TrailerOffset || (ulong)layout.TrailerOffset - filterValue < CrcLength)
        {
            return Layout(index, "Bloom filter", filterValue);
        }

        indexOffset = (long)indexValue;
        filterOffset = (long)filterValue;
        layout.FilterOffset = filterOffset;
        return null;

        static InvalidDataException Layout(uint index, string part, ulong offset) =>
            DecodeRejection.Create(BadSectionLayout, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s {part} at {offset} does not lie in order between the blocks and the trailer, with at least 4 bytes for it and each part after it."));
    }

    private static InvalidDataException? CheckCondition(ref FieldReader reader, ImmutableArray<RunSectionEntry> sections, out RunObjectCondition condition)
    {
        condition = default;
        if (!reader.TryByte(out var kind))
        {
            return reader.Truncation("the condition's kind");
        }

        return kind switch
        {
            0 => CheckDrain(ref reader, sections, out condition),
            1 => CheckCompaction(ref reader, out condition),
            _ => DecodeRejection.Create(BadCondition, string.Create(CultureInfo.InvariantCulture, $"The condition's kind is {kind}, neither 0, a drain, nor 1, a compaction.")),
        };
    }

    private static InvalidDataException? CheckDrain(ref FieldReader reader, ImmutableArray<RunSectionEntry> sections, out RunObjectCondition condition)
    {
        condition = default;
        if (!reader.TryUInt64(out var ledger))
        {
            return reader.Truncation("the drain's ledger");
        }

        if (ledger == 0)
        {
            return DecodeRejection.Create(BadCondition, "The drain's ledger is 0, which names no ledger.");
        }

        if (!reader.TryUInt64(out var from))
        {
            return reader.Truncation("the drain's watermark");
        }

        if (from > long.MaxValue)
        {
            return DecodeRejection.Create(BadCondition, string.Create(CultureInfo.InvariantCulture, $"The drain's watermark, {from}, lies past 2^63 - 1."));
        }

        // A drain output of S from a holds rows of S at slot a or above, so every section's tags do.
        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            if (section.MinTag.Ledger.Value != ledger || section.MaxTag.Ledger.Value != ledger || (ulong)section.MinTag.Slot.Value < from)
            {
                return DecodeRejection.Create(ConditionMismatch, string.Create(CultureInfo.InvariantCulture, $"Section {index}'s tags lie outside the drain of ledger {ledger} from slot {from}."));
            }
        }

        condition = new RunObjectCondition.Drain(new LedgerId(ledger), new Slot((long)from));
        return null;
    }

    private static InvalidDataException? CheckCompaction(ref FieldReader reader, out RunObjectCondition condition)
    {
        condition = default;
        if (!reader.TryUInt32(out var count))
        {
            return reader.Truncation("the compaction's input count");
        }

        if (count == 0)
        {
            return DecodeRejection.Create(BadCondition, "The compaction names no input.");
        }

        var inputs = ImmutableArray.CreateBuilder<SectionPosition>((int)Math.Min(count, reader.Remaining / MinInputLength));
        Span<byte> name = stackalloc byte[byte.MaxValue];
        for (var index = 0u; index < count; index++)
        {
            if (!reader.TryByte(out var length) || !reader.TryCopy(name[..length]))
            {
                return reader.Truncation("input", index, "object name");
            }

            // Latin-1 keeps one character per byte, so a byte outside an object name's alphabet fails the name's check.
            var text = Encoding.Latin1.GetString(name[..length]);
            if (!ObjectName.IsValid(text))
            {
                return DecodeRejection.Create(BadObjectName, string.Create(CultureInfo.InvariantCulture, $"Input {index}'s object, '{text}', is not an object name."));
            }

            if (!reader.TryUInt64(out var offset))
            {
                return reader.Truncation("input", index, "offset");
            }

            if (offset > long.MaxValue)
            {
                return DecodeRejection.Create(BadCondition, string.Create(CultureInfo.InvariantCulture, $"Input {index}'s offset, {offset}, lies past 2^63 - 1."));
            }

            var position = new SectionPosition(new ObjectName(text), (long)offset);
            if (index > 0 && inputs[^1].CompareTo(position) >= 0)
            {
                return DecodeRejection.Create(InputsUnordered, string.Create(CultureInfo.InvariantCulture, $"Input {index}, at offset {offset} of {text}, is not after the previous input."));
            }

            inputs.Add(position);
        }

        condition = new RunObjectCondition.Compaction(inputs.DrainToImmutable());
        return null;
    }

    private static InvalidDataException? CheckIndex(ReadOnlySequence<byte> part, RunSectionEntry entry, out RunBlockIndex? index)
    {
        index = null;
        var name = PartName.Index;
        if (CheckPart(part, name, out var body) is { } crcRejection)
        {
            return crcRejection;
        }

        var reader = new FieldReader(body, name);
        if (!reader.TryUInt32(out var count))
        {
            return reader.Truncation("the block count");
        }

        if (count == 0)
        {
            return DecodeRejection.Create(NoBlocks, "The block index lists no block.");
        }

        var filled = 0L;
        var blocks = ImmutableArray.CreateBuilder<RunBlockEntry>((int)Math.Min(count, reader.Remaining / MinIndexEntryLength));
        ReadOnlySpan<byte> previous = default;
        for (var block = 0u; block < count; block++)
        {
            if (!reader.TryUInt32(out var length))
            {
                return reader.Truncation("block", block, "length");
            }

            if (length < MinBlockLength || length > entry.Length - filled)
            {
                return DecodeRejection.Create(BlocksMismatch, string.Create(CultureInfo.InvariantCulture, $"Block {block}'s {length} bytes are fewer than {MinBlockLength} or run past the section's {entry.Length} bytes of blocks, after {filled}."));
            }

            if (!reader.TryByte(out var continues))
            {
                return reader.Truncation("block", block, "continuation flag");
            }

            if (continues > 1 || block == 0 && continues == 1)
            {
                return DecodeRejection.Create(BadContinuation, string.Create(CultureInfo.InvariantCulture, $"Block {block}'s continuation flag is {continues}, which {(block == 0 ? "the first block's is not" : "is neither 0 nor 1")}."));
            }

            if (CheckKey(ref reader, "block", block, "first key", null, out var key) is { } keyRejection)
            {
                return keyRejection;
            }

            if (block > 0 && key.SequenceCompareTo(previous) < 0)
            {
                return DecodeRejection.Create(KeysUnordered, string.Create(CultureInfo.InvariantCulture, $"Block {block}'s first key lies below the previous block's."));
            }

            // A range's read locates its blocks by their first keys, so each must lie in the partition the rows do.
            if (!entry.Partition.Contains(key))
            {
                return DecodeRejection.Create(FirstKeyOutsidePartition, string.Create(CultureInfo.InvariantCulture, $"Block {block}'s first key lies outside the section's partition, {entry.Partition}."));
            }

            var firstKey = key.ToArray();
            blocks.Add(new RunBlockEntry(entry.Offset + filled, length, continues == 1, firstKey));
            filled += length;
            previous = firstKey;
        }

        if (filled != entry.Length)
        {
            return DecodeRejection.Create(BlocksMismatch, string.Create(CultureInfo.InvariantCulture, $"The blocks hold {filled} bytes, not the section's {entry.Length}."));
        }

        if (reader.Remaining > 0)
        {
            return reader.Trailing();
        }

        index = new RunBlockIndex(blocks.MoveToImmutable());
        return null;
    }

    private static InvalidDataException? CheckFilter(ReadOnlySequence<byte> part, RunSectionEntry entry, out RunBloomFilter? filter)
    {
        filter = null;
        var name = PartName.Filter;
        if (CheckPart(part, name, out var body) is { } crcRejection)
        {
            return crcRejection;
        }

        var reader = new FieldReader(body, name);
        if (!reader.TryByte(out var hashCount))
        {
            return reader.Truncation("the hash count");
        }

        if (hashCount is < 1 or > RunBloomFilter.MaxHashCount)
        {
            return DecodeRejection.Create(BadFilter, string.Create(CultureInfo.InvariantCulture, $"The Bloom filter records {hashCount} hashes, outside 1 to {RunBloomFilter.MaxHashCount}."));
        }

        if (reader.Remaining == 0)
        {
            return DecodeRejection.Create(BadFilter, "The Bloom filter holds no byte.");
        }

        _ = reader.TryBytes((int)reader.Remaining, out var bits);
        filter = new RunBloomFilter(hashCount, bits.ToArray());
        return null;
    }

    /// <summary>
    /// Checks that <paramref name="blocks"/> holds <paramref name="count"/> consecutive blocks of
    /// <paramref name="section"/> from <paramref name="first"/>, back to back: a caller's contract, not the bytes'.
    /// </summary>
    private static void CheckBlockSpan(ArcBuffer blocks, RunSectionMetadata section, int first, int count)
    {
        var entries = section.Index.Blocks;
        if (first < 0 || count < 1 || count > entries.Length - first)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The blocks are consecutive blocks of the section's index.");
        }

        var last = entries[first + count - 1];
        if (blocks.Length != last.Offset + last.Length - entries[first].Offset)
        {
            throw new ArgumentException("The blocks' bytes run from the first block's offset to the last block's end.", nameof(blocks));
        }
    }

    /// <summary>
    /// Checks <paramref name="count"/> consecutive blocks of <paramref name="section"/> from <paramref name="first"/>,
    /// whose bytes <paramref name="blocks"/> holds back to back, passing each row to <paramref name="sink"/> until it
    /// stops the read.
    /// </summary>
    private static InvalidDataException? CheckBlocks(ArcBuffer blocks, RunSectionMetadata section, int first, int count, RowCursor cursor, in RowSink sink) =>
        CheckBlocks(blocks, blocks.AsReadOnlySequence(), section, first, count, cursor, in sink);

    private static InvalidDataException? CheckBlocks(ArcBuffer blocks, ReadOnlySequence<byte> blocksBytes, RunSectionMetadata section, int first, int count, RowCursor cursor, in RowSink sink)
    {
        var entries = section.Index.Blocks;
        var start = entries[first].Offset;
        var remaining = blocksBytes;
        for (var block = first; block < first + count; block++)
        {
            var entry = entries[block];
            var blockBytes = remaining.Slice(0, entry.Length);
            remaining = remaining.Slice(entry.Length);
            var blockBuffer = sink.IsDigest ? default : blocks.UnsafeSlice((int)(entry.Offset - start), (int)entry.Length);
            if (CheckBlock(blockBuffer, blockBytes, section, block, cursor, in sink, out var stopped) is { } rejection)
            {
                return rejection;
            }

            if (stopped)
            {
                break;
            }
        }

        return null;
    }

    private static InvalidDataException? CheckBlock(ArcBuffer block, ReadOnlySequence<byte> bytes, RunSectionMetadata section, int number, RowCursor cursor, in RowSink sink, out bool stopped)
    {
        stopped = false;
        var name = PartName.Block(number);
        if (CheckPart(bytes, name, out var body) is { } crcRejection)
        {
            return crcRejection;
        }

        var reader = new FieldReader(body, name);
        for (var row = 0u; reader.Remaining > 0; row++)
        {
            if (CheckRow(ref reader, block, number, row, cursor, in sink, out stopped) is { } rejection)
            {
                return rejection;
            }

            if (stopped)
            {
                return null;
            }
        }

        return null;
    }

    private static InvalidDataException? CheckRow(
        ref FieldReader reader,
        ArcBuffer block,
        int number,
        uint index,
        RowCursor cursor,
        in RowSink sink,
        out bool stopped)
    {
        stopped = false;
        if (CheckKey(ref reader, "row", index, "key", cursor, out var key) is { } keyRejection)
        {
            return keyRejection;
        }

        if (CheckTag(ref reader, "row", index, "tag", out var tag) is { } tagRejection)
        {
            return tagRejection;
        }

        if (!reader.TryByte(out var kind))
        {
            return reader.Truncation("row", index, "kind");
        }

        if (kind > (byte)RowKind.Delete)
        {
            return DecodeRejection.Create(UnknownKind, string.Create(CultureInfo.InvariantCulture, $"Row {index} of block {number} is of kind {kind}, not 0, 1, or 2."));
        }

        if (!reader.TryUInt32(out var payloadLength))
        {
            return reader.Truncation("row", index, "payload length");
        }

        if (payloadLength > reader.Remaining)
        {
            return reader.Truncation("row", index, "payload");
        }

        if (sink.IsDigest && reader.TrySpan((int)payloadLength, out var payloadSpan))
        {
            if (cursor.CheckRow(key, tag, number, index, out var newKey) is { } digestRowRejection)
            {
                return digestRowRejection;
            }

            AppendRow(sink.DigestHash, key, tag, (RowKind)kind, payloadSpan);
            cursor.Advance(key, tag, newKey);
            return null;
        }

        var payloadOffset = reader.Consumed;
        if (!reader.TryBytes((int)payloadLength, out var payload))
        {
            return reader.Truncation("row", index, "payload");
        }

        if (cursor.CheckRow(key, tag, number, index, out var isNewKey) is { } rowRejection)
        {
            return rowRejection;
        }

        var view = new RunRowView(key, tag, (RowKind)kind, block, payload, (int)payloadOffset, (int)payloadLength);
        stopped = !sink.Accept(view);
        cursor.Advance(key, tag, isNewKey);
        return null;
    }

    private static InvalidDataException? CheckPart(ReadOnlySequence<byte> part, PartName name, out ReadOnlySequence<byte> body)
    {
        Debug.Assert(part.Length >= CrcLength, "Every part holds at least its CRC-32C.");
        body = part.Slice(0, part.Length - CrcLength);
        return ObjectStoreChecksums.Crc32C(body) == ReadUInt32(part.Slice(part.Length - CrcLength))
            ? null
            : DecodeRejection.Create(CrcMismatch, string.Create(CultureInfo.InvariantCulture, $"{name.Capitalized}'s CRC-32C does not match its bytes."));
    }

    /// <summary>
    /// Reads a key: its bytes in place when they lie in one segment, and otherwise a copy in <paramref name="cursor"/>'s
    /// scratch, or a new array without a cursor.
    /// </summary>
    private static InvalidDataException? CheckKey(ref FieldReader reader, string item, uint index, string field, RowCursor? cursor, out ReadOnlySpan<byte> key)
    {
        key = default;
        if (!reader.TryUInt16(out var length))
        {
            return reader.Truncation(item, index, field + "'s length");
        }

        if (length == 0)
        {
            return DecodeRejection.Create(EmptyKey, string.Create(CultureInfo.InvariantCulture, $"{reader.Part.Capitalized}'s {item} {index}'s {field} is empty."));
        }

        if (reader.TrySpan(length, out key))
        {
            return null;
        }

        if (!reader.TryBytes(length, out var bytes))
        {
            return reader.Truncation(item, index, field);
        }

        if (bytes.IsSingleSegment)
        {
            key = bytes.FirstSpan;
            return null;
        }

        var copy = cursor is null ? new byte[length] : cursor.KeyScratch(length);
        bytes.CopyTo(copy);
        key = copy.AsSpan(0, length);
        return null;
    }

    private static InvalidDataException? CheckPartition(ref FieldReader reader, string item, uint index, out KeyRange partition)
    {
        partition = default;
        if (!reader.TryUInt32(out var depth))
        {
            return reader.Truncation(item, index, "partition depth");
        }

        if (depth > KeyRange.MaxDepth)
        {
            return DecodeRejection.Create(BadPartition, string.Create(CultureInfo.InvariantCulture, $"The partition of {item} {index} is {depth} bits deep, deeper than a key's {KeyRange.MaxDepth}."));
        }

        if (!reader.TryBytes((int)((depth + 7) / 8), out var prefixBytes))
        {
            return reader.Truncation(item, index, "partition prefix");
        }

        ReadOnlySpan<byte> prefix = prefixBytes.IsSingleSegment ? prefixBytes.FirstSpan : prefixBytes.ToArray();

        // The last byte holds depth % 8 bits of the prefix in its high bits, and its low bits are 0.
        var used = (int)(depth % 8);
        if (used != 0 && (prefix[^1] & (0xff >> used)) != 0)
        {
            return DecodeRejection.Create(BadPartition, string.Create(CultureInfo.InvariantCulture, $"The partition of {item} {index} sets a bit past its depth, {depth}."));
        }

        partition = new KeyRange(prefix, (int)depth);
        return null;
    }

    private static InvalidDataException? CheckTag(ref FieldReader reader, string item, uint index, string field, out Tag tag)
    {
        tag = default;
        if (!reader.TryUInt64(out var ledger))
        {
            return reader.Truncation(item, index, field + "'s ledger");
        }

        if (ledger == 0)
        {
            return DecodeRejection.Create(BadTag, string.Create(CultureInfo.InvariantCulture, $"{reader.Part.Capitalized}'s {item} {index}'s {field} is of ledger 0, which names no ledger."));
        }

        if (!reader.TryUInt64(out var slot))
        {
            return reader.Truncation(item, index, field + "'s slot");
        }

        if (slot > SectionRef.MaxTagSlot)
        {
            return DecodeRejection.Create(BadTag, string.Create(CultureInfo.InvariantCulture, $"{reader.Part.Capitalized}'s {item} {index}'s {field} lies at slot {slot}, past 2^63 - 2, the last slot a ledger has."));
        }

        if (!reader.TryUInt32(out var row))
        {
            return reader.Truncation(item, index, field + "'s index");
        }

        if (row > int.MaxValue)
        {
            return DecodeRejection.Create(BadTag, string.Create(CultureInfo.InvariantCulture, $"{reader.Part.Capitalized}'s {item} {index}'s {field} has index {row}, past 2^31 - 1."));
        }

        tag = new Tag(new LedgerId(ledger), new Slot((long)slot), (int)row);
        return null;
    }

    private static uint ReadUInt32(ReadOnlySequence<byte> bytes)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        bytes.Slice(0, sizeof(uint)).CopyTo(value);
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    /// <summary>
    /// The layout a trailer's entries have laid out so far: where the last section's blocks end and its Bloom filter
    /// starts, between the metadata and the trailer.
    /// </summary>
    private record struct Layout(long MetadataOffset, long TrailerOffset)
    {
        public long BlocksEnd { get; set; }

        public long FilterOffset { get; set; }
    }

    /// <summary>
    /// Names a part for a rejection's message, which it formats only when a rule is broken.
    /// </summary>
    private readonly record struct PartName(int Kind, int Number)
    {
        public static PartName Trailer => new(0, 0);

        public string Capitalized => string.Concat(char.ToUpperInvariant(ToString()[0]).ToString(), ToString().AsSpan(1));

        public static PartName Index => new(1, 0);

        public static PartName Filter => new(2, 0);

        public static PartName Block(int block) => new(3, block);

        public override string ToString() => Kind switch
        {
            0 => "the trailer",
            1 => "the block index",
            2 => "the Bloom filter",
            _ => string.Create(CultureInfo.InvariantCulture, $"block {Number}"),
        };
    }

    /// <summary>
    /// The state of a read of one section's rows: the last row read, against which the next is ordered, the running
    /// totals a whole section's read checks, and scratch space for keys which lie across segments.
    /// </summary>
    private sealed class RowCursor(RunSectionMetadata section, bool checkFilter)
    {
        private byte[] _keyScratch = [];
        private byte[] _previousKey = [];
        private int _previousLength;
        private bool _hasPrevious;
        private Tag _previousTag;
        private SectionTotals _totals;

        private ReadOnlySpan<byte> PreviousKey => _previousKey.AsSpan(0, _previousLength);

        public byte[] KeyScratch(int length)
        {
            if (_keyScratch.Length < length)
            {
                _keyScratch = new byte[length];
            }

            return _keyScratch;
        }

        /// <summary>
        /// Checks a row against its section and the previous row: its block's first key and continuation, its order, its
        /// partition and tag span, and, unless the read is of one key, which the filter has admitted, the Bloom filter.
        /// </summary>
        public InvalidDataException? CheckRow(ReadOnlySpan<byte> key, Tag tag, int number, uint index, out bool newKey)
        {
            var keyOrder = _hasPrevious ? key.SequenceCompareTo(PreviousKey) : 1;
            newKey = keyOrder != 0;
            if (index == 0)
            {
                var block = section.Index.Blocks[number];
                if (!key.SequenceEqual(block.FirstKey.Span) || _hasPrevious && block.Continues == newKey)
                {
                    return DecodeRejection.Create(IndexMismatch, string.Create(CultureInfo.InvariantCulture, $"Block {number}'s first row's key is not the block's first key in the index, or its continuation flag is not whether that key continues the previous block's."));
                }
            }

            if (_hasPrevious && (keyOrder < 0 || keyOrder == 0 && tag.CompareTo(_previousTag) <= 0))
            {
                return DecodeRejection.Create(RowsUnordered, string.Create(CultureInfo.InvariantCulture, $"Row {index} of block {number} does not lie after the previous row, by key and then tag."));
            }

            var entry = section.Entry;
            if (!entry.Partition.Contains(key))
            {
                return DecodeRejection.Create(KeyOutsidePartition, string.Create(CultureInfo.InvariantCulture, $"Row {index} of block {number} has a key outside the section's partition, {entry.Partition}."));
            }

            if (tag < entry.MinTag || tag > entry.MaxTag)
            {
                return DecodeRejection.Create(TagOutsideSpan, string.Create(CultureInfo.InvariantCulture, $"Row {index} of block {number} has a tag outside the section's tags."));
            }

            if (checkFilter && newKey && !section.Filter.MayContain(XxHash3.HashToUInt64(key)))
            {
                return DecodeRejection.Create(FilterMiss, string.Create(CultureInfo.InvariantCulture, $"The Bloom filter excludes the key of row {index} of block {number}."));
            }

            return null;
        }

        /// <summary>Records a row which passed its checks as the previous row and in the totals.</summary>
        public void Advance(ReadOnlySpan<byte> key, Tag tag, bool newKey)
        {
            if (newKey)
            {
                if (_previousKey.Length < key.Length)
                {
                    _previousKey = new byte[Math.Max(key.Length, 2 * _previousKey.Length)];
                }

                key.CopyTo(_previousKey);
                _previousLength = key.Length;
            }

            _previousTag = tag;
            _hasPrevious = true;
            _totals.Add(tag);
        }

        /// <summary>Checks the section's totals once its last block is read.</summary>
        public InvalidDataException? CheckTotals() => _totals.Check(section.Entry);
    }

    /// <summary>
    /// A section's running totals over the rows a reader has checked, its row count and tag span, which must be its trailer
    /// entry's once every row is read (archive §7): a whole section's read checks them after its last block, and a
    /// compaction which reads a section in ranges checks them over all its reads.
    /// </summary>
    internal struct SectionTotals
    {
        private ulong _rowCount;
        private Tag _minTag;
        private Tag _maxTag;

        /// <summary>Records one row of the section.</summary>
        public void Add(Tag tag)
        {
            if (_rowCount == 0 || tag < _minTag)
            {
                _minTag = tag;
            }

            if (_rowCount == 0 || tag > _maxTag)
            {
                _maxTag = tag;
            }

            _rowCount++;
        }

        /// <summary>Records every row of <paramref name="rows"/>.</summary>
        public void Add(ReadOnlySpan<TaggedRow> rows)
        {
            foreach (var row in rows)
            {
                Add(row.Tag);
            }
        }

        /// <summary>
        /// Returns the rejection when the rows recorded are not <paramref name="entry"/>'s row count, or their lowest or
        /// highest tag is not its own: <c>row-count-mismatch</c> or <c>tag-span-mismatch</c>.
        /// </summary>
        public readonly InvalidDataException? Check(RunSectionEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (_rowCount != entry.RowCount)
            {
                return DecodeRejection.Create(RowCountMismatch, string.Create(CultureInfo.InvariantCulture, $"The section holds {_rowCount} rows, not its trailer entry's {entry.RowCount}."));
            }

            return _minTag != entry.MinTag || _maxTag != entry.MaxTag
                ? DecodeRejection.Create(TagSpanMismatch, "The section's lowest or highest row's tag is not its trailer entry's.")
                : null;
        }
    }

    /// <summary>
    /// Receives the rows a decoder reads, each once it passes its checks: it collects every row, collects one key's rows
    /// and stops at the first row past it, collects a range's rows and stops at the first row past it, or feeds every row
    /// to a digest.
    /// </summary>
    private readonly struct RowSink
    {
        private readonly Mode _mode;
        private readonly RowBuffer? _rows;
        private readonly ReadOnlyMemory<byte> _key;
        private readonly KeyRange _range;
        private readonly XxHash128? _digest;

        private RowSink(Mode mode, RowBuffer? rows, ReadOnlyMemory<byte> key, KeyRange range, XxHash128? digest)
        {
            _mode = mode;
            _rows = rows;
            _key = key;
            _range = range;
            _digest = digest;
        }

        private enum Mode
        {
            Collect,
            Key,
            Range,
            Digest,
        }

        public bool IsDigest => _mode == Mode.Digest;

        public XxHash128 DigestHash => _digest!;

        public static RowSink Collect(RowBuffer rows) => new(Mode.Collect, rows, default, default, null);

        public static RowSink Key(ReadOnlyMemory<byte> key, RowBuffer rows) => new(Mode.Key, rows, key, default, null);

        public static RowSink Range(KeyRange range, RowBuffer rows) => new(Mode.Range, rows, default, range, null);

        public static RowSink Digest(XxHash128 digest) => new(Mode.Digest, null, default, default, digest);

        /// <summary>Takes one row.</summary>
        /// <returns>Whether the decoder reads on.</returns>
        public bool Accept(scoped in RunRowView row)
        {
            if (_mode == Mode.Digest)
            {
                AppendRow(_digest!, row.Key, row.Tag, row.Kind, row.Payload);
                return true;
            }

            var order = _mode switch
            {
                Mode.Collect => 0,
                Mode.Key => row.Key.SequenceCompareTo(_key.Span),
                Mode.Range => KeyRangeOrder.Compare(row.Key, _range),
                Mode.Digest => throw new UnreachableException(),
                _ => throw new UnreachableException(),
            };
            if (order == 0)
            {
                _rows!.Add(row.ToTaggedRow());
            }

            return order <= 0;
        }
    }

    /// <summary>
    /// The rows a decoder collects, in an array which grows only past the capacity it starts at.
    /// </summary>
    private sealed class RowBuffer(int capacity)
    {
        private TaggedRow[] _rows = capacity == 0 ? [] : new TaggedRow[capacity];
        private int _count;

        public void Add(TaggedRow row)
        {
            if (_count == _rows.Length)
            {
                Array.Resize(ref _rows, Math.Max(4, 2 * _rows.Length));
            }

            _rows[_count++] = row;
        }

        /// <summary>Hands the rows over, in the buffer's own array.</summary>
        public RunRows ToRows() => new(_rows, _count);

        /// <summary>Releases the rows' payloads.</summary>
        public void Release()
        {
            foreach (var row in _rows.AsSpan(0, _count))
            {
                row.Row.Dispose();
            }

            _count = 0;
        }
    }

    /// <summary>
    /// Reads a part's fields in order, each only when it ends within the part, across the part's segments.
    /// </summary>
    private ref struct FieldReader(ReadOnlySequence<byte> body, PartName part)
    {
        private SequenceReader<byte> _reader = new(body);

        public readonly PartName Part { get; } = part;

        public readonly long Remaining => _reader.Remaining;

        public readonly long Consumed => _reader.Consumed;

        public readonly long PartLength => _reader.Length;

        public bool TryByte(out byte value) => _reader.TryRead(out value);

        public bool TryUInt16(out ushort value)
        {
            var read = _reader.TryReadLittleEndian(out short signed);
            value = unchecked((ushort)signed);
            return read;
        }

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

        public bool TrySpan(int length, out ReadOnlySpan<byte> span)
        {
            var current = _reader.CurrentSpan;
            var offset = _reader.CurrentSpanIndex;
            if (current.Length - offset >= length)
            {
                span = current.Slice(offset, length);
                _reader.Advance(length);
                return true;
            }

            span = default;
            return false;
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

        public bool TrySkip(uint length)
        {
            if (_reader.Remaining < length)
            {
                return false;
            }

            _reader.Advance(length);
            return true;
        }

        public readonly InvalidDataException Truncation(string field) =>
            DecodeRejection.Create(BodyTruncated, string.Create(CultureInfo.InvariantCulture, $"{Part.Capitalized} ends before {field}, at byte {_reader.Consumed} of {_reader.Length}."));

        public readonly InvalidDataException Truncation(string item, uint index, string field) =>
            DecodeRejection.Create(BodyTruncated, string.Create(CultureInfo.InvariantCulture, $"{Part.Capitalized} ends before {item} {index}'s {field}, at byte {_reader.Consumed} of {_reader.Length}."));

        public readonly InvalidDataException Trailing() =>
            DecodeRejection.Create(TrailingBytes, string.Create(CultureInfo.InvariantCulture, $"{Part.Capitalized} runs {_reader.Remaining} {(_reader.Remaining == 1 ? "byte" : "bytes")} past its last field."));
    }
}

/// <summary>
/// One row a decoder read and checked, lent to the decoder's row sink for the step which reads it: its key over the
/// decoder's bytes or scratch, and its payload as a slice of its block.
/// </summary>
internal readonly ref struct RunRowView
{
    private readonly ArcBuffer _block;
    private readonly int _payloadOffset;
    private readonly int _payloadLength;

    public RunRowView(ReadOnlySpan<byte> key, Tag tag, RowKind kind, ArcBuffer block, ReadOnlySequence<byte> payload, int payloadOffset, int payloadLength)
    {
        Key = key;
        Tag = tag;
        Kind = kind;
        _block = block;
        Payload = payload;
        _payloadOffset = payloadOffset;
        _payloadLength = payloadLength;
    }

    /// <summary>Gets the row's key.</summary>
    public ReadOnlySpan<byte> Key { get; }

    /// <summary>Gets the row's tag.</summary>
    public Tag Tag { get; }

    /// <summary>Gets the row's kind.</summary>
    public RowKind Kind { get; }

    /// <summary>Gets the row's payload, a slice of its block's bytes.</summary>
    public ReadOnlySequence<byte> Payload { get; }

    /// <summary>
    /// Returns the row as an owned <see cref="TaggedRow"/>, which copies the key and retains the payload.
    /// </summary>
    public TaggedRow ToTaggedRow() =>
        new(Tag, new Row(Key, Kind, _payloadLength == 0 ? default : _block.UnsafeSlice(_payloadOffset, _payloadLength)));
}
