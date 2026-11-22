using System.Buffers;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

/// <summary>
/// The streaming batch builder (ledgers.md §2.4, §4, §7): rows written into a <see cref="LedgerBatch"/> field by field,
/// each body's head with its row's fields and its rest in any chunks, form the batch <see cref="LedgerSegmentRules"/>, an
/// independent reading of the format, gives, each row defining or referencing its key as the segment's dictionary says,
/// and <see cref="LedgerBatch.RowLength"/> is each row's exact length; a row the batch refuses writes nothing and leaves the
/// dictionary as it was; and the batch holds its declared rows and body bytes exactly. Each case runs in its own page leak
/// scope (verification.md §11.1).
/// </summary>
public sealed class LedgerBatchTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void RowsWrittenFieldByFieldInAnyChunksFormTheBatchTheRulesGive() =>
        Gen.Select(Rows, Gen.Int[1, 64], Gen.Double[0, 1], Tokens).Sample((rows, chunk, headShare, token) => _leaks.InScope(() =>
        {
            var dictionary = new LedgerSegmentDictionary();
            using var batch = new LedgerBatch(KeyRange.Root, int.MaxValue, rows.Length, new LedgerBatchSegment(IsStart: true, 0, 0, dictionary));
            var versions = new List<ulong>();
            foreach (var row in rows)
            {
                var references = dictionary.Contains(row.Key);
                var before = references ? batch.HeldVersion(row.Key) : new RowVersion(new RowToken(token), 17);
                var length = batch.Length;
                var rowLength = batch.RowLength(row.Key, before, new RowToken(token), row.Body.Length);
                var head = (int)(row.Body.Length * headShare);
                versions.Add(batch.BeginRow(row.Key, row.Kind, before, new RowToken(token), row.Body.Length, row.Body.AsSpan(0, head)).Count);
                for (var offset = head; offset < row.Body.Length; offset += chunk)
                {
                    batch.WriteBody(row.Body.AsSpan(offset, Math.Min(chunk, row.Body.Length - offset)));
                }

                Assert.Equal(length + rowLength, batch.Length);
            }

            var expected = batch.Length;
            using var encoded = batch.Complete();
            Assert.Equal(expected, encoded.Length);
            var reading = LedgerSegmentRules.ReadSegment([encoded.ToArray()], firstSlot: 0);
            Assert.Null(reading.Reason);
            AssertRows(rows, reading.Rows.Select(RowSample.Of));
            Assert.Equal(versions, reading.Rows.Select(static row => row.VersionCount));
            Assert.Equal(rows.Select(static row => Convert.ToHexString(row.Key)).Distinct().Count(), dictionary.Count);
        }));

    /// <summary>
    /// A row the batch refuses writes nothing and leaves the dictionary as it was: the batch's rows, length, and bytes are
    /// those of the rows it accepted, and a further row of the declared count completes it.
    /// </summary>
    [Fact]
    public void ARefusedRowWritesNothingAndLeavesTheDictionary()
    {
        var range = KeyRange.Containing([0x00], 1);
        var dictionary = new LedgerSegmentDictionary();
        var limit = LedgerBatch.MaxHeaderLength(2) - 2 * Varint.MaxLength + 2
            + (int)LedgerBatch.MaxRowLength(1, 0, 0, 5) - Varint.MaxLength + 1
            + (int)LedgerBatch.MaxRowLength(1, 0, 0, 6) - Varint.MaxLength + 1;
        using var batch = new LedgerBatch(range, limit, rowCount: 2, new LedgerBatchSegment(IsStart: true, 0, 0, dictionary));
        Assert.Equal(new RowVersion(default, 1), batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>("first"u8.ToArray())));
        var length = batch.Length;

        Assert.Contains("range", Assert.Throws<ArgumentException>(() => batch.BeginRow([0x80], RowKind.Append, default, default, 1)).Message, StringComparison.Ordinal);
        Assert.Contains("holds", Assert.Throws<ArgumentException>(() => batch.BeginRow([0x20], RowKind.Append, default, default, 7)).Message, StringComparison.Ordinal);
        _ = Assert.Throws<ArgumentException>(() => batch.BeginRow([], RowKind.Append, default, default, 1));
        _ = Assert.Throws<ArgumentException>(() => batch.BeginRow([0x20], (RowKind)3, default, default, 1));
        _ = Assert.Throws<ArgumentException>(() => batch.BeginRow(new byte[KeyRange.MaxKeyLength + 1], RowKind.Append, default, default, 1));
        _ = Assert.Throws<ArgumentException>(() => batch.BeginRow([0x20], RowKind.Append, new RowVersion(default, ulong.MaxValue), default, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => batch.BeginRow([0x20], RowKind.Append, default, default, 1, "ab"u8));
        Assert.Equal(1, batch.RowsBegun);
        Assert.Equal(length, batch.Length);
        Assert.Equal(1, dictionary.Count);
        Assert.False(dictionary.Contains([0x20]));

        Assert.Equal(new RowVersion(default, 1), batch.AddRow([0x20], RowKind.Replace, default, default, new ReadOnlySequence<byte>("second"u8.ToArray())));
        Assert.Equal(limit, batch.Length);
        using var encoded = batch.Complete();
        var reading = LedgerSegmentRules.ReadSegment([encoded.ToArray()], firstSlot: 0);
        Assert.Null(reading.Reason);
        AssertRows([new(RowKind.Append, [0x10], "first"u8.ToArray()), new(RowKind.Replace, [0x20], "second"u8.ToArray())], reading.Rows.Select(RowSample.Of));
    }

    /// <summary>
    /// A test's batch names its segment with its slot: the segment starts at the slot exactly when the batch starts one,
    /// and never after it; the two come together. A writer's batch alone names a segment otherwise.
    /// </summary>
    [Fact]
    public void ATestsBatchNamesASegmentConsistentWithItsSlot()
    {
        var ledger = new LedgerId(3);
        using var starting = new LedgerBatch(KeyRange.Root, int.MaxValue, 0, new LedgerBatchSegment(IsStart: true, 0, 0, new LedgerSegmentDictionary()), segmentId: new LedgerSegmentId(ledger, new Slot(4)), slot: new Slot(4));
        using var continuing = new LedgerBatch(KeyRange.Root, int.MaxValue, 1, new LedgerBatchSegment(IsStart: false, 9, 0, new LedgerSegmentDictionary()), segmentId: new LedgerSegmentId(ledger, new Slot(4)), slot: new Slot(6));

        Assert.Equal(new LedgerSegmentId(ledger, new Slot(4)), starting.Segment);
        Assert.Equal(new LedgerSegmentId(ledger, new Slot(4)), continuing.Segment);
        _ = Assert.Throws<ArgumentException>(() => new LedgerBatch(KeyRange.Root, int.MaxValue, 0, new LedgerBatchSegment(IsStart: true, 0, 0, new LedgerSegmentDictionary()), segmentId: new LedgerSegmentId(ledger, new Slot(4)), slot: new Slot(5)));
        _ = Assert.Throws<ArgumentException>(() => new LedgerBatch(KeyRange.Root, int.MaxValue, 1, new LedgerBatchSegment(IsStart: false, 9, 0, new LedgerSegmentDictionary()), segmentId: new LedgerSegmentId(ledger, new Slot(6)), slot: new Slot(6)));
        _ = Assert.Throws<ArgumentException>(() => new LedgerBatch(KeyRange.Root, int.MaxValue, 1, new LedgerBatchSegment(IsStart: false, 9, 0, new LedgerSegmentDictionary()), segmentId: new LedgerSegmentId(ledger, new Slot(7)), slot: new Slot(6)));
        _ = Assert.Throws<ArgumentException>(() => new LedgerBatch(KeyRange.Root, int.MaxValue, 1, new LedgerBatchSegment(IsStart: false, 9, 0, new LedgerSegmentDictionary()), segmentId: new LedgerSegmentId(ledger, new Slot(4))));
    }

    /// <summary>
    /// A row defines its key when the segment's dictionary has not, storing the caller's version and token, and
    /// references it otherwise, by ordinal; <see cref="LedgerBatch.BeginRow"/> returns the version the row resolves to.
    /// </summary>
    [Fact]
    public void ARowDefinesOrReferencesItsKeyAsTheDictionarySays()
    {
        var dictionary = new LedgerSegmentDictionary();
        dictionary.Define([0x01], 0, new RowVersion(new RowToken([9]), 4));
        using var batch = new LedgerBatch(KeyRange.Root, int.MaxValue, rowCount: 3, new LedgerBatchSegment(IsStart: false, 2, 1, dictionary));
        Assert.Equal(new RowVersion(new RowToken([9]), 5), batch.AddRow([0x01], RowKind.Append, new RowVersion(new RowToken([9]), 4), default, default));
        Assert.Equal(new RowVersion(new RowToken([7]), 11), batch.AddRow([0x02], RowKind.Append, new RowVersion(new RowToken([7]), 10), new RowToken([8]), default));
        Assert.Equal(new RowVersion(new RowToken([7]), 12), batch.AddRow([0x02], RowKind.Delete, new RowVersion(new RowToken([7]), 11), new RowToken([8]), default));
        using var encoded = batch.Complete();
        var rows = LedgerSegmentRules.ReadBatch(encoded.ToArray()).Rows;
        Assert.Equal([(false, 0UL), (true, 1UL), (false, 1UL)], rows.Select(static row => (row.IsDefining, row.Ordinal)));
        Assert.Equal(10UL, rows[1].VersionCount);
        Assert.Equal([8], rows[1].Token);
    }

    [Fact]
    public void ABatchHoldsItsDeclaredRowsAndBodyBytesExactly()
    {
        using (var batch = Batch(rowCount: 1))
        {
            _ = batch.BeginRow([0x01], RowKind.Append, default, default, 3);
            batch.WriteBody("ab"u8);
            _ = Assert.Throws<InvalidOperationException>(() => batch.WriteBody("cd"u8));
            _ = Assert.Throws<InvalidOperationException>(() => batch.BeginRow([0x02], RowKind.Append, default, default, 0));
            Assert.False(batch.IsComplete);
            batch.WriteBody("c"u8);
            Assert.True(batch.IsComplete);
            _ = Assert.Throws<InvalidOperationException>(() => batch.BeginRow([0x02], RowKind.Append, default, default, 0));
            using var encoded = batch.Complete();
            Assert.True(batch.IsProposed);
            _ = Assert.Throws<InvalidOperationException>(() => batch.Complete());
            _ = Assert.Throws<InvalidOperationException>(() => batch.WriteBody([]));
            _ = Assert.Throws<InvalidOperationException>(() => batch.BeginRow([0x02], RowKind.Append, default, default, 0));
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(static () => new LedgerBatch(KeyRange.Root, int.MaxValue, rowCount: 0, new LedgerBatchSegment(IsStart: false, 1, 0, new LedgerSegmentDictionary())));
        using var marker = new LedgerBatch(KeyRange.Root, int.MaxValue, rowCount: 0, new LedgerBatchSegment(IsStart: true, 0, 0, new LedgerSegmentDictionary()));
        Assert.True(marker.IsComplete);
        Assert.Equal(marker.HeaderLength, marker.Length);
    }

    [Fact]
    public void DisposingABatchReleasesWhatNoProposalTook()
    {
        var batch = Batch(rowCount: 2);
        using var body = ArcBuffer.Wrap(new byte[3000]);
        _ = batch.AddRow([0x01], RowKind.Append, default, default, body.AsReadOnlySequence());
        batch.Dispose();
        batch.Dispose();
        _ = Assert.Throws<InvalidOperationException>(() => batch.BeginRow([0x02], RowKind.Append, default, default, 0));
    }

    private static LedgerBatch Batch(int rowCount) =>
        new(KeyRange.Root, int.MaxValue, rowCount, new LedgerBatchSegment(IsStart: true, 0, 0, new LedgerSegmentDictionary()), 4096);
}
