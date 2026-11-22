using System.Buffers.Binary;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>Independent spec-level outcome for decoding a <c>CLOE</c> v1 tombstone.</summary>
internal readonly record struct TombstoneReading(string? Reason, long Slot);

/// <summary>Independent spec-level outcome for decoding a standalone <c>CLOE</c> v1 trailer.</summary>
internal readonly record struct TrailerReading(
    string? Reason,
    long FirstSlot,
    int Count,
    long StartOffset,
    long PreviousTrailerOffset);

/// <summary>Independent spec-level outcome for decoding a standalone <c>CLOE</c> v1 frame header.</summary>
internal readonly record struct FrameHeaderReading(
    string? Reason,
    ObjectLedgerFrameKind Kind,
    long Slot,
    int Length,
    uint Crc32c);

/// <summary>Independent spec-level frame decoded by <see cref="ObjectLedgerExtentRules"/>.</summary>
internal readonly record struct ExtentFrameReading(
    ObjectLedgerFrameKind Kind,
    long Slot,
    int FrameOffset,
    int PayloadOffset,
    int PayloadLength,
    uint Crc32c);

/// <summary>Independent spec-level append decoded by <see cref="ObjectLedgerExtentRules"/>.</summary>
internal readonly record struct ExtentAppendReading(
    long FirstSlot,
    int Count,
    long StartOffset,
    long PreviousTrailerOffset,
    long TrailerOffset,
    IReadOnlyList<ExtentFrameReading> Frames);

/// <summary>Independent spec-level outcome for decoding a <c>CLOE</c> v1 extent.</summary>
internal readonly record struct ExtentReading(
    string? Reason,
    long FirstSlot,
    long NextSlot,
    long? RollNextSlot,
    IReadOnlyList<ExtentAppendReading> Appends);

/// <summary>
/// Independent reference parser of the <c>CLOE</c> v1 format (object-ledgers.md §7, formats.md §1) for fuzzing
/// <see cref="ObjectLedgerExtentFormat"/>.
/// </summary>
internal static class ObjectLedgerExtentRules
{
    private const uint Version = 1;
    private const int FrameHeaderLength = 17;
    private const int FramePrefixLength = 13;
    private const int TrailerLength = 40;
    private const int MinAppendLength = FrameHeaderLength + TrailerLength;
    private const ulong MaxSlot = long.MaxValue - 1;
    private const int MaxPayloadLength = int.MaxValue - FrameHeaderLength - TrailerLength;

