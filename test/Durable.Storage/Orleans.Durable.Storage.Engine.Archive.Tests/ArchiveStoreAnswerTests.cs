using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The merge of a history read (archive.md §2.2, §8.1 step 4): it marks the rows the prefix holds by <c>Holds</c>, refuses
/// live rows out of tag order or marked held, answers a break of the key's chain (HI-I4) as its corruption, each break a
/// case of its own, and answers a prefix which reaches past the last write the scans met <c>PastEnd</c>. A prefix may
/// hold part of a write: the merge accepts it at <c>Plan</c> of the held part and refuses a write held after it or a
/// prefix away from that point, which two mutants accept and the chain checker flags too.
/// </summary>
public sealed class ArchiveStoreAnswerTests : IDisposable
{
    private static readonly byte[] Key = [1];
    private static readonly LedgerId Ledger = new(1);
    private static readonly LedgerSegmentId First = new(Ledger, Slot.Zero);
    private static readonly LedgerSegmentId Second = new(Ledger, new Slot(10));
    private static readonly LedgerSegmentId Third = new(Ledger, new Slot(20));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public async Task AMergeMarksTheRowsThePrefixHolds()
    {
        using var store = new InMemoryArchiveStore();
        using var rows = new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, Plan(store, First, default, ArchiveCommand.Append(1)), 2));
        store.Drain(Key, [rows[0]]);
        using var answer = await AnswerAsync(store);
        using var read = Assert.IsType<ArchiveRead>(answer.Merge(rows.All, new Dictionary<LedgerId, Slot> { [Ledger] = new Slot(11) }, openLedger: Ledger, linearizable: true).Value);

        Assert.Equal([true, false], read.Rows.ToArray().Select(static row => row.IsHeld));
        Assert.Equal(0, read.Rows[0].Body.Length);
        Assert.Equal(1, read.Rows[0].BodyLength);
        Assert.Equal(Ledger, read.OpenLedger);
        Assert.True(read.Linearizable);
        Assert.Equal(new byte[] { 1, 1 }, ArchiveOracle.Fold(read.Prefix, read.Rows).Content);
    }

    [Fact]
    public async Task AMergeRefusesLiveRowsOutOfTagOrderOrMarkedHeld()
    {
        using var store = new InMemoryArchiveStore();
        using var rows = new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, default, 2));
        using var answer = await AnswerAsync(store);
        using var held = rows[0].AsHeld();

        _ = Assert.Throws<ArgumentException>(() => answer.Merge([rows[1], rows[0]], new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false));
        _ = Assert.Throws<ArgumentException>(() => answer.Merge([held], new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false));
    }

    /// <summary>
    /// Each way a key's unheld rows can break its chain is corruption (HI-I4): a write from another token than its
    /// predecessor's <c>Plan</c>, the earliest from another than the prefix's; a defining row whose version's base or count
    /// does not follow its key's previous row's, a reset to count 0 among them; and a segment whose rows store two tokens.
    /// The chain checker flags each case too.
    /// </summary>
    [Theory]
    [InlineData("wrong-token")]
    [InlineData("wrong-anchor")]
    [InlineData("wrong-count")]
    [InlineData("wrong-base")]
    [InlineData("reset-count")]
    [InlineData("two-tokens")]
    public async Task EachBreakOfTheChainIsCorruption(string broken)
    {
        using var store = new InMemoryArchiveStore();
        var token = Plan(store, First, default, ArchiveCommand.Append(1));
        var other = new RowToken([1, 7, 0]);
        using var rows = broken switch
        {
            "wrong-token" => new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, default, 2)),
            "wrong-anchor" => new Rows(Row(First, 0, RowKind.Append, token, 1)),
            "wrong-count" => new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, token, 4)),
            "wrong-base" => new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, token, 2, versionBase: other)),
            "reset-count" => new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, token, 0)),
            _ => new Rows(Row(First, 0, RowKind.Append, default, 1), Row(First, 1, RowKind.Append, other, 2)),
        };
        using var answer = await AnswerAsync(store);

        _ = Assert.IsType<ArchiveCorruptException>(answer.Merge(rows.All, new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false).Value);
        Assert.NotNull(TokenChainChecker.Check(store, default, ChainRow.Of(rows.All)));
    }

    /// <summary>
    /// A prefix which reaches past the last write the scans met, as one read from a store which drained past the read's
    /// end would, answers <c>PastEnd</c>, after which the read starts again (archive.md §8.1 step 4).
    /// </summary>
    [Fact]
    public async Task APrefixPastTheLastWriteTheScansMetIsPastEnd()
    {
        using var store = new InMemoryArchiveStore();
        var token = Plan(store, First, default, ArchiveCommand.Append(1));
        using var rows = new Rows(Row(First, 0, RowKind.Append, default, 1), Row(Second, 10, RowKind.Append, token, 2));
        store.Drain(Key, [rows[0]]);
        store.Drain(Key, [rows[1]]);
        using var answer = await AnswerAsync(store);

        Assert.Same(ArchiveMergeOutcome.PastEnd.Instance, answer.Merge([rows[0]], new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false).Value);
        using var whole = Assert.IsType<ArchiveRead>(answer.Merge(rows.All, new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false).Value);
        Assert.All(whole.Rows.ToArray(), static row => Assert.True(row.IsHeld));
        Assert.Equal(answer.Prefix!.Token, whole.NextToken);
        using var none = Assert.IsType<ArchiveRead>(answer.Merge([], new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false).Value);
        Assert.Equal(answer.Prefix.Token, none.NextToken);
    }

    /// <summary>
    /// A read gives the token the key's next write starts from (archive.md §2.2): <c>Plan</c> of the last write it met,
    /// even when the prefix holds that write in part and its held opener is a tombstoned <c>Delete</c>, whose held row no
    /// longer carries its tombstone; and <c>Origin</c> when the store answered <c>Absent</c> and no row is live.
    /// </summary>
    [Fact]
    public async Task AReadGivesTheNextTokenAfterAPartlyHeldWriteWhoseOpenerIsAHeldDelete()
    {
        using var store = new PrefixCutStore();
        byte[] tombstone = [1, 2, 3, 4, 5, 6, 7, 8];
        var second = Plan(store, First, default, ArchiveCommand.Append(1));
        using var rows = new Rows(
            Row(First, 0, RowKind.Append, default, 1),
            Row(Second, 10, RowKind.Delete, second, 2, body: tombstone),
            Row(Second, 11, RowKind.Append, second, 3));
        store.Held[First] = 1;
        store.Held[Second] = 1;
        store.Token = Plan(store, Second, second, ArchiveCommand.Delete(tombstone));

        using var prefix = store.Prefix();
        using var read = Assert.IsType<ArchiveRead>(Merge(store, prefix, rows.All, ArchiveMerge.Mutant.None).Value);
        Assert.Equal([true, true, false], read.Rows.ToArray().Select(static row => row.IsHeld));
        _ = Assert.Throws<InvalidOperationException>(() => read.Rows[1].ToCommand());
        Assert.Equal(Plan(store, Second, second, ArchiveCommand.Delete(tombstone), ArchiveCommand.Append(1)), read.NextToken);

        using var memory = new InMemoryArchiveStore();
        using var absent = await AnswerAsync(memory);
        using var empty = Assert.IsType<ArchiveRead>(absent.Merge([], new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false).Value);
        Assert.True(empty.NextToken.IsEmpty);
    }

    /// <summary>
    /// A prefix which holds a write's first command of two, at <c>Plan</c> of that command, and the write before it whole:
    /// the merge marks exactly those rows held, the next write starts from <c>Plan</c> of the whole write, and the chain
    /// checker agrees.
    /// </summary>
    [Fact]
    public void APrefixWhichHoldsPartOfAWriteAtPlanOfThatPartMerges()
    {
        using var store = new PrefixCutStore();
        using var rows = PartlyHeldChain(store);
        store.Held[First] = 2;
        store.Held[Second] = 1;
        store.Token = Plan(store, Second, rows[2].Token, ArchiveCommand.Append(1));

        using var prefix = store.Prefix();
        using var read = Assert.IsType<ArchiveRead>(Merge(store, prefix, rows.All, ArchiveMerge.Mutant.None).Value);
        Assert.Equal([true, true, true, false, false], read.Rows.ToArray().Select(static row => row.IsHeld));
        Assert.Null(TokenChainChecker.Check(store, prefix.Token, ChainRow.Of(read.Rows)));
    }

    /// <summary>
    /// A write held after one held only in part, and a prefix which holds part of a write away from <c>Plan</c> of that
    /// part, are corruption; each mutant which skips one check accepts its case, and the chain checker flags both.
    /// </summary>
    [Theory]
    [InlineData(nameof(ArchiveMerge.Mutant.HoldsAfterAPartlyHeldWrite))]
    [InlineData(nameof(ArchiveMerge.Mutant.SkipsThePartlyHeldAnchor))]
    public void APartlyHeldWriteOffItsPlanIsCorruptionWhichItsMutantAccepts(string mutant)
    {
        using var store = new PrefixCutStore();
        using var rows = PartlyHeldChain(store);
        bool[] held;
        if (mutant == nameof(ArchiveMerge.Mutant.HoldsAfterAPartlyHeldWrite))
        {
            store.Held[First] = 1;
            store.Held[Second] = 2;
            store.Token = Plan(store, First, default, ArchiveCommand.Append(1));
            held = [true, false, true, true];
        }
        else
        {
            store.Held[First] = 1;
            store.Token = Plan(store, First, default, ArchiveCommand.Append(1), ArchiveCommand.Append(1));
            held = [true, false, false, false, false];
        }

        using var prefix = store.Prefix();
        var live = rows.All[..held.Length];
        _ = Assert.IsType<ArchiveCorruptException>(Merge(store, prefix, live, ArchiveMerge.Mutant.None).Value);
        var accepted = Merge(store, prefix, live, Enum.Parse<ArchiveMerge.Mutant>(mutant));
        Assert.False(accepted.Value is ArchiveCorruptException, $"The mutant {mutant} refused its case.");
        (accepted.Value as ArchiveRead)?.Dispose();
        var marked = ChainRow.Of(live).Select((row, index) => row with { Held = held[index] }).ToArray();
        Assert.NotNull(TokenChainChecker.Check(store, prefix.Token, marked));
    }

    /// <summary>A write the prefix holds after an earlier write it holds none of is corruption, which the chain checker flags too.</summary>
    [Fact]
    public void AWriteHeldAfterOneNotHeldIsCorruption()
    {
        using var store = new PrefixCutStore();
        using var rows = PartlyHeldChain(store);
        store.Held[Second] = 2;
        store.Token = rows[4].Token;

        using var prefix = store.Prefix();
        _ = Assert.IsType<ArchiveCorruptException>(Merge(store, prefix, rows.All, ArchiveMerge.Mutant.None).Value);
        var marked = ChainRow.Of(rows.All).Select((row, index) => row with { Held = index is 2 or 3 }).ToArray();
        Assert.NotNull(TokenChainChecker.Check(store, prefix.Token, marked));
    }

    public void Dispose() => _leaks.Dispose();

    private static ArchiveMergeOutcome Merge(IArchiveStore store, ArchivePrefix prefix, ReadOnlySpan<ArchiveRow> live, ArchiveMerge.Mutant mutant) =>
        ArchiveMerge.Merge(store, prefix, live, new Dictionary<LedgerId, Slot>(), openLedger: null, linearizable: false, mutant);

    /// <summary>A key's chain of three writes, two appends, two appends, and one, from <c>Origin</c>, in the store's tokens.</summary>
    private static Rows PartlyHeldChain(IArchiveStore store)
    {
        var second = Plan(store, First, default, ArchiveCommand.Append(1), ArchiveCommand.Append(1));
        var third = Plan(store, Second, second, ArchiveCommand.Append(1), ArchiveCommand.Append(1));
        return new Rows(
            Row(First, 0, RowKind.Append, default, 1),
            Row(First, 1, RowKind.Append, default, 2),
            Row(Second, 10, RowKind.Append, second, 3),
            Row(Second, 11, RowKind.Append, second, 4),
            Row(Third, 20, RowKind.Append, third, 5));
    }

    private static RowToken Plan(IArchiveStore store, LedgerSegmentId segment, RowToken token, params ArchiveCommand[] commands) =>
        store.Plan(new ArchiveWrite(segment, token, [.. commands]));

    private static ArchiveRow Row(LedgerSegmentId segment, long slot, RowKind kind, RowToken token, ulong count, RowToken versionBase = default, byte[]? body = null)
    {
        using var buffer = ArcBuffer.Wrap(body ?? [1]);
        return ArchiveRow.Live(new Tag(Ledger, new Slot(slot), 0), kind, new RowVersion(versionBase, count), segment, token, buffer);
    }

    private static async Task<ArchiveStoreAnswer> AnswerAsync(InMemoryArchiveStore store)
    {
        var map = Map();
        var read = await store.ReadAsync(Key, map, TestContext.Current.CancellationToken);
        return new ArchiveStoreAnswer(store, map, (read.Value as ArchiveStoreRead.Read)?.Prefix);
    }

    private static LedgerMapValue Map()
    {
        var replicas = ImmutableArray.Create(NodeId.New());
        var open = new LedgerMetadata(Ledger, KeyRange.Root, replicas, 4, new PrimaryClaim(replicas[0], SequencerInstanceId.New()), LedgerStatus.Open.Instance);
        return new LedgerMapValue(1, new LedgerId(2), 0, [open]);
    }

    /// <summary>A case's rows, which it owns.</summary>
    private sealed class Rows(params ArchiveRow[] rows) : IDisposable
    {
        public ReadOnlySpan<ArchiveRow> All => rows;

        public ArchiveRow this[int index] => rows[index];

        public void Dispose() => ArchiveMerge.Release(rows);
    }
}
