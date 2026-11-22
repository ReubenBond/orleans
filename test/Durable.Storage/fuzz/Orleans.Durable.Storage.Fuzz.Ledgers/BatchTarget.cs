using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// The batch, <c>CLLB</c> version 1 (ledgers.md §7), decoded alone: a canonical format, rejected with the reason
/// <see cref="LedgerSegmentRules"/> gives by the batch rules, one of the shared batch rejection vectors' reasons, or
/// accepted as the rows the rules give, which <see cref="LedgerBatch"/> encodes to exactly its bytes. The input is the
/// batch, which the target decodes from one page and from pages of a few bytes each, so fields span pages.
/// </summary>
public sealed class BatchTarget : FuzzTarget
{
    /// <summary>The reasons of <c>test/Durable.Storage/shared-vectors/ledgers/batch-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        GoldenVectors.Find().Cases("ledgers", "batch-rejections.json").Select(static vector => GoldenVectors.Text(vector, "reason")).ToHashSet(StringComparer.Ordinal));

    public override string Name => "ledgers-batch";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "batches.json", "batch-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgers", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    /// <summary>Returns whether <paramref name="row"/> lends what <paramref name="expected"/> holds.</summary>
    public static bool Matches(BatchRow row, RuleRow expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if ((byte)row.Kind != expected.Kind || row.IsDefining != expected.IsDefining || row.Ordinal != expected.Ordinal
            || !row.Body.ToArray().AsSpan().SequenceEqual(expected.Body) || row.BodyLength != expected.Body.Length)
        {
            return false;
        }

        return !row.IsDefining
            || row.Key.SequenceEqual(expected.Key)
                && row.VersionBefore.Base.AsSpan().SequenceEqual(expected.VersionBase)
                && row.VersionBefore.Count == expected.VersionCount
                && row.Token.AsSpan().SequenceEqual(expected.Token);
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = LedgerSegmentRules.ReadBatch(input);
        FuzzOutcome outcome;
        using (var whole = PagedBuffers.Create(input, []))
        {
            outcome = Check(input, whole, reading, "one page");
        }

        // Pages of 7 to 63 bytes, so fields, keys, and bodies span pages.
        var pageLength = 7 + input.Length % 57;
        using (var paged = PagedBuffers.Create(input, pageLength))
        {
            _ = Check(input, paged, reading, string.Create(CultureInfo.InvariantCulture, $"pages of {pageLength} bytes"));
        }

        return outcome;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => LedgerSegmentRules.Seal(input);

    private FuzzOutcome Check(ReadOnlySpan<byte> input, ArcBuffer buffer, BatchReading reading, string layout)
    {
        // The decoding is measured with a walk of every field it lends, which allocates only scratch for a key which
        // spans pages.
        var bound = MeasureAllocations(input.Length);
        var rejection = BatchView.Decode(buffer, out var view);
        if (rejection is null)
        {
            foreach (var row in view)
            {
                _ = (row.IsDefining ? row.Key.Length : 0) + row.Body.Length;
            }
        }

        bound.Check("BatchView.Decode");
        if (rejection is not null)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(
                reason == reading.Reason,
                $"rejected {layout} as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected {layout} as '{reason}', which no batch rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        Require(reading.Batch is not null, $"accepted {layout}, which the format's rules reject as '{reading.Reason}'.");
        var expected = reading.Batch;
        Require(
            view.IsSegmentStart == expected.IsStart && view.LedgerSegmentOffset == expected.Offset && view.OrdinalBase == expected.OrdinalBase,
            $"decoded the header of {layout} otherwise than the format's rules give it.");
        Require(view.Count == expected.Rows.Count, string.Create(CultureInfo.InvariantCulture, $"decoded {view.Count} rows from {layout}, where the format's rules give {expected.Rows.Count}."));
        foreach (var row in view)
        {
            Require(Matches(row, expected.Rows[row.Index]), string.Create(CultureInfo.InvariantCulture, $"decoded row {row.Index} of {layout} otherwise than the format's rules give it."));
        }

        if (LedgerBatches.Encode(expected) is { } encoded)
        {
            Require(input.SequenceEqual(encoded), $"accepted {layout}, whose rows do not encode back to its bytes.");
        }

        return FuzzOutcome.Accepted;
    }
}
