using System.Collections.Immutable;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// archive.md §10, merge, over every store: over random segments of several keys, some rows in one slot, two segments a
/// ledger, with the store holding the writes of the first few segments, every one, or none, and the read's map value
/// listing every ledger the store does not hold whole, the merge marks held exactly the rows <c>Holds</c> covers, the fold
/// of the prefix and the rows not held equals the fold of the key's decided rows, each command once, and the chain check
/// passes (HI-I1, HI-I4). Mutants which keep a held row, drop a row not held, or judge a row before its write's opener by
/// its own position fail the property with the fold's comparison.
/// </summary>
/// <remarks>
/// Each store's suite derives from it, giving a fresh store per case and its drain of a ledger's segments. A store whose
/// coverage is the read's map value holds whole ledgers, those its map value no longer lists, so its cases hold whole
/// ledgers; the memory store holds any first segments. A store whose writes land in parts, as archive logs' units do,
/// also samples cut points: the drain of the segment after the held ones crashes after some requests, leaving a key's
/// write landed in part, and the fold alone judges the merge, which <c>Holds</c> decides at the prefix's unit boundary.
/// </remarks>
public abstract class ArchiveMergeProperty : IDisposable
{
    /// <summary>The message of the fold's comparison, which the mutants must fail.</summary>
    protected const string FoldFailure = "which the key's decided rows fold to";

    private static readonly byte[][] Keys = [[1], [2], [3]];

    private static readonly ImmutableArray<NodeId> Replicas = [new NodeId(new Guid("00000000-0000-0000-0000-000000000001"))];

    private static readonly Gen<RowKind> Kinds = Gen.Frequency((6, Gen.Const(RowKind.Append)), (2, Gen.Const(RowKind.Replace)), (1, Gen.Const(RowKind.Delete)));

