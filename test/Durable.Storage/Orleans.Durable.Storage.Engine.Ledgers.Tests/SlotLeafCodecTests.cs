using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the <c>CLSI</c> version 1 leaf format (ledgerstore.md §7.4): decoding inverts the canonical encoder for any leaf, the decoder rejects
/// every rule-breaking leaf with the <see cref="InvalidDataException"/> naming the rule, and placement-shaped leaves compress.
/// </summary>
public sealed class SlotLeafCodecTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private static readonly Gen<SlotEntry[]> Placed =
        Gen.Select(Gen.Int[0, 4], Gen.Int[1, 40], Gen.Int[0, 3], Gen.ULong[1, 1_000])
            .Select(static (seed, perPage, jitter, file) =>
            {
                var random = new Random(seed * 7919 + perPage);
                var entries = new SlotEntry[SlotLeafCodec.Slots];
                var page = (uint)random.Next(0, 1_000);
                for (var index = 0; index < entries.Length; index++)
                {
                    var current = page + (uint)(index / perPage) + (uint)random.Next(0, jitter + 1);
                    entries[index] = new SlotEntry(SlotKind.Data, new WalPage(file, current));
                }

                return entries;
            });

    private static readonly Gen<SlotEntry> AnyEntry =
        Gen.Frequency(
            (3, Gen.Const(default(SlotEntry))),
            (6, Gen.Select(Gen.Int[1, 2], Gen.ULong[1, 4], Gen.UInt).Select(static (kind, file, page) => new SlotEntry((SlotKind)kind, new WalPage(file, page)))),
            (1, Gen.Select(Gen.Int[1, 2], Gen.ULong, Gen.UInt).Select(static (kind, file, page) => new SlotEntry((SlotKind)kind, new WalPage(file, page)))));

    private static readonly Gen<SlotEntry[]> Arbitrary =
        AnyEntry.Array[SlotLeafCodec.Slots].Where(static entries => entries.Any(static entry => entry.HasRecord));

    [Fact]
    public void DecodingInvertsTheCanonicalEncoderForAnyLeaf() =>
        Gen.OneOf(Placed, Arbitrary, Placed.Select(WithOutliers)).Sample(static entries =>
        {
            var bytes = SlotLeafCodec.Encode(entries);
            var leaf = FrozenLeaf.Decode(bytes).Leaf();
            var decoded = new SlotEntry[SlotLeafCodec.Slots];
            leaf.DecodeAll(decoded);
            Assert.Equal(entries, decoded);
            Assert.All(Enumerable.Range(0, SlotLeafCodec.Slots), index => Assert.Equal(entries[index], leaf.Get(index)));
            Assert.Equal(entries.Count(static entry => entry.HasRecord), leaf.Count);
            Assert.Equal(bytes, SlotLeafCodec.Encode(decoded));
        });

    [Fact]
    public void ArbitraryBytesAreEitherAValidLeafOrRejectedWithAReason() =>
        Gen.Select(Arbitrary, Gen.Int[0, 600], Gen.Byte).Sample(static (entries, position, value) =>
        {
            var bytes = SlotLeafCodec.Encode(entries);
            var mutated = (byte[])bytes.Clone();
            mutated[position % mutated.Length] ^= (byte)(value | 1);
            var decoding = FrozenLeaf.Decode(mutated);
            if (decoding is InvalidDataException rejection)
            {
                // Rejecting the mutation for one of the format's reasons is the other permitted outcome.
                Assert.Contains(DecodeRejection.ReasonOf(rejection), LedgerStoreVectorTests.LeafReasons);
                return;
            }

            var leaf = decoding.Leaf();
            var decoded = new SlotEntry[SlotLeafCodec.Slots];
            leaf.DecodeAll(decoded);
            Assert.Equal(leaf.Count, decoded.Count(static entry => entry.HasRecord));

            // Point decoding uses its own rank and exception lookups, so it must agree with the sequential decode.
            Assert.All(Enumerable.Range(0, SlotLeafCodec.Slots), index => Assert.Equal(decoded[index], leaf.Get(index)));
        });

    /// <summary>
    /// An exception's virtual page is a <c>u64</c>, and entries are computed in 128-bit arithmetic (ledgerstore.md
    /// §7.4): a virtual page at or above 2<sup>63</sup> names its page when the file's base brings it within 32 bits, and
    /// is out of range otherwise.
    /// </summary>
    [Theory]
    [InlineData(9_223_372_036_854_775_808UL, 9_223_372_036_854_775_815UL, 7u)]
    [InlineData(0UL, 9_223_372_036_854_775_808UL, null)]
    public void AnExceptionsVirtualPageUsesAll64Bits(ulong fileBase, ulong virtualPage, uint? page)
    {
        // Slot 0 alone is present and every width is 0, so its page is the exception's virtual page less the base.
        var bytes = new byte[SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength + SlotLeafCodec.BitmapLength + SlotLeafCodec.ExceptionLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 1);
        var offset = SlotLeafCodec.HeaderLength;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + sizeof(ulong)), fileBase);
        offset += SlotLeafCodec.FileEntryLength;
        bytes[offset] = 1;
        offset += SlotLeafCodec.BitmapLength;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 1), virtualPage);

        if (page is not { } expectedPage)
        {
            AssertRejected(bytes, "page-out-of-range");
            return;
        }

        var leaf = FrozenLeaf.Decode(bytes).Leaf();
        var expected = new SlotEntry(SlotKind.Data, new WalPage(1, expectedPage));
        var decoded = new SlotEntry[SlotLeafCodec.Slots];
        leaf.DecodeAll(decoded);
        Assert.Equal(expected, decoded[0]);
        Assert.Equal(expected, leaf.Get(0));
    }

    [Fact]
    public void PlacementShapedLeavesCompressToAFewBitsPerSlot()
    {
        // One client: each record starts a fresh page, so pages advance by one per slot.
        var sequential = Enumerable.Range(0, SlotLeafCodec.Slots).Select(static index => new SlotEntry(SlotKind.Data, new WalPage(3, (uint)(1_000 + index)))).ToArray();
        Assert.Equal(SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength, SlotLeafCodec.Encode(sequential).Length);

        // Groups of 21 records share a page.
        var grouped = Enumerable.Range(0, SlotLeafCodec.Slots).Select(static index => new SlotEntry(SlotKind.Data, new WalPage(3, (uint)(index / 21)))).ToArray();
        Assert.True(SlotLeafCodec.Encode(grouped).Length * 8 <= 3 * SlotLeafCodec.Slots + 8 * 40);

        // A recovery write far ahead of its neighbours becomes one exception rather than widening every residual.
        var recovered = (SlotEntry[])sequential.Clone();
        recovered[200] = new SlotEntry(SlotKind.Data, new WalPage(3, 9_000_000));
        Assert.Equal(SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength + SlotLeafCodec.ExceptionLength, SlotLeafCodec.Encode(recovered).Length);

        // A file roll inside the leaf costs one more file-table entry and one bit per slot.
        var rolled = Enumerable.Range(0, SlotLeafCodec.Slots)
            .Select(static index => new SlotEntry(SlotKind.Data, index < 100 ? new WalPage(3, (uint)(60_000 + index)) : new WalPage(4, (uint)(index - 100))))
            .ToArray();
        Assert.True(SlotLeafCodec.Encode(rolled).Length <= SlotLeafCodec.HeaderLength + 2 * SlotLeafCodec.FileEntryLength + 32 + 8);
    }

    [Fact]
    public void BoundaryLeavesRoundTrip()
    {
        var single = new SlotEntry[SlotLeafCodec.Slots];
        single[255] = new SlotEntry(SlotKind.Data, new WalPage(ulong.MaxValue, uint.MaxValue));
        var files = Enumerable.Range(0, SlotLeafCodec.Slots).Select(static index => new SlotEntry(SlotKind.Faulty, new WalPage((ulong)(index * 3 + 1), uint.MaxValue - (uint)index))).ToArray();
        foreach (var entries in new[] { single, files })
        {
            var leaf = FrozenLeaf.Decode(SlotLeafCodec.Encode(entries)).Leaf();
            Assert.All(Enumerable.Range(0, SlotLeafCodec.Slots), index => Assert.Equal(entries[index], leaf.Get(index)));
        }

        Assert.Throws<ArgumentException>(() => SlotLeafCodec.Encode(new SlotEntry[SlotLeafCodec.Slots]));
    }

    [Fact]
    public void EveryRuleBreakingLeafIsRejectedForItsReason()
    {
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        for (var index = 0; index < 200; index++)
        {
            entries[index] = new SlotEntry(index % 7 == 0 ? SlotKind.Faulty : SlotKind.Data, new WalPage(5, (uint)index));
        }

        entries[100] = new SlotEntry(SlotKind.Data, new WalPage(5, 3_000_000));
        entries[199] = new SlotEntry(SlotKind.Data, new WalPage(6, 0));
        var valid = SlotLeafCodec.Encode(entries);
        _ = FrozenLeaf.Decode(valid).Leaf();
        var bitmap = SlotLeafCodec.HeaderLength + 2 * SlotLeafCodec.FileEntryLength;
        Reject(valid, bytes => bytes[0] |= 2, "reserved-flags");
        Reject(valid, bytes => bytes[1] = 2, "width-out-of-range");
        Reject(valid, bytes => bytes[2] = 9, "width-out-of-range");
        Reject(valid, bytes => bytes[3] = 64, "width-out-of-range");
        Reject(valid, bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0), "file-count-out-of-range");
        Reject(valid, bytes => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength), 5), "file-table-order");

        // Eight more present slots than the arrays and exceptions account for.
        Reject(valid, bytes => bytes[bitmap + 30] = 0xFF, "length-mismatch");
        AssertRejected([.. valid, 0], "length-mismatch");
        AssertRejected(valid[..^1], "length-mismatch");
        AssertRejected(valid[..(SlotLeafCodec.HeaderLength - 1)], "truncated");
        AssertRejected(valid[..bitmap], "truncated");

        // An exception's virtual page far above its file's base names a page beyond 32 bits.
        Reject(valid, bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), long.MaxValue), "page-out-of-range");

        // A leaf of DATA alone packs no state bits, and a Faulty entry widens the states to their widest, one bit, so no
        // state is unknown (ledgerstore.md §7.4).
        Assert.Equal(1, valid[1]);
        var data = entries.Select(static entry => entry.HasRecord ? new SlotEntry(SlotKind.Data, entry.Page) : entry).ToArray();
        Assert.Equal(0, SlotLeafCodec.Encode(data)[1]);

        // A full bitmap must use the all-present flag.
        var full = Enumerable.Range(0, SlotLeafCodec.Slots).Select(static index => new SlotEntry(SlotKind.Data, new WalPage(1, (uint)index))).ToArray();
        var flagged = SlotLeafCodec.Encode(full);
        var bitmapped = new byte[flagged.Length + SlotLeafCodec.BitmapLength];
        flagged.AsSpan(0, SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength).CopyTo(bitmapped);
        bitmapped[0] = 0;
        bitmapped.AsSpan(SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength, SlotLeafCodec.BitmapLength).Fill(0xFF);
        flagged.AsSpan(SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength).CopyTo(bitmapped.AsSpan(SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength + SlotLeafCodec.BitmapLength));
        AssertRejected(bitmapped, "bitmap-empty-or-full");

        static void Reject(byte[] valid, Action<byte[]> mutate, string reason)
        {
            var bytes = (byte[])valid.Clone();
            mutate(bytes);
            AssertRejected(bytes, reason);
        }
    }

    [Fact]
    public void TheFormatIsPinnedByAGoldenVector()
    {
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        entries[0] = new SlotEntry(SlotKind.Data, new WalPage(7, 10));
        entries[1] = new SlotEntry(SlotKind.Data, new WalPage(7, 11));
        entries[2] = new SlotEntry(SlotKind.Faulty, new WalPage(7, 12));
        entries[3] = new SlotEntry(SlotKind.Data, new WalPage(8, 0));
        Assert.Equal(
            "00010100" + "0200" + "0000" + "0a00000000000000" + "0001000000000000"
            + "0700000000000000" + "0000000000000000" + "0800000000000000" + "0d00000000000000"
            + "0f" + new string('0', 62)
            + "04" + "08",
            Convert.ToHexStringLower(SlotLeafCodec.Encode(entries)));
    }

    [Fact]
    public void AnEntryOccupiesSixteenBytes() => Assert.Equal(16, Unsafe.SizeOf<SlotEntry>());

    /// <summary>
    /// The packed states are not the <see cref="SlotKind"/> values: Missing has no presence bit, and DATA and Faulty pack
    /// as 0 and 1 (ledgerstore.md §7.4).
    /// </summary>
    [Fact]
    public void ThePackedStatesAreZeroForDataAndOneForFaulty()
    {
        var entries = new SlotEntry[SlotLeafCodec.Slots];
        entries[0] = new SlotEntry(SlotKind.Data, new WalPage(1, 0));
        entries[1] = new SlotEntry(SlotKind.Faulty, new WalPage(1, 1));
        entries[2] = new SlotEntry(SlotKind.Data, new WalPage(1, 2));
        var bytes = SlotLeafCodec.Encode(entries);
        var states = SlotLeafCodec.HeaderLength + SlotLeafCodec.FileEntryLength + SlotLeafCodec.BitmapLength;
        Assert.Equal(1, bytes[1]);
        Assert.Equal(0b010, bytes[states]);

        var decoded = new SlotEntry[SlotLeafCodec.Slots];
        FrozenLeaf.Decode(bytes).Leaf().DecodeAll(decoded);
        Assert.Equal(entries, decoded);
    }

    private static void AssertRejected(byte[] bytes, string reason) =>
        Assert.Equal(reason, DecodeRejection.ReasonOf(Assert.IsType<InvalidDataException>(FrozenLeaf.Decode(bytes).Value)));

    private static SlotEntry[] WithOutliers(SlotEntry[] entries)
    {
        var copy = (SlotEntry[])entries.Clone();
        copy[0] = new SlotEntry(SlotKind.Data, new WalPage(entries[0].Page.File, uint.MaxValue));
        copy[200] = new SlotEntry(SlotKind.Faulty, new WalPage(entries[0].Page.File + 9, 0));
        copy[255] = default;
        return copy;
    }
}
