using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// The fold's decision on one ledger map change against one value (ledgers.md §2.6), which the committer maps to the
/// change's outcome.
/// </summary>
internal readonly union LedgerMapFoldVerdict(
    LedgerMapFoldVerdict.GoalPresent,
    LedgerMapFoldVerdict.Applies,
    LedgerMapFoldVerdict.RuleFails,
    LedgerMapFoldVerdict.TooLarge,
    LedgerMapFoldVerdict.Defect,
    LedgerMapFoldVerdict.Overflowed)
{
    /// <summary>The value holds the change's goal.</summary>
    public sealed record GoalPresent
    {
        private GoalPresent()
        {
        }

        public static GoalPresent Instance { get; } = new();
    }

    /// <summary>The change's goal is absent and its rule holds, and its result keeps LG-I1 and fits the value limit.</summary>
    public sealed record Applies
    {
        private Applies()
        {
        }

        public static Applies Instance { get; } = new();
    }

    /// <summary>The change's goal is absent and its commit rule fails.</summary>
    public sealed record RuleFails
    {
        private RuleFails()
        {
        }

        public static RuleFails Instance { get; } = new();
    }

    /// <summary>Applying the change would make the value exceed the register's value limit.</summary>
    public sealed record TooLarge
    {
        private TooLarge()
        {
        }

        public static TooLarge Instance { get; } = new();
    }

    /// <summary>The change is a defect: it breaks <paramref name="Reason"/>'s rule.</summary>
    public sealed record Defect(LedgerMapRejection Reason);

    /// <summary>Applying the change would advance <c>NextLedgerId</c> past 2^64 − 1 (conventions.md §8).</summary>
    public sealed record Overflowed(OverflowException Exception);
}

/// <summary>
/// A change the fold applied, with the retirement floor of the value it was applied to, which the pre-write check holds
/// an install's <c>ChosenAt</c> to (LG-I11).
/// </summary>
internal readonly record struct LedgerMapAppliedChange(LedgerMapChange Change, ulong FloorBefore);

/// <summary>
/// The commit rule of ledger map changes (ledgers.md §2.6): each change's goal, its rule, and its application, the fold
/// of a batch in submission order, and the invariants every written value keeps (LG-I1 to LG-I3, LG-I9 to LG-I11).
/// Pure.
/// </summary>
internal static class LedgerMapRules
{
    /// <summary>
    /// Decides <paramref name="change"/> against <paramref name="value"/>: its goal first, then its rule, then the result
    /// of applying it, which must keep LG-I1 and fit <paramref name="maxValueLength"/>.
    /// </summary>
    public static LedgerMapFoldVerdict Decide(LedgerMapValue value, LedgerMapChange change, int maxValueLength) =>
        Decide(new LedgerMapBuilder(value), change, maxValueLength);

    /// <summary>
    /// Folds <paramref name="changes"/> in submission order over <paramref name="value"/>: each is decided against the
    /// result of the changes before it, and applies to it when its verdict is <see cref="LedgerMapFoldVerdict.Applies"/>.
    /// The result keeps <paramref name="value"/>'s epoch, and is <paramref name="value"/> itself when nothing applies.
    /// </summary>
    public static (LedgerMapValue Result, LedgerMapFoldVerdict[] Verdicts, ImmutableArray<LedgerMapAppliedChange> Applied) Fold(
        LedgerMapValue value,
        IReadOnlyList<LedgerMapChange> changes,
        int maxValueLength)
    {
        var map = new LedgerMapBuilder(value);
        var verdicts = new LedgerMapFoldVerdict[changes.Count];
        var applied = ImmutableArray.CreateBuilder<LedgerMapAppliedChange>();
        for (var index = 0; index < changes.Count; index++)
        {
            verdicts[index] = Decide(map, changes[index], maxValueLength);
            if (verdicts[index].Value is LedgerMapFoldVerdict.Applies)
            {
                applied.Add(new LedgerMapAppliedChange(changes[index], map.RetirementFloor));
                Apply(map, changes[index]);
            }
        }

        return (applied.Count == 0 ? value : map.ToValue(), verdicts, applied.ToImmutable());
    }

    /// <summary>
    /// Returns whether <paramref name="change"/>'s goal holds in <paramref name="value"/>: an install's when the value
    /// lists a ledger under any claim it opens, a drop's when its ledger is below <c>NextLedgerId</c> and not listed, a
    /// floor raise's when the floor is at least its revision.
    /// </summary>
    public static bool GoalHolds(LedgerMapValue value, LedgerMapChange change) => GoalHolds(new LedgerMapBuilder(value), change);

