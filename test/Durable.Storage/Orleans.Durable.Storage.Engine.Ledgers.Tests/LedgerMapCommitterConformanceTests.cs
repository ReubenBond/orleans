using System.Collections.Concurrent;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The committer's conformance (ledgers.md §10): over generated histories, two committers share one scripted register,
/// each change derived as its submitter derives it and submitted to either, concurrently within a round, while writes
/// meet scripted outcomes: answered, applied and answered mismatched, failed after applying, failed without applying,
/// or failed and landed later, before the next round's reads or never. Some histories run under a value limit of a few
/// ledgers, so that installs are refused, and some batches then fence the register. <see cref="LedgerMapHistoryOracle"/> judges every answer against the
/// register's values since its submission, and every value written keeps the invariants.
/// </summary>
public sealed class LedgerMapCommitterConformanceTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private enum Fault
    {
        None,
        Answer,
        AppliedMismatch,
        FailApplied,
        FailUnapplied,
        LandLater,
    }

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void EveryAnswerOfTwoCommittersOnOneRegisterHoldsInItsHistory()
    {
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var appliedMismatches = 0;
        var fences = 0;
        var limits = Gen.Frequency((1, Gen.Const(int.MaxValue)), (1, Gen.Int[2, 5].Select(static ledgers => LedgerMapValue.HeaderLength + LedgerMapValue.CrcLength + ledgers * 100)));
        Gen.Select(LedgerMapSamples.Steps, Gen.Enum<Fault>().Array[40], Gen.Int[0, 1].Array[40], limits).Sample(
            sample =>
            {
                var (steps, faults, committers, limit) = sample;
                HashSet<string> kinds = [];
                _leaks.InScopeAsync(async () =>
                {
                    (kinds, var mismatches, var fenced) = await RunAsync(steps, faults, committers, limit);
                    _ = Interlocked.Add(ref appliedMismatches, mismatches);
                    _ = Interlocked.Add(ref fences, fenced);
                }).GetAwaiter().GetResult();
                foreach (var kind in kinds)
                {
                    _ = outcomes.AddOrUpdate(kind, 1, static (_, count) => count + 1);
                }

                return kinds.Count == 0 ? "none" : string.Join(",", kinds.Order(StringComparer.Ordinal));
            },
            TestDiagnostics.WriteLine,
            iter: 300);

        foreach (var kind in new[] { "Applied", "Superseded", "Rejected", "Refused", "unknown" })
        {
            Assert.True(outcomes.ContainsKey(kind), $"No submission was answered {kind}.");
        }

        Assert.True(appliedMismatches > 0, "No write applied and answered a mismatch.");
        Assert.True(fences > 0, "No batch fenced the register.");
    }

    private static async Task<(HashSet<string> Kinds, int AppliedMismatches, int Fences)> RunAsync(LedgerMapStep[] steps, Fault[] faults, int[] committers, int limit)
    {
        var register = new ScriptedConditionalRegister();
        var world = new LedgerMapWorld();
        var submissions = new List<LedgerMapSubmission>();
        var landings = new List<Func<bool>>();
        await using var first = new LedgerMapCommitter(register.Writer(0), limit);
        await using var second = new LedgerMapCommitter(register.Writer(1), limit);
        var round = new List<Task<LedgerMapSubmission>>();
        for (var index = 0; index < steps.Length; index++)
        {
            if (world.Derive(steps[index]) is { } derived)
            {
                if (faults[index % faults.Length] is var fault and not Fault.None)
                {
                    _ = DecideAsync(register.HoldNextWrite(), fault, landings);
                }

                var committer = committers[index % committers.Length];
                round.Add(SubmitAsync(committer == 0 ? first : second, committer, register, derived.Change));
            }

            if (round.Count > 0 && (steps[index].EndsBatch || index == steps.Length - 1))
            {
                submissions.AddRange(await Task.WhenAll(round));
                round.Clear();

                // Some writes whose outcome was unknown land now, after the reads which ended their batches.
                if (steps[index].Choice % 2 == 0)
                {
                    landings.ForEach(static land => land());
                    landings.Clear();
                }

                world.History.Clear();
                world.History.AddRange(History(register).Values);
            }
        }

        var (history, dispatches, writers) = History(register);
        var fences = 0;
        for (var epoch = 1; epoch < history.Count; epoch++)
        {
            Assert.Equal((ulong)epoch, history[epoch].Epoch);
            Assert.Null(LedgerMapReference.Violation(history[epoch - 1], history[epoch]));
            fences += IsFence(history[epoch - 1], history[epoch]) ? 1 : 0;
        }

        Assert.Empty(LedgerMapHistoryOracle.Violations(history, dispatches, writers, submissions, limit));
        return ([.. submissions.Select(static submission => submission.Outcome?.Value?.GetType().Name ?? "unknown")], register.AppliedMismatches, fences);
    }

    /// <summary>Returns whether <paramref name="next"/> is a fence over <paramref name="previous"/>: equal to it but for the epoch.</summary>
    private static bool IsFence(LedgerMapValue previous, LedgerMapValue next) =>
        next.NextLedgerId == previous.NextLedgerId
        && next.RetirementFloor == previous.RetirementFloor
        && next.Ledgers.AsSpan().SequenceEqual(previous.Ledgers.AsSpan());

    private static async Task DecideAsync(ScriptedConditionalRegister.HeldWrite held, Fault fault, List<Func<bool>> landings)
    {
        await held.Arrived;
        switch (fault)
        {
            case Fault.AppliedMismatch:
                held.ApplyAndAnswerMismatch();
                break;
            case Fault.FailApplied:
                held.Fail(apply: true);
                break;
            case Fault.FailUnapplied:
                held.Fail(apply: false);
                break;
            case Fault.LandLater:
                lock (landings)
                {
                    landings.Add(held.FailAndLandLater());
                }

                break;
            case Fault.None:
            case Fault.Answer:
            default:
                held.Answer();
                break;
        }
    }

    private static (List<LedgerMapValue> Values, List<long> Dispatches, List<int> Writers) History(ScriptedConditionalRegister register) =>
        ([LedgerMapValue.Empty, .. register.AppliedWrites.Select(static bytes => LedgerMapSamples.Value(LedgerMapValue.Decode(bytes.AsSpan())))], [-1, .. register.AppliedDispatches], [-1, .. register.AppliedWriters]);

    private static async Task<LedgerMapSubmission> SubmitAsync(LedgerMapCommitter committer, int writer, ScriptedConditionalRegister register, LedgerMapChange change)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        try
        {
            var outcome = await committer.CommitAsync(change, TestContext.Current.CancellationToken);
            return new(change, writer, submittedAt, outcome, AnsweredAt: register.Dispatches);
        }
        catch (IOException failure)
        {
            return new(change, writer, submittedAt, null, failure, register.Dispatches);
        }
    }
}