    /// <summary>
    /// Recomputes checksums for a candidate tombstone or extent so libFuzzer mutations explore past CRC-32C checks.
    /// </summary>
    public static byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (input.Length == FrameHeaderLength)
        {
            var copy = input.ToArray();
            var crc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, copy.AsSpan(0, FramePrefixLength));
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(FramePrefixLength, 4), crc);
            return copy;
        }

        if (input.Length < TrailerLength)
        {
            return null;
        }

        var sealedBytes = input.ToArray();
        var span = sealedBytes.AsSpan();
        var end = span.Length;
        var sealedAny = false;
        while (end >= TrailerLength)
        {
            var trailerOffset = end - TrailerLength;
            var trailer = span.Slice(trailerOffset, TrailerLength);
            if (!trailer[..4].SequenceEqual("CLOE"u8))
            {
                break;
            }

            // Recompute trailer CRC-32C first.
            var trailerCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, trailer[..36]);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer.Slice(36, 4), trailerCrc);
            sealedAny = true;

            var count = BinaryPrimitives.ReadUInt32LittleEndian(trailer.Slice(16, 4));
            var rawStartOffset = BinaryPrimitives.ReadUInt64LittleEndian(trailer.Slice(20, 8));
            if (rawStartOffset >= (ulong)trailerOffset)
            {
                break;
            }

            var startOffset = (int)rawStartOffset;
            var cursor = startOffset;
            for (var i = 0u; i < count && cursor + FrameHeaderLength <= trailerOffset; i++)
            {
                var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 9, 4));
                var remaining = trailerOffset - (cursor + FrameHeaderLength);
                if (rawLength > (uint)remaining)
                {
                    break;
                }

                var payloadLength = (int)rawLength;
                var frameCrc = new Crc32(Crc32ParameterSet.Crc32C);
                frameCrc.Append(span.Slice(cursor, FramePrefixLength));
                if (payloadLength > 0)
                {
                    frameCrc.Append(span.Slice(cursor + FrameHeaderLength, payloadLength));
                }

                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(cursor + FramePrefixLength, 4), frameCrc.GetCurrentHashAsUInt32());
                cursor += FrameHeaderLength + payloadLength;
            }

            if (startOffset == 0)
            {
                break;
            }

            end = startOffset;
        }

        return sealedAny ? sealedBytes : null;
    }

    public static TrailerReading ReadTrailer(ReadOnlySpan<byte> input)
    {
        if (input.Length < TrailerLength)
        {
            return new("truncated", 0, 0, 0, 0);
        }

        if (input.Length > TrailerLength)
        {
            return new("trailing-bytes", 0, 0, 0, 0);
        }

        if (!input[..4].SequenceEqual("CLOE"u8))
        {
            return new("bad-magic", 0, 0, 0, 0);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(4, 4)) != Version)
        {
            return new("bad-version", 0, 0, 0, 0);
        }

        var trailerCrc = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(36, 4));
        if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, input[..36]) != trailerCrc)
        {
            return new("crc-mismatch", 0, 0, 0, 0);
        }

        var firstSlot = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(8, 8));
        if (firstSlot > MaxSlot)
        {
            return new("bad-slot", 0, 0, 0, 0);
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(16, 4));
        if (count == 0)
        {
            return new("no-frames", 0, 0, 0, 0);
        }

        if (count > int.MaxValue || count - 1 > MaxSlot - firstSlot)
        {
            return new("bad-slot", 0, 0, 0, 0);
        }

        var startOffset = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(20, 8));
        var prevOffset = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(28, 8));
        const ulong maxStart = long.MaxValue - MinAppendLength;
        if (startOffset > maxStart)
        {
            return new("bad-offset", 0, 0, 0, 0);
        }

        if (startOffset == 0)
        {
            if (prevOffset != 0)
            {
                return new("bad-offset", 0, 0, 0, 0);
            }
        }
        else if (startOffset < MinAppendLength || prevOffset != startOffset - TrailerLength)
        {
            return new("bad-offset", 0, 0, 0, 0);
        }

        return new(null, (long)firstSlot, (int)count, (long)startOffset, (long)prevOffset);
    }

    public static FrameHeaderReading ReadFrameHeader(ReadOnlySpan<byte> input)
    {
        if (input.Length < FrameHeaderLength)
        {
            return new("truncated", default, 0, 0, 0);
        }

        var rawKind = input[0];
        if (rawKind is < 1 or > 3)
        {
            return new("bad-kind", default, 0, 0, 0);
        }

        var slot = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(1, 8));
        if (slot > MaxSlot)
        {
            return new("bad-slot", default, 0, 0, 0);
        }

        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(9, 4));
        if (rawKind == 1)
        {
            if (rawLength is 0 or > MaxPayloadLength)
            {
                return new("bad-length", default, 0, 0, 0);
            }
        }
        else if (rawLength != 0)
        {
            return new("bad-length", default, 0, 0, 0);
        }

        var crc = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(13, 4));
        return new(null, (ObjectLedgerFrameKind)rawKind, (long)slot, (int)rawLength, crc);
    }

    public static TombstoneReading ReadTombstone(ReadOnlySpan<byte> input)
    {
        if (input.Length < FrameHeaderLength)
        {
            return new("truncated", 0);
        }

        if (input.Length > FrameHeaderLength)
        {
            return new("trailing-bytes", 0);
        }

        var kind = input[0];
        if (kind is < 1 or > 3)
        {
            return new("bad-kind", 0);
        }

        var slot = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(1, 8));
        if (slot > MaxSlot)
        {
            return new("bad-slot", 0);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(9, 4));
        if (kind == 1)
        {
            if (length is 0 or > MaxPayloadLength)
            {
                return new("bad-length", 0);
            }
        }
        else if (length != 0)
        {
            return new("bad-length", 0);
        }

        if (kind != 3)
        {
            return new("bad-kind", 0);
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(13, 4));
        var actualCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, input[..FramePrefixLength]);
        if (actualCrc != expectedCrc)
        {
            return new("crc-mismatch", 0);
        }

        return new(null, (long)slot);
    }

    public static ExtentReading ReadExtent(ReadOnlySpan<byte> input)
    {
        if (input.Length < TrailerLength)
        {
            return Fail("truncated");
        }

        List<(long FirstSlot, int Count, long StartOffset, long PreviousTrailerOffset, int TrailerOffset)> trailers = [];
        var end = input.Length;
        while (true)
        {
            var trailerOffset = end - TrailerLength;
            if (trailerOffset < FrameHeaderLength)
            {
                return Fail("truncated");
            }

            var trailerReading = ReadTrailer(input.Slice(trailerOffset, TrailerLength));
            if (trailerReading.Reason is not null)
            {
                return Fail(trailerReading.Reason);
            }

            if (trailerReading.StartOffset >= trailerOffset)
            {
                return Fail("bad-offset");
            }

            trailers.Add((trailerReading.FirstSlot, trailerReading.Count, trailerReading.StartOffset, trailerReading.PreviousTrailerOffset, trailerOffset));
            if (trailerReading.StartOffset == 0)
            {
                break;
            }

            end = (int)trailerReading.StartOffset;
        }

        trailers.Reverse();
        var extentFirstSlot = trailers[0].FirstSlot;
        var expectedSlot = extentFirstSlot;
        long? rollNextSlot = null;
        List<ExtentAppendReading> appends = [];

        for (var appendIndex = 0; appendIndex < trailers.Count; appendIndex++)
        {
            var (firstSlot, count, startOffset, previousTrailerOffset, trailerOffset) = trailers[appendIndex];
            var isLastAppend = appendIndex == trailers.Count - 1;
            if (firstSlot != expectedSlot)
            {
                return Fail("slot-mismatch");
            }

            List<ExtentFrameReading> frames = [];
            var cursor = (int)startOffset;
            for (var frameIndex = 0; frameIndex < count; frameIndex++)
            {
                var remaining = trailerOffset - cursor;
                var header = ReadFrameHeader(input.Slice(cursor, remaining));
                if (header.Reason is not null)
                {
                    return Fail(header.Reason);
                }

                if (header.Kind == ObjectLedgerFrameKind.Seal)
                {
                    return Fail("bad-kind");
                }

                var payloadLength = header.Length;
                if (remaining - FrameHeaderLength < payloadLength)
                {
                    return Fail("truncated");
                }

                var payloadOffset = cursor + FrameHeaderLength;
                var crc = new Crc32(Crc32ParameterSet.Crc32C);
                crc.Append(input.Slice(cursor, FramePrefixLength));
                if (payloadLength > 0)
                {
                    crc.Append(input.Slice(payloadOffset, payloadLength));
                }

                if (crc.GetCurrentHashAsUInt32() != header.Crc32c)
                {
                    return Fail("crc-mismatch");
                }

                if (header.Slot != expectedSlot)
                {
                    return Fail("slot-mismatch");
                }

                var isLastFrame = frameIndex == count - 1;
                if (header.Kind == ObjectLedgerFrameKind.Roll)
                {
                    if (!isLastAppend || !isLastFrame)
                    {
                        return Fail("roll-not-last");
                    }

                    rollNextSlot = header.Slot;
                }
                else
                {
                    expectedSlot += 1;
                }

                frames.Add(new(header.Kind, header.Slot, cursor, payloadOffset, payloadLength, header.Crc32c));
                cursor = payloadOffset + payloadLength;
            }

            if (cursor != trailerOffset)
            {
                return Fail("trailing-bytes");
            }

            appends.Add(new(firstSlot, count, startOffset, previousTrailerOffset, trailerOffset, frames));
        }

        return new(null, extentFirstSlot, expectedSlot, rollNextSlot, appends);
    }

    private static ExtentReading Fail(string reason) => new(reason, 0, 0, null, []);
}
