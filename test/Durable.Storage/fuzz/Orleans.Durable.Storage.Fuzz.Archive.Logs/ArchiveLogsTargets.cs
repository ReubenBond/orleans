namespace Orleans.Durable.Storage.Fuzz.Archive.Logs;

/// <summary>
/// The fuzz targets of archive logs' formats: the token codec and Azure's metadata reader (archive-logs.md §10).
/// </summary>
public static class ArchiveLogsTargets
{
    public static IReadOnlyList<FuzzTarget> All { get; } = [new ArchiveLogTokenTarget(), new ArchiveLogMetadataTarget(), new ArchiveLogChunkBaseTarget(), new ArchiveLogChunkTarget()];
}
