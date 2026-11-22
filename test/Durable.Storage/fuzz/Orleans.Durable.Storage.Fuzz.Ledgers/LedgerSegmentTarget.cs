using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// A segment's batches, <c>CLLB</c> version 1, decoded in slot order by the batch rules and then the segment rules
/// (ledgers.md §7): the first failure, its batch and reason, is the one <see cref="LedgerSegmentRules"/> gives; on
/// acceptance the resolved rows are the rules', <see cref="LedgerBatch"/> encodes the same rows and boundaries to the
/// exact input, and decoding after a rewind from a slot gives the rows from that slot on.
/// </summary>
/// <remarks>
/// The input is a <c>u16</c> batch count, at most <see cref="MaxBatches"/>, and that many batches, each prefixed by its
/// <c>u32</c> length, at slots 0 to <c>n − 1</c>; an input whose framing falls short, or runs past its last batch, lies
/// outside the target's domain. The target decodes the batches from contiguous bytes and from pages of 7 to 63 bytes.
/// </remarks>
public sealed class LedgerSegmentTarget : FuzzTarget
{
    /// <summary>The most batches an input frames.</summary>
    public const int MaxBatches = 1024;

    /// <summary>The most slots a rewind is checked from, spread over the batches.</summary>
    private const int MaxRewinds = 64;

