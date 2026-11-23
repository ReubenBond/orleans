using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The journal placement director (orleans.md §5.4): it maps a grain to its journal as Orleans does, finds the
/// journal's open ledger in the placing silo's map view, and places the grain on the silo the registry names for the
/// ledger's primary while that entry is active, membership shows the silo active, and the silo hosts the grain's type,
/// over any placement hint; otherwise on the hinted silo when it hosts the grain's type, and else on a random compatible
/// silo, before the silo's runtime publishes its routing among them.
/// </summary>
public sealed class JournalPlacementDirectorTests : IAsyncLifetime
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _registry = new();
    private readonly ScriptedConditionalRegister _map = new();
    private readonly FakeMembership _membership = new();
    private readonly LocalRouting _routing = new();
    private readonly InMemoryArchiveStore _archive = new();
    private readonly NodeRegistryResolver _resolver;
    private readonly JournalPlacementDirector _director;
    private string _lowerKey = "";
    private string _upperKey = "";

    public JournalPlacementDirectorTests()
    {
        _resolver = new(_ => ValueTask.FromResult<IConditionalRegister>(_registry), _membership, TimeProvider.System, NullLogger.Instance);
        _director = new(_routing, _resolver, _membership);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SiloAddress[] Both => [Silo(1, 1), Silo(2, 1)];

    public async ValueTask InitializeAsync()
    {
        // Node 1 is the primary of the lower half of the key space, and node 2 of the upper.
        var view = await LedgerMapView.OpenAsync(_map, Token);
        await using (var committer = new LedgerMapCommitter(_map, 1 << 20, view))
        {
            var (lower, upper) = KeyRange.Root.Children;
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await committer.CommitAsync(new LedgerMapChange.Install([], [Opening(KeyRange.Root, Node(1))], 1), Token)).Value);
            var root = Assert.Single(view.Current.Ledgers);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await committer.CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lower, Node(1)), Opening(upper, Node(2))], 0), Token)).Value);
            _lowerKey = KeyIn(lower);
            _upperKey = KeyIn(upper);
        }

        await RegisterInOrderAsync(_registry, Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 1));
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active));
        _ = await _resolver.GetAsync(Token);
        _routing.Publish(Node(1), view, _archive);
    }

    public async ValueTask DisposeAsync()
    {
        await _resolver.DisposeAsync();
        _archive.Dispose();
        _leaks.Dispose();
    }

    /// <summary>A grain is placed on its journal's primary's silo, by the key Orleans names its journal by.</summary>
    [Fact]
    public async Task AGrainIsPlacedOnItsJournalsPrimary()
    {
        Assert.Equal(Silo(1, 1), await PlaceAsync(_lowerKey, Both));
        Assert.Equal(Silo(2, 1), await PlaceAsync(_upperKey, Both));
    }

    /// <summary>The director names no primary's silo before the silo's runtime publishes its routing.</summary>
    [Fact]
    public async Task AGrainPlacedBeforeTheRoutingIsPublishedGoesToACompatibleSilo()
    {
        var director = new JournalPlacementDirector(new LocalRouting(), _resolver, _membership);

        Assert.Null(director.PrimarySilo(Grain(_lowerKey), Both));
        Assert.Equal(Silo(2, 1), await PlaceAsync(director, _lowerKey, [Silo(2, 1)]));
    }

    /// <summary>The director names no retired primary's silo.</summary>
    [Fact]
    public async Task AGrainWhosePrimaryIsRetiredGoesToACompatibleSilo()
    {
        await using (var committer = Committer(_registry))
        {
            _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await committer.RegisterAsync(Entry(3, 1, ordinal: 0), StoreA, Node(1), Token)).Value);
        }

        _resolver.RequestReread();
        await WaitUntilAsync(() => _resolver.Current?.Find(Node(1))?.IsRetired is true);

        Assert.Null(_director.PrimarySilo(Grain(_lowerKey), Both));
        Assert.Equal(Silo(2, 1), await PlaceAsync(_lowerKey, [Silo(2, 1)]));
    }

    /// <summary>The director names no primary's silo which membership shows other than active.</summary>
    [Theory]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Dead)]
    public async Task AGrainWhosePrimarysSiloIsNotActiveGoesToACompatibleSilo(SiloStatus status)
    {
        _membership.Set((Silo(1, 1), status), (Silo(2, 1), SiloStatus.Active));

        Assert.Null(_director.PrimarySilo(Grain(_lowerKey), Both));
        Assert.Equal(Silo(2, 1), await PlaceAsync(_lowerKey, [Silo(2, 1)]));
    }

    /// <summary>The director names no primary's silo which does not host the grain's type.</summary>
    [Fact]
    public async Task AGrainWhosePrimarysSiloDoesNotHostItsTypeGoesToACompatibleSilo()
    {
        Assert.Null(_director.PrimarySilo(Grain(_lowerKey), [Silo(2, 1)]));
        Assert.Equal(Silo(2, 1), await PlaceAsync(_lowerKey, [Silo(2, 1)]));
    }

    /// <summary>Without a primary's silo or a hint, the grain goes to a compatible silo at random, each of them in turn.</summary>
    [Fact]
    public async Task AGrainWithoutAPrimarysSiloOrAHintGoesToARandomCompatibleSilo()
    {
        _membership.Set((Silo(1, 1), SiloStatus.Dead), (Silo(2, 1), SiloStatus.Dead), (Silo(3, 1), SiloStatus.Active), (Silo(4, 1), SiloStatus.Active));
        SiloAddress[] compatible = [Silo(3, 1), Silo(4, 1)];

        var placed = new HashSet<SiloAddress>();
        for (var placement = 0; placement < 200 && placed.Count < compatible.Length; placement++)
        {
            _ = placed.Add(await PlaceAsync(_lowerKey, compatible));
        }

        Assert.True(placed.SetEquals(compatible), $"The placements reached {string.Join(", ", placed)}.");
    }

    /// <summary>
    /// Orleans' placement hint, which a live migration's target carries, places a grain without a primary's silo on the
    /// hinted silo when it hosts the grain's type; the primary's silo overrides the hint.
    /// </summary>
    [Fact]
    public async Task AHintPlacesAGrainOnlyWhereNoPrimarysSiloDoes()
    {
        Assert.Equal(Silo(1, 1), await PlaceAsync(_director, _lowerKey, Both, hint: Silo(2, 1)));

        _membership.Set((Silo(1, 1), SiloStatus.Dead), (Silo(2, 1), SiloStatus.Active));
        for (var placement = 0; placement < 20; placement++)
        {
            Assert.Equal(Silo(2, 1), await PlaceAsync(_director, _lowerKey, [Silo(3, 1), Silo(2, 1)], hint: Silo(2, 1)));
        }
    }

    private static GrainId Grain(string key) => GrainId.Create(GrainType.Create("journaled"), IdSpan.Create(key));

    private Task<SiloAddress> PlaceAsync(string key, SiloAddress[] compatible) => PlaceAsync(_director, key, compatible);

    private static Task<SiloAddress> PlaceAsync(JournalPlacementDirector director, string key, SiloAddress[] compatible, SiloAddress? hint = null) =>
        director.OnAddActivation(
            JournalPlacement.Instance,
            new PlacementTarget(Grain(key), hint is null ? [] : new Dictionary<string, object> { [IPlacementDirector.PlacementHintKey] = hint }, default, 0),
            new Compatible(compatible));

    /// <summary>Returns a grain key whose journal, as Orleans names it by default, routes into <paramref name="range"/>.</summary>
    private static string KeyIn(KeyRange range) =>
        Enumerable.Range(0, int.MaxValue)
            .Select(static index => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"grain-{index}"))
            .First(key => range.Contains(JournalRowKey.Encode(JournalId.FromGrainId(Grain(key)).Value).Span));

    private static LedgerOpening Opening(KeyRange range, NodeId primary) =>
        new(range, [primary, .. new[] { Node(1), Node(2), Node(3) }.Where(node => node != primary)], 4, new PrimaryClaim(primary, SequencerInstanceId.New()));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(deadline) < TimeSpan.FromSeconds(30), "The condition held within 30 seconds.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Token);
        }
    }

    /// <summary>A placement context whose compatible silos are fixed.</summary>
    private sealed class Compatible(SiloAddress[] silos) : IPlacementContext
    {
        public SiloAddress LocalSilo => silos[0];

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public SiloAddress[] GetCompatibleSilos(PlacementTarget target) => silos;

        public IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target) =>
            new Dictionary<ushort, SiloAddress[]> { [0] = silos };
    }
}
