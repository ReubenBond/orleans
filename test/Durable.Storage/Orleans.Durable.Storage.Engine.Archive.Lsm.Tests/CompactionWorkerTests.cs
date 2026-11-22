using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Archive.Lsm.Tests.CompactionSamples;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Examples of <see cref="CompactionWorker"/> (archive §2.4, §8.2, §8.8): a full partition pushes its sections into its
/// children and a partition below <c>F × s</c> merges in place only its sections smaller than <c>s</c>, at each boundary,
/// capped at <c>2 × F × s</c>; the drop rule drops superseded rows and keeps each key's last <c>Replace</c> or
/// <c>Delete</c>, and drops an empty <c>Delete</c> only when no older row of its key may lie outside the compaction, in
/// a section or a live ledger (LSM-I5); outputs are uploaded under fresh names which record their inputs; of two racing
/// compactions of one partition one commits, and sweeps around them delete only the inputs it removed and the other's
/// outputs; and every failure leaves the inputs released and the budget whole.
/// </summary>
public sealed class CompactionWorkerTests : IDisposable
{
    private static readonly KeyRange Lower = KeyRange.Root.Children.Lower;
    private static readonly KeyRange Upper = KeyRange.Root.Children.Upper;

    // One grid bit, so a partition has two children, and k = 2; PushAll chooses s.
    private static readonly ArchiveOptions Pushing = new() { GridBits = 1, MaxSectionsPerPartition = 2 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly InMemoryObjectStore _objects = new();
    private readonly ProbeObjectStore _store;

    public CompactionWorkerTests() => _store = new ProbeObjectStore(_objects);

    /// <summary>Where an older row of a key lies outside a compaction of <see cref="Lower"/>.</summary>
    public enum Outside
    {
        Ancestor,
        Descendant,
        LeftByAMergeInPlace,
    }

    /// <summary>How a push fails at its fifth child.</summary>
    public enum PartwayFailure
    {
        Upload,
        BlockRead,
        InputGone,
        CorruptBlock,
    }

    /// <summary>Which compaction the geometry chooses.</summary>
    public enum CompactionKind
    {
        Push,
        MergeInPlace,
    }

    /// <summary>How an input's trailer entry disagrees with its rows.</summary>
    public enum EntryBreak
    {
        MoreRows,
        FewerRows,
        HigherMaxTag,
        LowerMinTag,
    }
    /// <summary>The lower ledgers, and the map, against which an empty <c>Delete</c> of ledger 3 or 4 is judged.</summary>
    public enum LowerLedgers
    {
        /// <summary>Ledger 2 is incomplete and the map lists it over the key's half: the row stays.</summary>
        IncompleteOverTheKey,

        /// <summary>Ledger 2 is incomplete and the map lists it over the other half: the row drops.</summary>
        IncompleteElsewhere,

        /// <summary>Ledger 2 is incomplete and the map does not list it: the row stays.</summary>
        IncompleteUnlisted,

        /// <summary>Ledger 2 is complete, though the map lists it over the key's half: the row drops.</summary>
        CompleteOverTheKey,

        /// <summary>Every lower ledger is complete, but the map predates the <c>Delete</c>'s ledger 3: the row stays.</summary>
        MapBeforeTheDeletesLedger,

        /// <summary>
        /// The <c>Delete</c> is of ledger 4, the map's next ID is 3, and ledger 3, which the map does not know, is
        /// incomplete: the row stays.
        /// </summary>
        IncompleteBeyondTheMap,
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A partition of more than <c>k</c> sections totalling at least <c>F × s</c> pushes every one of them into its
    /// children, each child which receives rows in an object of its own, <c>c/{child}/{id}</c>, whose trailer records the
    /// inputs; the change replaces the inputs with the outputs. Each output's create carries
    /// its writer's CRC-32C of the bytes it verified (archive §3).
    /// </summary>
    [Fact]
    public async Task AFullPartitionPushesEverySectionIntoItsChildren()
    {
        RunRowData[] rows = [Row(0x10, T(1, 1), RowKind.Append, "a"), Row(0x90, T(1, 2), RowKind.Append, "b"), Row(0x10, T(2, 1), RowKind.Append, "c")];
        var inputs = new[] { await PutAsync(_store, KeyRange.Root, rows[0]), await PutAsync(_store, KeyRange.Root, rows[1]), await PutAsync(_store, KeyRange.Root, rows[2]) };
        var manifest = Manifest(3, inputs);

        var change = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        Assert.Equal(inputs.Order(SectionRef.PositionOrder), change.Inputs);
        Assert.Equal([Lower, Upper], change.Outputs.Select(static output => output.Partition));
        Assert.Equal(2, change.Outputs.Select(static output => output.Object).Distinct().Count());
        Assert.StartsWith("c/1-00/", change.Outputs[0].Object.Value, StringComparison.Ordinal);
        Assert.StartsWith("c/1-80/", change.Outputs[1].Object.Value, StringComparison.Ordinal);
        foreach (var output in change.Outputs)
        {
            var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_store, output, Token));
            Assert.Equal(new RunObjectCondition.Compaction([.. change.Inputs.Select(static input => new SectionPosition(input.Object, input.Offset))]), tail.Trailer.Condition);
        }

        var applied = ManifestCommitRules.Apply(manifest, change);
        Assert.Equal(change.Outputs.Order(SectionRef.PositionOrder), applied.Sections);
        var after = await RowsAsync(_store, applied);
        AssertRows([rows[0], rows[2]], after["10"]);
        AssertRows([rows[1]], after["90"]);
        Assert.All(
            change.Outputs.Select(static output => output.Object).Distinct(),
            output => Assert.True(Assert.Single(_store.Writes, write => write.Call == "Create(" + output.Value + ")").CarriesItsContentsCrc32C));
    }

