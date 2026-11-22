using System.Collections.Concurrent;
using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The composition role of <see cref="LedgerReplicaSpec"/> (ledger-protocol.md §8.3, ledger-replicas.md §10): a
/// <see cref="Ledger"/> of quorum two over the memory replicas of three nodes, on the simulation's virtual clock, each
/// case in a simulation of its own: under every explored schedule of Accordant's concurrent windows, and, over replicas
/// which fail, hold, or refuse operations, under the seeded schedules of CsCheck's sequences (verification.md §4.1).
/// </summary>
/// <remarks>
/// <para>
/// The replicas yield to the scheduler as each request lands and before each answer returns, through the memory
/// provider's conditional seam (<c>memory-replica-yields</c>, verification.md §7.2), so each interleaving of the quorum's
/// requests and answers is a scheduling choice. A held operation waits for a harness release or its hold limit on the
/// virtual clock, which advances whenever nothing else can run, so no poller moves time as the thread-pool target's
/// does.
/// </para>
/// <para>
/// A call answers unavailable or <c>Backpressure</c> only when an injected fault fired during it or another call
/// overlapped it: replicas which all answer decide every write, read, seal check, and recovery.
/// </para>
/// </remarks>
public sealed class SimulatedLedgerCompositionConformanceTests
{
    private const int Window = 2;

    private const int RandomWindow = 3;

    private const int ReplicaCount = 3;

    private const int Quorum = 2;

    /// <summary>
    /// The stride of the explored windows at the gate: a quorum's requests and answers over three yielding replicas give
    /// a window about ninety choice points, so exploring each of Accordant's 92 groups takes about a minute, which the
    /// nightly window runs.
    /// </summary>
    private const int GateStride = 8;

    private static readonly LedgerId[] Ledgers = [new(51)];

    private static readonly NodeId[] Nodes =
    [
        new(Guid.Parse("0b5c1e2d-3f40-4a51-8b62-7c83d94ea5f0")),
        new(Guid.Parse("1c6d2f3e-4051-4b62-9c73-8d94ea5fb601")),
        new(Guid.Parse("2d7e3041-5162-4c73-ad84-9ea5fb60c712")),
    ];

    private static readonly Gen<long> Tails = Gen.Frequency((3, Gen.Const(0L)), (1, Gen.Long[1, 4]));

    /// <summary>
    /// Every concurrent group of Accordant's generation over the composition's inputs, after every shorter sequence,
    /// conforms over healthy replicas: every <see cref="GateStride"/>th group which changes the ledger under every
    /// schedule of its window within the gate's bound, and the rest under their seeded schedules, or every group explored
    /// under <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c>. The spec judges a group allowing a failed call to take effect later or
    /// never, as an overlapping call may fail one.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var options = Options(Window) with { Ambiguous = true };
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Fixed(Kit(conformance, options, _ => new Target(Window, chaos: null, mutant: false)));
        var sampledKit = Fixed(Kit(new SimulatedConformance(), options, _ => new Target(Window, chaos: null, mutant: false)));
        var cases = ConcurrentCases(kit).ToList();