    /// <summary>The reasons of the batch and segment rejection vectors.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        new[] { "batch-rejections.json", "segment-rejections.json" }
            .SelectMany(static file => GoldenVectors.Find().Cases("ledgers", file))
            .Select(static vector => GoldenVectors.Text(vector, "reason"))
            .ToHashSet(StringComparer.Ordinal));

    public override string Name => "ledgers-segment";

    public override int MaxLength => 8192;

    /// <summary>Frames <paramref name="batches"/> as the target's input.</summary>
    public static byte[] Frame(IReadOnlyList<byte[]> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var bytes = new List<byte>();
        Span<byte> field = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)batches.Count);
        bytes.AddRange(field[..sizeof(ushort)]);
        foreach (var batch in batches)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)batch.Length);
            bytes.AddRange(field);
            bytes.AddRange(batch);
        }

        return [.. bytes];
    }

    /// <summary>Reads the batches the input frames, or <see langword="null"/> when its framing does not hold them exactly.</summary>
    public static List<byte[]>? Unframe(ReadOnlySpan<byte> input)
    {
        if (input.Length < sizeof(ushort))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(input);
        if (count > MaxBatches)
        {
            return null;
        }

        var batches = new List<byte[]>(count);
        var rest = input[sizeof(ushort)..];
        for (var index = 0; index < count; index++)
        {
            if (rest.Length < sizeof(uint))
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            rest = rest[sizeof(uint)..];
            if (length > (uint)rest.Length)
            {
                return null;
            }

            batches.Add(rest[..(int)length].ToArray());
            rest = rest[(int)length..];
        }

        return rest.IsEmpty ? batches : null;
    }

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "segments.json", "segment-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgers", file))
            .Select(static vector => Frame([.. vector.GetProperty("batches").EnumerateArray().Select(static batch => Convert.FromHexString(batch.GetString()!))]));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        if (Unframe(input) is not { } batches)
        {
            return FuzzOutcome.None;
        }

        var reading = LedgerSegmentRules.ReadSegment(batches, firstSlot: 0);
        var bound = MeasureAllocations(input.Length);
        var decoded = DecodeSegment(batches, pageLength: 0);
        bound.Check("LedgerSegmentView");
        var outcome = Check(decoded, reading, "contiguous bytes");

        var pageLength = 7 + input.Length % 57;
        _ = Check(DecodeSegment(batches, pageLength), reading, string.Create(CultureInfo.InvariantCulture, $"pages of {pageLength} bytes"));
        if (outcome == FuzzOutcome.Accepted)
        {
            CheckEncoding(batches);
            CheckRewinds(batches, reading.Rows);
        }

        // A key-filtered view checks the segment rules for its key alone, as the filtered reading does (ledgers.md §4).
        var key = batches
            .Select(static batch => LedgerSegmentRules.ReadBatch(batch).Batch)
            .SelectMany(static batch => batch?.Rows ?? [])
            .FirstOrDefault(static row => row.IsDefining && row.Key is { Length: > 0 })?.Key;
        if (key is not null)
        {
            _ = Check(DecodeSegment(batches, pageLength: 0, key: key), LedgerSegmentRules.ReadSegment(batches, firstSlot: 0, key), "a key-filtered view");
        }

        return outcome;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input)
    {
        if (Unframe(input) is not { } batches)
        {
            return null;
        }

        return Frame([.. batches.Select(static batch => LedgerSegmentRules.Seal(batch) ?? batch)]);
    }

    /// <summary>Decodes the batches from slot 0, batch rules then segment rules, the first failure winning.</summary>
    private static Decoding DecodeSegment(List<byte[]> batches, int pageLength, int from = 0, byte[]? key = null)
    {
        var rows = new List<ResolvedRuleRow>();
        using var view = key is null ? new LedgerSegmentView(new LedgerId(1)) : new LedgerSegmentView(new LedgerId(1), key);
        for (var index = from; index < batches.Count; index++)
        {
            using var buffer = pageLength == 0 ? PagedBuffers.Create(batches[index], []) : PagedBuffers.Create(batches[index], pageLength);
            var rejection = BatchView.Decode(buffer, out var batch) ?? view.Advance(new Slot(index), batch);
            if (rejection is not null)
            {
                return new Decoding(index, rejection, rows);
            }

            foreach (var row in view.Rows)
            {
                rows.Add(new ResolvedRuleRow(
                    row.Tag.Slot.Value,
                    row.Tag.Index,
                    row.Key.ToArray(),
                    (byte)row.Kind,
                    row.Version.Base.ToArray(),
                    row.Version.Count,
                    row.Token.ToArray(),
                    row.Body.ToArray()));
            }
        }

        return new Decoding(-1, null, rows);
    }

    private static bool Same(ResolvedRuleRow left, ResolvedRuleRow right) =>
        left.Slot == right.Slot && left.Index == right.Index && left.Kind == right.Kind && left.VersionCount == right.VersionCount
        && left.Key.AsSpan().SequenceEqual(right.Key) && left.VersionBase.AsSpan().SequenceEqual(right.VersionBase)
        && left.Token.AsSpan().SequenceEqual(right.Token) && left.Body.AsSpan().SequenceEqual(right.Body);

    private FuzzOutcome Check(Decoding decoded, LedgerSegmentReading reading, string layout)
    {
        if (decoded.Rejection is { } rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(
                decoded.FailedBatch == reading.FailedBatch && reason == reading.Reason,
                string.Create(CultureInfo.InvariantCulture, $"rejected batch {decoded.FailedBatch} of {layout} as '{reason}' where the rules give batch {reading.FailedBatch} as '{reading.Reason ?? "valid"}': {rejection.Message}"));
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected {layout} as '{reason}', which no rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        Require(reading.Reason is null, string.Create(CultureInfo.InvariantCulture, $"accepted {layout}, which the rules reject at batch {reading.FailedBatch} as '{reading.Reason}'."));
        Require(decoded.Rows.Count == reading.Rows.Count, string.Create(CultureInfo.InvariantCulture, $"resolved {decoded.Rows.Count} rows from {layout}, where the rules give {reading.Rows.Count}."));
        for (var index = 0; index < decoded.Rows.Count; index++)
        {
            Require(Same(decoded.Rows[index], reading.Rows[index]), string.Create(CultureInfo.InvariantCulture, $"resolved row {index} of {layout} otherwise than the rules give it."));
        }

        return FuzzOutcome.Accepted;
    }

    /// <summary>Checks that <see cref="LedgerBatch"/> encodes the rows and boundaries the rules read to the exact input.</summary>
    private void CheckEncoding(List<byte[]> batches)
    {
        var encoded = LedgerBatches.EncodeSegment([.. batches.Select(static batch => LedgerSegmentRules.ReadBatch(batch).Batch!)]);
        for (var index = 0; index < batches.Count; index++)
        {
            Require(encoded[index].AsSpan().SequenceEqual(batches[index]), string.Create(CultureInfo.InvariantCulture, $"accepted batch {index}, whose rows do not encode back to its bytes."));
        }
    }

    /// <summary>
    /// Checks that a decoding which rewinds from a slot by its batch's segment offset gives the rows from that slot on.
    /// </summary>
    private void CheckRewinds(List<byte[]> batches, IReadOnlyList<ResolvedRuleRow> rows)
    {
        var step = Math.Max(1, batches.Count / MaxRewinds);
        for (var slot = 0; slot < batches.Count; slot += step)
        {
            var offset = LedgerSegmentRules.ReadBatch(batches[slot]).Batch!.Offset;
            var rewound = DecodeSegment(batches, pageLength: 0, from: slot - (int)offset);
            Require(rewound.Rejection is null, string.Create(CultureInfo.InvariantCulture, $"rejected a rewind from slot {slot}: {rewound.Rejection?.Message}"));
            var expected = rows.Where(row => row.Slot >= slot).ToList();
            var actual = rewound.Rows.Where(row => row.Slot >= slot).ToList();
            Require(
                expected.Count == actual.Count && expected.Zip(actual).All(static pair => Same(pair.First, pair.Second)),
                string.Create(CultureInfo.InvariantCulture, $"resolved other rows after a rewind from slot {slot} than from the segment's start."));
        }
    }

    private sealed record Decoding(int FailedBatch, InvalidDataException? Rejection, List<ResolvedRuleRow> Rows);
}
