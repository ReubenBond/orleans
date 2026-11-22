using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The fold oracle (the ledger storage contract's spec, <see cref="LedgerStorageSpec"/>, with the quorum's recording oracle,
/// <see cref="LedgerQuorumOracle"/>; ledger-protocol.md §2, §8) over <see cref="LedgerSystem"/>: a primary's
/// <c>Ledger</c> and <c>LedgerWriter</c> over three skewed memory replicas behind a recording, on the virtual clock, each
/// case in a simulation of its own: under every explored schedule of Accordant's concurrent windows of appends and
/// reads, and under the seeded schedules of CsCheck's sequences (verification.md §4.1). The recording checks every
/// contiguous tail a replica request carries against the completed prefix and the recorded <c>T</c> (LP-I11).
/// </summary>
/// <remarks>
/// The system judges a check, a recovery, and an acquisition by every request of its interval, so only appends and reads
/// run in concurrent groups, as in <c>LedgerConformanceTests</c>. The memory replicas yield to the scheduler as each
/// request lands and before each answer returns (<c>memory-replica-yields</c>, verification.md §7.2), and the
/// skewed replicas' slow reads wait out twice the hedge delay on the virtual clock, which advances once nothing else can
/// run, so a read hedges without a harness advancing time.
/// </remarks>
public sealed class SimulatedLedgerConformanceTests
{
    private const int Replicas = 3;
    private const int Quorum = 2;
    private const int Window = 2;

    /// <summary>
    /// The stride of the explored windows at the gate: the quorum's requests and answers over three yielding replicas give
    /// a window up to seventy choice points, so exploring each of Accordant's 24 groups takes about 20 s, which the
    /// nightly window runs.
    /// </summary>
    private const int GateStride = 2;

    private static Gen<byte> Values => LedgerConformanceOperations.Values;

    private static Gen<long> Slots => LedgerConformanceOperations.Slots;

    /// <summary>
    /// Every concurrent group of appends and reads of Accordant's generation, after every shorter sequence, conforms:
    /// every <see cref="GateStride"/>th group which appends under every schedule of its window within the gate's bound,
    /// and the rest under their seeded schedules, or every group explored under <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c>.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Kit(conformance, mutant: false);
        var sampledKit = Kit(new SimulatedConformance(), mutant: false);
        var cases = ConcurrentCases(kit).ToList();

        var explored = cases.Where((testCase, index) => ScheduleBound.IsDeep || Appends(testCase) && index % GateStride == 0).ToList();
        await kit.RunAsync(explored);
        await sampledKit.RunAsync(cases.Except(explored));

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// CsCheck's sequences of every operation, and its sequences with concurrent groups of appends and reads, conform,
    /// each case in a simulation of its own under its seeded schedule.
    /// </summary>
    [Fact]
    public async Task SequencesConformUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(conformance, mutant: false);
        var appendsAndReads = Gen.Frequency(
            (3, Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (1, Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))));
        var rounds = Gen.Frequency(
            (3, Calls.Select(static call => new ConformanceRound([call]))),
            (2, appendsAndReads.Array[2, 3].Select(static calls => new ConformanceRound(calls))));

        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(rounds.Array[1, 12].Select(static rounds => new ConformanceCase(rounds)), iterations: 80, threads: 1);

        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// A system whose replicas acknowledge a writer write before it lands (<see cref="AcknowledgesBeforeLandingReplica"/>)
    /// completes an append whose batch no replica yet holds, so a concurrent read carrying the completed prefix's tail
    /// finds its slot missing (LP-I10). The append starts last, so the default schedule runs it and then its landings, the
    /// newest operations, before the read and passes; one preemption runs the read between the append's answer and its
    /// landings and fails it, on every run. Seeded schedules also catch the mutant without concurrency, where the next
    /// round's read runs before the landings.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchReplicasWhichAcknowledgeBeforeLanding()
    {
        ConformanceCase witness = new(
        [
            new([new(LedgerStorageSpec.Read, new LedgerSlotRead(0)), new(LedgerStorageSpec.Append, new LedgerAppend(1))]),
        ]);
        var conformance = new SimulatedConformance(ScheduleBound.Gate);

        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(
            () => Kit(conformance, mutant: true).RunAsync(witness));

        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(conformance.Explorations);
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        const int Seeds = 40;
        var caught = 0;
        var sampledKit = Kit(new SimulatedConformance(), mutant: true);
        for (var seed = 1; seed <= Seeds; seed++)
        {
            try
            {
                await sampledKit.RunAsync(witness with { Seed = seed });
            }
            catch (ConformanceScheduleException)
            {
                caught++;
            }
        }

        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The fold oracle's acknowledge-before-land mutant: explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
    }

    private static Gen<ConformanceCall> Calls => LedgerConformanceOperations.Calls;

    /// <summary>
    /// Returns Accordant's concurrent cases over appends and reads.
    /// </summary>
    private static IEnumerable<ConcurrentTestCase> ConcurrentCases(ConformanceKit<LedgerState, LedgerSystem> kit)
    {
        var spec = LedgerStorageSpec.Create(LedgerQuorumOracle.Judge);
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerOutcome>(operation).With(request, request.ToString()));

        Add(LedgerStorageSpec.Append, new LedgerAppend(1));
        Add(LedgerStorageSpec.Append, new LedgerAppend(2));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(0));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(1));

        var cases = spec.GenerateConcurrentTests(new LedgerState(), inputs, kit.GenerationOptions(maxDepth: 3));
        Assert.NotEmpty(cases);
        return cases;
    }

    /// <summary>
    /// Returns whether a concurrent group of <paramref name="testCase"/> appends.
    /// </summary>
    private static bool Appends(ConcurrentTestCase testCase) =>
        testCase.Segments.Any(static segment => segment.OperationCalls.Count > 1 && segment.OperationCalls.Any(static call => call.OperationInput.Request is LedgerAppend));

    /// <summary>
    /// Returns a kit over a fresh system per case, whose replicas acknowledge writer writes before landing them when
    /// <paramref name="mutant"/> is set; such a system's disposal first waits for every landing.
    /// </summary>
    private static ConformanceKit<LedgerState, LedgerSystem> Kit(SimulatedConformance conformance, bool mutant)
    {
        List<AcknowledgesBeforeLandingReplica> mutants = [];
        ILedgerReplica Wrap(ILedgerReplica replica)
        {
            var wrapped = new AcknowledgesBeforeLandingReplica(replica);
            mutants.Add(wrapped);
            return wrapped;
        }

        return new ConformanceKit<LedgerState, LedgerSystem>(
                LedgerStorageSpec.Create(LedgerQuorumOracle.Judge),
                static () => new LedgerState(),
                seed =>
                {
                    // The kit runs one case at a time, so the list holds the running case's mutants alone.
                    mutants.Clear();
                    return Task.FromResult(new LedgerSystem(Replicas, Quorum, Window, LedgerMutation.None, coverage: null, leader: (int)((uint)seed % Replicas), wrap: mutant ? Wrap : null, settle: LandedAsync));
                },
                LedgerStorageSpec.IsCallerAllowed,
                CancellationToken.None,
                scheduler: conformance)
            .WithLedgerOperations();

        async Task LandedAsync()
        {
            foreach (var replica in mutants)
            {
                await replica.LandedAsync();
            }
        }
    }
}
