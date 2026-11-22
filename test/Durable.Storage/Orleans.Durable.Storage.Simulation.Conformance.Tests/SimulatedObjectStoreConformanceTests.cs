using System.IO.Hashing;
using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// Checks the kit's <see cref="SimulatedObjectStore"/> against <see cref="ObjectStoreSpec"/> through the conformance kit
/// (verification.md §4.1, §7.2): with unknown outcomes, a request the store fails answers nothing, whether it was lost,
/// applied, or lands later, and the spec judges every answer, a retried write's among them, against what the unknown
/// writes may have done; and every schedule of small concurrent windows, which the kit explores, judged by
/// <c>AllowsConcurrent</c>.
/// </summary>
/// <remarks>
/// Each case runs in a simulation of its own, seeded by the case (<see cref="SimulatedConformance"/>), and a concurrent
/// group's calls start as operations of its scheduler, so CsCheck shrinks a failing case's operations and interleaving
/// together and <c>CLOG_TEST_SEED</c> replays both.
/// </remarks>
public sealed class SimulatedObjectStoreConformanceTests
{
    private const int Iterations = 40;

    [Fact]
    public async Task TheSimulatedStoreConformsWithUnknownOutcomes()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(conformance, [], unknownOutcomes: true);
        await kit.SampleAsync(kit.Sequences(30), Iterations, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), Iterations, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// A case replays from its seed alone, its faults and its interleaving with it, and each fault the store injects fires.
    /// </summary>
    [Fact]
    public async Task ACaseReplaysFromItsSeedAndEachFaultFires()
    {
        List<SimulatedObjectStore> stores = [];
        var conformance = new SimulatedConformance();
        List<(ConformanceCase Case, string Schedule, int Trace)> runs = [];
        conformance.Observed = (testCase, run) => runs.Add((testCase, run.ScheduleId, run.Trace.Count));
        var kit = Kit(conformance, stores, unknownOutcomes: true);
        var pcg = new PCG(1, 1);
        var sequences = kit.Sequences(30);
        var groups = kit.ConcurrentSequences(10);
        for (var index = 0; index < 20; index++)
        {
            await kit.RunAsync((index % 2 == 0 ? sequences : groups).Generate(pcg, null, out _));
        }

        Assert.True(stores.Sum(static store => store.UnknownOutcomes) > 0, "No write's outcome was unknown.");
        Assert.True(stores.Sum(static store => store.DelayedLandings) > 0, "No write landed late.");
        Assert.True(stores.Sum(static store => store.RetriedAnswers) > 0, "No retried write met its own landing.");

        var first = runs.ToList();
        runs.Clear();
        foreach (var (testCase, _, _) in first)
        {
            await kit.RunAsync(testCase);
        }

        Assert.Equal(first.Select(static run => (run.Schedule, run.Trace)), runs.Select(static run => (run.Schedule, run.Trace)));
    }

    /// <summary>
    /// Every concurrent group of Accordant's generation over a small input set, after every shorter sequence, conforms
    /// under every schedule of its window within the gate's bound: the store's requests and answers yield to the
    /// scheduler, so each interleaving of the group's landings and answers is a schedule the exploration runs.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Kit(conformance, [], unknownOutcomes: false, yields: true);
        var spec = ObjectStoreSpec.Create(unknownOutcomes: false);

        var cases = spec.GenerateConcurrentTests(new ObjectStoreState(), Inputs(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// A store which checks a conditional write's condition in one request and writes in a second
    /// (<see cref="SimulatedObjectStore.ChecksBeforeWritingMutant"/>) breaks the conditional replace when a concurrent
    /// replace at the same version lands between them. The exploration of the group's window finds that schedule on every
    /// run, and its report names the schedule, which <c>CLOG_SCHEDULE</c> replays; the seeded schedules of sampled cases
    /// meet it only by chance, which the test measures.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAConditionCheckedBeforeItsWrite()
    {
        ConformanceCase witness = new(
        [
            new([new(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 1))]),
            new([new(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 1, 2)), new(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 1, 3))]),
        ]);

        var explored = new SimulatedConformance(ScheduleBound.Gate);
        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(
            () => Kit(explored, [], unknownOutcomes: false, yields: true, mutant: true).RunAsync(witness));
        Assert.Contains("The spec does not allow round 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(WindowScheduleExplorer.ScheduleVariable + "=", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(explored.Explorations);

        // The default schedule runs each call on until it waits and passes; the failing schedule preempts one.
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);
        TestDiagnostics.WriteLine(explored.Summary());

