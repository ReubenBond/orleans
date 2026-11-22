using System.Collections.Concurrent;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// CsCheck properties of the commit rule over generated histories from bootstrap (ledgers.md §2.6, §10): every verdict
/// agrees with <see cref="LedgerMapReference"/>, a batch's fold equals deciding its changes in submission order, every
/// value written keeps LG-I1 to LG-I3, LG-I10, and LG-I11, rules which fail keep failing, and goals which a drop or a
/// floor raise reaches stay reached; and the retirement gate is monotone: once a value has a floor at or above a node's
/// retirement and names the node nowhere, no later value names it, a property three mutants of the rule fail.
/// </summary>
public sealed class LedgerMapRulesPropertyTests : IDisposable
{
    private const int MaxLength = int.MaxValue;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Decides one change against one value: its verdict's kind, a defect's reason, and the value applying leaves.</summary>
    private delegate LedgerMapReference.Verdict Decider(LedgerMapValue value, LedgerMapChange change);

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void EveryVerdictAgreesWithTheReferenceAndEveryWrittenValueKeepsTheInvariants() =>
        LedgerMapSamples.Steps.Sample(static steps => _ = Run(steps, Real, check: true));

    /// <summary>
    /// For a change derived from a value as its submitter derives it, a rule which fails against a later value fails
    /// against every value after it; a drop's or a floor raise's goal, once present, stays present; and an install's goal
    /// turns absent only once every ledger it opened has left.
    /// </summary>
    [Fact]
    public void RulesWhichFailKeepFailingAndGoalsStay() =>
        LedgerMapSamples.Steps.Sample(static steps =>
        {
            var (world, derived) = Run(steps, Real, check: false);
            foreach (var change in derived.Where(static change => !change.Defective))
            {
                var kinds = world.History.Skip(change.From).Select(value => LedgerMapReference.Decide(value, change.Change, MaxLength).Kind).ToArray();
                var failed = Array.IndexOf(kinds, "rule-fails");
                if (failed >= 0)
                {
                    Assert.All(kinds[failed..], static kind => Assert.Equal("rule-fails", kind));
                }

                var reached = Array.IndexOf(kinds, "goal");
                if (reached >= 0 && change.Change.Value is not LedgerMapChange.Install)
                {
                    Assert.All(kinds[reached..], static kind => Assert.Equal("goal", kind));
                }
            }
        });

    /// <summary>
    /// The gate-monotonicity property, whose generator is biased toward a retirement followed by a stale install, with
    /// <c>classify</c> reporting how often a stale install meets an open gate.
    /// </summary>
    [Fact]
    public void ARetiredNodeAbsentFromAValueAtOrAboveItsFloorStaysAbsent()
    {
        var labels = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        LedgerMapSamples.Steps.Sample(
            steps =>
            {
                var (world, label) = RunWithLabel(steps, Real);
                Assert.True(GateHolds(world));
                _ = labels.AddOrUpdate(label, 1, static (_, count) => count + 1);
                return label;
            },
            TestDiagnostics.WriteLine,
            iter: 2_000);
        Assert.True(labels.GetValueOrDefault("stale-meets-open-gate") > 0, "No stale install met an open gate.");
    }

    public static TheoryData<string> Mutants => ["raise-ignored", "floor-lowered", "chosen-at-unchecked"];

    /// <summary>Each mutant of the rule lets a retired node back into the map after its gate opened.</summary>
    [Theory]
    [MemberData(nameof(Mutants))]
    public void EachMutantOfTheFloorBreaksTheGate(string mutant)
    {
        Decider decide = mutant switch
        {
            "raise-ignored" => static (value, change) => change.Value is LedgerMapChange.RaiseFloor ? new("goal") : Real(value, change),
            "floor-lowered" => static (value, change) => change.Value is LedgerMapChange.RaiseFloor raise && raise.Revision != value.RetirementFloor
                ? new("applies", Result: new LedgerMapValue(value.Epoch, value.NextLedgerId, raise.Revision, value.Ledgers))
                : Real(value, change),
            "chosen-at-unchecked" => static (value, change) => change.Value is LedgerMapChange.Install
                && Real(new LedgerMapValue(value.Epoch, value.NextLedgerId, 0, value.Ledgers), change) is { Kind: "applies", Result: { } result }
                ? new("applies", Result: new LedgerMapValue(value.Epoch, result.NextLedgerId, value.RetirementFloor, result.Ledgers))
                : Real(value, change),
            _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
        };

        _ = Assert.ThrowsAny<Exception>(() => LedgerMapSamples.Steps.Sample(steps => GateHolds(RunWithLabel(steps, decide).World), iter: 5_000));
    }

    /// <summary>Decides <paramref name="change"/> as the committer's fold does, materializing its result.</summary>
    private static LedgerMapReference.Verdict Real(LedgerMapValue value, LedgerMapChange change)
    {
        var (result, verdicts, _) = LedgerMapRules.Fold(value, [change], MaxLength);
        var verdict = Assert.Single(verdicts);
        Assert.Equal(LedgerMapReference.KindOf(verdict), LedgerMapReference.KindOf(LedgerMapRules.Decide(value, change, MaxLength)));
        return new(LedgerMapReference.KindOf(verdict), (verdict.Value as LedgerMapFoldVerdict.Defect)?.Reason, verdict.Value is LedgerMapFoldVerdict.Applies ? result : null);
    }

