using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// The sweeper's instruments, on the LSM's meter (archive §8.5): the last sweep's counts of referenced, pending, and
/// unreadable objects, the objects sweeps marked and deleted, and each sweep's duration.
/// </summary>
internal static class SweepMetrics
{
    /// <summary>The name of the gauge of the unreadable objects the last sweep left in place.</summary>
    internal const string UnreadableObjectsName = "orleans.durable.storage.archive.lsm.sweep.unreadable_objects";

    private static readonly Meter Meter = new(ArchiveMetrics.MeterName);

    private static readonly Gauge<long> UnreadableObjects = Meter.CreateGauge<long>(
        UnreadableObjectsName,
        unit: "{object}",
        description: "The unreferenced run objects whose reference condition the last sweep could not read, which it left in place.");

    private static readonly Gauge<long> ReferencedObjects = Meter.CreateGauge<long>(
        "orleans.durable.storage.archive.lsm.sweep.referenced_objects",
        unit: "{object}",
        description: "The run objects the last sweep found the manifest references.");

    private static readonly Gauge<long> PendingObjects = Meter.CreateGauge<long>(
        "orleans.durable.storage.archive.lsm.sweep.pending_objects",
        unit: "{object}",
        description: "The unreferenced run objects the last sweep found a commit could still reference.");

    private static readonly Counter<long> MarkedObjects = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.lsm.sweep.marked_objects",
        unit: "{object}",
        description: "The run objects sweeps found deletable for the first time, which a later sweep deletes.");

    private static readonly Counter<long> DeletedObjects = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.lsm.sweep.deleted_objects",
        unit: "{object}",
        description: "The run objects sweeps deleted.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "orleans.durable.storage.archive.lsm.sweep.duration",
        unit: "s",
        description: "How long each completed sweep took.");

    internal static void Record(SweepResult result, TimeSpan duration)
    {
        UnreadableObjects.Record(result.Unreadable);
        ReferencedObjects.Record(result.Referenced);
        PendingObjects.Record(result.Pending);
        MarkedObjects.Add(result.Marked);
        DeletedObjects.Add(result.Deleted);
        Duration.Record(duration.TotalSeconds);
    }
}
