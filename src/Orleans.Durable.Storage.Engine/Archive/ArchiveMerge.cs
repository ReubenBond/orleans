using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// The merge of a history read (archive.md §2.1, §8.1 step 4): a key's live rows, grouped by segment into its writes,
/// each row marked held by the store's <c>Holds</c>, and the stored tokens and versions of the rows not held checked
/// against the key's chain (HI-I4). The key's history is the prefix followed by the rows not held.
/// </summary>
internal static class ArchiveMerge
{
    /// <summary>A merge which marks rows wrongly or checks the chain short, which archive.md §10's oracles must catch.</summary>
    internal enum Mutant
    {
        /// <summary>The merge.</summary>
        None,

        /// <summary>Keeps the first row the prefix holds as not held.</summary>
        KeepsAHeldRow,

        /// <summary>Marks the first row the prefix does not hold as held, dropping it.</summary>
        DropsARowNotHeld,

        /// <summary>Judges a row before its write's opener by its own position in the segment, not the opener's.</summary>
        JudgesBeforeTheOpenerByItsOwnPosition,

        /// <summary>Skips the check of the chain and versions of the rows the prefix does not hold.</summary>
        SkipsTheChainCheck,

        /// <summary>Accepts a write the prefix holds after one it holds only in part.</summary>
        HoldsAfterAPartlyHeldWrite,

        /// <summary>Skips the check that a prefix which holds part of a write lies at <c>Plan</c> of the part it holds.</summary>
        SkipsThePartlyHeldAnchor,
    }

    /// <summary>
    /// Merges <paramref name="prefix"/> with <paramref name="live"/>, in strictly increasing tag order: each row retained
    /// and marked held when the prefix holds it, a held row dropping its body, and the chain of the rows not held checked.
    /// Absent, the prefix holds no row.
    /// </summary>
    /// <param name="store">The store whose pure members judge the rows.</param>
    /// <param name="prefix">The store's prefix of the key, or <see langword="null"/> for <c>Absent</c>, which the read retains.</param>
    /// <param name="live">The key's live rows, which the merge borrows.</param>
    /// <param name="through">Each live ledger's decided end, which the read copies.</param>
    /// <param name="openLedger">The open ledger over the key in the map value read, whose seal check gave its end; null when it lists none.</param>
    /// <param name="linearizable">Whether the open ledger's end was a seal check's tail in its primary's process.</param>
    /// <param name="mutant">The mutant to merge as, for tests.</param>
    /// <returns>
    /// The read, which the caller owns; <see cref="ArchiveMergeOutcome.PastEnd"/> when the prefix holds a write past the
    /// last one the scans met; or the <see cref="ArchiveCorruptException"/> naming how the rows break the key's chain: a
    /// stored token or version of a row not held off the chain, a segment's rows storing two tokens, a write held after
    /// one not held whole, or a prefix which holds part of a write away from <c>Plan</c> of that part (HI-I4).
    /// </returns>
    public static ArchiveMergeOutcome Merge(IArchiveStore store, ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> live, IReadOnlyDictionary<LedgerId, Slot> through, LedgerId? openLedger, bool linearizable, Mutant mutant = Mutant.None) =>
        MergeCore(store, prefix, live, through, openLedger, linearizable, mutant, copyThrough: true);

    internal static ArchiveMergeOutcome MergeOwned(IArchiveStore store, ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> live, IReadOnlyDictionary<LedgerId, Slot> through, LedgerId? openLedger, bool linearizable, Mutant mutant = Mutant.None) =>
        MergeCore(store, prefix, live, through, openLedger, linearizable, mutant, copyThrough: false);

    private static ArchiveMergeOutcome MergeCore(IArchiveStore store, ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> live, IReadOnlyDictionary<LedgerId, Slot> through, LedgerId? openLedger, bool linearizable, Mutant mutant, bool copyThrough)
    {
        if (live.IsEmpty)
        {
            return new ArchiveRead(prefix?.Retain(), [], copyThrough ? CopyThrough(through) : through, openLedger, linearizable, prefix?.Token ?? default);
        }

        var rows = new ArchiveRow[live.Length];
        var built = 0;
        try
        {
            var chain = new Chain(store, prefix, mutant);
            var ownPosition = mutant == Mutant.JudgesBeforeTheOpenerByItsOwnPosition;
            for (var start = 0; start < live.Length;)
            {
                var segment = live[start..EndOfSegment(live, start)];
                var write = ArchiveWrite.OfRows(segment);
                var held = prefix is null ? 0 : HeldCount(store, prefix, write);
                if (chain.Check(segment, write, held) is { } broken)
                {
                    return broken;
                }

                // A row before the write's opener, which the opener supersedes, is judged by the opener's position.
                var superseded = segment.Length - write.Commands.Length;
                for (var index = 0; index < segment.Length; index++)
                {
                    var position = ownPosition ? index + 1 : Math.Max(index - superseded + 1, 1);
                    rows[built++] = position <= held ? segment[index].AsHeld() : segment[index].Retain();
                }

                start += segment.Length;
            }

            if (chain.IsPastEnd)
            {
                return ArchiveMergeOutcome.PastEnd.Instance;
            }

            Mismark(rows, live, mutant);
            var read = new ArchiveRead(prefix?.Retain(), rows, copyThrough ? CopyThrough(through) : through, openLedger, linearizable, chain.NextToken);
            built = 0;
            return read;
        }
        finally
        {
            Release(rows.AsSpan(0, built));
        }
    }

