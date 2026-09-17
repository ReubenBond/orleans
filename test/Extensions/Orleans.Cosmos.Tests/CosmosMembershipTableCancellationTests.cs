using System.Net;
using System.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.Clustering.Cosmos;
using Orleans.Clustering.Cosmos.Models;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Tester.Cosmos.Clustering;

[TestCategory("Membership"), TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Membership")]
public class CosmosMembershipTableCancellationTests
{
    [Theory]
    [InlineData("Initialize")]
    [InlineData("Delete")]
    [InlineData("Cleanup")]
    [InlineData("ReadRow")]
    [InlineData("ReadAll")]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    public async Task CanceledOperationsDoNotAccessStorage(string operation)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var table = CreateTable(services, new CosmosClusteringOptions());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
        var entry = new MembershipEntry { SiloAddress = silo };
        var version = new TableVersion(1, "etag");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "Initialize" => table.InitializeMembershipTableAsync(true, token),
            "Delete" => table.DeleteMembershipTableEntriesAsync("cluster", token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            "ReadRow" => table.ReadRowAsync(silo, token),
            "ReadAll" => table.ReadAllAsync(token),
            "Insert" => table.InsertRowAsync(entry, version, token),
            "Update" => table.UpdateRowAsync(entry, "etag", version, token),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(token, exception.CancellationToken);
    }

    [Fact]
    public async Task CanceledInitializationRetainsSharedClientForRetry()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var completion = new TaskCompletionSource<CosmosClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TrackingCosmosClient();
        var calls = 0;
        var options = new CosmosClusteringOptions { DatabaseName = "database", ContainerName = "container" };
        options.ConfigureCosmosClient(_ =>
        {
            calls++;
            return new ValueTask<CosmosClient>(completion.Task);
        });
        var table = CreateTable(services, options);

        var initialization = table.InitializeMembershipTableAsync(false, cancellation.Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(initialization.IsCanceled);

        completion.SetResult(client);
        Assert.Equal(0, client.AccountCalls);
        Assert.Equal(0, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);

        var failure = new InvalidOperationException("retry reached the retained client");
        client.ContainerFailure = failure;
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken));

        Assert.Same(failure, retryException);
        Assert.Equal(1, calls);
        Assert.Equal(1, client.AccountCalls);
        Assert.Equal(1, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task NativeDatabaseCancellationIsNotWrapped()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var database = Substitute.For<Database>();
        using var client = new TrackingCosmosClient { Database = database };
        _ = database.DeleteAsync(Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
            cancellation.Cancel();
            return Task.FromCanceled<DatabaseResponse>(cancellation.Token);
        });
        var options = new CosmosClusteringOptions
        {
            DatabaseName = "database",
            ContainerName = "container",
            IsResourceCreationEnabled = true,
            CleanResourcesOnInitialization = true
        };
        options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(client));
        var table = CreateTable(services, options);

        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(initialization.IsCanceled);
        Assert.Equal(1, client.AccountCalls);
        Assert.Equal(1, client.DatabaseCalls);
        Assert.Equal(0, client.ContainerCalls);
    }

    [Theory]
    [InlineData(ConsistencyLevel.Session)]
    [InlineData(ConsistencyLevel.BoundedStaleness)]
    [InlineData(ConsistencyLevel.Strong)]
    public async Task SupportedAccountConsistencyReachesMembershipResources(ConsistencyLevel consistency)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var failure = new InvalidOperationException("validated account reached the membership container");
        using var client = new TrackingCosmosClient
        {
            Account = CreateAccount(consistency),
            ContainerFailure = failure
        };
        var options = CreateOptions(client);
        var table = CreateTable(services, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(1, client.AccountCalls);
        Assert.Equal(1, client.ContainerCalls);
        Assert.Equal(0, client.DatabaseCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Theory]
    [InlineData(ConsistencyLevel.Eventual)]
    [InlineData(ConsistencyLevel.ConsistentPrefix)]
    public async Task WeakAccountConsistencyFailsBeforeResourceAccess(ConsistencyLevel consistency)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var client = new TrackingCosmosClient { Account = CreateAccount(consistency) };
        var options = CreateOptions(client, createResources: true);
        var table = CreateTable(services, options);

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Azure Cosmos DB membership requires account consistency of Session, BoundedStaleness, or Strong. Account 'test-account' uses {consistency}.",
            exception.Message);
        Assert.Equal(1, client.AccountCalls);
        Assert.Equal(0, client.DatabaseCalls);
        Assert.Equal(0, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MultipleWritableRegionsFailBeforeResourceAccess(int writableRegions)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var client = new TrackingCosmosClient { Account = CreateAccount(writableRegions: writableRegions) };
        var table = CreateTable(services, CreateOptions(client, createResources: true));

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken));

        Assert.Equal(
            "Azure Cosmos DB membership requires a single writable region. Account 'test-account' has multiple writable regions.",
            exception.Message);
        Assert.Equal(1, client.AccountCalls);
        Assert.Equal(0, client.DatabaseCalls);
        Assert.Equal(0, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task AccountMetadataFailurePropagatesBeforeResourceAccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var failure = new CosmosException("Account metadata permission denied.", HttpStatusCode.Forbidden, 0, "activity", 0);
        using var client = new TrackingCosmosClient { AccountTask = Task.FromException<AccountProperties>(failure) };
        var table = CreateTable(services, CreateOptions(client, createResources: true));

        var exception = await Assert.ThrowsAsync<CosmosException>(
            () => table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(1, client.AccountCalls);
        Assert.Equal(0, client.DatabaseCalls);
        Assert.Equal(0, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task CancellationDuringAccountMetadataReadPreventsResourceAccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var completion = new TaskCompletionSource<AccountProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TrackingCosmosClient { AccountTask = completion.Task };
        var table = CreateTable(services, CreateOptions(client, createResources: true));

        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.Equal(1, client.AccountCalls);
        Assert.False(initialization.IsCompleted);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(initialization.IsCanceled);
        completion.SetResult(CreateAccount());

        Assert.Equal(0, client.DatabaseCalls);
        Assert.Equal(0, client.ContainerCalls);
        Assert.Equal(0, client.DisposeCalls);
    }

    private static CosmosClusteringOptions CreateOptions(TrackingCosmosClient client, bool createResources = false)
    {
        var options = new CosmosClusteringOptions
        {
            DatabaseName = "database",
            ContainerName = "container",
            IsResourceCreationEnabled = createResources,
            CleanResourcesOnInitialization = createResources
        };
        options.ConfigureCosmosClient(_ => new ValueTask<CosmosClient>(client));
        return options;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion(bool deletionRacesReplace)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var container = Substitute.For<Container>();
        var table = CreateHeartbeatTable(services, container);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            IAmAliveTime = DateTime.UnixEpoch.AddHours(1)
        };
        var version = new ClusterVersionEntity { Id = "ClusterVersion", ClusterId = "cluster", ClusterVersion = 7, ETag = "v7" };
        var versionResponse = new ResourceResponse<ClusterVersionEntity>(version);
        _ = container.ReadItemAsync<ClusterVersionEntity>(
            "ClusterVersion", new PartitionKey("cluster"), null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromResult<ItemResponse<ClusterVersionEntity>>(versionResponse));
        var missing = new CosmosException("retired silo", HttpStatusCode.NotFound, 0, "activity", 0);
        if (deletionRacesReplace)
        {
            var siloResponse = new ResourceResponse<SiloEntity>(new SiloEntity { Id = "127.0.0.1-11111-1", ClusterId = "cluster", IAmAliveTime = DateTime.UnixEpoch, ETag = "s1" });
            _ = container.ReadItemAsync<SiloEntity>(
                string.Empty, default, null, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ItemResponse<SiloEntity>>(siloResponse));
            _ = container.ReplaceItemAsync(
                new SiloEntity(), string.Empty, null, null, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(missing));
        }
        else
        {
            _ = container.ReadItemAsync<SiloEntity>(
                string.Empty, default, null, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(missing));
        }

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(7, version.ClusterVersion);
        Assert.Equal("v7", version.ETag);
        Assert.Equal(deletionRacesReplace ? 3 : 2, container.ReceivedCalls().Count());
        Assert.DoesNotContain(container.ReceivedCalls(), call => call.GetMethodInfo().Name is "CreateItemAsync" or "UpsertItemAsync" or "CreateTransactionalBatch");
        var versionRead = Assert.Single(container.ReceivedCalls(), call =>
            call.GetMethodInfo().GetGenericArguments().Contains(typeof(ClusterVersionEntity)));
        Assert.Equal("ClusterVersion", versionRead.GetArguments()[0]);
        Assert.Equal(new PartitionKey("cluster"), versionRead.GetArguments()[1]);
        Assert.Equal(TestContext.Current.CancellationToken, versionRead.GetArguments()[3]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HeartbeatInfrastructureFailuresRemainVisible(HttpStatusCode status)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var container = Substitute.For<Container>();
        var table = CreateHeartbeatTable(services, container);
        var failure = new CosmosException("infrastructure-failure", status, 0, "activity", 0);
        _ = container.ReadItemAsync<SiloEntity>(
            string.Empty, default, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<SiloEntity>>(failure));
        _ = container.ReadItemAsync<ClusterVersionEntity>(
            string.Empty, default, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromException<ItemResponse<ClusterVersionEntity>>(failure));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => table.UpdateIAmAliveAsync(new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            IAmAliveTime = DateTime.UnixEpoch
        }, TestContext.Current.CancellationToken));

        Assert.Contains("infrastructure-failure", exception.ToString());
        Assert.Equal(status == HttpStatusCode.NotFound ? 2 : 1, container.ReceivedCalls().Count());
        Assert.All(container.ReceivedCalls(), call => Assert.Equal("ReadItemAsync", call.GetMethodInfo().Name));
    }

    private static CosmosMembershipTable CreateHeartbeatTable(IServiceProvider services, Container container)
    {
        container.ReturnsForAll<Task<ItemResponse<SiloEntity>>>(Task.FromException<ItemResponse<SiloEntity>>(new InvalidOperationException("Unexpected silo operation.")));
        container.ReturnsForAll<Task<ItemResponse<ClusterVersionEntity>>>(Task.FromException<ItemResponse<ClusterVersionEntity>>(new InvalidOperationException("Unexpected version operation.")));
        var table = CreateTable(services, new CosmosClusteringOptions());
        typeof(CosmosMembershipTable).GetField("_container", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, container);
        return table;
    }

    private sealed class ResourceResponse<T>(T resource) : ItemResponse<T>
    {
        public override T Resource => resource;
    }

    private static AccountProperties CreateAccount(ConsistencyLevel consistency = ConsistencyLevel.Session, int writableRegions = 1)
    {
        var regions = Enumerable.Range(0, writableRegions).Select(index => new
        {
            name = $"region-{index}",
            databaseAccountEndpoint = $"https://region-{index}.example.test/"
        }).ToArray();
        var json = JsonConvert.SerializeObject(new
        {
            id = "test-account",
            writableLocations = regions,
            readableLocations = regions,
            userConsistencyPolicy = new { defaultConsistencyLevel = consistency.ToString() }
        });
        var account = Assert.IsType<AccountProperties>(JsonConvert.DeserializeObject<AccountProperties>(
            json, new JsonSerializerSettings { ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor }));
        Assert.Equal(consistency, account.Consistency.DefaultConsistencyLevel);
        Assert.Equal(writableRegions, account.WritableRegions.Count());
        return account;
    }

    private static CosmosMembershipTable CreateTable(IServiceProvider services, CosmosClusteringOptions options)
        => new(
            NullLoggerFactory.Instance,
            services,
            Options.Create(options),
            Options.Create(new ClusterOptions { ClusterId = "cluster" }));

    private sealed class TrackingCosmosClient : CosmosClient
    {
        public AccountProperties Account { get; init; } = CreateAccount();
        public Task<AccountProperties>? AccountTask { get; init; }
        public Database? Database { get; init; }
        public InvalidOperationException? ContainerFailure { get; set; }
        public int AccountCalls { get; private set; }
        public int DatabaseCalls { get; private set; }
        public int ContainerCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public override Task<AccountProperties> ReadAccountAsync()
        {
            AccountCalls++;
            return AccountTask ?? Task.FromResult(Account);
        }

        public override Container GetContainer(string databaseId, string containerId)
        {
            Assert.Equal("database", databaseId);
            Assert.Equal("container", containerId);
            ContainerCalls++;
            throw ContainerFailure ?? new InvalidOperationException("Unexpected container access.");
        }

        public override Database GetDatabase(string id)
        {
            Assert.Equal("database", id);
            DatabaseCalls++;
            return Database ?? throw new InvalidOperationException("Unexpected database access.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
            }
        }
    }
}
