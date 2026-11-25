using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;


namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A view of a decoded batch, <c>CLLB</c> version 1 (ledgers.md §4, §7): its segment state and its rows, in order, which
/// it lends as <see cref="BatchRow"/>s over a buffer it borrows. <see cref="Decode"/> verifies a batch's bytes by the batch
/// rules before lending a view of them; a <see cref="LedgerBatch"/> encodes one.
/// </summary>
/// <remarks>
/// <para>
/// A batch is the magic <c>CLLB</c>, the version 1 (<c>u32</c>), a flags byte (bit 0 the segment start), the segment
/// offset, the ordinal base, and the row count (each a varint), then the rows, then the CRC-32C of every byte before it
/// (<c>u32</c>). Each row is a flags byte (its kind in bits 0 and 1, <i>defining</i> in bit 2); a defining row's key
/// (a varint length and the bytes), its version before (a <c>u8</c> length and the base's bytes, then the count as a
/// varint), and its token (a <c>u8</c> length and the bytes); a referencing row's ordinal (a varint); and the body (a
/// varint length and the bytes). Each value has one encoding.
/// </para>
/// <para>
/// The buffer has one owner, which keeps it until it has finished with the view: a read's answer owns the verified bytes
/// and lends the view over them (ledgers.md §2.5; conventions.md §1). The view and the rows it lends are
/// <see langword="ref"/> structs, which keeps them off the heap and from crossing an <see langword="await"/>. Reading
/// through them after the owner releases the buffer is a use after release, which Debug builds' poisoned pages expose
/// (BUF-I4).
/// </para>
/// </remarks>
public readonly ref struct BatchView
{
    internal const string ReasonTruncated = "truncated";
    internal const string ReasonBadMagic = "bad-magic";
    internal const string ReasonBadVersion = "bad-version";
    internal const string ReasonCrcMismatch = "crc-mismatch";
    internal const string ReasonBadFlags = "bad-flags";
    internal const string ReasonBadVarint = "bad-varint";
    internal const string ReasonBadSegmentOffset = "bad-segment-offset";
    internal const string ReasonBadOrdinalBase = "bad-ordinal-base";
    internal const string ReasonNoRows = "no-rows";
    internal const string ReasonRowTruncated = "row-truncated";
    internal const string ReasonBadRowFlags = "bad-row-flags";
    internal const string ReasonBadChannel = "bad-channel";
    internal const string ReasonEmptyKey = "empty-key";
    internal const string ReasonKeyTooLong = "key-too-long";
    internal const string ReasonBaseTooLong = "base-too-long";
    internal const string ReasonTokenTooLong = "token-too-long";
    internal const string ReasonForwardOrdinal = "forward-ordinal";
    internal const string ReasonTrailingBytes = "trailing-bytes";
    internal const string ReasonUnknownOrdinal = "unknown-ordinal";

    /// <summary>The magic, version, and flags: the header's fixed bytes.</summary>
    internal const int FixedHeaderLength = 9;

    internal const int CrcLength = 4;

    internal const uint Version = 1;

    /// <summary>The batch flag of a segment's first batch.</summary>
    internal const byte StartFlag = 1;

    /// <summary>The row flag of a row which defines its key.</summary>
    internal const byte DefiningFlag = 4;

    /// <summary>A row's kind, in its flags' bits 0 and 1.</summary>
    internal const byte KindMask = 3;

    private const int MagicLength = 4;

    private const int VersionOffset = 4;

    private const int FlagsOffset = 8;
    private readonly int _rowsOffset;

    private BatchView(ArcBuffer buffer, bool isSegmentStart, ulong segmentOffset, ulong ordinalBase, int count, int rowsOffset)
    {
        Buffer = buffer;
        IsSegmentStart = isSegmentStart;
        LedgerSegmentOffset = segmentOffset;
        OrdinalBase = ordinalBase;
        Count = count;
        _rowsOffset = rowsOffset;
    }

    /// <summary>Gets a value indicating whether the batch begins a segment: it carries the start flag.</summary>
    public bool IsSegmentStart { get; }

    /// <summary>Gets the slots back to the batch's segment's first slot: 0 exactly when it begins a segment.</summary>
    public ulong LedgerSegmentOffset { get; }

    /// <summary>Gets the number of keys the batch's segment defined before it: 0 when it begins a segment.</summary>
    public ulong OrdinalBase { get; }

    /// <summary>Gets the number of rows; 0 only for a marker, which begins a segment.</summary>
    public int Count { get; }

    /// <summary>Gets the number of bytes of the batch, its CRC-32C included.</summary>
    public int Length => Buffer.Length;

    /// <summary>Gets the batch's bytes, which the view borrows.</summary>
    internal ArcBuffer Buffer { get; }

    private static ReadOnlySpan<byte> Magic => "CLLB"u8;

    /// <summary>Returns an enumerator which lends the rows in order, each valid only for the step which lends it.</summary>
    public Enumerator GetEnumerator() => new(Buffer, _rowsOffset, Count, OrdinalBase);

    /// <summary>
    /// Finds the batch's defining row of <paramref name="ordinal"/>, for a point read through a key index (ledgers.md
    /// §2.5, §8.4), which names a key's defining slot and ordinal.
    /// </summary>
    /// <param name="ordinal">The key's ordinal in its segment.</param>
    /// <param name="row">The defining row, valid while the batch's buffer is; otherwise the default.</param>
    /// <returns>
    /// <see langword="null"/> when the batch defines <paramref name="ordinal"/>; otherwise the
    /// <see cref="InvalidDataException"/> with reason <c>unknown-ordinal</c>, which the point-read path reports as
    /// corruption (ledgers.md §7).
    /// </returns>
    public InvalidDataException? FindDefining(ulong ordinal, out BatchRow row)
    {
        // The defining row of an ordinal at or above the base is the batch's defining row at its distance from the base,
        // counted without the implied ordinals, which wrap past the u64 maximum in a batch no segment rule has checked.
        if (ordinal >= OrdinalBase)
        {
            var remaining = ordinal - OrdinalBase;
            foreach (var candidate in this)
            {
                if (!candidate.IsDefining)
                {
                    continue;
                }

                if (remaining == 0)
                {
                    row = candidate;
                    return null;
                }

                remaining--;
            }
        }

        row = default;
        return DecodeRejection.Create(
            ReasonUnknownOrdinal,
            string.Create(CultureInfo.InvariantCulture, $"The batch defines no key of ordinal {ordinal}: its ordinal base is {OrdinalBase}."));
    }

    /// <summary>
    /// Lends a view of a batch which <see cref="Decode"/> already verified, without verifying it again.
    /// </summary>
    internal static BatchView OfVerified(ArcBuffer buffer)
    {
        var reader = new BatchReader(buffer, buffer.Length - CrcLength);
        Span<byte> header = stackalloc byte[FixedHeaderLength];
        var complete = reader.TryCopyTo(header);
        complete &= reader.TryReadVarint(out var offset) == Varint.Status.Valid;
        complete &= reader.TryReadVarint(out var ordinalBase) == Varint.Status.Valid;
        complete &= reader.TryReadVarint(out var count) == Varint.Status.Valid;
        Debug.Assert(complete && count <= int.MaxValue, "Decode verified the batch's header.");
        return new BatchView(buffer, (header[FlagsOffset] & StartFlag) != 0, offset, ordinalBase, (int)count, reader.Position);
    }

    /// <summary>
    /// Writes <paramref name="rows"/>, which the caller keeps, as one batch which begins a segment, into one owned buffer,
    /// which the caller owns and disposes: each key's first row defines it with the version <c>(empty, 0)</c> and the empty
    /// token, and its later rows reference it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is empty or holds a default <see cref="Row"/>.</exception>
    /// <exception cref="InvalidOperationException">A row's body has been released: the row was disposed.</exception>
    /// <exception cref="OverflowException">The batch would be longer than <see cref="int.MaxValue"/> bytes (conventions.md §8).</exception>
    internal static ArcBuffer Encode(ReadOnlySpan<Row> rows)
    {
        if (rows.IsEmpty)
        {
            throw new ArgumentException("A batch holds at least one row.", nameof(rows));
        }

        var length = (long)LedgerBatch.MaxHeaderLength(rows.Length);
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            ref readonly var row = ref rows[rowIndex];
            if (row.Key.IsEmpty)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"rows[{rowIndex}] is the default Row, which no constructor made."),
                    nameof(rows));
            }

            // Reading the body's length throws InvalidOperationException when the row's lease has been released.
            length += LedgerBatch.MaxRowLength(row.Key.Length, 0, 0, row.Body.Length);
        }

        if (length > int.MaxValue)
        {
            throw new OverflowException(string.Create(CultureInfo.InvariantCulture, $"A batch of these rows could be {length} bytes, longer than {int.MaxValue}."));
        }

        var dictionary = new LedgerSegmentDictionary();
        using var batch = new LedgerBatch(KeyRange.Root, (int)length, rows.Length, new LedgerBatchSegment(IsStart: true, Offset: 0, OrdinalBase: 0, dictionary), (int)length);
        foreach (ref readonly var row in rows)
        {
            _ = batch.AddRow(row, batch.HeldVersion(row.Key.Span), default);
        }

        return batch.Complete();
    }

    /// <summary>
    /// Verifies the whole batch in <paramref name="buffer"/>, which the caller keeps, by the batch rules, and lends a view
    /// of it (ledgers.md §7). It allocates nothing once its thread has decoded or encoded one batch, which allocates the
    /// thread's CRC-32C.
    /// </summary>
    /// <param name="buffer">The batch's bytes, in any division into pages.</param>
    /// <param name="view">The view of the batch, over <paramref name="buffer"/>, when the batch is valid; otherwise the default.</param>
    /// <returns>
    /// <see langword="null"/> when the batch is valid; otherwise the <see cref="InvalidDataException"/> naming the first
    /// rule the bytes break, its reason code in <see cref="Exception.Data"/> under <see cref="DecodeRejection.ReasonKey"/>
    /// (conventions.md §15). Its reader reports it as corruption (conventions.md §4).
    /// </returns>
    internal static InvalidDataException? Decode(ArcBuffer buffer, out BatchView view)
    {
        view = default;
        var length = buffer.Length;
        if (length < FixedHeaderLength + CrcLength)
        {
            return DecodeRejection.Create(
                ReasonTruncated,
                string.Create(CultureInfo.InvariantCulture, $"The batch's {length} bytes do not hold its {FixedHeaderLength} fixed header bytes and {CrcLength}-byte CRC-32C."));
        }

        var reader = new BatchReader(buffer, length - CrcLength);
        Span<byte> header = stackalloc byte[FixedHeaderLength];
        _ = reader.TryCopyTo(header);
        if (!header[..MagicLength].SequenceEqual(Magic))
        {
            return DecodeRejection.Create(ReasonBadMagic, "The batch's magic is not CLLB.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[VersionOffset..]);
        if (version != Version)
        {
            return DecodeRejection.Create(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"The batch has version {version}; only version {Version} is supported."));
        }

        if (!CrcMatches(buffer))
        {
            return DecodeRejection.Create(ReasonCrcMismatch, "The batch's CRC-32C does not match its bytes.");
        }

        var flags = header[FlagsOffset];
        if ((flags & ~StartFlag) != 0)
        {
            return DecodeRejection.Create(ReasonBadFlags, string.Create(CultureInfo.InvariantCulture, $"The batch's flags are 0x{flags:x2}; only bit 0 may be set."));
        }

        var isStart = flags == StartFlag;
        if (reader.TryReadVarint(out var offset) != Varint.Status.Valid)
        {
            return BadVarint("segment offset");
        }

        if (isStart != (offset == 0))
        {
            return DecodeRejection.Create(
                ReasonBadSegmentOffset,
                isStart
                    ? string.Create(CultureInfo.InvariantCulture, $"The batch begins a segment, and its segment offset is {offset}, not 0.")
                    : "The batch does not begin a segment, and its segment offset is 0.");
        }

        if (reader.TryReadVarint(out var ordinalBase) != Varint.Status.Valid)
        {
            return BadVarint("ordinal base");
        }

        if (isStart && ordinalBase != 0)
        {
            return DecodeRejection.Create(
                ReasonBadOrdinalBase,
                string.Create(CultureInfo.InvariantCulture, $"The batch begins a segment, and its ordinal base is {ordinalBase}, not 0."));
        }

        if (reader.TryReadVarint(out var count) != Varint.Status.Valid)
        {
            return BadVarint("row count");
        }

        if (count == 0 && !isStart)
        {
            return DecodeRejection.Create(ReasonNoRows, "The batch holds no rows, and only a marker, which begins a segment, holds none.");
        }

        var rowsOffset = reader.Position;
        var defining = 0UL;
        for (var rowIndex = 0UL; rowIndex < count; rowIndex++)
        {
            if (CheckRow(ref reader, rowIndex, ordinalBase, ref defining) is { } rejection)
            {
                return rejection;
            }
        }

        if (reader.Remaining != 0)
        {
            return DecodeRejection.Create(
                ReasonTrailingBytes,
                string.Create(CultureInfo.InvariantCulture, $"The batch's last counted row ends at byte {reader.Position}, and its CRC-32C starts at byte {length - CrcLength}."));
        }

        // Each row takes at least two bytes, so a count the bytes hold fits an int.
        view = new BatchView(buffer, isStart, offset, ordinalBase, checked((int)count), rowsOffset);
        return null;
    }

    /// <summary>Returns whether the batch's last 4 bytes are the CRC-32C of the bytes before them.</summary>
    internal static bool CrcMatches(ArcBuffer buffer)
    {
        using var crc = BatchCrc.Start();
        var covered = buffer.Length - CrcLength;
        Span<byte> stored = stackalloc byte[CrcLength];
        var filled = 0;
        foreach (var span in buffer)
        {
            var hashed = Math.Min(span.Length, covered);
            crc.Append(span[..hashed]);
            covered -= hashed;
            span[hashed..].CopyTo(stored[filled..]);
            filled += span.Length - hashed;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(stored) == crc.Hash;
    }

    /// <summary>Writes the header's fixed bytes: its magic, version, and flags.</summary>
    internal static void WriteFixedHeader(Span<byte> destination, bool isSegmentStart)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[VersionOffset..], Version);
        destination[FlagsOffset] = isSegmentStart ? StartFlag : (byte)0;
    }

    private static InvalidDataException BadVarint(string field) =>
        DecodeRejection.Create(
            ReasonBadVarint,
            string.Create(CultureInfo.InvariantCulture, $"The batch's {field} is not a varint which ends before the CRC-32C within {Varint.MaxLength} bytes, fits a u64, and is in its shortest form."));

    /// <summary>
    /// Checks one row's fields in layout order, each first to end at or before the CRC-32C and then against its own rule.
    /// </summary>
    private static InvalidDataException? CheckRow(ref BatchReader reader, ulong rowIndex, ulong ordinalBase, ref ulong defining)
    {
        if (!reader.TryRead(out byte flags))
        {
            return RowTruncation(rowIndex, "flags");
        }

        if ((flags & ~(KindMask | DefiningFlag)) != 0)
        {
            return DecodeRejection.Create(
                ReasonBadRowFlags,
                string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} has flags 0x{flags:x2}: a bit other than the kind and defining is set."));
        }

        var kind = (RowKind)(flags & KindMask);
        if (kind == RowKind.Message)
        {
            if ((flags & DefiningFlag) != 0)
            {
                return DecodeRejection.Create(
                    ReasonBadRowFlags,
                    string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} has flags 0x{flags:x2}: a Message row must not set the defining bit."));
            }

            if (!reader.TryRead(out byte channelLength))
            {
                return RowTruncation(rowIndex, "channel length");
            }

            if (channelLength is 0 or > MessageChannel.MaxLength)
            {
                return DecodeRejection.Create(
                    ReasonBadChannel,
                    string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex}'s channel length is {channelLength}; a message channel is 1 to {MessageChannel.MaxLength} bytes."));
            }

            Span<byte> channel = stackalloc byte[channelLength];
            if (!reader.TryCopyTo(channel))
            {
                return RowTruncation(rowIndex, "channel");
            }

            if (!MessageChannel.IsValid(channel))
            {
                return DecodeRejection.Create(
                    ReasonBadChannel,
                    string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex}'s channel contains an invalid byte: only ASCII [a-z0-9._-] is allowed."));
            }

            if (RowVarint(ref reader, rowIndex, "body length", out var messageBodyLength) is { } badMessageBodyLength)
            {
                return badMessageBodyLength;
            }

            return reader.TrySkip(messageBodyLength) ? null : RowTruncation(rowIndex, "body");
        }

        if ((flags & DefiningFlag) == 0)
        {
            if (RowVarint(ref reader, rowIndex, "ordinal", out var ordinal) is { } badOrdinal)
            {
                return badOrdinal;
            }

            // The ordinal must name a key defined before the row: below the base plus the batch's defining rows so far.
            if (ordinal >= ordinalBase && ordinal - ordinalBase >= defining)
            {
                return DecodeRejection.Create(
                    ReasonForwardOrdinal,
                    string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} references ordinal {ordinal}, which its segment has not defined before it: the ordinal base is {ordinalBase}, and the batch defined {defining} keys before the row."));
            }
        }
        else
        {
            if (RowVarint(ref reader, rowIndex, "key length", out var keyLength) is { } badKeyLength)
            {
                return badKeyLength;
            }

            if (keyLength == 0)
            {
                return DecodeRejection.Create(ReasonEmptyKey, string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} defines an empty key."));
            }

            if (keyLength > KeyRange.MaxKeyLength)
            {
                return DecodeRejection.Create(
                    ReasonKeyTooLong,
                    string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex} defines a key of {keyLength} bytes, longer than {KeyRange.MaxKeyLength}."));
            }

            if (!reader.TrySkip(keyLength))
            {
                return RowTruncation(rowIndex, "key");
            }

            if (Token(ref reader, rowIndex, "version base", ReasonBaseTooLong) is { } badBase)
            {
                return badBase;
            }

            if (RowVarint(ref reader, rowIndex, "version count", out _) is { } badCount)
            {
                return badCount;
            }

            if (Token(ref reader, rowIndex, "token", ReasonTokenTooLong) is { } badToken)
            {
                return badToken;
            }

            defining++;
        }

        if (RowVarint(ref reader, rowIndex, "body length", out var bodyLength) is { } badBodyLength)
        {
            return badBodyLength;
        }

        return reader.TrySkip(bodyLength) ? null : RowTruncation(rowIndex, "body");
    }

    /// <summary>Checks a row's varint: <c>row-truncated</c> when it reaches the CRC-32C unterminated, then <c>bad-varint</c>.</summary>
    private static InvalidDataException? RowVarint(ref BatchReader reader, ulong rowIndex, string field, out ulong value) =>
        reader.TryReadVarint(out value) switch
        {
            Varint.Status.Valid => null,
            Varint.Status.Unterminated => RowTruncation(rowIndex, field),
            Varint.Status.Invalid => DecodeRejection.Create(
                ReasonBadVarint,
                string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex}'s {field} is not a varint which ends within {Varint.MaxLength} bytes, fits a u64, and is in its shortest form.")),
            _ => throw new UnreachableException("A varint's status is one of its three."),
        };

    /// <summary>Checks a <c>u8</c>-prefixed token or version base of at most <see cref="RowToken.MaxLength"/> bytes.</summary>
    private static InvalidDataException? Token(ref BatchReader reader, ulong rowIndex, string field, string tooLong)
    {
        if (!reader.TryRead(out byte tokenLength))
        {
            return RowTruncation(rowIndex, field + " length");
        }

        if (tokenLength > RowToken.MaxLength)
        {
            return DecodeRejection.Create(
                tooLong,
                string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex}'s {field} is {tokenLength} bytes, longer than {RowToken.MaxLength}."));
        }

        return reader.TrySkip(tokenLength) ? null : RowTruncation(rowIndex, field);
    }

    private static InvalidDataException RowTruncation(ulong rowIndex, string field) =>
        DecodeRejection.Create(ReasonRowTruncated, string.Create(CultureInfo.InvariantCulture, $"Row {rowIndex}'s {field} runs past the batch's CRC-32C."));

    /// <summary>
    /// Lends a decoded batch's rows in order, reading the batch once. Each <see cref="Current"/> is valid only until the
    /// next <see cref="MoveNext"/>; a key which spans pages is copied into a scratch array the enumerator allocates and
    /// reuses. It must not be copied while in use.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly int _count;
        private readonly ulong _ordinalBase;
        private readonly int _rowsOffset;

        // The rows, from the first to the CRC-32C, which the reader walks once.
        private BatchReader _reader;

        // An alias of the rows from the start of the page which holds the current body's first byte, moved forward a page
        // at a time, so a body's slice walks no earlier page.
        private ArcBuffer _page;
        private int _pageStart;
        private int _index;
        private ulong _defining;
        private byte[]? _keyScratch;

        internal Enumerator(ArcBuffer buffer, int rowsOffset, int count, ulong ordinalBase)
        {
            _count = count;
            _ordinalBase = ordinalBase;
            _rowsOffset = rowsOffset;
            _page = count == 0 ? default : buffer.UnsafeSlice(rowsOffset, buffer.Length - rowsOffset - CrcLength);
            _reader = count == 0 ? default : new BatchReader(_page, _page.Length);
            _pageStart = 0;
            _index = -1;
            _defining = 0;
            _keyScratch = null;
            Current = default;
        }

        /// <summary>Gets the row the enumerator lends, valid until the next <see cref="MoveNext"/>.</summary>
        public BatchRow Current { get; private set; }

        public bool MoveNext()
        {
            if (_index + 1 >= _count)
            {
                _index = _count;
                Current = default;
                return false;
            }

            _index++;
            var complete = _reader.TryRead(out byte flags);
            var kind = (RowKind)(flags & KindMask);
            var isDefining = (flags & DefiningFlag) != 0;
            ReadOnlySpan<byte> keyOrChannel = default;
            RowVersion versionBefore = default;
            RowToken token = default;
            ulong ordinal;
            if (kind == RowKind.Message)
            {
                complete &= _reader.TryRead(out byte channelLength);
                keyOrChannel = Take(ref _reader, channelLength, ref _keyScratch);
                ordinal = 0;
            }
            else if (isDefining)
            {
                complete &= _reader.TryReadVarint(out var keyLength) == Varint.Status.Valid;
                keyOrChannel = Take(ref _reader, (int)keyLength, ref _keyScratch);
                var versionBase = ReadToken(ref _reader, ref complete);
                complete &= _reader.TryReadVarint(out var count) == Varint.Status.Valid;
                versionBefore = new RowVersion(versionBase, count);
                token = ReadToken(ref _reader, ref complete);

                // An implied ordinal past the u64's end arises only in a batch whose ordinal base its segment refuses
                // (ordinal-base-mismatch), so it wraps rather than fail a batch the batch rules accept.
                ordinal = unchecked(_ordinalBase + _defining);
                _defining++;
            }
            else
            {
                complete &= _reader.TryReadVarint(out ordinal) == Varint.Status.Valid;
            }

            complete &= _reader.TryReadVarint(out var bodyLength) == Varint.Status.Valid;
            Debug.Assert(bodyLength <= int.MaxValue, "Decode verified that each body ends at or before the CRC-32C.");
            var pageStart = _reader.MoveToPageWithNextByte();
            if (pageStart > _pageStart)
            {
                _page = _page.UnsafeSlice(pageStart - _pageStart, _page.Length - (pageStart - _pageStart));
                _pageStart = pageStart;
            }

            var bodyOffset = _reader.Position - _pageStart;
            var batchBodyOffset = _rowsOffset + _reader.Position;
            if (!_reader.TryTakeContiguous((int)bodyLength, out var body))
            {
                complete &= _reader.TrySkip(bodyLength);
            }

            Debug.Assert(complete, "Decode verified that each row ends at or before the CRC-32C.");
            Debug.Assert(RowKinds.IsDefined(kind), "Decode verified each row's kind.");
            Current = new BatchRow(_index, kind, isDefining, ordinal, keyOrChannel, versionBefore, token, body, _page, bodyOffset, batchBodyOffset, (int)bodyLength);
            return true;
        }

        private static RowToken ReadToken(scoped ref BatchReader reader, scoped ref bool complete)
        {
            complete &= reader.TryRead(out byte length);
            Span<byte> bytes = stackalloc byte[RowToken.MaxLength];
            bytes = bytes[..Math.Min((int)length, RowToken.MaxLength)];
            complete &= reader.TryCopyTo(bytes);
            return new RowToken(bytes);
        }

        /// <summary>
        /// Consumes the next <paramref name="count"/> bytes, returning a view of their page or, when they span pages, a
        /// copy in <paramref name="scratch"/>, which it allocates or grows when it is too small.
        /// </summary>
        private static ReadOnlySpan<byte> Take(scoped ref BatchReader reader, int count, scoped ref byte[]? scratch)
        {
            if (reader.TryTakeContiguous(count, out var bytes))
            {
                return bytes.Span;
            }

            if (scratch is null || scratch.Length < count)
            {
                scratch = new byte[count];
            }

            var copy = scratch.AsSpan(0, count);
            var complete = reader.TryCopyTo(copy);
            Debug.Assert(complete, "Decode verified that each row ends at or before the CRC-32C.");
            return copy;
        }
    }
}
