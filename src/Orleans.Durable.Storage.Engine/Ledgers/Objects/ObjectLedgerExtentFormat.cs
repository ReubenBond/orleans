using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>The frame kind of a <c>CLOE</c> v1 extent or tombstone (object-ledgers.md §7).</summary>
internal enum ObjectLedgerFrameKind : byte
{
    /// <summary>A slot's batch frame, carrying <c>length &gt; 0</c> batch bytes.</summary>
    Data = 1,

    /// <summary>The final frame of a rolled extent, naming the next extent's first slot (<c>length == 0</c>).</summary>
    Roll = 2,

    /// <summary>The sole frame of a tombstone block blob, naming the extent's first slot (<c>length == 0</c>).</summary>
    Seal = 3,
}

/// <summary>A decoded <c>CLOE</c> v1 frame header.</summary>
internal readonly record struct ObjectLedgerFrameHeader(
    ObjectLedgerFrameKind Kind,
    Slot Slot,
    int Length,
    uint Crc32c);

/// <summary>A decoded <c>CLOE</c> v1 append trailer.</summary>
internal readonly record struct ObjectLedgerTrailer(
    Slot FirstSlot,
    int Count,
    long StartOffset,
    long PreviousTrailerOffset);

/// <summary>A decoded frame within a <c>CLOE</c> v1 extent (zero-copy offset into its source bytes).</summary>
internal readonly record struct ObjectLedgerDecodedFrame(
    ObjectLedgerFrameKind Kind,
    Slot Slot,
    int FrameOffset,
    int PayloadOffset,
    int PayloadLength,
    uint Crc32c);

/// <summary>A decoded append within a <c>CLOE</c> v1 extent.</summary>
internal sealed record ObjectLedgerDecodedAppend(
    ObjectLedgerTrailer Trailer,
    long TrailerOffset,
    ImmutableArray<ObjectLedgerDecodedFrame> Frames);

/// <summary>A decoded <c>CLOE</c> v1 extent: one or more appends with consecutive slots and optional final <c>ROLL</c>.</summary>
internal sealed record ObjectLedgerDecodedExtent(
    Slot FirstSlot,
    Slot NextSlot,
    Slot? RollNextSlot,
    ImmutableArray<ObjectLedgerDecodedAppend> Appends);

/// <summary>What decoding a <c>CLOE</c> v1 trailer gives (conventions.md §15).</summary>
internal readonly union ObjectLedgerTrailerDecoding(ObjectLedgerTrailer, InvalidDataException);

/// <summary>What decoding a <c>CLOE</c> v1 frame header gives (conventions.md §15).</summary>
internal readonly union ObjectLedgerFrameHeaderDecoding(ObjectLedgerFrameHeader, InvalidDataException);

/// <summary>What decoding a <c>CLOE</c> v1 tombstone gives (conventions.md §15): the tombstoned extent's first slot, or its rejection.</summary>
internal readonly union ObjectLedgerTombstoneDecoding(Slot, InvalidDataException);

/// <summary>What decoding a whole <c>CLOE</c> v1 extent gives (conventions.md §15).</summary>
internal readonly union ObjectLedgerExtentDecoding(ObjectLedgerDecodedExtent, InvalidDataException);

/// <summary>
/// The <c>CLOE</c> v1 format of append-layout object-store ledger extents and their tombstones (object-ledgers.md §7,
/// formats.md §1).
/// </summary>
/// <remarks>
/// <para>
/// Every integer is little-endian and every checksum is Castagnoli CRC-32C:
/// </para>
/// <code>
/// frame    kind u8 (DATA 1, ROLL 2, SEAL 3), slot u64, length u32, CRC-32C u32, payload[length]
///          CRC-32C covers bytes [0..13) (kind, slot, length) followed by payload[0..length).
/// trailer  magic "CLOE" (4), version u32 (1), firstSlot u64, count u32, startOffset u64,
///          previousTrailerOffset u64, CRC-32C u32
///          CRC-32C covers bytes [0..36).
/// </code>
/// <para>
/// A tombstone is a standalone 17-byte block blob holding one <c>SEAL</c> frame (<c>length == 0</c>). An append blob
/// holds one or more appends; each append holds <c>count ≥ 1</c> frames of consecutive slots followed by one 40-byte
/// trailer. <c>SEAL</c> frames never appear in an append blob; a <c>ROLL</c> frame may appear at most once, as the last
/// frame of the last append (OL-I10). Because the trailer magic begins with <c>'C'</c> (<c>0x43</c>) and frame kinds are
/// <c>1..3</c>, forward scans of an extent distinguish the next frame from the append's trailer by its first byte.
/// </para>
/// </remarks>
internal static class ObjectLedgerExtentFormat
{
    /// <summary>The format version, 1.</summary>
    public const uint Version = 1;