    private static IReadOnlyDictionary<LedgerId, Slot> CopyThrough(IReadOnlyDictionary<LedgerId, Slot> through)
    {
        if (through is EmptyLedgerThrough or SingleLedgerThrough)
        {
            return through;
        }

        if (through.Count == 0)
        {
            return EmptyLedgerThrough.Instance;
        }

        if (through.Count == 1)
        {
            foreach (var pair in through)
            {
                return new SingleLedgerThrough(pair.Key, pair.Value);
            }
        }

        return through.ToDictionary();
    }

    /// <summary>Releases each row's body.</summary>
    public static void Release(ReadOnlySpan<ArchiveRow> rows)
    {
        foreach (var row in rows)
        {
            row.Dispose();
        }
    }

    /// <summary>Returns the end of the segment whose rows begin at <paramref name="start"/>.</summary>
    private static int EndOfSegment(ReadOnlySpan<ArchiveRow> live, int start)
    {
        var end = start + 1;
        while (end < live.Length && live[end].Segment == live[start].Segment)
        {
            end++;
        }

        return end;
    }

    /// <summary>The most commands of <paramref name="write"/> the prefix holds, found by halving, since <c>Holds</c> is monotone.</summary>
    private static int HeldCount(IArchiveStore store, ArchivePrefix prefix, ArchiveWrite write)
    {
        Debug.Assert(IsMonotone(store, prefix, write), "Holds is true for 0 and monotone in the count (archive.md §3).");
        var (low, high) = (0, write.Commands.Length);
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (store.Holds(prefix, write, middle))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static bool IsMonotone(IArchiveStore store, ArchivePrefix prefix, ArchiveWrite write)
    {
        var held = 0;
        while (held < write.Commands.Length && store.Holds(prefix, write, held + 1))
        {
            held++;
        }

        for (var count = held + 2; count <= write.Commands.Length; count++)
        {
            if (store.Holds(prefix, write, count))
            {
                return false;
            }
        }

        return store.Holds(prefix, write, 0);
    }

    /// <summary>Applies a mutant which marks one row wrongly, after the chain check judged the true marking.</summary>
    private static void Mismark(ArchiveRow[] rows, ReadOnlySpan<ArchiveRow> live, Mutant mutant)
    {
        var target = mutant switch
        {
            Mutant.KeepsAHeldRow => Array.FindIndex(rows, static row => row.IsHeld),
            Mutant.DropsARowNotHeld => Array.FindIndex(rows, static row => !row.IsHeld),
            Mutant.None
                or Mutant.JudgesBeforeTheOpenerByItsOwnPosition
                or Mutant.SkipsTheChainCheck
                or Mutant.HoldsAfterAPartlyHeldWrite
                or Mutant.SkipsThePartlyHeldAnchor => -1,
            _ => throw new UnreachableException(),
        };
        if (target < 0)
        {
            return;
        }

        rows[target].Dispose();
        rows[target] = mutant == Mutant.KeepsAHeldRow ? live[target].Retain() : live[target].AsHeld();
    }

    private static ArchiveCorruptException Broken(ArchiveWrite write, FormattableString reason) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The write of segment ({write.Segment.Ledger}, {write.Segment.Start}) {reason.ToString(CultureInfo.InvariantCulture)} (HI-I4)."));

    /// <summary>
    /// The check of a key's chain over the writes a merge meets (HI-I4), each held by the prefix in none, part, or all of
    /// its commands. A write held in none starts from the prefix's token, when it is the earliest such, and otherwise from
    /// <c>Plan</c> of the write before it, and its defining row's stored version follows the key's previous scanned row's.
    /// A write held in part lies after every write held whole, and the prefix's token is <c>Plan</c> of the part it holds;
    /// the next write starts from <c>Plan</c> of the whole write. A write held whole follows only writes held whole. The
    /// read reaches past the prefix unless the last write it met is held whole with <c>Plan</c> of it past the prefix's
    /// token.
    /// </summary>
    private struct Chain(IArchiveStore store, ArchivePrefix? prefix, Mutant mutant)
    {
        private readonly bool _checks = mutant != Mutant.SkipsTheChainCheck;

        // The token the next write held in none starts from, once a write not held whole planned it; otherwise the
        // prefix's token.
        private RowToken? _expected;
        private bool _sawNotWhollyHeld;
        private ArchiveWrite? _lastWhollyHeld;
        private RowVersion? _previous;

        /// <summary>
        /// Gets the token the key's next write starts from after the last write the merge met: <c>Plan</c> of that write,
        /// which the chain planned from its rows, or the prefix's token when the prefix holds it whole, which
        /// <see cref="IsPastEnd"/> confirmed, or when the merge met none; <c>Origin</c> for <c>Absent</c>.
        /// </summary>
        public readonly RowToken NextToken => _expected ?? prefix?.Token ?? default;

        /// <summary>Gets a value indicating whether the prefix holds a write past the last one the merge met.</summary>
        public readonly bool IsPastEnd => _checks && prefix is not null && _lastWhollyHeld is { } last && store.Plan(last) != prefix.Token;

        /// <summary>Checks one segment's write, whose rows are <paramref name="rows"/> and of whose commands the prefix holds <paramref name="held"/>.</summary>
        public ArchiveCorruptException? Check(ReadOnlySpan<ArchiveRow> rows, ArchiveWrite write, int held)
        {
            var previous = _previous;
            _previous = rows[^1].Version;
            _lastWhollyHeld = held == write.Commands.Length ? write : null;
            if (!_checks)
            {
                // The mutant checks nothing, but plans the next write as the checks would.
                _expected = held == write.Commands.Length ? null : store.Plan(write);
                return null;
            }

            foreach (var row in rows)
            {
                if (row.Token != write.Token)
                {
                    return Broken(write, $"stores two tokens in its key's rows, {write.Token} and {row.Token}");
                }
            }

            if (held == 0)
            {
                return CheckNoneHeld(rows[0], write, previous);
            }

            if (_sawNotWhollyHeld)
            {
                return Broken(write, $"is held, after an earlier write of its key which the store does not hold whole");
            }

            if (held == write.Commands.Length)
            {
                _expected = null;
                return null;
            }

            if (mutant != Mutant.SkipsThePartlyHeldAnchor
                && store.Plan(new ArchiveWrite(write.Segment, write.Token, write.Commands[..held], write.Superseded)) is var part
                && part != prefix!.Token)
            {
                return Broken(write, $"is held through command {held}, at {part}, but the prefix's token is {prefix.Token}");
            }

            _expected = store.Plan(write);
            _sawNotWhollyHeld = mutant != Mutant.HoldsAfterAPartlyHeldWrite;
            return null;
        }

        private ArchiveCorruptException? CheckNoneHeld(in ArchiveRow defining, ArchiveWrite write, RowVersion? previous)
        {
            var from = _expected ?? prefix?.Token ?? default;
            if (write.Token != from)
            {
                return Broken(write, $"stores token {write.Token}, not {from}, which its key's chain gives");
            }

            if (previous is { } before && (defining.Version.Count == 0 || new RowVersion(defining.Version.Base, defining.Version.Count - 1) != before))
            {
                return Broken(write, $"has its defining row at ({defining.Tag.Ledger}, {defining.Tag.Slot}, {defining.Tag.Index}) reach version {defining.Version}, which does not follow its key's previous row's {before}");
            }

            _expected = store.Plan(write);
            _sawNotWhollyHeld = true;
            return null;
        }
    }
}

/// <summary>Empty immutable <c>Through</c> map for history reads with no listed live ledgers.</summary>
internal sealed class EmptyLedgerThrough : IReadOnlyDictionary<LedgerId, Slot>
{
    public static EmptyLedgerThrough Instance { get; } = new();

