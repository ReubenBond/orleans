using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.FileSystem.Archive;

/// <summary>
/// The file-system object store's instruments, on the meter <see cref="MeterName"/> (archive §9).
/// </summary>
internal static class FileSystemObjectStoreMetrics
{
    internal const string MeterName = "Orleans.Durable.Storage.FileSystem";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Reclaimed = Meter.CreateCounter<long>(
        "orleans.durable.storage.filesystem.object_store.temporary_files.reclaimed",
        unit: "{file}",
        description: "The temporary files past their age which reclamations deleted, or found gone.");

    private static readonly Counter<long> Refused = Meter.CreateCounter<long>(
        "orleans.durable.storage.filesystem.object_store.temporary_files.refused",
        unit: "{file}",
        description: "The temporary files past their age whose deletion another handle refused, left for a later pass.");

    internal static void RecordReclamation(int reclaimed, int refused)
    {
        Reclaimed.Add(reclaimed);
        Refused.Add(refused);
    }
}
