using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.ManifestChanges;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Properties of the commit rules (archive §2.9) over manifest histories which workers' changes make: a change derived
/// from a value, once its rule fails against a value at or after that one, fails against every later one; a drain's goal,
/// once present, stays present; and a batch's fold leaves every change of the batch with its goal or a failed rule, so
/// the committer can answer each from the result.
/// </summary>
public sealed class ManifestCommitRulesTests : IDisposable
{
    private const int Steps = 40;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>
    /// Gets histories: the manifest values a run of committed changes makes, from the absent manifest, with each change
    /// and the index of the value it was derived from, sometimes an older one than the latest.
    /// </summary>
    private static Gen<(List<ManifestValue> Values, List<(ManifestChange Change, int DerivedFrom)> Changes)> Histories { get; } =
        Gen.Int.Select(static seed =>
        {
            var random = new Random(seed);
            List<ManifestValue> values = [ManifestValue.Absent];
            List<(ManifestChange, int)> changes = [];
            for (var step = 0; step < Steps; step++)
            {
                var from = Math.Max(0, values.Count - 1 - random.Next(3));
                var change = ManifestWorkload.Derive(values[from], random);
                changes.Add((change, from));
                var (result, carried) = ManifestCommitRules.Fold(values[^1], [change]);
                if (carried[0])
                {
                    values.Add(new ManifestValue(values[^1].Epoch + 1, result.DrainedBelow, result.Watermarks, result.Sections));
                }
            }

            return (values, changes);
        });

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void AFailedRuleStaysFailedAndADrainsGoalStaysPresent() =>
        Histories.Sample(
            static history =>
            {
                foreach (var (change, derivedFrom) in history.Changes)
                {
                    var failed = false;
                    var reached = false;
                    foreach (var value in history.Values.Skip(derivedFrom))
                    {
                        var rule = ManifestCommitRules.RuleHolds(value, change);
                        Assert.False(failed && rule, $"{change.Value}'s rule failed, then held at epoch {value.Epoch}.");
                        failed |= !rule;

                        if (change.Value is ManifestChange.Drain)
                        {
                            var goal = ManifestCommitRules.GoalHolds(value, change);
                            Assert.False(reached && !goal, $"{change.Value}'s goal was present, then absent at epoch {value.Epoch}.");
                            reached |= goal;
                        }
                    }
                }
            },
            iter: 300);

    /// <summary>
    /// A change's rule never holds where its output's reference condition is false (archive §8.5): the sweeper's
    /// deletions rest on this, since such a condition stays false.
    /// </summary>
    [Fact]
    public void ARuleNeverHoldsWhereItsOutputsConditionIsFalse() =>
        Histories.Sample(
            static history =>
            {
                foreach (var value in history.Values)
                {
                    foreach (var (change, _) in history.Changes)
                    {
                        Assert.False(
                            ManifestCommitRules.RuleHolds(value, change) && ManifestCommitRules.ConditionIsFalse(value, ManifestCommitRules.ConditionOf(change)),
                            $"{change.Value}'s rule holds at epoch {value.Epoch}, where its output's condition is false.");
                    }
                }
            },
            iter: 300);

    [Fact]
    public void AFoldLeavesEveryChangeWithItsGoalOrAFailedRule() =>
        Gen.Select(Histories, Gen.Int).Sample(
            static sample =>
            {
                var ((values, _), seed) = sample;
                var random = new Random(seed);
                var value = values[^1];
                var batch = Enumerable.Range(0, random.Next(1, 7))
                    .Select(_ => ManifestWorkload.Derive(values[Math.Max(0, values.Count - 1 - random.Next(3))], random))
                    .ToList();

                var (result, carried) = ManifestCommitRules.Fold(value, batch);

                Assert.Equal(!result.Equals(value), carried.Contains(true));
                Assert.All(batch, change => Assert.True(
                    ManifestCommitRules.GoalHolds(result, change) || !ManifestCommitRules.RuleHolds(result, change),
                    $"{change.Value} has neither its goal nor a failed rule after the fold."));
            },
            iter: 300);

