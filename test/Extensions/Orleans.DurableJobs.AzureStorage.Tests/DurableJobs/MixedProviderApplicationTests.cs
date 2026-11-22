using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Durable.Messaging;
using Orleans.Durable.Storage;
using Orleans.Durable.Storage.CasPaxos.Hosting;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Tasks;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

/// <summary>Where Durable Storage keeps its metadata registers in a <see cref="MixedProviderApplicationTests"/> deployment.</summary>
public enum MixedProviderMetadata
{
    /// <summary>One in-memory MetaStore which every silo shares.</summary>
    InMemory,

    /// <summary>The native CASPaxos MetaStore, whose three voters the silos host.</summary>
    NativeConsensus,
}

/// <summary>
/// Applications in the initial mixed-provider deployment: application journals, which hold journaled state, Messaging's
/// inboxes and outboxes, and Durable Task state, use Durable Storage, while Durable Jobs, which wakes grains with
/// scheduled messages and durable delays, keeps its shards in a named Azure Blob journal provider. Membership lies in
/// Azure Table storage, so every silo starts together, as the native consensus voters need.
/// </summary>
[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestArea("DurableJobs")]
public sealed class MixedProviderApplicationTests : IAsyncLifetime
{
    private const string TestKey = "MixedProviderApplicationTest";
    private const string JobsProvider = "jobs";
    private const int Silos = 3;
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, TestState> States = new(StringComparer.Ordinal);

