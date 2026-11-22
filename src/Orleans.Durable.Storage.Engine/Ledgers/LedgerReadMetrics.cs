using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The read path's instruments, on the meter <see cref="LedgerWriterMetrics.MeterName"/> (ledger-protocol.md §8.5).
/// </summary>
internal static class LedgerReadMetrics
{
    private static readonly Meter Meter = new(LedgerWriterMetrics.MeterName);

    private static readonly Counter<long> Hedges = Meter.CreateCounter<long>(
        "orleans.durable.storage.ledger.read.hedges",
        unit: "{read}",
        description: "Replica reads a read below a ledger's known contiguous tail sent to hedge a slow replica.");

    private static readonly Counter<long> HedgeBudgetExhausted = Meter.CreateCounter<long>(
        "orleans.durable.storage.ledger.read.hedge_budget_exhausted",
        unit: "{read}",
        description: "Reads below a ledger's known contiguous tail which waited on a slow replica because the hedge budget was empty.");

    internal static void RecordHedge() => Hedges.Add(1);

    internal static void RecordHedgeBudgetExhausted() => HedgeBudgetExhausted.Add(1);
}
