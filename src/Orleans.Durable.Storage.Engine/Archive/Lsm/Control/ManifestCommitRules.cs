using System.Collections.Immutable;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// The commit rules, goals, and effects of manifest changes (archive §2.9), and the fold of a batch over a manifest value.
/// </summary>
/// <remarks>
/// A change is derived from a manifest value its worker read (archive §3): a drain starts from the watermark that value
/// records, and a compaction's inputs are sections it lists. The manifest only advances past that value: watermarks and
/// <c>DrainedBelow</c> never decrease, a complete ledger stays complete, and a section, once removed, never returns
/// (LSM-I1, LSM-I7). So once a rule fails against a value at or after it, it fails against every later one, and a drain's
/// goal, once present, stays present. A fold tracks the sections its batch removes and adds, each looked up by hash or by
/// a binary search of the value's sections, and merges them into the value's sections once, so a batch of <c>B</c>
/// changes adding <c>A</c> sections to a manifest of <c>N</c> folds in <c>O(N + A log A)</c> beyond its lookups.
/// </remarks>
internal static class ManifestCommitRules
{
    /// <summary>
    /// Returns whether <paramref name="value"/> holds <paramref name="change"/>'s goal: for a drain of <c>S</c> to
    /// <c>b</c>, <c>S</c> lies below <c>DrainedBelow</c>, or its watermark is at least <c>b</c> and, when the drain is
    /// complete, <c>S</c> is marked complete; for a compaction, every output is listed, or, when it has none, no input is.
    /// </summary>
    public static bool GoalHolds(ManifestValue value, ManifestChange change) => GoalHolds(new Folding(value), change);

    /// <summary>
    /// Returns whether <paramref name="change"/>'s commit rule holds against <paramref name="value"/>: for a drain of
    /// <c>S</c> from <c>a</c>, <c>S</c> is at or above <c>DrainedBelow</c>, not complete, and its watermark is <c>a</c>
    /// (LSM-I1), so no drain makes a complete ledger incomplete again; for a compaction, every input is listed (LSM-I5).
    /// </summary>
    public static bool RuleHolds(ManifestValue value, ManifestChange change) => RuleHolds(new Folding(value), change);

    /// <summary>
    /// Returns whether <paramref name="condition"/>, the reference condition of a change's output (archive §8.5), is false
    /// in <paramref name="value"/>, and so in every later value: for a drain of <c>S</c> from <c>a</c>, <c>S</c> lies below
    /// <c>DrainedBelow</c>, is complete, or its watermark passed <c>a</c> (LSM-I1); for a compaction, some input is no
    /// longer listed (LSM-I7). The commit rule of the change the output belongs to then fails for good, and no commit
    /// references the output, on which the sweeper's deletions rest (LSM-I6).
    /// </summary>
    public static bool ConditionIsFalse(ManifestValue value, RunObjectCondition condition)
    {
        ArgumentNullException.ThrowIfNull(value);
        return condition switch
        {
            RunObjectCondition.Drain drain => value.IsComplete(drain.Ledger) || value.RecordedWatermark(drain.Ledger) > drain.From,
            RunObjectCondition.Compaction compaction => !compaction.Inputs.All(value.Lists),
            null => throw new UnreachableException("A reference condition holds its case."),
        };
    }

    /// <summary>
    /// Returns the reference condition of <paramref name="change"/>'s output, which its trailer records: a drain's ledger
    /// and start, or a compaction's inputs by position, in order.
    /// </summary>
    public static RunObjectCondition ConditionOf(ManifestChange change) => change switch
    {
        ManifestChange.Drain drain => new RunObjectCondition.Drain(drain.Ledger, drain.From),
        ManifestChange.Compaction compaction => new RunObjectCondition.Compaction(
            [.. compaction.Inputs.Select(static input => new SectionPosition(input.Object, input.Offset)).Order()]),
        null => throw new UnreachableException("A manifest change holds its case."),
    };

