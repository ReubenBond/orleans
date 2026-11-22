using System.Text.Json;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

public sealed class CasPaxosMetaStoreFactoryTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private static readonly Guid Cluster = new("01234567-89ab-cdef-0123-456789abcdef");

    [Theory]
    [InlineData("ordinary")]
    [InlineData("components")]
    [InlineData("punctuation")]
    [InlineData("cluster-wide")]
    [InlineData("long")]
    public async Task CompleteKeyPreservesClusterAndOrdinalName(string caseId)
    {
        var names = caseId switch
        {
            "ordinary" => new[] { "l/orders", "l/orders2" },
            "components" => ["a/l/b", "a/lb"],
            "punctuation" => ["l/a.b", "l/a_b"],
            "cluster-wide" => ["bucket-map", "node-registry"],
            "long" => ["l/" + new string('a', 128), "l/" + new string('a', 127) + "b"],
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };
        using var service = new RegisterServiceStub
        {
            OnRead = static (_, _) => ValueTask.FromResult(new RegisterValue(null, null)),
        };
        using var factory = new CasPaxosMetaStoreFactory(service);
        foreach (var name in names)
        {
            var store = await factory.GetRegisterAsync(Cluster, name, TestContext.Current.CancellationToken);
            Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            names.Select(static name => "0123456789abcdef0123456789abcdef/" + name),
            service.Reads.Select(static call => call.Key));
        Assert.NotEqual(service.Reads[0].Key, service.Reads[1].Key);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task CompleteKeysMatchTheSharedRegisterKeyVectors()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            FindStorageRoot(),
            "shared-vectors",
            "metastore",
            "register-keys.json"), TestContext.Current.CancellationToken));
        var registers = document.RootElement.GetProperty("registers").EnumerateArray().ToArray();
        using var service = new RegisterServiceStub
        {
            OnRead = static (_, _) => ValueTask.FromResult(new RegisterValue(null, null)),
        };
        using var factory = new CasPaxosMetaStoreFactory(service);

        foreach (var register in registers)
        {
            var handle = await factory.GetRegisterAsync(
                Guid.ParseExact(register.GetProperty("cluster").GetString()!, "N"),
                register.GetProperty("name").GetString()!,
                TestContext.Current.CancellationToken);
            _ = await handle.ReadAsync(TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(registers);
        Assert.Equal(
            registers.Select(static register => register.GetProperty("backendKeys").GetProperty("caspaxos").GetString()),
            service.Reads.Select(static call => call.Key));
    }

    [Fact]
    public async Task CompleteKeyIsStableAcrossFactoriesAndDistinctAcrossClusters()
    {
        using var service = new RegisterServiceStub
        {
            OnRead = static (_, _) => ValueTask.FromResult(new RegisterValue(null, null)),
        };
        using var first = new CasPaxosMetaStoreFactory(service);
        using var second = new CasPaxosMetaStoreFactory(service);
        var otherCluster = new Guid("01234567-89ab-cdef-0123-456789abcdee");

        foreach (var store in new[]
        {
            await first.GetRegisterAsync(Cluster, "l/orders", TestContext.Current.CancellationToken),
            await second.GetRegisterAsync(Cluster, "l/orders", TestContext.Current.CancellationToken),
            await first.GetRegisterAsync(otherCluster, "l/orders", TestContext.Current.CancellationToken),
        })
        {
            _ = await store.ReadAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [
                "0123456789abcdef0123456789abcdef/l/orders",
                "0123456789abcdef0123456789abcdef/l/orders",
                "0123456789abcdef0123456789abcdee/l/orders",
            ],
            service.Reads.Select(static call => call.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Orders")]
    [InlineData("\u00e9")]
    [InlineData("a\0b")]
    [InlineData("a%2Fb")]
    [InlineData("a:b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    public async Task InvalidRegisterNamesThrowWithoutContactingTheService(string name)
    {
        using var service = new RegisterServiceStub();
        using var factory = new CasPaxosMetaStoreFactory(service);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => _ = await factory.GetRegisterAsync(Cluster, name, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => _ = await factory.GetRegisterAsync(Cluster, "l/" + new string('a', 129), TestContext.Current.CancellationToken));

        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task EmptyClusterThrowsWithoutContactingTheService()
    {
        using var service = new RegisterServiceStub();
        using var factory = new CasPaxosMetaStoreFactory(service);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => _ = await factory.GetRegisterAsync(Guid.Empty, "l/orders", TestContext.Current.CancellationToken));

        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task Get_PreCancelledDoesNotContactService()
    {
        using var service = new RegisterServiceStub();
        using var factory = new CasPaxosMetaStoreFactory(service);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => factory.GetRegisterAsync(Cluster, "orders", cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeCallerOwnedServiceOrExistingHandles()
    {
        using var service = new RegisterServiceStub
        {
            OnRead = static (_, _) => ValueTask.FromResult(new RegisterValue(null, null)),
        };
        using var factory = new CasPaxosMetaStoreFactory(service);
        var store = await factory.GetRegisterAsync(Cluster, "orders", TestContext.Current.CancellationToken);

        factory.Dispose();
        factory.Dispose();

        Assert.Equal(0, service.DisposeCount);
        Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => factory.GetRegisterAsync(Cluster, "other", TestContext.Current.CancellationToken).AsTask());
        Assert.Single(service.Reads);
    }

    public void Dispose() => _leaks.Dispose();

    private static string FindStorageRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return Path.Combine(directory.FullName, "test", "Durable.Storage");
            }
        }

        throw new DirectoryNotFoundException("Could not find the Orleans repository root.");
    }
}
