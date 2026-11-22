using CsCheck;
using Microsoft.Accordant;
using Orleans.Configuration;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// OR-I15, OR-I16, LR-I9: the replicas <see cref="OrleansLedgerReplicaTransport"/> opens conform to the shared
/// <see cref="ILedgerReplica"/> spec (ledger-replicas.md §2.1, §10) on an <c>Orleans.TestingHost</c> cluster: on the
/// in-process path to the caller's own node, which makes no grain call, and on the grain-call path to another node's,
/// whose calls cross to the callee's silo; over a network which loses requests and replies, duplicates and delays them,
/// injected at the callee, judged under unknown outcomes; and under the host's <c>Backpressure</c>. The callee's node
/// handle, its LedgerStore, lists and drops the replicas. Accordant generates every sequence and concurrent group to a
/// small depth; CsCheck generates longer random sequences and concurrent groups and shrinks a failure. Each case runs on
/// two fresh ledgers of one cluster. The silos' stores outlive each case and hold pages across them, so the class's leak
/// check, which counts every page the callees' calls rent, judges the pages once the silos stop.
/// </summary>
[Collection(TestingHostCollection.Name)]
public sealed class OrleansLedgerReplicaConformanceTests : IDisposable
{
    private const int Window = 3;

    private static readonly Gen<long> Addresses = Gen.Frequency((24, Gen.Long[0, 5]), (1, Gen.Const(long.MaxValue)));
    private static readonly Gen<long> Tails = Gen.Frequency((3, Gen.Const(0L)), (1, Gen.Long[1, 4]));
    private static readonly Gen<bool> Rarely = Gen.Frequency((7, Gen.Const(false)), (1, Gen.Const(true)));
    private static readonly Gen<bool> Sometimes = Gen.Frequency((3, Gen.Const(false)), (1, Gen.Const(true)));
    private static readonly Gen<int> Ledgers = Gen.Frequency((3, Gen.Const(0)), (1, Gen.Const(1)));

    private static readonly LedgerReplicaSpecOptions Spec = new() { WindowSize = Window, Role = LedgerReplicaRole.Replica };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private long _nextLedger = 1000;
    private int _backpressure;

    public void Dispose() => _leaks.Dispose();

    [Fact(Timeout = 300_000)]
    public async Task TheInProcessPathConforms()
    {
        await using var cluster = await DeployAsync();
        await ConformsAsync(cluster, remote: false, TestContext.Current.CancellationToken);
        Assert.Equal(0, cluster.CalleeCalls(cluster.SiloNames[0]) + cluster.CalleeCalls(cluster.SiloNames[1]));
    }

    [Fact(Timeout = 300_000)]
    public async Task TheGrainCallPathConforms()
    {
        await using var cluster = await DeployAsync();
        await ConformsAsync(cluster, remote: true, TestContext.Current.CancellationToken);
        Assert.Equal(0, cluster.CalleeCalls(cluster.SiloNames[0]));
        Assert.True(cluster.CalleeCalls(cluster.SiloNames[1]) > 0, "No replica call crossed to the callee's silo.");
    }

