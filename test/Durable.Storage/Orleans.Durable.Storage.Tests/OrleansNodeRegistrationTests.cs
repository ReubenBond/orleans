using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.TestingHost;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>A grain keyed by a node identity, placed by <see cref="RegisteredNodePlacement"/>, which names its silo.</summary>
public interface INodeProbeGrain : IGrainWithGuidKey
{
    Task<SiloAddress> SiloAsync();
}

/// <inheritdoc cref="INodeProbeGrain"/>
[RegisteredNodePlacement]
public sealed class NodeProbeGrain(ILocalSiloDetails silo) : Grain, INodeProbeGrain
{
    /// <summary>The activations of each node's probe, across every silo of the process.</summary>
    internal static readonly ConcurrentDictionary<Guid, int> Activations = new();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _ = Activations.AddOrUpdate(this.GetPrimaryKey(), 1, static (_, count) => count + 1);
        return Task.CompletedTask;
    }

    public Task<SiloAddress> SiloAsync() => Task.FromResult(silo.SiloAddress);
}

/// <summary>
/// Silos over their own node roots and one MetaStore (orleans.md §5.2, §5.3, §9): each opens its own LedgerStore,
/// registers its node through the controller grain once it is active, and completes <c>Registered</c> only once the
/// registration's write landed; a restarted silo keeps its node identity at a later generation; a silo whose registration
/// is answered <c>Stale</c>, <c>WrongArchiveStore</c>, or <c>OrdinalOccupied</c> does not start, and one over an emptied
/// root starts at its predecessor's ordinal only when it names the predecessor for replacement; a node's grains activate
/// only on the silo the registry names for it, while a node with no registered silo is refused before any activation; and
/// a membership change makes every silo re-read the registry (R1, OR-I2, OR-I9, OR-I20).
/// </summary>
[Collection(TestingHostCollection.Name)]
public sealed class OrleansNodeRegistrationTests : IAsyncLifetime
{
    private const string TestKey = "DurableStorageTest";

    /// <summary>Each running test's state, keyed by the ID the silos' configuration names.</summary>
    private static readonly ConcurrentDictionary<string, TestState> States = new(StringComparer.Ordinal);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly TestState _state = new();
    private readonly List<TestCluster> _clusters = [];

