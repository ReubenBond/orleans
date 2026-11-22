using System.Buffers;
using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The ledger map committer's conformance (ledgers.md §10) under the simulation's scheduler: CsCheck's generated
/// histories, as <c>LedgerMapCommitterConformanceTests</c> draws them, each run in a simulation of its own, whose rounds of
/// concurrent submissions are explored windows (verification.md §4.1). Two committers share one scripted register; each
/// change is derived as its submitter derives it and submitted to either; writes meet scripted outcomes: answered,
/// applied and answered mismatched, failed after applying, failed without applying, or failed and landed later.
/// <see cref="LedgerMapHistoryOracle"/> judges every answer against the register's values since its submission, and
/// every value written keeps the map's invariants. The explorer chooses how each round's submissions, the batches they
/// join, and the register's decisions interleave.
/// </summary>
public sealed class SimulatedLedgerMapCommitterConformanceTests
{
    private const int MaxSteps = 12;

    /// <summary>The most generated histories the mutant's test explores.</summary>
    private const int MutantHistories = 40;

    /// <summary>The seed of the fixed set of histories whose answers cover every kind of outcome.</summary>
    private const uint CoverageSeed = 1;

    /// <summary>The size of the fixed set of histories.</summary>
    private const int CoverageHistories = 12;

    private enum Fault
    {
        None,
        Answer,
        AppliedMismatch,
        FailApplied,
        FailUnapplied,
        LandLater,
    }

    private static Gen<History> Histories { get; } =
        Gen.Select(
                Gen.Select(Gen.Int[0, 99], Gen.Int.NonNegative, Gen.Int[0, 99], Gen.Int[0, 2])
                    .Select(static (kind, choice, staleness, ends) => new LedgerMapStep(kind, choice, staleness, ends != 0))
                    .Array[1, MaxSteps],
                Gen.Enum<Fault>().Array[MaxSteps],
                Gen.Int[0, 1].Array[MaxSteps],
                Gen.Frequency((1, Gen.Const(int.MaxValue)), (1, Gen.Int[2, 5].Select(static ledgers => LedgerMapValue.HeaderLength + LedgerMapValue.CrcLength + ledgers * 100))),
                Gen.Int.NonNegative)
            .Select(static (steps, faults, committers, limit, seed) => new History(steps, faults, committers, limit, seed));

