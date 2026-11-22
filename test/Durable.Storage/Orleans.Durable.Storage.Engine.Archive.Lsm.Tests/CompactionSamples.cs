using System.Collections.Immutable;
using System.Text;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Sections, manifest values, and ledger map values for the compaction tests, which write sections straight into an
/// object store, and the history fold's reference reading of archive §8.6 over a key's rows.
/// </summary>
internal static class CompactionSamples
{
    private static readonly NodeId Node = new(new Guid("00000000-0000-0000-0000-00000000000c"));

    // Every placed section is an output of a compaction of this section, which no manifest lists.
    private static readonly RunObjectCondition Placed = new RunObjectCondition.Compaction([new SectionPosition(new ObjectName("d/1/0/placed"), 0)]);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Returns the tag of the row at index 0 of <paramref name="slot"/> of <paramref name="ledger"/>.</summary>
    public static Tag T(ulong ledger, long slot) => new(new LedgerId(ledger), new Slot(slot), 0);

    /// <summary>Returns a row of the one-byte key <paramref name="key"/>, with a UTF-8 payload.</summary>
    public static RunRowData Row(byte key, Tag tag, RowKind kind, string payload = "") =>
        new([key], tag, kind, Encoding.UTF8.GetBytes(payload));

    /// <summary>
    /// Writes <paramref name="rows"/>, in any order, as one section of <paramref name="partition"/> in an object of its
    /// own, and returns the manifest's reference to it.
    /// </summary>
    public static Task<SectionRef> PutAsync(IObjectStore store, KeyRange partition, params RunRowData[] rows) =>
        PutAsync(store, new ArchiveOptions().SectionSettings, partition, rows);

    /// <summary>
    /// Writes <paramref name="rows"/>, in any order, as one section of <paramref name="partition"/> under
    /// <paramref name="settings"/>, in an object of its own, and returns the manifest's reference to it.
    /// </summary>
    public static async Task<SectionRef> PutAsync(IObjectStore store, RunSectionSettings settings, KeyRange partition, params RunRowData[] rows)
    {
        RunRowData[] sorted = [.. rows.Order(Comparer<RunRowData>.Create(static (left, right) => RunObjectFormat.CompareRows(left.Key, left.Tag, right.Key, right.Tag)))];
        var data = new RunObjectData([new RunSectionData(partition, sorted)], Placed, settings);
        using var encoded = data.Encode();
        var name = data.Name(RunObjectNames.NewId());
        _ = await store.CreateAsync(name, encoded.Content, null, Token);
        return encoded.SectionRefs(name)[0];
    }

    /// <summary>
    /// Returns a manifest value listing <paramref name="sections"/>, at epoch 1, in which every ledger below
    /// <paramref name="drainedBelow"/> is complete and every other incomplete.
    /// </summary>
    public static ManifestValue Manifest(ulong drainedBelow, params IEnumerable<SectionRef> sections) =>
        new(1, new LedgerId(drainedBelow), [], sections);

    /// <summary>Returns a compaction worker over <paramref name="store"/>, with a tail cache of its own process.</summary>
    public static CompactionWorker Worker(IObjectStore store, ArchiveOptions options, long maxObjectLength = RunObjectFormat.MaxObjectLength) =>
        new(store, new TailCache(store, options), new ManifestView(store), options, maxObjectLength);

    /// <summary>Returns the change of a compaction which answered <see cref="CompactionOutcome.Compacted"/>.</summary>
    public static ManifestChange.Compaction Compacted(CompactionOutcome outcome) =>
        Assert.IsType<CompactionOutcome.Compacted>(outcome.Value).Change;

    /// <summary>Returns a ledger map value whose next ID is <paramref name="next"/>, listing open ledgers of the given ranges.</summary>
    public static LedgerMapValue Map(ulong next, params (ulong Id, KeyRange Range)[] ledgers) =>
        new(1, new LedgerId(next), 0, [.. ledgers.OrderBy(static ledger => ledger.Id).Select(static ledger =>
            new LedgerMetadata(new LedgerId(ledger.Id), ledger.Range, [Node], 4, new PrimaryClaim(Node, SequencerInstanceId.New()), LedgerStatus.Open.Instance))]);

    /// <summary>Returns every row the sections of <paramref name="manifest"/> hold, by key, in tag order, read whole.</summary>
    public static async Task<Dictionary<string, List<RunRowData>>> RowsAsync(IObjectStore store, ManifestValue manifest)
    {
        var rows = new Dictionary<string, List<RunRowData>>(StringComparer.Ordinal);
        foreach (var section in manifest.Sections)
        {
            var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(store, section, Token));
            using var read = Assert.IsType<RunRows>(await RunObjectReader.ReadSectionAsync(store, section.Object, RunObjectReader.SectionOf(tail, section), Token));
            foreach (var (tag, row) in read.Rows)
            {
                var key = Convert.ToHexString(row.Key.Span);
                if (!rows.TryGetValue(key, out var list))
                {
                    rows.Add(key, list = []);
                }

                list.Add(new RunRowData(row.Key.ToArray(), tag, row.Kind, row.Body.ToArray()));
            }
        }

        foreach (var list in rows.Values)
        {
            list.Sort(static (left, right) => left.Tag.CompareTo(right.Tag));
        }

        return rows;
    }

    /// <summary>
    /// Returns the prefix the fold of a key's rows in tag order gives, restated from archive §8.6 apart from the code: the
    /// base is the last <c>Replace</c> or <c>Delete</c>; the content the base's body when it is a <c>Replace</c>, then each
    /// later <c>Append</c>'s; <c>HasBase</c> whether the base is a <c>Replace</c>; the tombstone the body of a <c>Delete</c>
    /// base which holds one and which no <c>Append</c> follows; and the version base the last row's tag. Absent, null, for no
    /// row.
    /// </summary>
    public static Prefix? Fold(IReadOnlyList<RunRowData> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var start = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Kind is RowKind.Replace or RowKind.Delete)
            {
                start = index;
            }
        }

        var content = new List<byte>();
        var hasBase = start >= 0 && rows[start].Kind == RowKind.Replace;
        if (hasBase)
        {
            content.AddRange(rows[start].Payload);
        }

        var appended = false;
        for (var index = start + 1; index < rows.Count; index++)
        {
            content.AddRange(rows[index].Payload);
            appended = true;
        }

        var tombstone = start >= 0 && rows[start].Kind == RowKind.Delete && rows[start].Payload.Length > 0 && !appended
            ? Convert.ToHexString(rows[start].Payload)
            : null;
        return new Prefix(Convert.ToHexString([.. content]), hasBase, tombstone, rows[^1].Tag);
    }

    /// <summary>A key's prefix: its content, whether it has a base, its tombstone, and its version base, the last row's tag.</summary>
    public sealed record Prefix(string Content, bool HasBase, string? Tombstone, Tag VersionBase);
}
