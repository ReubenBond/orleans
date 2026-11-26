using System.Runtime.CompilerServices;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The composition's wire boundary (orleans.md §5.12, OR-I18): a payload crosses into an Orleans buffer and back into
/// Clog's page pool unchanged, whatever pages either side splits it over; receiving releases what Orleans delivered; and
/// the caller releases a read's DATA on every path, its reply checks' refusals included.
/// </summary>
public sealed class WireBuffersTests : IDisposable
{
    private static readonly NodeId Node = new(Guid.NewGuid());

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly OrleansBufferLedger _wire = new();

    public void Dispose()
    {
        try
        {
            _wire.Dispose();
        }
        finally
        {
            _leaks.Dispose();
        }
    }

    /// <summary>
    /// orleans.md §5.5: a page copied from a snapshot's chunks holds the content's bytes at its offset, whatever the chunks'
    /// lengths, empty chunks among them; a range which passes the content's end is refused with
    /// <see cref="ArgumentOutOfRangeException"/> naming the length. The slow path, concatenating the chunks and slicing, is the
    /// oracle.
    /// </summary>
    [Fact]
    public void ACopyFromChunksIsTheirConcatenationsRange() =>
        Gen.Select(Gen.Int[0, 40].Array[0, 6], Gen.Int[0, 130], Gen.Int[1, 130]).Sample((lengths, offset, length) => _leaks.InScope(() => CopyCase(lengths, offset, length)));

    private static void CopyCase(int[] lengths, int offset, int length)
    {
        var content = Enumerable.Range(0, lengths.Sum()).Select(static index => (byte)(index % 251)).ToArray();
        var chunks = new ArcBuffer[lengths.Length];
        var at = 0;
        for (var index = 0; index < lengths.Length; index++)
        {
            chunks[index] = ArcBuffer.Wrap(content[at..(at + lengths[index])]);
            at += lengths[index];
        }

        try
        {
            if (offset + length > content.Length)
            {
                Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => WireBuffers.CopyToOrleans(chunks, offset, length)).ParamName);
                return;
            }