    /// <summary>
    /// OR-I16, LR-I9: over a network between silos which loses requests and replies, duplicates requests, and delays them
    /// past the caller's response timeout, injected at the callee, the grain-call path still conforms to the shared spec
    /// under unknown outcomes: every failure is unavailable, and every history is one which failed operations taking
    /// effect later, once, or never, allows.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheGrainCallPathConformsOverAFaultyNetwork()
    {
        await using var cluster = await DeployFaultyAsync(CalleeFaultsMutant.None);
        var kit = FaultyKit(cluster, TestContext.Current.CancellationToken);
        await kit.SampleAsync(kit.Sequences(30), iterations: 40);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 40);
        Assert.True(cluster.Faults!.Faults > 0, "The faulty network injected no fault.");
    }

    /// <summary>
    /// The faulty-network run catches a callee which answers for the store: one which answers a lost request as an empty
    /// replica would, an answer the node never gave (LR-I9).
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheFaultyNetworkRunCatchesACalleeWhichAnswersForTheStore()
    {
        await using var cluster = await DeployFaultyAsync(CalleeFaultsMutant.AnswersForTheStore);
        var kit = FaultyKit(cluster, TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAnyAsync<Exception>(() => kit.SampleAsync(kit.Sequences(30), iterations: 200));
    }

    /// <summary>
    /// OR-I17: with a host which admits one call at a time and a short deadline, concurrent calls meet the host's
    /// <c>Backpressure</c>, which had no effect, and the grain-call path still conforms.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheGrainCallPathConformsUnderBackpressure()
    {
        await using var cluster = await DeployAsync(
            static options => options.MaxReplicaCalls = 1,
            static silo => silo.Configure<SiloMessagingOptions>(static messaging => messaging.ResponseTimeout = TimeSpan.FromMilliseconds(200)));
        var options = Spec with { Ambiguous = true, Backpressure = true };
        var kit = Kit(cluster, options, remote: true, TestContext.Current.CancellationToken);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 60);
        Assert.True(Volatile.Read(ref _backpressure) > 0, "No call met the host's Backpressure.");
    }

    private async Task<ReplicaCluster> DeployAsync(Action<DurableStorageOptions>? configure = null, Action<ISiloBuilder>? configureSilo = null)
    {
        var cluster = await ReplicaCluster.DeployAsync(2, configure, configureSilo);
        cluster.CalleeAccounting = _leaks;
        return cluster;
    }

    private async Task<ReplicaCluster> DeployFaultyAsync(CalleeFaultsMutant mutant)
    {
        var timeout = TimeSpan.FromSeconds(1);
        var cluster = await DeployAsync(configureSilo: silo => silo.Configure<SiloMessagingOptions>(messaging => messaging.ResponseTimeout = timeout));
        cluster.Faults = new CalleeFaults(timeout * 1.5, mutant);
        return cluster;
    }

    private async Task ConformsAsync(ReplicaCluster cluster, bool remote, CancellationToken token)
    {
        var spec = LedgerReplicaSpec.Create(Spec);
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerReplicaOutcome>(operation).With(request, request.ToString()));

        Add("Write", new LedgerReplicaWrite(0));
        Add("Write", new LedgerReplicaWrite(1, ContiguousTail: 1));
        Add("Write", new LedgerReplicaWrite(0, Recovery: true));
        Add("Write", new LedgerReplicaWrite(long.MaxValue));
        Add("WriteEmpty", new LedgerReplicaWriteEmpty(0));
        Add("Read", new LedgerReplicaRead(1));
        Add("ReadRange", new LedgerReplicaReadRange(0, 1, 1));
        Add("ReadTail", new LedgerReplicaReadTail(1));
        Add("ReadTail", new LedgerReplicaReadTail(0, Seal: true));
        Add("ListReplicas", LedgerReplicaListReplicas.Instance);
        Add("DropReplica", new LedgerReplicaDropReplica());
        Add("Write", new LedgerReplicaWrite(0, Ledger: 1));
        Add("DropReplica", new LedgerReplicaDropReplica(Ledger: 1));

        var kit = Kit(cluster, Spec, remote, token);
        var initial = new LedgerReplicaState();
        var sequences = spec.GenerateTests(initial, inputs, kit.GenerationOptions(3));
        var concurrent = spec.GenerateConcurrentTests(initial, inputs, kit.GenerationOptions(2));
        Assert.NotEmpty(sequences);
        Assert.NotEmpty(concurrent);
        await kit.RunAsync(sequences);
        await kit.RunAsync(concurrent);
        await kit.SampleAsync(kit.Sequences(30), iterations: 60);
        await kit.SampleAsync(kit.ConcurrentSequences(15), iterations: 60);
    }

    private ConformanceKit<LedgerReplicaState, OrleansTarget> Kit(ReplicaCluster cluster, LedgerReplicaSpecOptions options, bool remote, CancellationToken cancellationToken)
    {
        var caller = cluster.SiloNames[0];
        var node = cluster.SiloNames[remote ? 1 : 0];
        return Kit(options, _ => new OrleansTarget(cluster.Transport(caller), cluster.Node(node).Store, NextLedgers(), delivered: null, () => Interlocked.Increment(ref _backpressure)), cancellationToken);
    }

    /// <summary>
    /// Returns a kit over the grain-call path through the cluster's callee faults, seeded by each case, judged by the spec
    /// under unknown outcomes, with an action which waits for the delayed and duplicated requests' deliveries.
    /// </summary>
    private ConformanceKit<LedgerReplicaState, OrleansTarget> FaultyKit(ReplicaCluster cluster, CancellationToken cancellationToken)
    {
        var caller = cluster.SiloNames[0];
        var store = cluster.Node(cluster.SiloNames[1]).Store;
        var faults = cluster.Faults!;
        return Kit(
                Spec with { Ambiguous = true },
                seed =>
                {
                    var ledgers = NextLedgers();
                    faults.Seed(ledgers, seed);
                    return new OrleansTarget(cluster.Transport(caller), store, ledgers, faults.DeliverAsync, backpressured: null);
                },
                cancellationToken)
            .Action("Deliver", Gen.Const(Delivery.Instance), static (target, _) => target.DeliverAsync());
    }

    private LedgerId[] NextLedgers()
    {
        var first = Interlocked.Add(ref _nextLedger, LedgerReplicaSpec.LedgerCount) - LedgerReplicaSpec.LedgerCount;
        return [new((ulong)first), new((ulong)first + 1)];
    }

    private static ConformanceKit<LedgerReplicaState, OrleansTarget> Kit(LedgerReplicaSpecOptions options, Func<int, OrleansTarget> create, CancellationToken cancellationToken) =>
        new ConformanceKit<LedgerReplicaState, OrleansTarget>(
                LedgerReplicaSpec.Create(options),
                static () => new LedgerReplicaState(),
                seed => Task.FromResult(create(seed)),
                (request, state) => LedgerReplicaSpec.IsCallerAllowed(request, state, options),
                cancellationToken)
            .Operation(
                "Write",
                Gen.Select(Addresses, Ledgers, Tails, Rarely).Select(static (slot, ledger, tail, recovery) => new LedgerReplicaWrite(slot, ledger, tail, recovery)),
                Execute<LedgerReplicaWrite>,
                weight: 12)
            .Operation(
                "WriteEmpty",
                Gen.Select(Gen.Long[0, 5], Ledgers).Select(static (slot, ledger) => new LedgerReplicaWriteEmpty(slot, ledger)),
                Execute<LedgerReplicaWriteEmpty>)
            .Operation(
                "Read",
                Gen.Select(Addresses, Ledgers, Tails).Select(static (slot, ledger, tail) => new LedgerReplicaRead(slot, ledger, tail)),
                Execute<LedgerReplicaRead>,
                weight: 6)
            .Operation(
                "ReadRange",
                Gen.Select(Gen.Long[0, 5], Gen.Int[0, 4], Gen.Int[1, 4], Ledgers, Tails)
                    .Select(static (slot, offset, length, ledger, tail) => new LedgerReplicaReadRange(slot, offset, length, ledger, tail)),
                Execute<LedgerReplicaReadRange>,
                weight: 3)
            .Operation(
                "ReadTail",
                Gen.Select(Gen.Long[0, 4], Sometimes, Ledgers).Select(static (tail, seal, ledger) => new LedgerReplicaReadTail(tail, seal, ledger)),
                Execute<LedgerReplicaReadTail>,
                weight: 6)
            .Operation("ListReplicas", Gen.Const(LedgerReplicaListReplicas.Instance), Execute<LedgerReplicaListReplicas>, weight: 2)
            .Operation("DropReplica", Ledgers.Select(static ledger => new LedgerReplicaDropReplica(ledger)), Execute<LedgerReplicaDropReplica>);

    private static Task<LedgerReplicaOutcome> Execute<TRequest>(OrleansTarget target, TRequest request)
        where TRequest : notnull =>
        target.RunAsync(request);

    /// <summary>The delayed and duplicated requests are delivered.</summary>
    private sealed record Delivery
    {
        internal static Delivery Instance { get; } = new();

        public override string ToString() => "the delayed requests are delivered";
    }

    /// <summary>
    /// One case's replicas of two fresh ledgers on one node, opened through a silo's transport, and the node's LedgerStore,
    /// which lists and drops them; a listing names only the case's ledgers. It counts the <c>Backpressure</c> answers it
    /// meets, and waits for the faulty network's deliveries before it is disposed.
    /// </summary>
    private sealed class OrleansTarget(OrleansLedgerReplicaTransport transport, ILedgerStore node, LedgerId[] ledgers, Func<Task>? delivered, Action? backpressured)
        : IAsyncDisposable
    {
        private readonly LedgerReplicaOverlaps _overlaps = new();
        private readonly ILedgerReplica[] _replicas = [.. ledgers.Select(ledger => transport.Open(node.NodeId, ledger, new LedgerReplicaOptions { WindowSize = Window }))];

        public async Task<LedgerReplicaOutcome> RunAsync(object request)
        {
            var outcome = await _overlaps.RunAsync(request, () => request switch
            {
                LedgerReplicaDropReplica drop => LedgerReplicaExecutor.ExecuteNodeAsync(() => node.DropReplicaAsync(ledgers[drop.Ledger]), Classify),
                LedgerReplicaListReplicas => LedgerReplicaExecutor.ExecuteListingAsync(ListAsync, ledgers, Classify),
                _ => LedgerReplicaExecutor.ExecuteAsync(_replicas[LedgerReplicaSpec.LedgerOf(request)], request, Classify),
            });
            if (outcome.Kind == LedgerReplicaOutcomeKind.Backpressure)
            {
                backpressured?.Invoke();
            }

            return outcome;
        }

        /// <summary>Waits for the faulty network's delayed and duplicated deliveries.</summary>
        public Task DeliverAsync() => delivered?.Invoke() ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            foreach (var replica in _replicas)
            {
                await replica.DisposeAsync();
            }

            await DeliverAsync();
        }

        /// <summary>
        /// The transport's unknown outcome, and a call whose deadline passed in its node's queue, unsent, are unavailable;
        /// every other failure the contract does not define is unexpected.
        /// </summary>
        private static LedgerReplicaOutcome? Classify(Exception exception) =>
            exception is LedgerReplicaUnavailableException or LedgerReplicaQueueTimeoutException
                ? new LedgerReplicaOutcome(LedgerReplicaOutcomeKind.Unavailable, Failure: exception.GetType().Name)
                : null;

        private async ValueTask<IReadOnlyList<LedgerId>> ListAsync() =>
            [.. (await node.ListReplicasAsync()).Where(ledgers.Contains)];
    }
}
