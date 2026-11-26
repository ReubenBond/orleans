using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
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

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// An <c>Orleans.TestingHost</c> cluster whose silos each run a Clog node over a root of their own and one in-memory
/// MetaStore (orleans.md §9), with each silo's replica transport, replica host, and node reachable by the silo's name.
/// </summary>
/// <remarks>
/// Every silo runs each grain call through a callee filter, which counts the pages a call rents under
/// <see cref="CalleeAccounting"/>, answers an <see cref="ILedgerStoreGrain"/> call by <see cref="CalleeScript"/> in place of
/// its host, or injects <see cref="Faults"/>' faults around it, and holds a call at its callee or at its caller while a
/// test asks (<see cref="CalleeHold"/>, <see cref="CallerHold"/>). Disposing the cluster first checks that every running silo
/// is quiescent: each host holds no admission, and each transport no grant and no sender copy. The cluster controller's
/// balancing is paused (<c>MaxConcurrentTransitions</c> 0), so it bootstraps no ledger and places nothing on any store:
/// every replica a store holds is one its test opened.
/// </remarks>
internal sealed class ReplicaCluster : IAsyncDisposable
{
    private const string TestKey = "DurableStorageReplicaTest";

    private static readonly ConcurrentDictionary<string, ReplicaCluster> Clusters = new(StringComparer.Ordinal);

    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, IServiceProvider> _silos = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _calleeCalls = new(StringComparer.Ordinal);
    private readonly Action<DurableStorageOptions>? _configure;
    private readonly Action<ISiloBuilder>? _configureSilo;
    private TestCluster? _cluster;

    private ReplicaCluster(Action<DurableStorageOptions>? configure, Action<ISiloBuilder>? configureSilo)
    {
        _configure = configure;
        _configureSilo = configureSilo;
    }

    /// <summary>Gets the cluster's Clog cluster identity.</summary>
    public Guid Cluster { get; } = Guid.NewGuid();

    /// <summary>Gets the MetaStore every silo shares.</summary>
    public InMemoryMetaStore MetaStore { get; } = new();

    /// <summary>Gets the LSM's object store every silo shares.</summary>
    public InMemoryObjectStore Objects { get; } = new();

