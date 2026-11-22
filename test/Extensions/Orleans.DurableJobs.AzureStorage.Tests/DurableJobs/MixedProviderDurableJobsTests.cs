using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Durable.Storage;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

/// <summary>
/// The initial mixed-provider deployment: Durable Jobs keeps its shards in a named Azure Blob journal provider while the
/// silos' application journals, under the default provider name, use Durable Storage.
/// </summary>
[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestArea("DurableJobs")]
public sealed class MixedProviderDurableJobsTests : IAsyncLifetime
{
    private const string TestKey = "MixedProviderDurableJobsTest";
    private const string JobsProvider = "jobs";
    private const string OwnerProperty = "DurableJobsOwner";
    private const string AdoptedCountProperty = "DurableJobsAdoptedCount";
    private const string ClosedProperty = "DurableJobsClosed";
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
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
            if (Directory.Exists(_state.Roots))
            {
                Directory.Delete(_state.Roots, recursive: true);
            }
        }
    }

    /// <summary>
    /// Jobs shards are created, discovered, claimed, released, and adopted in the named Azure provider, and each job's
    /// retries and cancellation stay with its shard there, while the grains the jobs target recover their journals from
    /// Durable Storage across a graceful stop and a silo loss.
    /// </summary>
    [Fact(Timeout = 600_000), TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobsShardsLiveInTheNamedAzureProviderWhileApplicationJournalsUseDurableStorage()
    {
        TestUtils.CheckForAzureStorage();
        var token = TestContext.Current.CancellationToken;
        _state.ContainerName = $"mixedjobs-{_state.Id}";
        var cluster = await DeployAsync(4);
        var survivor = cluster.Primary!;

        // Creation: a grain on a secondary silo schedules the first job, which creates a shard that silo owns.
        var releasing = cluster.SecondarySilos[0];
        var released = Grain(releasing, "released");
        var releasedJob = await released.ScheduleAsync("released", Now() + TimeSpan.FromSeconds(30), failedAttempts: 0);
        var shards = await ReadShardsAsync(cluster);
        Assert.Equal(releasing.SiloAddress, Owner(shards[releasedJob.ShardId]));
        var inspector = Services(survivor).GetRequiredService<IDurableJobsStorageInspector>();
        var status = await inspector.InspectAsync(JobsProvider, token);
        Assert.True(status.IsWriteProvider);
        Assert.Equal(1, status.ShardCount);
        Assert.Equal(1, status.OwnedShardCount);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => inspector.InspectAsync(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, token).AsTask());

        // Release and claim: a graceful stop releases the populated shard, and a survivor claims it without adopting it.
        await cluster.StopSiloAsync(releasing, token);
        var claimed = await EventuallyAsync(
            async () => (await ReadShardsAsync(cluster)).GetValueOrDefault(releasedJob.ShardId) is { } properties
                && Owner(properties) is { } owner && !owner.Equals(releasing.SiloAddress) ? properties : null,
            "a surviving silo to claim the released shard");
        Assert.Equal("0", claimed[AdoptedCountProperty]);
        Assert.Equal(bool.TrueString, claimed[ClosedProperty]);
        var releasedRun = Assert.Single(await EventuallyAsync(
            async () => await Grain(survivor, "released").GetRunsAsync() is { Count: > 0 } runs ? runs : null,
            "the released shard's job to run on its claimant"));
        Assert.Equal(("released", 1, true), (releasedRun.JobName, releasedRun.DequeueCount, releasedRun.Succeeded));
        Assert.NotEqual(releasing.SiloAddress.ToParsableString(), releasedRun.Silo);

        // Adoption, retry, cancellation, and recovery: a silo which joined later schedules a failing job and one to cancel.
        var lost = await cluster.StartAdditionalSiloAsync();
        var adopted = Grain(lost, "adopted");
        var retriedJob = await adopted.ScheduleAsync("retried", Now() + TimeSpan.FromSeconds(10), failedAttempts: 2);
        var cancelledJob = await adopted.ScheduleAsync("cancelled", Now() + TimeSpan.FromSeconds(50), failedAttempts: 0);
        shards = await ReadShardsAsync(cluster);
        Assert.Equal(lost.SiloAddress, Owner(shards[retriedJob.ShardId]));
        Assert.Equal(lost.SiloAddress, Owner(shards[cancelledJob.ShardId]));

        // The first attempt fails on the silo which owns the shard; its retry is recorded in the shard before the silo is lost.
        _ = await EventuallyAsync(
            async () => await adopted.GetRunsAsync() is { Count: > 0 } runs ? runs : null,
            "the retried job's first attempt");
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        await cluster.KillSiloAsync(lost, token);

        var adoptedShards = await EventuallyAsync(
            async () =>
            {
                var current = await ReadShardsAsync(cluster);
                return new[] { retriedJob.ShardId, cancelledJob.ShardId }.All(id => current.TryGetValue(id, out var properties)
                    && Owner(properties) is { } owner && !owner.Equals(lost.SiloAddress))
                    ? current
                    : null;
            },
            "surviving silos to adopt the lost silo's shards");
        Assert.All(
            new[] { retriedJob.ShardId, cancelledJob.ShardId }.Distinct(),
            id => Assert.Equal("1", adoptedShards[id][AdoptedCountProperty]));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Silos: {string.Join(", ", cluster.Silos.Select(silo => $"{silo.Name}={silo.SiloAddress}"))}; lost={lost.SiloAddress}; " +
            $"owners: {string.Join(", ", adoptedShards.Select(shard => $"{shard.Key}={Owner(shard.Value)}"))}");

        // Cancellation reaches the adopted shard through the handle the lost silo returned. A request which arrives while the
        // adopter still recovers the shard is not recorded and is retried; the grain's activation, which issues it, recovers
        // its journal once Durable Storage has taken over the lost silo's ledgers.
        _ = await EventuallyAsync(
            async () => await Grain(survivor, "adopted").CancelAsync(cancelledJob) ? (object)true : null,
            "the adopted shard to record the job's cancellation");

        var retriedRuns = await EventuallyAsync(
            async () => await Grain(survivor, "adopted").GetRunsAsync() is { } runs && runs.Any(run => run.Succeeded) ? runs : null,
            "the retried job to succeed on an adopting silo");
        Assert.All(retriedRuns, run => Assert.Equal("retried", run.JobName));
        Assert.Equal(new[] { false, false, true }, retriedRuns.Select(run => run.Succeeded));
        Assert.Equal(lost.SiloAddress.ToParsableString(), retriedRuns[0].Silo);
        Assert.NotEqual(lost.SiloAddress.ToParsableString(), retriedRuns[^1].Silo);
        Assert.True(
            retriedRuns.Zip(retriedRuns.Skip(1)).All(pair => pair.First.DequeueCount < pair.Second.DequeueCount),
            $"Dequeue counts continue from the retry recorded in the shard: {string.Join(", ", retriedRuns.Select(run => run.DequeueCount))}.");

        // Recovery: after the cancelled job's due time, a fresh activation reads every attempt from Durable Storage and none ran.
        var cancelledWait = cancelledJob.DueTime + TimeSpan.FromSeconds(10) - Now();
        if (cancelledWait > TimeSpan.Zero)
        {
            await Task.Delay(cancelledWait, token);
        }

        await Grain(survivor, "adopted").DeactivateAsync();
        await Grain(survivor, "released").DeactivateAsync();
        await EventuallyAsync(
            async () => (await Grain(survivor, "adopted").GetRunsAsync()).SequenceEqual(retriedRuns) ? (object)true : null,
            "the adopted grain to recover its attempts");
        Assert.Equal(new[] { releasedRun }, await Grain(survivor, "released").GetRunsAsync());
    }

    private async Task<TestCluster> DeployAsync(short silos)
    {
        var builder = new TestClusterBuilder(silos);
        builder.Properties[TestKey] = _state.Id;
        _ = builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync(Token).WaitAsync(Patience, Token);
        return _cluster;
    }

    private static IMixedProviderJobGrain Grain(SiloHandle silo, string key) =>
        Services(silo).GetRequiredService<IGrainFactory>().GetGrain<IMixedProviderJobGrain>(key);

    private static IServiceProvider Services(SiloHandle silo) => ((InProcessSiloHandle)silo).SiloHost.Services;

    private static DateTimeOffset Now() => TimeProvider.System.GetUtcNow();

    private static SiloAddress? Owner(IReadOnlyDictionary<string, string> properties) =>
        properties.TryGetValue(OwnerProperty, out var owner) ? SiloAddress.FromParsableString(owner) : null;

    /// <summary>Lists the Jobs shards in the named Azure provider, by shard ID, with their metadata.</summary>
    private static async Task<Dictionary<string, IReadOnlyDictionary<string, string>>> ReadShardsAsync(TestCluster cluster)
    {
        var catalog = Services(cluster.Primary!).GetRequiredKeyedService<IJournalStorageCatalog>(JobsProvider);
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var options = new JournalCatalogListOptions { Prefix = new JournalId(JobShardId.StoragePrefix.Value + "/"), IncludeMetadata = true };
        await foreach (var entry in catalog.ListAsync(options, Token))
        {
            result[JobShardId.FromJournalId(entry.Id).Value] = (entry.Metadata
                ?? throw new InvalidOperationException($"Shard '{entry.Id}' was listed without the metadata the listing requested.")).Properties;
        }

        return result;
    }

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

    private static BlobContainerClient CreateContainerClient(string containerName) =>
        TestDefaultConfiguration.UseAadAuthentication
            ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, containerName), TestDefaultConfiguration.TokenCredential)
            : new BlobContainerClient(TestDefaultConfiguration.DataConnectionString, containerName);

    private sealed class TestState
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public string Roots { get; } = Path.Combine(Path.GetTempPath(), "orleans-mixed-jobs", Guid.NewGuid().ToString("N"));

        public InMemoryMetaStore MetaStore { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        public string? ContainerName { get; set; }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var root = Path.Combine(state.Roots, siloBuilder.Configuration["Orleans:Name"]!);
            _ = Directory.CreateDirectory(root);
            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });

            // Application journals: Durable Storage under the default provider name.
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(state.MetaStore);
            _ = siloBuilder.Services.AddSingleton<IObjectStore>(state.Objects);
            _ = siloBuilder.AddDurableJournalStorage(options => options.Configure(silo =>
            {
                silo.Node = new DurableStorageOptions
                {
                    Cluster = state.Cluster,
                    Root = root,
                    WalFileSize = 1 << 20,
                    PreallocateWalFiles = false,
                    ArchiveStore = ArchiveStoreKind.Lsm,
                    ArchiveStorePrefix = "mixed",
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
            _ = siloBuilder.Services.Configure<JsonJournalOptions>(static options => options.AddTypeInfoResolver(MixedProviderJobsJsonContext.Default));

            // Durable Jobs: a named Azure Blob journal provider, selected as the active Jobs provider.
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
                    options.ShouldRetry = static (context, _) => context.DequeueCount < 10 ? TimeProvider.System.GetUtcNow() + RetryDelay : null;
                });
        }
    }
}

