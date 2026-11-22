using System.Collections.Immutable;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Examples of the commit rule (ledgers.md §2.6): each change's goal, rule, and result; every defect with its reason;
/// the value limit and the ID space's end; the changes' shape faults and equality; and the pre-write check of
/// LG-I1 to LG-I3, LG-I10, and LG-I11 against hand-built bad values.
/// </summary>
public sealed class LedgerMapRulesTests : IDisposable
{
    private static int _sequencers;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ABootstrapAppliesToAMapWithNoOpenLedgerOnlyOnce()
    {
        var bootstrap = Install([], [KeyRange.Root]);
        var applied = Applies(LedgerMapValue.Empty, bootstrap);
        Assert.Equal(1UL, applied.Ledgers.Single().Id.Value);
        Assert.Equal(2UL, applied.NextLedgerId.Value);
        Assert.IsType<LedgerMapFoldVerdict.GoalPresent>(Decide(applied, bootstrap).Value);
        Assert.IsType<LedgerMapFoldVerdict.RuleFails>(Decide(applied, Install([], [KeyRange.Root])).Value);
    }

    /// <summary>A split closes its ledger and opens its children under consecutive IDs; a merge closes both siblings.</summary>
    [Fact]
    public void ASplitAndAMergeCloseAndOpenUnderConsecutiveIds()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        var (lower, upper) = KeyRange.Root.Children;
        var split = Applies(root, Install([new(new LedgerId(1), new Slot(7))], [lower, upper]));
        Assert.Equal([1UL, 2UL, 3UL], split.Ledgers.Select(static ledger => ledger.Id.Value));
        Assert.Equal(new LedgerStatus.Closed(new Slot(7)), split.Ledgers[0].Status);
        var merged = Applies(split, Install([new(new LedgerId(2), new Slot(1)), new(new LedgerId(3), new Slot(2))], [KeyRange.Root]));
        Assert.Equal(4UL, merged.Ledgers.Single(static ledger => ledger.IsOpen).Id.Value);
    }

    /// <summary>Of two replacements of one ledger folded in order, the first applies and the second's rule fails.</summary>
    [Fact]
    public void OfTwoReplacementsOfOneLedgerInABatchOneApplies()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        var (result, verdicts, _) = LedgerMapRules.Fold(root, [Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root]), Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root])], int.MaxValue);
        Assert.IsType<LedgerMapFoldVerdict.Applies>(verdicts[0].Value);
        Assert.IsType<LedgerMapFoldVerdict.RuleFails>(verdicts[1].Value);
        Assert.Equal(2UL, result.Ledgers.Single(static ledger => ledger.IsOpen).Id.Value);
    }

    [Fact]
    public void AnInstallChosenBelowTheFloorFails()
    {
        var root = Applies(Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root])), new LedgerMapChange.RaiseFloor(5));
        Assert.IsType<LedgerMapFoldVerdict.RuleFails>(Decide(root, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root], chosenAt: 4)).Value);
        _ = Applies(root, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root], chosenAt: 5));
    }

    /// <summary>The floor rises only: a raise to it or below finds its goal, and it never refuses, even into a full map.</summary>
    [Fact]
    public void TheFloorOnlyRisesAndARaiseIsNeverRefused()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        var raised = Applies(root, new LedgerMapChange.RaiseFloor(3));
        Assert.Equal(3UL, raised.RetirementFloor);
        Assert.IsType<LedgerMapFoldVerdict.GoalPresent>(Decide(raised, new LedgerMapChange.RaiseFloor(2)).Value);
        Assert.IsType<LedgerMapFoldVerdict.Applies>(LedgerMapRules.Decide(raised, new LedgerMapChange.RaiseFloor(4), raised.EncodedLength).Value);
    }

    /// <summary>A change which would exceed the limit is refused; a drop frees room, after which it applies.</summary>
    [Fact]
    public void AChangeWhichWouldExceedTheLimitIsRefusedUntilADropFreesRoom()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        var replaced = Applies(root, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root]));
        var limit = replaced.EncodedLength;
        var next = Install([new(new LedgerId(2), Slot.Zero)], [KeyRange.Root]);
        Assert.IsType<LedgerMapFoldVerdict.TooLarge>(LedgerMapRules.Decide(replaced, next, limit).Value);
        var dropped = Applies(replaced, new LedgerMapChange.Drop(new LedgerId(1)));
        Assert.IsType<LedgerMapFoldVerdict.Applies>(LedgerMapRules.Decide(dropped, next, limit).Value);
    }

    [Fact]
    public void EachDefectiveDropIsRejectedForItsRule()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        Assert.Equal(new LedgerMapFoldVerdict.Defect(LedgerMapRejection.DropNotClosed), Decide(root, new LedgerMapChange.Drop(new LedgerId(1))).Value);
        Assert.Equal(new LedgerMapFoldVerdict.Defect(LedgerMapRejection.DropNeverInstalled), Decide(root, new LedgerMapChange.Drop(new LedgerId(2))).Value);

        var twice = Applies(Applies(root, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root])), Install([new(new LedgerId(2), Slot.Zero)], [KeyRange.Root]));
        Assert.Equal(new LedgerMapFoldVerdict.Defect(LedgerMapRejection.DropBlockedByLineage), Decide(twice, new LedgerMapChange.Drop(new LedgerId(2))).Value);

        // In lineage order, both drops apply, and each then finds its goal.
        var (result, verdicts, _) = LedgerMapRules.Fold(twice, [new LedgerMapChange.Drop(new LedgerId(1)), new LedgerMapChange.Drop(new LedgerId(2))], int.MaxValue);
        Assert.All(verdicts, static verdict => Assert.IsType<LedgerMapFoldVerdict.Applies>(verdict.Value));
        Assert.IsType<LedgerMapFoldVerdict.GoalPresent>(Decide(result, new LedgerMapChange.Drop(new LedgerId(1))).Value);
    }

    /// <summary>Opens which leave a gap are a defect, and the batch's other changes still apply.</summary>
    [Fact]
    public void OpensWhichLeaveAGapAreRejectedWhileTheBatchsOtherChangesApply()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        var (_, verdicts, _) = LedgerMapRules.Fold(
            root,
            [Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root.Children.Lower]), new LedgerMapChange.RaiseFloor(1)],
            int.MaxValue);
        Assert.Equal(new LedgerMapFoldVerdict.Defect(LedgerMapRejection.OpensLeaveGap), verdicts[0].Value);
        Assert.IsType<LedgerMapFoldVerdict.Applies>(verdicts[1].Value);
        Assert.Equal(new LedgerMapFoldVerdict.Defect(LedgerMapRejection.OpensLeaveGap), Decide(LedgerMapValue.Empty, Install([], [KeyRange.Root.Children.Upper])).Value);
    }

    /// <summary>
    /// An install which closes a ledger no install created is a defect, while one which closes a ledger already closed is
    /// a failed rule, which a stale controller's install meets in good faith.
    /// </summary>
    [Fact]
    public void AnInstallWhichClosesANeverInstalledLedgerIsADefect()
    {
        var root = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        Assert.Equal(new LedgerMapFoldVerdict.Defect(LedgerMapRejection.InstallClosesNeverInstalled), Decide(root, Install([new(new LedgerId(2), Slot.Zero)], [KeyRange.Root])).Value);
        var replaced = Applies(root, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root]));
        Assert.IsType<LedgerMapFoldVerdict.RuleFails>(Decide(replaced, Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root])).Value);
    }

    /// <summary>An install which would advance NextLedgerId past 2^64 − 1 overflows, for that change alone (conventions.md §8).</summary>
    [Fact]
    public void AnInstallPastTheLastLedgerIdOverflows()
    {
        var last = new LedgerMapValue(1, new LedgerId(ulong.MaxValue), 0, []);
        _ = Assert.IsType<LedgerMapFoldVerdict.Overflowed>(Decide(last, Install([], [KeyRange.Root])).Value);
        var (lower, upper) = KeyRange.Root.Children;
        var nearlyLast = new LedgerMapValue(1, new LedgerId(ulong.MaxValue - 1), 0, []);
        _ = Assert.IsType<LedgerMapFoldVerdict.Overflowed>(Decide(nearlyLast, Install([], [lower, upper])).Value);
    }

    [Fact]
    public void AChangesShapeFaultsThrowAtConstruction()
    {
        var opening = Opening(KeyRange.Root);
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapChange.Install([], [], 0));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapChange.Install([new(new LedgerId(1), Slot.Zero), new(new LedgerId(1), new Slot(1))], [opening], 0));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapChange.Install([], [opening, Opening(KeyRange.Root.Children.Lower)], 0));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapChange.Drop(default));
        _ = Assert.Throws<ArgumentException>(() => new LedgerClosing(default, Slot.Zero));
        _ = Assert.Throws<ArgumentException>(() => new LedgerOpening(KeyRange.Root, [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2)], 1, opening.Primary));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerOpening(KeyRange.Root, [LedgerMapSamples.Node(1)], 0, opening.Primary));
    }

    /// <summary>Identical changes in distinct arrays are equal, so the committer merges them.</summary>
    [Fact]
    public void IdenticalChangesInDistinctArraysAreEqual()
    {
        var opening = Opening(KeyRange.Root);
        LedgerMapChange left = new LedgerMapChange.Install([new(new LedgerId(1), Slot.Zero)], [opening], 3);
        LedgerMapChange right = new LedgerMapChange.Install([new(new LedgerId(1), Slot.Zero)], [new LedgerOpening(KeyRange.Root, [.. opening.Replicas], opening.K, opening.Primary)], 3);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, new LedgerMapChange.Install([new(new LedgerId(1), new Slot(1))], [opening], 3));
        Assert.NotEqual(left, new LedgerMapChange.Drop(new LedgerId(1)));
        Assert.Equal((LedgerMapCommitOutcome)new LedgerMapCommitOutcome.Applied(2), new LedgerMapCommitOutcome.Applied(2));
        Assert.NotEqual((LedgerMapCommitOutcome)new LedgerMapCommitOutcome.Applied(2), new LedgerMapCommitOutcome.Superseded(2));
    }

    /// <summary>A switch over a default union, which holds no case, reaches its throwing <c>null</c> arm (conventions.md §15).</summary>
    [Fact]
    public void ASwitchOverADefaultUnionThrows()
    {
        _ = Assert.Throws<UnreachableException>(() => LedgerMapRules.Decide(LedgerMapValue.Empty, default, int.MaxValue));
        _ = Assert.Throws<UnreachableException>(() => LedgerMapReference.KindOf(default));
    }

    public static TheoryData<string> BadWrites =>
    [
        "LG-I1 gap", "LG-I3 reused ID", "LG-I3 next ID", "LG-I3 new ledger closed", "LG-I3 new ledger missing", "LG-I3 changed",
        "LG-I9 open ledger removed", "LG-I10 lineage", "LG-I11 floor fell", "LG-I11 install below the floor",
    ];

    /// <summary>
    /// The pre-write check names the invariant each hand-built bad value breaks, given the changes the fold applied to
    /// produce it, and passes the good ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadWrites))]
    public void ThePreWriteCheckNamesTheInvariantABadValueBreaks(string violation)
    {
        var (lower, upper) = KeyRange.Root.Children;
        var shape = (ImmutableArray.Create(LedgerMapSamples.Node(1)), 1, Opening(KeyRange.Root).Primary);
        var root = Ledger(1, KeyRange.Root);
        var rootClosed = root.Close(Slot.Zero);
        var one = new LedgerMapValue(1, new LedgerId(2), 3, [root]);
        var replace = Applied(Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root], chosenAt: 3), 3);
        var (previous, next, applied) = violation switch
        {
            "LG-I1 gap" => (one, new LedgerMapValue(2, new LedgerId(3), 3, [rootClosed, Ledger(2, lower)]), [Applied(Install([new(new LedgerId(1), Slot.Zero)], [lower], 3), 3)]),
            "LG-I3 reused ID" => (new LedgerMapValue(1, new LedgerId(3), 3, [root]), new LedgerMapValue(2, new LedgerId(3), 3, [rootClosed, Ledger(2, KeyRange.Root)]), []),
            "LG-I3 next ID" => (one, new LedgerMapValue(2, new LedgerId(4), 3, [rootClosed, Ledger(3, KeyRange.Root)]), [replace]),
            "LG-I3 new ledger closed" => (one, new LedgerMapValue(2, new LedgerId(4), 3, [rootClosed, Ledger(2, KeyRange.Root).Close(Slot.Zero), Ledger(3, KeyRange.Root)]),
                [replace, Applied(Install([], [KeyRange.Root], 3), 3)]),
            "LG-I3 new ledger missing" => (one, new LedgerMapValue(2, new LedgerId(4), 3, [rootClosed, Ledger(3, KeyRange.Root)]),
                [Applied(Install([new(new LedgerId(1), Slot.Zero)], [lower, upper], 3), 3)]),
            "LG-I3 changed" => (one, new LedgerMapValue(2, new LedgerId(2), 3, [LedgerMapSamples.Ledger(1, KeyRange.Root, (shape.Item1, 2, shape.Item3), LedgerStatus.Open.Instance)]), []),
            "LG-I9 open ledger removed" => (one, new LedgerMapValue(2, new LedgerId(3), 3, [Ledger(2, KeyRange.Root)]), [Applied(Install([], [KeyRange.Root], 3), 3)]),
            "LG-I10 lineage" => (
                new LedgerMapValue(1, new LedgerId(4), 3, [rootClosed, LedgerMapSamples.Ledger(2, KeyRange.Root, shape, new LedgerStatus.Closed(Slot.Zero)), Ledger(3, KeyRange.Root)]),
                new LedgerMapValue(2, new LedgerId(4), 3, [rootClosed, Ledger(3, KeyRange.Root)]),
                [Applied(new LedgerMapChange.Drop(new LedgerId(2)), 3)]),
            "LG-I11 floor fell" => (one, new LedgerMapValue(2, new LedgerId(2), 2, [root]), ImmutableArray<LedgerMapAppliedChange>.Empty),
            "LG-I11 install below the floor" => (one, new LedgerMapValue(2, new LedgerId(3), 3, [rootClosed, Ledger(2, KeyRange.Root)]),
                [Applied(Install([new(new LedgerId(1), Slot.Zero)], [KeyRange.Root], chosenAt: 2), 3)]),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };
        var reason = LedgerMapRules.CheckInvariants(previous, next, applied);
        Assert.StartsWith(violation[..violation.IndexOf(' ', StringComparison.Ordinal)] + ":", reason, StringComparison.Ordinal);

        // The same value one epoch on, and a replacement whose new ledger a later change of its write closes, pass.
        Assert.Null(LedgerMapRules.CheckInvariants(previous, new LedgerMapValue(previous.Epoch + 1, previous.NextLedgerId, previous.RetirementFloor, previous.Ledgers), []));
        var replacedTwice = new LedgerMapValue(2, new LedgerId(3), 3, [rootClosed, Ledger(2, KeyRange.Root).Close(new Slot(1))]);
        Assert.Null(LedgerMapRules.CheckInvariants(
            one,
            new LedgerMapValue(2, new LedgerId(4), 3, [.. replacedTwice.Ledgers, Ledger(3, KeyRange.Root)]),
            [replace, Applied(Install([new(new LedgerId(2), new Slot(1))], [KeyRange.Root], chosenAt: 3), 3)]));

        LedgerMetadata Ledger(ulong id, KeyRange range) => LedgerMapSamples.Ledger(id, range, shape, LedgerStatus.Open.Instance);

        static LedgerMapAppliedChange Applied(LedgerMapChange change, ulong floorBefore) => new(change, floorBefore);
    }
    internal static LedgerMapChange Install(ImmutableArray<LedgerClosing> closes, KeyRange[] ranges, ulong chosenAt = 0) =>
        new LedgerMapChange.Install(closes, [.. ranges.Select(Opening)], chosenAt);

    internal static LedgerOpening Opening(KeyRange range) =>
        new(range, [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(3)], 2,
            new PrimaryClaim(LedgerMapSamples.Node(1), new SequencerInstanceId(new Guid(Interlocked.Increment(ref _sequencers), 0x7e, 0x57, [0x80, 0, 0, 0, 0, 0, 0, 2]))));

    private static LedgerMapFoldVerdict Decide(LedgerMapValue value, LedgerMapChange change) => LedgerMapRules.Decide(value, change, int.MaxValue);

    /// <summary>Returns the value <paramref name="change"/>, which must apply, leaves.</summary>
    internal static LedgerMapValue Applies(LedgerMapValue value, LedgerMapChange change)
    {
        var (result, verdicts, _) = LedgerMapRules.Fold(value, [change], int.MaxValue);
        _ = Assert.IsType<LedgerMapFoldVerdict.Applies>(Assert.Single(verdicts).Value);
        return result;
    }
}