    /// <summary>
    /// Returns <paramref name="value"/> with <paramref name="change"/> applied, at the same epoch. A drain adds its
    /// sections, sets its ledger's watermark, marks the ledger complete when it is, and advances <c>DrainedBelow</c> past
    /// every complete ledger contiguous from it, removing their entries; a compaction removes its inputs and adds its
    /// outputs.
    /// </summary>
    /// <exception cref="InvalidOperationException">The change's rule does not hold against the value.</exception>
    public static ManifestValue Apply(ManifestValue value, ManifestChange change)
    {
        var folding = new Folding(value);
        if (!RuleHolds(folding, change))
        {
            throw new InvalidOperationException("A change applies only where its commit rule holds.");
        }

        folding.Apply(change);
        return folding.ToValue();
    }

    /// <summary>
    /// Folds <paramref name="batch"/>, in submission order, over <paramref name="value"/>: each change whose goal is
    /// absent and whose rule holds against the result of the changes before it applies to that result.
    /// </summary>
    /// <remarks>
    /// Every change derives from a value at or before <paramref name="value"/>, so applying one change never makes
    /// another's rule hold, and the result holds each change's goal or fails its rule: no change waits on a later one of
    /// its batch.
    /// </remarks>
    /// <returns>
    /// The result, at <paramref name="value"/>'s epoch and <paramref name="value"/> itself when nothing applies, and for
    /// each change whether it applied, and so whether the write of the result carries it.
    /// </returns>
    /// <exception cref="ArgumentException">A change adds a section at a position the value lists (LSM-I7).</exception>
    public static (ManifestValue Result, bool[] Carried) Fold(ManifestValue value, IReadOnlyList<ManifestChange> batch) =>
        FoldCore(value, batch, staleDrain: false);

    /// <summary>
    /// Folds <paramref name="batch"/> over <paramref name="value"/>; when <paramref name="staleDrain"/> is set, a drain
    /// of an incomplete ledger skips its watermark check, as the model's <c>stale-drain</c> mutant does (LSM-I1).
    /// </summary>
    internal static (ManifestValue Result, bool[] Carried) FoldCore(ManifestValue value, IReadOnlyList<ManifestChange> batch, bool staleDrain)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(batch);
        var folding = new Folding(value);
        var carried = new bool[batch.Count];
        for (var index = 0; index < batch.Count; index++)
        {
            carried[index] = staleDrain && batch[index] is ManifestChange.Drain drain
                ? drain.Ledger >= folding.DrainedBelow && !folding.IsComplete(drain.Ledger) && !drain.Sections.Any(folding.Contains)
                : !GoalHolds(folding, batch[index]) && RuleHolds(folding, batch[index]);
            if (carried[index])
            {
                Debug.Assert(!Adds(batch[index]).Any(folding.Contains), "A change adds sections no manifest has listed (LSM-I7).");
                folding.Apply(batch[index]);
            }
        }

