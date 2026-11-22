using CsCheck;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Journals.Tests.JournalSamples;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// The fold over a history read (journals.md §2.1, §4.2, §6, §8.3): CsCheck properties that the fold of a prefix and the
/// live rows it does not hold equals an independent model of the key's whole history, wherever the archive's prefix
/// ends and however many of the live rows it holds, with the version of the newest live row or, with none, the rebase to
/// the prefix's version base (JS-I10); and that warm state applying the later entries one at a time reaches the same
/// state. Examples pin existence, the rebase, and each corruption the fold reports. Each case runs in its own page leak
/// scope (verification.md §11.1).
/// </summary>
public sealed class JournalFoldTests : IDisposable
{
    private static readonly RowToken VersionBase = new([0x5a, 0x5a]);
    private static readonly RowToken Base = new([7]);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>A step of a generated history: an entry kind chosen among those which apply.</summary>
    private enum Step
    {
        Append,
        Replace,
        EmptyReplace,
        Delete,
        Expire,
    }

    /// <summary>Gets a generator of valid histories of one journal: an <c>Expired</c> follows only a <c>Delete</c>.</summary>
    private static Gen<Step[]> Histories { get; } = Gen.Enum<Step>().Array[0, 40].Select(static choices =>
    {
        var steps = new List<Step>(choices.Length);
        var deleted = false;
        foreach (var choice in choices)
        {
            var step = choice == Step.Expire && !deleted ? Step.Append : choice;
            steps.Add(step);
            deleted = step == Step.Delete;
        }

        return steps.ToArray();
    });

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The fold of the prefix of a history's first rows and the live rows after the dropped ones, the prefix holding a
    /// leading run of them, equals the model of the whole history: its existence, tombstone, and content's length, and its
    /// version, the newest live row's, or the prefix's version base at count 0 when no row is live (journals.md §8.3).
    /// </summary>
    [Fact]
    public void TheFoldOfAPrefixAndTheLiveRowsItDoesNotHoldIsTheHistorysFold() =>
        Gen.Select(Histories, Gen.Int[0, 100], Gen.Int[0, 100]).Sample((history, dropped, held) => _leaks.InScope(() =>
        {
            var (live, prefix) = Read(history, dropped, held);
            try
            {
                var state = JournalState.Fold(prefix, live);
                AssertModel(history, prefix, live, state);
            }
            finally
            {
                Release(live);
                prefix?.Dispose();
            }
        }));

    /// <summary>
    /// A read's content folds from the same prefix and live rows into chunks whose bytes are the model's content of the
    /// whole history, none of them empty, with the length of its base: the content the last opener after the prefix began,
    /// or the prefix's (journals.md §2.4, §8.3, §8.7).
    /// </summary>
    [Fact]
    public void AReadsContentIsTheHistorysContentWithItsBase() =>
        Gen.Select(Histories, Gen.Int[0, 100], Gen.Int[0, 100]).Sample((history, dropped, held) => _leaks.InScope(() =>
        {
            var (live, prefix) = Read(history, dropped, held);
            try
            {
                using var prefixContent = prefix?.Content.Retain() ?? default;
                var (chunks, baseLength) = JournalReadResult.FoldContent(prefixContent, live);
                var (content, expectedBase) = ContentModel(history, history.Length - live.Length + live.Count(static row => row.IsHeld));
                Assert.All(chunks, static chunk => Assert.NotEqual(0, chunk.Length));
                Assert.Equal(content, chunks.SelectMany(static chunk => chunk.ToArray()).ToArray());
                Assert.Equal(expectedBase, baseLength);
            }
            finally
            {
                Release(live);
                prefix?.Dispose();
            }
        }));
    /// <summary>
    /// Warm state which loads a history's first rows, from <c>Origin</c>, and then applies each later entry, as admission applies its own
    /// decided batches, reaches the state the fold of the whole history gives (JS-I6).
    /// </summary>
    [Fact]
    public void WarmStateApplyingTheLaterEntriesReachesTheWholeHistorysFold() =>
        Gen.Select(Histories, Gen.Int[0, 100]).Sample((history, split) => _leaks.InScope(() =>
        {
            var cut = split % (history.Length + 1);
            var rows = Rows(history, default);
            try
            {
                var state = JournalState.Fold(null, rows.AsSpan(0, cut));
                for (var index = cut; index < history.Length; index++)
                {
                    var version = V((ulong)index);
                    state = JournalState.Apply(state, EntryOf(history[index], version, index), version.Next());
                }

                AssertSameState(JournalState.Fold(null, rows), state);
            }
            finally
            {
                Release(rows);
            }
        }));

