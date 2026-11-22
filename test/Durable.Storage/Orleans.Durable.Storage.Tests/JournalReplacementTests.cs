using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Re-placement of journaled grains (orleans.md §5.4): the silo tracks an activation whose journal storage is created for
/// its own journal and whose type is placed by journal, until it is deactivated, and each pass asks every tracked
/// activation which the journal placement director, on this silo's views, would place on another silo to migrate, at most
/// <see cref="JournalReplacement.MaxMigrationsPerPass"/> a pass.
/// </summary>
public sealed class JournalReplacementTests : IAsyncLifetime
{
    private static readonly GrainType Placed = GrainType.Create("placed");
    private static readonly GrainType Unplaced = GrainType.Create("unplaced");

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _registry = new();
    private readonly ScriptedConditionalRegister _map = new();
    private readonly FakeMembership _membership = new();
    private readonly LocalRouting _routing = new();
    private readonly InMemoryArchiveStore _archive = new();
    private readonly FakeContexts _contexts = new();
    private readonly FakeManifests _manifests = new();
    private readonly NodeRegistryResolver _resolver;
    private readonly JournalPlacementDirector _director;
    private readonly JournalReplacement _replacement;
    private LedgerMapView _view = null!;
    private LedgerMapCommitter _committer = null!;

    public JournalReplacementTests()
    {
        _resolver = new(_ => ValueTask.FromResult<IConditionalRegister>(_registry), _membership, TimeProvider.System, NullLogger.Instance);
        _director = new(_routing, _resolver, _membership);
        _replacement = Replacement(Silo(1, 1));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // Node 1, this silo's, is the root's primary; node 2's silo is active, node 3's is joining, and node 4 has no entry.
        // Every silo hosts the placed type but node 2's, which hosts it only once a test says so.
        _view = await LedgerMapView.OpenAsync(_map, Token);
        _committer = new LedgerMapCommitter(_map, 1 << 20, _view);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await _committer.CommitAsync(new LedgerMapChange.Install([], [Opening(KeyRange.Root, Node(1))], 1), Token)).Value);
        await RegisterInOrderAsync(_registry, Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1, ordinal: 2));
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active), (Silo(3, 1), SiloStatus.Joining));
        _manifests.Host((Silo(1, 1), [Placed, Unplaced]), (Silo(2, 1), [Unplaced]), (Silo(3, 1), [Placed, Unplaced]));
        _ = await _resolver.GetAsync(Token);
        _routing.Publish(Node(1), _view, _archive);
    }

    public async ValueTask DisposeAsync()
    {
        _replacement.Dispose();
        await _committer.DisposeAsync();
        await _resolver.DisposeAsync();
        _archive.Dispose();
        _leaks.Dispose();
    }

    /// <summary>
    /// Only an activation whose storage is for its own journal and whose type is placed by journal is tracked, not a
    /// storage created outside an activation, where the accessor names the silo's hosted client; and it is tracked until
    /// it is deactivated.
    /// </summary>
    [Fact]
    public void AnActivationIsTrackedForItsOwnJournalWhenPlacedByJournalUntilItIsDeactivated()
    {
        var placed = new FakeGrainContext(Grain(Placed, "a"));
        Track(placed);
        Track(new FakeGrainContext(Grain(Unplaced, "b")));
        _contexts.Current = new FakeGrainContext(Grain(Placed, "c"));
        _replacement.Track(JournalId.FromGrainId(Grain(Placed, "d")));
        _contexts.Current = FakeContexts.HostedClient;
        _replacement.Track(JournalId.FromGrainId(Grain(Placed, "e")));
        Assert.Equal(1, _replacement.TrackedCount);

        placed.Deactivate();
        Assert.Equal(0, _replacement.TrackedCount);
    }

    /// <summary>
    /// The director's predicate decides: an activation on its primary's silo is asked nothing; once the primary moves to a
    /// silo which does not host its type, still nothing; once that silo hosts it, with no further move, the activation is
    /// asked; and the silo it migrates to asks it nothing. An activation of a type not placed by journal never is.
    /// </summary>
    [Fact]
    public async Task AnActivationIsAskedToMigrateWhileTheDirectorWouldPlaceItOnAnotherSilo()
    {
        var placed = new FakeGrainContext(Grain(Placed, "a"));
        var unplaced = new FakeGrainContext(Grain(Unplaced, "a"));
        Track(placed);
        Track(unplaced);
        Assert.Equal(0, _replacement.Replace());

        await MoveRootAsync(Node(2));
        Assert.Equal(0, _replacement.Replace());

        _manifests.Host((Silo(1, 1), [Placed, Unplaced]), (Silo(2, 1), [Placed, Unplaced]), (Silo(3, 1), [Placed, Unplaced]));
        Assert.Equal(1, _replacement.Replace());
        Assert.Equal((1, 0), (placed.Migrations, unplaced.Migrations));
        Assert.Equal(1, _replacement.Migrations);

        using var target = Replacement(Silo(2, 1));
        _contexts.Current = new FakeGrainContext(Grain(Placed, "a"));
        target.Track(JournalId.FromGrainId(Grain(Placed, "a")));
        Assert.Equal(0, target.Replace());
    }

    /// <summary>
    /// A primary whose silo membership does not show active, which the registry does not hold, or which is retired asks
    /// nothing; once the joining silo turns active, with no move, the activation is asked.
    /// </summary>
    [Fact]
    public async Task AnActivationWhosePrimaryHasNoActiveRegisteredSiloStaysUntilItHasOne()
    {
        var placed = new FakeGrainContext(Grain(Placed, "a"));
        Track(placed);

        await MoveRootAsync(Node(4));
        Assert.Equal(0, _replacement.Replace());

        await using (var committer = Committer(_registry))
        {
            _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await committer.RegisterAsync(Entry(5, 1, ordinal: 1), StoreA, Node(2), Token)).Value);
        }

        _resolver.RequestReread();
        await WaitUntilAsync(() => _resolver.Current?.Find(Node(2))?.IsRetired is true);
        _manifests.Host((Silo(1, 1), [Placed]), (Silo(2, 1), [Placed]), (Silo(3, 1), [Placed]));
        await MoveRootAsync(Node(2));
        Assert.Equal(0, _replacement.Replace());

        await MoveRootAsync(Node(3));
        Assert.Equal(0, _replacement.Replace());
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active), (Silo(3, 1), SiloStatus.Active));
        Assert.Equal(1, _replacement.Replace());
        Assert.Equal(1, placed.Migrations);
    }

    /// <summary>A pass asks at most its bound, and the next pass asks the rest.</summary>
    [Fact]
    public async Task APassAsksAtMostItsBoundAndTheNextAsksTheRest()
    {
        var count = JournalReplacement.MaxMigrationsPerPass + 7;
        for (var index = 0; index < count; index++)
        {
            Track(new FakeGrainContext(Grain(Placed, index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        await MoveRootAsync(Node(3));
        _membership.Set((Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Active), (Silo(3, 1), SiloStatus.Active));
        Assert.Equal(JournalReplacement.MaxMigrationsPerPass, _replacement.Replace());
        Assert.Equal(7, _replacement.Replace());
        Assert.Equal(0, _replacement.Replace());
    }

    private static GrainId Grain(GrainType type, string key) => GrainId.Create(type, IdSpan.Create(key));

    private static LedgerOpening Opening(KeyRange range, NodeId primary) =>
        new(range, [primary, .. new[] { Node(1), Node(2), Node(3), Node(4) }.Where(node => node != primary).Take(2)], 4, new PrimaryClaim(primary, SequencerInstanceId.New()));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30), "The condition held within 30 seconds.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Token);
        }
    }

    private JournalReplacement Replacement(SiloAddress silo) =>
        new(_routing, _director, _membership, _manifests, new FakeSiloDetails(silo), _contexts, type => type == Placed, TimeSpan.FromSeconds(10), TimeProvider.System, NullLoggerFactory.Instance);

    /// <summary>Tracks <paramref name="context"/> as its journal storage's creation does, within the activation.</summary>
    private void Track(FakeGrainContext context)
    {
        _contexts.Current = context;
        _replacement.Track(JournalId.FromGrainId(context.GrainId));
    }

    /// <summary>Replaces the root's open ledger with one whose primary is <paramref name="primary"/>.</summary>
    private async Task MoveRootAsync(NodeId primary)
    {
        var root = Assert.Single(_view.Current.Ledgers, static ledger => ledger.IsOpen);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await _committer.CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(KeyRange.Root, primary)], 1), Token)).Value);
    }

    /// <summary>The grain context accessor: the activation a test names, or the silo's hosted client outside one.</summary>
    private sealed class FakeContexts : IGrainContextAccessor
    {
        public static FakeGrainContext HostedClient { get; } = new(GrainId.Create(GrainType.Create("sys.client"), IdSpan.Create("hosted")));

        public IGrainContext Current { get; set; } = HostedClient;

        public IGrainContext GrainContext => Current;
    }

    /// <summary>The cluster manifest: the grain types each silo hosts, as a test sets them.</summary>
    private sealed class FakeManifests : IClusterManifestProvider
    {
        private int _version;

        public ClusterManifest Current { get; private set; } = new(MajorMinorVersion.Zero, []);

        public IAsyncEnumerable<ClusterManifest> Updates => throw new NotSupportedException();

        public GrainManifest LocalGrainManifest => throw new NotSupportedException();

        public void Host(params (SiloAddress Silo, GrainType[] Types)[] silos) =>
            Current = new(
                new MajorMinorVersion(++_version, 0),
                silos.ToImmutableDictionary(
                    static silo => silo.Silo,
                    static silo => new GrainManifest(
                        silo.Types.ToImmutableDictionary(static type => type, static _ => new GrainProperties(ImmutableDictionary.Create<string, string>(StringComparer.Ordinal))),
                        [])));
    }

    private sealed class FakeSiloDetails(SiloAddress silo) : ILocalSiloDetails
    {
        public string Name => silo.ToString();

        public string ClusterId => "test";

        public string DnsHostName => silo.ToString();

        public SiloAddress SiloAddress => silo;

        public SiloAddress GatewayAddress => silo;
    }
}