    /// <summary>The byte length of a frame header: kind (1) + slot (8) + length (4) + CRC-32C (4).</summary>
    public const int FrameHeaderLength = 17;

    /// <summary>The byte length of the frame header fields preceding the CRC-32C: kind (1) + slot (8) + length (4).</summary>
    public const int FrameCrcPrefixLength = 13;

    /// <summary>The byte length of a tombstone block blob: one <c>SEAL</c> frame with empty payload.</summary>
    public const int TombstoneLength = FrameHeaderLength;

    /// <summary>
    /// The byte length of a trailer: magic (4) + version (4) + firstSlot (8) + count (4) + startOffset (8) +
    /// previousTrailerOffset (8) + CRC-32C (4).
    /// </summary>
    public const int TrailerLength = 40;

    /// <summary>The byte length of a <c>ROLL</c> append: one <c>ROLL</c> frame (17) plus its trailer (40).</summary>
    public const int RollAppendLength = FrameHeaderLength + TrailerLength;

    /// <summary>The fewest bytes a non-empty append holds: one frame header plus one trailer.</summary>
    public const int MinAppendLength = FrameHeaderLength + TrailerLength;

    /// <summary>The greatest payload length a single frame may carry within an extent.</summary>
    public const int MaxFramePayloadLength = int.MaxValue - FrameHeaderLength - TrailerLength;

    /// <summary>The first byte of the trailer magic, <c>'C'</c>, disjoint from frame kinds <c>1..3</c>.</summary>
    public const byte TrailerLeadByte = (byte)'C';

    /// <summary>The bytes end before the expected header, frame payload, or trailer completes.</summary>
    public const string ReasonTruncated = "truncated";

    /// <summary>Unexpected bytes remain past the end of a standalone frame, tombstone, or append's frames.</summary>
    public const string ReasonTrailingBytes = "trailing-bytes";

    /// <summary>The trailer's magic is not <c>CLOE</c>.</summary>
    public const string ReasonBadMagic = "bad-magic";

    /// <summary>The trailer's version is not 1.</summary>
    public const string ReasonBadVersion = "bad-version";

    /// <summary>A frame's or trailer's CRC-32C does not match its bytes.</summary>
    public const string ReasonCrcMismatch = "crc-mismatch";

    /// <summary>A frame's kind is not valid for the context (unknown kind, <c>SEAL</c> in an extent, or non-<c>SEAL</c> tombstone).</summary>
    public const string ReasonBadKind = "bad-kind";

    /// <summary>A frame's or trailer's slot exceeds <see cref="ObjectLedgerNames.MaxSlot"/> or overflows it.</summary>
    public const string ReasonBadSlot = "bad-slot";

    /// <summary>A frame's payload length is zero for <c>DATA</c>, non-zero for <c>ROLL</c>/<c>SEAL</c>, or exceeds the maximum.</summary>
    public const string ReasonBadLength = "bad-length";

    /// <summary>A trailer declares zero frames.</summary>
    public const string ReasonNoFrames = "no-frames";

    /// <summary>A trailer's <c>startOffset</c> or <c>previousTrailerOffset</c> violates layout bounds or chaining.</summary>
    public const string ReasonBadOffset = "bad-offset";

    /// <summary>Frame slots are not consecutive with their trailer or across appends.</summary>
    public const string ReasonSlotMismatch = "slot-mismatch";

