using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// The ledger map value, <c>CLLM</c> version 1 (ledgers.md §7): a canonical format, rejected with the reason
/// <see cref="LedgerMapFormatRules"/> gives, one of the shared map rejection vectors' reasons, or accepted as the value the
/// rules give, which encodes to exactly its bytes. The input is the value, which the target decodes from contiguous bytes
/// and from pages of a few bytes each, as a register's read may lend it.
/// </summary>
public sealed class LedgerMapTarget : FuzzTarget
{
    /// <summary>The reasons of <c>tests/shared-vectors/ledgers/map-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () =>
        GoldenVectors.Find().Cases("ledgers", "map-rejections.json").Select(static vector => GoldenVectors.Text(vector, "reason")).ToHashSet(StringComparer.Ordinal));

    public override string Name => "ledgers-map";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "maps.json", "map-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgers", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = LedgerMapFormatRules.Read(input);
        var bound = MeasureAllocations(input.Length);
        var decoding = LedgerMapValue.Decode(input);
        bound.Check("LedgerMapValue.Decode");
        var outcome = Check(input, decoding, reading, "contiguous bytes");

        var pageLength = 7 + input.Length % 57;
        using (var paged = PagedBuffers.Create(input, pageLength))
        {
            _ = Check(input, LedgerMapValue.Decode(paged.AsReadOnlySequence()), reading, string.Create(CultureInfo.InvariantCulture, $"pages of {pageLength} bytes"));
        }

        return outcome;
    }

    protected override byte[]? Seal(ReadOnlySpan<byte> input) => LedgerMapFormatRules.Seal(input);

    private static bool Matches(LedgerMapValue value, RuleMap expected)
    {
        if (value.Epoch != expected.Epoch || value.NextLedgerId.Value != expected.NextLedgerId || value.RetirementFloor != expected.RetirementFloor
            || value.Ledgers.Length != expected.Ledgers.Count)
        {
            return false;
        }

        for (var index = 0; index < value.Ledgers.Length; index++)
        {
            var ledger = value.Ledgers[index];
            var rule = expected.Ledgers[index];
            ulong? tail = ledger.Status.Value is LedgerStatus.Closed closed ? (ulong)closed.T.Value : null;
            if (ledger.Id.Value != rule.Id || ledger.Range.Depth != rule.Depth || !ledger.Range.Prefix.SequenceEqual(rule.Prefix)
                || !EncodedReplicas(ledger.Location).SequenceEqual(rule.Replicas) || ledger.K != rule.K
                || ledger.Primary.Node.Value != rule.Primary || ledger.Primary.Sequencer.Value != rule.Sequencer || tail != rule.Tail)
            {
                return false;
            }
        }

        return true;
    }

    private FuzzOutcome Check(ReadOnlySpan<byte> input, LedgerMapDecoding decoding, MapReading reading, string layout) => decoding switch
    {
        InvalidDataException rejection => CheckRejection(rejection, reading, layout),
        LedgerMapValue value => CheckValue(input, value, reading, layout),
        null => throw new UnreachableException(),
    };

    private FuzzOutcome CheckRejection(InvalidDataException rejection, MapReading reading, string layout)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(
            reason == reading.Reason,
            $"rejected {layout} as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        Require(reason is not null && Reasons.Value.Contains(reason), $"rejected {layout} as '{reason}', which no map rejection vector names.");
        return FuzzOutcome.Rejected;
    }

    private FuzzOutcome CheckValue(ReadOnlySpan<byte> input, LedgerMapValue value, MapReading reading, string layout)
    {
        Require(reading.Map is not null, $"accepted {layout}, which the format's rules reject as '{reading.Reason}'.");
        Require(Matches(value, reading.Map), $"decoded {layout} otherwise than the format's rules give it.");
        var encoded = new byte[value.EncodedLength];
        value.Write(encoded);
        Require(input.SequenceEqual(encoded), $"accepted {layout}, whose value does not encode back to its bytes.");
        return FuzzOutcome.Accepted;
    }

    /// <summary>The replicas a location encodes: a ledger on nodes its nodes, one in the object store none (a count of 0).</summary>
    private static IEnumerable<Guid> EncodedReplicas(LedgerLocation location) => location switch
    {
        LedgerLocation.Nodes nodes => nodes.Replicas.Select(static node => node.Value),
        LedgerLocation.ObjectStore => [],
        null => throw new UnreachableException("conventions.md §15: a decoded ledger's location holds a case."),
    };
}
