using System.Collections.Immutable;
using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Archive.Lsm.Tests.CompactionSamples;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Random drains and compactions under the geometry of archive §8.8, over live ledgers of random ranges some of which are
/// undrained, against the history fold of §8.6 (LSM-I5): each compaction is judged by the geometry restated from the spec
/// and by every key's prefix, over its sections and its undrained rows, before and after it.
/// </summary>
public sealed class CompactionPropertyTests : IDisposable
{
    // Keys spread over the keyspace, some sharing a partition several grid steps deep.
    private static readonly byte[][] Keys = [[0x05], [0x05, 0x01], [0x46], [0x45], [0x86, 0xff], [0x85], [0xc4], [0xc5]];

    // Of a step's ten choices, those below this drain the step's rows, and the rest compact.
    private const int DrainChoices = 7;

    // The ranges a ledger takes: the whole keyspace or a half of it.
    private static readonly KeyRange[] Ranges = [KeyRange.Root, KeyRange.Root.Children.Lower, KeyRange.Root.Children.Upper];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Over random grid steps of one or two bits, <c>k</c> from <c>F</c> to <c>F + 2</c>, section sizes from 60 bytes,
    /// blocks of 1, 40, and 100 bytes and 256 KiB, so a push's children share blocks and keys' rows span them,
    /// six ledgers of random ranges, each complete or undrained and listed in the map or not, and random sequences of
    /// drains of complete ledgers and compactions: each compaction's inputs and outputs are those the geometry gives,
    /// pushing every section, up to <c>2 × F × s</c>, at <c>F × s</c> and above, and otherwise merging in place those
    /// smaller than <c>s</c>; the partition holds at most <c>k</c> sections afterwards when the cap took every input; no
    /// output holds a row its key's last <c>Replace</c> or <c>Delete</c> among the inputs supersedes; and every key's
    /// content, <c>HasBase</c>, tombstone, and version base, over its sections and its undrained rows, are unchanged,
    /// unless its last row was an empty <c>Delete</c> and it reads absent.
    /// </summary>
    [Fact]
    public async Task CompactionsUnderTheGeometryKeepEveryKeysFold()
    {
        var geometry = Gen.Select(Gen.Int[1, 2], Gen.Int[0, 2], Gen.Long[60, 1500], Gen.OneOfConst(1, 40, 100, 256 * 1024))
            .Select(static (bits, extra, sectionBytes, blockBytes) => new ArchiveOptions { GridBits = bits, MaxSectionsPerPartition = (1 << bits) + extra, SectionBytes = sectionBytes, SectionBlockBytes = blockBytes });
        var ledger = Gen.Select(Gen.Int[0, Ranges.Length - 1], Gen.Int[0, 3], Gen.Int[0, 3]);
        var row = Gen.Select(Gen.Int[0, Keys.Length - 1], Gen.Int[0, 2], Gen.Int[0, 80], Gen.Bool);
        var step = Gen.Select(Gen.Int[0, 9], Gen.Int[0, 5], row.Array[1, 6]);
        await Gen.Select(geometry, ledger.Array[6], step.Array[1, 40]).SampleAsync(
            sample => _leaks.InScopeAsync(async () =>
            {
                var (options, ledgers, steps) = sample;
                await new History(options, [.. ledgers.Select(static ledger => new LiveLedger(Ranges[ledger.Item1], Complete: ledger.Item2 > 0, Listed: ledger.Item3 > 0))]).RunAsync(steps);
            }),
            iter: 60);
    }

    /// <summary>
    /// Returns the compaction the geometry of archive §8.8 gives for <paramref name="sections"/>, restated apart from the
    /// code: a push of the sections, at <c>F × s</c> and above when the children lie within the deepest depth, or else a
    /// merge in place of those smaller than <c>s</c>, at least two; each takes its sections in position order, skipping one
    /// which would take the total past <c>2 × F × s</c>, the first always taken.
    /// </summary>
    private static (bool Push, SectionRef[] Inputs)? Expected(KeyRange partition, IReadOnlyList<SectionRef> sections, ArchiveOptions options)
    {
        if (sections.Count <= options.MaxSectionsPerPartition)
        {
            return null;
        }

        var fanOut = 1L << options.GridBits;
        var push = sections.Sum(static section => section.Length) >= fanOut * options.SectionBytes && partition.Depth + options.GridBits <= KeyRange.MaxDepth;
        IReadOnlyList<SectionRef> candidates = push ? sections : [.. sections.Where(section => section.Length < options.SectionBytes)];
        if (!push && candidates.Count < 2)
        {
            return null;
        }

        var inputs = new List<SectionRef>();
        foreach (var section in candidates)
        {
            if (inputs.Count == 0 || inputs.Sum(static input => input.Length) + section.Length <= 2 * fanOut * options.SectionBytes)
            {
                inputs.Add(section);
            }
        }

        return (push, [.. inputs]);
    }

    /// <summary>A ledger of the history: its range, whether its rows are drained and complete, and whether the map lists it.</summary>
    private sealed record LiveLedger(KeyRange Range, bool Complete, bool Listed);

    /// <summary>
    /// One case: ledgers 1 to 6, the undrained rows of the incomplete ones, the sections drains of the complete ones land,
    /// and the manifest and map values a compaction derives from.
    /// </summary>
    private sealed class History(ArchiveOptions options, LiveLedger[] ledgers)
    {
        private readonly InMemoryObjectStore _store = new();
        private readonly List<RunRowData> _undrained = [];
        private long _serial;

        private ManifestValue _manifest = ManifestValue.Absent;

