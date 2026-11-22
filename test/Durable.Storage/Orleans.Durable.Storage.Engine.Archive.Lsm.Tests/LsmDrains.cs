using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// A drain of a ledger's rows into the LSM as <see cref="DrainWorker"/> lands one: the rows from the ledger's watermark,
/// uploaded as one section of the whole keyspace under a fresh drain name, and the drain committed through the manifest
/// committer to the ledger's end, complete.
/// </summary>
internal static class LsmDrains
{
    /// <summary>
    /// Drains <paramref name="rows"/>, which it borrows, of <paramref name="ledger"/> from <paramref name="from"/>, or from
    /// the ledger's watermark, to <paramref name="end"/>, complete, and returns the committer's answer.
    /// </summary>
    public static async Task<ManifestCommitOutcome> DrainAsync(InMemoryObjectStore objects, ManifestView manifest, ManifestCommitter committer, LedgerId ledger, IEnumerable<KeyedRow> rows, Slot end, Slot? from, CancellationToken cancellationToken)
    {
        var start = from ?? (await manifest.ReadAsync(cancellationToken)).Value.RecordedWatermark(ledger);
        TaggedRow[] drained = [.. rows
            .Where(row => row.Row.Tag.Slot >= start)
            .OrderBy(static row => row.Key, Comparer<byte[]>.Create(static (left, right) => left.AsSpan().SequenceCompareTo(right)))
            .ThenBy(static row => row.Row.Tag)
            .Select(static row => new TaggedRow(row.Row.Tag, new Row(row.Key, row.Row.Kind, row.Row.Body)))];
        try
        {
            SectionRef[] sections = [];
            if (drained.Length > 0)
            {
                var name = RunObjectNames.Drain(ledger, start, RunObjectNames.NewId());
                using var encoded = RunObjectFormat.Encode([new RunSectionInput(KeyRange.Root, drained)], new RunObjectCondition.Drain(ledger, start), new ArchiveOptions().SectionSettings);
                _ = await objects.CreateAsync(name, encoded.Content, null, cancellationToken);
                sections = [.. encoded.SectionRefs(name)];
            }

            return await committer.CommitAsync(new ManifestChange.Drain(ledger, start, end, sections, complete: true), cancellationToken);
        }
        finally
        {
            foreach (var row in drained)
            {
                row.Row.Dispose();
            }
        }
    }
}