    private readonly TestState _state = new();
    private TestCluster? _cluster;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        States[_state.Id] = _state;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cluster is { } cluster)
            {
                await cluster.StopAllSilosAsync();
                await cluster.DisposeAsync();
            }

            if (_state.ContainerName is { } containerName)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await CreateContainerClient(containerName).DeleteIfExistsAsync(cancellationToken: cts.Token);
            }
        }
        finally
        {
            States.TryRemove(_state.Id, out _);
            try
            {
                Directory.Delete(_state.Roots, recursive: true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // A killed in-process silo keeps its files open until the process exits; the temporary directory remains.
            }
        }
    }

    /// <summary>
    /// A silo crashes while its grains hold a scheduled inbox message and a durable task waiting on a durable delay. With no
    /// call to those grains, the Durable Jobs wakeups, which survivors adopt from the lost silo's Azure shards, reactivate
    /// them on survivors, which recover their journals from Durable Storage: the inbox processes the scheduled message once,
    /// at or after its delivery time, and the durable task resumes and completes. The grains' journaled state then replays
    /// to the same values in fresh activations.
    /// </summary>
    [Theory(Timeout = 900_000), TestCategory("Azure"), TestCategory("DurableJobs")]
    [InlineData(MixedProviderMetadata.InMemory)]
    [InlineData(MixedProviderMetadata.NativeConsensus)]
    public async Task ApplicationsRecoverFromASiloLossThroughDurableJobsWakeups(MixedProviderMetadata metadata)
    {
        TestUtils.CheckForAzureStorage();
        var token = TestContext.Current.CancellationToken;
        _state.Metadata = metadata;
        _state.ContainerName = $"mixedapp-{_state.Id}";
        var cluster = await DeployAsync();
        var survivor = cluster.Silos[0];
        var victim = cluster.Silos[1];
        var victimAddress = victim.SiloAddress.ToParsableString();

        // The receiving grains activate on the silo which will crash; the sender lives on a survivor.
        var warehouse = Grain<IWarehouseGrain>(victim, "east");
        var fulfillment = Grain<IFulfillmentGrain>(victim, "order-1");
        Assert.Equal(victimAddress, await EventuallyAsync(async () => await warehouse.GetSiloAsync(), "the warehouse's first activation"));
        Assert.Equal(victimAddress, await EventuallyAsync(async () => await fulfillment.GetSiloAsync(), "the fulfillment's first activation"));
        var dispatch = Grain<IDispatchGrain>(survivor, "dispatch");

        var due = Now() + Settle;
        await dispatch.SendAsync("now", warehouse.GetGrainId(), new("anvil", 1), deliverAt: null);
        await dispatch.SendAsync("later", warehouse.GetGrainId(), new("rocket", 2), due);
        _ = await fulfillment.FulfillAsync(3, Settle).WithId("fulfillment").ScheduleAsync(token);
        var now = Shipped("now");
        var later = Shipped("later");
        _ = await EventuallyAsync(() => Task.FromResult(_state.Probe.Find(now)), "the immediate message to be processed");
        _ = await EventuallyAsync(() => Task.FromResult(_state.Probe.Find("fulfilling:order-1")), "the fulfillment to start");
        Assert.All(_state.Probe.Events, entry => Assert.Equal(victimAddress, entry.Silo));

        // Outbox delivery is ordered per receiver: once the immediate message has been handled, the scheduled one follows.
        // Give its acceptance, and the wakeup it schedules in the victim's Azure shard, time to commit.
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        Assert.Null(_state.Probe.Find(later));
        await cluster.KillSiloAsync(victim, token);

        // No call reaches the warehouse or the fulfillment: their Durable Jobs wakeups reactivate them on survivors.
        var shipped = await EventuallyAsync(() => Task.FromResult(_state.Probe.Find(later)), "the scheduled message's wakeup");
        Assert.NotEqual(victimAddress, shipped.Silo);
        Assert.True(shipped.At >= due, $"The scheduled message was processed at {shipped.At:O}, before its delivery time {due:O}.");
        var fulfilled = await EventuallyAsync(() => Task.FromResult(_state.Probe.Find("fulfilled:order-1")), "the durable delay's wakeup");
        Assert.NotEqual(victimAddress, fulfilled.Silo);

        // Each survivor activation follows its wakeup's due time: the jobs woke the grains, not a redelivery or a caller.
        AssertWokenAfter("activated:warehouse:east", due);
        AssertWokenAfter("activated:fulfillment:order-1", _state.Probe.Find("fulfilling:order-1")!.At + Settle);

        // The durable task's result, read from its grain's journal on a survivor.
        var result = await Grain<IFulfillmentGrain>(survivor, "order-1").GetDurableTask<int>("fulfillment").WaitAsync(token);
        Assert.Equal(6, result);

        // Journaled state: each message once, the scheduled one handled on a survivor, and the same values after replay.
        var expected = await EventuallyAsync(async () => await Grain<IWarehouseGrain>(survivor, "east").GetShipmentsAsync(), "the warehouse's journal");
        Assert.Equal(new[] { now["shipped:".Length..], later["shipped:".Length..] }, expected.Select(static shipment => shipment.MessageId));
        Assert.Equal(victimAddress, expected[0].Silo);
        Assert.Equal(shipped.Silo, expected[1].Silo);
        Assert.Equal(new[] { "now", "later" }, await dispatch.GetSentAsync());
        Assert.Single(_state.Probe.Events, entry => entry.Event == later);

        await Grain<IWarehouseGrain>(survivor, "east").DeactivateAsync();
        await dispatch.DeactivateAsync();
        Assert.Equal(expected, await EventuallyAsync(async () => await Grain<IWarehouseGrain>(survivor, "east").GetShipmentsAsync(), "the warehouse to replay its journal"));
        Assert.Equal(new[] { "now", "later" }, await EventuallyAsync(async () => await dispatch.GetSentAsync(), "the dispatcher to replay its journal"));
    }

    private void AssertWokenAfter(string activation, DateTimeOffset due)
    {
        var victim = _state.Probe.Events[0].Silo;
        var woken = _state.Probe.Events.First(entry => entry.Event == activation && entry.Silo != victim);
        Assert.True(woken.At >= due - TimeSpan.FromSeconds(1), $"{activation} on {woken.Silo} at {woken.At:O} preceded its wakeup's due time {due:O}.");
    }

    private static string Shipped(string messageId) => $"shipped:{MessageId.Create("shipments", messageId).Key}";

    private async Task<TestCluster> DeployAsync()
    {
        var builder = new TestClusterBuilder(Silos);
        builder.Options.UseTestClusterMembership = false;
        builder.Options.InitializeClientOnDeploy = false;
        builder.Properties[TestKey] = _state.Id;
        _ = builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync(Token).WaitAsync(Patience, Token);
        return _cluster;
    }

    private static T Grain<T>(SiloHandle silo, string key)
        where T : IGrainWithStringKey =>
        ((InProcessSiloHandle)silo).SiloHost.Services.GetRequiredService<IGrainFactory>().GetGrain<T>(key);

    private static DateTimeOffset Now() => TimeProvider.System.GetUtcNow();

    /// <summary>
    /// Observes until <paramref name="observe"/> answers. A failed observation is retried: a grain's activation fails while
    /// Durable Storage takes over the ledgers of a lost silo.
    /// </summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> observe, string what)
        where T : class
    {
        var deadline = Now() + Patience;
        Exception? lastFailure = null;
        while (Now() < deadline)
        {
            try
            {
                if (await observe() is { } result)
                {
                    return result;
                }
            }
            catch (Exception exception) when (!Token.IsCancellationRequested)
            {
                lastFailure = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Token);
        }

        Assert.Fail($"Timed out after {Patience} waiting for {what}. Last failure: {lastFailure}");
        throw new UnreachableException();
    }

    private static TableServiceClient CreateTableServiceClient() =>
        TestDefaultConfiguration.UseAadAuthentication
            ? new TableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential)
            : new TableServiceClient(TestDefaultConfiguration.DataConnectionString);

    private static BlobContainerClient CreateContainerClient(string containerName) =>
        TestDefaultConfiguration.UseAadAuthentication
            ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, containerName), TestDefaultConfiguration.TokenCredential)
            : new BlobContainerClient(TestDefaultConfiguration.DataConnectionString, containerName);

    private sealed class TestState
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public string Roots { get; } = Path.Combine(Path.GetTempPath(), "orleans-mixed-app", Guid.NewGuid().ToString("N"));

        public MixedProviderMetadata Metadata { get; set; }

        public InMemoryMetaStore MetaStore { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        public ApplicationProbe Probe { get; } = new();

        public string? ContainerName { get; set; }
    }

    /// <summary>
    /// Configures each silo's membership in Azure Table storage, its application journals in Durable Storage over the
    /// test's metadata, and Durable Jobs in the named Azure Blob provider. Silo <c>i</c> listens on the cluster's base
    /// port plus <c>i</c>, so every silo derives every voter's endpoint.
    /// </summary>
    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var basePort = int.Parse(siloBuilder.Configuration["BaseSiloPort"]!, CultureInfo.InvariantCulture);
            var ordinal = int.Parse(siloBuilder.Configuration["Orleans:Endpoints:SiloPort"]!, CultureInfo.InvariantCulture) - basePort;
            var name = siloBuilder.Configuration["Orleans:Name"]!;
            var root = Directory.CreateDirectory(Path.Combine(state.Roots, name)).FullName;

            _ = siloBuilder.UseAzureStorageClustering(options => options.TableServiceClient = CreateTableServiceClient());
            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });
            _ = siloBuilder.Services.AddSingleton(state.Probe);
            _ = siloBuilder.Services.AddSingleton<ApplicationMessages>();

            // Application journals: Durable Storage under the default provider name, over the test's metadata.
            if (state.Metadata == MixedProviderMetadata.NativeConsensus)
            {
                _ = siloBuilder.AddCasPaxosMetaStore(options => options.Configure(consensus =>
                {
                    for (var voter = 0; voter < Silos; voter++)
                    {
                        consensus.Voters.Add(new CasPaxosVoterOptions
                        {
                            Id = string.Create(CultureInfo.InvariantCulture, $"voter-{voter}"),
                            Endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{basePort + voter}"),
                            Database = Path.Combine(state.Roots, string.Create(CultureInfo.InvariantCulture, $"voter-{voter}.db")),
                        });
                    }

                    consensus.ProposerId = ordinal + 1;
                    consensus.ProposerDatabase = Path.Combine(state.Roots, $"proposer-{name}.db");
                }));
            }
            else
            {
                _ = siloBuilder.Services.AddSingleton<IMetaStore>(state.MetaStore);
            }

            _ = siloBuilder.Services.AddSingleton<IObjectStore>(state.Objects);
            _ = siloBuilder.AddDurableJournalStorage(options => options.Configure(silo =>
            {
                silo.Node = new DurableStorageOptions
                {
                    Cluster = state.Cluster,
                    Root = root,
                    Ordinal = ordinal,
                    WalFileSize = 1 << 20,
                    PreallocateWalFiles = false,
                    ArchiveStore = ArchiveStoreKind.Lsm,
                    ArchiveStorePrefix = "mixed-app",
                    MinimumFreeDiskBytes = 0,
                };
                silo.Control = new ControlOptions
                {
                    LedgersPerNode = 1,
                    SequencerWindow = 4,
                    TransitionTimeout = TimeSpan.FromSeconds(10),
                    ControlInterval = TimeSpan.FromMilliseconds(200),
                    BalanceInterval = TimeSpan.FromMilliseconds(200),
                    MapRefreshInterval = TimeSpan.FromSeconds(2),
                    ReplicaSweepInterval = TimeSpan.FromSeconds(1),
                    ManifestCommitInterval = TimeSpan.Zero,
                    SweepInterval = TimeSpan.FromMilliseconds(200),
                };
            }));
            _ = siloBuilder.Services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(MixedProviderApplicationJsonContext.Default));

            // Durable Jobs, which wakes Messaging and Durable Tasks: a named Azure Blob journal provider.
            _ = siloBuilder
                .AddAzureBlobJournalStorage(JobsProvider, options =>
                {
                    options.ConfigureTestDefaults();
                    options.ContainerName = state.ContainerName!;
                })
                .UseJournaledDurableJobs(options =>
                {
                    options.ActiveProviderName = JobsProvider;
                    options.ShardDuration = TimeSpan.FromMinutes(1);
                    options.ShardCheckInterval = TimeSpan.FromSeconds(2);
                    options.ShardClaimRampUpDuration = TimeSpan.Zero;
                })
                .AddDurableTasks();
        }
    }
}
