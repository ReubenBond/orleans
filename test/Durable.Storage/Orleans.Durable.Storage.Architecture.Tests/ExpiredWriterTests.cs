namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins JS-I7's writer (journals.md §8.6): <c>Expired</c> is written only by the primary's expiry scan. Each step from the
/// scan to the record has one caller across every assembly under src, read from the compiled metadata
/// <see cref="MetadataReferences"/> scans, a nested type, such as a closure or state machine, calling as its outermost
/// type: only <c>LedgerAdmission</c> builds an <c>Expired</c> record (<c>AdmitAsync</c> refusing one), only
/// <c>JournalStore</c> calls admission's <c>ExpireAsync</c> and <c>ExpireColdAsync</c>, which route a key to its
/// admission, only <c>JournalExpiryScan</c> calls the store's <c>ExpireAsync</c> and <c>ExpireColdAsync</c>, and only
/// <c>JournalStore</c>'s <c>ExpireTombstonesAsync</c> runs <c>JournalExpiryScan.RunAsync</c> and <c>RunColdAsync</c>.
/// </summary>
/// <remarks>
/// The record is pinned at its factory, <c>JournalEntryRecord.Expired</c>, rather than at its kind: C# compiles an enum
/// literal such as <c>JournalEntryKind.Expired</c> to a constant, which leaves no reference in the metadata to check.
/// </remarks>
public sealed class ExpiredWriterTests
{
    private const string Journals = "Orleans.Durable.Storage.Engine.Journals.";

    private static readonly Lazy<ScannedType[]> ProductionTypes = new(static () =>
        [.. Repository.ProductionAssemblies().SelectMany(static assembly => MetadataReferences.Scan(assembly.Path, static _ => true))]);

    public static TheoryData<string, string, string> Steps =>
        [
            (Journals + "JournalEntryRecord", "Expired", Journals + "LedgerAdmission"),
            (Journals + "LedgerAdmission", "ExpireAsync", Journals + "JournalStore"),
            (Journals + "LedgerAdmission", "ExpireColdAsync", Journals + "JournalStore"),
            (Journals + "JournalStore", "ExpireAsync", Journals + "JournalExpiryScan"),
            (Journals + "JournalStore", "ExpireColdAsync", Journals + "JournalExpiryScan"),
            (Journals + "JournalExpiryScan", "RunAsync", Journals + "JournalStore"),
            (Journals + "JournalExpiryScan", "RunColdAsync", Journals + "JournalStore"),
        ];

    /// <summary>
    /// The member <paramref name="member"/> of <paramref name="declaringType"/>, which one assembly under src declares, so a
    /// rename cannot retire the check, is called under src by <paramref name="caller"/> alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(Steps))]
    public void OnlyTheExpiryScansStepCallsEachStepTowardAnExpired(string declaringType, string member, string caller)
    {
        var declarations = Repository.ProductionAssemblies()
            .Select(static assembly => MetadataReferences.Declarations(assembly.Path))
            .Where(declared => declared.ContainsKey(declaringType))
            .ToArray();
        Assert.Contains(member, Assert.Single(declarations)[declaringType]);
        Assert.Contains(caller, declarations[0].Keys);

        var callee = new ReferencedMember(declaringType, member);
        string[] callers =
        [
            .. ProductionTypes.Value
                .Where(type => type.Members.Contains(callee))
                .Select(static type => type.FullName.Split('+')[0])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        Assert.Equal([caller], callers);
    }
}
