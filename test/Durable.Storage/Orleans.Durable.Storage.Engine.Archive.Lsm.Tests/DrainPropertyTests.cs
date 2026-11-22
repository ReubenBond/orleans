using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Properties of <see cref="DrainWorker"/>'s drains (archive §8.1): the length a drain projects for its rows, in any order,
/// bounds the length of the one-section drain output the writer encodes from them, by which a segment past the object
/// bound splits across objects; and drains of random segments take each whole closed segment once, in order (LSM-I2).
/// </summary>
public sealed class DrainPropertyTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Over random drain outputs, under block sizes from one byte, which gives every row a block of
    /// its own, and 1 to 64 Bloom bits per key, the projected length is at least the encoded object's.
    /// </summary>
    [Fact]
    public void TheProjectedLengthBoundsTheEncodedDrainOutput() =>
        Gen.Select(RunObjectSamples.DrainOutputs, Gen.OneOfConst(1, 40, 200, 4096, 256 * 1024), Gen.Int[1, 64]).Sample(
            (data, blockBytes, bitsPerKey) => _leaks.InScope(() =>
            {
                var settings = new RunSectionSettings(blockBytes, bitsPerKey);
                var section = data.Sections[0];
                var projected = RunObjectFormat.DrainOutputFixedLength(section.Partition);
                foreach (var row in section.Rows)
                {
                    var built = row.ToRow();
                    using (built.Row)
                    {
                        projected += RunObjectFormat.MaxRowFootprint(built.Row, bitsPerKey);
                    }
                }

                using var encoded = (data with { Settings = settings }).Encode();
                Assert.True(encoded.Content.Length <= projected, $"The drain output holds {encoded.Content.Length} bytes, past the {projected} projected.");
            }),
            iter: 500);

    /// <summary>
    /// Over random compaction outputs of one to three sections, under block sizes from one byte and 1 to 64 Bloom bits per
    /// key, the length a compaction projects, its trailer's fixed parts and each section's with its rows' footprints, is at
    /// least the encoded object's.
    /// </summary>
    [Fact]
    public void TheProjectedLengthBoundsTheEncodedCompactionOutput() =>
        RunObjectSamples.Objects.Where(static data => data.Condition.Value is RunObjectCondition.Compaction).Sample(
            data => _leaks.InScope(() =>
            {
                var condition = (RunObjectCondition.Compaction)data.Condition.Value;
                var projected = RunObjectFormat.CompactionTrailerFixedLength(condition.Inputs);
                foreach (var section in data.Sections)
                {
                    projected += RunObjectFormat.SectionFixedLength(section.Partition);
                    foreach (var row in section.Rows)
                    {
                        var built = row.ToRow();
                        using (built.Row)
                        {
                            projected += RunObjectFormat.MaxRowFootprint(built.Row, data.Settings.BloomBitsPerKey);
                        }
                    }
                }

                using var encoded = data.Encode();
                Assert.True(encoded.Content.Length <= projected, $"The compaction output holds {encoded.Content.Length} bytes, past the {projected} projected.");
            }),
            iter: 500);

    /// <summary>
    /// A drain's section sits at the deepest grid depth containing its ledger's range (archive §8.8): the greatest multiple
    /// of the grid step at or above the range's depth, at the range's ancestor there.
    /// </summary>
    [Fact]
    public void ADrainsPartitionIsTheDeepestGridPartitionContainingItsLedgersRange() =>
        Gen.Select(Gen.Byte.Array[8], Gen.Int[0, 64], Gen.Int[1, ArchiveOptions.MaxGridBits]).Sample(
            static (bits, depth, gridBits) =>
            {
                var range = KeyRange.Containing(bits, depth);
                var partition = DrainWorker.GridPartition(range, gridBits);
                Assert.True(partition.Depth % gridBits == 0 && partition.Depth <= depth && depth - partition.Depth < gridBits, "The partition lies at the deepest grid depth at or above the range's.");
                Assert.True(partition.Contains(range), "The partition contains the ledger's range.");
            });

    /// <summary>
    /// Over random batches (long keys, every kind, payloads up to 400 bytes), segments which markers end after random
    /// batches, and options (block sizes from one byte, 1 to 64 Bloom bits per key, section sizes of a few rows), the
    /// drains of an open ledger to its tail take its closed segments from slot 0, each once and in order: each change starts
    /// and ends at a decided segment start, with none between, so every watermark is one (LSM-I2); its sections together
    /// hold exactly the segment's rows, each object's projected length within the section size unless it holds one row, and
    /// each object as full as the size lets it be, the next object's first row passing it; and the drain after the last
    /// finds the open segment.
    /// </summary>
    [Fact]
    public async Task DrainsTakeEachWholeClosedSegmentOnceAndInOrder()
    {
        var row = Gen.Select(Gen.Byte.Array[1, 300], Gen.Int[0, 2], Gen.Byte.Array[0, 400]);
        var batches = Gen.Select(row.Array[1, 3], Gen.Bool).Array[1, 12];
        var options = Gen.Select(Gen.OneOfConst(1, 64, 4096), Gen.Int[1, 64], Gen.Long[1500, 6000]);
        await Gen.Select(batches, options).SampleAsync(
            async sample =>
            {
                var (data, (blockBytes, bitsPerKey, bound)) = sample;
                await using var world = await ArchiveWorld.StartAsync(TestContext.Current.CancellationToken);
                var ledger = world.Ledgers.OpenContaining([0]).Id;
                var proposed = new Dictionary<Tag, LiveRow>();
                foreach (var (batch, endsSegment) in data)
                {
                    LiveRow[] rows = [.. batch.Select(static row => new LiveRow(row.Item1, (RowKind)row.Item2, row.Item3))];
                    var tags = await world.Ledgers.ProposeAsync(ledger, rows, TestContext.Current.CancellationToken);
                    for (var index = 0; index < rows.Length; index++)
                    {
                        proposed.Add(tags[index], rows[index]);
                    }

                    if (endsSegment)
                    {
                        _ = await world.Ledgers.EndSegmentAsync(ledger, TestContext.Current.CancellationToken);
                    }
                }

                var last = await world.Ledgers.EndSegmentAsync(ledger, TestContext.Current.CancellationToken);
                var tail = await world.Ledgers.ConfirmOpenAsync(ledger, TestContext.Current.CancellationToken);
                var starts = (await world.Ledgers.DecidedStartsAsync(world.Ledgers.Ledger(ledger), tail, TestContext.Current.CancellationToken))!.Value;
                var worker = world.Worker(options: new ArchiveOptions { SectionBlockBytes = blockBytes, BloomBitsPerKey = bitsPerKey, SectionBytes = bound });
                var fixedLength = RunObjectFormat.DrainOutputFixedLength(KeyRange.Root);
                var from = Slot.Zero;
                DrainOutcome outcome = default;
                for (var drains = 0; drains <= starts.Length && (outcome = await world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), tail, TestContext.Current.CancellationToken)).Value is DrainOutcome.Drained drained; drains++)
                {
                    _ = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Outcome.Value);
                    var change = drained.Change;
                    Assert.Equal(from, change.From);
                    Assert.Contains(change.From, starts);
                    Assert.Contains(change.Below, starts);
                    Assert.DoesNotContain(starts, start => start > change.From && start < change.Below);
                    List<Tag> held = [];
                    List<(Tag First, long Projected, long FirstFootprint, int Rows)> objects = [];
                    foreach (var section in change.Sections)
                    {
                        var length = (await world.Objects.ListAsync(section.Object.Value, cancellationToken: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken)).Single().Length;
                        using var content = (await world.Objects.ReadAsync(section.Object, 0, (int)length, TestContext.Current.CancellationToken))!.Value;
                        using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(content).Value);
                        var rows = decoded.Sections.SelectMany(static read => read.Rows.Rows.ToArray()).ToArray();
                        var footprints = rows.Select(row => RunObjectFormat.MaxRowFootprint(row.Row, bitsPerKey)).ToArray();
                        var projected = fixedLength + footprints.Sum();
                        Assert.True(projected <= bound || rows.Length == 1, $"An object of {rows.Length} rows projects {projected} bytes, past the section size of {bound}.");
                        objects.Add((rows[0].Tag, projected, footprints[0], rows.Length));
                        held.AddRange(rows.Select(static row => row.Tag));
                    }

                    Assert.Equal(proposed.Keys.Where(tag => tag.Slot >= change.From && tag.Slot < change.Below).Order(), held.Order());

                    // Objects follow the sorted rows, so the one with the next first row in key order comes next.
                    objects.Sort((left, right) => RunObjectFormat.RowOrder.Compare(Probe(proposed, left.First), Probe(proposed, right.First)));
                    for (var index = 1; index < objects.Count; index++)
                    {
                        Assert.True(objects[index - 1].Projected + objects[index].FirstFootprint > bound, "An object ended before a row which fit it.");
                    }
                    from = change.Below;
                }

                Assert.Equal(new DrainOutcome(new DrainOutcome.OpenSegment(last)), outcome);
                Assert.Equal(last, from);
            },
            iter: 25);
    }

    /// <summary>Returns a row to order by: the proposed row of <paramref name="tag"/>, under its tag, with no body.</summary>
    private static TaggedRow Probe(Dictionary<Tag, LiveRow> proposed, Tag tag) =>
        new(tag, new Row(proposed[tag].Key, proposed[tag].Kind, default));
}