    public ValueTask InitializeAsync()
    {
        States[_state.Id] = _state;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cluster in _clusters)
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }

        _ = States.TryRemove(_state.Id, out _);
        if (Directory.Exists(_state.Roots))
        {
            Directory.Delete(_state.Roots, recursive: true);
        }

        _leaks.Dispose();
    }

    [Fact(Timeout = 120_000)]
    public async Task EachSiloRegistersItsOwnNodeAndItsGrainsActivateThere()
    {
        var cluster = await DeployAsync(2);
        var nodes = new List<(NodeId Node, SiloAddress Silo)>();
        foreach (var silo in cluster.Silos)
        {
            var node = _state.Node(silo.Name);
            var entry = (await node.Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
            Assert.Equal(silo.SiloAddress, entry.Silo);
            Assert.Equal(node.Node, entry.Node);
            nodes.Add((node.Node, silo.SiloAddress));
        }

        Assert.NotEqual(nodes[0].Node, nodes[1].Node);
        var registry = await ReadRegistryAsync();
        Assert.Equal(new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, new ArchiveStorePrefix("t"), (await StoredRecordAsync(_state.Objects)).Id), registry.ArchiveStore);
        foreach (var (node, silo) in nodes)
        {
            Assert.Equal(silo, registry.Find(node)?.Silo);
            Assert.Equal(silo, await ProbeAsync(cluster, node));
        }
    }

    /// <summary>
    /// The controller grain builds its node registry committer inside the call which activates it, under that call's
    /// <see cref="RequestContext"/> and Orleans' activation Activity; the committer's batches serve every registering silo,
    /// so the register's reads and writes run under neither (conventions.md §12).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRegistryCommitterRunsInNoRegisteringCallersContext()
    {
        var cluster = await DeployAsync(1);
        _ = await _state.Node(cluster.Silos[0].Name).Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var source = new ActivitySource("Orleans.Durable.Storage.Tests.RegisteringCaller");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        _state.MetaStore.RecordContexts = true;

        // Another key's activation, whose activating call is this registration.
        RequestContext.Set(GatedMetaStore.ProbeKey, "registering caller");
        try
        {
            using (source.StartActivity("registering caller"))
            {
                _ = await cluster.Client.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key + 1).RegisterNodeAsync(Entry(98, 1), StoreA, nodeToReplace: null);
            }
        }
        finally
        {
            RequestContext.Clear();
        }

        var contexts = _state.MetaStore.Contexts;
        Assert.NotEmpty(contexts);
        Assert.All(contexts, static context => Assert.Equal((null, null), context));
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeWithNoRegisteredSiloIsRefusedBeforeAnyActivation()
    {
        var cluster = await DeployAsync(1);
        var node = Node(99);

        var refusal = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => cluster.Client.GetGrain<INodeProbeGrain>(node.Value).SiloAsync().WaitAsync(TestContext.Current.CancellationToken));

        Assert.Contains(node.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.False(NodeProbeGrain.Activations.ContainsKey(node.Value));
    }

    /// <summary>
    /// <c>Registered</c> completes only once the registration's write answers: while the MetaStore holds the write, the
    /// silo's start waits at its active stage, and the node is not registered.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeIsRegisteredOnlyOnceItsRegistrationLands()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _state.MetaStore.WriteGate = gate;
        var deploying = DeployAsync(1);

        await _state.MetaStore.WriteArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var node = await _state.NodeAsync("Primary");
        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(node.Registered.IsCompleted);
        Assert.False(deploying.IsCompleted);

        gate.SetResult();
        var cluster = await deploying.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var entry = (await node.Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        Assert.Equal(cluster.Primary!.SiloAddress, entry.Silo);
        Assert.Equal(entry, (await ReadRegistryAsync()).Find(entry.Node));
    }

    /// <summary>A restarted silo opens its node root again, so it registers the same node at a later generation, which its grains follow.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARestartedSiloRegistersItsNodeAtItsNewGeneration()
    {
        var cluster = await DeployAsync(2);
        var secondary = cluster.SecondarySilos[0];
        var before = (await _state.Node(secondary.Name).Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        Assert.Equal(secondary.SiloAddress, await ProbeAsync(cluster, before.Node));

        var restarted = Assert.IsType<InProcessSiloHandle>(await cluster.RestartSiloAsync(secondary));
        var after = (await _state.Node(restarted.Name).Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;

        Assert.Equal(before.Node, after.Node);
        Assert.Equal(restarted.SiloAddress, after.Silo);
        Assert.True(after.Silo.Generation > before.Silo.Generation);
        Assert.Equal(after, (await ReadRegistryAsync()).Find(after.Node));
        Assert.Equal(restarted.SiloAddress, await ProbeAsync(cluster, after.Node));
    }

    /// <summary>
    /// orleans.md §5.3, OR-I2: a silo whose node the registry holds retired, by an ordinal successor, registers the node again
    /// at its new generation, still retired at its revision, and starts, so it serves the node's replicas until its gate
    /// opens; the successor stays active at the ordinal, and the node is never active again.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloWhoseNodeIsRetiredRegistersItRetiredAndStarts()
    {
        _state.Ordinals["Primary"] = 0;
        var first = await DeployAsync(1);
        var retired = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        await first.StopAllSilosAsync(TestContext.Current.CancellationToken);
        await using (var committer = Committer(await RegisterAsync()))
        {
            var successor = new NodeRegistryEntry(Node(42), Silo(42, 1), ordinal: 0);
            _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await committer.RegisterAsync(successor, await RegisteredStoreAsync(), retired.Node, TestContext.Current.CancellationToken)).Value);
        }

        var retiredAt = (await ReadRegistryAsync()).Find(retired.Node)!.RetiredRevision;
        Assert.NotEqual(0UL, retiredAt);

        _ = await DeployAsync(1);

        var restarted = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        Assert.Equal(retired.Node, restarted.Node);
        Assert.True(restarted.Silo.Generation > retired.Silo.Generation);
        var registry = await ReadRegistryAsync();
        Assert.Equal((restarted.Silo, retiredAt), (registry.Find(retired.Node)!.Silo, registry.Find(retired.Node)!.RetiredRevision));
        Assert.False(registry.Find(Node(42))!.IsRetired);
    }

    /// <summary>
    /// A silo over an emptied root at an ordinal its predecessor holds active does not start, logs a critical event naming
    /// the ordinal and the predecessor, and leaves the predecessor active; started again with the predecessor named for
    /// replacement, it registers the node its refused start created and retires the predecessor (orleans.md §5.3).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloOverAnEmptyRootAtAnActiveOrdinalStartsOnlyWhenItsPredecessorIsNamed()
    {
        _state.Ordinals["Primary"] = 0;
        var first = await DeployAsync(1);
        var predecessor = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        await DisposeClusterAsync(first);
        Directory.Delete(Path.Combine(_state.Roots, "Primary"), recursive: true);

        _ = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1));

        var refused = await _state.NodeAsync("Primary");
        Assert.NotEqual(predecessor.Node, refused.Node);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.Registered);
        Assert.Contains(predecessor.Node.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"RetireNodeAsync({predecessor.Node})", refusal.Message, StringComparison.Ordinal);
        var critical = Assert.Single(_state.Logs.Entries, static entry => entry.Level == LogLevel.Critical);
        Assert.Equal(refused.Node, critical.Fields["Node"]);
        Assert.Equal(0, critical.Fields["Ordinal"]);
        Assert.Equal(predecessor.Node, critical.Fields["Occupant"]);
        Assert.False(Assert.IsType<bool>(critical.Fields["HasEntry"]));
        Assert.Same(refusal, critical.Exception);
        var registry = await ReadRegistryAsync();
        Assert.Equal(predecessor, registry.Find(predecessor.Node));
        Assert.Null(registry.Find(refused.Node));

        _state.NodeToReplace = predecessor.Node;
        _ = await DeployAsync(1);

        var successor = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        Assert.Equal(refused.Node, successor.Node);
        registry = await ReadRegistryAsync();
        Assert.Equal(successor, registry.Find(successor.Node));
        Assert.Equal(registry.Revision, registry.Find(predecessor.Node)!.RetiredRevision);
        Assert.Contains(_state.Logs.Entries, entry => entry.Level == LogLevel.Warning && predecessor.Node.Equals(entry.Fields.GetValueOrDefault("Predecessor")) && successor.Node.Equals(entry.Fields.GetValueOrDefault("Node")));
    }

    /// <summary>
    /// orleans.md §5.3, §8: the disk-replacement procedure by identity. The operator retires the old node through the
    /// controller grain, whose call returns at once and which the registry then shows retired; a silo over the replaced,
    /// empty root at that ordinal then registers its new node there with no node named for replacement.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARetirementByIdentityFreesTheOrdinalForASiloOverAReplacedDisk()
    {
        _state.Ordinals["Primary"] = 0;
        var first = await DeployAsync(1);
        var predecessor = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        var controller = first.Client.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);

        await controller.RetireNodeAsync(predecessor.Node);

        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while ((await controller.GetRetirementAsync(predecessor.Node)).Value is not NodeStanding.Retired)
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, "The registry did not show the node retired in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        await DisposeClusterAsync(first);
        Directory.Delete(Path.Combine(_state.Roots, "Primary"), recursive: true);

        _ = await DeployAsync(1);

        var successor = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        Assert.NotEqual(predecessor.Node, successor.Node);
        Assert.Equal(0, successor.Ordinal);
        var registry = await ReadRegistryAsync();
        Assert.Equal(successor, registry.Find(successor.Node));
        Assert.True(registry.Find(predecessor.Node)!.IsRetired);
        Assert.DoesNotContain(_state.Logs.Entries, static entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>
    /// OR-I20, orleans.md §5.3: a silo joining a cluster whose node registry records the archive store registers with that
    /// identity, never one of its own: over another store, here an empty one, it registers at once, the registry keeps the
    /// cluster's store, and its own store's record, which its background check reads, plays no part in its registration.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AJoiningSiloRegistersWithTheRegistrysStoreNeverItsOwn()
    {
        var cluster = await DeployAsync(1);
        _ = await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var store = await RegisteredStoreAsync();
        _state.OtherObjects["Secondary_1"] = new InMemoryObjectStore();

        _ = await cluster.StartAdditionalSiloAsync();

        var joined = (await _state.Node("Secondary_1").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
        var registry = await ReadRegistryAsync();
        Assert.Equal(store, registry.ArchiveStore);
        Assert.False(registry.Find(joined.Node)?.IsRetired ?? true);
        Assert.Contains(_state.Logs.Entries, entry => entry.Fields.TryGetValue("Node", out var node) && Equals(node, joined.Node) && entry.Fields.TryGetValue("Store", out var id) && Equals(id, store.Id));
    }

    /// <summary>
    /// OR-I20's registration race: two silos of a new cluster, over two stores, of one layout or of two kinds, each find the
    /// registry empty and register with their own store's record. The first registration records its store; the other
    /// silo's is answered <c>WrongArchiveStore</c>, and it does not start, so a silo which ignored that answer, serving
    /// over a store the cluster's drops do not rest on, fails the test. The silos are of two Orleans clusters over one Clog
    /// cluster, each with its own controller grain, so neither's registration waits for the other's.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task TwoFirstRegistrationsOverTwoStoresRaceAndOnlyTheStoreRecordedFirstStarts(ArchiveStoreKind winnersKind)
    {
        var loser = new TestState(_state);
        var held = new HeldObjectStore(loser.Objects);
        loser.OtherObjects["Primary"] = held;
        States[loser.Id] = loser;
        _state.Kinds["Primary"] = winnersKind;
        try
        {
            var losing = DeployAsync(loser, 1);
            await held.Arrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            _ = await DeployAsync(1);
            var winner = (await _state.Node("Primary").Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Entry;
            var recorded = await RegisteredStoreAsync();
            held.Release();

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => losing.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            Assert.Contains(Chain(failure), static exception => exception is InvalidOperationException && exception.Message.Contains("another archive store", StringComparison.Ordinal)
                && exception.Message.Contains("/store'", StringComparison.Ordinal) && exception.Message.Contains("delete it by hand", StringComparison.Ordinal));
            var registry = await ReadRegistryAsync();
            Assert.Equal(recorded, registry.ArchiveStore);
            Assert.Equal(winnersKind, recorded.Kind);
            Assert.Equal([winner.Node], registry.Nodes.Select(static entry => entry.Node));
            Assert.NotEqual(recorded.Id, (await StoredRecordAsync(loser.Objects)).Id);
        }
        finally
        {
            held.Release();
            _ = States.TryRemove(loser.Id, out _);
            if (Directory.Exists(loser.Roots))
            {
                Directory.Delete(loser.Roots, recursive: true);
            }
        }
    }

    /// <summary>
    /// A silo configured with the memory store, which CLNR records for tests but no silo drains into, does not start: its
    /// options refuse it before it registers, so a first silo leaves the registry without an archive store or a node
    /// (OR-I20).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloConfiguredWithTheMemoryStoreDoesNotStartOrRegister()
    {
        _state.Kinds["Primary"] = ArchiveStoreKind.Memory;

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1).WaitAsync(TestContext.Current.CancellationToken));

        Assert.Contains(Chain(failure), static exception => exception is Microsoft.Extensions.Options.OptionsValidationException validation
            && validation.Failures.Any(static failure => failure.Contains($"'{nameof(DurableStorageOptions.ArchiveStore)}'", StringComparison.Ordinal)));
        var registry = await ReadRegistryAsync();
        Assert.Null(registry.ArchiveStore);
        Assert.Empty(registry.Nodes);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? next = exception; next is not null; next = next.InnerException)
        {
            yield return next;
            if (next is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// A silo's resolver which sees every active silo registered re-reads the registry on a membership change, not before:
    /// a node registered behind its back stays unresolved past the re-read interval, and resolves once a silo joins.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMembershipChangeMakesEverySiloRereadTheRegistry()
    {
        var cluster = await DeployAsync(2);
        var services = cluster.Silos.Select(static silo => ((InProcessSiloHandle)silo).SiloHost.Services).ToArray();
        var resolvers = services.Select(static provider => provider.GetRequiredService<NodeRegistryResolver>()).ToArray();
        var memberships = services.Select(static provider => provider.GetRequiredService<IClusterMembershipService>()).ToArray();
        MembershipVersion[] Versions() => [.. memberships.Select(static membership => membership.CurrentSnapshot.Version)];
        foreach (var silo in cluster.Silos)
        {
            _ = await _state.Node(silo.Name).Registered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        // Membership settles first: each update of the silos' joining requests a re-read. A re-read the resolvers still make
        // for an earlier update, which a loaded machine can delay, would read the new node early, so the quiet window is
        // the one in which no silo read the register, and a window with a read is tried again with another node.
        await EventuallyAsync(() => resolvers.All(resolver => !resolver.IsStale(resolver.Current)));
        var node = 0;
        for (var attempt = 0; node == 0; attempt++)
        {
            Assert.True(attempt < 10, "No silo stayed without a registry read for two re-read intervals.");
            var settled = Versions();
            var reads = _state.MetaStore.Reads;
            await using (var committer = Committer(await RegisterAsync()))
            {
                _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await committer.RegisterAsync(Entry(70 + attempt, 1), await RegisteredStoreAsync(), nodeToReplace: null, TestContext.Current.CancellationToken)).Value);
            }

            await Task.Delay(NodeRegistryResolver.RereadInterval * 2, TimeProvider.System, TestContext.Current.CancellationToken);
            if (reads == _state.MetaStore.Reads && settled.SequenceEqual(Versions()))
            {
                node = 70 + attempt;
            }
        }

        Assert.All(resolvers, resolver => Assert.Null(resolver.Resolve(Node(node))));

        _ = await cluster.StartAdditionalSiloAsync();

        await EventuallyAsync(() => resolvers.All(resolver => Silo(node, 1).Equals(resolver.Resolve(Node(node)))));
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, "The condition did not hold in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Calls the node's probe grain until it answers: a silo whose cached registry predates the node's registration refuses
    /// the first calls and re-reads the registry (orleans.md §5.3), and a call to a stopped silo's activation fails.
    /// </summary>
    private static async Task<SiloAddress> ProbeAsync(TestCluster cluster, NodeId node)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await cluster.Client.GetGrain<INodeProbeGrain>(node.Value).SiloAsync();
            }
            catch (Exception) when (attempt < 100)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }
    }

    private Task<TestCluster> DeployAsync(short silos) => DeployAsync(_state, silos);

    private async Task<TestCluster> DeployAsync(TestState state, short silos)
    {
        var builder = new TestClusterBuilder(silos);
        builder.Properties[TestKey] = state.Id;
        _ = builder.AddSiloBuilderConfigurator<NodeConfigurator>();
        _ = builder.AddClientBuilderConfigurator<ClientConfigurator>();
        var cluster = builder.Build();
        lock (_clusters)
        {
            _clusters.Add(cluster);
        }

        try
        {
            await cluster.DeployAsync();
        }
        catch (Exception)
        {
            // A failed start releases its silos' roots before the test starts another over them.
            await DisposeClusterAsync(cluster);
            throw;
        }

        return cluster;
    }

    private async Task DisposeClusterAsync(TestCluster cluster)
    {
        lock (_clusters)
        {
            _ = _clusters.Remove(cluster);
        }

        await cluster.StopAllSilosAsync();
        await cluster.DisposeAsync();
    }

    private ValueTask<IConditionalRegister> RegisterAsync() =>
        _state.MetaStore.Inner.GetRegisterAsync(_state.Cluster, NodeRegistryValue.RegisterName, TestContext.Current.CancellationToken);

    private async Task<NodeRegistryValue> ReadRegistryAsync() =>
        (await NodeRegistryCodec.ReadAsync(await RegisterAsync(), TestContext.Current.CancellationToken)).Value;

    /// <summary>Returns the archive store the node registry records, which the cluster's first registration recorded.</summary>
    private async Task<ArchiveStoreIdentity> RegisteredStoreAsync() => Assert.NotNull((await ReadRegistryAsync()).ArchiveStore);

    /// <summary>Returns the store's record which <paramref name="objects"/> holds, as the LSM names it.</summary>
    private static async Task<ArchiveStoreRecord> StoredRecordAsync(IObjectStore objects)
    {
        var stored = Assert.NotNull(await objects.ReadVersionedAsync(ObjectName.Store, TestContext.Current.CancellationToken));
        using var content = stored.Content;
        return Assert.IsType<ArchiveStoreRecord>(ArchiveStoreRecord.Decode(content.ToArray()).Value);
    }

    /// <summary>One test's MetaStore, cluster, node roots, and the nodes its silos built, by silo name.</summary>
    /// <param name="shared">The state whose cluster and MetaStore this one's silos share, or none for a cluster of its own.</param>
    private sealed class TestState(TestState? shared = null)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<LocalNode>> _nodes = new(StringComparer.Ordinal);

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = shared?.Cluster ?? Guid.NewGuid();

        public GatedMetaStore MetaStore { get; } = shared?.MetaStore ?? new();

        public string Roots { get; } = Path.Combine(Path.GetTempPath(), "clog-orleans-tests", Guid.NewGuid().ToString("N"));

        public ConcurrentDictionary<string, int> Ordinals { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the kind of archive store each silo selects, by name, or the LSM for a silo it does not name.</summary>
        public ConcurrentDictionary<string, ArchiveStoreKind> Kinds { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the object store each silo runs over, by name, or <see cref="Objects"/> for a silo it does not name.</summary>
        public ConcurrentDictionary<string, IObjectStore> OtherObjects { get; } = new(StringComparer.Ordinal);

        public InMemoryObjectStore Objects { get; } = new();

        /// <summary>Gets the archive logs of a silo on archive logs, in one container with its object store.</summary>
        public MemoryArchiveLogStore ArchiveLogs { get; } = new();

        public NodeId? NodeToReplace { get; set; }

        public RecordingLoggerProvider Logs { get; } = new();

        /// <summary>Returns the node the silo named <paramref name="silo"/> built most recently.</summary>
        public LocalNode Node(string silo) => NodeAsync(silo).Result;

        /// <summary>Waits for the silo named <paramref name="silo"/> to build its node.</summary>
        public Task<LocalNode> NodeAsync(string silo) =>
            _nodes.GetOrAdd(silo, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        public void Built(string silo, LocalNode node)
        {
            var built = new TaskCompletionSource<LocalNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            built.SetResult(node);
            _ = _nodes.AddOrUpdate(silo, built, (_, previous) => previous.TrySetResult(node) ? previous : built);
        }
    }

    /// <summary>
    /// The test's MetaStore: in memory, counting the silos' reads, and once <see cref="WriteGate"/> is set, each write to a register applies and then
    /// waits for the gate before it answers.
    /// </summary>
    private sealed class GatedMetaStore : IMetaStore
    {
        private readonly TaskCompletionSource _writeArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public InMemoryMetaStore Inner { get; } = new();

        public TaskCompletionSource? WriteGate { get; set; }

        /// <summary>The <see cref="RequestContext"/> key a test sets to see whether a caller's context reaches the register.</summary>
        public const string ProbeKey = "clog-test-probe";

        /// <summary>Gets or sets a value indicating whether each register read and write records its context.</summary>
        public bool RecordContexts { get; set; }

        /// <summary>Gets the probe value and the current Activity's name each recorded read and write saw.</summary>
        public IReadOnlyList<(object? Probe, string? Activity)> Contexts => [.. _contexts];

        private readonly ConcurrentQueue<(object? Probe, string? Activity)> _contexts = new();

        private void RecordContext()
        {
            if (RecordContexts)
            {
                _contexts.Enqueue((RequestContext.Get(ProbeKey), Activity.Current?.OperationName));
            }
        }

        public Task WriteArrived => _writeArrived.Task;

        /// <summary>Gets the number of reads the silos made of any register.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public async ValueTask<IConditionalRegister> GetRegisterAsync(Guid cluster, string name, CancellationToken cancellationToken = default) =>
            new GatedRegister(this, await Inner.GetRegisterAsync(cluster, name, cancellationToken).ConfigureAwait(false));

        private sealed class GatedRegister(GatedMetaStore store, IConditionalRegister register) : IConditionalRegister
        {
            public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
            {
                _ = Interlocked.Increment(ref store._reads);
                store.RecordContext();
                return register.ReadAsync(cancellationToken);
            }

            public async ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken = default)
            {
                store.RecordContext();
                var outcome = await register.WriteAsync(expectedVersion, value, cancellationToken).ConfigureAwait(false);
                if (store.WriteGate is { } gate)
                {
                    _ = store._writeArrived.TrySetResult();
                    await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return outcome;
            }
        }
    }

    /// <summary>Records the node a silo builds, under the silo's name, as the silo starts.</summary>
    private sealed class NodeCapture(LocalNode node, ILocalSiloDetails silo, IConfiguration configuration) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle observer) => States[configuration[TestKey]!].Built(silo.Name, node);
    }

    /// <summary>
    /// An object store whose creates of the LSM's store record wait until the test releases them, once the first arrived:
    /// a new cluster's first registration, held after it found the registry empty and before it creates the record.
    /// </summary>
    private sealed class HeldObjectStore(IObjectStore inner) : IObjectStore
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public void Release() => _released.TrySetResult();

        public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            if (name == ObjectName.Store)
            {
                _ = _arrived.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.CreateAsync(name, content, crc32c, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) => inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    private sealed class NodeConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var name = siloBuilder.Configuration["Orleans:Name"]!;
            var root = Path.Combine(state.Roots, name);
            _ = Directory.CreateDirectory(root);
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(state.MetaStore);
            var bucket = new PrefixStrippingObjectStore(state.OtherObjects.TryGetValue(name, out var other) ? other : state.Objects, "t/");
            _ = siloBuilder.Services.AddSingleton<IObjectStore>(bucket);
            _ = siloBuilder.Services.AddSingleton<IArchiveLogContainer>(new TestArchiveLogContainer("t", () => state.ArchiveLogs, () => bucket));
            _ = siloBuilder.AddDurableJournalStorageWith(new DurableStorageSiloOptions
            {
                Node = new DurableStorageOptions
                {
                    Cluster = state.Cluster,
                    Root = root,
                    Ordinal = state.Ordinals.TryGetValue(name, out var ordinal) ? ordinal : null,
                    NodeToReplace = state.NodeToReplace?.Value,
                    WalFileSize = 1 << 20,
                    PreallocateWalFiles = false,
                    ArchiveStore = state.Kinds.TryGetValue(name, out var kind) ? kind : ArchiveStoreKind.Lsm,
                    ArchiveStorePrefix = "t",
                },
            });
            _ = siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, NodeCapture>();
            _ = siloBuilder.Services.AddSingleton<ILoggerProvider>(state.Logs);
        }
    }

    /// <summary>Records every silo's <see cref="DurableStorageNode"/> log entries: level, structured fields, and exception.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(DurableStorageNode).FullName ? new Logger(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var fields = state is IReadOnlyList<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value) : [];
                lock (provider._lock)
                {
                    provider._entries.Add(new LogEntry(logLevel, fields, exception));
                }
            }
        }
    }

    /// <summary>One recorded log entry.</summary>
    private sealed record LogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> Fields, Exception? Exception);

    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
            _ = clientBuilder.Services.Configure<ExceptionSerializationOptions>(static serialization => serialization.SupportedNamespacePrefixes.Add("Orleans.Durable.Storage"));
    }
}