        var explored = cases.Where((testCase, index) => ScheduleBound.IsDeep || ChangesTheLedger(testCase) && index % GateStride == 0).ToList();
        await kit.RunAsync(explored);
        await sampledKit.RunAsync(cases.Except(explored));

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// CsCheck's sequences and concurrent sequences conform over replicas which fail before or after applying an
    /// operation, hold it, or answer it <c>Backpressure</c>, on demand and at random from the case's seed, each case in a
    /// simulation of its own under its seeded schedule. Every case ends by clearing the faults and checking that a seal
    /// check, a recovery, and a read of each address succeed.
    /// </summary>
    [Fact]
    public async Task TheCompositionConformsOverFaultyReplicasUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var options = Options(RandomWindow) with { Ambiguous = true, Backpressure = true };
        var kit = Heals(Random(Kit(conformance, options, seed => new Target(RandomWindow, chaos: seed, mutant: false))))
            .Action(
                "Fault",
                Gen.Select(
                        Gen.Int[0, ReplicaCount - 1],
                        Gen.Int[1, 3],
                        Gen.OneOfConst(InjectedFault.BeforeApplying, InjectedFault.AfterApplying, InjectedFault.Held, InjectedFault.Backpressure),
                        Gen.Frequency((1, Gen.Const(default(string))), (1, Gen.OneOfConst<string?>("Write", "RecoveryWrite", "Read", "ReadTail", "Seal"))))
                    .Select(static (replica, count, fault, operation) => new ReplicaFault(replica, count, fault, operation)),
                static (target, fault) =>
                {
                    target.Inject(fault);
                    return Task.CompletedTask;
                },
                weight: 8)
            .Action("Release", Gen.Const(Release.Instance), static (target, _) => target.ReleaseAsync(), weight: 2);

        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), iterations: 80, threads: 1);

        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// A composition whose replicas acknowledge a writer write before it lands (<see cref="AcknowledgesBeforeLandingReplica"/>)
    /// answers an append written although a concurrent recovery sealed the replicas before the write landed, so the
    /// recovery's end leaves the acknowledged slot out (LP-I3). The write starts last, so the default schedule runs it and
    /// its landings, the newest operations, before the recovery and passes; one preemption runs the recovery's seals
    /// first and fails it, on every run.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchReplicasWhichAcknowledgeBeforeLanding()
    {
        ConformanceCase witness = new(
        [
            new([new("Recover", new LedgerReplicaRecover(0)), new("Write", new LedgerReplicaWrite(0))]),
            new([new("Read", new LedgerReplicaRead(0))]),
        ]);
        var options = Options(Window) with { Ambiguous = true };
        var conformance = new SimulatedConformance(ScheduleBound.Gate);

        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(
            () => Fixed(Kit(conformance, options, _ => new Target(Window, chaos: null, mutant: true))).RunAsync(witness));

        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(conformance.Explorations);
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        const int Seeds = 40;
        var caught = 0;
        var sampledKit = Fixed(Kit(new SimulatedConformance(), options, _ => new Target(Window, chaos: null, mutant: true)));
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
            $"The acknowledge-before-land mutant: explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
    }

    /// <summary>
    /// Returns Accordant's concurrent cases over the composition's inputs, generated by the strict spec.
    /// </summary>
    private static IEnumerable<ConcurrentTestCase> ConcurrentCases(ConformanceKit<LedgerReplicaState, Target> kit)
    {
        var spec = LedgerReplicaSpec.Create(Options(Window));
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerReplicaOutcome>(operation).With(request, request.ToString()));

        Add("Write", new LedgerReplicaWrite(0));
        Add("Write", new LedgerReplicaWrite(1));
        Add("Write", new LedgerReplicaWrite(1, ContiguousTail: 1));
        Add("Read", new LedgerReplicaRead(1));
        Add("ReadTail", new LedgerReplicaReadTail(0));
        Add("Recover", new LedgerReplicaRecover(0));
        Add("ListReplicas", LedgerReplicaListReplicas.Instance);
        Add("Remove", LedgerReplicaRemove.Instance);

        var cases = spec.GenerateConcurrentTests(new LedgerReplicaState(), inputs, kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        return cases;
    }

    private static LedgerReplicaSpecOptions Options(int window) =>
        new() { WindowSize = window, Role = LedgerReplicaRole.Composition, ReplicaCount = ReplicaCount, Quorum = Quorum };

    /// <summary>
    /// Returns whether a concurrent group of <paramref name="testCase"/> holds a call which changes the ledger: a write, a
    /// recovery, or the ledger's removal.
    /// </summary>
    private static bool ChangesTheLedger(ConcurrentTestCase testCase) =>
        testCase.Segments.Any(static segment =>
            segment.OperationCalls.Count > 1
            && segment.OperationCalls.Any(static call => call.OperationInput.Request is LedgerReplicaWrite or LedgerReplicaRecover or LedgerReplicaRemove));

    private static ConformanceKit<LedgerReplicaState, Target> Kit(SimulatedConformance conformance, LedgerReplicaSpecOptions options, Func<int, Target> create) =>
        new(
            LedgerReplicaSpec.Create(options),
            static () => new LedgerReplicaState(),
            seed => Task.FromResult(create(seed)),
            (request, state) => LedgerReplicaSpec.IsCallerAllowed(request, state, options),
            CancellationToken.None,
            scheduler: conformance);

    /// <summary>
    /// Registers the composition's operations, whose requests the explored and witness cases name.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, Target> Fixed(ConformanceKit<LedgerReplicaState, Target> kit) =>
        kit
            .Operation("Write", Gen.Const(new LedgerReplicaWrite(0)), Run)
            .Operation("Read", Gen.Const(new LedgerReplicaRead(0)), Run)
            .Operation("ReadTail", Gen.Const(new LedgerReplicaReadTail(0)), Run)
            .Operation("Recover", Gen.Const(new LedgerReplicaRecover(0)), Run)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Run)
            .Operation("Remove", Gen.Const(LedgerReplicaRemove.Instance), Run);

    /// <summary>
    /// Registers the composition's operations with requests drawn at random over addresses 0 to 5, and rarely an invalid
    /// address, each carrying a known contiguous tail.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, Target> Random(ConformanceKit<LedgerReplicaState, Target> kit)
    {
        var addresses = Gen.Frequency((24, Gen.Long[0, 5]), (1, Gen.Const(long.MaxValue)));
        return kit
            .Operation("Write", Gen.Select(addresses, Tails).Select(static (slot, tail) => new LedgerReplicaWrite(slot, ContiguousTail: tail)), Run, weight: 12)
            .Operation("WriteEmpty", Gen.Long[0, 5].Select(static slot => new LedgerReplicaWriteEmpty(slot)), Run)
            .Operation("Read", Gen.Select(addresses, Tails).Select(static (slot, tail) => new LedgerReplicaRead(slot, ContiguousTail: tail)), Run, weight: 6)
            .Operation(
                "ReadRange",
                Gen.Select(Gen.Long[0, 5], Gen.Int[0, 4], Gen.Int[1, 4], Tails)
                    .Select(static (slot, offset, length, tail) => new LedgerReplicaReadRange(slot, offset, length, ContiguousTail: tail)),
                Run,
                weight: 3)
            .Operation("ReadTail", Gen.Long[0, 4].Select(static tail => new LedgerReplicaReadTail(tail)), Run, weight: 6)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Run, weight: 2)
            .Operation("Recover", Tails.Select(static tail => new LedgerReplicaRecover(tail)), Run, weight: 2)
            .Operation("Remove", Gen.Const(LedgerReplicaRemove.Instance), Run);
    }

    /// <summary>
    /// Ends every case by healing: every fault clears and every held operation lands, and then a seal check, a recovery,
    /// and a read of each address must succeed, since no fault fires.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, Target> Heals(ConformanceKit<LedgerReplicaState, Target> kit) =>
        kit.Action("Heal", Gen.Const(Healing.Instance), static (target, _) => target.HealAsync(), weight: 0)
            .Finally(
            [
                new("Heal", Healing.Instance),
                new("ReadTail", new LedgerReplicaReadTail(0)),
                new("Recover", new LedgerReplicaRecover(0)),
                .. Enumerable.Range(0, 6).Select(static slot => new ConformanceCall("Read", new LedgerReplicaRead(slot))),
            ]);

    private static Task<LedgerReplicaOutcome> Run<TRequest>(Target target, TRequest request)
        where TRequest : notnull =>
        target.RunAsync(request);

    /// <summary>
    /// Every held replica operation is released and lands.
    /// </summary>
    private sealed record Release
    {
        internal static Release Instance { get; } = new();

        public override string ToString() => "the held operations land";
    }

    /// <summary>
    /// Every fault clears and every held replica operation lands.
    /// </summary>
    private sealed record Healing
    {
        internal static Healing Instance { get; } = new();

        public override string ToString() => "the faults clear and the held operations land";
    }

    /// <summary>
    /// The composition over the memory replicas of three nodes, each behind a <see cref="FaultyLedgerReplica"/>, and the
    /// nodes' handles, which list and drop their replicas.
    /// </summary>
    private sealed class Target : IAsyncDisposable, IConformanceEvents
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly LedgerReplicaOverlaps _overlaps = new();
        private readonly InMemoryLedgerReplicaTransport _nodes = new() { YieldsAtLandingAndAnswer = true };
        private readonly FaultyLedgerReplica[] _replicas;
        private readonly AcknowledgesBeforeLandingReplica[] _mutants = [];
        private readonly Ledger _ledger;
        private readonly LedgerAsReplica _ledgerAsReplica;
        private int _running;
        private int _overlapped;

        /// <summary>
        /// Opens the replicas, which fail at random from <paramref name="chaos"/> when it is set, and the composition over
        /// them, whose replicas acknowledge writer writes before landing them when <paramref name="mutant"/> is set.
        /// </summary>
        public Target(int window, int? chaos, bool mutant)
        {
            var options = new LedgerReplicaOptions { WindowSize = window };
            var clock = SimulationEnvironment.Clock;
            _replicas =
            [
                .. Nodes.Select((node, index) => new FaultyLedgerReplica(
                    index,
                    _nodes.Open(node, Ledgers[0], options),
                    clock,
                    _events,
                    chaos is { } seed ? new System.Random(unchecked(seed * ReplicaCount + index)) : null)),
            ];
            ILedgerReplica[] seen = _replicas;
            if (mutant)
            {
                _mutants = [.. _replicas.Select(static replica => new AcknowledgesBeforeLandingReplica(replica))];
                seen = _mutants;
            }

            _ledger = new Ledger(Ledgers[0], LedgerQuorum.ForNodes(Ledgers[0], [.. Nodes], seen, Quorum, options, clock, localNode: null), new LedgerLocation.Nodes([.. Nodes]));
            _ledgerAsReplica = new LedgerAsReplica(_ledger);
        }

        private long FaultsFired => _replicas.Sum(static replica => replica.Fired);

        public async Task<LedgerReplicaOutcome> RunAsync(object request)
        {
            var overlapped = _overlapped;
            if (++_running > 1)
            {
                _overlapped++;
            }

            var fired = FaultsFired;
            LedgerReplicaOutcome outcome;
            bool unexplained;
            try
            {
                outcome = await _overlaps.RunAsync(request, () => ExecuteAsync(request));
            }
            finally
            {
                unexplained = _overlapped == overlapped && FaultsFired == fired;
                _running--;
            }

            return outcome.Kind is LedgerReplicaOutcomeKind.Unavailable or LedgerReplicaOutcomeKind.Backpressure && unexplained
                ? outcome with
                {
                    Kind = LedgerReplicaOutcomeKind.Unexpected,
                    Failure = $"{outcome.Kind} although it ran alone and no injected fault fired: {outcome.Failure}",
                }
                : outcome;
        }

        public void Inject(ReplicaFault fault) => _replicas[fault.Replica].Inject(fault.Count, fault.Fault, fault.Operation);

        public async Task ReleaseAsync()
        {
            foreach (var replica in _replicas)
            {
                await replica.ReleaseAsync();
            }
        }

        public async Task HealAsync()
        {
            foreach (var replica in _replicas)
            {
                await replica.HealAsync();
            }
        }

        public IReadOnlyList<string> TakeEvents()
        {
            var events = new List<string>();
            while (_events.TryDequeue(out var text))
            {
                events.Add(text);
            }

            return events;
        }

        public async ValueTask DisposeAsync()
        {
            await HealAsync();
            foreach (var mutant in _mutants)
            {
                await mutant.DisposeAsync();
            }

            await _ledgerAsReplica.DisposeAsync();
            await _ledger.DisposeAsync();
            _nodes.Dispose();
        }

        private static LedgerReplicaOutcome? Classify(Exception exception) =>
            exception is AggregateException aggregate && aggregate.InnerExceptions.All(static inner => inner is InjectedFaultException)
                ? new(LedgerReplicaOutcomeKind.Unavailable, Failure: LedgerReplicaExecutor.Describe(exception))
                : null;

        private Task<LedgerReplicaOutcome> ExecuteAsync(object request) => request switch
        {
            LedgerReplicaRecover recover => LedgerReplicaExecutor.ExecuteRecoverAsync(() => RecoverAsync(new Slot(recover.ContiguousTail)), Classify),
            LedgerReplicaListReplicas => LedgerReplicaExecutor.ExecuteListingAsync(ListReplicasAsync, Ledgers, Classify),
            LedgerReplicaRemove => LedgerReplicaExecutor.ExecuteNodeAsync(RemoveAsync, Classify),
            _ => LedgerReplicaExecutor.ExecuteAsync(_ledgerAsReplica, request, Classify),
        };

        /// <summary>
        /// Recovers the ledger from the greater of <paramref name="contiguousTail"/> and its known contiguous tail.
        /// </summary>
        private ValueTask<RecoveryOutcome> RecoverAsync(Slot contiguousTail)
        {
            _ = _ledger.RaiseKnownContiguousTail(contiguousTail);
            return _ledger.RecoverAsync(CancellationToken.None);
        }

        private async ValueTask<IReadOnlyList<LedgerId>> ListReplicasAsync()
        {
            var listed = new SortedSet<LedgerId>();
            foreach (var node in Nodes)
            {
                listed.UnionWith(await _nodes.Node(node).ListReplicasAsync());
            }

            return [.. listed];
        }

        private async ValueTask RemoveAsync()
        {
            foreach (var node in Nodes)
            {
                await _nodes.Node(node).DropReplicaAsync(Ledgers[0]);
            }
        }
    }
}