    /// <summary>A key with no history has no state; its version is <c>Origin</c>.</summary>
    [Fact]
    public void AKeyWithNoHistoryHasNoState() => Assert.Null(JournalState.Fold(null, []));

    /// <summary>
    /// With no live row, a load rebases (JS-I10): the version is the prefix's version base at count 0, and the content,
    /// existence, and tombstone are the prefix's.
    /// </summary>
    [Fact]
    public void WithNoLiveRowTheVersionRebasesToThePrefixsVersionBase()
    {
        using var content = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
        using var prefix = new ArchivePrefix(default, VersionBase, hasBase: false, null, content, null);

        var state = JournalState.Fold(prefix, [])!;

        Assert.Equal(new JournalVersion(VersionBase, 0), state.CurrentVersion);
        Assert.True(state.Exists);
        Assert.Null(state.DeletedAt);
        Assert.Equal(3, state.ContentLength);
    }

    /// <summary>A prefix's tombstone is the journal's: deleted at its time, absent, with no content.</summary>
    [Fact]
    public void APrefixsTombstoneDeletesTheJournal()
    {
        using var prefix = new ArchivePrefix(default, VersionBase, hasBase: false, Tombstone(DefaultTicks), default, null);

        var state = JournalState.Fold(prefix, [])!;

        Assert.False(state.Exists);
        Assert.Equal(new DateTimeOffset(DefaultTicks, TimeSpan.Zero), state.DeletedAt);
    }

    /// <summary>
    /// An empty <c>Replace</c> leaves the journal existing, from a prefix whose base it opened and as a live row, and an
    /// empty prefix with no base, as an <c>Expired</c> leaves it, is absent (journals.md §2.1).
    /// </summary>
    [Fact]
    public void AnEmptyReplaceExistsAndAPrefixWithNoBaseOrContentIsAbsent()
    {
        using var opened = new ArchivePrefix(default, VersionBase, hasBase: true, null, default, null);
        using var expired = new ArchivePrefix(default, VersionBase, hasBase: false, null, default, null);
        ArchiveRow[] live = [Live(At(1), RowKind.Replace, V(4), [])];
        try
        {
            Assert.True(JournalState.Fold(opened, [])!.Exists);
            Assert.False(JournalState.Fold(expired, [])!.Exists);
            Assert.Null(JournalState.Fold(expired, [])!.DeletedAt);
            var state = JournalState.Fold(expired, live)!;
            Assert.True(state.Exists);
            Assert.Equal(V(4), state.CurrentVersion);
            Assert.Equal(0, state.ContentLength);
        }
        finally
        {
            Release(live);
        }
    }

    /// <summary>
    /// An <c>Expired</c> leaves the journal absent at its advanced version, and a later <c>Append</c> continues the count
    /// (journals.md §2.1).
    /// </summary>
    [Fact]
    public void AnExpiredLeavesTheJournalAbsentAndTheCountGoesOn()
    {
        var state = JournalState.Apply(null, EntryOf(Step.Delete, V(0), 1), V(1));
        state = JournalState.Apply(state, EntryOf(Step.Expire, V(1), 2), V(2));
        Assert.False(state.Exists);
        Assert.Null(state.DeletedAt);
        Assert.Equal(V(2), state.CurrentVersion);

        state = JournalState.Apply(state, EntryOf(Step.Append, V(2), 3), V(3));

        Assert.True(state.Exists);
        Assert.Equal(V(3), state.CurrentVersion);
    }

