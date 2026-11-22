using System.Buffers;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Fuzz.Archive;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Checks the shared manifest vectors under <c>tests/shared-vectors/archive</c> (archive §7): each manifest's value
/// encodes to its bytes and its bytes decode to its value, and each rejection is rejected for its reason, as the
/// format's independent reading of the rules also gives, which together cover every reason; the snapshots render the
/// decoded manifests and each rejection's reason and message (verification.md §8).
/// </summary>
public sealed class ManifestVectorTests : IDisposable
{
    private const string Area = "archive";

    /// <summary>The reasons of archive §7, in the order the rules are checked.</summary>
    private static readonly string[] Reasons =
    [
        ManifestFormat.Truncated,
        ManifestFormat.BadMagic,
        ManifestFormat.BadVersion,
        ManifestFormat.LengthMismatch,
        ManifestFormat.CrcMismatch,
        ManifestFormat.BodyTruncated,
        ManifestFormat.ZeroEpoch,
        ManifestFormat.ZeroDrainedBelow,
        ManifestFormat.WatermarkBelowDrained,
        ManifestFormat.WatermarksUnordered,
        ManifestFormat.SlotOutOfRange,
        ManifestFormat.BadCompleteFlag,
        ManifestFormat.EmptyWatermark,
        ManifestFormat.CompleteAtDrainedBelow,
        ManifestFormat.BadObjectName,
        ManifestFormat.BadSectionRange,
        ManifestFormat.BadPartition,
        ManifestFormat.BadTag,
        ManifestFormat.TagsUnordered,
        ManifestFormat.BadTailRange,
        ManifestFormat.SectionsUnordered,
        ManifestFormat.TrailingBytes,
    ];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ManifestVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "manifest.json"))
        {
            var value = ManifestSamples.FromVector(vector);
            using (var encoded = ManifestFormat.Encode(value))
            {
                AssertBytes(vector, encoded.Content.ToArray());
            }

            Assert.Equal(value, Assert.IsType<ManifestValue>(ManifestFormat.Decode(new ReadOnlySequence<byte>(Hex(vector))).Value));
            Assert.Null(ManifestRules.Read(Hex(vector)).Reason);
        }
    }

    [Fact]
    public void ManifestRejectionVectorsAreRejectedForTheirReason()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "manifest-rejections.json"))
        {
            AssertRejected(vector, ManifestFormat.Decode(new ReadOnlySequence<byte>(Hex(vector))).Value as InvalidDataException);
            Assert.Equal(Text(vector, "reason"), ManifestRules.Read(Hex(vector)).Reason);
            _ = reasons.Add(Text(vector, "reason"));
        }

        Assert.Equal(Reasons.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Renders each manifest vector as the decoder reads it: its epoch, <c>DrainedBelow</c>, watermarks, and sections.
    /// The vectors pin the bytes; the snapshot is their reviewed reading.
    /// </summary>
    [Fact]
    public Task ManifestVectorsDecodeAsTheFormatDescribesThem()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "manifest.json"))
        {
            var value = Assert.IsType<ManifestValue>(ManifestFormat.Decode(new ReadOnlySequence<byte>(Hex(vector))).Value);
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  epoch {value.Epoch}, DrainedBelow {value.DrainedBelow}");
            foreach (var (ledger, watermark) in value.Watermarks)
            {
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"  ledger {ledger}: watermark {watermark.Below}{(watermark.Complete ? ", complete" : string.Empty)}");
            }

            foreach (var section in value.Sections)
            {
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"  section {section.Object} [{section.Offset}, +{section.Length}), partition {section.Partition}, tags {Render(section.MinTag)} to {Render(section.MaxTag)}, tail [{section.Tail.Offset}, +{section.Tail.Length})");
            }
        }

        return Verify(text.ToString());
    }

    /// <summary>Renders each rejection vector with the reason and message its decoding reports.</summary>
    [Fact]
    public Task ManifestRejectionsReportTheirReasonAndMessage()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "manifest-rejections.json"))
        {
            var rejection = Assert.IsType<InvalidDataException>(ManifestFormat.Decode(new ReadOnlySequence<byte>(Hex(vector))).Value);
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  {DecodeRejection.ReasonOf(rejection)}: {rejection.Message}");
        }

        return Verify(text.ToString());
    }

    private static string Render(Orleans.Durable.Storage.Engine.Ledgers.Tag tag) => string.Create(CultureInfo.InvariantCulture, $"({tag.Ledger}, {tag.Slot}, {tag.Index})");
}
