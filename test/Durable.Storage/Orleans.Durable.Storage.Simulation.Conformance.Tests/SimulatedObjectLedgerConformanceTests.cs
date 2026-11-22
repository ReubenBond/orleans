using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The ledger storage contract's spec (<see cref="LedgerStorageSpec"/>, LL-I1 to LL-I9) over object-store ledgers
/// (<see cref="ObjectLedgerSystem"/>; object-ledgers.md §10), each case in a simulation of its own on the virtual clock:
/// over the simulated object store, which fails requests, lands writes before the caller hears, never, or later, and
/// answers a retried write's own landing; and over the file-system store on a recorded file system. CsCheck's sequences,
/// and its concurrent groups of appends and reads with another process's checks and recoveries and drops, run under
/// seeded schedules, and Accordant's concurrent windows under every explored schedule (verification.md §4.1), which
/// catch a store that acknowledges a batch before it lands.
/// </summary>
public sealed class SimulatedObjectLedgerConformanceTests
{
    private const int Window = 2;

    /// <summary>The root of each case's recorded file system, which lives in memory.</summary>
    private static readonly string Root = Path.GetFullPath("clog-simulated-object-ledgers");

    [Fact]
    public async Task SequencesConformOverTheSimulatedStoreUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(conformance, faults: true);
        await kit.SampleAsync(kit.Sequences(20), iterations: 60, threads: 1);
        await kit.SampleAsync(AppendsAndReads(), iterations: 60, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    [Fact]
    public async Task SequencesConformOverTheFileSystemStoreUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                LedgerStorageSpec.Create(),
                static () => new LedgerState(),
                static _ => Task.FromResult(ObjectLedgerSystem.Over(
                    new FileSystemObjectStore(Root, SimulationEnvironment.Clock, new RecordingFileSystem(Root) { YieldsAtIo = true }),
                    Window)),
                LedgerStorageSpec.IsCallerAllowed,
                CancellationToken.None,
                scheduler: conformance)
            .WithLedgerOperations();
        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(AppendsAndReads(), iterations: 40, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// Every concurrent group of appends and reads of Accordant's generation, after every shorter sequence, conforms under
    /// every schedule of its window within the bound: the store yields as each request lands and before each answer.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Kit(conformance, faults: false);
        var spec = LedgerStorageSpec.Create();
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
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    [Fact]
    public async Task AppendLayoutSequencesConformOverTheSimulatedStoreUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = AppendKit(conformance, faults: true);
        await kit.SampleAsync(kit.Sequences(20), iterations: 60, threads: 1);
        await kit.SampleAsync(AppendsAndReads(), iterations: 60, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    [Fact]
    public async Task AppendLayoutSequencesConformOverTheFileSystemStoreUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                LedgerStorageSpec.Create(),
                static () => new LedgerState(),
                static _ => Task.FromResult(ObjectLedgerSystem.OverAppendable(
                    new FileSystemObjectStore(Root, SimulationEnvironment.Clock, new RecordingFileSystem(Root) { YieldsAtIo = true }),
                    Window)),
                LedgerStorageSpec.IsCallerAllowed,
                CancellationToken.None,
                scheduler: conformance)
            .WithLedgerOperations();
        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(AppendsAndReads(), iterations: 40, threads: 1);
        TestDiagnostics.WriteLine(conformance.Summary());
    }

    [Fact]
    public async Task AppendLayoutEveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = AppendKit(conformance, faults: false);
        var spec = LedgerStorageSpec.Create();
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
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    [Fact]
    public async Task AppendLayoutAppendRecoverAndDropWindowsConformUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = AppendKit(conformance, faults: false);
        ConformanceRound Call(string operation, object request) => new([new ConformanceCall(operation, request)]);