    private EmptyLedgerThrough()
    {
    }

    public int Count => 0;

    public IEnumerable<LedgerId> Keys => [];

    public IEnumerable<Slot> Values => [];

    public Slot this[LedgerId key] => throw new KeyNotFoundException($"Ledger {key} is not in this read's Through map.");

    public bool ContainsKey(LedgerId key) => false;

    public bool TryGetValue(LedgerId key, out Slot value)
    {
        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<LedgerId, Slot>> GetEnumerator()
    {
        yield break;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Single-entry immutable <c>Through</c> map for the common single open-ledger history read.</summary>
internal sealed class SingleLedgerThrough(LedgerId ledger, Slot end) : IReadOnlyDictionary<LedgerId, Slot>
{
    public LedgerId Ledger { get; } = ledger;

    public Slot End { get; } = end;

    public int Count => 1;

    public IEnumerable<LedgerId> Keys
    {
        get
        {
            yield return Ledger;
        }
    }

    public IEnumerable<Slot> Values
    {
        get
        {
            yield return End;
        }
    }

    public Slot this[LedgerId key] =>
        key == Ledger ? End : throw new KeyNotFoundException($"Ledger {key} is not in this read's Through map.");

    public bool ContainsKey(LedgerId key) => key == Ledger;

    public bool TryGetValue(LedgerId key, [MaybeNullWhen(false)] out Slot value)
    {
        if (key == Ledger)
        {
            value = End;
            return true;
        }

        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<LedgerId, Slot>> GetEnumerator()
    {
        yield return new KeyValuePair<LedgerId, Slot>(Ledger, End);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
