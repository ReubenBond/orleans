using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive;

namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>
/// An archive store's record (archive.md §7): rejected with the reason <see cref="StoreRecordRules"/> gives, one of the
/// shared store record rejection vectors' reasons, or accepted as the record the rules give, whose encoding decodes back
/// to it. The record's fields form maps, so an accepted record's bytes need not be its canonical encoding.
/// </summary>
public sealed class StoreRecordTarget : FuzzTarget
{
    /// <summary>The reasons of <c>tests/shared-vectors/archive/store-record-rejections.json</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reasons = new(static () => GoldenVectors.Find().Reasons("archive", "store-record-rejections.json"));

    public override string Name => "archive-store-record";

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        return new[] { "store-records.json", "store-record-rejections.json" }
            .SelectMany(file => vectors.Cases("archive", file))
            .Select(static vector => GoldenVectors.Hex(vector));
    }

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        var reading = StoreRecordRules.Read(input);
        return ArchiveStoreRecord.Decode(input) switch
        {
            InvalidDataException rejection => CheckRejection(rejection, reading),
            ArchiveStoreRecord record => CheckRecord(record, reading),
            null => throw new UnreachableException(),
        };
    }

    private static bool Matches(ArchiveStoreRecord record, RuleStoreRecord expected) =>
        record.Id.ToString() == expected.Id
        && record.Layout == expected.Layout
        && Same(record.StoreFields, expected.Fields)
        && Same(record.Parameters, expected.Parameters);

    private static bool Same(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected) =>
        actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private FuzzOutcome CheckRejection(InvalidDataException rejection, StoreRecordReading reading)
    {
        var reason = DecodeRejection.ReasonOf(rejection);
        Require(reason == reading.Reason, $"rejected the record as '{reason}' where the format's rules give '{reading.Reason ?? "valid"}': {rejection.Message}");
        Require(reason is not null && Reasons.Value.Contains(reason), $"rejected the record as '{reason}', which no store record rejection vector names.");
        return FuzzOutcome.Rejected;
    }

    private FuzzOutcome CheckRecord(ArchiveStoreRecord record, StoreRecordReading reading)
    {
        Require(reading.Record is not null, $"accepted a record which the format's rules reject as '{reading.Reason}'.");
        Require(Matches(record, reading.Record), "decoded a record otherwise than the format's rules give it.");
        var again = ArchiveStoreRecord.Decode(record.Encode()).Value as ArchiveStoreRecord;
        Require(again is not null && ArchiveStoreRecord.FirstDifference(again, record) is null && again.Id == record.Id, "accepted a record whose encoding does not decode back to it.");
        return FuzzOutcome.Accepted;
    }
}