    /// <summary>A <c>ROLL</c> frame appears before the last frame of the last append (OL-I10).</summary>
    public const string ReasonRollNotLast = "roll-not-last";

    [ThreadStatic]
    private static Crc32? t_frameCrc32;

    /// <summary>Gets the 4-byte trailer magic, <c>CLOE</c>.</summary>
    internal static ReadOnlySpan<byte> Magic => "CLOE"u8;

    private static Crc32 RentFrameCrc32C()
    {
        var crc = t_frameCrc32;
        if (crc is null)
        {
            crc = new Crc32(Crc32ParameterSet.Crc32C);
            t_frameCrc32 = crc;
        }
        else
        {
            crc.Reset();
        }

        return crc;
    }

    /// <summary>Computes a frame's CRC-32C over its 13-byte header prefix and an <see cref="ArcBuffer"/> payload without allocating.</summary>
    public static uint ComputeFrameCrc32c(ObjectLedgerFrameKind kind, Slot slot, ArcBuffer payload)
    {
        Span<byte> prefix = stackalloc byte[FrameCrcPrefixLength];
        WriteFramePrefix(prefix, kind, slot, payload.Length);
        var crc = RentFrameCrc32C();
        crc.Append(prefix);
        foreach (var span in payload)
        {
            if (!span.IsEmpty)
            {
                crc.Append(span);
            }
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>Computes a frame's CRC-32C over its 13-byte header prefix and payload.</summary>
    public static uint ComputeFrameCrc32c(ObjectLedgerFrameKind kind, Slot slot, ReadOnlySequence<byte> payload)
    {
        Span<byte> prefix = stackalloc byte[FrameCrcPrefixLength];
        WriteFramePrefix(prefix, kind, slot, (int)payload.Length);
        return ComputeFrameCrc32c(prefix, payload);
    }

    /// <summary>Computes a frame's CRC-32C over its 13-byte header prefix and payload span.</summary>
    public static uint ComputeFrameCrc32c(ObjectLedgerFrameKind kind, Slot slot, ReadOnlySpan<byte> payload)
    {
        Span<byte> prefix = stackalloc byte[FrameCrcPrefixLength];
        WriteFramePrefix(prefix, kind, slot, payload.Length);
        var crc = RentFrameCrc32C();
        crc.Append(prefix);
        if (!payload.IsEmpty)
        {
            crc.Append(payload);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>Computes a frame's CRC-32C over an already-encoded 13-byte header prefix and a payload sequence.</summary>
    public static uint ComputeFrameCrc32c(ReadOnlySpan<byte> headerPrefix, ReadOnlySequence<byte> payload)
    {
        var crc = RentFrameCrc32C();
        crc.Append(headerPrefix);
        foreach (var segment in payload)
        {
            if (!segment.IsEmpty)
            {
                crc.Append(segment.Span);
            }
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>Writes a 17-byte frame header into <paramref name="destination"/>.</summary>
    public static void WriteFrameHeader(Span<byte> destination, ObjectLedgerFrameKind kind, Slot slot, int payloadLength, uint crc32c)
    {
        WriteFramePrefix(destination, kind, slot, payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(FrameCrcPrefixLength, sizeof(uint)), crc32c);
    }

    /// <summary>Writes a 40-byte <c>CLOE</c> v1 trailer into <paramref name="destination"/>.</summary>
    public static void WriteTrailer(Span<byte> destination, Slot firstSlot, int count, long startOffset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstSlot.Value, ObjectLedgerNames.MaxSlot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        if (startOffset > 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(startOffset, MinAppendLength);
        }

        var previousTrailerOffset = startOffset == 0 ? 0UL : (ulong)(startOffset - TrailerLength);
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), (ulong)firstSlot.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16, 4), (uint)count);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(20, 8), (ulong)startOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(28, 8), previousTrailerOffset);
        var crc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, destination[..36]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(36, 4), crc);
    }

    /// <summary>Encodes a tombstone block blob: one 17-byte <c>SEAL</c> frame naming <paramref name="firstSlot"/>.</summary>
    public static byte[] EncodeTombstone(Slot firstSlot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstSlot.Value, ObjectLedgerNames.MaxSlot);
        var bytes = new byte[TombstoneLength];
        var crc = ComputeFrameCrc32c(ObjectLedgerFrameKind.Seal, firstSlot, []);
        WriteFrameHeader(bytes, ObjectLedgerFrameKind.Seal, firstSlot, 0, crc);
        return bytes;
    }