    /// <summary>
    /// A load's planning state (journals.md §8.3): with its newest live segment unheld, open or closed, the key's write
    /// there, its token, and both folds of it; with no live row, the prefix's token and no recorded segment.
    /// </summary>
    [Fact]
    public void APlanningStateIsTheNewestUnheldSegmentsWrite()
    {
        var first = new LedgerSegmentId(new LedgerId(1), Slot.Zero);
        var second = new LedgerSegmentId(new LedgerId(1), new Slot(5));
        var token = Store.Plan(new ArchiveWrite(first, default, [ArchiveCommand.Append(2)]));
        ArchiveRow[] live =
        [
            Live(At(0), RowKind.Append, V(1), [1, 1], segment: first),
            Live(At(5), RowKind.Append, V(2), [2, 2], token, second),
            Live(At(6), RowKind.Append, V(3), [3, 3], token, second),
        ];
        using var prefix = new ArchivePrefix(token, VersionBase, hasBase: false, null, default, null);
        try
        {
            var planning = Load(null, live).Planning;

            Assert.Equal((second, token), (planning.Segment, planning.Token));
            Assert.Equal(Store.Plan(new ArchiveWrite(second, token, [ArchiveCommand.Append(2), ArchiveCommand.Append(2)])), planning.Written.End());
            Assert.True(planning.FoldsAgree());
            var rebased = Load(prefix, []).Planning;
            Assert.Equal((null, prefix.Token, prefix.Token), (rebased.Segment, rebased.Token, rebased.Written.End()));
        }
        finally
        {
            Release(live);
        }
    }

    /// <summary>
    /// A newest write which the prefix holds in part, whose held opener is a tombstoned <c>Delete</c> whose held row no
    /// longer carries its tombstone, plans from the token the read gives after it: a held row lies in a closed segment,
    /// so the key has no recorded segment (journals.md §8.3, archive.md §2.2).
    /// </summary>
    [Fact]
    public void APartlyHeldNewestWriteWhoseOpenerIsAHeldDeletePlansFromTheReadsNextToken()
    {
        var segment = new LedgerSegmentId(new LedgerId(1), Slot.Zero);
        var nextToken = new RowToken([1, 2, 0, 1]);
        using var delete = Live(At(0), RowKind.Delete, V(1), Tombstone(DefaultTicks), segment: segment);
        ArchiveRow[] live = [delete.AsHeld(), Live(At(1), RowKind.Append, V(2), [7], segment: segment)];
        using var prefix = new ArchivePrefix(new RowToken([1, 2, 0, 0]), VersionBase, hasBase: false, Tombstone(DefaultTicks), default, null);
        try
        {
            var planning = JournalPlanning.Load(Store, live, nextToken);

            Assert.True(JournalState.Fold(prefix, live)!.Exists);
            Assert.Equal((null, nextToken, nextToken), (planning.Segment, planning.Token, planning.Written.End()));
            Assert.True(planning.FoldsAgree());
        }
        finally
        {
            Release(live);
        }
    }

    /// <summary>
    /// A <c>Delete</c> row whose body is neither empty nor an 8-byte tombstone in the deletion time's range, and a prefix
    /// whose tombstone is out of range, are corruption (journals.md §6).
    /// </summary>
    [Theory]
    [InlineData(7, 0L)]
    [InlineData(9, 0L)]
    [InlineData(8, -1L)]
    [InlineData(8, JournalEntry.MaxTicks + 1)]
    public void ADeleteWhoseBodyIsNoTombstoneIsCorrupt(int length, long ticks)
    {
        var body = new byte[length];
        Tombstone(ticks).AsSpan(0, Math.Min(length, 8)).CopyTo(body);
        ArchiveRow[] live = [Live(At(3, 1), RowKind.Delete, V(1), body)];
        try
        {
            var corrupt = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Fold(null, live));

            Assert.Contains("The Delete row at (1, 3, 1) has a body of", corrupt.Message, StringComparison.Ordinal);
        }
        finally
        {
            Release(live);
        }

