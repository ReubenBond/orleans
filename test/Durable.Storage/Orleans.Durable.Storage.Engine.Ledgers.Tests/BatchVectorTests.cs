using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the shared batch vectors under <c>tests/shared-vectors/ledgers</c> (ledgers.md §7): each batch's segment state
/// and rows encode to its bytes and its bytes decode to them, from one page and from pages of one byte, and each
/// rejection is rejected for its reason, which together cover every reason of the batch rules in their order; the
/// snapshots render the decoded batches and each rejection's reason and message (verification.md §8).
/// </summary>
public sealed class BatchVectorTests : IDisposable
{
    private const string Area = "ledgers";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void BatchVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "batches.json"))
        {
            var batch = Batch(vector);
            Assert.Equal(Hex(vector), LedgerBatches.Encode(batch));
            Assert.Equal(Hex(vector), LedgerSegmentRules.Encode(batch));
            foreach (var pageLength in (int[])[int.MaxValue, 1])
            {
                using var buffer = PagedBuffers.Create(Hex(vector), pageLength);
                Assert.Null(BatchView.Decode(buffer, out var view));
                Assert.Equal((batch.IsStart, batch.Offset, batch.OrdinalBase, batch.Rows.Count), (view.IsSegmentStart, view.LedgerSegmentOffset, view.OrdinalBase, view.Count));
                foreach (var row in view)
                {
                    Assert.True(BatchTarget.Matches(row, batch.Rows[row.Index]), $"{Name(vector)}: row {row.Index}");
                }
            }
        }
    }

    /// <summary>
    /// Each rejection vector is rejected for its reason, under the key every format's decoder uses.
    /// </summary>
    [Fact]
    public void BatchRejectionVectorsAreRejectedForTheirReason()
    {
        foreach (var vector in Cases(Area, "batch-rejections.json"))
        {
            foreach (var pageLength in (int[])[int.MaxValue, 1])
            {
                using var buffer = PagedBuffers.Create(Hex(vector), pageLength);
                var rejection = BatchView.Decode(buffer, out var batch);
                AssertRejected(vector, rejection);
                Assert.Equal(Text(vector, "reason"), rejection?.Data["orleans.durable.storage.decode.reason"]);
                Assert.Equal(0, batch.Count);
            }

            Assert.Equal(Text(vector, "reason"), LedgerSegmentRules.ReadBatch(Hex(vector)).Reason);
        }
    }

    /// <summary>The vectors name ledgers.md §7's batch reasons, in its order.</summary>
    [Fact]
    public void TheVectorsNameLedgersSevensReasonsInItsOrder() => Assert.Equal(SpecReasons(), VectorReasons());

    /// <summary>
    /// Renders each batch vector as the decoder lends it: its segment state, and each row's index, kind, whether it
    /// defines its key, its ordinal, a defining row's key, version before, and token, and its body. The vectors pin the
    /// bytes; the snapshot is their reviewed reading.
    /// </summary>
    [Fact]
    public Task BatchVectorsDecodeAsTheFormatDescribesThem()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "batches.json"))
        {
            using var buffer = PagedBuffers.Create(Hex(vector), []);
            Assert.Null(BatchView.Decode(buffer, out var batch));
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  length {buffer.Length}, start {batch.IsSegmentStart}, offset {batch.LedgerSegmentOffset}, ordinal base {batch.OrdinalBase}, row count {batch.Count}");
            foreach (var row in batch)
            {
                _ = row.IsDefining
                    ? text.AppendLine(CultureInfo.InvariantCulture, $"  row {row.Index}: {row.Kind}, defines ordinal {row.Ordinal}, key {Rendered(row.Key)}, version before ({Rendered(row.VersionBefore.Base.AsSpan())}, {row.VersionBefore.Count}), token {Rendered(row.Token.AsSpan())}, body {Rendered(row.Body.ToArray())}")
                    : text.AppendLine(CultureInfo.InvariantCulture, $"  row {row.Index}: {row.Kind}, references ordinal {row.Ordinal}, body {Rendered(row.Body.ToArray())}");
            }
        }

        return Verify(text.ToString());
    }

    /// <summary>Renders each rejection vector with the reason and message its decoding reports.</summary>
    [Fact]
    public Task BatchRejectionsReportTheirReasonAndMessage()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "batch-rejections.json"))
        {
            using var buffer = PagedBuffers.Create(Hex(vector), []);
            var rejection = BatchView.Decode(buffer, out _);
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  {Reason(rejection)}: {rejection?.Message}");
        }

        return Verify(text.ToString());
    }

    /// <summary>Reads a batch vector's segment state and rows.</summary>
    internal static RuleBatch Batch(JsonElement vector) =>
        new(
            vector.GetProperty("segmentStart").GetBoolean(),
            ulong.Parse(Text(vector, "segmentOffset"), CultureInfo.InvariantCulture),
            ulong.Parse(Text(vector, "ordinalBase"), CultureInfo.InvariantCulture),
            [
                .. vector.GetProperty("rows").EnumerateArray().Select(static row =>
                {
                    var defining = row.GetProperty("defining").GetBoolean();
                    return new RuleRow(
                        (byte)Enum.Parse<RowKind>(Text(row, "kind")),
                        defining,
                        ulong.Parse(Text(row, "ordinal"), CultureInfo.InvariantCulture),
                        defining ? Hex(row, "key") : null,
                        defining ? Hex(row, "versionBase") : null,
                        defining ? ulong.Parse(Text(row, "versionCount"), CultureInfo.InvariantCulture) : 0,
                        defining ? Hex(row, "token") : null,
                        Hex(row, "body"));
                }),
            ]);

    /// <summary>Renders bytes in hexadecimal, eliding the middle of more than 16, with their length.</summary>
    internal static string Rendered(ReadOnlySpan<byte> bytes) =>
        bytes.Length switch
        {
            0 => "empty",
            <= 16 => string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(bytes)} (length {bytes.Length})"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(bytes[..8])}...{Convert.ToHexStringLower(bytes[^8..])} (length {bytes.Length})"),
        };
}