    /// <summary>Encodes a single-frame <c>ROLL</c> append (57 bytes) at <paramref name="startOffset"/> naming <paramref name="nextSlot"/>.</summary>
    public static ArcBuffer EncodeRollAppend(Slot nextSlot, long startOffset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nextSlot.Value, ObjectLedgerNames.MaxSlot);
        var bytes = new byte[RollAppendLength];
        var crc = ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, nextSlot, []);
        WriteFrameHeader(bytes, ObjectLedgerFrameKind.Roll, nextSlot, 0, crc);
        WriteTrailer(bytes.AsSpan(FrameHeaderLength, TrailerLength), nextSlot, count: 1, startOffset);
        return ArcBuffer.Wrap(bytes);
    }

    /// <summary>
    /// Decodes a 40-byte <c>CLOE</c> v1 trailer, checking length, magic, version, CRC-32C, slot bounds, frame count, and
    /// offset chaining in order.
    /// </summary>
    public static ObjectLedgerTrailerDecoding DecodeTrailer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TrailerLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"The trailer holds {bytes.Length} bytes, fewer than {TrailerLength}."));
        }

        if (bytes.Length > TrailerLength)
        {
            return DecodeRejection.Create(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"The trailer holds {bytes.Length} bytes, more than {TrailerLength}."));
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            return DecodeRejection.Create(ReasonBadMagic, "The trailer's magic is not CLOE.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (version != Version)
        {
            return DecodeRejection.Create(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"The trailer's version is {version}, not {Version}."));
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(36, 4));
        var actualCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes[..36]);
        if (actualCrc != expectedCrc)
        {
            return DecodeRejection.Create(ReasonCrcMismatch, "The trailer's CRC-32C does not match its bytes.");
        }

        var rawFirstSlot = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8));
        if (rawFirstSlot > ObjectLedgerNames.MaxSlot)
        {
            return DecodeRejection.Create(ReasonBadSlot, string.Create(CultureInfo.InvariantCulture, $"The trailer's first slot {rawFirstSlot} passes {ObjectLedgerNames.MaxSlot}."));
        }

        var rawCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4));
        if (rawCount == 0)
        {
            return DecodeRejection.Create(ReasonNoFrames, "The trailer counts no frame.");
        }

        if (rawCount > int.MaxValue || rawCount - 1 > ObjectLedgerNames.MaxSlot - rawFirstSlot)
        {
            return DecodeRejection.Create(ReasonBadSlot, string.Create(CultureInfo.InvariantCulture, $"The trailer's {rawCount} frames from slot {rawFirstSlot} pass {ObjectLedgerNames.MaxSlot}."));
        }

        var rawStartOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(20, 8));
        var rawPrevOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(28, 8));
        const ulong maxStartOffset = long.MaxValue - MinAppendLength;
        if (rawStartOffset > maxStartOffset)
        {
            return DecodeRejection.Create(ReasonBadOffset, string.Create(CultureInfo.InvariantCulture, $"The trailer's start offset {rawStartOffset} passes {maxStartOffset}."));
        }

        if (rawStartOffset == 0)
        {
            if (rawPrevOffset != 0)
            {
                return DecodeRejection.Create(ReasonBadOffset, string.Create(CultureInfo.InvariantCulture, $"The first append's trailer gives previous trailer offset {rawPrevOffset}, not 0."));
            }
        }
        else if (rawStartOffset < MinAppendLength || rawPrevOffset != rawStartOffset - TrailerLength)
        {
            return DecodeRejection.Create(ReasonBadOffset, string.Create(CultureInfo.InvariantCulture, $"The trailer's start offset {rawStartOffset} and previous trailer offset {rawPrevOffset} do not chain."));
        }

        return new ObjectLedgerTrailer(
            new Slot((long)rawFirstSlot),
            (int)rawCount,
            (long)rawStartOffset,
            (long)rawPrevOffset);
    }

    /// <summary>
    /// Decodes a 17-byte frame header, checking length, kind, slot, and payload length rules (without checking payload
    /// bytes or CRC-32C).
    /// </summary>
    public static ObjectLedgerFrameHeaderDecoding DecodeFrameHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FrameHeaderLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"The frame header holds {bytes.Length} bytes, fewer than {FrameHeaderLength}."));
        }

        var rawKind = bytes[0];
        if (rawKind is < (byte)ObjectLedgerFrameKind.Data or > (byte)ObjectLedgerFrameKind.Seal)
        {
            return DecodeRejection.Create(ReasonBadKind, string.Create(CultureInfo.InvariantCulture, $"The frame kind {rawKind} is not DATA, ROLL, or SEAL."));
        }

        var kind = (ObjectLedgerFrameKind)rawKind;
        var rawSlot = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(1, 8));
        if (rawSlot > ObjectLedgerNames.MaxSlot)
        {
            return DecodeRejection.Create(ReasonBadSlot, string.Create(CultureInfo.InvariantCulture, $"The frame's slot {rawSlot} passes {ObjectLedgerNames.MaxSlot}."));
        }

        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(9, 4));
        if (kind == ObjectLedgerFrameKind.Data)
        {
            if (rawLength is 0 or > MaxFramePayloadLength)
            {
                return DecodeRejection.Create(ReasonBadLength, string.Create(CultureInfo.InvariantCulture, $"A DATA frame's length {rawLength} lies outside 1..{MaxFramePayloadLength}."));
            }
        }
        else if (rawLength != 0)
        {
            return DecodeRejection.Create(ReasonBadLength, string.Create(CultureInfo.InvariantCulture, $"A {kind} frame's length is {rawLength}, not 0."));
        }

        var crc32c = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(FrameCrcPrefixLength, sizeof(uint)));
        return new ObjectLedgerFrameHeader(kind, new Slot((long)rawSlot), (int)rawLength, crc32c);
    }

    /// <summary>Decodes a tombstone block blob: exactly one 17-byte <c>SEAL</c> frame.</summary>
    public static ObjectLedgerTombstoneDecoding DecodeTombstone(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TombstoneLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"The tombstone holds {bytes.Length} bytes, fewer than {TombstoneLength}."));
        }

        if (bytes.Length > TombstoneLength)
        {
            return DecodeRejection.Create(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"The tombstone holds {bytes.Length} bytes, more than {TombstoneLength}."));
        }

        var header = DecodeFrameHeader(bytes);
        if (header is not ObjectLedgerFrameHeader parsed)
        {
            return header is InvalidDataException rejection ? rejection : throw new System.Diagnostics.UnreachableException();
        }

        if (parsed.Kind != ObjectLedgerFrameKind.Seal)
        {
            return DecodeRejection.Create(ReasonBadKind, string.Create(CultureInfo.InvariantCulture, $"The tombstone holds a {parsed.Kind} frame, not SEAL."));
        }

        var actualCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes[..FrameCrcPrefixLength]);
        if (actualCrc != parsed.Crc32c)
        {
            return DecodeRejection.Create(ReasonCrcMismatch, "The tombstone's CRC-32C does not match its header.");
        }

        return parsed.Slot;
    }

    /// <summary>
    /// Decodes a whole non-empty <c>CLOE</c> v1 extent by walking its trailers backwards from EOF to offset 0, then
    /// verifying every append's frames, CRCs, slot continuity, and <c>ROLL</c> placement (OL-I10).
    /// </summary>
    public static ObjectLedgerExtentDecoding DecodeExtent(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TrailerLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"The extent holds {bytes.Length} bytes, fewer than one {TrailerLength}-byte trailer."));
        }

        List<(ObjectLedgerTrailer Trailer, long TrailerOffset)> trailers = [];
        var end = bytes.Length;
        while (true)
        {
            var trailerOffset = end - TrailerLength;
            if (trailerOffset < FrameHeaderLength)
            {
                return DecodeRejection.Create(ReasonTruncated, "An append leaves fewer bytes before its trailer than one frame header.");
            }

            var decodedTrailer = DecodeTrailer(bytes.Slice(trailerOffset, TrailerLength));
            if (decodedTrailer is not ObjectLedgerTrailer trailer)
            {
                return decodedTrailer is InvalidDataException trailerRejection ? trailerRejection : throw new System.Diagnostics.UnreachableException();
            }

            if (trailer.StartOffset >= trailerOffset)
            {
                return DecodeRejection.Create(ReasonBadOffset, string.Create(CultureInfo.InvariantCulture, $"The trailer's start offset {trailer.StartOffset} does not precede its trailer offset {trailerOffset}."));
            }

            trailers.Add((trailer, trailerOffset));
            if (trailer.StartOffset == 0)
            {
                break;
            }

            end = (int)trailer.StartOffset;
        }

        trailers.Reverse();
        var appends = ImmutableArray.CreateBuilder<ObjectLedgerDecodedAppend>(trailers.Count);
        var extentFirstSlot = trailers[0].Trailer.FirstSlot;
        var expectedSlot = extentFirstSlot;
        Slot? rollNextSlot = null;

        for (var appendIndex = 0; appendIndex < trailers.Count; appendIndex++)
        {
            var (trailer, trailerOffset) = trailers[appendIndex];
            var isLastAppend = appendIndex == trailers.Count - 1;
            if (trailer.FirstSlot != expectedSlot)
            {
                return DecodeRejection.Create(
                    ReasonSlotMismatch,
                    string.Create(CultureInfo.InvariantCulture, $"Append {appendIndex} begins at slot {trailer.FirstSlot}, expected {expectedSlot}."));
            }

            var maxPossibleFrames = (int)Math.Min(int.MaxValue, Math.Max(1L, (trailerOffset - trailer.StartOffset) / FrameHeaderLength));
            var frames = ImmutableArray.CreateBuilder<ObjectLedgerDecodedFrame>(Math.Min(trailer.Count, maxPossibleFrames));
            var cursor = (int)trailer.StartOffset;
            for (var frameIndex = 0; frameIndex < trailer.Count; frameIndex++)
            {
                var remaining = (int)trailerOffset - cursor;
                var decodedHeader = DecodeFrameHeader(bytes.Slice(cursor, remaining));
                if (decodedHeader is not ObjectLedgerFrameHeader header)
                {
                    return decodedHeader is InvalidDataException headerRejection ? headerRejection : throw new System.Diagnostics.UnreachableException();
                }

                if (header.Kind == ObjectLedgerFrameKind.Seal)
                {
                    return DecodeRejection.Create(ReasonBadKind, "A SEAL frame belongs to a tombstone, not an extent append.");
                }

                if (remaining - FrameHeaderLength < header.Length)
                {
                    return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} needs {header.Length} payload bytes, only {remaining - FrameHeaderLength} remain before the trailer."));
                }

                var payloadOffset = cursor + FrameHeaderLength;
                var crc = RentFrameCrc32C();
                crc.Append(bytes.Slice(cursor, FrameCrcPrefixLength));
                if (header.Length > 0)
                {
                    crc.Append(bytes.Slice(payloadOffset, header.Length));
                }

                if (crc.GetCurrentHashAsUInt32() != header.Crc32c)
                {
                    return DecodeRejection.Create(ReasonCrcMismatch, string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} fails its CRC-32C."));
                }

                if (header.Slot != expectedSlot)
                {
                    return DecodeRejection.Create(
                        ReasonSlotMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} carries slot {header.Slot}, expected {expectedSlot}."));
                }

                var isLastFrame = frameIndex == trailer.Count - 1;
                if (header.Kind == ObjectLedgerFrameKind.Roll)
                {
                    if (!isLastAppend || !isLastFrame)
                    {
                        return DecodeRejection.Create(ReasonRollNotLast, "A ROLL frame must be the last frame of the extent's last append (OL-I10).");
                    }

                    rollNextSlot = header.Slot;
                }
                else
                {
                    expectedSlot += 1;
                }

                frames.Add(new ObjectLedgerDecodedFrame(header.Kind, header.Slot, cursor, payloadOffset, header.Length, header.Crc32c));
                cursor = payloadOffset + header.Length;
            }

            if (cursor != trailerOffset)
            {
                return DecodeRejection.Create(
                    ReasonTrailingBytes,
                    string.Create(CultureInfo.InvariantCulture, $"Append {appendIndex} leaves {trailerOffset - cursor} bytes between its last frame and its trailer."));
            }

            appends.Add(new ObjectLedgerDecodedAppend(trailer, trailerOffset, frames.MoveToImmutable()));
        }

        return new ObjectLedgerDecodedExtent(extentFirstSlot, expectedSlot, rollNextSlot, appends.MoveToImmutable());
    }

    /// <summary>
    /// Decodes a contiguous sequence of one or more complete <c>CLOE</c> v1 appends starting at extent byte offset
    /// <paramref name="baseOffset"/>, directly over <paramref name="bytes"/> without flattening to an array
    /// (conventions.md §1, object-ledgers.md §7). Each decoded frame's <see cref="ObjectLedgerDecodedFrame.FrameOffset"/>
    /// and <see cref="ObjectLedgerDecodedFrame.PayloadOffset"/> are relative to <paramref name="baseOffset"/>.
    /// </summary>
    public static ObjectLedgerExtentDecoding DecodeExtentRange(
        ReadOnlySequence<byte> bytes,
        long baseOffset,
        Slot? expectedFirstSlot = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        if (bytes.IsSingleSegment && baseOffset == 0 && expectedFirstSlot is null)
        {
            return DecodeExtent(bytes.FirstSpan);
        }

        if (bytes.Length < TrailerLength)
        {
            return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"The extent range holds {bytes.Length} bytes, fewer than one {TrailerLength}-byte trailer."));
        }

        List<(ObjectLedgerTrailer Trailer, long TrailerOffset)> trailers = [];
        Span<byte> trailerSpan = stackalloc byte[TrailerLength];
        var relativeEnd = bytes.Length;
        while (true)
        {
            var relativeTrailerOffset = relativeEnd - TrailerLength;
            if (relativeTrailerOffset < FrameHeaderLength)
            {
                return DecodeRejection.Create(ReasonTruncated, "An append leaves fewer bytes before its trailer than one frame header.");
            }

            bytes.Slice(relativeTrailerOffset, TrailerLength).CopyTo(trailerSpan);
            var decodedTrailer = DecodeTrailer(trailerSpan);
            if (decodedTrailer is not ObjectLedgerTrailer trailer)
            {
                return decodedTrailer is InvalidDataException trailerRejection ? trailerRejection : throw new System.Diagnostics.UnreachableException();
            }

            var extentTrailerOffset = baseOffset + relativeTrailerOffset;
            if (trailer.StartOffset < baseOffset || trailer.StartOffset >= extentTrailerOffset)
            {
                return DecodeRejection.Create(
                    ReasonBadOffset,
                    string.Create(CultureInfo.InvariantCulture, $"The trailer's start offset {trailer.StartOffset} does not lie within [{baseOffset}, {extentTrailerOffset})."));
            }

            trailers.Add((trailer, extentTrailerOffset));
            if (trailer.StartOffset == baseOffset)
            {
                break;
            }

            relativeEnd = trailer.StartOffset - baseOffset;
        }

        trailers.Reverse();
        var appends = ImmutableArray.CreateBuilder<ObjectLedgerDecodedAppend>(trailers.Count);
        var rangeFirstSlot = trailers[0].Trailer.FirstSlot;
        var expectedSlot = expectedFirstSlot ?? rangeFirstSlot;
        Slot? rollNextSlot = null;
        Span<byte> headerSpan = stackalloc byte[FrameHeaderLength];

        for (var appendIndex = 0; appendIndex < trailers.Count; appendIndex++)
        {
            var (trailer, extentTrailerOffset) = trailers[appendIndex];
            var isLastAppend = appendIndex == trailers.Count - 1;
            if (trailer.FirstSlot != expectedSlot)
            {
                return DecodeRejection.Create(
                    ReasonSlotMismatch,
                    string.Create(CultureInfo.InvariantCulture, $"Append {appendIndex} begins at slot {trailer.FirstSlot}, expected {expectedSlot}."));
            }

            var maxPossibleFrames = (int)Math.Min(int.MaxValue, Math.Max(1L, (extentTrailerOffset - trailer.StartOffset) / FrameHeaderLength));
            var frames = ImmutableArray.CreateBuilder<ObjectLedgerDecodedFrame>(Math.Min(trailer.Count, maxPossibleFrames));
            var relCursor = checked((int)(trailer.StartOffset - baseOffset));
            var relTrailerOffset = checked((int)(extentTrailerOffset - baseOffset));
            for (var frameIndex = 0; frameIndex < trailer.Count; frameIndex++)
            {
                var remaining = relTrailerOffset - relCursor;
                if (remaining < FrameHeaderLength)
                {
                    return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} holds {remaining} bytes, fewer than {FrameHeaderLength}."));
                }

                bytes.Slice(relCursor, FrameHeaderLength).CopyTo(headerSpan);
                var decodedHeader = DecodeFrameHeader(headerSpan);
                if (decodedHeader is not ObjectLedgerFrameHeader header)
                {
                    return decodedHeader is InvalidDataException headerRejection ? headerRejection : throw new System.Diagnostics.UnreachableException();
                }

                if (header.Kind == ObjectLedgerFrameKind.Seal)
                {
                    return DecodeRejection.Create(ReasonBadKind, "A SEAL frame belongs to a tombstone, not an extent append.");
                }

                if (remaining - FrameHeaderLength < header.Length)
                {
                    return DecodeRejection.Create(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} needs {header.Length} payload bytes, only {remaining - FrameHeaderLength} remain before the trailer."));
                }

                var relPayloadOffset = relCursor + FrameHeaderLength;
                var actualCrc = ComputeFrameCrc32c(headerSpan[..FrameCrcPrefixLength], bytes.Slice(relPayloadOffset, header.Length));
                if (actualCrc != header.Crc32c)
                {
                    return DecodeRejection.Create(ReasonCrcMismatch, string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} fails its CRC-32C."));
                }

                if (header.Slot != expectedSlot)
                {
                    return DecodeRejection.Create(
                        ReasonSlotMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Frame {frameIndex} of append {appendIndex} carries slot {header.Slot}, expected {expectedSlot}."));
                }

                var isLastFrame = frameIndex == trailer.Count - 1;
                if (header.Kind == ObjectLedgerFrameKind.Roll)
                {
                    if (!isLastAppend || !isLastFrame)
                    {
                        return DecodeRejection.Create(ReasonRollNotLast, "A ROLL frame must be the last frame of the extent's last append (OL-I10).");
                    }

                    rollNextSlot = header.Slot;
                }
                else
                {
                    expectedSlot += 1;
                }

                frames.Add(new ObjectLedgerDecodedFrame(header.Kind, header.Slot, relCursor, relPayloadOffset, header.Length, header.Crc32c));
                relCursor = relPayloadOffset + header.Length;
            }

            if (relCursor != relTrailerOffset)
            {
                return DecodeRejection.Create(
                    ReasonTrailingBytes,
                    string.Create(CultureInfo.InvariantCulture, $"Append {appendIndex} leaves {relTrailerOffset - relCursor} bytes between its last frame and its trailer."));
            }

            appends.Add(new ObjectLedgerDecodedAppend(trailer, extentTrailerOffset, frames.MoveToImmutable()));
        }

        return new ObjectLedgerDecodedExtent(rangeFirstSlot, expectedSlot, rollNextSlot, appends.MoveToImmutable());
    }

    private static void WriteFramePrefix(Span<byte> destination, ObjectLedgerFrameKind kind, Slot slot, int payloadLength)
    {
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(1, 8), (ulong)slot.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(9, 4), (uint)payloadLength);
    }
}