        return (carried.Contains(true) ? folding.ToValue() : value, carried);
    }

    private static bool GoalHolds(Folding value, ManifestChange change) => change switch
    {
        ManifestChange.Drain drain => drain.Ledger < value.DrainedBelow
            || value.RecordedWatermark(drain.Ledger) >= drain.Below && (!drain.Complete || value.IsComplete(drain.Ledger)),
        ManifestChange.Compaction compaction => compaction.Outputs.IsEmpty
            ? !compaction.Inputs.Any(value.Contains)
            : compaction.Outputs.All(value.Contains),
        null => throw new UnreachableException(),
    };

    private static bool RuleHolds(Folding value, ManifestChange change) => change switch
    {
        ManifestChange.Drain drain => drain.Ledger >= value.DrainedBelow && !value.IsComplete(drain.Ledger) && value.RecordedWatermark(drain.Ledger) == drain.From,
        ManifestChange.Compaction compaction => compaction.Inputs.All(value.Contains),
        null => throw new UnreachableException(),
    };

    private static ImmutableArray<SectionRef> Adds(ManifestChange change) => change switch
    {
        ManifestChange.Drain drain => drain.Sections,
        ManifestChange.Compaction compaction => compaction.Outputs,
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// A manifest value with the changes of a batch applied so far: its <c>DrainedBelow</c> and watermarks, and the
    /// sections the changes removed and added, which <see cref="ToValue"/> merges into the value's sections once.
    /// </summary>
    private sealed class Folding(ManifestValue value)
    {
        private HashSet<SectionRef>? _removed;
        private List<SectionRef>? _added;
        private HashSet<SectionRef>? _addedSet;

        public LedgerId DrainedBelow { get; private set; } = value.DrainedBelow;

        public ImmutableSortedDictionary<LedgerId, ManifestWatermark> Watermarks { get; private set; } = value.Watermarks;

        public bool IsComplete(LedgerId ledger) => ledger < DrainedBelow || Watermarks.TryGetValue(ledger, out var watermark) && watermark.Complete;

        public Slot RecordedWatermark(LedgerId ledger) => Watermarks.TryGetValue(ledger, out var watermark) ? watermark.Below : Slot.Zero;

        public bool Contains(SectionRef section) =>
            _removed?.Contains(section) != true && (value.Contains(section) || _addedSet?.Contains(section) == true);

        public void Apply(ManifestChange change)
        {
            if (change.Value is ManifestChange.Drain drain)
            {
                ApplyDrain(drain);
            }
            else if (change.Value is ManifestChange.Compaction compaction)
            {
                (_removed ??= []).UnionWith(compaction.Inputs);
                Add(compaction.Outputs);
            }
            else
            {
                throw new UnreachableException();
            }
        }

        /// <summary>
        /// Returns the value the changes applied make, at the value's epoch: its sections less those removed, merged in
        /// one pass with those added, in position order.
        /// </summary>
        /// <exception cref="ArgumentException">An added section lies at a position the result lists twice (LSM-I7).</exception>
        public ManifestValue ToValue()
        {
            SectionRef[] added = [.. (_added ?? []).Where(section => _removed?.Contains(section) != true).Order(SectionRef.PositionOrder)];
            var merged = ImmutableArray.CreateBuilder<SectionRef>(value.Sections.Length + added.Length);
            var next = 0;
            foreach (var section in value.Sections)
            {
                if (_removed?.Contains(section) == true)
                {
                    continue;
                }

                while (next < added.Length && SectionRef.CompareByPosition(added[next], section) < 0)
                {
                    AddTo(merged, added[next++]);
                }

                if (next < added.Length && SectionRef.CompareByPosition(added[next], section) == 0)
                {
                    throw new ArgumentException("A change adds a section at a position the manifest lists (LSM-I7).", nameof(added));
                }

                merged.Add(section);
            }

            while (next < added.Length)
            {
                AddTo(merged, added[next++]);
            }

            return new ManifestValue(value.Epoch, DrainedBelow, Watermarks, merged.DrainToImmutable());
        }

        private static void AddTo(ImmutableArray<SectionRef>.Builder merged, SectionRef section)
        {
            if (merged.Count > 0 && SectionRef.CompareByPosition(merged[^1], section) >= 0)
            {
                throw new ArgumentException("Two changes add sections at one position (LSM-I7).", nameof(section));
            }

            merged.Add(section);
        }

        private void ApplyDrain(ManifestChange.Drain drain)
        {
            var watermarks = Watermarks.SetItem(drain.Ledger, new ManifestWatermark(drain.Below, drain.Complete));
            var drainedBelow = DrainedBelow;
            while (watermarks.TryGetValue(drainedBelow, out var watermark) && watermark.Complete)
            {
                watermarks = watermarks.Remove(drainedBelow);
                drainedBelow = new LedgerId(checked(drainedBelow.Value + 1));
            }

            (DrainedBelow, Watermarks) = (drainedBelow, watermarks);
            Add(drain.Sections);
        }

        private void Add(ImmutableArray<SectionRef> sections)
        {
            (_added ??= []).AddRange(sections);
            (_addedSet ??= []).UnionWith(sections);
        }
    }
}
