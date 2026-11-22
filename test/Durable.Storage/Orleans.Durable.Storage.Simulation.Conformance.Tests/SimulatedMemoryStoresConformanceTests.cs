using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The memory providers of the archive's store contracts, each case in a simulation of its own (verification.md §4.1):
/// the <c>IArchiveLogStore</c> spec over <see cref="MemoryArchiveLogStore"/>, whose writes and runs await between their
/// requests, under every explored schedule of Accordant's concurrent windows and, with faults, under the seeded schedules
/// of CsCheck's sequences; and the <c>IObjectStore</c> spec over <see cref="InMemoryObjectStore"/>, healthy and behind
/// <see cref="FaultInjectingObjectStore"/>, under seeded schedules. The memory object store answers each call in one step
/// under its lock, so its windows hold only the order in which their calls start, which seeded schedules vary; the race
/// mutants of both contracts run over the simulated object store and the fake Azure transport.
/// </summary>
public sealed class SimulatedMemoryStoresConformanceTests
{
    /// <summary>
    /// The most faults the faulty object store throws in one case, each an unknown outcome, which bounds the checker's
    /// search (#363), as <c>ObjectStoreConformanceTests</c> bounds it.
    /// </summary>
    private const int MaxUnknownOutcomes = 6;

    /// <summary>
    /// The stride of the memory archive-log store's explored windows at the gate: exploring each of Accordant's 166 groups
    /// takes about 12 s, which the nightly window runs.
    /// </summary>
    private const int GateStride = 2;

    /// <summary>
    /// Every concurrent group of Accordant's generation over the archive-log store's input set, after every shorter
    /// sequence, conforms over the memory store: every <see cref="GateStride"/>th group of racing writes under every
    /// schedule of its window within the gate's bound, and the rest under their seeded schedules, or every group explored
    /// under <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c>.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowOfTheMemoryArchiveLogStoreConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var spec = ArchiveLogStoreSpec.Create(unknownOutcomes: false, staleReads: true, retriesMeetLandings: false, interleavedWrites: true);
        var kit = LogKit(conformance, spec, faultRate: 0);
        var sampledKit = LogKit(new SimulatedConformance(), spec, faultRate: 0);

        var cases = spec.GenerateConcurrentTests(new ArchiveLogStoreState(), ArchiveLogStoreKit.InputSet(spec), kit.GenerationOptions(2)).ToList();
        Assert.NotEmpty(cases);
        var explored = cases.Where((testCase, index) => ScheduleBound.IsDeep || RacesWrites(testCase) && index % GateStride == 0).ToList();
        await kit.RunAsync(explored);
        await sampledKit.RunAsync(cases.Except(explored));

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// CsCheck's sequences and concurrent sequences conform over the memory archive-log store, whose requests fail before
    /// or after they land or answer a landed write's condition failed, each case under its seeded schedule.
    /// </summary>
    [Fact]
    public async Task TheMemoryArchiveLogStoreConformsWithFaultsUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = LogKit(conformance, ArchiveLogStoreSpec.Create(unknownOutcomes: true), faultRate: 0.2);