    /// <summary>Gets the directory holding every silo's node root.</summary>
    public string Roots { get; } = Path.Combine(Path.GetTempPath(), "clog-orleans-replica-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Gets or sets the accounting which counts the pages each grain call rents at its callee, or null.</summary>
    public ArcBufferPageAccounting? CalleeAccounting { get; set; }

    /// <summary>
    /// Gets or sets the answer each <see cref="ILedgerStoreGrain"/> call gets at its callee in place of its host's, from its
    /// request: a value of the call's answer union, or an exception to throw; null runs the host.
    /// </summary>
    public Func<object, object>? CalleeScript { get; set; }

    /// <summary>Gets or sets the faults injected around <see cref="ILedgerStoreGrain"/> calls at their callee, or null.</summary>
    public CalleeFaults? Faults { get; set; }

    /// <summary>
    /// Gets or sets what each <see cref="ILedgerStoreGrain"/> call awaits at its callee, from its request, before the callee
    /// runs it, whatever its caller's cancellation; or null, so no call waits.
    /// </summary>
    public Func<object, Task>? CalleeHold { get; set; }

    /// <summary>
    /// Gets or sets what each <see cref="ILedgerStoreGrain"/> call awaits in its caller's outgoing filter, from its request,
    /// before Orleans sends it, whatever its caller's cancellation; or null, so no call waits.
    /// </summary>
    public Func<object, Task>? CallerHold { get; set; }

    /// <summary>Gets the TestingHost cluster.</summary>
    public TestCluster TestCluster => _cluster ?? throw new InvalidOperationException("The cluster is not deployed.");

    /// <summary>Gets the names of the running silos, the primary first.</summary>
    public IReadOnlyList<string> SiloNames => [.. TestCluster.Silos.Select(static silo => silo.Name)];

    /// <summary>
    /// Deploys <paramref name="silos"/> silos, waits until each node is registered and every silo resolves every node, and
    /// returns the cluster.
    /// </summary>
    public static async Task<ReplicaCluster> DeployAsync(short silos, Action<DurableStorageOptions>? configure = null, Action<ISiloBuilder>? configureSilo = null)
    {
        var cluster = new ReplicaCluster(configure, configureSilo);
        Clusters[cluster._id] = cluster;
        try
        {
            var builder = new TestClusterBuilder(silos);
            builder.Properties[TestKey] = cluster._id;
            _ = builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            _ = builder.AddClientBuilderConfigurator<ClientConfigurator>();
            cluster._cluster = builder.Build();
            await cluster._cluster.DeployAsync();
            await cluster.WaitUntilResolvedAsync();
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns the services of the silo named <paramref name="silo"/>.</summary>
    public IServiceProvider Services(string silo) => _silos[silo];

    /// <summary>Returns the replica transport of the silo named <paramref name="silo"/>.</summary>
    public OrleansLedgerReplicaTransport Transport(string silo) => Services(silo).GetRequiredService<OrleansLedgerReplicaTransport>();

    /// <summary>Returns the replica host of the silo named <paramref name="silo"/>.</summary>
    public LedgerReplicaHost Host(string silo) => Services(silo).GetRequiredService<LedgerReplicaHost>();

    /// <summary>Returns the node of the silo named <paramref name="silo"/>.</summary>
    public LocalNode Node(string silo) => Services(silo).GetRequiredService<LocalNode>();

    /// <summary>Returns the number of <see cref="ILedgerStoreGrain"/> calls which reached the silo named <paramref name="silo"/>.</summary>
    public int CalleeCalls(string silo) => _calleeCalls.GetValueOrDefault(silo);

    /// <summary>Returns the registry resolver of the silo named <paramref name="silo"/>.</summary>
    public NodeRegistryResolver Resolver(string silo) => Services(silo).GetRequiredService<NodeRegistryResolver>();

    /// <summary>Waits until every running silo's node is registered and every silo resolves every node to its silo.</summary>
    public async Task WaitUntilResolvedAsync()
    {
        var nodes = new List<(NodeId Node, SiloAddress Silo)>();
        foreach (var silo in TestCluster.Silos)
        {
            var entry = (await Node(silo.Name).Registered.WaitAsync(TimeSpan.FromSeconds(30))).Entry;
            nodes.Add((entry.Node, entry.Silo));
        }

        await EventuallyAsync(() => TestCluster.Silos.All(silo => nodes.All(node => node.Silo.Equals(Resolver(silo.Name).Resolve(node.Node)))));
    }

    /// <summary>Waits until <paramref name="condition"/> holds, failing after 30 seconds.</summary>
    public static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, "The condition did not hold in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System);
        }
    }

    /// <summary>
    /// Waits until every running silo is quiescent, failing after 30 seconds: its host holds no admitted call or byte, and
    /// its transport no grant or waiting call.
    /// </summary>
    public Task AssertQuiescentAsync() => EventuallyAsync(() => TestCluster.Silos.All(silo =>
        Host(silo.Name) is { Calls: 0, Bytes: 0 }
        && Transport(silo.Name).Lanes.All(static lane => lane is { OutstandingBytes: 0, OutstandingCalls: 0, Waiting: 0 })));

    public async ValueTask DisposeAsync()
    {
        if (_cluster is { } cluster)
        {
            try
            {
                if (Faults is { } faults)
                {
                    await faults.DeliverAsync();
                }

                await AssertQuiescentAsync();
            }
            finally
            {
                await cluster.StopAllSilosAsync();
                await cluster.DisposeAsync();
            }
        }

        _ = Clusters.TryRemove(_id, out _);
        if (Directory.Exists(Roots))
        {
            Directory.Delete(Roots, recursive: true);
        }
    }

    /// <summary>Runs a call which reached a silo, under <see cref="CalleeAccounting"/>, as the cluster's hooks direct.</summary>
    private Task InvokeAsync(IIncomingGrainCallContext context, string silo)
    {
        if (context.Grain is not ILedgerStoreGrain grain)
        {
            return context.Invoke();
        }

        _ = _calleeCalls.AddOrUpdate(silo, 1, static (_, calls) => calls + 1);
        var request = context.Request.GetArgument(0)!;
        if (CalleeHold is { } hold)
        {
            return HeldAsync(hold(request), context, grain, request);
        }

        return RunAsync(context, grain, request);
    }

    private async Task HeldAsync(Task held, IIncomingGrainCallContext context, ILedgerStoreGrain grain, object request)
    {
        await held;
        await RunAsync(context, grain, request);
    }

    /// <summary>Runs a call which reached a silo as <see cref="CalleeScript"/> or <see cref="Faults"/> direct.</summary>
    private Task RunAsync(IIncomingGrainCallContext context, ILedgerStoreGrain grain, object request)
    {
        if (CalleeScript is { } script)
        {
            // The script answers in place of the host, so it releases the payload the host would have (OR-I18).
            if (request is LedgerReplicaWriteRequest write)
            {
                write.Payload.Dispose();
            }

            var answer = script(request);
            if (answer is Exception failure)
            {
                return Task.FromException(failure);
            }

            context.Result = context.InterfaceMethod.Name switch
            {
                nameof(ILedgerStoreGrain.WriteAsync) => GrainDouble.WriteOf(answer),
                nameof(ILedgerStoreGrain.ReadAsync) => GrainDouble.ReadOf(answer),
                nameof(ILedgerStoreGrain.ReadTailAsync) => GrainDouble.TailOf(answer),
                var method => throw new InvalidOperationException($"No replica call {method}."),
            };
            return Task.CompletedTask;
        }

        return Faults is { } faults ? faults.InvokeAsync(context, grain, request) : context.Invoke();
    }

    /// <summary>The callee filter every silo runs.</summary>
    private sealed class CalleeFilter(IConfiguration configuration, ILocalSiloDetails silo) : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context)
        {
            var cluster = Clusters[configuration[TestKey]!];
            return cluster.CalleeAccounting is { } accounting
                ? ArcBufferLeakCheck.Run(accounting, () => cluster.InvokeAsync(context, silo.Name))
                : cluster.InvokeAsync(context, silo.Name);
        }
    }

    /// <summary>The caller filter every silo runs, which holds a call as <see cref="CallerHold"/> directs.</summary>
    private sealed class CallerFilter(IConfiguration configuration) : IOutgoingGrainCallFilter
    {
        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.InterfaceMethod.DeclaringType == typeof(ILedgerStoreGrain) && Clusters[configuration[TestKey]!].CallerHold is { } hold)
            {
                await hold(context.Request.GetArgument(0)!);
            }

            await context.Invoke();
        }
    }

    /// <summary>Records each silo's services under the silo's name as the silo starts.</summary>
    private sealed class ServicesCapture(IServiceProvider services, ILocalSiloDetails silo, IConfiguration configuration) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle observer) => Clusters[configuration[TestKey]!]._silos[silo.Name] = services;
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var cluster = Clusters[siloBuilder.Configuration[TestKey]!];
            var name = siloBuilder.Configuration["Orleans:Name"]!;
            var root = Path.Combine(cluster.Roots, name);
            _ = Directory.CreateDirectory(root);
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(cluster.MetaStore);
            _ = siloBuilder.Services.AddSingleton<IObjectStore>(cluster.Objects);
            var options = new DurableStorageOptions
            {
                Cluster = cluster.Cluster,
                Root = root,
                WalFileSize = 1 << 20,
                PreallocateWalFiles = false,
                ArchiveStore = Orleans.Durable.Storage.Engine.Archive.ArchiveStoreKind.Lsm,
            };
            cluster._configure?.Invoke(options);
            _ = siloBuilder.AddDurableJournalStorageWith(new DurableStorageSiloOptions { Node = options, Control = { MaxConcurrentTransitions = 0 } });
            _ = siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, ServicesCapture>();
            _ = siloBuilder.AddIncomingGrainCallFilter<CalleeFilter>();
            _ = siloBuilder.AddOutgoingGrainCallFilter<CallerFilter>();
            cluster._configureSilo?.Invoke(siloBuilder);
        }
    }

    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
            _ = clientBuilder.Services.Configure<ExceptionSerializationOptions>(static serialization => serialization.SupportedNamespacePrefixes.Add("Orleans.Durable.Storage"));
    }
}
