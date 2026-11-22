using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

/// <summary>
/// The memory register meets the register contract. It decides a write synchronously, before its caller can cancel the
/// wait, so it runs no case of a write cancelled mid-call.
/// </summary>
public sealed class InMemoryConditionalRegisterTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public Task EmptyStoreReturnsNull() =>
        ConditionalRegisterContract.EmptyStoreReturnsNull(CreateRegister());

    [Fact]
    public Task NullVersionCreatesOnlyWhenAbsent() =>
        ConditionalRegisterContract.NullVersionCreatesOnlyWhenAbsent(CreateRegister());

    [Fact]
    public Task EmptyValueReadsAsPresent() =>
        ConditionalRegisterContract.EmptyValueReadsAsPresent(CreateRegister());

    [Fact]
    public Task NonNullVersionDoesNotCreateMissingRegister() =>
        ConditionalRegisterContract.NonNullVersionDoesNotCreateMissingRegister(CreateRegister());

    [Fact]
    public Task MatchingVersionUpdatesAndChangesVersion() =>
        ConditionalRegisterContract.MatchingVersionUpdatesAndChangesVersion(CreateRegister());

    [Fact]
    public Task AnAppliedWriteReturnsTheRegistersNewVersion() =>
        ConditionalRegisterContract.AnAppliedWriteReturnsTheRegistersNewVersion(CreateRegister());

    [Fact]
    public Task StaleVersionDoesNotOverwriteValue() =>
        ConditionalRegisterContract.StaleVersionDoesNotOverwriteValue(CreateRegister());

    [Fact]
    public Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues() =>
        ConditionalRegisterContract.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(CreateRegister());

    [Fact]
    public Task ConcurrentCreatesHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentCreatesHaveOneWinner(CreateRegister());

    [Fact]
    public Task ConcurrentWritesWithOneVersionHaveOneWinner() =>
        ConditionalRegisterContract.ConcurrentWritesWithOneVersionHaveOneWinner(CreateRegister());

    [Fact]
    public Task CancellationIsObserved() =>
        ConditionalRegisterContract.CancellationIsObserved(CreateRegister());

    [Fact]
    public async Task VersionIsTheDecimalCountOfAppliedWrites()
    {
        var register = CreateRegister();

        var created = (await register.WriteAsync(null, [1], TestContext.Current.CancellationToken)).AppliedVersion();
        Assert.False((await register.WriteAsync(new MetaStoreVersion("2"), [2], TestContext.Current.CancellationToken)).IsApplied());
        var replaced = (await register.WriteAsync(created, [3], TestContext.Current.CancellationToken)).AppliedVersion();

        Assert.Equal("1", created.Value);
        Assert.Equal("2", replaced.Value);
    }

    public void Dispose() => _leaks.Dispose();

    private static InMemoryConditionalRegister CreateRegister() => new();
}

public sealed class InMemoryMetaStoreTests : IDisposable
{
    private static readonly Guid Cluster = new("0123456789abcdef0123456789abcdef");
    private static readonly Guid OtherCluster = new("fedcba9876543210fedcba9876543210");
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public async Task EqualArgumentsAddressTheSameRegister()
    {
        var store = new InMemoryMetaStore();
        var first = await store.GetRegisterAsync(Cluster, "l/orders", TestContext.Current.CancellationToken);
        Assert.True((await first.WriteAsync(null, [1], TestContext.Current.CancellationToken)).IsApplied());

        var second = await store.GetRegisterAsync(Cluster, "l/orders", TestContext.Current.CancellationToken);
        using var entry = Assert.IsType<MetaStoreEntry>(await second.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1 }, entry.Value.ToArray());
    }

    [Fact]
    public async Task RegistersOfDifferentClustersAndNamesAreIndependent()
    {
        var store = new InMemoryMetaStore();
        (Guid Cluster, string Name)[] keys =
        [
            (Cluster, "l/orders"),
            (OtherCluster, "l/orders"),
            (Cluster, "l/orders2"),
            (Cluster, "bucket-map"),
            (Cluster, "node-registry"),
        ];
        for (var index = 0; index < keys.Length; index++)
        {
            var register = await store.GetRegisterAsync(keys[index].Cluster, keys[index].Name, TestContext.Current.CancellationToken);
            Assert.True((await register.WriteAsync(null, [(byte)index], TestContext.Current.CancellationToken)).IsApplied());
        }

        for (var index = 0; index < keys.Length; index++)
        {
            var register = await store.GetRegisterAsync(keys[index].Cluster, keys[index].Name, TestContext.Current.CancellationToken);
            using var entry = Assert.IsType<MetaStoreEntry>(await register.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { (byte)index }, entry.Value.ToArray());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("L/orders")]
    [InlineData("l//orders")]
    [InlineData("l:orders")]
    public async Task InvalidNamesThrow(string name)
    {
        var store = new InMemoryMetaStore();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => _ = await store.GetRegisterAsync(Cluster, name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyClusterThrows()
    {
        var store = new InMemoryMetaStore();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => _ = await store.GetRegisterAsync(Guid.Empty, "l/orders", TestContext.Current.CancellationToken));
    }

    public void Dispose() => _leaks.Dispose();
}
