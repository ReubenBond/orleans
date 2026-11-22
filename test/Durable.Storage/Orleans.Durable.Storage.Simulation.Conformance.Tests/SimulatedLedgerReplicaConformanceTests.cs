using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The replica role of <see cref="LedgerReplicaSpec"/> (ledger-replicas.md §2.1, §10) over the replicas of two ledgers on
/// one node, the memory provider's and a LedgerStore's across restarts, each case in a simulation of its own: under every
/// explored schedule of Accordant's concurrent windows, and under the seeded schedules of CsCheck's concurrent sequences
/// (verification.md §4.1).
/// </summary>
/// <remarks>
/// <para>
/// The memory provider applies each operation at once, so its replicas yield to the scheduler as each request lands and
/// before each answer returns, through the conditional seam the simulation configurations compile in
/// (<c>InMemoryLedgerReplicaTransport.YieldsAtLandingAndAnswer</c>, verification.md §7.2).
/// </para>
/// <para>
/// The LedgerStore runs over its simulated node's disk (<see cref="LedgerStoreNode"/>), which completes each WAL write as
/// an operation of its own on the node's lane. An explored case's disk completes every write after no latency, so the
/// scheduler, not a latency drawn from the seed, chooses when each write completes among the window's other steps; a
/// sampled case's disk completes each after its own random latency.
/// </para>
/// </remarks>
public sealed class SimulatedLedgerReplicaConformanceTests
{
    private const int Window = 2;

    /// <summary>The cluster which claims the replica's simulated node root.</summary>
    private static readonly Guid Cluster = Guid.ParseExact("5e7f1a2b3c4d5e6f708192a3b4c5d6e7", "N");

    /// <summary>
    /// The window of CsCheck's sequences, over addresses 0 to 5.
    /// </summary>
    private const int RandomWindow = 3;

    /// <summary>
    /// The stride of the LedgerStore's explored windows at the gate: its WAL appends and disk completions give a window
    /// about forty choice points, so exploring each of Accordant's 228 groups takes about three and a half minutes, which
    /// the nightly window runs.
    /// </summary>
    private const int GateStride = 16;

    private static readonly LedgerId[] Ledgers = [new(41), new(42)];

    private static readonly NodeId Node = new(Guid.Parse("4d1e2c3b-0a9f-4e8d-b7c6-a5b4c3d2e1f0"));

    private static readonly Gen<long> Addresses = Gen.Frequency((24, Gen.Long[0, 5]), (1, Gen.Const(long.MaxValue)));

    /// <summary>
    /// The known contiguous tail a request carries: mostly none, and otherwise a small one (LR-I6).
    /// </summary>
    private static readonly Gen<long> Tails = Gen.Frequency((3, Gen.Const(0L)), (1, Gen.Long[1, 4]));

    private static readonly Gen<bool> Rarely = Gen.Frequency((7, Gen.Const(false)), (1, Gen.Const(true)));

    private static readonly Gen<bool> Sometimes = Gen.Frequency((3, Gen.Const(false)), (1, Gen.Const(true)));

    private static readonly Gen<int> ReplicaLedgers = Gen.Frequency((3, Gen.Const(0)), (1, Gen.Const(1)));

    /// <summary>
    /// Every concurrent group of Accordant's generation over the replica role's inputs, after every shorter sequence,
    /// conforms under every schedule of its window within the bound, over the memory provider.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Fixed(Kit(conformance, Window, _ => Task.FromResult<Target>(new MemoryTarget(Window, mutant: false))));

        await kit.RunAsync(ConcurrentCases(kit, restarts: false));

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// Every concurrent group of Accordant's generation over the replica role's inputs and the store's restart, after
    /// every shorter sequence, conforms over a LedgerStore whose disk completions are the scheduler's choices: every
    /// <see cref="GateStride"/>th group which changes the store under every schedule of its window within the gate's
    /// bound, and the rest under their seeded schedules, or every group explored under <c>DURABLE_STORAGE_SCHEDULE_BOUND=deep</c>.
    /// </summary>
    [Fact]
    public async Task EveryConcurrentWindowOfALedgerStoreConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var kit = Restarts(Fixed(Kit(conformance, Window, _ => StoreTarget.CreateAsync(conformance.Environment, Window, TimeSpan.Zero))));
        var sampled = new SimulatedConformance();
        var sampledKit = Restarts(Fixed(Kit(sampled, Window, _ => StoreTarget.CreateAsync(sampled.Environment, Window, TimeSpan.Zero))));
        var cases = ConcurrentCases(kit, restarts: true).ToList();

