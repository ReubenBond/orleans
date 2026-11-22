using System.Collections.Immutable;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The commit rule of ledgers.md §2.6 restated from the spec's text, apart from <c>LedgerMapRules</c>, over ranges of at
/// most <see cref="Bits"/> bits: it judges coverage and overlap by enumerating the 2^<see cref="Bits"/> keys of that
/// many bits rather than through <see cref="KeyRange"/>'s algebra. It is the oracle of the rule's properties, and its
/// invariant checker judges every value a committer writes (verification.md §3.2).
/// </summary>
internal static class LedgerMapReference
{
    /// <summary>The bits of the keys the reference enumerates: every range it judges is at most this deep.</summary>
    public const int Bits = 4;

    /// <summary>A verdict as the reference reads the rule: its kind, a defect's reason, and the value applying leaves.</summary>
    public readonly record struct Verdict(string Kind, LedgerMapRejection? Reason = null, LedgerMapValue? Result = null);

    /// <summary>Decides <paramref name="change"/> against <paramref name="value"/> as ledgers.md §2.6 states the rule.</summary>
    public static Verdict Decide(LedgerMapValue value, LedgerMapChange change, int maxValueLength)
    {
        switch (change)
        {
            case LedgerMapChange.RaiseFloor raise:
                return value.RetirementFloor >= raise.Revision
                    ? new("goal")
                    : new("applies", Result: new LedgerMapValue(value.Epoch, value.NextLedgerId, raise.Revision, value.Ledgers));
            case LedgerMapChange.Drop drop:
                var listed = value.Ledgers.Where(ledger => ledger.Id == drop.Ledger).ToArray();
                if (drop.Ledger.Value < value.NextLedgerId.Value && listed.Length == 0)
                {
                    return new("goal");
                }

                if (drop.Ledger.Value >= value.NextLedgerId.Value)
                {
                    return new("defect", LedgerMapRejection.DropNeverInstalled);
                }

                if (listed[0].Status.Value is LedgerStatus.Open)
                {
                    return new("defect", LedgerMapRejection.DropNotClosed);
                }

                if (value.Ledgers.Any(ledger => ledger.Id.Value < drop.Ledger.Value && SharesAKey(ledger.Range, listed[0].Range)))
                {
                    return new("defect", LedgerMapRejection.DropBlockedByLineage);
                }

                return new("applies", Result: new LedgerMapValue(value.Epoch, value.NextLedgerId, value.RetirementFloor, [.. value.Ledgers.Where(ledger => ledger.Id != drop.Ledger)]));
            case LedgerMapChange.Install install:
                if (value.Ledgers.Any(ledger => install.Opens.Any(open => open.Primary.Equals(ledger.Primary))))
                {
                    return new("goal");
                }

                if (install.Closes.Any(closing => closing.Id.Value >= value.NextLedgerId.Value))
                {
                    return new("defect", LedgerMapRejection.InstallClosesNeverInstalled);
                }

                var openIds = value.Ledgers.Where(static ledger => ledger.Status.Value is LedgerStatus.Open).Select(static ledger => ledger.Id).ToHashSet();
                var closes = install.Closes.Select(static closing => closing.Id).ToHashSet();
                var ruleHolds = closes.All(openIds.Contains)
                    && value.Ledgers.Where(ledger => openIds.Contains(ledger.Id)).All(ledger => !install.Opens.Any(open => SharesAKey(open.Range, ledger.Range)) || closes.Contains(ledger.Id))
                    && install.ChosenAt >= value.RetirementFloor;
                if (!ruleHolds)
                {
                    return new("rule-fails");
                }

                var ledgers = value.Ledgers
                    .Select(ledger => install.Closes.Where(closing => closing.Id == ledger.Id).Select(closing => (LedgerMetadata?)ledger.Close(closing.T)).FirstOrDefault() ?? ledger)
                    .ToList();
                var next = value.NextLedgerId.Value;
                foreach (var open in install.Opens)
                {
                    ledgers.Add(new LedgerMetadata(new LedgerId(next), open.Range, open.Replicas, open.K, open.Primary, LedgerStatus.Open.Instance));
                    next++;
                }

                var result = new LedgerMapValue(value.Epoch, new LedgerId(next), value.RetirementFloor, [.. ledgers]);
                if (!Partitions(result))
                {
                    return new("defect", LedgerMapRejection.OpensLeaveGap);
                }

                return result.EncodedLength > maxValueLength ? new("too-large") : new("applies", Result: result);
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Returns the kind of a fold verdict, in the reference's words.</summary>
    public static string KindOf(LedgerMapFoldVerdict verdict) => verdict switch
    {
        LedgerMapFoldVerdict.GoalPresent => "goal",
        LedgerMapFoldVerdict.Applies => "applies",
        LedgerMapFoldVerdict.RuleFails => "rule-fails",
        LedgerMapFoldVerdict.TooLarge => "too-large",
        LedgerMapFoldVerdict.Defect => "defect",
        LedgerMapFoldVerdict.Overflowed => "overflowed",
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Returns the first invariant a written value <paramref name="next"/> breaks over <paramref name="previous"/>, judged
    /// by enumerating keys: LG-I1 to LG-I3, LG-I9 to LG-I11; or <see langword="null"/>.
    /// </summary>
    public static string? Violation(LedgerMapValue previous, LedgerMapValue next)
    {
        // Once a value lists an open ledger, every later one partitions the keyspace among its open ledgers.
        if ((previous.Ledgers.Any(static ledger => ledger.Status.Value is LedgerStatus.Open) || next.Ledgers.Any(static ledger => ledger.Status.Value is LedgerStatus.Open))
            && !Partitions(next))
        {
            return "LG-I1";
        }

        // A ledger listed in both values is the same, or the earlier one closed: nothing else of it changes.
        foreach (var ledger in next.Ledgers)
        {
            if (previous.Get(ledger.Id) is { } old && !ledger.Equals(old)
                && !(old.Status.Value is LedgerStatus.Open && ledger.Status.Value is LedgerStatus.Closed closed && ledger.Equals(old.Close(closed.T))))
            {
                return "LG-I9";
            }
        }

        var previousIds = previous.Ledgers.Select(static ledger => ledger.Id.Value).ToHashSet();
        var created = next.Ledgers.Where(ledger => !previousIds.Contains(ledger.Id.Value) && ledger.Id.Value >= previous.NextLedgerId.Value).Select(static ledger => ledger.Id.Value).Order().ToArray();
        var expected = Enumerable.Range(0, created.Length).Select(index => previous.NextLedgerId.Value + (ulong)index);
        if (!created.SequenceEqual(expected) || next.NextLedgerId.Value != previous.NextLedgerId.Value + (ulong)created.Length
            || next.Ledgers.Any(ledger => ledger.Id.Value < previous.NextLedgerId.Value && !previousIds.Contains(ledger.Id.Value)))
        {
            return "LG-I3";
        }

        foreach (var opened in next.Ledgers.Where(ledger => created.Contains(ledger.Id.Value)))
        {
            if (previous.Ledgers.Any(old => old.Status.Value is LedgerStatus.Open && SharesAKey(old.Range, opened.Range)
                && next.Ledgers.Any(kept => kept.Id == old.Id && kept.Status.Value is LedgerStatus.Open)))
            {
                return "LG-I2";
            }
        }

        foreach (var removed in previous.Ledgers.Where(old => next.Ledgers.All(ledger => ledger.Id != old.Id)))
        {
            if (next.Ledgers.Any(ledger => ledger.Id.Value < removed.Id.Value && SharesAKey(ledger.Range, removed.Range)))
            {
                return "LG-I10";
            }
        }

        return next.RetirementFloor < previous.RetirementFloor ? "LG-I11" : null;
    }

    /// <summary>Returns whether every key of <see cref="Bits"/> bits lies in exactly one open ledger's range (LG-I1).</summary>
    public static bool Partitions(LedgerMapValue value) =>
        Keys().All(key => value.Ledgers.Count(ledger => ledger.Status.Value is LedgerStatus.Open && Holds(ledger.Range, key)) == 1);

    /// <summary>Returns whether two ranges share a key of <see cref="Bits"/> bits.</summary>
    public static bool SharesAKey(KeyRange left, KeyRange right) => Keys().Any(key => Holds(left, key) && Holds(right, key));

    /// <summary>The keys of <see cref="Bits"/> bits, each as a byte whose low bits are 0.</summary>
    private static IEnumerable<int> Keys() => Enumerable.Range(0, 1 << Bits).Select(static key => key << (8 - Bits));

    /// <summary>Returns whether <paramref name="range"/>, of depth at most <see cref="Bits"/>, holds the one-byte key.</summary>
    private static bool Holds(KeyRange range, int key)
    {
        Debug.Assert(range.Depth <= Bits, "The reference judges ranges of at most its bits.");
        return range.Depth == 0 || key >> (8 - range.Depth) == range.Prefix[0] >> (8 - range.Depth);
    }
}
