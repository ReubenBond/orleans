namespace Orleans.Durable.Storage.Fuzz.Archive;

/// <summary>
/// The fuzz targets of the archive's formats.
/// </summary>
public static class ArchiveTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } = [new ManifestTarget(), new RunObjectTarget(), new StoreRecordTarget()];
}
