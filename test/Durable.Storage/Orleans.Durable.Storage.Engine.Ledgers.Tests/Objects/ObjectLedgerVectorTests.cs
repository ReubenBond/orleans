using System.Globalization;
using System.Text.Json;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// The object-store ledgers' golden vectors (object-ledgers.md §7, §8.1; <c>tests/shared-vectors/ledgers</c>): the names
/// and reverse keys, their listing order, the slots' classification and settlement, and the store's record.
/// </summary>
public sealed class ObjectLedgerVectorTests : IDisposable
{
    private const string Area = "ledgers";
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ReverseKeysEncodeAndParseTheirSlots()
    {
        using var document = SharedVectors.Load(Area, "object-names.json");
        foreach (var vector in document.RootElement.GetProperty("reverseKeys").EnumerateArray())
        {
            var slot = new Slot(SharedVectors.Int64(vector, "slot"));
            Assert.Equal(SharedVectors.Text(vector, "key"), ObjectLedgerNames.ReverseKey(slot));
            Assert.Equal(slot, ObjectLedgerNames.ParseReverseKey(SharedVectors.Text(vector, "key")));
        }

        foreach (var key in document.RootElement.GetProperty("invalidReverseKeys").EnumerateArray())
        {
            Assert.Null(ObjectLedgerNames.ParseReverseKey(key.GetString()));
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ObjectLedgerNames.ReverseKey(new Slot(long.MaxValue)));
    }