    /// <summary>
    /// The production rules agree with the reference reading of archive §2.9 on every change and value of generated
    /// histories: each change's goal and rule, and each batch's fold, which applies the batch's changes one at a time.
    /// </summary>
    [Fact]
    public void TheRulesAndTheFoldAgreeWithTheReferenceReading() =>
        Gen.Select(Histories, Gen.Int).Sample(
            static sample =>
            {
                var ((values, changes), seed) = sample;
                foreach (var value in values)
                {
                    var reference = ReferenceManifest.Of(value);
                    foreach (var (change, _) in changes)
                    {
                        Assert.Equal(ManifestReference.Goal(change, reference), ManifestCommitRules.GoalHolds(value, change));
                        Assert.Equal(ManifestReference.Rule(change, reference), ManifestCommitRules.RuleHolds(value, change));
                    }
                }

                var random = new Random(seed);
                var batch = Enumerable.Range(0, random.Next(1, 9))
                    .Select(_ => ManifestWorkload.Derive(values[Math.Max(0, values.Count - 1 - random.Next(3))], random))
                    .ToList();
                var (result, _) = ManifestCommitRules.Fold(values[^1], batch);
                Assert.True(ManifestReference.Fold(batch, ReferenceManifest.Of(values[^1])).Matches(result), "The fold differs from applying the batch's changes one at a time.");
                Assert.Equal(values[^1].Epoch, result.Epoch);
            },
            iter: 300);

    /// <summary>
    /// A drain which copies no rows still marks its ledger complete.
    /// </summary>
    [Fact]
    public void ADrainOfNoRowsMarksItsLedgerComplete()
    {
        var (result, carried) = ManifestCommitRules.Fold(ManifestValue.Absent, [Drain(2, 0, 0, complete: true)]);

        Assert.True(carried[0]);
        Assert.True(result.IsComplete(Ledger(2)));
        Assert.Equal(Ledger(1), result.DrainedBelow);
        _ = Assert.Throws<InvalidOperationException>(() => ManifestCommitRules.Apply(ManifestValue.Absent, Drain(1, 10, 20)));
    }

    /// <summary>
    /// A drain of no slots only completes its ledger: one which leaves it incomplete, or which adds sections, is refused.
    /// </summary>
    [Fact]
    public void ADrainOfNoSlotsCompletesItsLedgerAndAddsNoSections()
    {
        _ = Assert.Throws<ArgumentException>(() => Drain(1, 5, 5));
        _ = Assert.Throws<ArgumentException>(() => Drain(1, 5, 5, true, Section("d/1/5/a")));
        Assert.True(((ManifestChange.Drain)Drain(1, 5, 5, complete: true).Value!).Complete);
    }

    /// <summary>
    /// A complete ledger stays complete (LSM-I1): a drain of it from its recorded watermark fails its rule and applies
    /// nothing.
    /// </summary>
    [Fact]
    public void ADrainOfACompleteLedgerFailsItsRule()
    {
        var (complete, _) = ManifestCommitRules.Fold(ManifestValue.Absent, [Drain(2, 0, 7, complete: true, Section("d/2/0/a"))]);
        var later = Drain(2, 7, 9, false, Section("d/2/7/b"));

        Assert.True(complete.IsComplete(Ledger(2)));
        Assert.False(ManifestCommitRules.RuleHolds(complete, later));
        var (result, carried) = ManifestCommitRules.Fold(complete, [later]);
        Assert.False(carried[0]);
        Assert.True(result.IsComplete(Ledger(2)));
    }

    /// <summary>
    /// A change lists each section position once, and a compaction's outputs lie at none of its inputs' positions, so no
    /// fold lists a position twice (LSM-I7).
    /// </summary>
    [Fact]
    public void AChangeRefusesTwoSectionsAtOnePosition()
    {
        var section = Section("d/1/0/a");
        var moved = new SectionRef(section.Object, section.Offset, 7, section.Partition, section.MinTag, section.MaxTag, section.Tail);

        _ = Assert.Throws<ArgumentException>(() => Drain(1, 0, 10, false, section, moved));
        _ = Assert.Throws<ArgumentException>(() => Compaction([section, moved]));
        _ = Assert.Throws<ArgumentException>(() => Compaction([section], moved));
        Assert.Equal<SectionRef>([Section("d/1/0/a"), Section("d/1/0/b")], ((ManifestChange.Drain)Drain(1, 0, 10, false, Section("d/1/0/b"), Section("d/1/0/a")).Value!).Sections);
    }
}
