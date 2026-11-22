using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Manifest changes and writes for the view and committer tests.
/// </summary>
internal static class ManifestChanges
{
    /// <summary>
    /// Returns a section of the object <paramref name="name"/>, over the whole keyspace, of rows of ledger 1.
    /// </summary>
    public static SectionRef Section(string name)
    {
        var tag = new Tag(new LedgerId(1), new Slot(0), 0);
        return new SectionRef(new ObjectName(name), 0, 100, KeyRange.Root, tag, tag, new ObjectRange(100, 16));
    }

    /// <summary>
    /// Returns <paramref name="count"/> sections, each of its own object, enough past 9,200 that their manifest's
    /// encoding spans several buffer pages of 1 MiB.
    /// </summary>
    public static SectionRef[] ManySections(int count) =>
        [.. Enumerable.Range(0, count).Select(static index => Section(string.Create(CultureInfo.InvariantCulture, $"d/1/0/{index:x32}")))];

    public static ManifestChange Drain(ulong ledger, long from, long below, bool complete = false, params SectionRef[] sections) =>
        new ManifestChange.Drain(new LedgerId(ledger), new Slot(from), new Slot(below), sections, complete);

    public static ManifestChange Compaction(SectionRef[] inputs, params SectionRef[] outputs) => new ManifestChange.Compaction(inputs, outputs);

    /// <summary>
    /// Replaces the manifest with <paramref name="value"/> where it holds <paramref name="expected"/>, as a competing
    /// committer would, and returns the new version.
    /// </summary>
    public static async Task<ObjectVersion> WriteAsync(IObjectStore store, ManifestValue value, ObjectVersion? expected, CancellationToken cancellationToken)
    {
        using var encoded = ManifestFormat.Encode(value);
        var outcome = await store.ReplaceAsync(ObjectName.Manifest, expected, encoded.Content, encoded.Crc32C, cancellationToken);
        return Assert.IsType<ObjectReplaceOutcome.Applied>(outcome.Value).Version;
    }

    /// <summary>
    /// Returns the value <paramref name="changes"/> make of <paramref name="value"/>, one replace later.
    /// </summary>
    public static ManifestValue Next(ManifestValue value, params ManifestChange[] changes)
    {
        var (result, _) = ManifestCommitRules.Fold(value, changes);
        return new ManifestValue(value.Epoch + 1, result.DrainedBelow, result.Watermarks, result.Sections);
    }

    public static Slot Slot(long value) => new(value);

    public static LedgerId Ledger(ulong value) => new(value);
}