    [Fact]
    public void ANamesListingRunsNewestSlotFirst()
    {
        using var document = SharedVectors.Load(Area, "object-names.json");
        var order = document.RootElement.GetProperty("listingOrder");
        var id = new LedgerId(7);
        var listed = order.GetProperty("slots").EnumerateArray()
            .Select(slot => ObjectLedgerNames.Slot(id, new Slot(long.Parse(slot.GetString()!, CultureInfo.InvariantCulture))))
            .Order()
            .Select(name => ObjectLedgerNames.ParseSlot(id, name)!.Value.Value.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(order.GetProperty("listed").EnumerateArray().Select(static slot => slot.GetString()), listed);

        var extentListed = order.GetProperty("slots").EnumerateArray()
            .Select(slot => ObjectLedgerNames.Extent(id, new Slot(long.Parse(slot.GetString()!, CultureInfo.InvariantCulture))))
            .Order()
            .Select(name => ObjectLedgerNames.ParseExtent(id, name)!.Value.Value.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(order.GetProperty("listed").EnumerateArray().Select(static slot => slot.GetString()), extentListed);
    }

    [Fact]
    public void TheNamesFollowTheLayout()
    {
        using var document = SharedVectors.Load(Area, "object-names.json");
        Assert.Equal(SharedVectors.Text(document.RootElement, "store"), ObjectLedgerNames.Store.Value);
        foreach (var vector in document.RootElement.GetProperty("names").EnumerateArray())
        {
            var id = new LedgerId(SharedVectors.UInt64(vector, "ledger"));
            var expected = new ObjectName(SharedVectors.Text(vector, "name"));
            var name = vector.TryGetProperty("slot", out _)
                ? ObjectLedgerNames.Slot(id, new Slot(SharedVectors.Int64(vector, "slot")))
                : SharedVectors.Text(vector, "object") == "sealed" ? ObjectLedgerNames.Sealed(id) : ObjectLedgerNames.Dropped(id);
            Assert.Equal(expected, name);
            Assert.Equal(id, ObjectLedgerNames.ParseLedger(name));
        }

        foreach (var vector in document.RootElement.GetProperty("extentNames").EnumerateArray())
        {
            var id = new LedgerId(SharedVectors.UInt64(vector, "ledger"));
            var firstSlot = new Slot(SharedVectors.Int64(vector, "firstSlot"));
            var expected = new ObjectName(SharedVectors.Text(vector, "name"));
            var name = ObjectLedgerNames.Extent(id, firstSlot);
            Assert.Equal(expected, name);
            Assert.Equal(id, ObjectLedgerNames.ParseLedger(name));
            Assert.Equal(firstSlot, ObjectLedgerNames.ParseExtent(id, name));
            Assert.Null(ObjectLedgerNames.ParseSlot(id, name));
        }

        var id7 = new LedgerId(7);
        Assert.Equal(new Slot(42), ObjectLedgerNames.ParseExtentFromLedgerObject(ObjectLedgerNames.Extent(id7, new Slot(42))));
        Assert.Null(ObjectLedgerNames.ParseExtentFromLedgerObject(ObjectLedgerNames.Slot(id7, new Slot(42))));
        Assert.Null(ObjectLedgerNames.ParseExtentFromLedgerObject(ObjectLedgerNames.Sealed(id7)));
        Assert.Null(ObjectLedgerNames.ParseExtentFromLedgerObject(new ObjectName("ledgers/l/0000000000000007/extents/1")));
        Assert.Null(ObjectLedgerNames.ParseExtent(id7, ObjectLedgerNames.Extent(new LedgerId(8), Slot.Zero)));
        Assert.Null(ObjectLedgerNames.ParseExtent(id7, ObjectLedgerNames.Slot(id7, Slot.Zero)));
        Assert.Null(ObjectLedgerNames.ParseExtent(id7, new ObjectName(ObjectLedgerNames.Extents(id7) + "1")));
        Assert.Null(ObjectLedgerNames.ParseSlot(id7, ObjectLedgerNames.Slot(new LedgerId(8), Slot.Zero)));
        Assert.Null(ObjectLedgerNames.ParseSlot(id7, new ObjectName(ObjectLedgerNames.Slots(id7) + "1")));
        Assert.Null(ObjectLedgerNames.ParseSlot(id7, ObjectLedgerNames.Sealed(id7)));
        Assert.True(ObjectLedgerNames.IsMarker(ObjectLedgerNames.Sealed(id7)));
        Assert.True(ObjectLedgerNames.IsMarker(ObjectLedgerNames.Dropped(id7)));
        Assert.True(ObjectLedgerNames.IsDropped(ObjectLedgerNames.Dropped(id7)));
        Assert.False(ObjectLedgerNames.IsDropped(ObjectLedgerNames.Sealed(id7)));
        Assert.False(ObjectLedgerNames.IsMarker(ObjectLedgerNames.Slot(id7, Slot.Zero)));
        Assert.False(ObjectLedgerNames.IsMarker(new ObjectName(ObjectLedgerNames.Ledger(id7) + "sealed-not")));
        Assert.True(string.CompareOrdinal(ObjectLedgerNames.Slot(id7, new Slot(ObjectLedgerNames.MaxSlot)).Value, ObjectLedgerNames.AfterLedger(id7)) < 0);
        Assert.True(string.CompareOrdinal(ObjectLedgerNames.AfterLedger(id7), ObjectLedgerNames.Sealed(new LedgerId(8)).Value) < 0);
        Assert.Null(ObjectLedgerNames.ParseLedger(new ObjectName("ledgers/l/00000000000000000000/sealed")));
        Assert.Null(ObjectLedgerNames.ParseLedger(new ObjectName("ledgers/l/7/sealed")));
        Assert.Null(ObjectLedgerNames.ParseLedger(ObjectLedgerNames.Store));
    }

    [Fact]
    public void AnObjectIsClassifiedByItsLengthThenItsChecksum()
    {
        foreach (var vector in SharedVectors.Cases(Area, "object-slots.json", "classification"))
        {
            var content = new System.Buffers.ReadOnlySequence<byte>(SharedVectors.Hex(vector, "contentHex"));
            var kind = ObjectLedgerSlots.Classify(Properties(vector.GetProperty("properties"))!, content);
            Assert.True(SharedVectors.Text(vector, "result") == kind.ToString(), $"'{SharedVectors.Name(vector)}' classified as {kind}.");
        }
    }

    [Fact]
    public void ACreateIsSettledByItsOccupantsProperties()
    {
        foreach (var vector in SharedVectors.Cases(Area, "object-slots.json", "settlement"))
        {
            var payload = SharedVectors.Hex(vector, "payloadHex");
            var settlement = ObjectLedgerSlots.Settle(Properties(vector.GetProperty("observed")), payload.Length, ObjectStoreChecksums.Crc32C(new System.Buffers.ReadOnlySequence<byte>(payload)));
            Assert.True(SharedVectors.Text(vector, "result") == settlement.ToString(), $"'{SharedVectors.Name(vector)}' settled as {settlement}.");
        }
    }

    [Fact]
    public void StoreRecordsDecodeAndEncodeTheirBytes()
    {
        foreach (var vector in SharedVectors.Cases(Area, "object-store-records.json"))
        {
            var bytes = SharedVectors.Hex(vector);
            var record = Assert.IsType<ObjectLedgerStoreRecord>(ObjectLedgerStoreRecord.Decode(bytes).Value);
            Assert.Equal(SharedVectors.Text(vector, "id"), record.Id.ToString());
            Assert.Equal(SharedVectors.Text(vector, "layout"), ObjectLedgerStoreRecord.LayoutName(record.Layout));
            SharedVectors.AssertBytes(vector, record.Encode());
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreRecord(default, ObjectLedgerLayout.PerSlot));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectLedgerStoreRecord(new ObjectLedgerStoreId(UInt128.One), (ObjectLedgerLayout)7));
    }

    [Fact]
    public void StoreRecordRejectionsNameTheFirstRuleTheyBreak()
    {
        foreach (var vector in SharedVectors.Cases(Area, "object-store-record-rejections.json"))
        {
            SharedVectors.AssertRejected(vector, ObjectLedgerStoreRecord.Decode(SharedVectors.Hex(vector)).Value as Exception);
        }
    }

    [Fact]
    public void ExtentTombstonesAndExtentsDecodeAndEncodeTheirBytes()
    {
        foreach (var vector in SharedVectors.Cases(Area, "object-extents.json", "tombstones"))
        {
            var slot = new Slot(SharedVectors.Int64(vector, "slot"));
            var bytes = SharedVectors.Hex(vector);
            var decoded = Assert.IsType<Slot>(ObjectLedgerExtentFormat.DecodeTombstone(bytes).Value);
            Assert.Equal(slot, decoded);
            SharedVectors.AssertBytes(vector, ObjectLedgerExtentFormat.EncodeTombstone(slot));
        }

        foreach (var vector in SharedVectors.Cases(Area, "object-extents.json", "extents"))
        {
            var bytes = SharedVectors.Hex(vector);
            var decoded = Assert.IsType<ObjectLedgerDecodedExtent>(ObjectLedgerExtentFormat.DecodeExtent(bytes).Value);
            Assert.Equal(new Slot(SharedVectors.Int64(vector, "firstSlot")), decoded.FirstSlot);
            Assert.Equal(new Slot(SharedVectors.Int64(vector, "nextSlot")), decoded.NextSlot);
            var expectedRoll = vector.GetProperty("rollNextSlot").ValueKind == JsonValueKind.Null
                ? (Slot?)null
                : new Slot(SharedVectors.Int64(vector, "rollNextSlot"));
            Assert.Equal(expectedRoll, decoded.RollNextSlot);

            var expectedAppends = vector.GetProperty("appends").EnumerateArray().ToArray();
            Assert.Equal(expectedAppends.Length, decoded.Appends.Length);
            var reencoded = new byte[bytes.Length];
            var cursor = 0;
            for (var i = 0; i < expectedAppends.Length; i++)
            {
                var ea = expectedAppends[i];
                var actual = decoded.Appends[i];
                Assert.Equal(new Slot(SharedVectors.Int64(ea, "firstSlot")), actual.Trailer.FirstSlot);
                Assert.Equal(ea.GetProperty("count").GetInt32(), actual.Trailer.Count);
                Assert.Equal(SharedVectors.Int64(ea, "startOffset"), actual.Trailer.StartOffset);
                Assert.Equal(SharedVectors.Int64(ea, "previousTrailerOffset"), actual.Trailer.PreviousTrailerOffset);

                var expectedFrames = ea.GetProperty("frames").EnumerateArray().ToArray();
                Assert.Equal(expectedFrames.Length, actual.Frames.Length);
                if (actual.Frames.Length == 1 && actual.Frames[0].Kind == ObjectLedgerFrameKind.Roll)
                {
                    var ef = expectedFrames[0];
                    var frame = actual.Frames[0];
                    Assert.Equal("roll", SharedVectors.Text(ef, "kind"));
                    Assert.Equal(new Slot(SharedVectors.Int64(ef, "slot")), frame.Slot);
                    Assert.Empty(SharedVectors.Hex(ef, "payloadHex"));
                    using var roll = ObjectLedgerExtentFormat.EncodeRollAppend(frame.Slot, actual.Trailer.StartOffset);
                    var rollBytes = roll.ToArray();
                    rollBytes.CopyTo(reencoded.AsSpan(cursor));
                    cursor += rollBytes.Length;
                }
                else
                {
                    for (var j = 0; j < expectedFrames.Length; j++)
                    {
                        var ef = expectedFrames[j];
                        var frame = actual.Frames[j];
                        var expectedKind = SharedVectors.Text(ef, "kind") switch
                        {
                            "data" => ObjectLedgerFrameKind.Data,
                            "roll" => ObjectLedgerFrameKind.Roll,
                            _ => throw new InvalidOperationException($"Unexpected kind '{SharedVectors.Text(ef, "kind")}'."),
                        };
                        Assert.Equal(expectedKind, frame.Kind);
                        Assert.Equal(new Slot(SharedVectors.Int64(ef, "slot")), frame.Slot);
                        var expectedPayload = SharedVectors.Hex(ef, "payloadHex");
                        Assert.Equal(expectedPayload, bytes.AsSpan(frame.PayloadOffset, frame.PayloadLength).ToArray());

                        var crc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(frame.Kind, frame.Slot, expectedPayload);
                        Assert.Equal(crc, frame.Crc32c);
                        ObjectLedgerExtentFormat.WriteFrameHeader(
                            reencoded.AsSpan(cursor, ObjectLedgerExtentFormat.FrameHeaderLength),
                            frame.Kind,
                            frame.Slot,
                            expectedPayload.Length,
                            crc);
                        cursor += ObjectLedgerExtentFormat.FrameHeaderLength;
                        expectedPayload.CopyTo(reencoded.AsSpan(cursor));
                        cursor += expectedPayload.Length;
                    }

                    ObjectLedgerExtentFormat.WriteTrailer(
                        reencoded.AsSpan(cursor, ObjectLedgerExtentFormat.TrailerLength),
                        actual.Trailer.FirstSlot,
                        actual.Trailer.Count,
                        actual.Trailer.StartOffset);
                    cursor += ObjectLedgerExtentFormat.TrailerLength;
                }
            }

            Assert.Equal(bytes.Length, cursor);
            SharedVectors.AssertBytes(vector, reencoded);
        }
    }

    [Fact]
    public void ExtentAndTombstoneRejectionsNameTheFirstRuleTheyBreak()
    {
        foreach (var vector in SharedVectors.Cases(Area, "object-extent-rejections.json", "tombstones"))
        {
            SharedVectors.AssertRejected(vector, ObjectLedgerExtentFormat.DecodeTombstone(SharedVectors.Hex(vector)).Value as Exception);
        }

        foreach (var vector in SharedVectors.Cases(Area, "object-extent-rejections.json", "extents"))
        {
            SharedVectors.AssertRejected(vector, ObjectLedgerExtentFormat.DecodeExtent(SharedVectors.Hex(vector)).Value as Exception);
        }

        // Standalone DecodeTrailer rejects inputs longer than TrailerLength with trailing-bytes.
        Span<byte> overlongTrailer = stackalloc byte[ObjectLedgerExtentFormat.TrailerLength + 1];
        ObjectLedgerExtentFormat.WriteTrailer(overlongTrailer[..ObjectLedgerExtentFormat.TrailerLength], Slot.Zero, 1, 0);
        var trailing = Assert.IsType<InvalidDataException>(ObjectLedgerExtentFormat.DecodeTrailer(overlongTrailer).Value);
        Assert.Equal(ObjectLedgerExtentFormat.ReasonTrailingBytes, DecodeRejection.ReasonOf(trailing));
    }

    private static ObjectProperties? Properties(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : new ObjectProperties(
                SharedVectors.Int64(element, "length"),
                element.GetProperty("crc32c").GetString() is { } crc ? uint.Parse(crc, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : null);
}