        var explored = cases.Where((testCase, index) => ScheduleBound.IsDeep || ChangesTheStore(testCase) && index % GateStride == 0).ToList();
        await kit.RunAsync(explored);
        await sampledKit.RunAsync(cases.Except(explored));

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// CsCheck's sequences and concurrent sequences over the replica role's requests and the store's restart conform over
    /// a LedgerStore, each case in a simulation of its own under its seeded schedule and disk latencies.
    /// </summary>
    [Fact]
    public async Task ALedgerStoreConformsUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = Restarts(
            Random(Kit(conformance, RandomWindow, _ => StoreTarget.CreateAsync(conformance.Environment, RandomWindow, maxDiskLatency: null))),
            weight: 2);

        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);
        await kit.SampleAsync(kit.ConcurrentSequences(10), iterations: 80, threads: 1);

        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// A write which checks its replica's seal before it lands and answers from its check
    /// (<c>InMemoryLedgerReplicaTransport.WriteChecksSealBeforeLandingMutant</c>) answers written although a seal between
    /// its steps refused it, so a later read finds its slot missing. The default schedule runs the seal before the write
    /// checks and passes; one preemption between the write's steps fails it, on every run. The seal starts first, so the
    /// default schedule runs the write, the newest, until it waits, and one preemption gives the seal the step between
    /// the write's check and its landing.
    /// </summary>
    [Fact]
    public Task ExploredSchedulesCatchAWriteWhichChecksTheSealBeforeLanding() =>
        CatchesAsync(Window, _ => Task.FromResult<Target>(new MemoryTarget(Window, mutant: true)), "write-checks-seal");

    /// <summary>
    /// A LedgerStore write behind a replica which checks the seal itself, writes in a later step, and answers written
    /// whenever its check found the replica unsealed (<see cref="ChecksSealBeforeWritingReplica"/>) answers written
    /// although a seal which the store made durable between the check and the write refused it, so a later read finds
    /// its slot missing. As with the memory provider's mutant, the default schedule passes and one preemption fails it,
    /// on every run.
    /// </summary>
    [Fact]
    public Task ExploredSchedulesCatchALedgerStoreWriteWhichAnswersFromAnEarlierSealCheck() =>
        CatchesAsync(
            Window,
            conformance => StoreTarget.CreateAsync(conformance.Environment, Window, TimeSpan.Zero, static replica => new ChecksSealBeforeWritingReplica(replica)),
            "checks-seal-before-writing");

    /// <summary>
    /// Runs the mutant's witness under exploration, which must catch it on a schedule with a preemption, and under 40
    /// seeded schedules, reporting how many catch it.
    /// </summary>
    private static async Task CatchesAsync(int window, Func<SimulatedConformance, Task<Target>> create, string mutant)
    {
        ConformanceCase witness = new(
        [
            new([new("Write", new LedgerReplicaWrite(0))]),
            new([new("ReadTail", new LedgerReplicaReadTail(0, Seal: true)), new("Write", new LedgerReplicaWrite(1))]),
            new([new("Read", new LedgerReplicaRead(1))]),
        ]);
        var conformance = new SimulatedConformance(ScheduleBound.Gate);

        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(() => Fixed(Kit(conformance, window, _ => create(conformance))).RunAsync(witness));

        Assert.Contains("The spec does not allow round", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(conformance.Explorations);
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);

        // The same case under its seeded schedule, one simulation per seed, catches the mutant only on some seeds.
        const int Seeds = 40;
        var caught = 0;
        var seeded = new SimulatedConformance();
        var sampledKit = Fixed(Kit(seeded, window, _ => create(seeded)));
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
            $"The {mutant} mutant: explored, caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}; seeded, caught on {caught} of {Seeds} seeds."));
    }

    /// <summary>
    /// Returns Accordant's concurrent cases over the replica role's inputs on the node's two ledgers, and the store's
    /// restart when <paramref name="restarts"/> is set.
    /// </summary>
    private static IEnumerable<ConcurrentTestCase> ConcurrentCases(ConformanceKit<LedgerReplicaState, Target> kit, bool restarts)
    {
        var spec = LedgerReplicaSpec.Create(Options(Window));
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerReplicaOutcome>(operation).With(request, request.ToString()));

        Add("Write", new LedgerReplicaWrite(0));
        Add("Write", new LedgerReplicaWrite(1, ContiguousTail: 1));
        Add("Write", new LedgerReplicaWrite(0, Recovery: true));
        Add("Read", new LedgerReplicaRead(1));
        Add("ReadTail", new LedgerReplicaReadTail(0));
        Add("ReadTail", new LedgerReplicaReadTail(0, Seal: true));
        Add("ListReplicas", LedgerReplicaListReplicas.Instance);
        Add("DropReplica", new LedgerReplicaDropReplica());
        Add("Write", new LedgerReplicaWrite(0, Ledger: 1));
        if (restarts)
        {
            Add("Restart", new LedgerReplicaRestart(OperatingSystem: false));
        }

        var cases = spec.GenerateConcurrentTests(new LedgerReplicaState(), inputs, kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        return cases;
    }

    private static LedgerReplicaSpecOptions Options(int window) => new() { WindowSize = window, Role = LedgerReplicaRole.Replica };

    /// <summary>
    /// Returns whether a concurrent group of <paramref name="testCase"/> holds a call which changes the store: a write, a
    /// seal, a drop, or a restart.
    /// </summary>
    private static bool ChangesTheStore(ConcurrentTestCase testCase) =>
        testCase.Segments.Any(static segment =>
            segment.OperationCalls.Count > 1
            && segment.OperationCalls.Any(static call => call.OperationInput.Request is LedgerReplicaWrite or LedgerReplicaReadTail { Seal: true } or LedgerReplicaDropReplica or LedgerReplicaRestart));

    private static ConformanceKit<LedgerReplicaState, Target> Kit(SimulatedConformance conformance, int window, Func<int, Task<Target>> create)
    {
        var options = Options(window);
        return new ConformanceKit<LedgerReplicaState, Target>(
            LedgerReplicaSpec.Create(options),
            static () => new LedgerReplicaState(),
            create,
            (request, state) => LedgerReplicaSpec.IsCallerAllowed(request, state, options),
            CancellationToken.None,
            scheduler: conformance);
    }

    /// <summary>
    /// Registers the replica role's operations, whose requests the explored and witness cases name.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, Target> Fixed(ConformanceKit<LedgerReplicaState, Target> kit) =>
        kit
            .Operation("Write", Gen.Const(new LedgerReplicaWrite(0)), Run)
            .Operation("Read", Gen.Const(new LedgerReplicaRead(0)), Run)
            .Operation("ReadTail", Gen.Const(new LedgerReplicaReadTail(0)), Run)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Run)
            .Operation("DropReplica", Gen.Const(new LedgerReplicaDropReplica()), Run);

    /// <summary>
    /// Registers the replica role's operations with requests drawn at random over addresses 0 to 5, and rarely an invalid
    /// address, on the node's two ledgers.
    /// </summary>
    private static ConformanceKit<LedgerReplicaState, Target> Random(ConformanceKit<LedgerReplicaState, Target> kit) =>
        kit
            .Operation(
                "Write",
                Gen.Select(Addresses, ReplicaLedgers, Tails, Rarely).Select(static (slot, ledger, tail, recovery) => new LedgerReplicaWrite(slot, ledger, tail, recovery)),
                Run,
                weight: 12)
            .Operation(
                "WriteEmpty",
                Gen.Select(Gen.Long[0, 5], ReplicaLedgers).Select(static (slot, ledger) => new LedgerReplicaWriteEmpty(slot, ledger)),
                Run)
            .Operation(
                "Read",
                Gen.Select(Addresses, ReplicaLedgers, Tails).Select(static (slot, ledger, tail) => new LedgerReplicaRead(slot, ledger, tail)),
                Run,
                weight: 6)
            .Operation(
                "ReadRange",
                Gen.Select(Gen.Long[0, 5], Gen.Int[0, 4], Gen.Int[1, 4], ReplicaLedgers, Tails)
                    .Select(static (slot, offset, length, ledger, tail) => new LedgerReplicaReadRange(slot, offset, length, ledger, tail)),
                Run,
                weight: 3)
            .Operation(
                "ReadTail",
                Gen.Select(Gen.Long[0, 4], Sometimes, ReplicaLedgers).Select(static (tail, seal, ledger) => new LedgerReplicaReadTail(tail, seal, ledger)),
                Run,
                weight: 6)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Run, weight: 2)
            .Operation("DropReplica", ReplicaLedgers.Select(static ledger => new LedgerReplicaDropReplica(ledger)), Run);

    private static ConformanceKit<LedgerReplicaState, Target> Restarts(ConformanceKit<LedgerReplicaState, Target> kit, int weight = 1) =>
        kit.Operation("Restart", Gen.Bool.Select(static operatingSystem => new LedgerReplicaRestart(operatingSystem)), Run, weight);

    private static Task<LedgerReplicaOutcome> Run<TRequest>(Target target, TRequest request)
        where TRequest : notnull =>
        target.RunAsync(request);

    /// <summary>
    /// The replicas of the spec's two ledgers on one node, and the node's handle, which lists and drops them.
    /// </summary>
    private abstract class Target : IAsyncDisposable
    {
        private readonly LedgerReplicaOverlaps _overlaps = new();

        protected abstract ILedgerStore NodeHandle { get; }

        public virtual Task<LedgerReplicaOutcome> RunAsync(object request) => _overlaps.RunAsync(request, () => ExecuteAsync(request));

        public abstract ValueTask DisposeAsync();

        protected abstract ILedgerReplica Replica(int ledger);

        protected virtual Task<LedgerReplicaOutcome> ExecuteAsync(object request) => request switch
        {
            LedgerReplicaDropReplica drop => LedgerReplicaExecutor.ExecuteNodeAsync(() => NodeHandle.DropReplicaAsync(Ledgers[drop.Ledger]), static _ => null),
            LedgerReplicaListReplicas => LedgerReplicaExecutor.ExecuteListingAsync(() => NodeHandle.ListReplicasAsync(), Ledgers, static _ => null),
            _ => LedgerReplicaExecutor.ExecuteAsync(Replica(LedgerReplicaSpec.LedgerOf(request)), request, static _ => null),
        };
    }

    /// <summary>
    /// The memory provider's replicas, whose operations yield to the scheduler as they land and before they answer.
    /// </summary>
    private sealed class MemoryTarget : Target
    {
        private readonly InMemoryLedgerReplicaTransport _nodes;
        private readonly ILedgerReplica[] _replicas;

        public MemoryTarget(int window, bool mutant)
        {
            _nodes = new InMemoryLedgerReplicaTransport { YieldsAtLandingAndAnswer = true, WriteChecksSealBeforeLandingMutant = mutant };
            var options = new LedgerReplicaOptions { WindowSize = window };
            _replicas = [.. Ledgers.Select(ledger => _nodes.Open(Node, ledger, options))];
            NodeHandle = _nodes.Node(Node);
        }

        protected override ILedgerStore NodeHandle { get; }

        public override async ValueTask DisposeAsync()
        {
            foreach (var replica in _replicas)
            {
                await replica.DisposeAsync();
            }

            _nodes.Dispose();
        }

        protected override ILedgerReplica Replica(int ledger) => _replicas[ledger];
    }

    /// <summary>
    /// A LedgerStore's replicas on a simulated node, which the spec's restart crashes and restarts from what its disk
    /// kept; the store is its node's handle, which lists and drops them (ledger-replicas.md §9.2).
    /// </summary>
    /// <remarks>
    /// A restart waits for the operations in flight and holds back new ones until the store serves again, so it is one
    /// atomic step among them, as the spec has it. A call counts as overlapping only from when it passes that gate.
    /// The gate is the simulation's own: a count of the calls in flight and the tasks a restart and its waiters await.
    /// </remarks>
    private sealed class StoreTarget : Target
    {
        private readonly LedgerStoreNode _node;
        private readonly LedgerReplicaOptions _options;
        private readonly Func<ILedgerReplica, ILedgerReplica> _wrap;
        private ILedgerReplica[] _replicas = [];
        private TaskCompletionSource? _restarting;
        private TaskCompletionSource? _idle;
        private int _running;

        private StoreTarget(LedgerStoreNode node, int window, Func<ILedgerReplica, ILedgerReplica>? wrap)
        {
            _node = node;
            _options = new LedgerReplicaOptions { WindowSize = window };
            _wrap = wrap ?? (static replica => replica);
        }

        protected override ILedgerStore NodeHandle => _node.Store;

        /// <summary>
        /// Starts a node whose disk completes each write after up to <paramref name="maxDiskLatency"/>, or the node's default, and whose store
        /// holds no replica: each replica's first write, recovery write, or seal creates it (LS-I1).
        /// </summary>
        public static async Task<Target> CreateAsync(
            SimulationEnvironment environment, int window, TimeSpan? maxDiskLatency, Func<ILedgerReplica, ILedgerReplica>? wrap = null)
        {
            var node = new LedgerStoreNode(environment, "replica-0", Cluster);
            if (maxDiskLatency is { } latency)
            {
                node.MaxDiskLatency = latency;
            }

            await node.StartAsync();
            var target = new StoreTarget(node, window, wrap);
            target.OpenReplicas();
            return target;
        }

        public override async Task<LedgerReplicaOutcome> RunAsync(object request)
        {
            while (_restarting is { } restarting)
            {
                await restarting.Task;
            }

            if (request is not LedgerReplicaRestart)
            {
                _running++;
                try
                {
                    return await base.RunAsync(request);
                }
                finally
                {
                    if (--_running == 0)
                    {
                        _idle?.TrySetResult();
                    }
                }
            }

            var restarted = _restarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                while (_running > 0)
                {
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _idle.Task;
                }

                return await base.RunAsync(request);
            }
            finally
            {
                _idle = null;
                _restarting = null;
                restarted.SetResult();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await ReleaseReplicasAsync();
            await _node.DisposeAsync();
        }

        protected override ILedgerReplica Replica(int ledger) => _replicas[ledger];

        protected override Task<LedgerReplicaOutcome> ExecuteAsync(object request) =>
            request is LedgerReplicaRestart restart
                ? LedgerReplicaExecutor.ExecuteNodeAsync(() => new ValueTask(RestartAsync(restart.OperatingSystem)), static _ => null)
                : base.ExecuteAsync(request);

        /// <summary>
        /// Crashes the node and restarts it from what its disk kept: every acknowledged operation survives, its creation,
        /// seal, and drop included (LS-I1, LS-I2, LS-I11).
        /// </summary>
        private async Task RestartAsync(bool operatingSystem)
        {
            await ReleaseReplicasAsync();
            await _node.CrashAsync(operatingSystem);
            await _node.StartAsync();
            OpenReplicas();
        }

        private void OpenReplicas() => _replicas = [.. Ledgers.Select(ledger => _wrap(_node.Store.OpenReplica(ledger, _options)))];

        private async ValueTask ReleaseReplicasAsync()
        {
            foreach (var replica in _replicas)
            {
                await replica.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A race mutant at the replica's boundary: a writer write reads the replica's tail first and answers sealed when the
    /// replica is sealed; otherwise it writes in a step of its own and answers written, whatever the write answers. A seal
    /// which lands between its check and its write refuses the write, which still answers written (LR-I8). The store's
    /// unsealing tail read completes at once, so the step between them is the mutant's own yield.
    /// </summary>
    private sealed class ChecksSealBeforeWritingReplica(ILedgerReplica inner) : ILedgerReplica
    {
        public async ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, global::Orleans.Durable.Storage.Engine.Buffers.ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            if (recovery)
            {
                return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            }

            if (await inner.ReadTailAsync(contiguousTail, seal: false, cancellationToken) is LedgerReplicaTail { IsSealed: true })
            {
                return ReplicaWriteOutcome.Sealed.Instance;
            }

            await SimulationTasks.Yield();
            var written = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            return written is ReplicaWriteOutcome.Sealed ? ReplicaWriteOutcome.Written.Instance : written;
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