            var copy = WireBuffers.CopyToOrleans(chunks, offset, length);
            try
            {
                Assert.Equal(content.AsSpan(offset, length).ToArray(), copy.ToArray());
            }
            finally
            {
                copy.Dispose();
            }
        }
        finally
        {
            foreach (var chunk in chunks)
            {
                chunk.Dispose();
            }
        }
    }

    /// <summary>
    /// A payload crosses unchanged both ways: empty, within one page, longer than Orleans' 16 KiB pages, and across more
    /// than one of Clog's 1 MiB pages, from a source whose first page starts at an offset. The Orleans copy is one page
    /// which holds exactly the payload, so no empty page rides along with it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16 * 1024 + 1)]
    [InlineData((2 << 20) + 7)]
    public void APayloadCrossesUnchangedBothWays(int length)
    {
        var bytes = new byte[length + 3];
        new Random(length).NextBytes(bytes);
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        writer.AdvanceReader(3);
        using var source = writer.ConsumeSlice(length);

        var sent = WireBuffers.CopyToOrleans(source);
        var held = sent;
        Assert.Equal(bytes[3..], sent.ToArray());
        Assert.True(length == 0 || sent is { Offset: 0 } && sent.First!.Length == length);

        using var received = WireBuffers.MoveToArcBuffer(ref sent);
        Assert.Equal(bytes[3..], received.ToArray());
        Assert.True(sent is { First: null, Length: 0 });
        Assert.Equal(length > 0, WireBytes.IsReleased(held));
        Assert.Equal(bytes[3..], source.ToArray());
    }

    /// <summary>
    /// A buffer Orleans delivers as a slice of its receive pages, at an offset into the first and across many of its
    /// 16 KiB pages, moves into Clog's page pool: once Orleans reuses the released pages, the moved bytes are unchanged.
    /// A small one lands in one of Clog's pooled minimum pages.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(16 * 1024 + 1)]
    [InlineData((2 << 20) + 7)]
    public void AReceivedSliceMovesIntoTheArcBufferPool(int length)
    {
        var bytes = new byte[length + 5];
        new Random(length).NextBytes(bytes);
        var writer = new OrleansBufferWriter();
        writer.Write(bytes);
        writer.AdvanceReader(5);
        var received = writer.ConsumeSlice(length);
        var held = received;
        writer.Dispose();

        using var moved = WireBuffers.MoveToArcBuffer(ref received);
        Assert.True(WireBytes.IsReleased(held));
        using (var reuse = new OrleansBufferWriter())
        {
            reuse.Write(Enumerable.Repeat((byte)0xFF, bytes.Length + (16 << 10)).ToArray());
        }

        Assert.Equal(bytes[5..], moved.ToArray());
        Assert.True(length > 1024 || moved.First.IsMinimumSize);
    }

    /// <summary>
    /// A small copy into Orleans wraps an exact-sized ordinary array below the large object heap's threshold rather than
    /// renting from Orleans' pool of pinned 16 KiB pages or leaking a 32 KiB <see cref="System.Buffers.ArrayPool{T}"/>
    /// buffer, so a copy Orleans keeps is reclaimed as an ordinary small array (orleans.md §5.12).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(619)]
    [InlineData(4 << 10)]
    [InlineData(16 << 10)]
    public void ASmallCopyIntoOrleansPoolIsAnOrdinaryArray(int length)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[length]);
        using var source = writer.ConsumeSlice(length);

        var copy = WireBuffers.CopyToOrleans(source);
        var page = copy.First!;
        var array = page.Array;
        Assert.False(page.IsMinimumSize);
        Assert.Equal(length, array.Length);
        copy.Dispose();

        Assert.InRange(array.Length, 1, 84_975);
    }

    /// <summary>
    /// A small copy which Orleans keeps and never releases, as on its drop paths (OR-I18), strands nothing: once dropped,
    /// a collection reclaims its array, which no pool holds (orleans.md §5.12).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4 << 10)]
    [InlineData(16 << 10)]
    public void ASmallCopyOrleansNeverReleasesIsReclaimedOnceDropped(int length)
    {
        var array = DropAnUnreleasedCopy(length);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(array.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropAnUnreleasedCopy(int length)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[length]);
        using var source = writer.ConsumeSlice(length);
        var copy = WireBuffers.CopyToOrleans(source);
        return new(copy.First!.Array);
    }

    /// <summary>The caller copies a read's DATA into Clog's page pool and releases the buffer Orleans delivered.</summary>
    [Fact]
    public void TheCallerCopiesDataIntoTheArcBufferPoolAndReleasesWhatOrleansDelivered()
    {
        var data = _wire.Track(WireBytes.Data(1, 2, 3));

        Assert.True(LedgerReplicaCallResults.Read(Node, data, reserved: 3, ranged: false).TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            Assert.Equal([1, 2, 3], batch.ToArray());
        }

        Assert.True(WireBytes.IsReleased(data.Bytes));
    }

    /// <summary>The caller releases the DATA of a reply which fails a check: longer than the reply reserved, or a ranged read's of another length.</summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(4, true)]
    public void TheCallerReleasesTheDataOfAReplyWhichFailsACheck(int reserved, bool ranged)
    {
        var data = _wire.Track(WireBytes.Data(1, 2, 3));

        _ = Assert.Throws<InvalidDataException>(() => LedgerReplicaCallResults.Read(Node, data, reserved, ranged));
        Assert.True(WireBytes.IsReleased(data.Bytes));
    }

    /// <summary>
    /// Direct encoding of a <see cref="JournalEntryRecord"/> into Orleans' page pool matches
    /// <see cref="JournalEntryRecord.ToArcBuffer"/> byte-for-byte across Append (small and multi-page), Replace (empty and
    /// non-empty), and Delete records, keeps the single non-POH page contract (orleans.md §5.12), round-trips through
    /// <see cref="WireBuffers.MoveToArcBuffer"/> and <see cref="JournalEntryRecord.Decode"/>, and rejects a default record.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(32 * 1024 + 17)]
    public void AJournalEntryRecordCopiesDirectlyIntoOrleansMatchingToArcBuffer(int payloadLength)
    {
        var key = JournalRowKey.Encode("wire-journal");
        var bytes = new byte[payloadLength];
        new Random(payloadLength).NextBytes(bytes);
        using var payload = ArcBuffer.Wrap(bytes);

        var token = new RowToken([1, 2]);
        JournalEntryRecord[] records =
        [
            JournalEntryRecord.Append(key, new JournalVersion(token, 4), payload),
            JournalEntryRecord.Replace(key, new JournalVersion(token, 5), payload),
            JournalEntryRecord.Replace(key, new JournalVersion(token, 6), default),
            JournalEntryRecord.Delete(key, new JournalVersion(token, 7), DateTimeOffset.UnixEpoch.AddMinutes(9)),
        ];

        foreach (var record in records)
        {
            using (record)
            {
                using var expected = record.ToArcBuffer();
                var copy = WireBuffers.CopyToOrleans(record);
                var held = copy;
                Assert.Equal(expected.ToArray(), copy.ToArray());
                Assert.True(copy is { Offset: 0 } && copy.First!.Length == record.Length);
                Assert.Equal(record.Length, copy.First!.Array.Length);
                Assert.False(copy.First!.IsMinimumSize);

                using var moved = WireBuffers.MoveToArcBuffer(ref copy);
                Assert.True(WireBytes.IsReleased(held));
                Assert.Null(JournalEntryRecord.Decode(key, moved, out var decoded));
                using (decoded)
                {
                    Assert.Equal(record.Entry, decoded.Entry);
                    Assert.Equal(record.Payload.ToArray(), decoded.Payload.ToArray());
                }
            }
        }

        _ = Assert.Throws<InvalidOperationException>(() => WireBuffers.CopyToOrleans(default(JournalEntryRecord)));
    }

    /// <summary>
    /// Property check across arbitrary payload lengths (including Orleans' 16 KiB minimum page size boundary and
    /// multi-page Clog buffers) and source offsets: <see cref="WireBuffers.CopyToOrleans(ArcBuffer)"/> produces an
    /// exact-sized non-POH external Orleans buffer and <see cref="WireBuffers.MoveToArcBuffer"/> round-trips it cleanly
    /// into Clog's pool while releasing the Orleans buffer.
    /// </summary>
    [Fact]
    public void WireRoundTripPreservesArbitraryPayloadsAndExactOrleansBufferSizing()
    {
        var lengthGen = Gen.OneOf(
            Gen.Int[0, 64],
            Gen.Const(OrleansBufferWriter.MinimumPageSize - 1),
            Gen.Const(OrleansBufferWriter.MinimumPageSize),
            Gen.Const(OrleansBufferWriter.MinimumPageSize + 1),
            Gen.Int[OrleansBufferWriter.MinimumPageSize - 32, OrleansBufferWriter.MinimumPageSize * 3 + 32]);

        Gen.Select(lengthGen, Gen.Int[0, 31], Gen.Byte).Sample((length, leadOffset, seed) => _leaks.InScope(() =>
        {
            var raw = new byte[leadOffset + length];
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = unchecked((byte)(seed + i * 29));
            }

            var expected = raw[leadOffset..];
            using var writer = new ArcBufferWriter();
            writer.Write(raw);
            writer.AdvanceReader(leadOffset);
            using var source = writer.ConsumeSlice(length);

            var sent = WireBuffers.CopyToOrleans(source);
            var held = sent;
            if (length == 0)
            {
                Assert.True(sent.IsEmpty);
                Assert.Null(sent.First);
            }
            else
            {
                Assert.Equal(0, sent.Offset);
                Assert.Equal(length, sent.Length);
                Assert.Equal(length, sent.First!.Length);
                Assert.Equal(length, sent.First!.Array.Length);
                Assert.False(sent.First!.IsMinimumSize);
                Assert.Equal(expected, sent.ToArray());
            }

            using var received = WireBuffers.MoveToArcBuffer(ref sent);
            Assert.True(sent is { First: null, Length: 0 });
            Assert.Equal(length > 0, WireBytes.IsReleased(held));
            Assert.Equal(expected, received.ToArray());
        }), iter: 120);
    }
}
