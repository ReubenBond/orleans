using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>Journals's instruments, on the meter <see cref="MeterName"/> (journals.md §8.3).</summary>
internal static class JournalMetrics
{
    internal const string MeterName = "Orleans.Durable.Storage.Journals";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Reloads = Meter.CreateCounter<long>(
        "orleans.durable.storage.journals.reloads",
        unit: "{load}",
        description: "Loads of a warm key before rejecting an entry whose expected version may be the key's rebased version.");

    internal static void RecordReload() => Reloads.Add(1);
}
