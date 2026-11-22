using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Changes a worker derives from a manifest value it read, as drains and compactions do (archive §2.3, §2.4): a drain
/// of an incomplete ledger from its watermark to at most the ledger's tail, complete only at the tail, or a compaction of
/// listed sections into new ones. Each ledger <c>S</c>'s tail is fixed, <see cref="Tail"/>, as a closed ledger's is.
/// Every object name it gives is new in the process.
/// </summary>
internal static class ManifestWorkload
{
    private static int s_names;

    /// <summary>
    /// Returns a change derived from <paramref name="value"/> with <paramref name="random"/>'s choices.
    /// </summary>
    public static ManifestChange Derive(ManifestValue value, Random random)
    {
        if (value.Sections.Length > 0 && random.Next(4) == 0)
        {
            List<SectionRef> inputs = [.. value.Sections.Where(_ => random.Next(3) == 0)];
            if (inputs.Count == 0)
            {
                inputs.Add(value.Sections[random.Next(value.Sections.Length)]);
            }

            return Compaction([.. inputs], [.. Enumerable.Range(0, random.Next(3)).Select(_ => Section(Name("c/1")))]);
        }

        var ledger = value.DrainedBelow.Value + (ulong)random.Next(4);
        while (value.IsComplete(Ledger(ledger)))
        {
            ledger++;
        }

        var from = value.RecordedWatermark(Ledger(ledger)).Value;
        var tail = Tail(ledger);
        var below = Math.Min(from + 1 + random.Next(3), tail);
        SectionRef[] sections = below > from ? [Section(Name(string.Create(CultureInfo.InvariantCulture, $"d/{ledger}/{from}")))] : [];

        // A drain of no slots, from the ledger's tail, only completes it.
        return Drain(ledger, from, below, below == tail && (below == from || random.Next(2) == 0), sections);
    }

    /// <summary>
    /// Returns ledger <paramref name="ledger"/>'s tail: the slot below which it holds every row.
    /// </summary>
    public static long Tail(ulong ledger) => (long)(ledger % 7) * 2;

    private static string Name(string scope) => string.Create(CultureInfo.InvariantCulture, $"{scope}/{Interlocked.Increment(ref s_names):x8}");
}