        if (length == 8)
        {
            using var prefix = new ArchivePrefix(default, VersionBase, hasBase: false, body, default, null);
            Assert.Contains("The archive prefix's tombstone", Assert.Throws<JournalStoreCorruptException>(() => JournalState.Fold(prefix, [])).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An entry which does not apply to the state, or whose decided version does not follow it, is corruption: warm state
    /// no longer folds the ledger's rows (journals.md §1).
    /// </summary>
    [Fact]
    public void AnEntryWhichDoesNotApplyIsCorrupt()
    {
        var state = JournalState.Apply(null, EntryOf(Step.Append, V(0), 0), V(1));

        _ = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Apply(state, EntryOf(Step.Append, V(0), 1), V(1)));
        _ = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Apply(state, EntryOf(Step.Append, V(1), 1), V(3)));
        _ = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Apply(state, EntryOf(Step.Expire, V(1), 1), V(2)));
        _ = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Apply(state, EntryOf(Step.Append, V(9, 1), 1), new JournalVersion(new RowToken([9]), 2)));
    }

    /// <summary>
    /// A state whose count is the <c>u64</c> maximum is one no row can advance: whatever version an entry is decided at,
    /// applying it is corruption (admission refuses the entry before it reaches a batch).
    /// </summary>
    [Fact]
    public void AStateNoRowCanAdvanceTakesNoEntry()
    {
        ArchiveRow[] live = [Live(At(1), RowKind.Append, V(ulong.MaxValue), [1])];
        try
        {
            var state = JournalState.Fold(null, live)!;

            _ = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Apply(state, EntryOf(Step.Append, V(ulong.MaxValue), 1), V(0)));
            _ = Assert.Throws<JournalStoreCorruptException>(() => JournalState.Apply(state, EntryOf(Step.Append, V(ulong.MaxValue), 1), V(ulong.MaxValue)));
            Assert.Equal(V(ulong.MaxValue), state.CurrentVersion);
        }
        finally
        {
            Release(live);
        }
    }

    /// <summary>
    /// Warm state's budget counts each loaded state at <c>JournalState.EstimatedBytes</c> (journals.md §8.8), at least what
    /// folding one allocates: an existing state and a deleted one.
    /// </summary>
    [Fact]
    public void WarmStateCountsAtLeastWhatAFoldedStateAllocates()
    {
        using var existing = new ArchivePrefix(default, VersionBase, hasBase: true, null, default, null);
        using var deleted = new ArchivePrefix(default, VersionBase, hasBase: false, Tombstone(DefaultTicks), default, null);
        foreach (var prefix in new[] { existing, deleted })
        {
            _ = JournalState.Fold(prefix, []);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var state = JournalState.Fold(prefix, []);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(state);

            Assert.InRange(allocated, 1, JournalState.EstimatedBytes);
        }
    }

    /// <summary>
    /// Warm state's budget counts each loaded key's planning state at <c>JournalPlanning.EstimatedBytes</c> (journals.md
    /// §8.8), at least what one allocates with its two folds, before and after a roll: a store's fold is within
    /// <c>ArchivePlan.MaxStateBytes</c>, which archive stores' contract tests check.
    /// </summary>
    [Fact]
    public void WarmStateCountsAtLeastWhatAPlanningStateAllocates()
    {
        var token = Store.Plan(new ArchiveWrite(new LedgerSegmentId(new LedgerId(1), Slot.Zero), default, [ArchiveCommand.Replace(1), ArchiveCommand.Append(1)]));
        _ = JournalPlanning.Unrecorded(Store, token);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var planning = JournalPlanning.Unrecorded(Store, token);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1, JournalPlanning.EstimatedBytes);

        before = GC.GetAllocatedBytesForCurrentThread();
        planning.RollTo(Store, new LedgerSegmentId(new LedgerId(1), new Slot(9)), token);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(planning);

        Assert.InRange(allocated, 1, 2 * ArchivePlan.MaxStateBytes);
    }

    /// <summary>
    /// Splits a history's rows into the archive's prefix and the live rows: the first <paramref name="dropped"/> rows
    /// lie only in the prefix, the next <paramref name="held"/> are live and held, and the rest are live and not held.
    /// </summary>
    private static (ArchiveRow[] Live, ArchivePrefix? Prefix) Read(Step[] history, int dropped, int held)
    {
        var rows = Rows(history, Base);
        var drop = history.Length == 0 ? 0 : dropped % (history.Length + 1);
        var hold = drop + (history.Length == 0 ? 0 : held % (history.Length - drop + 1));
        ArchivePrefix? prefix = null;
        if (hold > 0)
        {
            var tagged = rows.Take(hold).Select(static row => new TaggedRow(row.Tag, new Row([1], row.Kind, row.Body))).ToArray();
            try
            {
                prefix = ArchivePrefix.Fold(tagged, default, VersionBase, null);
            }
            finally
            {
                foreach (var row in tagged)
                {
                    row.Row.Dispose();
                }
            }
        }

        var live = new ArchiveRow[history.Length - drop];
        for (var index = drop; index < history.Length; index++)
        {
            live[index - drop] = index < hold ? rows[index].AsHeld() : rows[index].Retain();
        }

        Release(rows);
        return (live, prefix);
    }

    /// <summary>Writes a history's rows, of one segment of ledger 1, at versions of <paramref name="rowBase"/> from count 1.</summary>
    private static ArchiveRow[] Rows(Step[] history, RowToken rowBase)
    {
        var rows = new ArchiveRow[history.Length];
        for (var index = 0; index < history.Length; index++)
        {
            var version = new JournalVersion(rowBase, (ulong)index + 1);
            rows[index] = history[index] switch
            {
                Step.Append => Live(At(index), RowKind.Append, version, [(byte)index, 1]),
                Step.Replace => Live(At(index), RowKind.Replace, version, [(byte)index, 2, 2]),
                Step.EmptyReplace => Live(At(index), RowKind.Replace, version, []),
                Step.Delete => Live(At(index), RowKind.Delete, version, Tombstone(index)),
                Step.Expire => Live(At(index), RowKind.Delete, version, []),
                _ => throw new ArgumentOutOfRangeException(nameof(history)),
            };
        }

        return rows;
    }

    /// <summary>Returns the entry of a history's step at <paramref name="index"/>, at <paramref name="expected"/>, as its row writes it.</summary>
    private static JournalEntry EntryOf(Step step, JournalVersion expected, int index) => step switch
    {
        Step.Append => Entry(JournalEntryKind.Append, expected, payloadLength: 2),
        Step.Replace => Entry(JournalEntryKind.Replace, expected, payloadLength: 3),
        Step.EmptyReplace => Entry(JournalEntryKind.Replace, expected),
        Step.Delete => Entry(JournalEntryKind.Delete, expected, deletionTicks: index),
        Step.Expire => Entry(JournalEntryKind.Expired, expected),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    /// <summary>
    /// Returns the content of a history by journals.md §2.1, and the length of its base: the content after the first
    /// <paramref name="prefixRows"/> rows, which the prefix holds, or after the last opener past them.
    /// </summary>
    private static (byte[] Content, long BaseLength) ContentModel(Step[] history, int prefixRows)
    {
        var content = new List<byte>();
        long baseLength = 0;
        for (var index = 0; index < history.Length; index++)
        {
            switch (history[index])
            {
                case Step.Append:
                    content.AddRange([(byte)index, 1]);
                    break;
                case Step.Replace:
                    content.Clear();
                    content.AddRange([(byte)index, 2, 2]);
                    baseLength = content.Count;
                    break;
                case Step.EmptyReplace or Step.Delete or Step.Expire:
                    content.Clear();
                    baseLength = 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(history));
            }

            if (index == prefixRows - 1)
            {
                baseLength = content.Count;
            }
        }

        return ([.. content], baseLength);
    }
    /// <summary>Asserts the fold against an independent model of journals.md §2.1 and §8.3 over the whole history.</summary>
    private static void AssertModel(Step[] history, ArchivePrefix? prefix, ArchiveRow[] live, JournalState? state)
    {
        if (prefix is null && live.Length == 0)
        {
            Assert.Null(state);
            return;
        }

        bool exists = false;
        long? deleted = null;
        long content = 0;
        for (var index = 0; index < history.Length; index++)
        {
            (exists, deleted, content) = history[index] switch
            {
                Step.Append => (true, null, content + 2),
                Step.Replace => (true, null, 3L),
                Step.EmptyReplace => (true, null, 0L),
                Step.Delete => (false, index, 0L),
                Step.Expire => (false, (long?)null, 0L),
                _ => throw new ArgumentOutOfRangeException(nameof(history)),
            };
        }

        Assert.NotNull(state);
        Assert.Equal(exists, state.Exists);
        Assert.Equal(deleted is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null, state.DeletedAt);
        Assert.Equal(content, state.ContentLength);
        Assert.Equal(live.Length == 0 ? new JournalVersion(VersionBase, 0) : new JournalVersion(Base, (ulong)history.Length), state.CurrentVersion);
    }

    /// <summary>Asserts that two states hold the same values: the state compares by none, being its owner's to change.</summary>
    private static void AssertSameState(JournalState? expected, JournalState? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(
            (expected.CurrentVersion, expected.Exists, expected.DeletedAt, expected.ContentLength),
            (actual.CurrentVersion, actual.Exists, actual.DeletedAt, actual.ContentLength));
    }
}
