using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The append path's instruments, on the meter <see cref="MeterName"/> (ledger-protocol.md §8.5).
/// </summary>
internal static class LedgerWriterMetrics
{
    internal const string MeterName = "Orleans.Durable.Storage.Ledgers";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> AcquireDuration = Meter.CreateHistogram<double>(
        "orleans.durable.storage.ledger.append.sequencer.acquire.duration",
        unit: "ms",
        description: "Time spent acquiring a ledger writer's sequencer slot.");

    private static readonly Histogram<double> CompletionDuration = Meter.CreateHistogram<double>(
        "orleans.durable.storage.ledger.append.sequencer.complete.duration",
        unit: "ms",
        description: "Time spent completing a ledger writer's sequencer slot after its batch is durable.");

    private static readonly UpDownCounter<long> Occupancy = Meter.CreateUpDownCounter<long>(
        "orleans.durable.storage.ledger.append.sequencer.occupancy",
        unit: "{slot}",
        description: "The number of acquired ledger writer sequencer slots which have not completed.");

    private static readonly Histogram<double> WriteDuration = Meter.CreateHistogram<double>(
        "orleans.durable.storage.ledger.append.payload.write.duration",
        unit: "ms",
        description: "Time spent writing one batch to a ledger's replicas.");

    internal static void RecordAcquire(TimeSpan elapsed) =>
        AcquireDuration.Record(elapsed.TotalMilliseconds);

    internal static void RecordCompletion(TimeSpan elapsed) =>
        CompletionDuration.Record(elapsed.TotalMilliseconds);

    internal static void RecordOccupancy(int delta) => Occupancy.Add(delta);

    internal static void RecordWrite(TimeSpan elapsed) =>
        WriteDuration.Record(elapsed.TotalMilliseconds);
}
