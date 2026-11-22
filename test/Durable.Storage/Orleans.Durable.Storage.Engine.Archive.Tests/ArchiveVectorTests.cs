using System.Text.Json;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The golden vectors of the archive's own formats (archive.md §7): the store's record, each accepted record decoding to
/// its fields and encoding back to its bytes and each rejected one refused with its reason; and the 20-byte version base
/// of a store whose coverage is the read's map value, a tag's ledger, slot, and index little-endian, distinct for
/// distinct tags. A prefix's content is bounded at <see cref="int.MaxValue"/> bytes.
/// </summary>
[NoPageLeakCheck("The vectors are bytes and text; no case rents a page.")]
public sealed class ArchiveVectorTests
{
    public static TheoryData<string> Records => [.. SharedVectors.Cases("archive", "store-records.json").Select(SharedVectors.Name)];

    public static TheoryData<string> Rejections => [.. SharedVectors.Cases("archive", "store-record-rejections.json").Select(SharedVectors.Name)];

    public static TheoryData<string> VersionBases => [.. SharedVectors.Cases("archive", "version-bases.json").Select(SharedVectors.Name)];

    [Theory]
    [MemberData(nameof(Records))]
    public void EachStoreRecordVectorDecodesToItsFieldsAndEncodesToItsBytes(string name)
    {
        var vector = Case("store-records.json", name);
        var record = Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(SharedVectors.Hex(vector)).Value);

        Assert.Equal(SharedVectors.Text(vector, "id"), record.Id.ToString());
        Assert.Equal(SharedVectors.Text(vector, "layout"), record.Layout);
        Assert.Equal(Map(vector.GetProperty("fields")), record.StoreFields);
        Assert.Equal(Map(vector.GetProperty("parameters")), record.Parameters);
        SharedVectors.AssertBytes(vector, ArchiveStoreRecord.Create(record.Id, record.Layout, Map(vector.GetProperty("fields")), Map(vector.GetProperty("parameters"))).Encode());
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void EachStoreRecordRejectionVectorIsRefusedWithItsReason(string name)
    {
        var vector = Case("store-record-rejections.json", name);

        SharedVectors.AssertRejected(vector, Assert.IsType<InvalidDataException>(ArchiveStoreRecord.Decode(SharedVectors.Hex(vector)).Value));
    }

    [Theory]
    [MemberData(nameof(VersionBases))]
    public void EachVersionBaseVectorEncodesItsTag(string name)
    {
        var vector = Case("version-bases.json", name);
        var tag = new Tag(new LedgerId(SharedVectors.UInt64(vector, "ledger")), new Slot(SharedVectors.Int64(vector, "slot")), (int)SharedVectors.Int64(vector, "index"));

        SharedVectors.AssertBytes(vector, LedgerMapCoverage.VersionBaseOf(tag).AsSpan());
    }

    /// <summary>Distinct tags encode to distinct version bases, so a key's bases never repeat while the store holds it.</summary>
    [Fact]
    public void DistinctTagsEncodeToDistinctVersionBases() =>
        Gen.Select(Gen.ULong[1, ulong.MaxValue], Gen.Long[0, long.MaxValue], Gen.Int[0, int.MaxValue]).Array[2]
            .Sample(static tags =>
            {
                var (left, right) = (new Tag(new LedgerId(tags[0].Item1), new Slot(tags[0].Item2), tags[0].Item3), new Tag(new LedgerId(tags[1].Item1), new Slot(tags[1].Item2), tags[1].Item3));
                Assert.Equal(left == right, LedgerMapCoverage.VersionBaseOf(left) == LedgerMapCoverage.VersionBaseOf(right));
            });

    [Fact]
    public void AContentPastTheBoundIsNotSupported()
    {
        ArchivePrefix.CheckContentLength(int.MaxValue);
        _ = Assert.Throws<NotSupportedException>(() => ArchivePrefix.CheckContentLength(int.MaxValue + 1L));
    }

    private static JsonElement Case(string file, string name) => SharedVectors.Cases("archive", file).Single(vector => SharedVectors.Name(vector) == name);

    private static Dictionary<string, string> Map(JsonElement element) =>
        element.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.GetString()!, StringComparer.Ordinal);
}
