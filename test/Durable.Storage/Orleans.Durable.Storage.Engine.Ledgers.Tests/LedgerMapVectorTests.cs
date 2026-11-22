using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.LedgerMapSamples;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the shared ledger map vectors under <c>tests/shared-vectors/ledgers</c> (ledgers.md §7): each value encodes to
/// its bytes and its bytes decode to it, contiguous and from pages of one byte, and each rejection is rejected for the
/// reason of the first rule it breaks, which together cover every reason; the snapshots render the decoded values and
/// each rejection's reason and message (verification.md §8).
/// </summary>
public sealed class LedgerMapVectorTests : IDisposable
{
    private const string Area = "ledgers";

    private const string Format = "**Ledger map, `CLLM` version 1.**";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void MapVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "maps.json"))
        {
            var value = FromVector(vector);
            AssertBytes(vector, Encoded(value));
            using (var encoded = value.Encode())
            {
                AssertBytes(vector, encoded.ToArray());
            }

            Assert.Equal(Describe(value), Describe(Value(LedgerMapValue.Decode(Hex(vector).AsSpan()))));
            using var paged = PagedBuffers.Create(Hex(vector), 1);
            Assert.Equal(Describe(value), Describe(Value(LedgerMapValue.Decode(paged.AsReadOnlySequence()))));
        }
    }

    /// <summary>
    /// Each rejection vector is rejected for its reason, under the key every format's decoder uses, as the independent
    /// reading of the rules names it, and the vectors name ledgers.md §7's reasons, in its order.
    /// </summary>
    [Fact]
    public void MapRejectionVectorsAreRejectedForTheirReason()
    {
        foreach (var vector in Cases(Area, "map-rejections.json"))
        {
            var rejection = Rejection(LedgerMapValue.Decode(Hex(vector).AsSpan()));
            AssertRejected(vector, rejection);
            Assert.Equal(Text(vector, "reason"), LedgerMapFormatRules.Read(Hex(vector)).Reason);
        }

        string[] reasons = [.. Cases(Area, "map-rejections.json").Select(static vector => Text(vector, "reason")).Distinct(StringComparer.Ordinal)];
        Assert.Equal(SpecReasons(Format), reasons);
    }

    /// <summary>Renders each map vector as the decoder reads it. The vectors pin the bytes; the snapshot is their reviewed reading.</summary>
    [Fact]
    public Task MapVectorsDecodeAsTheFormatDescribesThem()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "maps.json"))
        {
            var bytes = Hex(vector);
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  length {bytes.Length}")
                .Append(Describe(Value(LedgerMapValue.Decode(bytes.AsSpan()))));
        }

        return Verify(text.ToString());
    }

    /// <summary>Renders each rejection vector with the reason and message its decoding reports.</summary>
    [Fact]
    public Task MapRejectionsReportTheirReasonAndMessage()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "map-rejections.json"))
        {
            var rejection = Rejection(LedgerMapValue.Decode(Hex(vector).AsSpan()));
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  {Reason(rejection)}: {rejection?.Message}");
        }

        return Verify(text.ToString());
    }
}
