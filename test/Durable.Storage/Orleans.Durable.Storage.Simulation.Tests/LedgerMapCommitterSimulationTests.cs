using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Two ledger map committers share a simulated register which injects every fault its contract allows: outages of
/// reads, unknown outcomes which applied, never apply, or land later, and writes which applied and answer a mismatch, as
/// after a backend-SDK retry (ledgers.md §2.6, §10; verification.md §7.2). Submitters derive their changes as executors
/// and controllers do, and <see cref="LedgerMapHistoryOracle"/> judges every answer against the register's values since
/// its submission; every value the register holds keeps the map's invariants.
/// </summary>
public sealed class LedgerMapCommitterSimulationTests
{
    private const int Rounds = 24;

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(1, 9);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryAnswerHoldsInTheRegistersHistoryUnderFaults(int seed) =>
        SeededSimulation.Run(seed, static environment => SimulateAsync(environment, export: true));

    /// <summary>
    /// The committers' simulation replays from its seed and from its artifact.
    /// </summary>
    [Fact]
    public void TheCommittersSimulationReplaysFromItsSeed() =>
        SeededSimulation.RunAndReplay(201, static environment => SimulateAsync(environment, export: false));

    /// <summary>
    /// Runs the committers' rounds and judges their history; with <paramref name="export"/>, writes the register's values
    /// as a ledger map trace, whose ledgers take no writes, when <see cref="LedgerMapTraces.Enabled"/>.
    /// </summary>
    private static async Task SimulateAsync(SimulationEnvironment environment, bool export)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.25,
            AppliedMismatchRate = 0.25,
            ReadFailureRate = 0.05,
        };
        await using var first = new LedgerMapCommitter(register.Writer(0), int.MaxValue);
        await using var second = new LedgerMapCommitter(register.Writer(1), int.MaxValue);
        var world = new LedgerMapWorld();
        var submissions = new List<LedgerMapSubmission>();
        for (var round = 0; round < Rounds; round++)
        {
            world.History.Clear();
            world.History.AddRange(History(register));
            var answers = new List<Task<LedgerMapSubmission>>();
            for (var change = environment.Random.Next(1, 5); change > 0; change--)
            {
                var step = new LedgerMapStep(environment.Random.Next(100), environment.Random.Next(), environment.Random.Next(100), EndsBatch: false);
                if (world.Derive(step) is { } derived)
                {
                    var committer = environment.Random.Chance(0.5) ? 0 : 1;
                    answers.Add(SubmitAsync(committer == 0 ? first : second, committer, register, derived.Change));
                }
            }

            submissions.AddRange(await SimulationTasks.WhenAll(answers));
        }

        // Let every delayed write land before judging the history.
        await Task.Delay(register.MaxLandingDelay + 2 * register.MaxLatency, environment.TimeProvider);
        var history = History(register);
        for (var epoch = 1; epoch < history.Count; epoch++)
        {
            Assert.Equal((ulong)epoch, history[epoch].Epoch);
            Assert.Null(LedgerMapReference.Violation(history[epoch - 1], history[epoch]));
        }

        Assert.Empty(LedgerMapHistoryOracle.Violations(history, [-1, .. register.AppliedDispatches], [-1, .. register.AppliedWriters], submissions));
        Assert.True(register.UnknownOutcomes > 0, "No write had an unknown outcome.");
        Assert.True(register.AppliedMismatches > 0, "No write which applied answered a mismatch.");
        Assert.Contains(submissions, static submission => submission.Outcome?.Value is LedgerMapCommitOutcome.Applied);
        if (export)
        {
            var trace = new LedgerMapTrace();
            trace.Observe(register.AppliedWrites);
            LedgerMapTraces.Export(environment, "Committer", nameof(LedgerMapCommitterSimulationTests), trace, LedgerMapReference.Bits, tracesWrites: false);
        }
    }

    private static List<LedgerMapValue> History(SimulatedConditionalRegister register)
    {
        var history = new List<LedgerMapValue> { LedgerMapValue.Empty };
        foreach (var bytes in register.AppliedWrites)
        {
            history.Add(Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(bytes.AsSpan()).Value));
        }

        return history;
    }

    private static async Task<LedgerMapSubmission> SubmitAsync(LedgerMapCommitter committer, int writer, SimulatedConditionalRegister register, LedgerMapChange change)
    {
        var submittedAt = (ulong)register.AppliedWrites.Count;
        try
        {
            var outcome = await committer.CommitAsync(change);
            return new(change, writer, submittedAt, outcome, AnsweredAt: register.Dispatches);
        }
        catch (Exception failure) when (failure is IOException or TimeoutException)
        {
            return new(change, writer, submittedAt, null, failure, register.Dispatches);
        }
    }
}
