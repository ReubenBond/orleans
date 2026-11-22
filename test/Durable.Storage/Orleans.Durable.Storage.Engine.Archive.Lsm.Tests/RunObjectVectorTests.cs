using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Fuzz.Archive;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Checks the shared run object vectors under <c>tests/shared-vectors/archive</c> (archive §7): each object's bytes
/// decode to its value, as the format's independent reading of the rules also reads them, and the writer encodes each
/// value it can write as its bytes; each rejection is rejected for its reason, by the whole read, the partial decoders,
/// and the independent reading, which together cover every reason in §7's order. The snapshots render the decoded objects
/// and each rejection's reason and message (verification.md §8).
/// </summary>
public sealed partial class RunObjectVectorTests : IDisposable
{
    private const string Area = "archive";

    /// <summary>The reasons of archive §7, in the order of its table.</summary>
    private static readonly string[] Reasons =
    [
        RunObjectFormat.Truncated,
        RunObjectFormat.BadMagic,
        RunObjectFormat.BadVersion,
        RunObjectFormat.LengthMismatch,
        RunObjectFormat.CrcMismatch,
        RunObjectFormat.BodyTruncated,
        RunObjectFormat.NoSections,
        RunObjectFormat.BadSectionLayout,
        RunObjectFormat.SectionsNotContiguous,
        RunObjectFormat.BadSectionRange,
        RunObjectFormat.BadPartition,
        RunObjectFormat.BadTag,
        RunObjectFormat.TagsUnordered,
        RunObjectFormat.NoRows,

        RunObjectFormat.BadCondition,
        RunObjectFormat.BadObjectName,
        RunObjectFormat.InputsUnordered,
        RunObjectFormat.ConditionMismatch,
        RunObjectFormat.TrailingBytes,
        RunObjectFormat.NoBlocks,
        RunObjectFormat.BlocksMismatch,
        RunObjectFormat.BadContinuation,
        RunObjectFormat.EmptyKey,
        RunObjectFormat.KeysUnordered,
        RunObjectFormat.FirstKeyOutsidePartition,
        RunObjectFormat.BadFilter,
        RunObjectFormat.UnknownKind,
        RunObjectFormat.IndexMismatch,
        RunObjectFormat.RowsUnordered,
        RunObjectFormat.KeyOutsidePartition,
        RunObjectFormat.TagOutsideSpan,
        RunObjectFormat.FilterMiss,
        RunObjectFormat.RowCountMismatch,
        RunObjectFormat.TagSpanMismatch,
    ];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void RunObjectVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "run-objects.json"))
        {
            var value = RunObjectSamples.FromVector(vector);
            if (vector.GetProperty("encoded").GetBoolean())
            {
                using var encoded = value.Encode();
                AssertBytes(vector, encoded.Content.ToArray());
            }

            var bytes = Hex(vector);
            using var content = ArcBuffer.Wrap(bytes);
            using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(content).Value);
            Assert.Equal(value.Condition, decoded.Trailer.Condition);
            var sections = vector.GetProperty("sections").EnumerateArray().ToArray();
            Assert.Equal(value.Sections.Length, decoded.Sections.Length);
            for (var index = 0; index < sections.Length; index++)
            {
                var (expected, section) = (value.Sections[index], decoded.Sections[index]);
                Assert.Equal(expected.Partition, section.Metadata.Entry.Partition);
                Assert.Equal(sections[index].GetProperty("hashCount").GetInt32(), section.Metadata.Filter.HashCount);
                Assert.Equal(sections[index].GetProperty("blocks").GetInt32(), section.Metadata.Index.Blocks.Length);
                RunObjectSamples.AssertRows(expected.Rows, section.Rows.Rows);
            }

            var reading = RunObjectRules.Read(bytes);
            Assert.True(RunObjectTarget.Matches(decoded, Assert.IsType<RunRuleObject>(reading.Object)), $"'{Name(vector)}' decodes otherwise than the rules read it.");
            RunObjectSamples.AssertPartialDecodersAgree(bytes, null);
        }
    }

    [Fact]
    public void RunObjectRejectionVectorsAreRejectedForTheirReason()
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Cases(Area, "run-object-rejections.json"))
        {
            var bytes = Hex(vector);
            using var content = ArcBuffer.Wrap(bytes);
            AssertRejected(vector, RunObjectFormat.Decode(content).Value as InvalidDataException);
            Assert.Equal(Text(vector, "reason"), RunObjectRules.Read(bytes).Reason);
            RunObjectSamples.AssertPartialDecodersAgree(bytes, Text(vector, "reason"));
            _ = reasons.Add(Text(vector, "reason"));
        }

        Assert.Equal(Reasons.Order(StringComparer.Ordinal), reasons.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The reasons' order is §7's table's.
    /// </summary>
    [Fact]
    public void TheReasonsAreArchiveSevensInItsOrder()
    {
        var archive = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "spec", "components", "archive-lsm.md"));
        var table = archive[archive.IndexOf("**Run object**", StringComparison.Ordinal)..];
        table = table[table.IndexOf("| Reason | Rule |", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Equal(Reasons, ReasonCell().Matches(table).Select(static match => match.Groups[1].Value));
    }

    /// <summary>
    /// The vectors record the reasons in the decoder's order.
    /// </summary>
    [Fact]
    public void TheVectorsRecordTheReasonsInTheirOrder()
    {
        using var document = Load(Area, "run-object-rejections.json");
        Assert.Equal(Reasons, document.RootElement.GetProperty("order").EnumerateArray().Select(static reason => reason.GetString()));
    }

    /// <summary>
    /// Renders each run object vector as the decoder reads it: its condition, and each section's entry, blocks, filter,
    /// and rows. The vectors pin the bytes; the snapshot is their reviewed reading.
    /// </summary>
    [Fact]
    public Task RunObjectVectorsDecodeAsTheFormatDescribesThem()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "run-objects.json"))
        {
            using var content = ArcBuffer.Wrap(Hex(vector));
            using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(content).Value);
            var trailer = decoded.Trailer;
            _ = text.AppendLine(Name(vector))
                .AppendLine(CultureInfo.InvariantCulture, $"  {Render(trailer.Condition)}; metadata at {trailer.MetadataOffset}, trailer at {trailer.TrailerOffset}, {trailer.ObjectLength} bytes");
            foreach (var section in decoded.Sections)
            {
                var (entry, index, filter) = section.Metadata;
                _ = text.AppendLine(CultureInfo.InvariantCulture, $"  section [{entry.Offset}, +{entry.Length}), partition {Render(entry.Partition)}, tags {Render(entry.MinTag)} to {Render(entry.MaxTag)}, {entry.RowCount} rows, index at {entry.IndexOffset}, filter at {entry.FilterOffset}")
                    .AppendLine(CultureInfo.InvariantCulture, $"    blocks: {string.Join(", ", index.Blocks.Select(static block => string.Create(CultureInfo.InvariantCulture, $"[{block.Offset}, +{block.Length}){(block.Continues ? " continuing" : string.Empty)} from {Render(block.FirstKey.Span)}")))}")
                    .AppendLine(CultureInfo.InvariantCulture, $"    filter: {filter.HashCount} hashes, {Convert.ToHexStringLower(filter.Bits)}");
                foreach (var (tag, row) in section.Rows.Rows)
                {
                    _ = text.AppendLine(CultureInfo.InvariantCulture, $"    {Render(row.Key.Span)} {Render(tag)} {row.Kind}, payload {Convert.ToHexStringLower(row.Body.ToArray())}");
                }
            }
        }

        return Verify(text.ToString());
    }

    /// <summary>Renders each rejection vector with the reason and message its decoding reports.</summary>
    [Fact]
    public Task RunObjectRejectionsReportTheirReasonAndMessage()
    {
        var text = new StringBuilder();
        foreach (var vector in Cases(Area, "run-object-rejections.json"))
        {
            using var content = ArcBuffer.Wrap(Hex(vector));
            var rejection = Assert.IsType<InvalidDataException>(RunObjectFormat.Decode(content).Value);
            _ = text.AppendLine(Name(vector)).AppendLine(CultureInfo.InvariantCulture, $"  {DecodeRejection.ReasonOf(rejection)}: {rejection.Message}");
        }

        return Verify(text.ToString());
    }

    [GeneratedRegex(@"^\| `([a-z-]+)` \|", RegexOptions.Multiline)]
    private static partial Regex ReasonCell();

    private static string Render(RunObjectCondition condition) => condition switch
    {
        RunObjectCondition.Drain drain => string.Create(CultureInfo.InvariantCulture, $"drain of ledger {drain.Ledger} from {drain.From}"),
        RunObjectCondition.Compaction compaction => "compaction of " + string.Join(", ", compaction.Inputs.Select(static input => string.Create(CultureInfo.InvariantCulture, $"{input.Object}@{input.Offset}"))),
        null => throw new System.Diagnostics.UnreachableException(),
    };

    private static string Render(ReadOnlySpan<byte> key) =>
        key.Length <= 32 ? Convert.ToHexStringLower(key) : string.Create(CultureInfo.InvariantCulture, $"{key.Length} bytes from {Convert.ToHexStringLower(key[..8])}");

    private static string Render(KeyRange partition) =>
        partition.Depth <= 64 ? partition.ToString() : string.Create(CultureInfo.InvariantCulture, $"{Render(partition.Prefix)}/{partition.Depth}");

    private static string Render(Tag tag) => string.Create(CultureInfo.InvariantCulture, $"({tag.Ledger}, {tag.Slot}, {tag.Index})");
}
