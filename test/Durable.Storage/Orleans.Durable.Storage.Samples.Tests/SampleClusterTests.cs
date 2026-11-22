using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Samples.Journaling;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Samples.Tests;

/// <summary>
/// The sample's silo configuration, its <c>appsettings.json</c> and <c>appsettings.Development.json</c>, as the AppHost
/// runs it, and <see cref="SampleSilo.AddSampleJournaling"/> over
/// in-memory stores in place of Azure Storage's, starts a three-silo cluster in which a journaled grain recovers its state
/// on another silo once its own is killed, and the killed silo's restart over its node root rejoins the cluster
/// (orleans.md §5.7, R2).
/// </summary>
public sealed class SampleClusterTests
{
    private const string TestKey = "DurableStorage:Samples:Test";

    private static readonly ConcurrentDictionary<string, SampleState> States = new(StringComparer.Ordinal);

    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(Timeout = 300_000)]
    public async Task AGrainRecoversItsStateAfterItsSiloIsKilledAndRestarted()
    {
        var token = TestContext.Current.CancellationToken;
        var state = new SampleState();
        States[state.Id] = state;
        try
        {
            var builder = new TestClusterBuilder(3);
            builder.Properties[TestKey] = state.Id;
            _ = builder.AddSiloBuilderConfigurator<SampleSiloConfigurator>();
            await using var cluster = builder.Build();
            await cluster.DeployAsync(token).WaitAsync(Patience, token);
            var (grain, host) = await GrainOnASecondaryAsync(cluster);
            await grain.SetAsync("a", "1");
            await grain.SetAsync("b", "2");

            await cluster.KillSiloAsync(host, token).WaitAsync(Patience, token);

            var recovered = await EventuallyAsync(grain.ReadAsync, "the grain's state to recover on another silo");
            Assert.NotEqual(host.Name, recovered.Silo);
            Assert.Equal([new("a", "1"), new("b", "2")], recovered.Entries);
            await grain.SetAsync("c", "3");

            var restarted = Assert.IsType<SiloHandle>(await cluster.RestartSiloAsync(host).WaitAsync(Patience, token), exactMatch: false);

            var after = await EventuallyAsync(grain.ReadAsync, "the grain to answer once the killed silo restarted");
            Assert.Equal([new("a", "1"), new("b", "2"), new("c", "3")], after.Entries);
            Assert.Equal(host.Name, restarted.Name);
            Assert.Equal(3, cluster.GetActiveSilos().Count());
        }
        finally
        {
            _ = States.TryRemove(state.Id, out _);
            state.Dispose();
        }
    }

    /// <summary>
    /// Returns a grain whose activation runs on a secondary silo, whose death the cluster survives: the primary hosts the
    /// test cluster's membership.
    /// </summary>
    private static async Task<(IKeyValueGrain Grain, SiloHandle Host)> GrainOnASecondaryAsync(TestCluster cluster)
    {
        for (var index = 0; index < 64; index++)
        {
            var grain = cluster.Client.GetGrain<IKeyValueGrain>(string.Create(CultureInfo.InvariantCulture, $"cart-{index}"));
            var silo = (await EventuallyAsync(grain.ReadAsync, "the grain to activate")).Silo;
            if (cluster.GetActiveSilos().FirstOrDefault(handle => handle.Name == silo) is { } host && host != cluster.Primary)
            {
                return (grain, host);
            }
        }

        throw new InvalidOperationException("No grain activated on a secondary silo.");
    }

    /// <summary>Calls <paramref name="call"/> until it answers, within <see cref="Patience"/>.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> call, string what)
    {
        var deadline = TimeProvider.System.GetTimestamp() + (long)(Patience.TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (true)
        {
            try
            {
                return await call().WaitAsync(Patience, Token);
            }
            catch (Exception failure) when (failure is not OperationCanceledException && TimeProvider.System.GetTimestamp() < deadline)
            {
                TestContext.Current.SendDiagnosticMessage($"Waiting for {what}: {failure.GetType().Name}: {failure.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Token);
            }
        }
    }

    /// <summary>One test's MetaStore and object store, which every silo shares, and the directory of its node roots.</summary>
    private sealed class SampleState : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public InMemoryMetaStore MetaStore { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        public string Roots { get; } = Directory.CreateTempSubdirectory("clog-sample-").FullName;

        public void Dispose() => Directory.Delete(Roots, recursive: true);
    }

    /// <summary>
    /// Configures each silo as the sample's Program does, from its settings in the Development environment, with the node's cluster, root, and
    /// ordinal which the AppHost passes, and the test's stores in place of Azure Storage's.
    /// </summary>
    private sealed class SampleSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var name = siloBuilder.Configuration["Orleans:Name"]!;
            var ordinal = name.StartsWith("Secondary_", StringComparison.Ordinal) ? int.Parse(name["Secondary_".Length..], CultureInfo.InvariantCulture) : 0;
            var root = Directory.CreateDirectory(Path.Combine(state.Roots, name)).FullName;
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [NodeKey(nameof(DurableStorageOptions.Cluster))] = state.Cluster.ToString(),
                    [NodeKey(nameof(DurableStorageOptions.Root))] = root,
                    [NodeKey(nameof(DurableStorageOptions.Ordinal))] = ordinal.ToString(CultureInfo.InvariantCulture),
                })
                .Build();
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(state.MetaStore);
            _ = siloBuilder.Services.AddSingleton<IObjectStore>(state.Objects);
            _ = siloBuilder.AddSampleJournaling(configuration);

            // Membership declares a killed silo dead within seconds, which starts the takeovers of its ledgers.
            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });
        }

        private static string NodeKey(string option) => ConfigurationPath.Combine(SampleSilo.Section, nameof(DurableStorageSiloOptions.Node), option);
    }
}
