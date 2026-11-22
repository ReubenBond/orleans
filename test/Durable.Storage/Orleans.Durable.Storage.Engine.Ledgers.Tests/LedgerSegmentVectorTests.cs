using System.Globalization;
using System.Text;
using System.Text.Json;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the shared segment vectors under <c>tests/shared-vectors/ledgers</c> (ledgers.md §7): each segment's batches
/// resolve, through a <see cref="LedgerSegmentView"/> from contiguous bytes and from pages of one byte, to the rows,
/// versions, and tokens it records, and its batches' rows encode back to its bytes; each rejection is rejected at its batch
/// for its reason, the rejections together covering every segment rule in ledgers.md §7's order; the snapshots render the
/// resolved rows and each rejection's batch, reason, and message (verification.md §8).
/// </summary>
public sealed class LedgerSegmentVectorTests : IDisposable
{
    private const string Area = "ledgers";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void TheSegmentVectorsMatchTheDecoderAndTheEncoder()
    {
        foreach (var vector in Cases(Area, "segments.json"))
        {
            var batches = Batches(vector);
            var slot = Slot(vector);
            var expected = Rows(vector);
            AssertResolved(expected, LedgerSegmentRules.ReadSegment(batches, slot).Rows.Select(ResolvedSample.Of));
            foreach (int[] pageLengths in (int[][])[[], [1]])
            {
                var decoded = DecodeSegment(batches, slot, pageLengths);
                Assert.Null(decoded.Reason);
                AssertResolved(expected, decoded.Rows);
            }

            Assert.Equal(batches, LedgerBatches.EncodeSegment([.. batches.Select(static batch => LedgerSegmentRules.ReadBatch(batch).Batch!)]));
        }
    }

    [Fact]
    public void TheSegmentRejectionVectorsAreRejectedAtTheirBatchForTheirReason()
    {
        foreach (var vector in Cases(Area, "segment-rejections.json"))
        {
            var batches = Batches(vector);
            var expected = (vector.GetProperty("batch").GetInt32(), Text(vector, "reason"));
            foreach (int[] pageLengths in (int[][])[[], [1]])
            {
                var decoded = DecodeSegment(batches, Slot(vector), pageLengths);
                Assert.Equal(expected, (decoded.FailedBatch, decoded.Reason));
            }

            var reading = LedgerSegmentRules.ReadSegment(batches, Slot(vector));
            Assert.Equal(expected, (reading.FailedBatch, reading.Reason));
        }
    }

    /// <summary>
    /// ledgers.md §4: a key-filtered view of each vector, for each key its batches define, resolves that key's rows alone
    /// or rejects at the batch and for the reason the filtered reading of the rules names, which checks the segment rules
    /// for that key and each batch's offset and ordinal base.
    /// </summary>
    [Fact]
    public void AKeyFilteredViewJudgesTheVectorsAsTheFilteredRulesDo()
    {
        foreach (var vector in Cases(Area, "segments.json").Concat(Cases(Area, "segment-rejections.json")))
        {
            var batches = Batches(vector);
            var slot = Slot(vector);
            var keys = batches
                .Select(static batch => LedgerSegmentRules.ReadBatch(batch).Batch)
                .Where(static batch => batch is not null)
                .SelectMany(static batch => batch!.Rows.Where(static row => row.IsDefining).Select(static row => row.Key!))
                .DistinctBy(Convert.ToHexString);
            foreach (var key in keys)
            {
                var reading = LedgerSegmentRules.ReadSegment(batches, slot, key);
                var decoded = DecodeSegment(batches, slot, pageLengths: [1], key);
                Assert.Equal((reading.FailedBatch, reading.Reason), (decoded.FailedBatch, decoded.Reason));
                if (reading.Reason is null)
                {
                    AssertResolved(reading.Rows.Select(ResolvedSample.Of), decoded.Rows);
                }
            }
        }
    }

    /// <summary>The rejection vectors name ledgers.md §7's segment reasons, in its order, after any batch reasons.</summary>
    [Fact]
    public void TheVectorsNameLedgersSevensSegmentReasonsInItsOrder()
    {
        var segmentReasons = SpecReasons("**Segment rules.**");
        Assert.Equal(segmentReasons, VectorReasons("segment-rejections.json").Where(segmentReasons.Contains));
    }

    /// <summary>Renders each segment vector's resolved rows; the vectors pin the bytes, and the snapshot is their reading.</summary>
    [Fact]
    public Task TheSegmentVectorsResolveAsTheFormatDescribesThem()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "segments.json"))
        {
            _ = text.AppendLine(Name(vector));
            foreach (var row in DecodeSegment(Batches(vector), Slot(vector)).Rows)
            {
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"  slot {row.Slot} row {row.Index}: {row.Kind}, key {BatchVectorTests.Rendered(row.Key)}, version ({BatchVectorTests.Rendered(row.VersionBase)}, {row.VersionCount}), token {BatchVectorTests.Rendered(row.Token)}, body {BatchVectorTests.Rendered(row.Body)}");
            }
        }

        return Verify(text.ToString());
    }

    /// <summary>Renders each segment rejection with the batch, reason, and message its decoding reports.</summary>
    [Fact]
    public Task TheSegmentRejectionsReportTheirBatchReasonAndMessage()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "segment-rejections.json"))
        {
            var batches = Batches(vector);
            using var view = new LedgerSegmentView(new LedgerId(1));
            InvalidDataException? rejection = null;
            var failed = -1;
            for (var index = 0; index < batches.Count && rejection is null; index++)
            {
                using var buffer = PagedBuffers.Create(batches[index], []);
                rejection = BatchView.Decode(buffer, out var batch) ?? view.Advance(new Slot(Slot(vector) + index), batch);
                failed = index;
            }

            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  batch {failed}, {Reason(rejection)}: {rejection?.Message}");
        }

        return Verify(text.ToString());
    }

    private static List<byte[]> Batches(JsonElement vector) =>
        [.. vector.GetProperty("batches").EnumerateArray().Select(static batch => Convert.FromHexString(batch.GetString()!))];

    private static long Slot(JsonElement vector) => long.Parse(Text(vector, "slot"), CultureInfo.InvariantCulture);

    private static List<ResolvedSample> Rows(JsonElement vector) =>
        [
            .. vector.GetProperty("rows").EnumerateArray().Select(static row => new ResolvedSample(
                long.Parse(Text(row, "slot"), CultureInfo.InvariantCulture),
                row.GetProperty("index").GetInt32(),
                Hex(row, "key"),
                Enum.Parse<RowKind>(Text(row, "kind")),
                Hex(row, "versionBase"),
                ulong.Parse(Text(row, "versionCount"), CultureInfo.InvariantCulture),
                Hex(row, "token"),
                Hex(row, "body"))),
        ];
}
