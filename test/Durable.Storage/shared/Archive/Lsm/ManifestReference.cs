using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A manifest as archive §2.9 describes it, written out apart from <see cref="ManifestCommitRules"/>: <c>DrainedBelow</c>,
/// each ledger's watermark and completion flag, and the listed sections, unordered.
/// </summary>
internal sealed record ReferenceManifest(ulong DrainedBelow, IReadOnlyDictionary<ulong, (long Watermark, bool Complete)> Watermarks, IReadOnlyList<SectionRef> Sections)
{
    /// <summary>
    /// Returns the reference reading of <paramref name="value"/>.
    /// </summary>
    public static ReferenceManifest Of(ManifestValue value) =>
        new(
            value.DrainedBelow.Value,
            value.Watermarks.ToDictionary(static entry => entry.Key.Value, static entry => (entry.Value.Below.Value, entry.Value.Complete)),
            [.. value.Sections]);

    /// <summary>
    /// Returns whether a section equal to <paramref name="section"/> is listed.
    /// </summary>
    public bool Lists(SectionRef section) => Sections.Any(section.Equals);

    /// <summary>
    /// Returns the recorded watermark of <paramref name="ledger"/>: its entry's, or 0.
    /// </summary>
    public long WatermarkOf(ulong ledger) => Watermarks.TryGetValue(ledger, out var entry) ? entry.Watermark : 0;

    /// <summary>
    /// Returns whether <paramref name="ledger"/> is marked complete.
    /// </summary>
    public bool MarkedComplete(ulong ledger) => Watermarks.TryGetValue(ledger, out var entry) && entry.Complete;

    /// <summary>
    /// Returns whether this reading and <paramref name="value"/> hold the same <c>DrainedBelow</c>, watermarks, and set
    /// of sections.
    /// </summary>
    public bool Matches(ManifestValue value)
    {
        var other = Of(value);
        return DrainedBelow == other.DrainedBelow
            && Watermarks.Count == other.Watermarks.Count
            && Watermarks.All(entry => other.Watermarks.TryGetValue(entry.Key, out var match) && match == entry.Value)
            && Sections.Count == other.Sections.Count
            && Sections.All(other.Lists);
    }
}

/// <summary>
/// The commit rules, goals, and effects of archive §2.9's table, and the fold of a batch, read from the spec apart from
/// the production code, so tests judge the committer by an oracle which shares none of its code.
/// </summary>
internal static class ManifestReference
{
    /// <summary>
    /// Returns whether the change's goal holds: a drain's ledger is below <c>DrainedBelow</c>, or its watermark is at least
    /// the drain's end and, when the drain completes it, the ledger is marked complete; a compaction's outputs are listed,
    /// or, when it has none, no input is.
    /// </summary>
    public static bool Goal(ManifestChange change, ReferenceManifest manifest) => change switch
    {
        ManifestChange.Drain drain => drain.Ledger.Value < manifest.DrainedBelow
            || manifest.WatermarkOf(drain.Ledger.Value) >= drain.Below.Value && (!drain.Complete || manifest.MarkedComplete(drain.Ledger.Value)),
        ManifestChange.Compaction compaction => compaction.Outputs.Length == 0
            ? compaction.Inputs.All(input => !manifest.Lists(input))
            : compaction.Outputs.All(manifest.Lists),
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Returns whether the change's rule holds: a drain's ledger is at or above <c>DrainedBelow</c>, not marked complete,
    /// and its watermark is the drain's start; every input of a compaction is listed.
    /// </summary>
    public static bool Rule(ManifestChange change, ReferenceManifest manifest) => change switch
    {
        ManifestChange.Drain drain => drain.Ledger.Value >= manifest.DrainedBelow && !manifest.MarkedComplete(drain.Ledger.Value)
            && manifest.WatermarkOf(drain.Ledger.Value) == drain.From.Value,
        ManifestChange.Compaction compaction => compaction.Inputs.All(manifest.Lists),
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Applies the change: a drain adds its sections, sets its ledger's watermark, marks the ledger complete when it
    /// completes it, and advances <c>DrainedBelow</c> past every complete ledger contiguous from it, removing their
    /// entries; a compaction removes its inputs and adds its outputs.
    /// </summary>
    public static ReferenceManifest Apply(ManifestChange change, ReferenceManifest manifest) => change switch
    {
        ManifestChange.Drain drain => ApplyDrain(drain, manifest),
        ManifestChange.Compaction compaction => manifest with
        {
            Sections = [.. manifest.Sections.Where(section => !compaction.Inputs.Any(section.Equals)), .. compaction.Outputs],
        },
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Folds the batch in submission order: each change whose goal is absent and whose rule holds against the result of
    /// the changes before it applies to that result.
    /// </summary>
    public static ReferenceManifest Fold(IEnumerable<ManifestChange> batch, ReferenceManifest manifest)
    {
        foreach (var change in batch)
        {
            if (!Goal(change, manifest) && Rule(change, manifest))
            {
                manifest = Apply(change, manifest);
            }
        }

        return manifest;
    }

    private static ReferenceManifest ApplyDrain(ManifestChange.Drain drain, ReferenceManifest manifest)
    {
        var watermarks = new Dictionary<ulong, (long Watermark, bool Complete)>(manifest.Watermarks)
        {
            [drain.Ledger.Value] = (drain.Below.Value, drain.Complete),
        };
        var drainedBelow = manifest.DrainedBelow;
        while (watermarks.TryGetValue(drainedBelow, out var entry) && entry.Complete)
        {
            _ = watermarks.Remove(drainedBelow);
            drainedBelow++;
        }

        return new ReferenceManifest(drainedBelow, watermarks, [.. manifest.Sections, .. drain.Sections]);
    }
}
