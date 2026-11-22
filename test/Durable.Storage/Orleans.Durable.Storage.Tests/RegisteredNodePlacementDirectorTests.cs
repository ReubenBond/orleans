using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The registered-node placement director (orleans.md §5.3, R1): it places a node's grain on the node's registered silo
/// while membership shows that silo active, a retired node's included, which serves until its gate opens (§5.8), and
/// otherwise refuses with the node named, before the first read and for a registered silo which is not active; a refusal
/// re-reads the registry only when the cache is stale against membership.
/// </summary>
public sealed class RegisteredNodePlacementDirectorTests : IAsyncDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _register = new();
    private readonly CountingTimeProvider _time = new();
    private readonly FakeMembership _membership = new();
    private readonly NodeRegistryResolver _resolver;
    private readonly RegisteredNodePlacementDirector _director;

    public RegisteredNodePlacementDirectorTests()
    {
        _resolver = new(_ => ValueTask.FromResult<Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister>(_register), _membership, _time, NullLogger.Instance);
        _director = new(_resolver, _membership, NullLogger<RegisteredNodePlacementDirector>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _resolver.DisposeAsync();
        _leaks.Dispose();
    }

    [Fact]
    public async Task ItPlacesANodesGrainOnItsRegisteredActiveSilo()
    {
        await RegisterAsync(Entry(1, 1));
        _membership.Set((Silo(1, 1), SiloStatus.Active));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Silo(1, 1), await Place(Node(1)));
    }

    [Fact]
    public async Task ItRefusesBeforeTheFirstRead()
    {
        var refusal = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => Place(Node(1)));

        Assert.Equal(Node(1), refusal.Node);
    }

    /// <summary>
    /// A retired node's grains are placed on its registered silo while membership shows it active, so it serves its
    /// replicas until its retirement gate opens, and nowhere once it is not.
    /// </summary>
    [Fact]
    public async Task ItPlacesARetiredNodeOnlyOnItsActiveSilo()
    {
        await RegisterAsync(Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 0));
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active));
        Assert.True((await _resolver.GetAsync(TestContext.Current.CancellationToken)).Find(Node(1))!.IsRetired);

        Assert.Equal(Silo(1, 1), await Place(Node(1)));
        Assert.Equal(Silo(2, 1), await Place(Node(2)));

        _membership.Set((Silo(1, 1), SiloStatus.Dead), (Silo(2, 1), SiloStatus.Active));
        var refusal = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => Place(Node(1)));
        Assert.Equal(Node(1), refusal.Node);
    }

    [Theory]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Dead)]
    public async Task ItRefusesARegisteredSiloWhichIsNotActive(SiloStatus status)
    {
        await RegisterAsync(Entry(1, 1));
        _membership.Set((Silo(1, 1), status));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);

        var refusal = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => Place(Node(1)));

        Assert.Equal(Node(1), refusal.Node);
    }

    /// <summary>
    /// A refusal while every active silo has an entry leaves the registry unread; one while an active silo has none reads it,
    /// once, and the read finds the registration which places the grain.
    /// </summary>
    [Fact]
    public async Task ARefusalRereadsOnlyWhenTheCacheIsStale()
    {
        await RegisterAsync(Entry(1, 1));
        _membership.Set((Silo(1, 1), SiloStatus.Active));
        _ = await _resolver.GetAsync(TestContext.Current.CancellationToken);
        var reads = _register.Reads;

        _ = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => Place(Node(2)));
        await PassSecondsAsync(3);
        Assert.Equal(reads, _register.Reads);

        await RegisterAsync(Entry(2, 1));
        reads = _register.Reads;
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active));
        _ = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => Place(Node(2)));
        await PassSecondsAsync(1);
        Assert.Equal(Silo(2, 1), await Place(Node(2)));
        await PassSecondsAsync(3);
        Assert.Equal(reads + 1, _register.Reads);
    }

    private Task<SiloAddress> Place(NodeId node) =>
        _director.OnAddActivation(
            RegisteredNodePlacement.Instance,
            new PlacementTarget(GrainId.Create(GrainType.Create("probe"), GrainIdKeyExtensions.CreateGuidKey(node.Value)), [], default, 0),
            null!);

    /// <summary>Advances the clock a second at a time, letting the resolver's re-reads run in between.</summary>
    private async Task PassSecondsAsync(int seconds)
    {
        for (var second = 0; second < seconds * 10; second++)
        {
            for (var turn = 0; turn < 5; turn++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
            }

            _time.Advance(NodeRegistryResolver.RereadInterval / 10);
        }
    }

    private Task RegisterAsync(params NodeRegistryEntry[] entries) => RegisterInOrderAsync(_register, entries);
}
