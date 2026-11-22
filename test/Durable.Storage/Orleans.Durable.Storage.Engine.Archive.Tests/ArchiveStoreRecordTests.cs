using System.Text;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The store's record (archive.md §2.4, §7): it reads back as it was written, its store fields and then the caller's
/// parameters, each under <c>caller.</c>, in ordinal order; it refuses each malformed form with its reason; a record refuses
/// every process whose store fields or caller parameters differ from it in any one field, comparing the two as separate
/// maps, so a caller parameter never takes a store field's place, which a comparison that ignores one field fails; and a
/// store's identity refuses an undefined kind and the default ID, with each kind's value pinned.
/// </summary>
[NoPageLeakCheck("The store's record is text; no case rents a page.")]
public sealed class ArchiveStoreRecordTests
{
    private static readonly ArchiveStoreId Id = new(0x0123456789abcdefUL);

    private static readonly Dictionary<string, string> StoreFields = new(StringComparer.Ordinal) { ["max-units"] = "49900", ["max-append-bytes"] = "unbounded" };

    private static readonly Dictionary<string, string> Parameters = new(StringComparer.Ordinal) { ["max-body-bytes"] = "16777216", ["format"] = "json" };

    [Fact]
    public void ARecordReadsBackAsItWasWrittenWithEachGroupInOrdinalOrder()
    {
        var record = ArchiveStoreRecord.Create(Id, "chunks", StoreFields, Parameters);

        Assert.Equal(
            "id=00000000000000000123456789abcdef\nlayout=chunks\nmax-append-bytes=unbounded\nmax-units=49900\ncaller.format=json\ncaller.max-body-bytes=16777216",
            Encoding.UTF8.GetString(record.Encode()));
        var decoded = Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(record.Encode()).Value);
        Assert.Equal(Id, decoded.Id);
        Assert.Equal("chunks", decoded.Layout);
        Assert.Equal(record.StoreFields, decoded.StoreFields);
        Assert.Equal(record.Parameters, decoded.Parameters);
        Assert.Null(ArchiveStoreRecord.FirstDifference(decoded, record));
    }

    /// <summary>A caller parameter which takes a store field's name, or the layout's, is the caller's own, apart from it.</summary>
    [Fact]
    public void ACallerParameterNamedAsAStoreFieldStaysApart()
    {
        var record = ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string> { ["max-units"] = "1", ["layout"] = "x" });
        var decoded = Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(record.Encode()).Value);

        Assert.Equal("49900", decoded.StoreFields["max-units"]);
        Assert.Equal(("1", "x"), (decoded.Parameters["max-units"], decoded.Parameters["layout"]));
        Assert.Equal("chunks", decoded.Layout);
        Assert.Equal(new ArchiveStoreRecordField("max-units", IsParameter: true), ArchiveStoreRecord.FirstDifference(record, ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string> { ["layout"] = "x" })));
    }

    [Theory]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\nbad=\xff", ArchiveStoreRecord.ReasonBadUtf8)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\n", ArchiveStoreRecord.ReasonBadLine)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\nFormat=json", ArchiveStoreRecord.ReasonBadLine)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\ncaller.=json", ArchiveStoreRecord.ReasonBadLine)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\nother.format=json", ArchiveStoreRecord.ReasonBadLine)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout", ArchiveStoreRecord.ReasonBadLine)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\nformat=a\nformat=b", ArchiveStoreRecord.ReasonDuplicateField)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=lsm\ncaller.format=a\ncaller.format=b", ArchiveStoreRecord.ReasonDuplicateField)]
    [InlineData("id=00000000000000000000000000000000\nlayout=lsm", ArchiveStoreRecord.ReasonBadId)]
    [InlineData("layout=lsm\nid=00000000000000000123456789abcdef", ArchiveStoreRecord.ReasonBadId)]
    [InlineData("id=00000000000000000123456789ABCDEF\nlayout=lsm", ArchiveStoreRecord.ReasonBadId)]
    [InlineData("id=00000000000000000123456789abcdef", ArchiveStoreRecord.ReasonBadLayout)]
    [InlineData("id=00000000000000000123456789abcdef\nlayout=", ArchiveStoreRecord.ReasonBadLayout)]
    [InlineData("id=00000000000000000123456789abcdef\nformat=json\nlayout=lsm", ArchiveStoreRecord.ReasonBadLayout)]
    public void AMalformedRecordIsRefusedWithItsReason(string text, string reason)
    {
        var bytes = text.Contains('\xff', StringComparison.Ordinal) ? [.. Encoding.UTF8.GetBytes(text[..^1]), 0xff] : Encoding.UTF8.GetBytes(text);

        var rejection = Assert.IsType<InvalidDataException>(ArchiveStoreRecord.Decode(bytes).Value);

        Assert.Equal(reason, DecodeRejection.ReasonOf(rejection));
    }

    /// <summary>A process whose store fields or caller parameters differ from the record's in any one is refused, naming the field.</summary>
    [Fact]
    public void ARecordRefusesEveryProcessWhichDiffersInOneField() => Assert.Null(FirstUnrefused(ArchiveStoreRecord.FirstDifference));

    /// <summary>
    /// Every single-field difference, either way round, is refused by exactly its own half's check, which names the field
    /// <see cref="ArchiveStoreRecord.FirstDifference"/> names, while the other half's check passes.
    /// </summary>
    [Fact]
    public void EveryDifferenceIsRefusedByExactlyItsOwnHalf()
    {
        foreach (var (field, process) in Processes())
        {
            foreach (var (stored, other) in new[] { (ArchiveStoreRecord.Create(Id, "chunks", StoreFields, Parameters), process), (process, ArchiveStoreRecord.Create(Id, "chunks", StoreFields, Parameters)) })
            {
                var storeHalf = Record.Exception(() => stored.CheckStoreFieldsMatch(other.Layout, other.StoreFields));
                var callerHalf = Record.Exception(() => stored.CheckParametersMatch(other.Parameters));
                var mismatch = Assert.IsType<ArchiveStoreMismatchException>(field.IsParameter ? callerHalf : storeHalf);
                Assert.Null(field.IsParameter ? storeHalf : callerHalf);
                Assert.Equal((field.Name, field.IsParameter), (mismatch.Field, mismatch.IsParameter));
                Assert.Equal(field, ArchiveStoreRecord.FirstDifference(stored, other));
            }
        }
    }

    /// <summary>A comparison which ignores one field, the mutant of archive.md §10, lets a process which differs in it through.</summary>
    [Fact]
    public void AComparisonWhichIgnoresOneFieldFailsTheCheck()
    {
        foreach (var ignored in new[] { new ArchiveStoreRecordField("layout", false), new ArchiveStoreRecordField("max-units", false), new ArchiveStoreRecordField("format", true) })
        {
            Assert.Equal(ignored, FirstUnrefused((stored, process) => IgnoringField(stored, process, ignored)));
        }
    }

    /// <summary>
    /// Each half's check names the first field of its own half which differs, with both values, and passes whatever the
    /// other half holds: the store's check never reads a caller parameter, and the caller's never a store field.
    /// </summary>
    [Theory]
    [InlineData("parameter", "format", true, "json", "xml")]
    [InlineData("parameter missing in the caller's", "format", true, "json", null)]
    [InlineData("parameter missing in the record", "zone", true, null, "a")]
    [InlineData("parameter named as a store field", "layout", true, null, "x")]
    [InlineData("layout", "layout", false, "chunks", "logs")]
    [InlineData("store field", "max-units", false, "49900", "2000")]
    [InlineData("store field missing in the caller's", "max-units", false, "49900", null)]
    [InlineData("store field missing in the record", "zone", false, null, "a")]
    public void EachHalfsCheckThrowsTheMismatchNamingTheFieldAndBothValues(string what, string field, bool isParameter, string? stored, string? caller)
    {
        var record = ArchiveStoreRecord.Create(Id, "chunks", StoreFields, Parameters);
        var process = what switch
        {
            "parameter" => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string>(Parameters) { ["format"] = "xml" }),
            "parameter missing in the caller's" => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string> { ["max-body-bytes"] = Parameters["max-body-bytes"] }),
            "parameter missing in the record" => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string>(Parameters) { ["zone"] = "a" }),
            "parameter named as a store field" => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string>(Parameters) { ["layout"] = "x" }),
            "layout" => ArchiveStoreRecord.Create(Id, "logs", StoreFields, Parameters),
            "store field" => ArchiveStoreRecord.Create(Id, "chunks", new Dictionary<string, string>(StoreFields) { ["max-units"] = "2000" }, Parameters),
            "store field missing in the caller's" => ArchiveStoreRecord.Create(Id, "chunks", StoreFields.Where(static pair => pair.Key != "max-units").ToDictionary(), Parameters),
            "store field missing in the record" => ArchiveStoreRecord.Create(Id, "chunks", new Dictionary<string, string>(StoreFields) { ["zone"] = "a" }, Parameters),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };
        Action differing = isParameter ? () => record.CheckParametersMatch(process.Parameters) : () => record.CheckStoreFieldsMatch(process.Layout, process.StoreFields);
        Action other = isParameter ? () => record.CheckStoreFieldsMatch(process.Layout, process.StoreFields) : () => record.CheckParametersMatch(process.Parameters);

        var mismatch = Assert.Throws<ArchiveStoreMismatchException>(differing);
        Assert.Equal((field, isParameter, stored, caller), (mismatch.Field, mismatch.IsParameter, mismatch.StoredValue, mismatch.CallerValue));
        Assert.Contains(stored is null ? "as nothing" : $"as '{stored}'", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(caller is null ? "with nothing" : $"with '{caller}'", mismatch.Message, StringComparison.Ordinal);
        other();
        Assert.Equal(new ArchiveStoreRecordField(field, isParameter), ArchiveStoreRecord.FirstDifference(record, process));
    }

    [Theory]
    [InlineData("Format", "json")]
    [InlineData("", "json")]
    [InlineData("caller.format", "json")]
    [InlineData("format", "a\nb")]
    public void ACreateOrTheCallersCheckRefusesAParameterWhichIsNoField(string key, string value)
    {
        _ = Assert.Throws<ArgumentException>(() => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string> { [key] = value }));
        _ = Assert.Throws<ArgumentException>(() => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, Parameters).CheckParametersMatch(new Dictionary<string, string> { [key] = value }));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("layout")]
    [InlineData("caller.x")]
    public void ACreateRefusesAStoreFieldWhichIsNoField(string key) =>
        Assert.Throws<ArgumentException>(() => ArchiveStoreRecord.Create(Id, "chunks", new Dictionary<string, string> { [key] = "x" }, Parameters));

    [Fact]
    public void ACreateRefusesAValueWhichIsNotWellFormedOrTheDefaultId()
    {
        _ = Assert.Throws<ArgumentException>(() => ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string> { ["format"] = new string((char)0xd800, 1) }));
        _ = Assert.Throws<ArgumentException>(() => ArchiveStoreRecord.Create(default, "chunks", StoreFields, Parameters));
    }

    /// <summary>
    /// Each kind's value is pinned, the Archive's own persisted values, which the Orleans node registry records; an identity
    /// refuses an undefined kind and the default ID, which no store holds.
    /// </summary>
    [Fact]
    public void AnIdentityPinsEachKindAndRefusesAnUndefinedKindOrTheDefaultId()
    {
        Assert.Equal((1, 2, 3), ((int)ArchiveStoreKind.Lsm, (int)ArchiveStoreKind.Logs, (int)ArchiveStoreKind.Memory));
        Assert.Equal([ArchiveStoreKind.Lsm, ArchiveStoreKind.Logs, ArchiveStoreKind.Memory], Enum.GetValues<ArchiveStoreKind>());
        Assert.Equal((ArchiveStoreKind.Logs, Id), (new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), Id).Kind, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), Id).Id));
        _ = Assert.Throws<ArgumentException>(() => new ArchiveStoreIdentity(0, new ArchiveStorePrefix("t"), Id));
        _ = Assert.Throws<ArgumentException>(() => new ArchiveStoreIdentity((ArchiveStoreKind)4, new ArchiveStorePrefix("t"), Id));
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, default, Id)).ParamName);
        _ = Assert.Throws<ArgumentException>(() => new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, new ArchiveStorePrefix("t"), default));
    }

    /// <summary>
    /// Returns the first field, of the layout, the store's fields, and the caller's parameters, a change to which alone, a
    /// missing parameter, or an added one, <paramref name="difference"/> does not report; or null when it reports every one.
    /// </summary>
    private static ArchiveStoreRecordField? FirstUnrefused(Func<ArchiveStoreRecord, ArchiveStoreRecord, ArchiveStoreRecordField?> difference)
    {
        var stored = ArchiveStoreRecord.Create(Id, "chunks", StoreFields, Parameters);
        foreach (var (field, process) in Processes())
        {
            if (difference(stored, process) is null || difference(process, stored) is null)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the processes which differ from the record of <see cref="StoreFields"/> and <see cref="Parameters"/> in one
    /// field alone, a change to it, a missing one, or an added one, with that field, in ordinal order of the fields.
    /// </summary>
    private static IEnumerable<(ArchiveStoreRecordField Field, ArchiveStoreRecord Process)> Processes()
    {
        var processes = new List<(ArchiveStoreRecordField Field, ArchiveStoreRecord Process)>
        {
            (new("zone", false), ArchiveStoreRecord.Create(Id, "chunks", new Dictionary<string, string>(StoreFields) { ["zone"] = "a" }, Parameters)),
            (new("layout", false), ArchiveStoreRecord.Create(Id, "azure-append-blob", StoreFields, Parameters)),
            (new("zone", true), ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string>(Parameters) { ["zone"] = "a" })),
            (new("format", true), ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string> { ["max-body-bytes"] = "16777216" })),
            (new("max-units", false), ArchiveStoreRecord.Create(Id, "chunks", new Dictionary<string, string> { ["max-append-bytes"] = "unbounded" }, Parameters)),
        };
        foreach (var key in StoreFields.Keys)
        {
            processes.Add((new(key, false), ArchiveStoreRecord.Create(Id, "chunks", new Dictionary<string, string>(StoreFields) { [key] = "1" }, Parameters)));
        }

        foreach (var key in Parameters.Keys)
        {
            processes.Add((new(key, true), ArchiveStoreRecord.Create(Id, "chunks", StoreFields, new Dictionary<string, string>(Parameters) { [key] = "other" })));
        }

        return processes.OrderBy(static process => process.Field.ToString(), StringComparer.Ordinal);
    }

    /// <summary>The mutant: the record's comparison, blind to <paramref name="ignored"/>.</summary>
    private static ArchiveStoreRecordField? IgnoringField(ArchiveStoreRecord stored, ArchiveStoreRecord process, ArchiveStoreRecordField ignored)
    {
        var field = ArchiveStoreRecord.FirstDifference(stored, process);
        return field == ignored ? null : field;
    }
}
