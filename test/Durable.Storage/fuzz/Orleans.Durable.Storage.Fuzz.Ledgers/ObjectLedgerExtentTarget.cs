using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// Fuzz target for the <c>CLOE</c> v1 object-store ledger extent, tombstone, trailer, and frame-header decoders
/// (object-ledgers.md §7, formats.md §1): exercises <see cref="ObjectLedgerExtentFormat"/> against
/// <see cref="ObjectLedgerExtentRules"/> and the shared rejection vectors' reason codes.
/// </summary>
public sealed class ObjectLedgerExtentTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> TombstoneReasons = new(static () => LoadReasons("tombstones"));
    private static readonly Lazy<HashSet<string>> ExtentReasons = new(static () => LoadReasons("extents"));

    public override string Name => "ledgers-object-extent";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        foreach (var file in new[] { "object-extents.json", "object-extent-rejections.json" })
        {
            var document = vectors.Document("ledgers", file);
            foreach (var group in new[] { "tombstones", "extents" })
            {
                foreach (var element in document.GetProperty(group).EnumerateArray())
                {
                    yield return GoldenVectors.Hex(element);
                }
            }
        }
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => ObjectLedgerExtentRules.Seal(input);

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        CheckStandaloneTrailer(input);
        CheckStandaloneFrameHeader(input);
        var tombstoneAccepted = CheckTombstone(input);
        var extentAccepted = CheckExtent(input);
        Require(!(tombstoneAccepted && extentAccepted), "input was accepted as both a tombstone and an extent.");
        return tombstoneAccepted || extentAccepted ? FuzzOutcome.Accepted : FuzzOutcome.Rejected;
    }

    private void CheckStandaloneTrailer(ReadOnlySpan<byte> input)
    {
        var reading = ObjectLedgerExtentRules.ReadTrailer(input);
        var bound = MeasureAllocations(input.Length);
        var decoded = ObjectLedgerExtentFormat.DecodeTrailer(input);
        bound.Check("ObjectLedgerExtentFormat.DecodeTrailer");
        if (decoded is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason == reading.Reason, $"rejected trailer as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        }
        else if (decoded is ObjectLedgerTrailer trailer)
        {
            Require(reading.Reason is null, $"accepted a trailer which the format's rules reject as '{reading.Reason}'.");
            Require(
                trailer.FirstSlot.Value == reading.FirstSlot &&
                trailer.Count == reading.Count &&
                trailer.StartOffset == reading.StartOffset &&
                trailer.PreviousTrailerOffset == reading.PreviousTrailerOffset,
                "decoded trailer fields did not match the format's rules.");
            Span<byte> reencoded = stackalloc byte[ObjectLedgerExtentFormat.TrailerLength];
            ObjectLedgerExtentFormat.WriteTrailer(reencoded, trailer.FirstSlot, trailer.Count, trailer.StartOffset);
            Require(reencoded.SequenceEqual(input), "re-encoded trailer did not match input.");
        }
        else
        {
            throw new UnreachableException();
        }
    }

    private void CheckStandaloneFrameHeader(ReadOnlySpan<byte> input)
    {
        var reading = ObjectLedgerExtentRules.ReadFrameHeader(input);
        var bound = MeasureAllocations(input.Length);
        var decoded = ObjectLedgerExtentFormat.DecodeFrameHeader(input);
        bound.Check("ObjectLedgerExtentFormat.DecodeFrameHeader");
        if (decoded is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason == reading.Reason, $"rejected frame header as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        }
        else if (decoded is ObjectLedgerFrameHeader header)
        {
            Require(reading.Reason is null, $"accepted a frame header which the format's rules reject as '{reading.Reason}'.");
            Require(
                header.Kind == reading.Kind &&
                header.Slot.Value == reading.Slot &&
                header.Length == reading.Length &&
                header.Crc32c == reading.Crc32c,
                "decoded frame header fields did not match the format's rules.");
            Span<byte> reencoded = stackalloc byte[ObjectLedgerExtentFormat.FrameHeaderLength];
            ObjectLedgerExtentFormat.WriteFrameHeader(reencoded, header.Kind, header.Slot, header.Length, header.Crc32c);
            Require(reencoded.SequenceEqual(input[..ObjectLedgerExtentFormat.FrameHeaderLength]), "re-encoded frame header did not match input prefix.");
        }
        else
        {
            throw new UnreachableException();
        }
    }

    private bool CheckTombstone(ReadOnlySpan<byte> input)
    {
        var reading = ObjectLedgerExtentRules.ReadTombstone(input);
        var bound = MeasureAllocations(input.Length);
        var decoded = ObjectLedgerExtentFormat.DecodeTombstone(input);
        bound.Check("ObjectLedgerExtentFormat.DecodeTombstone");
        return decoded switch
        {
            InvalidDataException rejection => CheckTombstoneRejection(rejection, reading),
            Slot slot => CheckTombstoneValue(slot, reading, input),
            null => throw new UnreachableException(),
        };
    }

    private bool CheckTombstoneRejection(InvalidDataException rejection, TombstoneReading reading)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(reason == reading.Reason, $"rejected tombstone as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        Require(reason is not null && TombstoneReasons.Value.Contains(reason), $"rejected tombstone as '{reason}', which no tombstone rejection vector names.");
        return false;
    }

    private bool CheckTombstoneValue(Slot slot, TombstoneReading reading, ReadOnlySpan<byte> input)
    {
        Require(reading.Reason is null, $"accepted a tombstone which the format's rules reject as '{reading.Reason}'.");
        Require(slot.Value == reading.Slot, $"decoded tombstone slot {slot.Value}, expected {reading.Slot}.");
        Require(ObjectLedgerExtentFormat.EncodeTombstone(slot).AsSpan().SequenceEqual(input), "re-encoded tombstone did not match input.");
        return true;
    }

    private bool CheckExtent(ReadOnlySpan<byte> input)
    {
        var reading = ObjectLedgerExtentRules.ReadExtent(input);
        var bound = MeasureAllocations(input.Length);
        var decoded = ObjectLedgerExtentFormat.DecodeExtent(input);
        bound.Check("ObjectLedgerExtentFormat.DecodeExtent");
        return decoded switch
        {
            InvalidDataException rejection => CheckExtentRejection(rejection, reading),
            ObjectLedgerDecodedExtent extent => CheckExtentValue(extent, reading, input),
            null => throw new UnreachableException(),
        };
    }

    private bool CheckExtentRejection(InvalidDataException rejection, ExtentReading reading)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(reason == reading.Reason, $"rejected extent as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        Require(reason is not null && ExtentReasons.Value.Contains(reason), $"rejected extent as '{reason}', which no extent rejection vector names.");
        return false;
    }

    private bool CheckExtentValue(ObjectLedgerDecodedExtent extent, ExtentReading reading, ReadOnlySpan<byte> input)
    {
        Require(reading.Reason is null, $"accepted an extent which the format's rules reject as '{reading.Reason}'.");
        Require(
            extent.FirstSlot.Value == reading.FirstSlot &&
            extent.NextSlot.Value == reading.NextSlot &&
            extent.RollNextSlot?.Value == reading.RollNextSlot,
            "decoded extent slot range did not match the format's rules.");
        Require(extent.Appends.Length == reading.Appends.Count, $"decoded {extent.Appends.Length} appends, expected {reading.Appends.Count}.");

        var reencoded = new byte[input.Length];
        var writeCursor = 0;
        for (var appendIndex = 0; appendIndex < extent.Appends.Length; appendIndex++)
        {
            var actualAppend = extent.Appends[appendIndex];
            var expectedAppend = reading.Appends[appendIndex];
            Require(
                actualAppend.Trailer.FirstSlot.Value == expectedAppend.FirstSlot &&
                actualAppend.Trailer.Count == expectedAppend.Count &&
                actualAppend.Trailer.StartOffset == expectedAppend.StartOffset &&
                actualAppend.Trailer.PreviousTrailerOffset == expectedAppend.PreviousTrailerOffset &&
                actualAppend.TrailerOffset == expectedAppend.TrailerOffset,
                $"decoded append {appendIndex} trailer did not match the format's rules.");
            Require(actualAppend.Frames.Length == expectedAppend.Frames.Count, $"decoded {actualAppend.Frames.Length} frames in append {appendIndex}, expected {expectedAppend.Frames.Count}.");

            if (actualAppend.Frames.Length == 1 && actualAppend.Frames[0].Kind == ObjectLedgerFrameKind.Roll)
            {
                using var rollBuf = ObjectLedgerExtentFormat.EncodeRollAppend(actualAppend.Frames[0].Slot, actualAppend.Trailer.StartOffset);
                var rollBytes = rollBuf.ToArray();
                rollBytes.CopyTo(reencoded.AsSpan(writeCursor));
                writeCursor += rollBytes.Length;
            }
            else
            {
                for (var frameIndex = 0; frameIndex < actualAppend.Frames.Length; frameIndex++)
                {
                    var actual = actualAppend.Frames[frameIndex];
                    var expected = expectedAppend.Frames[frameIndex];
                    Require(
                        actual.Kind == expected.Kind &&
                        actual.Slot.Value == expected.Slot &&
                        actual.FrameOffset == expected.FrameOffset &&
                        actual.PayloadOffset == expected.PayloadOffset &&
                        actual.PayloadLength == expected.PayloadLength &&
                        actual.Crc32c == expected.Crc32c,
                        $"decoded frame {frameIndex} of append {appendIndex} did not match the format's rules.");

                    var payload = input.Slice(actual.PayloadOffset, actual.PayloadLength);
                    var crc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(actual.Kind, actual.Slot, payload);
                    Require(crc == actual.Crc32c, $"recomputed CRC-32C did not match decoded frame {frameIndex} of append {appendIndex}.");
                    ObjectLedgerExtentFormat.WriteFrameHeader(
                        reencoded.AsSpan(writeCursor, ObjectLedgerExtentFormat.FrameHeaderLength),
                        actual.Kind,
                        actual.Slot,
                        actual.PayloadLength,
                        crc);
                    writeCursor += ObjectLedgerExtentFormat.FrameHeaderLength;
                    payload.CopyTo(reencoded.AsSpan(writeCursor, actual.PayloadLength));
                    writeCursor += actual.PayloadLength;
                }

                ObjectLedgerExtentFormat.WriteTrailer(
                    reencoded.AsSpan(writeCursor, ObjectLedgerExtentFormat.TrailerLength),
                    actualAppend.Trailer.FirstSlot,
                    actualAppend.Trailer.Count,
                    actualAppend.Trailer.StartOffset);
                writeCursor += ObjectLedgerExtentFormat.TrailerLength;
            }

            // Also validate frame fields for ROLL-only appends.
            if (actualAppend.Frames.Length == 1 && actualAppend.Frames[0].Kind == ObjectLedgerFrameKind.Roll)
            {
                var actual = actualAppend.Frames[0];
                var expected = expectedAppend.Frames[0];
                Require(
                    actual.Kind == expected.Kind &&
                    actual.Slot.Value == expected.Slot &&
                    actual.FrameOffset == expected.FrameOffset &&
                    actual.PayloadOffset == expected.PayloadOffset &&
                    actual.PayloadLength == expected.PayloadLength &&
                    actual.Crc32c == expected.Crc32c,
                    $"decoded ROLL frame of append {appendIndex} did not match the format's rules.");
            }
        }

        Require(writeCursor == input.Length && reencoded.AsSpan().SequenceEqual(input), "re-encoded extent did not match input byte-for-byte.");
        return true;
    }

    private static HashSet<string> LoadReasons(string group)
    {
        var doc = GoldenVectors.Find().Document("ledgers", "object-extent-rejections.json");
        return [.. doc.GetProperty(group).EnumerateArray().Select(static v => GoldenVectors.Text(v, "reason"))];
    }
}
