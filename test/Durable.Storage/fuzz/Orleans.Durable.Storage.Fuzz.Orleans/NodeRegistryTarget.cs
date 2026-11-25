using System.Buffers;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine;

namespace Orleans.Durable.Storage.Fuzz.Orleans;

/// <summary>
/// The node registry value, <c>CLNR</c> version 1 (orleans.md §7.3): a canonical format, rejected with the reason
/// <see cref="NodeRegistryFormatRules"/> gives, one of the shared registry rejection vectors' reasons, or accepted as the
/// value the rules give, which encodes to exactly its bytes. The target decodes the input from contiguous bytes and from
/// segments of a few bytes each, as a register's read may lend it.
/// </summary>
public sealed class NodeRegistryTarget : FuzzTarget
{
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("orleans", "registry-rejections.json"));

    public override string Name => "orleans-registry";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "registries.json", "registry-rejections.json" }
            .SelectMany(file => vectors.Cases("orleans", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = NodeRegistryFormatRules.Read(input);
        var bound = MeasureAllocations(input.Length);
        var decoding = NodeRegistryCodec.Decode(input);
        bound.Check("NodeRegistryCodec.Decode");
        var outcome = Check(input, decoding, reading, "contiguous bytes");
        _ = Check(input, NodeRegistryCodec.Decode(Segments(input.ToArray(), 3 + input.Length % 11)), reading, "segments");
        return outcome;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => NodeRegistryFormatRules.Seal(input);

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

    private static bool Matches(NodeRegistryValue value, RuleRegistry expected)
    {
        if (value.Revision != expected.Revision || value.Nodes.Length != expected.Nodes.Count
            || (int?)value.ArchiveStore?.Kind != (expected.ArchiveStore == 0 ? null : expected.ArchiveStore)
            || value.ArchiveStore?.Id.Value != expected.ArchiveStoreId
            || value.ArchiveStore?.Prefix.Value != expected.ArchiveStorePrefix
            || value.ObjectLedgerStore?.Value != expected.ObjectLedgerStoreId
            || value.DrainAllRevision != expected.DrainAllRevision)
        {
            return false;
        }

        for (var index = 0; index < value.Nodes.Length; index++)
        {
            var entry = value.Nodes[index];
            var rule = expected.Nodes[index];
            if (entry.Node.Value != rule.Node || entry.Silo.Endpoint.ToString() != rule.SiloEndpoint || entry.Silo.Generation != rule.Generation
                || entry.Ordinal != rule.Ordinal || entry.RetiredRevision != rule.RetiredRevision || entry.FailureDomain != rule.FailureDomain
                || entry.Diskless != rule.Diskless)
            {
                return false;
            }
        }

        return true;
    }

    private FuzzOutcome Check(ReadOnlySpan<byte> input, NodeRegistryDecoding decoding, RegistryReading reading, string layout) => decoding switch
    {
        InvalidDataException rejection => CheckRejection(rejection, reading, layout),
        NodeRegistryValue value => CheckValue(input, value, reading, layout),
        null => throw new UnreachableException(),
    };

    private FuzzOutcome CheckRejection(InvalidDataException rejection, RegistryReading reading, string layout)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(reason == reading.Reason, $"rejected {layout} as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        Require(reason is not null && Reasons.Value.Contains(reason), $"rejected {layout} as '{reason}', which no registry rejection vector names.");
        return FuzzOutcome.Rejected;
    }

    private FuzzOutcome CheckValue(ReadOnlySpan<byte> input, NodeRegistryValue value, RegistryReading reading, string layout)
    {
        Require(reading.Registry is not null, $"accepted {layout}, which the format's rules reject as '{reading.Reason}'.");
        Require(Matches(value, reading.Registry), $"decoded {layout} otherwise than the format's rules give it.");
        Require(input.SequenceEqual(NodeRegistryCodec.Encode(value)), $"accepted {layout}, whose value does not encode back to its bytes.");
        return FuzzOutcome.Accepted;
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

/// <summary>The fuzz targets of the formats <c>Orleans.Durable.Storage</c> decodes: the node registry and the journal node grain's answers.</summary>
public static class OrleansTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } = [new NodeRegistryTarget(), new ReminderMessageTarget(), new JournalWriteAnswerTarget(), new JournalReadAnswerTarget(), new JournalPageAnswerTarget(), new ReplicaWriteAnswerTarget(), new ReplicaReadAnswerTarget(), new ReplicaTailAnswerTarget()];
}