        await kit.SampleAsync(kit.Sequences(20).WithReplays(), iterations: 40, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(8).WithReplays(), iterations: 40, threads: 1);

        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// CsCheck's sequences and concurrent sequences conform over the memory object store, healthy and behind the
    /// fault-injecting store, each case under its seeded schedule.
    /// </summary>
    [Fact]
    public async Task TheMemoryObjectStoresConformUnderSeededSchedules()
    {
        var healthy = new SimulatedConformance();
        var kit = ObjectKit(healthy, static _ => new InMemoryObjectStore(), unknownOutcomes: false);
        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), iterations: 40, threads: 1);

        var faulty = new SimulatedConformance();
        var faultyKit = ObjectKit(faulty, static seed => new FaultInjectingObjectStore<InMemoryObjectStore>(new InMemoryObjectStore(), seed, maxUnknownOutcomes: MaxUnknownOutcomes),
        unknownOutcomes: true);
        await faultyKit.SampleAsync(faultyKit.Sequences(20), iterations: 40, threads: 1);
        await faultyKit.SampleAsync(faultyKit.ConcurrentSequences(10), iterations: 40, threads: 1);

        TestDiagnostics.WriteLine(healthy.Summary());
        TestDiagnostics.WriteLine(faulty.Summary());
    }

    /// <summary>
    /// Returns whether a case holds a group of two or more writes, whose conditions race.
    /// </summary>
    private static bool RacesWrites(ConcurrentTestCase testCase) =>
        testCase.Segments.Any(static segment =>
            segment.OperationCalls.Count(static call => call.OperationInput.Request is LogAppend or LogOpen or LogDeleteBase) >= 2);

    private static ConformanceKit<ArchiveLogStoreState, LogTarget> LogKit(SimulatedConformance conformance, Spec<ArchiveLogStoreState> spec, double faultRate) =>
        new ConformanceKit<ArchiveLogStoreState, LogTarget>(
                spec,
                static () => new ArchiveLogStoreState(),
                seed => Task.FromResult(new LogTarget(new Random(seed), faultRate)),
                ArchiveLogStoreSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ArchiveLogStoreSpec.Conflicts,
                scheduler: conformance)
            .ArchiveLogStoreOperations(static (target, request) => target.RunAsync(request));

    private static ConformanceKit<ObjectStoreState, ObjectTarget> ObjectKit<TStore>(SimulatedConformance conformance, Func<int, TStore> create, bool unknownOutcomes)
        where TStore : IObjectStore, IAppendableObjectStore =>
        new ConformanceKit<ObjectStoreState, ObjectTarget>(
                ObjectStoreSpec.Create(unknownOutcomes),
                static () => new ObjectStoreState(),
                seed =>
                {
                    var store = create(seed);
                    return Task.FromResult(new ObjectTarget(store, store));
                },
                ObjectStoreSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ObjectStoreSpec.Conflicts,
                scheduler: conformance)
            .ObjectStoreOperations(static (target, request) => target.RunAsync(request), appends: true);

    /// <summary>
    /// One case's memory archive-log store, whose requests fail at <paramref name="faultRate"/>, drawn from the case's
    /// seed: a call which fails answers nothing, and a read which a write overlapped and which answers <c>Stale</c>
    /// reports it as racing.
    /// </summary>
    private sealed class LogTarget(Random random, double faultRate) : IAsyncDisposable
    {
        private readonly ArchiveLogStoreExecutor _executor = new(new MemoryArchiveLogStore(faults: _ =>
            random.NextDouble() >= faultRate ? ArchiveLogFault.None : (ArchiveLogFault)random.Next(1, 4)));

        private int _writing;
        private int _writes;

        public async Task<LogOutcome> RunAsync(object request)
        {
            var write = request is LogAppend or LogOpen or LogDeleteBase;
            var (writing, writes) = (_writing, _writes);
            if (write)
            {
                _writing++;
                _writes++;
            }

            try
            {
                var outcome = await _executor.RunAsync(request, CancellationToken.None);
                var overlapped = writing > 0 || _writes != writes;
                return outcome.Kind == LogOutcomeKind.Stale && overlapped ? new LogOutcome(LogOutcomeKind.StaleRacing) : outcome;
            }
            catch (Exception exception) when (ArchiveLogRaces.IsTransportFailure(exception))
            {
                return LogOutcome.Unknown;
            }
            finally
            {
                if (write)
                {
                    _writing--;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// One case's object store and the executor of its calls; an injected failure answers nothing.
    /// </summary>
    private sealed class ObjectTarget(IObjectStore store, IAppendableObjectStore appendable) : IAsyncDisposable, IConformanceEvents
    {
        private readonly ObjectStoreExecutor _executor = new(store, appendable);

        public async Task<ObjectOutcome> RunAsync(object request)
        {
            try
            {
                return await _executor.RunAsync(request, CancellationToken.None);
            }
            catch (ObjectStoreFaultException)
            {
                return ObjectOutcome.Unknown;
            }
        }

        public IReadOnlyList<string> TakeEvents() => store is IConformanceEvents events ? events.TakeEvents() : [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