        public async Task RunAsync((int Choice, int Ledger, (int Key, int Kind, int Length, bool Empty)[] Rows)[] steps)
        {
            _manifest = Manifest([]);
            var map = MapOf();
            foreach (var (choice, ledger, rows) in steps)
            {
                var (id, live) = (ledger + 1, ledgers[ledger]);
                byte[][] keys = [.. Keys.Where(key => live.Range.Contains(key))];
                RunRowData[] written = [.. rows.Select(row => Written(id, keys[row.Key % keys.Length], (RowKind)row.Kind, row.Length, row.Empty))];
                if (!live.Complete)
                {
                    _undrained.AddRange(written);
                    continue;
                }

                if (choice < DrainChoices)
                {
                    _manifest = Manifest([.. _manifest.Sections, await PutAsync(_store, options.SectionSettings, DrainWorker.GridPartition(live.Range, options.GridBits), written)]);
                    continue;
                }

                var overfull = CompactionWorker.OverfullPartitions(_manifest, options);
                if (!overfull.IsEmpty)
                {
                    _manifest = await CompactAndJudgeAsync(overfull[choice % overfull.Length], map);
                }
            }
        }

        private RunRowData Written(int ledger, byte[] key, RowKind kind, int length, bool empty)
        {
            var serial = ++_serial;
            var payload = kind == RowKind.Delete && empty ? [] : Encoding.UTF8.GetBytes(new string('p', length) + serial);
            return new RunRowData(key, new Tag(new LedgerId((ulong)ledger), new Slot(serial), 0), kind, payload);
        }

        /// <summary>
        /// Returns a manifest value listing <paramref name="sections"/>, in which the complete ledgers are complete:
        /// <c>DrainedBelow</c> at the first incomplete one, and the later complete ones marked.
        /// </summary>
        private ManifestValue Manifest(IEnumerable<SectionRef> sections)
        {
            var drainedBelow = 1;
            while (drainedBelow <= ledgers.Length && ledgers[drainedBelow - 1].Complete)
            {
                drainedBelow++;
            }

            var watermarks = Enumerable.Range(drainedBelow + 1, Math.Max(0, ledgers.Length - drainedBelow))
                .Where(id => ledgers[id - 1].Complete)
                .Select(static id => KeyValuePair.Create(new LedgerId((ulong)id), new ManifestWatermark(new Slot(1), Complete: true)));
            return new ManifestValue(1, new LedgerId((ulong)drainedBelow), watermarks, sections);
        }

        private LedgerMapValue MapOf() =>
            Map((ulong)ledgers.Length + 1, [.. ledgers.Select(static (ledger, index) => (Id: (ulong)index + 1, Ledger: ledger)).Where(static ledger => ledger.Ledger.Listed).Select(static ledger => (ledger.Id, ledger.Ledger.Range))]);

        private async Task<ManifestValue> CompactAndJudgeAsync(KeyRange partition, LedgerMapValue map)
        {
            var sections = _manifest.SectionsOf(partition);
            var geometry = Expected(partition, sections, options);
            Assert.NotNull(geometry);
            var expected = geometry.Value;
            var change = Compacted(await Worker(_store, options).CompactAsync(partition, _manifest, map, Token));
            Assert.Equal(expected.Inputs, change.Inputs);
            Assert.All(change.Outputs, output => Assert.Equal(
                expected.Push ? partition.Depth + options.GridBits : partition.Depth,
                output.Partition.Depth));
            Assert.All(change.Outputs, output => Assert.True(partition.Contains(output.Partition), "An output lies in the compacted partition."));

            var applied = ManifestCommitRules.Apply(_manifest, change);
            var uncapped = expected.Push ? sections.Length : sections.Count(section => section.Length < options.SectionBytes);
            if (expected.Inputs.Length == uncapped)
            {
                Assert.True(applied.SectionsOf(partition).Length <= options.MaxSectionsPerPartition, "The partition holds at most k sections after a compaction which took every input.");
            }

            var inputs = await RowsAsync(_store, CompactionSamples.Manifest(1, change.Inputs));
            var outputs = await RowsAsync(_store, CompactionSamples.Manifest(1, change.Outputs));
            foreach (var (key, rows) in inputs)
            {
                if (rows.LastOrDefault(static row => row.Kind is RowKind.Replace or RowKind.Delete) is { } last && outputs.TryGetValue(key, out var kept))
                {
                    Assert.All(kept, row => Assert.True(row.Tag >= last.Tag, "An output holds a superseded row."));
                }
            }

            var before = WithUndrained(await RowsAsync(_store, _manifest));
            var after = WithUndrained(await RowsAsync(_store, applied));
            foreach (var (key, rows) in before)
            {
                if (after.TryGetValue(key, out var remaining))
                {
                    Assert.Equal(Fold(rows), Fold(remaining));
                    continue;
                }

                Assert.True(rows[^1] is { Kind: RowKind.Delete, Payload.Length: 0 }, "A key reads absent only after an empty Delete.");
            }

            Assert.Empty(after.Keys.Except(before.Keys));
            return applied;
        }

        /// <summary>Adds the undrained rows of each key to <paramref name="rows"/>, in tag order.</summary>
        private Dictionary<string, List<RunRowData>> WithUndrained(Dictionary<string, List<RunRowData>> rows)
        {
            foreach (var row in _undrained)
            {
                var key = Convert.ToHexString(row.Key);
                if (!rows.TryGetValue(key, out var list))
                {
                    rows.Add(key, list = []);
                }

                list.Add(row);
            }

            foreach (var list in rows.Values)
            {
                list.Sort(static (left, right) => left.Tag.CompareTo(right.Tag));
            }

            return rows;
        }
    }
}
