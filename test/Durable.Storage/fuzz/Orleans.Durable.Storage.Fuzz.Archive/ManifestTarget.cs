using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive.Lsm;

namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>
/// The manifest, <c>CLAM</c> version 1 (archive §7): a canonical format, rejected with the reason
/// <see cref="ManifestRules"/> gives, one of the shared manifest rejection vectors' reasons, or accepted as the value the
/// rules give, which encodes to exactly its bytes. The target decodes the input from contiguous bytes and from segments of
/// a few bytes each, as a manifest read across buffer pages arrives.
/// </summary>
public sealed class ManifestTarget : FuzzTarget
{
    /// <summary>The reasons of <c>test/Durable.Storage/shared-vectors/archive/manifest-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        GoldenVectors.Find().Cases("archive", "manifest-rejections.json").Select(static vector => GoldenVectors.Text(vector, "reason")).ToHashSet(StringComparer.Ordinal));

    public override string Name => "archive-manifest";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "manifest.json", "manifest-rejections.json" }
            .SelectMany(file => vectors.Cases("archive", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = ManifestRules.Read(input);
        var contiguous = new ReadOnlySequence<byte>(input.ToArray());
        var bound = MeasureAllocations(input.Length);
        var decoding = ManifestFormat.Decode(contiguous);
        bound.Check("ManifestFormat.Decode");
        var outcome = Check(input, decoding, reading, "contiguous bytes");

        var segmentLength = 7 + input.Length % 57;
        _ = Check(input, ManifestFormat.Decode(Segments(input, segmentLength)), reading, string.Create(CultureInfo.InvariantCulture, $"segments of {segmentLength} bytes"));
        return outcome;
    }

    /// <summary>
    /// Returns <paramref name="input"/> as a sequence of segments of <paramref name="length"/> bytes each, the last
    /// holding what is left.
    /// </summary>
    private static ReadOnlySequence<byte> Segments(ReadOnlySpan<byte> input, int length)
    {
        if (input.Length <= length)
        {
            return new ReadOnlySequence<byte>(input.ToArray());
        }

        var first = new Segment(input[..length].ToArray(), 0);
        var last = first;
        for (var offset = length; offset < input.Length; offset += length)
        {
            last = last.Append(input[offset..Math.Min(offset + length, input.Length)].ToArray());
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private FuzzOutcome Check(ReadOnlySpan<byte> input, ManifestDecoding decoding, ManifestReading reading, string layout)
    {
        if (decoding.Value is InvalidDataException rejection)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason == reading.Reason, $"rejected the bytes from {layout} as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected the bytes as '{reason}', which no manifest rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        var value = (ManifestValue)decoding.Value;
        var expected = reading.Manifest;
        Require(expected is not null, $"accepted the bytes from {layout}, which the format's rules reject as '{reading.Reason}'.");
        Require(Matches(value, expected), $"decoded the bytes from {layout} otherwise than the format's rules give them.");
        using var encoded = ManifestFormat.Encode(value);
        Require(input.SequenceEqual(encoded.Content.ToArray()), "accepted bytes whose value does not encode back to them.");
        return FuzzOutcome.Accepted;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => ManifestRules.Seal(input);

    private static bool Matches(ManifestValue value, RuleManifest expected) =>
        value.Epoch == expected.Epoch
        && value.DrainedBelow.Value == expected.DrainedBelow
        && value.Watermarks.Select(static entry => new RuleWatermark(entry.Key.Value, (ulong)entry.Value.Below.Value, entry.Value.Complete))
            .SequenceEqual(expected.Watermarks)
        && value.Sections.Length == expected.Sections.Count
        && value.Sections.Zip(expected.Sections).All(static pair => Matches(pair.First, pair.Second));

    private static bool Matches(SectionRef section, RuleSection expected) =>
        string.Equals(section.Object.Value, expected.Object, StringComparison.Ordinal)
        && (ulong)section.Offset == expected.Offset
        && (ulong)section.Length == expected.Length
        && section.Partition.Depth == expected.Depth
        && section.Partition.Prefix.SequenceEqual(expected.Prefix)
        && Tag(section.MinTag) == expected.MinTag
        && Tag(section.MaxTag) == expected.MaxTag
        && (ulong)section.Tail.Offset == expected.TailOffset
        && (ulong)section.Tail.Length == expected.TailLength;

    private static RuleTag Tag(Orleans.Durable.Storage.Engine.Ledgers.Tag tag) =>
        new(tag.Ledger.Value, (ulong)tag.Slot.Value, (uint)tag.Index);

    /// <summary>
    /// One segment of a sequence <see cref="Segments"/> builds.
    /// </summary>
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes, long runningIndex)
        {
            Memory = bytes;
            RunningIndex = runningIndex;
        }

        public Segment Append(byte[] bytes)
        {
            var next = new Segment(bytes, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