    /// <summary>
    /// Checks the invariants of a value the committer is about to write over <paramref name="previous"/>, which the
    /// changes <paramref name="applied"/>, in order, produced, and returns the first one <paramref name="next"/> breaks, or
    /// <see langword="null"/>. A failure is a committer defect.
    /// </summary>
    /// <remarks>
    /// LG-I2 follows from LG-I1 and the checks of LG-I3 and LG-I9: every ledger of <paramref name="previous"/> stays listed,
    /// unchanged or closed by this write, or leaves once closed, so a ledger opened over an open ledger which stays listed
    /// open would overlap it.
    /// </remarks>
    public static string? CheckInvariants(LedgerMapValue previous, LedgerMapValue next, IReadOnlyList<LedgerMapAppliedChange> applied)
    {
        KeyRange[] open = [.. next.Ledgers.Where(static ledger => ledger.IsOpen).Select(static ledger => ledger.Range)];
        if (open.Length > 0 && !KeyRange.IsPartition(open))
        {
            return "LG-I1: the open ledgers' ranges do not partition the keyspace.";
        }

        if (open.Length == 0 && previous.Ledgers.Any(static ledger => ledger.IsOpen))
        {
            return "LG-I1: the write leaves no ledger open over the keyspace.";
        }

        if (next.Epoch != checked(previous.Epoch + 1))
        {
            return "The epoch did not advance by one.";
        }

        var closedHere = new Dictionary<LedgerId, Slot>();
        var droppedHere = new HashSet<LedgerId>();
        var opened = 0UL;
        foreach (var change in applied)
        {
            if (change.Change.Value is LedgerMapChange.Install install)
            {
                if (install.ChosenAt < change.FloorBefore)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"LG-I11: an install chosen at {install.ChosenAt} applied over the floor {change.FloorBefore}.");
                }

                opened += (ulong)install.Opens.Length;
                foreach (var closing in install.Closes)
                {
                    closedHere[closing.Id] = closing.T;
                }
            }
            else if (change.Change.Value is LedgerMapChange.Drop drop)
            {
                _ = droppedHere.Add(drop.Ledger);
            }
        }

        if (next.NextLedgerId < previous.NextLedgerId || next.NextLedgerId.Value - previous.NextLedgerId.Value != opened)
        {
            return "LG-I3: the next ledger ID did not advance by the ledgers opened.";
        }

        foreach (var ledger in next.Ledgers)
        {
            if (ledger.Id >= previous.NextLedgerId)
            {
                // A new ID is listed open, unless a later change of the write closed it.
                if (!ledger.IsOpen && !ClosedHere(ledger))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"LG-I3: new ledger {ledger.Id} is listed closed, but no change of the write closed it.");
                }
            }
            else if (previous.Get(ledger.Id) is not { } old || !ledger.Equals(old) && !(old.IsOpen && ClosedHere(ledger) && ledger.Equals(old.Close(closedHere[ledger.Id]))))
            {
                return string.Create(CultureInfo.InvariantCulture, $"LG-I3: ledger {ledger.Id} returns, or changes otherwise than closed by this write.");
            }
        }

        for (var id = previous.NextLedgerId.Value; id < next.NextLedgerId.Value; id++)
        {
            if (next.Get(new LedgerId(id)) is null && !droppedHere.Contains(new LedgerId(id)))
            {
                return string.Create(CultureInfo.InvariantCulture, $"LG-I3: new ledger {id} is missing, but no change of the write dropped it.");
            }
        }

        foreach (var removed in previous.Ledgers.Where(old => next.Get(old.Id) is null))
        {
            if (removed.IsOpen && !closedHere.ContainsKey(removed.Id))
            {
                return string.Create(CultureInfo.InvariantCulture, $"LG-I9: ledger {removed.Id} left the map open.");
            }

            if (next.Ledgers.Any(ledger => ledger.Id < removed.Id && ledger.Range.Overlaps(removed.Range)))
            {
                return string.Create(CultureInfo.InvariantCulture, $"LG-I10: ledger {removed.Id} left before a lower ledger overlapping it.");
            }
        }

        return next.RetirementFloor < previous.RetirementFloor ? "LG-I11: the retirement floor fell." : null;

        bool ClosedHere(LedgerMetadata ledger) =>
            closedHere.TryGetValue(ledger.Id, out var t) && ledger.Status.Value is LedgerStatus.Closed closed && closed.T == t;
    }

    private static LedgerMapFoldVerdict Decide(LedgerMapBuilder map, LedgerMapChange change, int maxValueLength) => change switch
    {
        LedgerMapChange.Install install => DecideInstall(map, install, maxValueLength),
        LedgerMapChange.Drop drop => DecideDrop(map, drop),
        LedgerMapChange.RaiseFloor raise => DecideRaiseFloor(map, raise),
        null => throw new UnreachableException(),
    };

    private static bool GoalHolds(LedgerMapBuilder map, LedgerMapChange change) => change switch
    {
        LedgerMapChange.Install install => InstallGoalHolds(map, install),
        LedgerMapChange.Drop drop => drop.Ledger < map.NextLedgerId && map.IndexOf(drop.Ledger) < 0,
        LedgerMapChange.RaiseFloor raise => map.RetirementFloor >= raise.Revision,
        null => throw new UnreachableException(),
    };

    /// <summary>Applies a change whose verdict is <see cref="LedgerMapFoldVerdict.Applies"/>.</summary>
    private static void Apply(LedgerMapBuilder map, LedgerMapChange change)
    {
        if (change.Value is LedgerMapChange.Install install)
        {
            foreach (var closing in install.Closes)
            {
                map.Close(map.IndexOf(closing.Id), closing.T);
            }

            // LG-I3: the write assigns consecutive IDs from NextLedgerId, in the order of the opens, so a successor's ID
            // exceeds every parent's it closes (ledgers.md §5), which the archive's prefixes rely on.
            foreach (var opening in install.Opens)
            {
                Debug.Assert(install.Closes.All(closing => closing.Id < map.NextLedgerId), "A successor ledger's ID exceeds every parent's it closes.");
                map.Open(opening);
            }
        }
        else if (change.Value is LedgerMapChange.Drop drop)
        {
            map.Remove(map.IndexOf(drop.Ledger));
        }
        else if (change.Value is LedgerMapChange.RaiseFloor raise)
        {
            map.RaiseFloor(raise.Revision);
        }
        else
        {
            throw new UnreachableException();
        }
    }

    private static bool InstallGoalHolds(LedgerMapBuilder map, LedgerMapChange.Install install)
    {
        for (var index = 0; index < map.Count; index++)
        {
            foreach (var opening in install.Opens)
            {
                if (opening.Primary == map[index].Primary)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static LedgerMapFoldVerdict DecideInstall(LedgerMapBuilder map, LedgerMapChange.Install install, int maxValueLength)
    {
        if (InstallGoalHolds(map, install))
        {
            return LedgerMapFoldVerdict.GoalPresent.Instance;
        }

        // A ledger at or above NextLedgerId was never installed, so no map derived it: a defect, not a stale choice.
        if (install.Closes.Any(closing => closing.Id >= map.NextLedgerId))
        {
            return new LedgerMapFoldVerdict.Defect(LedgerMapRejection.InstallClosesNeverInstalled);
        }

        // The rule (LG-I2, LG-I11), which the executor's precondition and the registry's decision share.
        LedgerId[] closedIds = [.. install.Closes.Select(static closing => closing.Id)];
        KeyRange[] opened = [.. install.Opens.Select(static opening => opening.Range)];
        if (!LedgerMapValue.InstallRuleHolds(map.AsSpan(), map.RetirementFloor, closedIds, opened, install.ChosenAt))
        {
            return LedgerMapFoldVerdict.RuleFails.Instance;
        }

        // conventions.md §8: the write may advance NextLedgerId to 2^64 − 1, no further.
        if (ulong.MaxValue - map.NextLedgerId.Value < (ulong)install.Opens.Length)
        {
            return new LedgerMapFoldVerdict.Overflowed(new OverflowException(string.Create(
                CultureInfo.InvariantCulture,
                $"Opening {install.Opens.Length} ledgers from ledger {map.NextLedgerId} would advance the next ledger ID past 2^64 - 1.")));
        }

        // LG-I1: the open ranges partition the keyspace, and every open ledger overlapping an opened range is closed, so
        // the result partitions it exactly when the opens tile what the closed ledgers held, or the whole keyspace when
        // no ledger is open. The pre-write check judges the whole value.
        KeyRange[] replaced = map.OpenCount == 0 ? [KeyRange.Root] : [.. install.Closes.Select(closing => map.Get(closing.Id)!.Value.Range)];
        if (!KeyRange.Tiles(opened, replaced))
        {
            return new LedgerMapFoldVerdict.Defect(LedgerMapRejection.OpensLeaveGap);
        }

        var length = map.EncodedLength + install.Closes.Length * sizeof(ulong);
        foreach (var opening in install.Opens)
        {
            length += LedgerMapValue.EntryLength(opening.Range, opening.Location, closed: false);
        }

        return length > maxValueLength ? LedgerMapFoldVerdict.TooLarge.Instance : LedgerMapFoldVerdict.Applies.Instance;
    }

    private static LedgerMapFoldVerdict DecideDrop(LedgerMapBuilder map, LedgerMapChange.Drop drop)
    {
        if (drop.Ledger >= map.NextLedgerId)
        {
            return new LedgerMapFoldVerdict.Defect(LedgerMapRejection.DropNeverInstalled);
        }

        if (map.Get(drop.Ledger) is not { } dropped)
        {
            return LedgerMapFoldVerdict.GoalPresent.Instance;
        }

        if (dropped.IsOpen)
        {
            return new LedgerMapFoldVerdict.Defect(LedgerMapRejection.DropNotClosed);
        }

        // LG-I10: a ledger leaves only after every lower ledger overlapping it.
        for (var index = 0; index < map.Count && map[index].Id < drop.Ledger; index++)
        {
            if (map[index].Range.Overlaps(dropped.Range))
            {
                return new LedgerMapFoldVerdict.Defect(LedgerMapRejection.DropBlockedByLineage);
            }
        }

        return LedgerMapFoldVerdict.Applies.Instance;
    }

    /// <summary>A floor raise's rule always holds, and it never changes the value's size.</summary>
    private static LedgerMapFoldVerdict DecideRaiseFloor(LedgerMapBuilder map, LedgerMapChange.RaiseFloor raise) =>
        map.RetirementFloor >= raise.Revision ? LedgerMapFoldVerdict.GoalPresent.Instance : LedgerMapFoldVerdict.Applies.Instance;
}
