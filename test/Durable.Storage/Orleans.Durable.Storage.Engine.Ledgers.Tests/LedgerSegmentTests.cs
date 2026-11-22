using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using CsCheck;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.BatchSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The segment decoder, <see cref="LedgerSegmentView"/> (ledgers.md §2.2, §4, §7; LG-I12, LG-I13): segments a writer's
/// batches form resolve as <see cref="LedgerSegmentRules"/>, an independent reading of the batch and segment rules, gives
/// them, from their start, after a rewind from any slot, from any division into pages, and for one key alone; every
/// mutated segment is rejected for the rule that reading names, batch rules before segment rules; mutants of the version
/// rule fail; and examples pin ordinals reset across a start flag, counts at the <c>u64</c> maximum, and an empty
/// <c>Delete</c> followed by an <c>Append</c>. Each case runs in its own page leak scope (verification.md §11.1).
/// </summary>
public sealed class LedgerSegmentTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>How a mutant of the version rule (LG-I13) counts a key's rows.</summary>
    private enum VersionMutant
    {
        /// <summary>The count of a key's rows restarts at each batch, from its defining row's stored count.</summary>
        CountPerBatch,

        /// <summary>A referencing row's count restarts from the defining row's stored count.</summary>
        ResetAtReference,
    }

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A writer's segments resolve as the rules give them; each batch re-encodes to its bytes; and a rewind from any
    /// slot, by that batch's segment offset, and any division into pages, resolve the same rows from that slot on, and a
    /// key-filtered view the same rows of its key, in constant memory (a metamorphic property).
    /// </summary>
    [Fact]
    public void SegmentsResolveAsTheRulesGiveThemFromEverySlotAndPageSplit() =>
        Gen.Select(Segments.Where(Writable), PageLengths, Gen.Int.NonNegative).Sample((segment, pageLengths, pick) => _leaks.InScope(() =>
        {
            var batches = EncodeSegment(segment, firstSlot: 3);
            var reading = LedgerSegmentRules.ReadSegment(batches, firstSlot: 3);
            Assert.Null(reading.Reason);
            var expected = reading.Rows.Select(ResolvedSample.Of).ToList();
            var whole = DecodeSegment(batches, firstSlot: 3);
            Assert.Null(whole.Reason);
            AssertResolved(expected, whole.Rows);
            AssertResolved(expected, DecodeSegment(batches, firstSlot: 3, pageLengths).Rows);
            Assert.Equal(batches, LedgerBatches.EncodeSegment([.. batches.Select(static batch => LedgerSegmentRules.ReadBatch(batch).Batch!)]));

            var from = pick % batches.Count;
            var rewind = (int)LedgerSegmentRules.ReadBatch(batches[from]).Batch!.Offset;
            var rewound = DecodeSegment(batches, firstSlot: 3, pageLengths, from: from - rewind);
            AssertResolved(expected.Where(row => row.Slot >= 3 + from), rewound.Rows.Where(row => row.Slot >= 3 + from));

            if (expected.Count > 0)
            {
                var key = expected[pick % expected.Count].Key;
                var filtered = DecodeSegment(batches, firstSlot: 3, pageLengths, key);
                Assert.Null(filtered.Reason);
                AssertResolved(expected.Where(row => row.Key.AsSpan().SequenceEqual(key)), filtered.Rows);
            }
        }));

    /// <summary>
    /// Every mutated segment (a batch's start flag, offset, ordinal base, a defining row's key or count, or a referencing
    /// row's ordinal set to another value, with its CRC-32C recomputed, or batches dropped or swapped) is rejected at the
    /// batch and for the reason the rules name, or resolved as they give it; <c>classify</c> reports each outcome, and
    /// every segment rule's reason arises.
    /// </summary>
    [Fact]
    public void EveryMutatedSegmentIsJudgedAsTheRulesJudgeIt()
    {
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Gen.Select(Segments.Where(Writable), Gen.Int.NonNegative, Gen.Int.NonNegative, Gen.Int[0, 7], Gen.Int[-1, 7]).Sample(
            sample =>
            {
                var (segment, pick, value, kind, second) = sample;
                var outcome = "accepted";
                _leaks.InScope(() =>
                {
                    // A second mutation, where one is drawn, breaks two rules at once, so their order is judged too.
                    var batches = Mutate(EncodeSegment(segment), pick, value, kind);
                    if (second >= 0 && batches.TrueForAll(static batch => LedgerSegmentRules.ReadBatch(batch).Batch is not null))
                    {
                        batches = Mutate(batches, pick / 3, value / 5, second);
                    }
                    var reading = LedgerSegmentRules.ReadSegment(batches, firstSlot: 0);
                    var decoded = DecodeSegment(batches, firstSlot: 0, pageLengths: [5]);
                    Assert.Equal((reading.FailedBatch, reading.Reason), (decoded.FailedBatch, decoded.Reason));
                    if (reading.Reason is null)
                    {
                        AssertResolved(reading.Rows.Select(ResolvedSample.Of), decoded.Rows);
                    }

                    // A key-filtered view checks the rules for its key alone, as the filtered reading does.
                    PlannedRow[] planned = [.. segment.SelectMany(static batch => batch.Rows)];
                    if (planned.Length > 0)
                    {
                        var key = planned[value % planned.Length].Key;
                        var filteredReading = LedgerSegmentRules.ReadSegment(batches, firstSlot: 0, key);
                        var filtered = DecodeSegment(batches, firstSlot: 0, pageLengths: [5], key);
                        Assert.Equal((filteredReading.FailedBatch, filteredReading.Reason), (filtered.FailedBatch, filtered.Reason));
                        if (filteredReading.Reason is null)
                        {
                            AssertResolved(filteredReading.Rows.Select(ResolvedSample.Of), filtered.Rows);
                        }
                    }

                    outcome = reading.Reason ?? "accepted";
                });
                _ = outcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
                return outcome;
            },
            TestDiagnostics.WriteLine,
            iter: 3_000);

        foreach (var reason in SpecReasons("**Segment rules.**").Append("accepted"))
        {
            Assert.Contains(reason, outcomes.Keys);
        }
    }

    /// <summary>
    /// The mutants of the version rule (LG-I13), one which counts a key's rows per batch rather than per segment and one
    /// which resets the count at a referencing row, resolve the segment vectors otherwise than they record, and otherwise
    /// than the decoder does on a generated segment whose key recurs across batches.
    /// </summary>
    [Fact]
    public void TheVersionRulesMutantsFail()
    {
        foreach (var mutant in Enum.GetValues<VersionMutant>())
        {
            var caught = SharedVectors.Cases("ledgers", "segments.json").Any(vector =>
            {
                var batches = vector.GetProperty("batches").EnumerateArray().Select(static batch => Convert.FromHexString(batch.GetString()!)).ToList();
                var recorded = vector.GetProperty("rows").EnumerateArray().Select(static row => ulong.Parse(SharedVectors.Text(row, "versionCount"), System.Globalization.CultureInfo.InvariantCulture));
                return !Resolve(batches, mutant).SequenceEqual(recorded);
            });
            Assert.True(caught, $"The segment vectors do not catch the {mutant} mutant.");

            PlannedBatch[] recurring =
            [
                new(Start: true, [new(RowKind.Append, [1], [], [], 5, [])]),
                new(Start: false, [new(RowKind.Append, [1], [], [], 0, []), new(RowKind.Append, [1], [], [], 0, [])]),
            ];
            var encoded = EncodeSegment(recurring);
            var decoded = DecodeSegment(encoded).Rows.Select(static row => row.VersionCount);
            Assert.Equal([6UL, 7, 8], decoded);
            Assert.NotEqual(decoded, Resolve(encoded, mutant));
        }
    }

    /// <summary>
    /// A start flag resets the ordinals and the key table: the next segment defines its keys again from ordinal 0, with
    /// the versions and tokens its defining rows store.
    /// </summary>
    [Fact]
    public void AStartFlagResetsTheOrdinals()
    {
        PlannedBatch[] segment =
        [
            new(Start: true, [new(RowKind.Append, [1], [10], [7], 3, [8]), new(RowKind.Append, [2], [20], [], 0, [])]),
            new(Start: true, []),
            new(Start: false, [new(RowKind.Append, [2], [21], [9], 40, [6]), new(RowKind.Replace, [2], [22], [], 0, [])]),
        ];
        var batches = EncodeSegment(segment);
        var third = LedgerSegmentRules.ReadBatch(batches[2]).Batch!;
        Assert.Equal((1UL, 0UL, 0UL), (third.Offset, third.OrdinalBase, third.Rows[0].Ordinal));
        var rows = DecodeSegment(batches).Rows;
        Assert.Equal(
            ["0/0 (07, 4) 08", "0/1 (, 1) ", "2/0 (09, 41) 06", "2/1 (09, 42) 06"],
            rows.Select(static row => $"{row.Slot}/{row.Index} ({Convert.ToHexStringLower(row.VersionBase)}, {row.VersionCount}) {Convert.ToHexStringLower(row.Token)}"));
    }

    /// <summary>
    /// An empty <c>Delete</c> followed by an <c>Append</c> of its key: the count keeps advancing across both, whatever their
    /// kinds.
    /// </summary>
    [Fact]
    public void AnEmptyDeleteAndThenAnAppendKeepTheCountAdvancing()
    {
        PlannedBatch[] segment =
        [
            new(Start: true, [new(RowKind.Delete, [1], [], [], 11, [])]),
            new(Start: false, [new(RowKind.Append, [1], [5], [], 0, [])]),
        ];
        Assert.Equal([12UL, 13], DecodeSegment(EncodeSegment(segment)).Rows.Select(static row => row.VersionCount));
    }

    /// <summary>
    /// A count at the <c>u64</c> maximum: its last row reaches it, a defining row which stores it breaks
    /// <c>count-overflow</c>, and so does a reference which would pass it.
    /// </summary>
    [Fact]
    public void ACountReachesTheMaximumAndNoFurther()
    {
        PlannedBatch[] reaches =
        [
            new(Start: true, [new(RowKind.Append, [1], [], [], ulong.MaxValue - 2, [])]),
            new(Start: false, [new(RowKind.Append, [1], [], [], 0, [])]),
        ];
        Assert.Equal([ulong.MaxValue - 1, ulong.MaxValue], DecodeSegment(EncodeSegment(reaches)).Rows.Select(static row => row.VersionCount));

        var stored = LedgerSegmentRules.Encode(new RuleBatch(IsStart: true, 0, 0, [new RuleRow(0, IsDefining: true, 0, [1], [], ulong.MaxValue, [], [])]));
        Assert.Equal((0, "count-overflow"), (DecodeSegment([stored]).FailedBatch, DecodeSegment([stored]).Reason));
        var passes = LedgerSegmentRules.Encode(new RuleBatch(IsStart: false, 1, 1, [new RuleRow(0, IsDefining: false, 0, null, null, 0, null, [])]));
        var defining = LedgerSegmentRules.Encode(new RuleBatch(IsStart: true, 0, 0, [new RuleRow(0, IsDefining: true, 0, [1], [], ulong.MaxValue - 1, [], [])]));
        var rejected = DecodeSegment([defining, passes]);
        Assert.Equal((1, "count-overflow"), (rejected.FailedBatch, rejected.Reason));
    }

    /// <summary>
    /// The first batch decoded must start a segment, and a view which rejected a batch refuses every later one, since its
    /// segment cannot be resolved past it.
    /// </summary>
    [Fact]
    public void AViewWhichRejectedABatchIsUnusable()
    {
        var continuation = LedgerSegmentRules.Encode(new RuleBatch(IsStart: false, 1, 0, [new RuleRow(0, IsDefining: true, 0, [1], [], 0, [], [])]));
        using var buffer = PagedBuffers.Create(continuation, []);
        Assert.Null(BatchView.Decode(buffer, out var view));
        using var segment = new LedgerSegmentView(new LedgerId(1));
        Assert.Equal("segment-offset-mismatch", Reason(segment.Advance(new Slot(1), view)));
        var empty = 0;
        foreach (var _ in segment.Rows)
        {
            empty++;
        }

        Assert.Equal(0, empty);
        _ = Assert.Throws<InvalidOperationException>(() => segment.Advance(new Slot(1), BatchView.OfVerified(buffer)));
        segment.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => segment.Advance(new Slot(1), BatchView.OfVerified(buffer)));
    }

    /// <summary>
    /// ledgers.md §4: a view takes the slot after the last it advanced past, or a segment's start: a skipped batch which
    /// only references keys, or one advanced past twice, would pass the offset and ordinal-base checks and resolve the later
    /// rows of its keys to wrong versions, so it is refused with <see cref="ArgumentException"/> and the view is unchanged.
    /// </summary>
    [Fact]
    public void AViewRefusesASkippedOrRepeatedBatch()
    {
        PlannedBatch[] segment =
        [
            new(Start: true, [new(RowKind.Append, [1], [], [], 0, []), new(RowKind.Append, [2], [], [], 0, [])]),
            new(Start: false, [new(RowKind.Append, [1], [], [], 0, [])]),
            new(Start: false, [new(RowKind.Append, [1], [], [], 0, [])]),
        ];
        var batches = EncodeSegment(segment);
        using var view = new LedgerSegmentView(new LedgerId(1));
        using var first = PagedBuffers.Create(batches[0], []);
        using var second = PagedBuffers.Create(batches[1], []);
        using var third = PagedBuffers.Create(batches[2], []);
        Assert.Null(view.Advance(Slot.Zero, BatchView.OfVerified(first)));

        _ = Assert.Throws<ArgumentException>(() => view.Advance(new Slot(2), BatchView.OfVerified(third)));
        Assert.Null(view.Advance(new Slot(1), BatchView.OfVerified(second)));
        _ = Assert.Throws<ArgumentException>(() => view.Advance(new Slot(1), BatchView.OfVerified(second)));
        Assert.Null(view.Advance(new Slot(2), BatchView.OfVerified(third)));
        var versions = new List<ulong>();
        foreach (var row in view.Rows)
        {
            versions.Add(row.Version.Count);
        }

        Assert.Equal([3UL], versions);
    }

    /// <summary>A view's key table holds each key once, from its defining row, and resets at each start flag.</summary>
    [Fact]
    public void AViewsKeyTableHoldsTheKeysItsSegmentDefined()
    {
        PlannedBatch[] segment =
        [
            new(Start: true, [new(RowKind.Append, [1], [], [], 0, []), new(RowKind.Append, [2], [], [], 0, []), new(RowKind.Append, [1], [], [], 0, [])]),
            new(Start: false, [new(RowKind.Append, [3], [], [], 0, [])]),
            new(Start: true, [new(RowKind.Append, [3], [], [], 0, [])]),
        ];
        var batches = EncodeSegment(segment);
        using var view = new LedgerSegmentView(new LedgerId(1));
        ulong[] defined = [2, 3, 1];
        for (var index = 0; index < batches.Count; index++)
        {
            using var buffer = PagedBuffers.Create(batches[index], []);
            Assert.Null(BatchView.Decode(buffer, out var batch));
            Assert.Null(view.Advance(new Slot(index), batch));
            Assert.Equal(defined[index], view.DefinedKeys);
        }

        Assert.Equal(new LedgerSegmentId(new LedgerId(1), new Slot(2)), view.Segment);
    }

    /// <summary>
    /// A view's pooled key table and open-addressing duplicate table grow across resizes, release oversized buffers at
    /// a segment start, and still detect duplicates in both the large segment and the following segment.
    /// </summary>
    [Fact]
    public void AViewsPooledKeyTableGrowsPastKeptCapacityDetectsDuplicatesAndResetsAcrossSegments()
    {
        const int keyCount = 300;
        var keys = Enumerable.Range(0, keyCount)
            .Select(static i => System.Text.Encoding.ASCII.GetBytes($"segment-pooled-key-{i:D13}"))
            .ToArray();

        PlannedBatch[] segment =
        [
            new(Start: true, [.. keys.Select(static key => new PlannedRow(RowKind.Append, key, [], [], 0, [1]))]),
            new(Start: false, [new(RowKind.Append, keys[0], [], [], 0, [2]), new(RowKind.Append, keys[^1], [], [], 0, [3])]),
            new(Start: true, [new(RowKind.Append, keys[0], [], [], 0, [4]), new(RowKind.Append, keys[1], [], [], 0, [5])]),
        ];
        var batches = EncodeSegment(segment);
        using var view = new LedgerSegmentView(new LedgerId(1));

        using (var first = PagedBuffers.Create(batches[0], []))
        {
            Assert.Null(BatchView.Decode(first, out var batch));
            Assert.Null(view.Advance(Slot.Zero, batch));
            Assert.Equal((ulong)keyCount, view.DefinedKeys);
            var index = 0;
            foreach (var row in view.Rows)
            {
                Assert.True(row.Key.SequenceEqual(keys[index]));
                Assert.Equal((ulong)index, row.Ordinal);
                Assert.Equal(1UL, row.Version.Count);
                index++;
            }

            Assert.Equal(keyCount, index);
        }

        // A defining row reusing an already-defined key in the grown table is rejected on a separate view.
        var duplicateFirst = LedgerSegmentRules.Encode(new RuleBatch(
            IsStart: false,
            Offset: 1,
            OrdinalBase: keyCount,
            [new RuleRow(0, IsDefining: true, keyCount, keys[keyCount / 2], [], 0, [], [])]));
        using (var duplicateView = new LedgerSegmentView(new LedgerId(1)))
        using (var first = PagedBuffers.Create(batches[0], []))
        using (var duplicateBuffer = PagedBuffers.Create(duplicateFirst, []))
        {
            Assert.Null(BatchView.Decode(first, out var firstBatch));
            Assert.Null(duplicateView.Advance(Slot.Zero, firstBatch));
            Assert.Null(BatchView.Decode(duplicateBuffer, out var batch));
            var rejection = Assert.IsType<InvalidDataException>(duplicateView.Advance(new Slot(1), batch));
            Assert.Equal(LedgerSegmentView.ReasonDuplicateKey, DecodeRejection.ReasonOf(rejection));
        }

        // Referencing rows in the large segment still resolve against the intact table.
        using (var second = PagedBuffers.Create(batches[1], []))
        {
            Assert.Null(BatchView.Decode(second, out var batch));
            Assert.Null(view.Advance(new Slot(1), batch));
            var resolved = new List<(byte[] Key, ulong Count)>();
            foreach (var row in view.Rows)
            {
                resolved.Add((row.Key.ToArray(), row.Version.Count));
            }

            Assert.Equal(2, resolved.Count);
            Assert.True(resolved[0].Key.SequenceEqual(keys[0]));
            Assert.Equal(2UL, resolved[0].Count);
            Assert.True(resolved[1].Key.SequenceEqual(keys[^1]));
            Assert.Equal(2UL, resolved[1].Count);
        }

        // Starting a new segment releases the oversized pooled buffers and allows reusing keys cleanly.
        using (var third = PagedBuffers.Create(batches[2], []))
        {
            Assert.Null(BatchView.Decode(third, out var batch));
            Assert.Null(view.Advance(new Slot(2), batch));
            Assert.Equal(2UL, view.DefinedKeys);
        }

        // Duplicate detection continues to work after the oversized reset.
        var duplicateSecond = LedgerSegmentRules.Encode(new RuleBatch(
            IsStart: false,
            Offset: 1,
            OrdinalBase: 2,
            [new RuleRow(0, IsDefining: true, 2, keys[1], [], 0, [], [])]));
        using (var duplicateBuffer = PagedBuffers.Create(duplicateSecond, []))
        {
            Assert.Null(BatchView.Decode(duplicateBuffer, out var batch));
            var rejection = Assert.IsType<InvalidDataException>(view.Advance(new Slot(3), batch));
            Assert.Equal(LedgerSegmentView.ReasonDuplicateKey, DecodeRejection.ReasonOf(rejection));
        }
    }

    /// <summary>Resolves the version counts of a segment's rows as a mutant of the version rule does.</summary>
    private static List<ulong> Resolve(IReadOnlyList<byte[]> batches, VersionMutant mutant)
    {
        var counts = new List<ulong>();
        var stored = new List<ulong>();
        var current = new List<ulong>();
        foreach (var bytes in batches)
        {
            var batch = LedgerSegmentRules.ReadBatch(bytes).Batch!;
            if (batch.IsStart)
            {
                stored.Clear();
                current.Clear();
            }

            var seenInBatch = new HashSet<ulong>();
            foreach (var row in batch.Rows)
            {
                var ordinal = (int)row.Ordinal;
                if (row.IsDefining)
                {
                    stored.Add(row.VersionCount);
                    current.Add(row.VersionCount);
                }

                var count = mutant switch
                {
                    VersionMutant.CountPerBatch => seenInBatch.Add(row.Ordinal) ? stored[ordinal] + 1 : current[ordinal] + 1,
                    VersionMutant.ResetAtReference => row.IsDefining ? stored[ordinal] + 1 : stored[ordinal] + 1,
                    _ => throw new UnreachableException(),
                };
                current[ordinal] = count;
                counts.Add(count);
            }
        }

        return counts;
    }

    /// <summary>
    /// Mutates a segment's batch <paramref name="pick"/>: its start flag, offset, or ordinal base set to another value; a
    /// defining row's key set to an earlier defined key or its count to the maximum; a referencing row's ordinal set to
    /// another; or the batch dropped or swapped with the next, its CRC-32C recomputed.
    /// </summary>
    private static List<byte[]> Mutate(List<byte[]> batches, int pick, int value, int kind)
    {
        if (batches.Count == 0)
        {
            return batches;
        }

        var index = pick % batches.Count;
        var batch = LedgerSegmentRules.ReadBatch(batches[index]).Batch!;
        var rows = batch.Rows.ToList();
        RuleBatch mutated;
        switch (kind)
        {
            case 0:
                mutated = batch.IsStart ? batch with { IsStart = false, Offset = (ulong)(1 + value % 3) } : batch with { IsStart = true, Offset = 0, OrdinalBase = 0 };
                if (mutated.Rows.Count == 0 && !mutated.IsStart)
                {
                    return batches;
                }

                break;
            case 1:
                mutated = batch.IsStart ? batch : batch with { Offset = batch.Offset + 1 + (ulong)(value % 3) };
                break;
            case 2:
                mutated = batch.IsStart ? batch : batch with { OrdinalBase = (ulong)(value % 4) };
                if (rows.Any(row => !row.IsDefining && row.Ordinal >= mutated.OrdinalBase + (ulong)rows.Count(static row => row.IsDefining)))
                {
                    return batches;
                }

                break;
            case 3 when rows.Count > 0 && rows[value % rows.Count].IsDefining:
                var redefined = value % rows.Count;
                var earlier = LedgerSegmentRules.ReadSegment(batches[..(index + 1)], firstSlot: 0).Rows.FirstOrDefault();
                rows[redefined] = rows[redefined] with { Key = earlier?.Key ?? [0] };
                mutated = batch with { Rows = rows };
                break;
            case 4 when rows.Count > 0 && rows[value % rows.Count].IsDefining:
                rows[value % rows.Count] = rows[value % rows.Count] with { VersionCount = ulong.MaxValue - (ulong)(value % 2) };
                mutated = batch with { Rows = rows };
                break;
            case 5 when rows.Count > 0 && !rows[value % rows.Count].IsDefining && batch.OrdinalBase > 0:
                rows[value % rows.Count] = rows[value % rows.Count] with { Ordinal = (ulong)(value % (int)Math.Min(batch.OrdinalBase, 8)) };
                mutated = batch with { Rows = rows };
                break;
            case 6:
                return [.. batches.Where((_, at) => at != index)];
            case 7 when index + 1 < batches.Count:
                return [.. batches[..index], batches[index + 1], batches[index], .. batches[(index + 2)..]];
            default:
                return batches;
        }

        return [.. batches[..index], LedgerSegmentRules.Encode(mutated), .. batches[(index + 1)..]];
    }
}