    private static (LedgerMapWorld World, string Label) RunWithLabel(LedgerMapStep[] steps, Decider decide)
    {
        var label = "no-stale";
        var (world, _) = Run(steps, decide, check: false, (value, change, world) =>
        {
            if (change.Value is not LedgerMapChange.Install install || install.ChosenAt >= value.RetirementFloor)
            {
                return;
            }

            label = install.Opens.SelectMany(static open => open.Replicas).Any(node => GateOpen(world, value, node)) ? "stale-meets-open-gate" : label == "no-stale" ? "stale" : label;
        });
        return (world, label);
    }

    /// <summary>
    /// Runs <paramref name="steps"/>: derives each change, folds each batch over the register's value with
    /// <paramref name="decide"/>, and writes the result when a change applied. With <paramref name="check"/>, it holds
    /// each verdict to the reference, the fold to deciding in order, and each written value to the invariants.
    /// </summary>
    private static (LedgerMapWorld World, List<LedgerMapDerived> Derived) Run(
        LedgerMapStep[] steps,
        Decider decide,
        bool check,
        Action<LedgerMapValue, LedgerMapChange, LedgerMapWorld>? observe = null)
    {
        var world = new LedgerMapWorld();
        var derived = new List<LedgerMapDerived>();
        var batch = new List<LedgerMapChange>();
        for (var index = 0; index < steps.Length; index++)
        {
            if (world.Derive(steps[index]) is { } change)
            {
                derived.Add(change);
                batch.Add(change.Change);
            }

            if (batch.Count > 0 && (steps[index].EndsBatch || index == steps.Length - 1))
            {
                Write(world, batch, decide, check, observe);
                batch.Clear();
            }
        }

        return (world, derived);
    }

    private static void Write(LedgerMapWorld world, List<LedgerMapChange> batch, Decider decide, bool check, Action<LedgerMapValue, LedgerMapChange, LedgerMapWorld>? observe)
    {
        var previous = world.History[^1];
        var value = previous;
        var verdicts = new List<LedgerMapReference.Verdict>();
        foreach (var change in batch)
        {
            observe?.Invoke(value, change, world);
            var verdict = decide(value, change);
            if (check)
            {
                var expected = LedgerMapReference.Decide(value, change, MaxLength);
                Assert.Equal((expected.Kind, expected.Reason), (verdict.Kind, verdict.Reason));
                if (verdict.Result is { } result)
                {
                    Assert.Equal(LedgerMapSamples.Describe(expected.Result!), LedgerMapSamples.Describe(result));
                }
            }

            verdicts.Add(verdict);
            value = verdict.Result ?? value;
        }

        var (folded, foldVerdicts, applied) = LedgerMapRules.Fold(previous, batch, MaxLength);
        if (check)
        {
            Assert.Equal(LedgerMapSamples.Describe(value), LedgerMapSamples.Describe(folded));
            Assert.Equal(verdicts.Select(static verdict => verdict.Kind), foldVerdicts.Select(LedgerMapReference.KindOf));
        }

        if (!ReferenceEquals(value, previous))
        {
            var written = new LedgerMapValue(previous.Epoch + 1, value.NextLedgerId, value.RetirementFloor, value.Ledgers);
            if (check)
            {
                Assert.Null(LedgerMapRules.CheckInvariants(previous, written, applied));
                Assert.Null(LedgerMapReference.Violation(previous, written));
            }

            world.History.Add(written);
        }

        // Each raise answered applied names the value the batch's answers name: the written one, or the register's.
        for (var index = 0; index < batch.Count; index++)
        {
            if (batch[index].Value is LedgerMapChange.RaiseFloor raise && verdicts[index].Kind is "goal" or "applies")
            {
                world.RaisesApplied.Add((raise.Revision, world.History.Count - 1));
            }
        }
    }

    /// <summary>Returns whether <paramref name="node"/>'s gate is open in <paramref name="value"/>: retired, covered by the floor, and named nowhere.</summary>
    private static bool GateOpen(LedgerMapWorld world, LedgerMapValue value, NodeId node) =>
        world.Retired.Any(retired => LedgerMapWorld.Node(retired.Key) == node && value.RetirementFloor >= retired.Value) && !LedgerMapWorld.Names(value, node);

    /// <summary>
    /// Returns whether no value names a retired node after its gate opened: at a value whose floor covers its retirement,
    /// or which a floor raise to its retirement or above was answered applied at, which names it nowhere.
    /// </summary>
    private static bool GateHolds(LedgerMapWorld world)
    {
        foreach (var (number, retired) in world.Retired)
        {
            var node = LedgerMapWorld.Node(number);
            var opened = Enumerable.Range(0, world.History.Count)
                .Where(index => !LedgerMapWorld.Names(world.History[index], node)
                    && (world.History[index].RetirementFloor >= retired || world.RaisesApplied.Contains((retired, index))))
                .DefaultIfEmpty(-1)
                .Min();
            if (opened >= 0 && world.History.Skip(opened).Any(value => LedgerMapWorld.Names(value, node)))
            {
                return false;
            }
        }

        return true;
    }
}