    /// <summary>
    /// Every history of a fixed set, drawn from one seed, holds under every schedule of its rounds within the bound, and
    /// the set's answers cover every kind of outcome. The set is fixed, so the coverage holds on every run (#373): drawn
    /// from <see cref="CoverageSeed"/>, its 10th history is the first answered <c>Refused</c>, and its 4th the first
    /// answered <c>Rejected</c>.
    /// </summary>
    [Fact]
    public void EveryAnswerHoldsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var pcg = new PCG(CoverageSeed, CoverageSeed);
        HashSet<string> kinds = [];
        for (var index = 0; index < CoverageHistories; index++)
        {
            kinds.UnionWith(Check(conformance, Histories.Generate(pcg, null, out _), mutant: false));
        }

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
        foreach (var kind in new[] { "Applied", "Superseded", "Rejected", "Refused", "unknown" })
        {
            Assert.True(kinds.Contains(kind), $"No submission was answered {kind}.");
        }
    }
    /// <summary>
    /// Two committers whose register writers check the version in one step, write in a later one, and answer applied
    /// from their check (<see cref="ChecksVersionBeforeWritingRegister"/>) answer a change applied whose write another
    /// committer's write superseded between the two steps, so the change never reaches the register. The first generated
    /// history with a round of concurrent submissions to both committers catches it, by one preemption; its default
    /// schedule, which runs each submission on until it waits, passes.
    /// </summary>
    [Fact]
    public void ExploredSchedulesCatchARegisterWriterWhichAnswersFromAnEarlierCheck()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Gate);
        var pcg = new PCG(1, 1);
        var histories = 0;
        History? caught = null;
        while (caught is null && histories < MutantHistories)
        {
            histories++;
            var history = Histories.Generate(pcg, null, out _);
            try
            {
                _ = Check(conformance, history, mutant: true);
            }
            catch (ConformanceScheduleException)
            {
                caught = history;
            }
        }

        Assert.NotNull(caught);
        var found = conformance.Explorations[^1];
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        // The same history under its seeded schedule, one simulation per seed, catches the mutant only on some seeds.
        const int Seeds = 40;
        var seeded = new SimulatedConformance();
        var caughtSeeded = 0;
        for (var seed = 1; seed <= Seeds; seed++)
        {
            try
            {
                _ = Check(seeded, caught with { Seed = seed }, mutant: true);
            }
            catch (ConformanceScheduleException)
            {
                caughtSeeded++;
            }
        }

        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The check-then-write register mutant: explored, caught in history {histories} ({caught}) at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caughtSeeded} of {Seeds} seeds."));
    }
    /// <summary>
    /// Runs <paramref name="history"/> in a simulation of its own, explored or under its seeded schedule as
    /// <paramref name="conformance"/> does, and returns the kinds of answer its submissions received; its committers' register writers are
    /// <see cref="ChecksVersionBeforeWritingRegister"/>s when <paramref name="mutant"/> is set.
    /// </summary>
    private static HashSet<string> Check(SimulatedConformance conformance, History history, bool mutant)
    {
        HashSet<string> kinds = [];
        conformance.RunCaseAsync(
                new ConformanceCase([], history.ToString(), history.Seed),
                async () => kinds = await RunAsync(conformance, history, mutant))
            .GetAwaiter()
            .GetResult();
        return kinds;
    }

    private static async Task<HashSet<string>> RunAsync(SimulatedConformance conformance, History history, bool mutant)
    {
        var register = new ScriptedConditionalRegister();
        var world = new LedgerMapWorld();
        var submissions = new List<LedgerMapSubmission>();
        var landings = new List<Func<bool>>();
        IConditionalRegister Writer(int writer) => mutant ? new ChecksVersionBeforeWritingRegister(register.Writer(writer)) : register.Writer(writer);
        await using var first = new LedgerMapCommitter(Writer(0), history.Limit);
        await using var second = new LedgerMapCommitter(Writer(1), history.Limit);
        var round = new List<Func<Task<LedgerMapSubmission>>>();
        for (var index = 0; index < history.Steps.Length; index++)
        {
            var step = history.Steps[index];
            if (world.Derive(step) is { } derived)
            {
                if (history.Faults[index] is var fault and not Fault.None)
                {
                    register.HoldNextWrite().DecideOnArrival(held => Decide(held, fault, landings));
                }

                var committer = history.Committers[index];
                var change = derived.Change;
                round.Add(() => SubmitAsync(committer == 0 ? first : second, committer, register, change));
            }

            if (round.Count > 0 && (step.EndsBatch || index == history.Steps.Length - 1))
            {
                submissions.AddRange(await RunRoundAsync(conformance, round));
                round.Clear();

                // Some writes whose outcome was unknown land now, after the reads which ended their batches.
                if (step.Choice % 2 == 0)
                {
                    landings.ForEach(static land => land());
                    landings.Clear();
                }

                world.History.Clear();
                world.History.AddRange(Applied(register).Values);
            }
        }

        var (values, dispatches, writers) = Applied(register);
        for (var epoch = 1; epoch < values.Count; epoch++)
        {
            Assert.Equal((ulong)epoch, values[epoch].Epoch);
            Assert.Null(LedgerMapReference.Violation(values[epoch - 1], values[epoch]));
        }

        Assert.Empty(LedgerMapHistoryOracle.Violations(values, dispatches, writers, submissions, history.Limit));
        return [.. submissions.Select(static submission => submission.Outcome?.Value?.GetType().Name ?? "unknown")];
    }

    /// <summary>
    /// Runs one round's submissions as a concurrent group, each started as an operation of the simulation's scheduler, in
    /// an explored window when it holds more than one.
    /// </summary>
    private static async Task<List<LedgerMapSubmission>> RunRoundAsync(SimulatedConformance conformance, List<Func<Task<LedgerMapSubmission>>> round)
    {
        var concurrent = round.Count > 1;
        if (concurrent)
        {
            conformance.GroupStarting();
        }

        var started = round.Select(submit => conformance.Start(async () => await submit())).ToList();
        var answers = new List<LedgerMapSubmission>(started.Count);
        foreach (var answer in started)
        {
            answers.Add((LedgerMapSubmission)await answer);
        }

        if (concurrent)
        {
            conformance.GroupCompleted();
        }

        return answers;
    }

    /// <summary>
    /// Decides a held write as it arrives; the register applies the decision a scheduler step later.
    /// </summary>
    private static void Decide(ScriptedConditionalRegister.HeldWrite held, Fault fault, List<Func<bool>> landings)
    {
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
                landings.Add(held.FailAndLandLater());
                break;
            case Fault.None:
            case Fault.Answer:
            default:
                held.Answer();
                break;
        }
    }

    private static (List<LedgerMapValue> Values, List<long> Dispatches, List<int> Writers) Applied(ScriptedConditionalRegister register) =>
        ([LedgerMapValue.Empty, .. register.AppliedWrites.Select(static bytes => Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(bytes.AsSpan()).Value))], [-1, .. register.AppliedDispatches], [-1, .. register.AppliedWriters]);

    private static async Task<LedgerMapSubmission> SubmitAsync(LedgerMapCommitter committer, int writer, ScriptedConditionalRegister register, LedgerMapChange change)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        try
        {
            var outcome = await committer.CommitAsync(change);
            return new(change, writer, submittedAt, outcome, AnsweredAt: register.Dispatches);
        }
        catch (IOException failure)
        {
            return new(change, writer, submittedAt, null, failure, register.Dispatches);
        }
    }

    /// <summary>
    /// A generated history: its steps, the fault each derived step's write meets, the committer each submits to, the
    /// value limit, and the simulation's seed.
    /// </summary>
    private sealed record History(LedgerMapStep[] Steps, Fault[] Faults, int[] Committers, int Limit, int Seed)
    {
        public override string ToString() =>
            FormattableString.Invariant($"{Steps.Length} steps, faults [{string.Join(", ", Faults.Take(Steps.Length))}], limit {Limit}, seed {Seed}");
    }

    /// <summary>
    /// A race mutant of a register writer: a write reads the register's version first and answers a mismatch when it
    /// differs; otherwise it writes a scheduler step later and answers applied, whatever the write answers, with a
    /// version of its own (MS-I5). Another writer's write between the two steps supersedes it, and its change never
    /// reaches the register.
    /// </summary>
    private sealed class ChecksVersionBeforeWritingRegister(IConditionalRegister inner) : IConditionalRegister
    {
        private int _unapplied;

        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);

        public async ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default)
        {
            bool matches;
            using (var current = await inner.ReadAsync(cancellationToken))
            {
                matches = expectedVersion is null ? current is null : current?.Version == expectedVersion;
            }

            if (!matches)
            {
                return RegisterWriteOutcome.Mismatch.Instance;
            }

            var content = value.ToArray();
            await SimulationTasks.Yield();
            var written = await inner.WriteAsync(expectedVersion, new ReadOnlySequence<byte>(content), cancellationToken);
            return written.Value is RegisterWriteOutcome.Mismatch
                ? new RegisterWriteOutcome.Applied(new MetaStoreVersion(FormattableString.Invariant($"unapplied-{++_unapplied}")))
                : written;
        }
    }
}