/// <summary>Durable Jobs refuses, at silo start, a journal provider which cannot hold its shards.</summary>
[TestCategory("Azure"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("DurableJobs")]
public sealed class MixedProviderDurableJobsConfigurationTests
{
    /// <summary>
    /// Selecting Durable Storage, which implements neither the journal catalog nor journal metadata, for Durable Jobs stops
    /// the silo before its storage node starts, naming the missing capability and the option which selects a provider.
    /// </summary>
    [Fact]
    public async Task SelectingDurableStorageForJobsFailsAtStartupNamingTheMissingCatalog()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "orleans-mixed-jobs-config", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var objects = new InMemoryObjectStore();
            var builder = Host.CreateApplicationBuilder();
            _ = builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DurableStorage:Node:Cluster"] = Guid.NewGuid().ToString(),
                ["DurableStorage:Node:Root"] = root,
                ["DurableStorage:Node:ArchiveStore"] = nameof(ArchiveStoreKind.Lsm),
                ["DurableStorage:Node:ArchiveStorePrefix"] = "config",
            });
            _ = builder.Services.AddSingleton<IMetaStore>(new InMemoryMetaStore());
            _ = builder.Services.AddSingleton<IObjectStore>(objects);
            _ = builder.UseOrleans(silo => silo
                .UseLocalhostClustering()
                .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage"))
                .UseJournaledDurableJobs());
            using var host = builder.Build();

            var failure = await Assert.ThrowsAsync<OrleansConfigurationException>(() => host.StartAsync(token));

            Assert.Contains($"'{ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME}' does not implement {nameof(IJournalStorageCatalog)}", failure.Message, StringComparison.Ordinal);
            Assert.Contains("catalog listing and journal metadata", failure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(DurableJobsOptions.ActiveProviderName), failure.Message, StringComparison.Ordinal);
            Assert.Empty(await objects.ListAsync("", cancellationToken: token).ToListAsync(token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
