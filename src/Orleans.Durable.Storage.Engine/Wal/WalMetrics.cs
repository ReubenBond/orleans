using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// The write-ahead log's write path: how long each durable write group takes and what it carries, and how long a
/// record waits from acceptance until it is durable.
/// </summary>
internal static class WalMetrics
{
    internal const string MeterName = "Orleans.Durable.Storage.Wal";

    private static readonly Meter Meter = new(MeterName);

    // From 0.1 ms, which a fast device's durable write takes, to 5 s, which only a stalled one does.
    private static readonly InstrumentAdvice<double> DurationBuckets = new()
    {
        HistogramBucketBoundaries = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 5000],
    };

    private static readonly InstrumentAdvice<long> CountBuckets = new()
    {
        HistogramBucketBoundaries = [1, 2, 4, 8, 16, 32, 64, 128, 256],
    };

    private static readonly Histogram<double> GroupDuration = Meter.CreateHistogram(
        "orleans.durable.storage.wal.write_group.duration",
        "ms",
        "Time one write group takes to become durable: its positioned vectored write with data sync.",
        advice: DurationBuckets);

    private static readonly Histogram<long> GroupRecords = Meter.CreateHistogram(
        "orleans.durable.storage.wal.write_group.records",
        "{record}",
        "Records with a page in one write group: how many records share a durable write.",
        advice: CountBuckets);

    private static readonly Histogram<long> GroupPages = Meter.CreateHistogram(
        "orleans.durable.storage.wal.write_group.pages",
        "{page}",
        "Pages one write group writes.",
        advice: CountBuckets);

    private static readonly Histogram<long> GroupConcurrency = Meter.CreateHistogram(
        "orleans.durable.storage.wal.write_group.concurrency",
        "{group}",
        "Write groups in flight when a group was submitted, itself included: the concurrency the log chose for the device.",
        advice: CountBuckets);

    private static readonly Histogram<double> RecordDuration = Meter.CreateHistogram(
        "orleans.durable.storage.wal.record.duration",
        "ms",
        "Time from a record's acceptance until it is durable: waiting for a write slot, then its group's write.",
        advice: DurationBuckets);

    internal static void RecordGroup(TimeSpan elapsed, int records, long pages, int concurrency)
    {
        GroupDuration.Record(elapsed.TotalMilliseconds);
        GroupRecords.Record(records);
        GroupPages.Record(pages);
        GroupConcurrency.Record(concurrency);
    }

    internal static void RecordDurable(TimeSpan elapsed) => RecordDuration.Record(elapsed.TotalMilliseconds);
}
