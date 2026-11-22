using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// An object-ledger store's record (object-ledgers.md §7): rejected with the reason <see cref="ObjectStoreRecordRules"/>
/// gives, one of the shared rejection vectors' reasons, or accepted as the record the rules give, whose encoding is the
/// input, since the record has one form.
/// </summary>
public sealed class ObjectStoreRecordTarget : FuzzTarget
{
    /// <summary>The reasons of <c>tests/shared-vectors/ledgers/object-store-record-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("ledgers", "object-store-record-rejections.json"));

    public override string Name => "ledgers-object-store-record";

    public override int MaxLength => 256;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "object-store-records.json", "object-store-record-rejections.json" }
            .SelectMany(file => vectors.Cases("ledgers", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = ObjectStoreRecordRules.Read(input);
        return ObjectLedgerStoreRecord.Decode(input) switch
        {
            InvalidDataException rejection => CheckRejection(rejection, reading),
            ObjectLedgerStoreRecord record => CheckRecord(record, reading, input.ToArray()),
            null => throw new UnreachableException(),
        };
    }

    private FuzzOutcome CheckRejection(InvalidDataException rejection, ObjectStoreRecordReading reading)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(reason == reading.Reason, $"rejected the record as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        Require(reason is not null && Reasons.Value.Contains(reason), $"rejected the record as '{reason}', which no object store record rejection vector names.");
        return FuzzOutcome.Rejected;
    }

    private FuzzOutcome CheckRecord(ObjectLedgerStoreRecord record, ObjectStoreRecordReading reading, byte[] input)
    {
        Require(reading.Reason is null, $"accepted a record which the format's rules reject as '{reading.Reason}'.");
        Require(
            record.Id.ToString() == reading.Id && ObjectLedgerStoreRecord.LayoutName(record.Layout) == reading.Layout,
            "decoded a record otherwise than the format's rules give it.");
        Require(record.Encode().AsSpan().SequenceEqual(input), "accepted a record whose encoding is not its input.");
        return FuzzOutcome.Accepted;
    }
}