    /// <summary>A row: its key, kind, body length, and whether it shares the previous row's slot.</summary>
    private static readonly Gen<(int Key, RowKind Kind, int Length, bool SameSlot)> Rows =
        Gen.Select(Gen.Int[0, Keys.Length - 1], Kinds, Gen.Int[0, 2], Gen.Bool, static (key, kind, length, same) => (key, kind, kind switch
        {
            // Every Append is non-empty, and a Delete's body, its tombstone, is empty or 8 bytes (archive.md §3).
            RowKind.Append => length + 1,
            RowKind.Delete => length == 0 ? 0 : 8,
            RowKind.Replace => length,
            RowKind.LocalAppend => throw new ArgumentOutOfRangeException(nameof(kind)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, same));

    /// <summary>Segments of rows, and how many of them, from the first, the store holds.</summary>
    private static readonly Gen<((int Key, RowKind Kind, int Length, bool SameSlot)[][] Segments, int Held)> Cases =
        from segments in Rows.Array[1, 5].Array[1, 6]
        from held in Gen.Int[0, segments.Length]
        select (segments, held);

    /// <summary>
    /// A case each mutant changes the fold of, by construction (#262): the store holds the first ledger whole, whose first
    /// segment holds a write of the first key with one row before its opener, a <c>Replace</c>, and an <c>Append</c> after
    /// it, so that judging the rows by their own positions leaves that <c>Append</c> not held and folds it twice, and
    /// keeping the first held row folds it twice; the second ledger's segment, not held, holds the third key's one row,
    /// which dropping leaves out of the fold. A random case holds that write's shape only now and then.
    /// </summary>
    private static readonly ((int Key, RowKind Kind, int Length, bool SameSlot)[][] Segments, int Held) MutantWitness =
    (
        [
            [(0, RowKind.Append, 1, false), (0, RowKind.Replace, 1, false), (0, RowKind.Append, 1, false)],
            [(1, RowKind.Append, 1, false)],
            [(2, RowKind.Append, 1, false)],
        ],
        2
    );

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>A store for one case, and its drain.</summary>
    protected interface IMergeStore : IAsyncDisposable
    {
        /// <summary>Gets the store.</summary>
        IArchiveStore Store { get; }

        /// <summary>
        /// Gets a value indicating whether the store's coverage is the read's map value, so it holds whole ledgers, those
        /// the map value no longer lists.
        /// </summary>
        bool HoldsWholeLedgers { get; }

        /// <summary>
        /// Drains <paramref name="segments"/>, the first of a ledger's segments or all of them, each its rows of every key,
        /// which the drain borrows, as the store's drain would; a ledger's last segment ends at <paramref name="end"/>.
        /// </summary>
        Task DrainAsync(LedgerId ledger, IReadOnlyList<KeyedRow[]> segments, Slot end);

        /// <summary>
        /// Drains <paramref name="segment"/> as <see cref="DrainAsync"/> would, crashing after its first
        /// <paramref name="requests"/> requests land, so the key whose request failed holds its write in part.
        /// </summary>
        Task DrainInPartAsync(LedgerId ledger, KeyedRow[] segment, int requests) =>
            throw new NotSupportedException("The store's writes land whole.");
    }

    [Fact]
    public Task TheMergeMarksTheRowsHoldsCoversAndFoldsToTheDecidedRows() =>
        Cases.SampleAsync(@case => _leaks.InScopeAsync(() => CheckAsync(@case, ArchiveMerge.Mutant.None, checksMarking: true)), iter: Iterations, threads: 1);

    [Theory]
    [InlineData(nameof(ArchiveMerge.Mutant.KeepsAHeldRow))]
    [InlineData(nameof(ArchiveMerge.Mutant.DropsARowNotHeld))]
    [InlineData(nameof(ArchiveMerge.Mutant.JudgesBeforeTheOpenerByItsOwnPosition))]
    public async Task AMutantMergeFailsTheFoldsComparison(string mutant)
    {
        await _leaks.InScopeAsync(() => CheckAsync(MutantWitness, ArchiveMerge.Mutant.None, checksMarking: true));
        var failure = await Assert.ThrowsAsync<CsCheckException>(() => Gen.Const(MutantWitness).SampleAsync(@case => _leaks.InScopeAsync(() => CheckAsync(@case, Enum.Parse<ArchiveMerge.Mutant>(mutant), checksMarking: false)), iter: 1, threads: 1));

        // The property, here with the fold as its only oracle, fails: CsCheck reports the case's failure in its message,
        // the fold's comparison, not a leak or another fault.
        Assert.Contains(FoldFailure, failure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _leaks.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Samples cases whose drain of the segment after the held ones crashes after 0 to 11 requests, and checks each key's
    /// merge against the fold of its decided rows, each command once; for a store whose writes land in parts.
    /// </summary>
    protected Task SampleCutPointsAsync(long? iterations = null) =>
        Gen.Select(Cases, Gen.Int[0, 11]).SampleAsync(@case => _leaks.InScopeAsync(() => CheckAsync(@case.Item1, ArchiveMerge.Mutant.None, checksMarking: false, @case.Item2)), iter: iterations ?? Iterations, threads: 1);

    /// <summary>Gets the cases each property samples.</summary>
    protected virtual long Iterations => 100;

    /// <summary>Returns a fresh store for one case.</summary>
    protected abstract Task<IMergeStore> CreateAsync();

    /// <summary>
    /// Lays the case's segments out two to a ledger, drains the first <c>Held</c>, or, on a store which holds whole
    /// ledgers, those of whole ledgers among them, and checks each key's merge against the fold of its decided rows; with
    /// a <paramref name="cut"/>, it then drains the next segment until that many requests landed.
    /// </summary>
    private async Task CheckAsync(((int Key, RowKind Kind, int Length, bool SameSlot)[][] Segments, int Held) @case, ArchiveMerge.Mutant mutant, bool checksMarking, int? cut = null)
    {
        await using var harness = await CreateAsync();
        var store = harness.Store;
        var segments = Build(store, @case.Segments);
        try
        {
            var held = harness.HoldsWholeLedgers && @case.Held < segments.Count ? @case.Held - @case.Held % 2 : @case.Held;
            for (var first = 0; first < held; first += 2)
            {
                var ledger = segments[first][0].Row.Segment.Ledger;
                var drained = segments.Skip(first).Take(Math.Min(2, held - first)).ToArray();
                await harness.DrainAsync(ledger, drained, EndOf(segments[Math.Min(first + 1, segments.Count - 1)]));
            }

            if (cut is { } requests && held < segments.Count)
            {
                await harness.DrainInPartAsync(segments[held][0].Row.Segment.Ledger, segments[held], requests);
            }

            var map = MapListing(segments, held);
            foreach (var key in Keys)
            {
                var live = segments.SelectMany(static segment => segment).Where(row => row.Key.AsSpan().SequenceEqual(key)).Select(static row => row.Row).ToArray();
                await CheckKeyAsync(store, key, map, live, [.. segments.Take(held).Select(static segment => segment[0].Row.Segment)], mutant, checksMarking);
            }
        }
        finally
        {
            foreach (var segment in segments)
            {
                KeyedRow.Release(segment);
            }
        }
    }

    private static async Task CheckKeyAsync(IArchiveStore store, byte[] key, LedgerMapValue map, ArchiveRow[] live, HashSet<LedgerSegmentId> drained, ArchiveMerge.Mutant mutant, bool checksMarking)
    {
        var answer = await store.ReadAsync(key, map, CancellationToken.None);
        using var prefix = (answer.Value as ArchiveStoreRead.Read)?.Prefix;
        var outcome = ArchiveMerge.Merge(store, prefix, live, new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false, mutant);
        using var read = Assert.IsType<ArchiveRead>(outcome.Value);
        var expected = ArchiveOracle.Fold(live, static row => row.Kind, static row => row.Body.ToArray());
        var actual = ArchiveOracle.Fold(read.Prefix, read.Rows);
        Assert.True(expected.SameAs(actual), $"The prefix and the rows not held fold to {actual}, not {expected}, {FoldFailure}.");
        if (checksMarking)
        {
            Assert.Equal(live.Select(row => drained.Contains(row.Segment)), read.Rows.ToArray().Select(static row => row.IsHeld));
            Assert.Null(TokenChainChecker.Check(store, prefix?.Token ?? default, ChainRow.Of(read.Rows)));
        }
    }

    /// <summary>
    /// A map value which lists, closed at its last slot, every ledger the store does not hold whole, and an empty open
    /// successor, which holds each key as LG-I1 requires, so a store whose coverage is the map value holds exactly the
    /// ledgers it drained.
    /// </summary>
    private static LedgerMapValue MapListing(List<KeyedRow[]> segments, int held)
    {
        var claim = new PrimaryClaim(Replicas[0], SequencerInstanceId.New());
        var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>();
        var last = segments[^1][0].Row.Segment.Ledger;
        for (var index = held == segments.Count ? held : held - held % 2; index < segments.Count; index += 2)
        {
            var ledger = segments[index][0].Row.Segment.Ledger;
            var end = EndOf(segments[Math.Min(index + 1, segments.Count - 1)]);
            ledgers.Add(new LedgerMetadata(ledger, KeyRange.Root, Replicas, 4, claim, new LedgerStatus.Closed(end)));
        }

        ledgers.Add(new LedgerMetadata(new LedgerId(last.Value + 1), KeyRange.Root, Replicas, 4, claim, LedgerStatus.Open.Instance));
        return new LedgerMapValue(1, new LedgerId(last.Value + 2), 0, ledgers.ToImmutable());
    }

    private static Slot EndOf(KeyedRow[] segment) => new(segment[^1].Row.Tag.Slot.Value + 1);

    /// <summary>
    /// Builds the rows, two segments to a ledger, a slot to each row but those which share their predecessor's, each key's
    /// defining row storing the token the store's <c>Plan</c> gives its previous write, and each row the version after
    /// its key's previous row.
    /// </summary>
    private static List<KeyedRow[]> Build(IArchiveStore store, (int Key, RowKind Kind, int Length, bool SameSlot)[][] segments)
    {
        var chains = Keys.Select(static _ => (Next: default(RowToken), Version: default(RowVersion))).ToArray();
        var built = new List<KeyedRow[]>();
        for (var index = 0; index < segments.Length; index++)
        {
            var ledger = new LedgerId(1 + (ulong)index / 2);
            var segment = new LedgerSegmentId(ledger, new Slot(index % 2 * 100));
            var plans = new Dictionary<int, (RowToken Start, List<ArchiveCommand> Commands)>();
            var superseded = new Dictionary<int, int>();
            var rows = new List<KeyedRow>();
            var (slot, position) = (segment.Start.Value, 0);
            foreach (var (key, kind, length, sameSlot) in segments[index])
            {
                (slot, position) = rows.Count > 0 && sameSlot ? (slot, position + 1) : (rows.Count == 0 ? slot : slot + 1, 0);
                if (!plans.TryGetValue(key, out var write))
                {
                    plans[key] = write = (chains[key].Next, []);
                }

                var body = Enumerable.Range(0, length).Select(offset => unchecked((byte)(rows.Count * 7 + offset + 1))).ToArray();
                var command = ArchiveCommand.Of(kind, body);
                if (command.IsOpener)
                {
                    superseded[key] = superseded.GetValueOrDefault(key) + write.Commands.Count;
                    write.Commands.Clear();
                }

                write.Commands.Add(command);
                chains[key].Version = chains[key].Version.Next();
                using var bytes = ArcBuffer.Wrap(body);
                rows.Add(new KeyedRow(Keys[key], ArchiveRow.Live(new Tag(ledger, new Slot(slot), position), kind, chains[key].Version, segment, write.Start, bytes)));
            }

            foreach (var (key, write) in plans)
            {
                chains[key].Next = store.Plan(new ArchiveWrite(segment, write.Start, [.. write.Commands], superseded.GetValueOrDefault(key)));
            }

            built.Add([.. rows]);
        }

        return built;
    }
}