        // Append + Recover concurrent window (with and without an existing batch).
        await kit.RunAsync(new ConformanceCase(
        [
            new([new(LedgerStorageSpec.Append, new LedgerAppend(1)), new(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true))]),
        ]));
        await kit.RunAsync(new ConformanceCase(
        [
            Call(LedgerStorageSpec.Append, new LedgerAppend(1)),
            new([new(LedgerStorageSpec.Append, new LedgerAppend(2)), new(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true))]),
        ]));

        // Read + Drop and ReadRange/Scan + Drop concurrent windows after recovery and observation.
        await kit.RunAsync(new ConformanceCase(
        [
            Call(LedgerStorageSpec.Append, new LedgerAppend(1)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(2)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Observe, LedgerObserve.Instance),
            new([new(LedgerStorageSpec.Read, new LedgerSlotRead(0)), new(LedgerStorageSpec.Drop, new LedgerDrop(Every: true))]),
        ]));
        await kit.RunAsync(new ConformanceCase(
        [
            Call(LedgerStorageSpec.Append, new LedgerAppend(1)),
            Call(LedgerStorageSpec.Append, new LedgerAppend(2)),
            Call(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)),
            Call(LedgerStorageSpec.Observe, LedgerObserve.Instance),
            new([new(LedgerStorageSpec.Scan, new LedgerScan(0)), new(LedgerStorageSpec.Drop, new LedgerDrop(Every: true))]),
        ]));

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// A store which acknowledges a batch's create or append before it lands (<see cref="AcknowledgesBeforeLandingObjectStore"/>)
    /// completes an append whose batch the store does not yet hold, so a read below the completed prefix which runs before
    /// the landing finds its slot absent, which the storage reports as corruption and the spec refuses (LL-I6). The append
    /// starts last in its window, so the default schedule runs it and its landing before the read and passes; a preemption
    /// runs the read between the append's answer and its landing and fails it, on every run. Seeded schedules of an append
    /// and then a read catch it too, where the read runs before the landing. Both per-slot and append layouts are checked.
    /// </summary>
    [Fact]
    public async Task SchedulesCatchAStoreWhichAcknowledgesBeforeLanding()
    {
        await AssertCaughtAsync(appendable: false);
        await AssertCaughtAsync(appendable: true);

        static async Task AssertCaughtAsync(bool appendable)
        {
            var explored = new SimulatedConformance(ScheduleBound.Gate);
            var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(() => Kit(explored, appendable).RunAsync(new ConformanceCase(
            [
                new([new(LedgerStorageSpec.Read, new LedgerSlotRead(0)), new(LedgerStorageSpec.Append, new LedgerAppend(1))]),
            ])));
            Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
            var found = Assert.Single(explored.Explorations);
            Assert.True(found.Schedules > 1, $"The default schedule failed (appendable={appendable}): {found}.");

            const int Seeds = 40;
            var caught = 0;
            var seeded = Kit(new SimulatedConformance(), appendable);
            for (var seed = 1; seed <= Seeds; seed++)
            {
                try
                {
                    await seeded.RunAsync(new ConformanceCase(
                        [new([new(LedgerStorageSpec.Append, new LedgerAppend(1))]), new([new(LedgerStorageSpec.Read, new LedgerSlotRead(0))])],
                        Seed: seed));
                }
                catch (ConformanceScheduleException)
                {
                    caught++;
                }
            }

            TestDiagnostics.WriteLine(FormattableString.Invariant(
                $"The acknowledge-before-land store (appendable={appendable}): explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure!.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
        }

        static ConformanceKit<LedgerState, ObjectLedgerSystem> Kit(SimulatedConformance conformance, bool appendable) =>
            new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                    LedgerStorageSpec.Create(),
                    static () => new LedgerState(),
                    _ =>
                    {
                        var store = new AcknowledgesBeforeLandingObjectStore(new InMemoryObjectStore());
                        return Task.FromResult(appendable
                            ? ObjectLedgerSystem.OverAppendable(store, Window, owned: store)
                            : ObjectLedgerSystem.Over(store, Window, owned: store));
                    },
                    LedgerStorageSpec.IsCallerAllowed,
                    CancellationToken.None,
                    scheduler: conformance)
                .WithLedgerOperations();
    }

    private static Gen<ConformanceCase> AppendsAndReads()
    {
        var appendsAndReads = Gen.Frequency(
            (4, LedgerConformanceOperations.Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (2, LedgerConformanceOperations.Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))),
            (1, Gen.Const(new ConformanceCall(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)))),
            (2, Gen.Const(new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)))),
            (1, Gen.Bool.Select(static every => new ConformanceCall(LedgerStorageSpec.Drop, new LedgerDrop(every)))));
        var rounds = Gen.Frequency(
            (3, LedgerConformanceOperations.Calls.Select(static call => new ConformanceRound([call]))),
            (2, appendsAndReads.Array[2, 3].Select(static calls => new ConformanceRound(calls))));
        return rounds.Array[1, 12].Select(static rounds => new ConformanceCase(rounds));
    }

    /// <summary>
    /// Returns a kit over a fresh simulated store per case: with <paramref name="faults"/>, one which fails requests and
    /// lands writes late; otherwise one which yields for latency, so a window's landings and answers interleave.
    /// </summary>
    private static ConformanceKit<LedgerState, ObjectLedgerSystem> Kit(SimulatedConformance conformance, bool faults) =>
        new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                LedgerStorageSpec.Create(),
                static () => new LedgerState(),
                seed =>
                {
                    var store = new SimulatedObjectStore(conformance.Environment, FormattableString.Invariant($"ledgers-{seed}"))
                    {
                        UnknownOutcomeRate = faults ? 0.1 : 0,
                        RetriedAnswerRate = faults ? 0.1 : 0,
                        ReadFailureRate = faults ? 0.05 : 0,
                        YieldsForLatency = !faults,
                    };
                    return Task.FromResult(ObjectLedgerSystem.Over(store, Window, owned: store));
                },
                LedgerStorageSpec.IsCallerAllowed,
                CancellationToken.None,
                scheduler: conformance)
            .WithLedgerOperations();

    private static ConformanceKit<LedgerState, ObjectLedgerSystem> AppendKit(SimulatedConformance conformance, bool faults) =>
        new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                LedgerStorageSpec.Create(),
                static () => new LedgerState(),
                seed =>
                {
                    var store = new SimulatedObjectStore(conformance.Environment, FormattableString.Invariant($"ledgers-append-{seed}"))
                    {
                        UnknownOutcomeRate = faults ? 0.1 : 0,
                        RetriedAnswerRate = faults ? 0.1 : 0,
                        ReadFailureRate = faults ? 0.05 : 0,
                        YieldsForLatency = !faults,
                    };
                    return Task.FromResult(ObjectLedgerSystem.OverAppendable(store, Window, owned: store));
                },
                LedgerStorageSpec.IsCallerAllowed,
                CancellationToken.None,
                scheduler: conformance)
            .WithLedgerOperations();
}
