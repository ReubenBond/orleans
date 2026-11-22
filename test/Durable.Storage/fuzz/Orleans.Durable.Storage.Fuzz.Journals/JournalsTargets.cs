namespace Orleans.Durable.Storage.Fuzz.Journals;

/// <summary>
/// The fuzz targets of the Journals layer's formats: the entry record and a version's text form.
/// </summary>
public static class JournalsTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } = [new EntryTarget(), new VersionTarget()];
}