    /// <summary>
    /// A push of <c>k + 1</c> sections at the default grid step writes one object for each of the 16 children, each
    /// holding one section of exactly its child's rows, named after it, and naming every input; it reads each input's tail
    /// once and then, for each child, exactly the child's block and the block before it, in one ranged read; and each
    /// child's lease holds its estimated memory while its object uploads, the first's the inputs' tails too.
    /// </summary>
    [Fact]
    public async Task APushOfKPlusOneSectionsWritesAnObjectForEachOfSixteenChildren()
    {
        var (options, rows, inputs) = await SixteenChildInputsAsync();
        var fanOut = 1 << options.GridBits;
        var children = Enumerable.Range(0, fanOut).Select(child => KeyRange.Containing([(byte)(child << (8 - options.GridBits))], options.GridBits)).ToArray();
        var manifest = Manifest(3, inputs);
        var metadata = await MetadataAsync(inputs);
        var worker = Worker(_store, options);
        var held = new List<long>();
        _store.BeforeCreate = _ => held.Add(worker.BudgetCapacity - worker.AvailableBudgetBytes);
        var readsBefore = _store.RangedReads.Count;

        var change = Compacted(await worker.CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        // Each section holds one row of each child, a block per row, so child c's rows lie in block c, and its read takes
        // block c − 1 too, whose rows may run into it.
        var expectedReads = inputs.Select(static input => (input.Object, input.Tail.Offset, (int)input.Tail.Length)).ToList();
        var expectedHeld = new long[fanOut];
        for (var child = 0; child < fanOut; child++)
        {
            for (var input = 0; input < inputs.Length; input++)
            {
                var blocks = metadata[input].Index.Blocks;
                var first = Math.Max(0, child - 1);
                var length = blocks[child].Offset + blocks[child].Length - blocks[first].Offset;
                expectedReads.Add((inputs[input].Object, blocks[first].Offset, (int)length));
                // Archive §8.2: the blocks' bytes, and for their share of the section's rows, rounded up, 180 bytes each
                // and the average first key's length, one byte here.
                var share = (long)Math.Ceiling((double)rows[input].Length * length / inputs[input].Length);
                expectedHeld[child] += length + share * (180 + 1);
            }
        }

        expectedHeld[0] += inputs.Sum(static input => input.Tail.Length);
        Assert.Equal(expectedReads.Order(), _store.RangedReads.Skip(readsBefore).Order());
        Assert.Equal(expectedHeld, held);
        Assert.Equal(options.CompactionBudgetBytes, worker.AvailableBudgetBytes);
        Assert.Equal(inputs.Order(SectionRef.PositionOrder), change.Inputs);
        Assert.Equal(children, change.Outputs.Select(static output => output.Partition));
        Assert.Equal(fanOut, change.Outputs.Select(static output => output.Object).Distinct().Count());
        var condition = new RunObjectCondition.Compaction([.. change.Inputs.Select(static input => new SectionPosition(input.Object, input.Offset))]);
        for (var child = 0; child < fanOut; child++)
        {
            var output = change.Outputs[child];
            Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"c/4-{child << 4:x2}/"), output.Object.Value, StringComparison.Ordinal);
            var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_store, output, Token));
            _ = Assert.Single(tail.Sections);
            Assert.Equal(condition, tail.Trailer.Condition);
            var kept = (await RowsAsync(_store, Manifest(3, output))).Values.SelectMany(static list => list).OrderBy(static row => row.Tag).ToArray();
            AssertRows([.. rows.SelectMany(static section => section).Where(row => children[child].Contains(row.Key)).OrderBy(static row => row.Tag)], kept);
        }
    }

    /// <summary>
    /// Two sibling pushes on one worker, under a budget of the largest child's lease, which no two children fit at once, take
    /// turns child by child: the second waits for the budget while the first holds it, and each child's upload follows
    /// the other push's previous one; both compact.
    /// </summary>
    [Fact]
    public async Task SiblingPushesInterleaveTheirChildrenWithinTheBudget()
    {
        SectionRef[] lower = [.. await Task.WhenAll(
            PutAsync(_store, Lower, Row(0x10, T(1, 1), RowKind.Append, "a"), Row(0x50, T(1, 2), RowKind.Append, "b")),
            PutAsync(_store, Lower, Row(0x11, T(1, 3), RowKind.Append, "c"), Row(0x51, T(1, 4), RowKind.Append, "d")),
            PutAsync(_store, Lower, Row(0x12, T(1, 5), RowKind.Append, "e"), Row(0x52, T(1, 6), RowKind.Append, "f")))];
        SectionRef[] upper = [.. await Task.WhenAll(
            PutAsync(_store, Upper, Row(0x90, T(1, 7), RowKind.Append, "a"), Row(0xd0, T(1, 8), RowKind.Append, "b")),
            PutAsync(_store, Upper, Row(0x91, T(1, 9), RowKind.Append, "c"), Row(0xd1, T(1, 10), RowKind.Append, "d")),
            PutAsync(_store, Upper, Row(0x92, T(1, 11), RowKind.Append, "e"), Row(0xd2, T(1, 12), RowKind.Append, "f")))];
        var manifest = Manifest(3, [.. lower, .. upper]);
        var pushing = PushAll(manifest, Lower);
        var reads = await Task.WhenAll(new[] { Lower, Upper }.Select(async partition =>
        {
            var plan = Assert.IsType<CompactionPlan>(CompactionWorker.Plan(partition, manifest.SectionsOf(partition), pushing));
            return CompactionWorker.OutputReads(plan, await MetadataAsync(plan.Inputs));
        }));
        var budget = reads.SelectMany(static plan => plan).Max(static read => read.ChargedBytes);
        Assert.True(reads.All(plan => plan.Zip(reads[1 - Array.IndexOf(reads, plan)]).All(pair => pair.First.ChargedBytes + pair.Second.ChargedBytes > budget)), "No two siblings' children fit the budget at once.");
        var worker = Worker(_store, pushing with { CompactionBudgetBytes = budget });
        var creates = Creates();
        Task<CompactionOutcome>? second = null;
        _store.BeforeCreate = earlier => second ??= earlier == creates ? worker.CompactAsync(Upper, manifest, Map(3), Token) : null;

        var first = await worker.CompactAsync(Lower, manifest, Map(3), Token);

        _ = Compacted(first);
        Assert.NotNull(second);
        _ = Compacted(await second);
        string[] uploads = [.. _store.Calls.Where(static call => call.StartsWith("Create(", StringComparison.Ordinal)).Skip(creates).Select(static call => call["Create(".Length..^1]).Select(static name => name[..(name.IndexOf('/', 2) + 1)])];
        Assert.Equal(["c/2-00/", "c/2-80/", "c/2-40/", "c/2-c0/"], uploads);
        Assert.Equal(budget, worker.AvailableBudgetBytes);
    }

    /// <summary>
    /// A child whose lease would pass the budget takes the whole budget, and so runs alone, and the push compacts.
    /// </summary>
    [Fact]
    public async Task AChildPastTheBudgetTakesTheWholeBudget()
    {
        var inputs = await ThreeInputsAsync();
        var worker = Worker(_store, PushAll(Manifest(3, inputs), KeyRange.Root) with { CompactionBudgetBytes = 1 });
        var held = new List<long>();
        _store.BeforeCreate = _ => held.Add(worker.BudgetCapacity - worker.AvailableBudgetBytes);

        _ = Compacted(await worker.CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));

        Assert.Equal([1L, 1L], held);
        Assert.Equal(1, worker.AvailableBudgetBytes);
    }

    /// <summary>
    /// A push over blocks of three rows, at the default grid step, composes its children at block boundaries: a key whose
    /// rows, a <c>Replace</c> and an empty <c>Delete</c> among them, span three blocks from one which holds the previous
    /// child's last key keeps its fold; a child no input holds a row of, though its read takes the block before it, uploads
    /// nothing; and a child whose rows the drop rule removes entirely uploads nothing either.
    /// </summary>
    [Fact]
    public async Task APushComposesItsChildrenAtBlockBoundaries()
    {
        var settings = new ArchiveOptions().SectionSettings with { BlockBytes = 91 };
        var expired = Row(0x50, T(1, 9), RowKind.Append, "e");
        RunRowData[] big =
        [
            Row(0x41, T(1, 2), RowKind.Append, "1"),
            Row(0x42, T(1, 3), RowKind.Append, "2"),
            Row(0x50, T(1, 4), RowKind.Append, "a"),
            Row(0x50, T(1, 5), RowKind.Replace, "b"),
            Row(0x50, T(1, 6), RowKind.Append, "c"),
            Row(0x50, T(1, 7), RowKind.Append, "d"),
            Row(0x50, T(1, 8), RowKind.Delete),
            expired,
            Row(0xc1, T(1, 10), RowKind.Append, "f"),
            Row(0xc1, T(1, 11), RowKind.Delete),
            Row(0xc2, T(1, 12), RowKind.Delete),
            Row(0xf1, T(1, 13), RowKind.Append, "g"),
        ];
        var options = new ArchiveOptions();
        var inputs = new List<SectionRef> { await PutAsync(_store, settings, KeyRange.Root, big) };
        for (var small = 0; small < options.MaxSectionsPerPartition; small++)
        {
            inputs.Add(await PutAsync(_store, settings, KeyRange.Root, Row((byte)(small == 0 ? 0x01 : 0xe0 | small), T(2, small + 1), RowKind.Append, "s")));
        }

        var manifest = Manifest(3, inputs);
        var index = (await MetadataAsync(inputs))[0].Index.Blocks;
        var continued = index.Select((block, number) => (block, number)).First(static pair => pair.block.Continues);
        Assert.True(continued.block.FirstKey.Span.SequenceEqual((byte[])[0x50]), "A block continues key 50.");
        Assert.True(index[continued.number - 1].FirstKey.Span.SequenceEqual((byte[])[0x41]), "The block before it starts with child 4's key 41.");
        Assert.Equal(2, index.Count(static block => block.FirstKey.Span.SequenceEqual((byte[])[0x50])));
        var before = await RowsAsync(_store, manifest);
        var creates = Creates();

        var change = Compacted(await Worker(_store, options with { SectionBytes = inputs.Sum(static input => input.Length) / 16 }).CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        Assert.Equal([0x00, 0x40, 0x50, 0xe0, 0xf0], change.Outputs.Select(static output => output.Partition.Prefix[0]));
        Assert.Equal(5, Creates() - creates);
        var after = await RowsAsync(_store, ManifestCommitRules.Apply(manifest, change));
        Assert.Equal(before.Keys.Except(["C1", "C2"]).Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, rows) in after)
        {
            Assert.Equal(Fold(before[key]), Fold(rows));
        }

        AssertRows([expired], after["50"]);
    }
    /// <summary>
    /// A partition of more than <c>k</c> sections totalling less than <c>F × s</c> merges in place only those smaller than
    /// <c>s</c>, into one section of the same partition, and leaves the others.
    /// </summary>
    [Fact]
    public async Task APartitionBelowFTimesSMergesInPlaceOnlyItsSmallSections()
    {
        var big = await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, new string('x', 400)));
        var small = new[] { await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 2), RowKind.Append, "a")), await PutAsync(_store, KeyRange.Root, Row(0x90, T(1, 3), RowKind.Append, "b")) };
        var options = Pushing with { SectionBytes = big.Length };
        Assert.True(small.Sum(static section => section.Length) < big.Length, "The small sections and the big one total less than F × s.");

        var change = Compacted(await Worker(_store, options).CompactAsync(KeyRange.Root, Manifest(3, [big, .. small]), Map(3), Token));

        Assert.Equal(small.Order(SectionRef.PositionOrder), change.Inputs);
        Assert.Equal(KeyRange.Root, Assert.Single(change.Outputs).Partition);
    }

    /// <summary>
    /// The geometry's boundaries (archive §8.8): sections totalling exactly <c>F × s</c> push, and one byte fewer merge in
    /// place every section smaller than <c>s</c>; a push takes sections in position order up to <c>2 × F × s</c>, skipping
    /// each which would pass it; and a partition of at most <c>k</c> sections, or at the deepest grid depth with fewer than
    /// two smaller than <c>s</c>, holds no compaction.
    /// </summary>
    [Fact]
    public void ThePlanFollowsTheGeometryAtEachBoundary()
    {
        var options = Pushing with { SectionBytes = 20 };
        var atFTimesS = Sections(KeyRange.Root, 10, 10, 20);
        var belowFTimesS = Sections(KeyRange.Root, 10, 10, 19);
        var pastTheCap = Sections(KeyRange.Root, 50, 40, 25);
        var deepest = new KeyRange(new byte[KeyRange.MaxKeyLength], KeyRange.MaxDepth);

        var push = Assert.IsType<CompactionPlan>(CompactionWorker.Plan(KeyRange.Root, atFTimesS, options));
        var merge = Assert.IsType<CompactionPlan>(CompactionWorker.Plan(KeyRange.Root, belowFTimesS, options));
        var capped = Assert.IsType<CompactionPlan>(CompactionWorker.Plan(KeyRange.Root, pastTheCap, options));

        Assert.Equal((true, false, true), (push.IsPush, merge.IsPush, capped.IsPush));
        Assert.Equal(atFTimesS, push.Inputs);
        Assert.Equal(belowFTimesS, merge.Inputs);
        Assert.Equal([pastTheCap[0], pastTheCap[2]], capped.Inputs);
        Assert.Null(CompactionWorker.Plan(KeyRange.Root, atFTimesS[..2], options));
        Assert.Null(CompactionWorker.Plan(deepest, Sections(deepest, 19, 20, 20), options));
        Assert.Equal(KeyRange.MaxDepth, Assert.IsType<CompactionPlan>(CompactionWorker.Plan(deepest, Sections(deepest, 19, 19, 20), options)).OutputDepth);
    }

    /// <summary>
    /// The composition's assignments: each partition which holds a compaction once, in the order of its first section's
    /// position, and none which holds more than <c>k</c> sections but no compaction.
    /// </summary>
    [Fact]
    public void OverfullPartitionsAreThoseWhichHoldACompactionInPositionOrder()
    {
        var options = Pushing with { SectionBytes = 20 };
        var deepest = new KeyRange(new byte[KeyRange.MaxKeyLength], KeyRange.MaxDepth);
        var manifest = Manifest(
            3,
            [.. Sections(Lower, "b", 10, 10, 10), .. Sections(Upper, "a", 10, 10, 10), .. Sections(KeyRange.Root, "c", 10, 10), .. Sections(deepest, "0", 19, 20, 20)]);

        Assert.Equal([Upper, Lower], CompactionWorker.OverfullPartitions(manifest, options));
    }

    /// <summary>A partition of at most <c>k</c> sections compacts nothing, and reads and uploads nothing.</summary>
    [Fact]
    public async Task APartitionOfAtMostKSectionsCompactsNothing()
    {
        var sections = new[] { await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a")), await PutAsync(_store, KeyRange.Root, Row(0x90, T(1, 2), RowKind.Append, "b")) };
        var calls = _store.Calls.Count;

        Assert.Equal(CompactionOutcome.NothingToCompact.Instance, (await Worker(_store, PushAll(Manifest(3, sections), KeyRange.Root)).CompactAsync(KeyRange.Root, Manifest(3, sections), Map(3), Token)).Value);
        Assert.Equal(calls, _store.Calls.Count);
    }

    /// <summary>
    /// The drop rule drops each key's rows before its last <c>Replace</c> or <c>Delete</c> and keeps that row, a tombstoned
    /// <c>Delete</c> among them, with the <c>Append</c>s after it, so each key's prefix is unchanged.
    /// </summary>
    [Fact]
    public async Task SupersededRowsDropAndEachKeysBaseStays()
    {
        RunRowData[] a = [Row(0x10, T(1, 1), RowKind.Append, "a0"), Row(0x10, T(1, 2), RowKind.Replace, "a1"), Row(0x10, T(1, 3), RowKind.Append, "a2")];
        RunRowData[] b = [Row(0x90, T(1, 4), RowKind.Replace, "b0"), Row(0x90, T(1, 5), RowKind.Delete, "tomb"), Row(0x90, T(1, 6), RowKind.Append, "b1"), Row(0x90, T(2, 1), RowKind.Delete, "tomb2")];
        var inputs = new[] { await PutAsync(_store, KeyRange.Root, a[0], b[0], b[1]), await PutAsync(_store, KeyRange.Root, a[1], b[2]), await PutAsync(_store, KeyRange.Root, a[2], b[3]) };
        var manifest = Manifest(3, inputs);
        var before = await RowsAsync(_store, manifest);

        var change = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        var after = await RowsAsync(_store, ManifestCommitRules.Apply(manifest, change));
        AssertRows(a[1..], after["10"]);
        AssertRows([b[3]], after["90"]);
        Assert.Equal(Fold(before["10"]), Fold(after["10"]));
        Assert.Equal(Fold(before["90"]), Fold(after["90"]));
    }

    /// <summary>
    /// An empty <c>Delete</c>, an expiry, drops with the rows it supersedes when no older row of its key lies outside the
    /// compaction, so the key reads absent; the <c>Append</c>s after it stay. A section outside the compaction which holds
    /// only newer rows of the key, or rows of other keys alone, holds no older row.
    /// </summary>
    [Fact]
    public async Task AnEmptyDeleteWithNothingOlderOutsideDrops()
    {
        var inputs = new[]
        {
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a0"), Row(0x90, T(1, 3), RowKind.Append, "b0")),
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 2), RowKind.Delete), Row(0x90, T(1, 4), RowKind.Delete)),
            await PutAsync(_store, KeyRange.Root, Row(0x90, T(1, 5), RowKind.Append, "b1")),
        };
        var newer = await PutAsync(_store, Upper, Row(0x90, T(2, 1), RowKind.Append, "b2"));
        var other = await PutAsync(_store, Lower, Row(0x11, T(1, 0), RowKind.Append, "c"));
        var manifest = Manifest(3, [.. inputs, newer, other]);

        var change = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        var after = await RowsAsync(_store, ManifestCommitRules.Apply(manifest, change));
        Assert.False(after.ContainsKey("10"));
        AssertRows([Row(0x90, T(1, 5), RowKind.Append, "b1"), Row(0x90, T(2, 1), RowKind.Append, "b2")], after["90"]);
    }

    /// <summary>
    /// An empty <c>Delete</c> stays when an older row of its key may lie outside the compaction: in a section of an
    /// ancestor partition, of a descendant, or of the same partition which a merge in place leaves.
    /// </summary>
    [Theory]
    [InlineData(Outside.Ancestor)]
    [InlineData(Outside.Descendant)]
    [InlineData(Outside.LeftByAMergeInPlace)]
    public async Task AnEmptyDeleteWithAnOlderRowInASectionOutsideStays(Outside where)
    {
        var options = Pushing;
        var deleted = Row(0x10, T(1, 5), RowKind.Delete);
        var inputs = await DeleteInputsAsync(Lower, deleted);
        var older = Row(0x10, T(1, 1), RowKind.Append, "a0");
        var outside = where switch
        {
            Outside.Ancestor => await PutAsync(_store, KeyRange.Root, older),
            Outside.Descendant => await PutAsync(_store, Lower.Children.Lower, older),
            Outside.LeftByAMergeInPlace => await PutAsync(_store, Lower, older with { Payload = new byte[400] }),
            _ => throw new ArgumentOutOfRangeException(nameof(where)),
        };
        var manifest = Manifest(3, [.. inputs, outside]);
        options = where == Outside.LeftByAMergeInPlace ? Pushing with { SectionBytes = outside.Length } : PushAll(manifest, Lower);
        var change = Compacted(await Worker(_store, options).CompactAsync(Lower, manifest, Map(3), Token));

        Assert.DoesNotContain(outside, change.Inputs);
        AssertKept(deleted, await RowsAsync(_store, ManifestCommitRules.Apply(manifest, change)));
    }

    /// <summary>
    /// An empty <c>Delete</c> stays when an outside section may hold an older row of its key only by its metadata: its
    /// object is gone since the manifest value was read, or its Bloom filter admits the key, which its block index then
    /// names a candidate block for, though the section holds no row of it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEmptyDeleteStaysWhenAnOutsideSectionCannotRuleItsKeyOut(bool objectGone)
    {
        // At one bit per key, the filter of 31 even keys admits most odd keys; the block index's one block names each.
        var settings = new ArchiveOptions().SectionSettings with { BloomBitsPerKey = 1 };
        var outside = await PutAsync(_store, settings, Lower.Children.Lower, [.. Enumerable.Range(1, 31).Select(index => Row((byte)(2 * index), T(1, index), RowKind.Append, "x"))]);
        var metadata = RunObjectReader.SectionOf(Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_store, outside, Token)), outside);
        var key = Enumerable.Range(1, 30).Select(index => (byte)(2 * index + 1)).First(key => metadata.Candidates([key]).Count > 0);
        var deleted = Row(key, T(2, 5), RowKind.Delete);
        var inputs = await DeleteInputsAsync(Lower, deleted);
        if (objectGone)
        {
            await _objects.DeleteAsync(outside.Object, Token);
        }

        var manifest = Manifest(3, [.. inputs, outside]);
        var change = Compacted(await Worker(_store, PushAll(manifest, Lower)).CompactAsync(Lower, manifest, Map(3), Token));

        AssertKept(deleted, await RowsAsync(_store, Manifest(3, change.Outputs)));
    }

    /// <summary>
    /// An empty <c>Delete</c> of a complete ledger drops only when every lower ledger whose range may contain its key is
    /// complete in the manifest value: the map gives a ledger's range, and one it does not list, or one at or past its next
    /// ID, must be complete.
    /// </summary>
    [Theory]
    [InlineData(LowerLedgers.IncompleteOverTheKey, false)]
    [InlineData(LowerLedgers.IncompleteElsewhere, true)]
    [InlineData(LowerLedgers.IncompleteUnlisted, false)]
    [InlineData(LowerLedgers.CompleteOverTheKey, true)]
    [InlineData(LowerLedgers.MapBeforeTheDeletesLedger, false)]
    [InlineData(LowerLedgers.IncompleteBeyondTheMap, false)]
    public async Task AnEmptyDeleteDropsOnlyOnceEveryLowerLedgerContainingItsKeyIsComplete(LowerLedgers ledgers, bool drops)
    {
        var deleteLedger = ledgers == LowerLedgers.IncompleteBeyondTheMap ? 4UL : 3UL;
        var (drainedBelow, map) = ledgers switch
        {
            LowerLedgers.IncompleteOverTheKey => (2UL, Map(4, (2, Lower), (3, KeyRange.Root))),
            LowerLedgers.IncompleteElsewhere => (2UL, Map(4, (2, Upper), (3, KeyRange.Root))),
            LowerLedgers.IncompleteUnlisted => (2UL, Map(4, (3, KeyRange.Root))),
            LowerLedgers.CompleteOverTheKey => (3UL, Map(4, (2, Lower), (3, KeyRange.Root))),
            LowerLedgers.MapBeforeTheDeletesLedger => (3UL, Map(3, (2, Upper))),
            LowerLedgers.IncompleteBeyondTheMap => (3UL, Map(3, (2, Upper))),
            _ => throw new ArgumentOutOfRangeException(nameof(ledgers)),
        };
        var inputs = new[]
        {
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a0")),
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(deleteLedger, 1), RowKind.Delete)),
            await PutAsync(_store, KeyRange.Root, Row(0x90, T(deleteLedger, 2), RowKind.Append, "b")),
        };
        // The Delete's ledger is complete, so it supersedes the rows before it (LSM-I5); the case decides only the lower ones.
        var manifest = drainedBelow == deleteLedger
            ? Manifest(deleteLedger + 1, inputs)
            : new ManifestValue(1, new LedgerId(drainedBelow), [KeyValuePair.Create(new LedgerId(deleteLedger), new ManifestWatermark(new Slot(3), Complete: true))], inputs);

        var change = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, map, Token));

        Assert.Equal(!drops, (await RowsAsync(_store, ManifestCommitRules.Apply(manifest, change))).ContainsKey("10"));
    }

    /// <summary>
    /// A <c>Replace</c> of a ledger not yet complete supersedes nothing (LSM-I5): ledger 1, dropped, holds <c>Replace A</c>
    /// and <c>Append B</c>; ledger 2, open, holds <c>Append D</c> at slot 10 and <c>Replace C</c> at slot 50, both drained
    /// past a reader's tail of 40. A compaction keeps A, B, and D, so the reader, whose map lists ledger 2 open and which
    /// scans it below 40, folds the prefix and D to <c>A B D</c>, the key's state at its cut; once ledger 2 completes, a
    /// compaction drops them, and a reader then scans ledger 2 to its <c>T</c>, past C, or reads C in the prefix.
    /// </summary>
    [Fact]
    public async Task AReplaceOfALedgerNotYetCompleteSupersedesNothingALaggingReaderNeeds()
    {
        var inputs = new[]
        {
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Replace, "A"), Row(0x10, T(1, 2), RowKind.Append, "B")),
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(2, 10), RowKind.Append, "D"), Row(0x10, T(2, 50), RowKind.Replace, "C")),
            await PutAsync(_store, KeyRange.Root, Row(0x90, T(2, 51), RowKind.Append, "x")),
        };
        var open = new ManifestValue(1, new LedgerId(2), [KeyValuePair.Create(new LedgerId(2), new ManifestWatermark(new Slot(60), Complete: false))], inputs);
        var map = Map(3, (2, KeyRange.Root));

        var change = Compacted(await Worker(_store, PushAll(open, KeyRange.Root)).CompactAsync(KeyRange.Root, open, map, Token));
        var compacted = ManifestCommitRules.Apply(open, change);
        Assert.Equal(["A", "B", "D", "C"], (await RowsAsync(_store, compacted))["10"].Select(static row => System.Text.Encoding.UTF8.GetString(row.Payload)));

        // The lagging reader: the LSM's prefix of the ledgers its map no longer lists, then ledger 2's rows below 40.
        await StoreManifestAsync(compacted);
        var store = new LsmArchiveStore(_store, new ManifestView(_store), new TailCache(_store, new ArchiveOptions()), new ArchiveOptions(), TimeProvider.System);
        using (var read = Assert.IsType<ArchiveStoreRead.Read>((await store.ReadAsync(new byte[] { 0x10 }, map, Token)).Value))
        {
            Assert.Equal("AB", System.Text.Encoding.UTF8.GetString(read.Prefix.Content.ToArray()));
        }

        // Once ledger 2 is complete, its Replace supersedes the rows before it in a compaction of the same inputs.
        var complete = new ManifestValue(1, new LedgerId(3), [], inputs);
        var later = Compacted(await Worker(_store, PushAll(complete, KeyRange.Root)).CompactAsync(KeyRange.Root, complete, Map(4, (3, KeyRange.Root)), Token));
        Assert.Equal(["C"], (await RowsAsync(_store, ManifestCommitRules.Apply(complete, later)))["10"].Select(static row => System.Text.Encoding.UTF8.GetString(row.Payload)));
    }

    /// <summary>
    /// An output whose content no longer matches the CRC-32C its writer computed is refused: memory changed after
    /// verification, so the worker reports the defect, which stops the process, returns no change, and leaves no output
    /// object; the budget is whole again (archive §3, conventions.md §14).
    /// </summary>
    [Fact]
    public async Task AnOutputRefusedForChangedContentStopsTheProcess()
    {
        var inputs = await ThreeInputsAsync();
        var worker = Worker(_store, PushAll(Manifest(3, inputs), KeyRange.Root));
        var before = await _objects.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token);
        var since = FatalErrors.ReportedCount;
        _store.ChangeContent = true;

        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(() => worker.CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        _ = Assert.IsType<ObjectContentChangedException>(report.Failure.Exception);
        Assert.Equal(before, await _objects.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token));
        Assert.Equal(new ArchiveOptions().CompactionBudgetBytes, worker.AvailableBudgetBytes);
    }

    /// <summary>
    /// archive-lsm.md §2.4: an input whose object is gone while a manifest read afresh still lists its section is a listed
    /// object deleted, corruption which names the object and counts, and the compaction uploads nothing.
    /// </summary>
    [Fact]
    public async Task AGoneInputWhichAFreshReadStillListsIsCorrupt()
    {
        var inputs = await ThreeInputsAsync();
        await StoreManifestAsync(Manifest(3, inputs));
        await _objects.DeleteAsync(inputs[1].Object, Token);
        using var counted = new CorruptCount();

        var thrown = await Assert.ThrowsAsync<ArchiveCorruptException>(() => Worker(_store, PushAll(Manifest(3, inputs), KeyRange.Root)).CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));

        Assert.Contains(inputs[1].Object.Value, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(1, counted.Total);
        Assert.Equal(3, Creates());
    }

    /// <summary>
    /// An input whose object is gone and which a manifest read afresh no longer lists was removed by a concurrent
    /// compaction and then swept (LSM-I6): the worker plans the partition again from the fresh value and compacts its
    /// inputs, and counts no corruption.
    /// </summary>
    [Fact]
    public async Task AGoneInputWhichAFreshReadNoLongerListsIsPlannedAgain()
    {
        var inputs = await ThreeInputsAsync();
        var replacement = await PutAsync(_store, KeyRange.Root, Row(0x50, T(2, 1), RowKind.Append, "d"));
        var fresh = Manifest(4, [inputs[0], inputs[2], replacement]);
        await StoreManifestAsync(fresh);
        await _objects.DeleteAsync(inputs[1].Object, Token);
        using var counted = new CorruptCount();

        var change = Compacted(await Worker(_store, PushAll(fresh, KeyRange.Root)).CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));

        Assert.Equal(fresh.SectionsOf(KeyRange.Root).Order(SectionRef.PositionOrder), change.Inputs.Order(SectionRef.PositionOrder));
        Assert.Equal(0, counted.Total);
    }

    /// <summary>
    /// A compaction whose every plan meets an input gone, which each next fresh read no longer lists, stops after
    /// <see cref="CompactionWorker.Attempts"/> plans as an unknown outcome, an <see cref="IOException"/>, never
    /// corruption.
    /// </summary>
    [Fact]
    public async Task ACompactionWhoseInputsKeepVanishingFailsAsAnUnknownOutcome()
    {
        var inputs = await ThreeInputsAsync();
        SectionRef[] vanishing = [inputs[1], .. await Task.WhenAll(Enumerable.Range(0, CompactionWorker.Attempts).Select(index => PutAsync(_store, KeyRange.Root, Row(0x50, T(2, index + 1), RowKind.Append, "v"))))];
        foreach (var section in vanishing)
        {
            await _objects.DeleteAsync(section.Object, Token);
        }

        // Each fresh read finds a newer value, in which a concurrent compaction replaced the gone input with another one
        // a sweep then deleted.
        var reads = 0;
        _store.BeforeReadVersioned = name => name == ObjectName.Manifest ? StoreManifestAsync(Manifest(4UL + (ulong)reads, [inputs[0], inputs[2], vanishing[++reads]])) : Task.CompletedTask;
        using var counted = new CorruptCount();

        _ = await Assert.ThrowsAsync<IOException>(() => Worker(_store, PushAll(Manifest(3, inputs), KeyRange.Root)).CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));

        Assert.Equal(CompactionWorker.Attempts, reads);
        Assert.Equal(0, counted.Total);
        Assert.Equal(3 + CompactionWorker.Attempts, Creates());
    }

    /// <summary>Two inputs which hold a row of one key under one tag are corrupt, and the compaction uploads nothing.</summary>
    [Fact]
    public async Task TwoInputsHoldingARowUnderOneTagAreCorrupt()
    {
        var inputs = new[] { await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a")), await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a")), await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 3), RowKind.Append, "c")) };

        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => Worker(_store, PushAll(Manifest(3, inputs), KeyRange.Root)).CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));
        Assert.Equal(3, Creates());
    }

    /// <summary>
    /// An input whose trailer fails its CRC-32C is corrupt, and so is an outside section's which the drop rule reads; the
    /// compaction uploads nothing, and corruption never counts as an unknown which keeps a row.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATrailerFailingItsCrcIsCorruptAndUploadsNothing(bool outside)
    {
        var deleted = Row(0x10, T(1, 5), RowKind.Delete);
        var inputs = await DeleteInputsAsync(Lower, deleted);
        var older = await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a0"));
        await CorruptTrailerAsync(outside ? older : inputs[1]);
        var creates = Creates();

        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => Worker(_store, PushAll(Manifest(3, [.. inputs, older]), Lower)).CompactAsync(Lower, Manifest(3, [.. inputs, older]), Map(3), Token));
        Assert.Equal(creates, Creates());
    }

    /// <summary>
    /// A compaction whose outputs would pass the object bound writes them across several objects, each within it and named
    /// after its own first section's partition, and every row lands once.
    /// </summary>
    [Fact]
    public async Task OutputsPastTheObjectBoundSpanSeveralObjects()
    {
        var rows = Enumerable.Range(0, 24).Select(index => Row((byte)(index * 10), T(1, index + 1), RowKind.Append, new string('p', 60))).ToArray();
        var inputs = new[] { await PutAsync(_store, KeyRange.Root, rows[..8]), await PutAsync(_store, KeyRange.Root, rows[8..16]), await PutAsync(_store, KeyRange.Root, rows[16..]) };
        var manifest = Manifest(3, inputs);

        var change = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root), maxObjectLength: 1000).CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        var objects = change.Outputs.GroupBy(static output => output.Object).ToArray();
        Assert.True(objects.Length > 2, "The outputs span several objects.");
        foreach (var outputs in objects)
        {
            var tail = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_store, outputs.First(), Token));
            Assert.True(tail.Trailer.ObjectLength <= 1000, $"An output object holds {tail.Trailer.ObjectLength} bytes.");
            Assert.Equal(RunObjectNames.Compaction(tail.Trailer.Sections[0].Partition, 0).Value[..^32], outputs.Key.Value[..^32]);
        }

        var after = await RowsAsync(_store, ManifestCommitRules.Apply(manifest, change));
        AssertRows(rows, [.. after.Values.SelectMany(static list => list).OrderBy(static row => row.Tag)]);
    }

    /// <summary>
    /// Two compactions of one partition from one value race: the committer applies the first and supersedes the second,
    /// whose input is no longer listed (LSM-I5); their outputs take distinct names, and the manifest lists only the first's.
    /// </summary>
    [Fact]
    public async Task OfTwoRacingCompactionsOfOnePartitionOneCommits()
    {
        var inputs = await ThreeInputsAsync();
        var view = new ManifestView(_store);
        await using var committer = new ManifestCommitter(_store, view, TimeProvider.System, TimeSpan.Zero);
        using (var encoded = ManifestFormat.Encode(Manifest(3, inputs)))
        {
            _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await _store.ReplaceAsync(ObjectName.Manifest, null, encoded.Content, encoded.Crc32C, Token)).Value);
        }

        var manifest = (await view.ReadAsync(Token)).Value;
        var first = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));
        var second = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));
        var outcomes = await Task.WhenAll(committer.CommitAsync(first, Token), committer.CommitAsync(second, Token));

        Assert.Empty(first.Outputs.Select(static output => output.Object).Intersect(second.Outputs.Select(static output => output.Object)));
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(outcomes[0].Value);
        _ = Assert.IsType<ManifestCommitOutcome.Superseded>(outcomes[1].Value);
        Assert.Equal(first.Outputs, (await view.ReadAsync(Token)).Value.Sections);
    }

    /// <summary>
    /// Sweeps race two compactions of one partition (LSM-I6): before either commits, a sweep keeps both compactions'
    /// outputs pending, since every input is listed; once one commits and the other is superseded, a sweep marks the
    /// inputs the first removed and the second's outputs, one of whose inputs is no longer listed, and keeps the first's
    /// outputs, and the next sweep deletes what it marked.
    /// </summary>
    [Fact]
    public async Task SweepsAroundRacingCompactionsDeleteTheRemovedInputsAndTheLosersOutputs()
    {
        var inputs = await ThreeInputsAsync();
        var view = new ManifestView(_store);
        await using var committer = new ManifestCommitter(_store, view, TimeProvider.System, TimeSpan.Zero);
        using (var encoded = ManifestFormat.Encode(Manifest(3, inputs)))
        {
            _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await _store.ReplaceAsync(ObjectName.Manifest, null, encoded.Content, encoded.Crc32C, Token)).Value);
        }

        var sweeper = new Sweeper(_store, view, TimeProvider.System, NullLogger<Sweeper>.Instance);
        var manifest = (await view.ReadAsync(Token)).Value;
        var first = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));
        var second = Compacted(await Worker(_store, PushAll(manifest, KeyRange.Root)).CompactAsync(KeyRange.Root, manifest, Map(3), Token));
        var applied = first.Outputs.Select(static output => output.Object).Distinct().Count();
        var superseded = second.Outputs.Select(static output => output.Object).Distinct().Count();

        Assert.Equal(new SweepResult(Referenced: inputs.Length, Pending: applied + superseded, Marked: 0, Deleted: 0, Unreadable: 0), await sweeper.SweepAsync(Token));
        _ = await Task.WhenAll(committer.CommitAsync(first, Token), committer.CommitAsync(second, Token));
        Assert.Equal(new SweepResult(Referenced: applied, Pending: 0, Marked: inputs.Length + superseded, Deleted: 0, Unreadable: 0), await sweeper.SweepAsync(Token));
        Assert.Equal(new SweepResult(Referenced: applied, Pending: 0, Marked: 0, Deleted: inputs.Length + superseded, Unreadable: 0), await sweeper.SweepAsync(Token));

        var listed = await _store.ListAsync(string.Empty, cancellationToken: Token).Select(static listed => listed.Name).Where(static name => name != ObjectName.Manifest).ToListAsync(Token);
        Assert.Equal(first.Outputs.Select(static output => output.Object).Distinct().Order(), listed.Order());
    }

    /// <summary>
    /// A failed input read, a failed upload of the second output object, and a cancellation partway through each propagate,
    /// return no change, release every row, and leave the budget whole.
    /// </summary>
    [Theory]
    [InlineData("input read")]
    [InlineData("second upload")]
    [InlineData("cancellation")]
    public async Task AFailureReleasesEveryRowAndTheBudget(string failure)
    {
        var rows = Enumerable.Range(0, 24).Select(index => Row((byte)(index * 10), T(1, index + 1), RowKind.Append, new string('p', 60))).ToArray();
        var inputs = new[] { await PutAsync(_store, KeyRange.Root, rows[..8]), await PutAsync(_store, KeyRange.Root, rows[8..16]), await PutAsync(_store, KeyRange.Root, rows[16..]) };
        var worker = Worker(_store, PushAll(Manifest(3, inputs), KeyRange.Root), maxObjectLength: 1000);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var creates = Creates();
        switch (failure)
        {
            case "input read":
                _store.FailRangedReads(1);
                break;
            case "second upload":
                _store.BeforeCreate = earlier => _ = earlier == creates + 1 ? throw new IOException("A scripted create failure.") : 0;
                break;
            case "cancellation":
                _store.BeforeCreate = earlier => cancellation.Cancel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        var thrown = await Record.ExceptionAsync(() => worker.CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), cancellation.Token));

        Assert.IsAssignableFrom(failure == "cancellation" ? typeof(OperationCanceledException) : typeof(IOException), thrown);
        Assert.Equal(new ArchiveOptions().CompactionBudgetBytes, worker.AvailableBudgetBytes);
    }

    /// <summary>
    /// A push which fails partway, at its fifth child, by a failed upload, a failed read of an input's blocks, an input's
    /// object gone since the manifest value was read, or a corrupt block of an input, leaks no page, leaves the budget
    /// whole, and returns no change: the failure propagates, or, for the gone input, a manifest read afresh which lists no
    /// section answers <see cref="CompactionOutcome.NothingToCompact"/>. The four children's objects uploaded before it are
    /// the garbage left: one
    /// section each, named after its child, whose trailer records exactly the inputs, by which the sweeper reclaims them.
    /// </summary>
    [Theory]
    [InlineData(PartwayFailure.Upload)]
    [InlineData(PartwayFailure.BlockRead)]
    [InlineData(PartwayFailure.InputGone)]
    [InlineData(PartwayFailure.CorruptBlock)]
    public async Task APushWhichFailsPartwayLeavesOnlyItsEarlierChildrenAsGarbage(PartwayFailure failure)
    {
        const int ChildrenUploadedBeforeFailure = 4;
        var (options, _, inputs) = await SixteenChildInputsAsync();
        var manifest = Manifest(3, inputs);
        if (failure == PartwayFailure.CorruptBlock)
        {
            // Child c's rows lie in block c, which no earlier child's read takes.
            var block = (await MetadataAsync(inputs))[^1].Index.Blocks[ChildrenUploadedBeforeFailure];
            await FlipByteAsync(inputs[^1], block.Offset);
        }

        var worker = Worker(_store, options);
        var creates = Creates();

        // A failed upload fails the fifth child's own create; the read failures are armed at the fourth child's, so the
        // fifth child's first read meets them.
        _store.BeforeCreateAsync = async earlier =>
        {
            switch (failure)
            {
                case PartwayFailure.Upload when earlier == creates + ChildrenUploadedBeforeFailure:
                    throw new IOException("A scripted create failure.");
                case PartwayFailure.BlockRead when earlier == creates + ChildrenUploadedBeforeFailure - 1:
                    _store.FailRangedReads(1);
                    break;
                case PartwayFailure.InputGone when earlier == creates + ChildrenUploadedBeforeFailure - 1:
                    await _objects.DeleteAsync(inputs[^1].Object, Token);
                    break;
                case PartwayFailure.Upload or PartwayFailure.BlockRead or PartwayFailure.InputGone or PartwayFailure.CorruptBlock:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }
        };

        CompactionOutcome? outcome = null;
        var thrown = await Record.ExceptionAsync(async () => outcome = await worker.CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        switch (failure)
        {
            case PartwayFailure.InputGone:
                Assert.Null(thrown);
                Assert.Equal(CompactionOutcome.NothingToCompact.Instance, outcome?.Value);
                break;
            case PartwayFailure.CorruptBlock:
                Assert.Null(outcome);
                Assert.Equal(RunObjectFormat.CrcMismatch, DecodeRejection.ReasonOf(Assert.IsType<ArchiveCorruptException>(thrown).InnerException!));
                break;
            case PartwayFailure.Upload or PartwayFailure.BlockRead:
                Assert.Null(outcome);
                _ = Assert.IsType<IOException>(thrown);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Assert.Equal(options.CompactionBudgetBytes, worker.AvailableBudgetBytes);
        var inputNames = inputs.Select(static input => input.Object).ToHashSet();
        var left = await _objects.ListAsync("c/", cancellationToken: Token).Where(listed => !inputNames.Contains(listed.Name)).OrderBy(static listed => listed.Name.Value, StringComparer.Ordinal).ToListAsync(Token);
        Assert.Equal(["c/4-00/", "c/4-10/", "c/4-20/", "c/4-30/"], left.Select(static listed => listed.Name.Value[..7]));
        var condition = new RunObjectCondition.Compaction([.. inputs.Select(static input => new SectionPosition(input.Object, input.Offset)).Order()]);
        foreach (var listed in left)
        {
            var trailer = Assert.IsType<RunTrailer>(await RunObjectReader.ReadTrailerAsync(_store, listed, Token));
            _ = Assert.Single(trailer.Sections);
            Assert.Equal(condition, trailer.Condition);
        }
    }

    /// <summary>
    /// An input whose trailer entry gives another row count or tag span than its rows is corrupt. A push reads it one
    /// child at a time, so only its check of each input's totals over every child's reads finds it: one row more or
    /// fewer, a highest tag raised, or a lowest tag lowered, each with a tail which verifies. A merge in place reads it
    /// whole, so the decoder's whole-section read finds it.
    /// </summary>
    [Theory]
    [InlineData(CompactionKind.Push, EntryBreak.MoreRows)]
    [InlineData(CompactionKind.Push, EntryBreak.FewerRows)]
    [InlineData(CompactionKind.Push, EntryBreak.HigherMaxTag)]
    [InlineData(CompactionKind.Push, EntryBreak.LowerMinTag)]
    [InlineData(CompactionKind.MergeInPlace, EntryBreak.MoreRows)]
    [InlineData(CompactionKind.MergeInPlace, EntryBreak.HigherMaxTag)]
    public async Task AnInputWhoseTotalsDisagreeWithItsEntryIsCorrupt(CompactionKind kind, EntryBreak broken)
    {
        var inputs = new[]
        {
            await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 2), RowKind.Append, "a"), Row(0x90, T(1, 3), RowKind.Append, "b")),
            await PutAsync(_store, KeyRange.Root, Row(0x20, T(1, 4), RowKind.Append, "c")),
            await PutAsync(_store, KeyRange.Root, Row(0xa0, T(1, 5), RowKind.Append, "d")),
        };
        inputs[0] = await PatchEntryAsync(inputs[0], broken);
        _ = Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_store, inputs[0], Token));
        var manifest = Manifest(3, inputs);
        var options = kind == CompactionKind.Push ? PushAll(manifest, KeyRange.Root) : Pushing with { SectionBytes = inputs.Sum(static input => input.Length) };

        var thrown = await Assert.ThrowsAsync<ArchiveCorruptException>(() => Worker(_store, options).CompactAsync(KeyRange.Root, manifest, Map(3), Token));

        var reason = broken is EntryBreak.MoreRows or EntryBreak.FewerRows ? RunObjectFormat.RowCountMismatch : RunObjectFormat.TagSpanMismatch;
        Assert.Equal(reason, DecodeRejection.ReasonOf(thrown.InnerException!));
    }

    /// <summary>
    /// archive §8.2: a block which fails its CRC-32C on one read and verifies on the read past the caches which confirms
    /// it was a fault of the read: the compaction fails with an <see cref="IOException"/>, its caller compacts again later,
    /// and no corruption counts; corruption which a fresh read finds again counts once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyCorruptionAFreshReadFindsAgainIsCorruption(bool persists)
    {
        const int Child = 4;
        var (options, _, inputs) = await SixteenChildInputsAsync();
        var block = (await MetadataAsync(inputs))[^1].Index.Blocks[Child];
        await FlipByteAsync(inputs[^1], block.Offset);
        var tailReads = 0;
        _store.BeforeRangedRead = async (name, offset) =>
        {
            // The second read of the input's tail is the confirming read's, which finds the block restored unless it persists.
            if (name == inputs[^1].Object && offset == inputs[^1].Tail.Offset && ++tailReads == 2 && !persists)
            {
                await FlipByteAsync(inputs[^1], block.Offset);
            }
        };
        using var counted = new CorruptCount();

        var thrown = await Record.ExceptionAsync(() => Worker(_store, options).CompactAsync(KeyRange.Root, Manifest(3, inputs), Map(3), Token));

        Assert.Equal(2, tailReads);
        var corruption = persists ? Assert.IsType<ArchiveCorruptException>(thrown) : Assert.IsType<ArchiveCorruptException>(Assert.IsType<IOException>(thrown).InnerException);
        Assert.Equal(RunObjectFormat.CrcMismatch, DecodeRejection.ReasonOf(corruption.InnerException!));
        Assert.Equal(persists ? 1 : 0, counted.Total);
    }

    /// <summary>The worker refuses invalid options and an object bound outside 1 to the longest run object.</summary>
    [Fact]
    public void TheWorkerRefusesInvalidOptions()
    {
        var tails = new TailCache(_store, Pushing);
        var view = new ManifestView(_store);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CompactionWorker(_store, tails, view, Pushing with { MaxSectionsPerPartition = 1 }));
        _ = Assert.Throws<ArgumentNullException>(() => new CompactionWorker(null!, tails, view, Pushing));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CompactionWorker(_store, tails, view, Pushing, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CompactionWorker(_store, tails, view, Pushing, RunObjectFormat.MaxObjectLength + 1L));
        _ = Assert.Throws<ArgumentNullException>(() => new CompactionWorker(_store, null!, view, Pushing));
        _ = Assert.Throws<ArgumentNullException>(() => new CompactionWorker(_store, tails, null!, Pushing));
    }
    /// <summary>
    /// Returns options under which <paramref name="partition"/>'s sections in <paramref name="manifest"/> push whole: an
    /// <c>s</c> which their total reaches <c>F</c> times and which caps them at no less than their total.
    /// </summary>
    private static ArchiveOptions PushAll(ManifestValue manifest, KeyRange partition)
    {
        var total = manifest.SectionsOf(partition).Sum(static section => section.Length);
        return Pushing with { SectionBytes = (total + 3) / 4 };
    }

    private static void AssertRows(IReadOnlyList<RunRowData> expected, IReadOnlyList<RunRowData> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.True(expected[index].SameAs(actual[index]), $"Row {index} differs.");
        }
    }

    /// <summary>Asserts that the sections' rows hold <paramref name="deleted"/> as its key's last row.</summary>
    private static void AssertKept(RunRowData deleted, Dictionary<string, List<RunRowData>> rows) =>
        Assert.True(rows[Convert.ToHexString(deleted.Key)][^1].SameAs(deleted), "The empty Delete stays its key's last row.");

    /// <summary>Returns sections of <paramref name="partition"/> of the given lengths, in objects whose names follow <paramref name="prefix"/>.</summary>
    private static ImmutableArray<SectionRef> Sections(KeyRange partition, string prefix, params long[] lengths) =>
        [.. lengths.Select((length, index) => new SectionRef(new ObjectName($"d/1/0/{prefix}{index}"), 0, length, partition, T(1, 1), T(1, 1), new ObjectRange(length, 1)))];

    private static ImmutableArray<SectionRef> Sections(KeyRange partition, params long[] lengths) => Sections(partition, "s", lengths);

    /// <summary>Returns three sections of <paramref name="partition"/>: an older row of another key, <paramref name="deleted"/>, and a newer row.</summary>
    private async Task<SectionRef[]> DeleteInputsAsync(KeyRange partition, RunRowData deleted) =>
    [
        await PutAsync(_store, partition, Row(0x10, T(1, 3), RowKind.Append, "a1") with { Key = [(byte)(deleted.Key[0] ^ 0x01)] }),
        await PutAsync(_store, partition, deleted),
        await PutAsync(_store, partition, Row(0x20, T(1, 6), RowKind.Append, "b") with { Tag = new Tag(deleted.Tag.Ledger, deleted.Tag.Slot + 1, 0) }),
    ];

    private async Task<SectionRef[]> ThreeInputsAsync() =>
    [
        await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 1), RowKind.Append, "a")),
        await PutAsync(_store, KeyRange.Root, Row(0x90, T(1, 2), RowKind.Append, "b")),
        await PutAsync(_store, KeyRange.Root, Row(0x10, T(1, 3), RowKind.Append, "c")),
    ];

    private int Creates() => _store.Calls.Count(static call => call.StartsWith("Create(", StringComparison.Ordinal));

    /// <summary>Puts <paramref name="manifest"/> in the store as its manifest, in place of any it holds.</summary>
    private async Task StoreManifestAsync(ManifestValue manifest)
    {
        await _objects.DeleteAsync(ObjectName.Manifest, Token);
        using var encoded = ManifestFormat.Encode(manifest);
        _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await _objects.ReplaceAsync(ObjectName.Manifest, null, encoded.Content, encoded.Crc32C, Token)).Value);
    }

    /// <summary>
    /// Writes <c>k + 1</c> sections of the whole keyspace at the default geometry, each holding one row in each child, a
    /// block per row; returns the options under which they push whole, their rows, and the sections.
    /// </summary>
    private async Task<(ArchiveOptions Options, RunRowData[][] Rows, SectionRef[] Inputs)> SixteenChildInputsAsync()
    {
        var options = new ArchiveOptions();
        var fanOut = 1 << options.GridBits;
        var rows = Enumerable.Range(0, options.MaxSectionsPerPartition + 1)
            .Select(section => Enumerable.Range(0, fanOut).Select(child => Row((byte)(child << (8 - options.GridBits) | section % fanOut), T(1, section * fanOut + child + 1), RowKind.Append, new string('p', 40))).ToArray())
            .ToArray();
        var inputs = new SectionRef[rows.Length];
        for (var section = 0; section < rows.Length; section++)
        {
            inputs[section] = await PutAsync(_store, options.SectionSettings with { BlockBytes = 64 }, KeyRange.Root, rows[section]);
        }

        return (options with { SectionBytes = inputs.Sum(static section => section.Length) / fanOut }, rows, inputs);
    }

    /// <summary>Returns each section's metadata, read from its object's tail.</summary>
    private async Task<RunSectionMetadata[]> MetadataAsync(IEnumerable<SectionRef> sections)
    {
        var metadata = new List<RunSectionMetadata>();
        foreach (var section in sections)
        {
            metadata.Add(RunObjectReader.SectionOf(Assert.IsType<RunTail>(await RunObjectReader.ReadTailAsync(_objects, section, Token)), section));
        }

        return [.. metadata];
    }

    /// <summary>
    /// Rewrites the trailer entry of <paramref name="section"/>, alone in its object, to break it as
    /// <paramref name="broken"/> says, with the trailer's CRC-32C recomputed, and returns the reference which names its
    /// patched tags. The entry is found by its encoded tags and row count (archive §7), and the trailer by the length it
    /// ends with.
    /// </summary>
    private async Task<SectionRef> PatchEntryAsync(SectionRef section, EntryBreak broken)
    {
        byte[] bytes;
        using (var content = Assert.IsType<ArcBuffer>(await _objects.ReadAsync(section.Object, 0, (int)(section.Tail.Offset + section.Tail.Length), Token)))
        {
            bytes = content.ToArray();
        }

        var rowCount = (await MetadataAsync([section]))[0].Entry.RowCount;
        var (minTag, maxTag) = (section.MinTag, section.MaxTag);
        var trailer = bytes.Length - (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        var fields = Encoded(minTag, maxTag, rowCount);
        var at = trailer + bytes.AsSpan(trailer).IndexOf(fields);
        Assert.True(at >= trailer && bytes.AsSpan(at + 1).IndexOf(fields) < 0, "The trailer holds the entry's tags and row count once.");
        (minTag, maxTag, rowCount) = broken switch
        {
            EntryBreak.MoreRows => (minTag, maxTag, rowCount + 1),
            EntryBreak.FewerRows => (minTag, maxTag, rowCount - 1),
            EntryBreak.HigherMaxTag => (minTag, new Tag(maxTag.Ledger, new Slot(maxTag.Slot.Value + 1), maxTag.Index), rowCount),
            EntryBreak.LowerMinTag => (new Tag(minTag.Ledger, new Slot(minTag.Slot.Value - 1), minTag.Index), maxTag, rowCount),
            _ => throw new ArgumentOutOfRangeException(nameof(broken)),
        };
        Encoded(minTag, maxTag, rowCount).CopyTo(bytes.AsSpan(at));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(bytes, trailer, bytes.Length - 4 - trailer)));
        await _objects.DeleteAsync(section.Object, Token);
        using var patched = ArcBuffer.Wrap(bytes);
        _ = await _objects.CreateAsync(section.Object, patched, null, Token);
        return new SectionRef(section.Object, section.Offset, section.Length, section.Partition, minTag, maxTag, section.Tail);

        // A tag is its ledger u64, slot u64, and index u32, and a row count a u64, each little-endian (archive §7).
        static byte[] Encoded(Tag min, Tag max, ulong count)
        {
            var encoded = new byte[48];
            foreach (var (tag, offset) in new[] { (min, 0), (max, 20) })
            {
                BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(offset), tag.Ledger.Value);
                BinaryPrimitives.WriteInt64LittleEndian(encoded.AsSpan(offset + 8), tag.Slot.Value);
                BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(offset + 16), tag.Index);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(40), count);
            return encoded;
        }
    }

    /// <summary>Flips a bit of the byte at <paramref name="offset"/> of <paramref name="section"/>'s object.</summary>
    private async Task FlipByteAsync(SectionRef section, long offset)
    {
        byte[] bytes;
        using (var content = Assert.IsType<ArcBuffer>(await _objects.ReadAsync(section.Object, 0, (int)(section.Tail.Offset + section.Tail.Length), Token)))
        {
            bytes = content.ToArray();
        }

        bytes[offset] ^= 1;
        await _objects.DeleteAsync(section.Object, Token);
        using var flipped = ArcBuffer.Wrap(bytes);
        _ = await _objects.CreateAsync(section.Object, flipped, null, Token);
    }
    /// <summary>Flips the last byte of <paramref name="section"/>'s object, its trailer's CRC-32C.</summary>
    private async Task CorruptTrailerAsync(SectionRef section)
    {
        byte[] bytes;
        using (var content = Assert.IsType<ArcBuffer>(await _objects.ReadAsync(section.Object, 0, (int)(section.Tail.Offset + section.Tail.Length), Token)))
        {
            bytes = content.ToArray();
        }

        bytes[^1] ^= 1;
        await _objects.DeleteAsync(section.Object, Token);
        using var corrupt = ArcBuffer.Wrap(bytes);
        _ = await _objects.CreateAsync(section.Object, corrupt, null, Token);
    }

    /// <summary>
    /// Counts <c>orleans.durable.storage.archive.lsm.compaction.corrupt</c>'s measurements made while it is current, so compactions of tests
    /// running alongside leave it unchanged.
    /// </summary>
    private sealed class CorruptCount : IDisposable
    {
        private static readonly AsyncLocal<CorruptCount?> Current = new();
        private readonly MeterListener _listener;
        private long _total;

        public CorruptCount()
        {
            Current.Value = this;
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == ArchiveMetrics.MeterName && instrument.Name == "orleans.durable.storage.archive.lsm.compaction.corrupt")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                if (Current.Value == this)
                {
                    _ = Interlocked.Add(ref _total, value);
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }
}
