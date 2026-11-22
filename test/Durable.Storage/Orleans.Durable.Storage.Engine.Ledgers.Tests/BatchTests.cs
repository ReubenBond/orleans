using System.Buffers;
using System.Buffers.Binary;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the batch codec at its limits and its rule order (ledgers.md §4, §7; conventions.md §1, §8): rows the encoder
/// refuses, the longest encoding, keys, version bases, tokens, counts, ordinals, and bodies at their bounds from both
/// sides, owned rows which outlive the batch's buffer, a valid batch decoded without allocating, a point read's
/// defining row by ordinal, and bytes which break two rules rejected for the first.
/// </summary>
public sealed class BatchTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void TheBatchReaderAdvancesAcrossPagesAndKeepsTheLastPageAtTheEnd()
    {
        using var buffer = PagedBuffers.Create([1, 2, 3, 4, 5, 6, 7, 8, 9], [2, 3, 1]);
        var reader = new BatchReader(buffer, buffer.Length);
        Assert.Equal(0, reader.MoveToPageWithNextByte());
        Assert.True(reader.TryRead(out byte first));
        Assert.Equal(1, first);
        Assert.True(reader.TryTakeContiguous(1, out var contiguous));
        Assert.Equal([2], contiguous.ToArray());
        Assert.Equal(2, reader.MoveToPageWithNextByte());
        Assert.False(reader.TryTakeContiguous(4, out _));
        Assert.Equal(2, reader.Position);

        Span<byte> copied = stackalloc byte[4];
        Assert.True(reader.TryCopyTo(copied));
        Assert.Equal([3, 4, 5, 6], copied.ToArray());
        Assert.True(reader.TrySkip(2));
        Assert.Equal(8, reader.MoveToPageWithNextByte());
        Assert.True(reader.TryTakeContiguous(1, out contiguous));
        Assert.Equal([9], contiguous.ToArray());

        for (var repeat = 0; repeat < 2; repeat++)
        {
            Assert.Equal(8, reader.MoveToPageWithNextByte());
            Assert.Equal((9, 0), (reader.Position, reader.Remaining));
            Assert.False(reader.TryRead(out byte _));
            Assert.True(reader.TryTakeContiguous(0, out contiguous));
            Assert.True(contiguous.IsEmpty);
            Assert.True(reader.TrySkip(0));
            Assert.True(reader.TryCopyTo([]));
        }
    }

    [Fact]
    public void TheBatchReaderTakesNothingWhenAFieldPassesItsLimit()
    {
        using var buffer = PagedBuffers.Create([1, 2, 3, 4, 5, 6], 2);
        var reader = new BatchReader(buffer, 5);
        Span<byte> destination = stackalloc byte[6];
        destination.Fill(0x7f);
        Assert.False(reader.TryCopyTo(destination));
        Assert.False(reader.TrySkip(ulong.MaxValue));
        Assert.False(reader.TryTakeContiguous(6, out _));
        Assert.Equal(0, reader.Position);
        Assert.Equal([0x7f, 0x7f, 0x7f, 0x7f, 0x7f, 0x7f], destination.ToArray());

        Assert.True(reader.TryRead(out uint value));
        Assert.Equal(0x04030201U, value);
        Assert.False(reader.TryRead(out uint failed));
        Assert.Equal(0U, failed);
        Assert.False(reader.TryTakeContiguous(2, out _));
        Assert.False(reader.TrySkip(2));
        Assert.False(reader.TryCopyTo(destination[..2]));
        Assert.Equal((4, 1), (reader.Position, reader.Remaining));
        Assert.Equal([0x7f, 0x7f, 0x7f, 0x7f, 0x7f, 0x7f], destination.ToArray());
        Assert.True(reader.TryRead(out byte last));
        Assert.Equal(5, last);
        Assert.False(reader.TryRead(out byte _));
        Assert.Equal((5, 0), (reader.Position, reader.Remaining));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(128UL)]
    [InlineData(ulong.MaxValue)]
    public void TheBatchReaderReadsVarintsAcrossPagesAndStopsAtItsLimit(ulong value)
    {
        Span<byte> encoding = stackalloc byte[Varint.MaxLength];
        var length = Varint.Write(encoding, value);
        using var buffer = PagedBuffers.Create([42, .. encoding[..length], 17], 1);
        var reader = new BatchReader(buffer, buffer.Length);
        Assert.True(reader.TrySkip(1));
        Assert.Equal(Varint.Status.Valid, reader.TryReadVarint(out var read));
        Assert.Equal(value, read);
        Assert.Equal(length + 1, reader.Position);
        Assert.True(reader.TryRead(out byte next));
        Assert.Equal(17, next);

        for (var prefix = 0; prefix < length; prefix++)
        {
            var limited = new BatchReader(buffer, prefix + 1);
            Assert.True(limited.TrySkip(1));
            Assert.Equal(Varint.Status.Unterminated, limited.TryReadVarint(out _));
            Assert.Equal((prefix + 1, 0), (limited.Position, limited.Remaining));
        }
    }

    [Fact]
    public void TheBatchReaderReadsAnEmptyBufferWithoutMoving()
    {
        using var buffer = PagedBuffers.Create([1, 2], 1);
        foreach (var emptyBuffer in (ArcBuffer[])[default, buffer.UnsafeSlice(1, 0)])
        {
            var reader = new BatchReader(emptyBuffer, 0);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Assert.Equal(0, reader.MoveToPageWithNextByte());
                Assert.True(reader.TryTakeContiguous(0, out var empty));
                Assert.True(empty.IsEmpty);
                Assert.True(reader.TryCopyTo([]));
                Assert.True(reader.TrySkip(0));
                Assert.False(reader.TryTakeContiguous(1, out _));
                Assert.False(reader.TryRead(out byte _));
                Assert.Equal(Varint.Status.Unterminated, reader.TryReadVarint(out _));
                Assert.Equal((0, 0), (reader.Position, reader.Remaining));
            }
        }
    }

    [Fact]
    public void EncodingRequiresAtLeastOneConstructedRow()
    {
        Assert.Equal("rows", Assert.Throws<ArgumentException>(() => BatchView.Encode([])).ParamName);
        Assert.Equal("rows", Assert.Throws<ArgumentException>(() => BatchView.Encode([default])).ParamName);
    }

    /// <summary>
    /// A row whose body has been released fails the encoding before anything is written, so no page is left rented.
    /// </summary>
    [Fact]
    public void EncodingADisposedRowFailsBeforeWriting()
    {
        using var kept = PagedBuffers.Create([1, 2], []);
        using var keptRow = new Row([1], RowKind.Append, kept);
        var body = PagedBuffers.Create([3, 4], []);
        var disposed = new Row([2], RowKind.Replace, body);
        body.Dispose();
        disposed.Dispose();

        _ = Assert.Throws<InvalidOperationException>(() => BatchView.Encode([keptRow, disposed]));
    }

    /// <summary>
    /// An encoding which could be longer than <see cref="int.MaxValue"/> bytes fails before it copies anything
    /// (conventions.md §8).
    /// </summary>
    [Fact]
    public void AnEncodingLongerThanIntMaxValueOverflows()
    {
        using var body = PagedBuffers.Create(new byte[1 << 20], []);
        var rows = new Row[2_048];
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index] = new Row([0x01], RowKind.Append, body);
        }

        try
        {
            _ = Assert.Throws<OverflowException>(() => BatchView.Encode(rows));
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }

    /// <summary>
    /// Keys of 1 and 65,535 bytes, empty bodies, a body longer than a <c>u16</c> holds, and a body longer than the
    /// largest page a writer rents round trip, from the fewest pages and from pages of 1 to 28,657 bytes repeated across
    /// the whole batch, and read as the independent reading of the rules reads them. The batch is longer than 1 MiB, so
    /// the encoder writes it across pages (buffers.md §8).
    /// </summary>
    [Fact]
    public void RowsAtTheirLimitsRoundTrip()
    {
        RowSample[] rows =
        [
            new(RowKind.Append, [0x00], []),
            new(RowKind.Replace, [.. Enumerable.Range(0, KeyRange.MaxKeyLength).Select(static index => (byte)(index & 0xff))], [0x01]),
            new(RowKind.Delete, [0xff], []),
            new(RowKind.Append, [0x7f], new byte[(1 << 20) + 3 * ArcBufferWriter.MinimumPageSize]),
            new(RowKind.Replace, [0x10], [.. Enumerable.Range(0, ushort.MaxValue + 4_465).Select(static index => (byte)(index * 7 % 251))]),
            new(RowKind.Append, [0x00], [0x02]),
        ];
        var bytes = Encode(rows, bodyPageLengths: [1_000], out var pages);
        Assert.True(pages > 1, "The encoder wrote a batch longer than the largest page into one page.");
        var reading = LedgerSegmentRules.ReadSegment([bytes], firstSlot: 0);
        Assert.Null(reading.Reason);
        AssertRows(rows, reading.Rows.Select(RowSample.Of));
        Assert.False(LedgerSegmentRules.ReadBatch(bytes).Rows[^1].IsDefining);

        foreach (int[] pageLengths in (int[][])[[], [7, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597, 2584, 4181, 6765, 10946, 17711, 28657]])
        {
            using var buffer = PagedBuffers.Create(bytes, pageLengths);
            AssertRows(rows, Decode(buffer));
        }
    }

    /// <summary>
    /// Version bases and tokens of 32 bytes, a count whose varint takes 10 bytes, and ordinals 127 and 128 round trip,
    /// and the batch rules read them; a token of 33 bytes is refused at construction.
    /// </summary>
    [Fact]
    public void VersionsTokensAndOrdinalsAtTheirLimitsRoundTrip()
    {
        var longest = Enumerable.Range(0, RowToken.MaxLength).Select(static index => (byte)index).ToArray();
        var batch = new RuleBatch(IsStart: false, Offset: 128, OrdinalBase: 200,
        [
            new RuleRow(0, IsDefining: false, Ordinal: 127, null, null, 0, null, [1]),
            new RuleRow(1, IsDefining: false, Ordinal: 128, null, null, 0, null, []),
            new RuleRow(2, IsDefining: true, Ordinal: 200, [9], longest, ulong.MaxValue - 1, longest, [2]),
        ]);
        var bytes = LedgerSegmentRules.Encode(batch);
        Assert.Equal(bytes, LedgerBatches.Encode(batch));
        var reading = LedgerSegmentRules.ReadBatch(bytes);
        Assert.Null(reading.Reason);
        using var buffer = PagedBuffers.Create(bytes, 3);
        Assert.Null(BatchView.Decode(buffer, out var view));
        Assert.Equal((false, 128UL, 200UL, 3), (view.IsSegmentStart, view.LedgerSegmentOffset, view.OrdinalBase, view.Count));
        foreach (var row in view)
        {
            Assert.True(BatchTarget.Matches(row, reading.Rows[row.Index]));
        }

        Assert.Equal(RowToken.MaxLength, new RowToken(longest).Length);
        _ = Assert.Throws<ArgumentException>(() => new RowToken(new byte[RowToken.MaxLength + 1]));
    }

    /// <summary>
    /// A row's defining and referencing fields are lent as its batch alone records them: a referencing row carries no key,
    /// version, or token, and a defining row's ordinal is implied by the batch's ordinal base and the defining rows before
    /// it.
    /// </summary>
    [Fact]
    public void ARowLendsWhatItsBatchAloneRecords()
    {
        var batch = new RuleBatch(IsStart: false, Offset: 1, OrdinalBase: 4,
        [
            new RuleRow(0, IsDefining: true, Ordinal: 4, [1], [7], 3, [8], [5]),
            new RuleRow(1, IsDefining: false, Ordinal: 4, null, null, 0, null, [6]),
            new RuleRow(2, IsDefining: true, Ordinal: 5, [2], [], 0, [], []),
        ]);
        using var buffer = PagedBuffers.Create(LedgerSegmentRules.Encode(batch), []);
        Assert.Null(BatchView.Decode(buffer, out var view));
        var index = 0;
        foreach (var row in view)
        {
            var expected = batch.Rows[index++];
            Assert.Equal((expected.IsDefining, expected.Ordinal, (RowKind)expected.Kind), (row.IsDefining, row.Ordinal, row.Kind));
            if (!row.IsDefining)
            {
                Assert.Equal(3, ReferencingFieldsThrow(row));
            }
        }

        Assert.Equal(3, index);
    }

    /// <summary>
    /// A point read finds a batch's defining row by its ordinal, and an ordinal the batch does not define answers
    /// <c>unknown-ordinal</c>, as a stale key-index entry does (ledgers.md §2.5, §7).
    /// </summary>
    [Fact]
    public void APointReadFindsADefiningRowByOrdinal()
    {
        var batch = new RuleBatch(IsStart: false, Offset: 2, OrdinalBase: 3,
        [
            new RuleRow(0, IsDefining: false, Ordinal: 1, null, null, 0, null, [1]),
            new RuleRow(0, IsDefining: true, Ordinal: 3, [0x0a], [], 0, [0x0b], [2]),
            new RuleRow(0, IsDefining: true, Ordinal: 4, [0x0c], [], 0, [], [3]),
        ]);
        using var buffer = PagedBuffers.Create(LedgerSegmentRules.Encode(batch), []);
        Assert.Null(BatchView.Decode(buffer, out var view));
        Assert.Null(view.FindDefining(4, out var found));
        Assert.Equal([0x0c], found.Key.ToArray());
        Assert.Equal(2, found.Index);
        Assert.Null(view.FindDefining(3, out found));
        Assert.Equal([0x0b], found.Token.ToArray());
        foreach (var ordinal in (ulong[])[1, 2, 5])
        {
            Assert.Equal("unknown-ordinal", Reason(view.FindDefining(ordinal, out _)));
        }
    }

    /// <summary>
    /// An owned row copies its key and retains its body's pages, and a retained body keeps its pages, so both outlive the
    /// batch's buffer; an empty body is retained as the default buffer.
    /// </summary>
    [Fact]
    public void OwnedRowsAndRetainedBodiesOutliveTheirBatchsBuffer()
    {
        RowSample[] samples = [new(RowKind.Replace, [1, 2], [6, 7, 8]), new(RowKind.Append, [9], [])];
        var owned = new List<Row>();
        var retained = new List<ArcBuffer>();
        var buffer = PagedBuffers.Create(Encode(samples), [13, 5, 9]);
        try
        {
            Assert.Null(BatchView.Decode(buffer, out var batch));
            using var segment = new LedgerSegmentView(new LedgerId(1));
            Assert.Null(segment.Advance(Slot.Zero, batch));
            foreach (var row in segment.Rows)
            {
                owned.Add(row.ToRow());
                retained.Add(row.RetainBody());
            }

            var lent = buffer.UnsafeSlice(0, 1);
            buffer.Dispose();
            _ = Assert.Throws<InvalidOperationException>(() => lent.Length);
            AssertRows(samples, owned.Select(RowSample.Of));
            Assert.Equal(samples.Select(static sample => sample.Body), retained.Select(static body => body.ToArray()));
            Assert.True(retained[1].IsDefault);
        }
        finally
        {
            buffer.Dispose();
            foreach (var row in owned)
            {
                row.Dispose();
            }

            foreach (var body in retained)
            {
                body.Dispose();
            }
        }
    }

    /// <summary>
    /// A valid batch decodes without allocating, from one page or many, and its rows are lent without allocating while
    /// each key lies in one page.
    /// </summary>
    [Fact]
    public void AValidBatchDecodesWithoutAllocating()
    {
        RowSample[] samples = [new(RowKind.Append, [1, 2, 3], [7, 8]), new(RowKind.Delete, [9], []), new(RowKind.Append, [1, 2, 3], [9])];
        var bytes = Encode(samples);
        using var whole = PagedBuffers.Create(bytes, []);
        using var paged = PagedBuffers.Create(bytes, 3);
        _ = Walk(whole);
        _ = Walk(paged);

        Assert.Equal(0, Allocated(() => BatchView.Decode(paged, out _)));
        Assert.Equal(0, Allocated(() => BatchView.Decode(whole, out _)));
        Assert.Equal(0, Allocated(() => Walk(whole)));

        static long Allocated(Func<object?> body)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = body();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        static object? Walk(ArcBuffer buffer)
        {
            _ = BatchView.Decode(buffer, out var batch);
            foreach (var row in batch)
            {
                _ = (row.IsDefining ? row.Key.Length + row.Token.Length : 0) + row.Body.Length;
            }

            return null;
        }
    }

    [Fact]
    public void TheDefaultBatchViewLendsNoRows()
    {
        var empty = default(BatchView);
        Assert.Equal(0, empty.Count);
        var lent = 0;
        foreach (var row in empty)
        {
            lent += 1 + row.Index;
        }

        Assert.Equal(0, lent);
    }

    /// <summary>A marker holds no rows and begins a segment.</summary>
    [Fact]
    public void AMarkerHoldsNoRows()
    {
        var bytes = LedgerSegmentRules.Encode(new RuleBatch(IsStart: true, 0, 0, []));
        using var buffer = PagedBuffers.Create(bytes, []);
        Assert.Null(BatchView.Decode(buffer, out var view));
        Assert.Equal((true, 0), (view.IsSegmentStart, view.Count));
        Assert.Equal(LedgerBatch.MaxHeaderLength(0) - 2 * Varint.MaxLength + 2, bytes.Length);
    }

    /// <summary>
    /// Varints at their bounds: 10 bytes accepted, a 10th byte past the <c>u64</c> and an 11-byte form rejected, and a
    /// longer form than the shortest rejected, in the header as <c>bad-varint</c> and in a row as <c>bad-varint</c>, or as
    /// <c>row-truncated</c> when it reaches the CRC-32C unterminated.
    /// </summary>
    [Fact]
    public void VarintsAreCheckedAtTheirBounds()
    {
        byte[] tenBytes = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01];
        AssertAccepted(Header(start: false, [.. tenBytes], [0x00], [0x01], Defining()));
        AssertRejected("bad-varint", Header(start: false, [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x02], [0x00], [0x01], Defining()));
        AssertRejected("bad-varint", Header(start: false, [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00], [0x00], [0x01], Defining()));
        AssertRejected("bad-varint", Header(start: false, [0x81, 0x00], [0x00], [0x01], Defining()));
        AssertRejected("bad-varint", Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x01, 0x6b, 0x00, 0x80, 0x00, 0x00, 0x00]));
        AssertRejected("row-truncated", Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x01, 0x6b, 0x00, 0x80]));
        AssertRejected("bad-varint", Header(start: true, [0x00], [0x00], [0x80]));
    }

    /// <summary>Keys of 0 and 65,536 bytes are rejected, and a body one byte past the CRC-32C is truncated.</summary>
    [Fact]
    public void KeysAndBodiesAreCheckedAtTheirBounds()
    {
        AssertRejected("empty-key", Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x00, 0x00, 0x00, 0x00, 0x00]));
        var tooLong = new List<byte> { 0x04 };
        LedgerSegmentRules.AddVarint(tooLong, KeyRange.MaxKeyLength + 1);
        AssertRejected("key-too-long", Header(start: true, [0x00], [0x00], [0x01], [.. tooLong]));
        AssertAccepted(Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x01, 0x6b, 0x00, 0x00, 0x00, 0x01, 0x07]));
        AssertRejected("row-truncated", Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x01, 0x6b, 0x00, 0x00, 0x00, 0x02, 0x07]));
    }

    /// <summary>
    /// Bytes which break two rules are rejected for the first in the rules' procedural order (ledgers.md §7), as the
    /// independent reading of the rules also finds.
    /// </summary>
    [Fact]
    public void RulesAreCheckedInTheirProceduralOrder()
    {
        // The whole batch before its header's varints: a CRC-32C mismatch before bad flags.
        var badFlags = Header(start: true, [0x00], [0x00], [0x01], Defining());
        badFlags[8] = 0x03;
        AssertRejected("crc-mismatch", badFlags);
        AssertRejected("bad-flags", LedgerSegmentRules.Seal(badFlags)!);

        // Each header varint before its own rule, in layout order: the offset's form before the ordinal base's rule.
        AssertRejected("bad-varint", Header(start: true, [0x80, 0x00], [0x01], [0x01], Defining()));
        AssertRejected("bad-segment-offset", Header(start: true, [0x01], [0x01], [0x01], Defining()));
        AssertRejected("bad-ordinal-base", Header(start: true, [0x00], [0x01], [0x00]));

        // A row's fields in layout order: its flags before its key, its key length before its key's bytes.
        AssertRejected("bad-row-flags", Header(start: true, [0x00], [0x00], [0x01], [0x07, 0x00]));
        AssertRejected("empty-key", Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x00, 0x21]));
        AssertRejected("base-too-long", Header(start: true, [0x00], [0x00], [0x01], [0x04, 0x01, 0x6b, 0x21, 0x00]));

        // A referencing row's ordinal before its body; a forward ordinal before the next row's truncation.
        AssertRejected("forward-ordinal", Header(start: true, [0x00], [0x00], [0x02], [0x00, 0x00, 0x05]));

        // A second row's truncation before trailing bytes.
        AssertRejected("row-truncated", Header(start: true, [0x00], [0x00], [0x02], [.. Defining(), 0x04, 0x05]));
        AssertRejected("trailing-bytes", Header(start: true, [0x00], [0x00], [0x01], [.. Defining(), .. Defining()]));
    }

    /// <summary>Returns how many of a referencing row's defining fields throw <see cref="InvalidOperationException"/>.</summary>
    private static int ReferencingFieldsThrow(BatchRow row)
    {
        var thrown = 0;
        try
        {
            _ = row.Key.Length;
        }
        catch (InvalidOperationException)
        {
            thrown++;
        }

        try
        {
            _ = row.VersionBefore;
        }
        catch (InvalidOperationException)
        {
            thrown++;
        }

        try
        {
            _ = row.Token;
        }
        catch (InvalidOperationException)
        {
            thrown++;
        }

        return thrown;
    }

    /// <summary>A defining row with an empty key, base, and token, and an empty body: its fields' bytes.</summary>
    private static byte[] Defining() => [0x04, 0x01, 0x6b, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Lays out a batch from its header's varint bytes, as given, and its rows' bytes, with its CRC-32C.</summary>
    private static byte[] Header(bool start, byte[] offset, byte[] ordinalBase, byte[] count, byte[]? rows = null)
    {
        var bytes = new List<byte>();
        bytes.AddRange("CLLB"u8.ToArray());
        Span<byte> version = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 1);
        bytes.AddRange(version);
        bytes.Add(start ? (byte)1 : (byte)0);
        bytes.AddRange(offset);
        bytes.AddRange(ordinalBase);
        bytes.AddRange(count);
        bytes.AddRange(rows ?? []);
        bytes.AddRange(new byte[BatchView.CrcLength]);
        return LedgerSegmentRules.Seal([.. bytes])!;
    }

    private static void AssertAccepted(byte[] bytes)
    {
        Assert.Null(LedgerSegmentRules.ReadBatch(bytes).Reason);
        using var buffer = PagedBuffers.Create(bytes, 1);
        Assert.Null(BatchView.Decode(buffer, out _));
    }

    private static void AssertRejected(string reason, byte[] bytes)
    {
        Assert.Equal(reason, LedgerSegmentRules.ReadBatch(bytes).Reason);
        foreach (var pageLength in (int[])[bytes.Length + 1, 1])
        {
            using var buffer = PagedBuffers.Create(bytes, pageLength);
            var rejection = BatchView.Decode(buffer, out var batch);
            Assert.Equal(reason, Reason(rejection));
            Assert.Equal(0, batch.Count);
        }
    }
}