        // The same case under its seeded schedule, one simulation per seed, catches the mutant only on some seeds.
        const int Seeds = 40;
        var caught = 0;
        var sampled = new SimulatedConformance();
        var sampledKit = Kit(sampled, [], unknownOutcomes: false, yields: true, mutant: true);
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
            $"The check-then-write mutant: explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
        Assert.InRange(caught, 1, Seeds);
    }

    /// <summary>
    /// The store checks its copy of a write's content against the writer's CRC-32C, as a service checks the bytes it
    /// received: a wrong one is refused with <see cref="ObjectContentChangedException"/>, or, with its answer lost, as an
    /// unknown outcome; either way nothing applies and a read finds nothing. The content's own CRC-32C then writes it.
    /// </summary>
    [Fact]
    public void AWriteWhoseCrc32CIsWrongIsRefusedAndAppliesNothing() =>
        SeededSimulation.Run(1, static async environment =>
        {
            await using var store = new SimulatedObjectStore(environment, "archive-crc");
            var name = new ObjectName("d/1/0/a");
            var bytes = "content"u8.ToArray();
            var crc32c = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes);
            using var content = ArcBuffer.Wrap(bytes);

            _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await store.CreateAsync(name, content, crc32c ^ 1, CancellationToken.None));
            _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await store.ReplaceAsync(ObjectName.Manifest, null, content, crc32c ^ 1, CancellationToken.None));
            store.LosesChangedContentRefusals = true;
            var lost = await Assert.ThrowsAnyAsync<Exception>(async () => await store.CreateAsync(name, content, crc32c ^ 1, CancellationToken.None));
            Assert.True(lost is IOException or TimeoutException, $"A refusal whose answer is lost fails as an unknown outcome, not with {lost.GetType().Name}.");

            Assert.Empty(store.AppliedWritesOf(name));
            Assert.Empty(store.AppliedWritesOf(ObjectName.Manifest));
            Assert.Null(await store.ReadVersionedAsync(name, CancellationToken.None));
            Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(name, content, crc32c, CancellationToken.None)).Value);
        });

    /// <summary>
    /// An append whose landing a seal follows before its retry answers appended, since the sealed object holds its bytes;
    /// the mutant, which answers sealed without looking, gives an answer the spec does not allow.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAppendWhichLandsBeforeASealAndIsRetriedAnswersAppended(bool mutant) =>
        SeededSimulation.Run(1, async environment =>
        {
            await using var store = new SimulatedObjectStore(environment, "archive-sealed-retry") { AnswersSealedForALandedAppendMutant = mutant };
            var executor = new ObjectStoreExecutor(store, store);
            var history = new List<(object Request, ObjectOutcome Outcome)> { (new ObjectCreateAppendable(4), await executor.RunAsync(new ObjectCreateAppendable(4), CancellationToken.None)) };
            store.SealBetweenTheNextAppendAndItsRetry();
            foreach (var request in new object[] { new ObjectAppend(4, 0, 1), new ObjectSeal(4), new ObjectRead(4, 0, ObjectStoreSpec.ChunkLength) })
            {
                history.Add((request, await executor.RunAsync(request, CancellationToken.None)));
            }

            var violation = ObjectStoreSpec.FirstViolation(history);
            Assert.True(mutant == violation is not null, violation ?? "The mutant's sealed answer went unflagged.");
        });

    /// <summary>
    /// Returns the input set of the explored windows: creates of one name and replaces of the manifest, each pair racing
    /// on its condition, versioned reads, a delete, and a listing.
    /// </summary>
    private static InputSet Inputs(Microsoft.Accordant.Spec<ObjectStoreState> spec)
    {
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, ObjectOutcome>(operation).With(request, request.ToString()));

        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(1, 1));
        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(1, 2));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 3));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 4));
        Add(ObjectStoreSpec.ReadVersionedOperation, new ObjectReadVersioned(0));
        Add(ObjectStoreSpec.ReadVersionedOperation, new ObjectReadVersioned(1));
        Add(ObjectStoreSpec.DeleteOperation, new ObjectDelete(1));
        Add(ObjectStoreSpec.ListOperation, new ObjectList(0));
        return inputs;
    }

    private static ConformanceKit<ObjectStoreState, SimulatedTarget> Kit(
        SimulatedConformance conformance,
        List<SimulatedObjectStore> stores,
        bool unknownOutcomes,
        bool yields = false,
        bool mutant = false) =>
        new ConformanceKit<ObjectStoreState, SimulatedTarget>(
                ObjectStoreSpec.Create(unknownOutcomes),
                static () => new ObjectStoreState(),
                seed =>
                {
                    var store = new SimulatedObjectStore(conformance.Environment, FormattableString.Invariant($"archive-{seed}"))
                    {
                        UnknownOutcomeRate = unknownOutcomes ? 0.2 : 0,
                        RetriedAnswerRate = unknownOutcomes ? 0.1 : 0,
                        ReadFailureRate = unknownOutcomes ? 0.1 : 0,
                        YieldsForLatency = yields,
                        ChecksBeforeWritingMutant = mutant,
                    };
                    stores.Add(store);
                    return Task.FromResult(new SimulatedTarget(store));
                },
                ObjectStoreSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ObjectStoreSpec.Conflicts,
                scheduler: conformance)
            .ObjectStoreOperations(static (target, request) => target.RunAsync(request), appends: true);

    /// <summary>
    /// One case's store, with the executor of its calls: a request the store fails, with an <see cref="IOException"/> or
    /// a <see cref="TimeoutException"/>, answers nothing, so its outcome is unknown.
    /// </summary>
    private sealed class SimulatedTarget(SimulatedObjectStore store) : IAsyncDisposable
    {
        private readonly ObjectStoreExecutor _executor = new(store, store);

        public async Task<ObjectOutcome> RunAsync(object request)
        {
            try
            {
                return await _executor.RunAsync(request, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                return ObjectOutcome.Unknown;
            }
        }

        public ValueTask DisposeAsync() => store.DisposeAsync();
    }
}
