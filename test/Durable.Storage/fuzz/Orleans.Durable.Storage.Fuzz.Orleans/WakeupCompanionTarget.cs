using System.Buffers;
using System.Text;
using Orleans.Durable.Jobs;
using Orleans.Durable.Storage.Engine;

namespace Orleans.Durable.Storage.Fuzz.Orleans;

/// <summary>
/// The <c>orleans.wakeup</c> companion record format, version 1 (orleans.md §5.15): a canonical format rejected with
/// the reason <see cref="WakeupCompanionRules"/> gives, one of the shared wakeup rejection vectors' reasons, or
/// accepted as the value the rules give, which encodes back to its exact bytes. Exercises both the contiguous
/// <see cref="ReadOnlySpan{T}"/> overload and multi-segment <see cref="ReadOnlySequence{T}"/> overload with
/// allocation bounds.
/// </summary>
public sealed class WakeupCompanionTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("orleans", "wakeup-companion-rejections.json"));

    public override string Name => "wakeup-companion";

    public override int MaxLength => WakeupMessageCodec.MaxEncodedLength + 16;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "wakeup-companions.json", "wakeup-companion-rejections.json" }
            .SelectMany(file => vectors.Cases("orleans", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var expected = WakeupCompanionRules.Read(input);

        var spanBound = MeasureAllocations(input.Length);
        var spanRejection = WakeupMessageCodec.Decode(input, out var spanRecord);
        spanBound.Check("WakeupMessageCodec.Decode(ReadOnlySpan<byte>)");
        var outcome = Check(input, spanRejection, spanRecord, expected, "contiguous bytes");

        var sequence = Segments(input.ToArray(), 3 + input.Length % 11);
        var seqBound = MeasureAllocations(input.Length);
        var seqRejection = WakeupMessageCodec.Decode(sequence, out var seqRecord);
        seqBound.Check("WakeupMessageCodec.Decode(ReadOnlySequence<byte>)");
        _ = Check(input, seqRejection, seqRecord, expected, "segments");

        return outcome;
    }

    private FuzzOutcome Check(ReadOnlySpan<byte> input, InvalidDataException? rejection, WakeupRecord record, WakeupCompanionRules.Reading expected, string layout)
    {
        if (rejection is not null)
        {
            var reason = DecodeRejection.ReasonOf(rejection);
            Require(reason == expected.Reason, $"rejected {layout} as '{reason}' where the rules give '{expected.Reason ?? "valid"}': {rejection.Message}");
            Require(reason is not null && Reasons.Value.Contains(reason), $"rejected {layout} as '{reason}', which no wakeup rejection vector names.");
            return FuzzOutcome.Rejected;
        }

        Require(expected.Value is not null, $"accepted {layout}, which the rules reject as '{expected.Reason}'.");
        var parsed = expected.Value.Value;
        var actualGrain = Encoding.UTF8.GetString(record.Target.Type.Value.AsSpan()) + "/" + Encoding.UTF8.GetString(record.Target.Key.AsSpan());
        Require(actualGrain == parsed.TargetGrainId, $"decoded {layout} target '{actualGrain}' != expected '{parsed.TargetGrainId}'.");
        Require(record.JobName == parsed.JobName, $"decoded {layout} job '{record.JobName}' != expected '{parsed.JobName}'.");
        Require(record.DueTimeUtcTicks == parsed.DueTimeUtcTicks, $"decoded {layout} due time {record.DueTimeUtcTicks} != expected {parsed.DueTimeUtcTicks}.");
        Require(input.SequenceEqual(WakeupMessageCodec.Encode(record)), $"accepted {layout}, whose value does not encode back to its exact bytes.");
        return FuzzOutcome.Accepted;
    }

    private static ReadOnlySequence<byte> Segments(byte[] bytes, int length)
    {
        if (bytes.Length <= length)
        {
            return new(bytes);
        }

        var first = new Segment(bytes.AsMemory(0, length), 0);
        var last = first;
        for (var start = length; start < bytes.Length; start += length)
        {
            last = last.Append(bytes.AsMemory(start, Math.Min(length, bytes.Length - start)));
        }

        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
