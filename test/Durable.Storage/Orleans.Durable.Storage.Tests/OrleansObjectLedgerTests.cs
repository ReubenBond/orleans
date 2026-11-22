using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Focused unit tests for row 7.5's Orleans composition pieces (orleans.md §5.14, §6): option validation for diskless
/// silos and object-store ledgers, single-provider wiring in <see cref="SiloArchives.CreateObjectLedgers"/>,
/// <see cref="NodeRegistryCommitter"/> adoption and mismatch refusal of <see cref="ObjectLedgerStoreId"/> plus diskless
/// registration, and <see cref="NodePressure"/> isolating disk pressure from object-store ledgers while throttling them
/// when the object-store request budget is exhausted.
/// </summary>
public sealed class OrleansObjectLedgerUnitTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>orleans.md §5.14, §6: diskless silos require <see cref="DurableStorageOptions.ObjectLedgers"/> and forbid an ordinal.</summary>
    [Fact]
    public void OptionsValidationEnforcesDisklessAndObjectLedgerRules()
    {
        var diskless = ValidOptions(root: "");
        diskless.ObjectLedgers = new ObjectLedgerStoreOptions();
        diskless.Validate();

        var disklessWithoutObjects = ValidOptions(root: "");
        _ = Assert.Throws<ArgumentException>(disklessWithoutObjects.Validate);

        var disklessWithOrdinal = ValidOptions(root: "");
        disklessWithOrdinal.ObjectLedgers = new ObjectLedgerStoreOptions();
        disklessWithOrdinal.Ordinal = 0;
        _ = Assert.Throws<ArgumentException>(disklessWithOrdinal.Validate);

        var appendLayout = ValidOptions(root: "");
        appendLayout.ObjectLedgers = new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append };
        appendLayout.Validate();

        var oversizedBatch = ValidOptions(root: "");
        oversizedBatch.ObjectLedgers = new ObjectLedgerStoreOptions { MaxBatchBytes = 4096 };
        oversizedBatch.Journals.MaxBodyBytes = 4096;
        _ = Assert.Throws<ArgumentOutOfRangeException>(oversizedBatch.Validate);

        var control = new ControlOptions
        {
            LedgerPlacement = LedgerPlacement.ObjectStore,
            ObjectLedgerWindow = 3,
            ObjectStoreRequestsPerSecond = 250,
        };
        control.Validate();
        Assert.Equal(3, control.EffectiveObjectLedgerWindow(ObjectLedgerLayout.PerSlot));
        Assert.Equal(2, new ControlOptions().EffectiveObjectLedgerWindow(ObjectLedgerLayout.PerSlot));
        Assert.Equal(4, new ControlOptions().EffectiveObjectLedgerWindow(ObjectLedgerLayout.Append));

        var badWindow = new ControlOptions { ObjectLedgerWindow = 0 };
        _ = Assert.Throws<ArgumentOutOfRangeException>(badWindow.Validate);

        var badRate = new ControlOptions { ObjectStoreRequestsPerSecond = 0 };
        _ = Assert.Throws<ArgumentOutOfRangeException>(badRate.Validate);

        var badPlacement = new ControlOptions { LedgerPlacement = (LedgerPlacement)99 };
        _ = Assert.Throws<ArgumentOutOfRangeException>(badPlacement.Validate);
    }

    /// <summary>
    /// orleans.md §5.14: <see cref="SiloArchives.CreateObjectLedgers"/> requires one provider instance of both object store
    /// interfaces, prefixes it with <see cref="PrefixedAppendableObjectStore"/>, and refuses two distinct instances or an
    /// append layout without <see cref="IAppendableObjectStore"/>.
    /// </summary>
    [Fact]
    public async Task CreateObjectLedgersUsesOnePrefixedProviderInstanceAndRefusesSplitProviders()
    {
        var shared = new InMemoryObjectStore();
        var services = new ServiceCollection();
        _ = services.AddSingleton(ValidOptions(root: "", prefix: "cluster1", objectLedgers: new ObjectLedgerStoreOptions()));
        _ = services.AddSingleton(Options.Create(new ClusterOptions { ServiceId = "svc" }));
        _ = services.AddSingleton<IObjectStore>(shared);
        _ = services.AddSingleton<IAppendableObjectStore>(shared);
        await using (var provider = services.BuildServiceProvider())
        {
            var ledgers = SiloArchives.CreateObjectLedgers(provider);
            Assert.True(ledgers.IsConfigured);
            var id = await ledgers.FirstRecordAsync(Token);
            Assert.NotEqual(UInt128.Zero, id.Value);
            Assert.NotNull(await shared.GetPropertiesAsync(new ObjectName("cluster1/" + ObjectLedgerStore.StoreRecordName.Value), Token));
        }

        // Two distinct instances registered for IObjectStore and IAppendableObjectStore must be refused.
        var split = new ServiceCollection();
        _ = split.AddSingleton(ValidOptions(root: "", prefix: "cluster1", objectLedgers: new ObjectLedgerStoreOptions()));
        _ = split.AddSingleton(Options.Create(new ClusterOptions { ServiceId = "svc" }));
        _ = split.AddSingleton<IObjectStore>(new InMemoryObjectStore());
        _ = split.AddSingleton<IAppendableObjectStore>(new InMemoryObjectStore());
        await using (var provider = split.BuildServiceProvider())
        {
            _ = Assert.Throws<InvalidOperationException>(() => SiloArchives.CreateObjectLedgers(provider));
        }

        // Append layout without an IAppendableObjectStore must be refused.
        var nonAppend = new ServiceCollection();
        _ = nonAppend.AddSingleton(ValidOptions(root: "", prefix: "cluster1", objectLedgers: new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append }));
        _ = nonAppend.AddSingleton(Options.Create(new ClusterOptions { ServiceId = "svc" }));
        _ = nonAppend.AddSingleton<IObjectStore>(new NonAppendableObjectStore(new InMemoryObjectStore()));
        await using (var provider = nonAppend.BuildServiceProvider())
        {
            _ = Assert.Throws<InvalidOperationException>(() => SiloArchives.CreateObjectLedgers(provider));
        }

        // Keyed DI seam allows targeting a separate bucket/account for ObjectLedgers from the unkeyed LSM IObjectStore.
        var archiveStore = new InMemoryObjectStore();
        var dedicatedObjectStore = new InMemoryObjectStore();
        var keyed = new ServiceCollection();
        _ = keyed.AddSingleton(ValidOptions(root: "", prefix: "cluster2", objectLedgers: new ObjectLedgerStoreOptions()));
        _ = keyed.AddSingleton(Options.Create(new ClusterOptions { ServiceId = "svc" }));
        _ = keyed.AddSingleton<IObjectStore>(archiveStore);
        _ = keyed.AddSingleton<IAppendableObjectStore>(archiveStore);
        _ = keyed.AddKeyedSingleton<IObjectStore>(DurableStorageOptions.ObjectLedgersStoreServiceKey, dedicatedObjectStore);
        _ = keyed.AddKeyedSingleton<IAppendableObjectStore>(DurableStorageOptions.ObjectLedgersStoreServiceKey, dedicatedObjectStore);
        await using (var provider = keyed.BuildServiceProvider())
        {
            var ledgers = SiloArchives.CreateObjectLedgers(provider);
            _ = await ledgers.FirstRecordAsync(Token);
            Assert.NotNull(await dedicatedObjectStore.GetPropertiesAsync(new ObjectName("cluster2/" + ObjectLedgerStore.StoreRecordName.Value), Token));
            Assert.Null(await archiveStore.GetPropertiesAsync(new ObjectName("cluster2/" + ObjectLedgerStore.StoreRecordName.Value), Token));
        }
    }

    /// <summary>
    /// OL-I1: <see cref="ObjectLedgerGate.RequirePassed"/> fails closed while pending, refused, or cancelled, and returns
    /// the verified store only once <see cref="ObjectLedgerGate.Pass"/> has run.
    /// </summary>
    [Fact]
    public void ObjectLedgerGateFailsClosedUntilPassedAndAfterRefusal()
    {
        var store = ObjectLedgerStore.OverAppendable(new InMemoryObjectStore(), new ObjectLedgerStoreOptions(), TimeProvider.System);

        var pending = new ObjectLedgerGate();
        Assert.False(pending.IsPassed);
        _ = Assert.Throws<InvalidOperationException>(pending.RequirePassed);
        pending.Pass(store);
        Assert.True(pending.IsPassed);
        Assert.Same(store, pending.RequirePassed());

        var refused = new ObjectLedgerGate();
        var refusal = new ObjectLedgerStoreMismatchException("mismatch");
        refused.Refuse(refusal);
        Assert.False(refused.IsPassed);
        var failed = Assert.Throws<InvalidOperationException>(refused.RequirePassed);
        Assert.Same(refusal, failed.InnerException);

        var cancelled = new ObjectLedgerGate();
        cancelled.Cancel();
        Assert.False(cancelled.IsPassed);
        _ = Assert.Throws<InvalidOperationException>(cancelled.RequirePassed);
    }

    /// <summary>
    /// orleans.md §5.14: the first silo configuring object ledgers adopts the store ID into <c>CLNR</c>; a later silo with
    /// the same ID succeeds (including diskless silos); a later silo with a different ID receives
    /// <see cref="NodeRegistrationOutcome.WrongObjectLedgerStore"/>.
    /// </summary>
    [Fact]
    public async Task NodeRegistryAdoptsObjectLedgerStoreIdRecordsDisklessAndRefusesMismatch()
    {
        var metaStore = new InMemoryMetaStore();
        var cluster = Guid.NewGuid();
        var register = await metaStore.GetRegisterAsync(cluster, NodeRegistryValue.RegisterName, Token);
        await using var committer = new NodeRegistryCommitter(register, NodeRegistryCodec.MaxValueLength, NullLogger<NodeRegistryCommitter>.Instance);
        var archive = new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, new ArchiveStorePrefix("t"), new ArchiveStoreId(new UInt128(0, 1)));
        var objectStoreA = new ObjectLedgerStoreId(new UInt128(0, 0xA1));
        var objectStoreB = new ObjectLedgerStoreId(new UInt128(0, 0xB2));

        var diskfulNode = new NodeId(Guid.NewGuid());
        var first = await committer.RegisterAsync(
            new NodeRegistryEntry(diskfulNode, SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1), ordinal: 0, failureDomain: "zone-a", retiredRevision: 0, diskless: false),
            archive,
            nodeToReplace: null,
            objectLedgerStore: objectStoreA,
            cancellationToken: Token);
        _ = Assert.IsType<NodeRegistrationOutcome.Registered>(first.Value);
        var afterFirst = (await NodeRegistryCodec.ReadAsync(register, Token)).Value;
        Assert.Equal(objectStoreA, afterFirst.ObjectLedgerStore);
        Assert.False(afterFirst.Find(diskfulNode)!.Diskless);

        var disklessNode = new NodeId(Guid.NewGuid());
        var second = await committer.RegisterAsync(
            new NodeRegistryEntry(disklessNode, SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11112), 1), ordinal: null, failureDomain: "zone-b", retiredRevision: 0, diskless: true),
            archive,
            nodeToReplace: null,
            objectLedgerStore: objectStoreA,
            cancellationToken: Token);
        _ = Assert.IsType<NodeRegistrationOutcome.Registered>(second.Value);
        var afterSecond = (await NodeRegistryCodec.ReadAsync(register, Token)).Value;
        Assert.Equal(objectStoreA, afterSecond.ObjectLedgerStore);
        Assert.True(afterSecond.Find(disklessNode)!.Diskless);

        var wrongNode = new NodeId(Guid.NewGuid());
        var mismatch = await committer.RegisterAsync(
            new NodeRegistryEntry(wrongNode, SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11113), 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true),
            archive,
            nodeToReplace: null,
            objectLedgerStore: objectStoreB,
            cancellationToken: Token);
        _ = Assert.IsType<NodeRegistrationOutcome.WrongObjectLedgerStore>(mismatch.Value);
        Assert.Null((await NodeRegistryCodec.ReadAsync(register, Token)).Value.Find(wrongNode));
    }

    /// <summary>
    /// orleans.md §5.9, §5.14, OR-I10: disk pressure throttles node-backed ledgers only and never object-store ledgers; an
    /// exhausted object-store request budget throttles object-store ledgers only.
    /// </summary>
    [Fact]
    public void NodePressureSeparatesDiskPressureFromObjectStoreRequestThrottling()
    {
        var clock = new FakeTimeProvider();
        var pressure = new NodePressure(TimeSpan.FromSeconds(1), clock, NullLogger<NodePressure>.Instance);
        var primary = new NodeId(Guid.NewGuid());
        var replica = new NodeId(Guid.NewGuid());
        var third = new NodeId(Guid.NewGuid());
        var nodesLedger = new LedgerMetadata(
            new LedgerId(1),
            KeyRange.Root,
            new LedgerLocation.Nodes([primary, replica, third]),
            k: 4,
            new PrimaryClaim(primary, SequencerInstanceId.New()),
            LedgerStatus.Open.Instance);
        var objectLedger = new LedgerMetadata(
            new LedgerId(2),
            KeyRange.Root,
            LedgerLocation.ObjectStore.Instance,
            k: 2,
            new PrimaryClaim(primary, SequencerInstanceId.New()),
            LedgerStatus.Open.Instance);

        var objectThrottled = false;
        pressure.SetObjectStoreThrottled(() => objectThrottled);

        // Disk pressure on primary and replica throttles nodesLedger, never objectLedger.
        pressure.SetUnderPressure(true);
        pressure.Report(replica);
        Assert.True(pressure.IsThrottled(nodesLedger));
        Assert.False(pressure.IsThrottled(objectLedger));

        // Clearing disk pressure and exhausting object-store request budget throttles objectLedger only.
        pressure.SetUnderPressure(false);
        clock.Advance(TimeSpan.FromSeconds(NodePressure.ReportLifetimeIntervals + 1));
        objectThrottled = true;
        Assert.False(pressure.IsThrottled(nodesLedger));
        Assert.True(pressure.IsThrottled(objectLedger));
    }

    /// <summary>
    /// orleans.md §5.6, §5.14: an inactive or deactivating <see cref="ClusterControllerGrain"/> throws
    /// <see cref="ControllerInactiveException"/> on <see cref="IClusterControllerGrain.StartDrainAllAsync"/>,
    /// <see cref="IClusterControllerGrain.CancelDrainAllAsync"/>, and <see cref="IClusterControllerGrain.GetDrainAllAsync"/>
    /// even when local object-store ledgers are not configured.
    /// </summary>
    [Fact]
    public async Task ClusterControllerGrainThrowsControllerInactiveWhenInactiveOrDeactivating()
    {
        var metaStore = new InMemoryMetaStore();
        var options = ValidOptions(root: Path.GetTempPath());
        var control = new ControlOptions();
        var membership = new FakeMembership();
        await using var resolver = new NodeRegistryResolver(metaStore, options, membership, TimeProvider.System, NullLogger<NodeRegistryResolver>.Instance);
        var objects = new InMemoryObjectStore();
        var archive = new SiloArchive.Lsm(objects, new ArchiveStorePrefix("t"));
        using var sweeps = new SweepHost(control, archive.Sweeps(maintenance: null, TimeProvider.System, NullLoggerFactory.Instance), TimeProvider.System, NullLoggerFactory.Instance);
        var grain = new ClusterControllerGrain(
            metaStore,
            options,
            control,
            resolver,
            membership,
            new StubSiloDetails(SiloAddress.New(IPAddress.Loopback, 11111, 1)),
            archive,
            SiloObjectLedgers.CreateUnconfigured(),
            sweeps,
            TimeProvider.System,
            NullLoggerFactory.Instance);

        _ = await Assert.ThrowsAsync<ControllerInactiveException>(grain.StartDrainAllAsync);
        _ = await Assert.ThrowsAsync<ControllerInactiveException>(grain.CancelDrainAllAsync);
        _ = await Assert.ThrowsAsync<ControllerInactiveException>(grain.GetDrainAllAsync);

        await grain.OnDeactivateAsync(new DeactivationReason(DeactivationReasonCode.ShuttingDown, "test"), Token);

        _ = await Assert.ThrowsAsync<ControllerInactiveException>(grain.StartDrainAllAsync);
        _ = await Assert.ThrowsAsync<ControllerInactiveException>(grain.CancelDrainAllAsync);
        _ = await Assert.ThrowsAsync<ControllerInactiveException>(grain.GetDrainAllAsync);
    }

    private sealed class StubSiloDetails(SiloAddress address) : ILocalSiloDetails
    {
        public string Name => "stub";

        public string ClusterId => "test";

        public string DnsHostName => "localhost";

        public SiloAddress SiloAddress { get; } = address;

        public SiloAddress GatewayAddress { get; } = address;
    }

    private static DurableStorageOptions ValidOptions(string root, string prefix = "t", ObjectLedgerStoreOptions? objectLedgers = null) => new()
    {
        Cluster = Guid.NewGuid(),
        Root = root,
        ArchiveStore = ArchiveStoreKind.Lsm,
        ArchiveStorePrefix = prefix,
        ObjectLedgers = objectLedgers,
        MinimumFreeDiskBytes = 0,
    };

    private sealed class NonAppendableObjectStore(IObjectStore inner) : IObjectStore
    {
        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }
}

/// <summary>
/// Multi-silo <see cref="TestCluster"/> tests for object-store ledgers (orleans.md §5.14, §6, §9): a mixed cluster of
/// disk-backed and diskless silos serving both node-backed quorum ledgers and object-store ledgers, rolling ledgers
/// between locations in both directions, taking over an object-store ledger on primary stop, restarting a diskless silo
/// with a fresh node identity, sweeping dropped object-store ledgers via <see cref="ObjectLedgerSweeper"/> on the
/// controller's silo, checking every journal against its write history, and refusing a joining silo whose object-ledger
/// store record mismatches the cluster's.
/// </summary>
[Collection(TestingHostCollection.Name)]
public sealed class OrleansObjectLedgerTests : IAsyncLifetime
{
    private const string TestKey = "DurableStorageObjectLedgerTest";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, TestState> States = new(StringComparer.Ordinal);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
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
        if (_cluster is not null)
        {
            foreach (var silo in _cluster.Silos.Reverse().ToList())
            {
                await _cluster.StopSiloAsync(silo).WaitAsync(Patience, CancellationToken.None);
            }

            await _cluster.DisposeAsync();
        }

        _ = States.TryRemove(_state.Id, out _);
        if (Directory.Exists(_state.Roots))
        {
            Directory.Delete(_state.Roots, recursive: true);
        }

        _leaks.Dispose();
    }

    /// <summary>
    /// orleans.md §5.14: a mixed cluster with 3 disk-backed silos and a diskless silo (no <c>Root</c>) starts on node-backed
    /// ledgers, rolls open ledgers to <see cref="LedgerLocation.ObjectStore"/>, performs an object-store primary takeover,
    /// restarts the diskless silo (registering a fresh <see cref="NodeId"/>), rolls back to <see cref="LedgerLocation.Nodes"/>,
    /// drains and sweeps dropped object-store ledgers down to their <c>dropped</c> tombstones, and preserves every
    /// journal's full history across every transition.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task MixedClusterRollsBetweenLocationsTakesOverObjectLedgersAndRestartsDisklessSilo()
    {
        var token = TestContext.Current.CancellationToken;
        var cluster = await DeployAsync(initialSilos: 3, token);

        // Phase 1: bootstrap on node-backed quorum ledgers and record the shared object-ledger store ID in CLNR.
        _ = await ServedAsync(cluster, static map =>
            map.Ledgers.Count(static ledger => ledger.IsOpen) >= 3 &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.Nodes));
        var registry = await ReadRegistryAsync();
        var objectStoreId = Assert.NotNull(registry.ObjectLedgerStore);
        Assert.NotNull(await _state.Objects.GetPropertiesAsync(new ObjectName(_state.Prefix + "/" + ObjectLedgerStore.StoreRecordName.Value), token));

        string[] keys = ["alpha", "beta", "gamma", "delta"];
        Dictionary<string, List<byte>> expected = keys.ToDictionary(static key => key, static _ => new List<byte>(), StringComparer.Ordinal);
        await AppendRoundAsync(cluster, keys, expected, "nodes-1-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        // Phase 2: join a diskless silo (no Root, ObjectLedgers configured); verify it registers Diskless = true,
        // creates no node root directory, and is never placed as a replica of any node-backed ledger.
        _state.NextSiloDiskless = true;
        var disklessSilo = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, token);
        _state.NextSiloDiskless = false;
        await EveryResolverListsEveryNodeAsync(cluster);

        var disklessNodeId = NodeOf(disklessSilo);
        registry = await ReadRegistryAsync();
        var disklessEntry = registry.Find(disklessNodeId);
        Assert.NotNull(disklessEntry);
        Assert.True(disklessEntry.Diskless);
        Assert.Equal(objectStoreId, registry.ObjectLedgerStore);
        Assert.False(Directory.Exists(Path.Combine(_state.Roots, disklessSilo.Name)));

        // Phase 3: switch cluster LedgerPlacement to ObjectStore -> controller rolls every open Nodes ledger to ObjectStore.
        SetPlacement(cluster, LedgerPlacement.ObjectStore);
        var objectMap = await ServedAsync(cluster, static map =>
            map.Ledgers.Any(static ledger => ledger.IsOpen) &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.ObjectStore));
        Assert.All(objectMap.Ledgers.Where(static ledger => ledger.IsOpen), static ledger =>
        {
            _ = Assert.IsType<LedgerLocation.ObjectStore>(ledger.Location.Value);
        });

        await AppendRoundAsync(cluster, keys, expected, "objects-1-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        // Phase 4: takeover of an open object-store ledger when a non-primary disk-backed silo stops; verify survivor takes over.
        // Keep cluster.Primary alive because TestCluster hosts the development membership table there.
        var stoppable = cluster.Silos
            .Where(silo => silo != cluster.Primary && silo.SiloAddress != disklessSilo.SiloAddress)
            .First(silo => objectMap.Ledgers.Any(ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore && ledger.Primary.Node == NodeOf(silo)));
        var stoppedNode = NodeOf(stoppable);
        await cluster.StopSiloAsync(stoppable, token).WaitAsync(Patience, token);

        var afterTakeover = await ServedAsync(cluster, map =>
            map.Ledgers.Any(static ledger => ledger.IsOpen) &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(ledger =>
                ledger.Location.Value is LedgerLocation.ObjectStore && ledger.Primary.Node != stoppedNode));
        Assert.Contains(afterTakeover.Ledgers, ledger => ledger.IsOpen && (ledger.Primary.Node == disklessNodeId || cluster.Silos.Any(silo => NodeOf(silo) == ledger.Primary.Node)));
        await AppendRoundAsync(cluster, keys, expected, "takeover-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        // Phase 5: restart the diskless silo (stop it, start a fresh diskless silo with no Root); verify fresh NodeId.
        await cluster.StopSiloAsync(disklessSilo, token).WaitAsync(Patience, token);
        _state.NextSiloDiskless = true;
        var restartedDiskless = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, token);
        _state.NextSiloDiskless = false;
        await EveryResolverListsEveryNodeAsync(cluster);

        var restartedDisklessNodeId = NodeOf(restartedDiskless);
        Assert.NotEqual(disklessNodeId, restartedDisklessNodeId);
        registry = await ReadRegistryAsync();
        var restartedEntry = registry.Find(restartedDisklessNodeId);
        Assert.NotNull(restartedEntry);
        Assert.True(restartedEntry.Diskless);

        // Restart a third disk-backed silo WITHOUT ObjectLedgers configured (rolling-migration mixed cluster): while
        // placement is ObjectStore it never receives an open object-store ledger, and once placement rolls back to Nodes
        // it participates in the 3-node quorum.
        _state.NextSiloOmitObjectLedgers = true;
        var unconfiguredDiskful = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, token);
        _state.NextSiloOmitObjectLedgers = false;
        await EveryResolverListsEveryNodeAsync(cluster);
        var unconfiguredNodeId = NodeOf(unconfiguredDiskful);
        Assert.False(Services(unconfiguredDiskful).GetRequiredService<SiloObjectLedgers>().IsConfigured);

        await AppendRoundAsync(cluster, keys, expected, "diskless-restarted-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        var mapBeforeRollback = await ReadMapAsync();
        Assert.DoesNotContain(
            mapBeforeRollback.Ledgers,
            ledger => ledger.IsOpen && ledger.Location.Value is LedgerLocation.ObjectStore && ledger.Primary.Node == unconfiguredNodeId);

        // Non-vacuous pre-sweep check: collect every object-store ledger that actually wrote non-empty batch slot objects
        // under the cluster prefix before rolling back to Nodes.
        var objectLedgerIds = mapBeforeRollback.Ledgers
            .Where(static ledger => ledger.Location.Value is LedgerLocation.ObjectStore)
            .Select(static ledger => ledger.Id)
            .Concat(objectMap.Ledgers.Where(static ledger => ledger.Location.Value is LedgerLocation.ObjectStore).Select(static ledger => ledger.Id))
            .Distinct()
            .ToArray();
        List<LedgerId> ledgersWithLiveSlots = [];
        foreach (var id in objectLedgerIds)
        {
            var names = await ListNamesAsync(_state.Objects, _state.Prefix + "/" + ObjectLedgerNames.Ledger(id), token);
            var hasSlot = false;
            foreach (var name in names)
            {
                var relative = new ObjectName(name[(_state.Prefix.Length + 1)..]);
                if (ObjectLedgerNames.ParseSlot(id, relative) is not null)
                {
                    var props = await _state.Objects.GetPropertiesAsync(new ObjectName(name), token);
                    if (props is not null && props.Length > 0)
                    {
                        hasSlot = true;
                        break;
                    }
                }
            }

            if (hasSlot)
            {
                ledgersWithLiveSlots.Add(id);
            }
        }

        Assert.NotEmpty(ledgersWithLiveSlots);

        // Phase 6: roll open ledgers back from ObjectStore to Nodes; verify closed object-store ledgers drain, drop,
        // and are swept by ObjectLedgerSweeper (leaving only their dropped tombstone under the cluster prefix).
        SetPlacement(cluster, LedgerPlacement.Nodes);
        var rolledBackMap = await ServedAsync(cluster, static map =>
            map.Ledgers.Any(static ledger => ledger.IsOpen) &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.Nodes));
        Assert.All(rolledBackMap.Ledgers.Where(static ledger => ledger.IsOpen), ledger =>
        {
            Assert.Equal(3, ledger.Replicas.Distinct().Count());
            Assert.DoesNotContain(restartedDisklessNodeId, ledger.Replicas);
        });

        await AppendRoundAsync(cluster, keys, expected, "nodes-2-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        // Wait until every object-store ledger with live batch objects has drained, dropped from the map, and been swept
        // down to its dropped marker with zero remaining batch slot objects.
        await EventuallyAsync(async () =>
        {
            var map = await ReadMapAsync();
            if (ledgersWithLiveSlots.Any(id => map.Ledgers.Any(ledger => ledger.Id == id)))
            {
                return false;
            }

            foreach (var id in ledgersWithLiveSlots)
            {
                var names = await ListNamesAsync(_state.Objects, _state.Prefix + "/" + ObjectLedgerNames.Ledger(id), token);
                if (!names.Contains(_state.Prefix + "/" + ObjectLedgerNames.Dropped(id).Value, StringComparer.Ordinal))
                {
                    return false;
                }

                foreach (var name in names)
                {
                    var relative = new ObjectName(name[(_state.Prefix.Length + 1)..]);
                    if (ObjectLedgerNames.ParseSlot(id, relative) is not null)
                    {
                        var props = await _state.Objects.GetPropertiesAsync(new ObjectName(name), token);
                        if (props is not null && props.Length > 0)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }, "closed object-store ledgers to drain, drop, and be swept");

        // Final check: deactivate every grain and verify full history recovers cleanly from Archive + open Nodes ledgers.
        await VerifyHistoryAsync(cluster, keys, expected, token);
    }

    /// <summary>
    /// orleans.md §5.14, OL-I1: joining silos are refused on startup when their object-ledger store holds a different ID,
    /// has been emptied (without recreating <c>ledgers/store</c>), holds a corrupt <c>ledgers/store</c> record, or holds a
    /// record whose layout disagrees with the silo's configured layout.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task JoiningSiloRefusesMismatchedEmptiedCorruptAndWrongLayoutObjectLedgerStore()
    {
        var token = TestContext.Current.CancellationToken;
        var cluster = await DeployAsync(initialSilos: 1, token);
        var before = await ReadRegistryAsync();
        var recordedStore = Assert.NotNull(before.ObjectLedgerStore);
        var storeRecordName = new ObjectName(_state.Prefix + "/" + ObjectLedgerStore.StoreRecordName.Value);

        // 1. Different store ID.
        var rogueStore = await CloneArchiveWithoutObjectLedgerRecordAsync(token);
        var rogueLedgers = ObjectLedgerStore.OverAppendable(
            new PrefixedAppendableObjectStore(rogueStore, rogueStore, _state.Prefix + "/"),
            new ObjectLedgerStoreOptions(),
            TimeProvider.System);
        var rogueRecord = await rogueLedgers.StoreRecordAsync(token);
        Assert.NotEqual(recordedStore, rogueRecord.Id);
        await AssertJoiningSiloRefusedAsync<ObjectLedgerStoreMismatchException>(cluster, rogueStore, token);

        // 2. Emptied store under a registered ID: must refuse and must NOT recreate ledgers/store.
        var emptiedStore = await CloneArchiveWithoutObjectLedgerRecordAsync(token);
        Assert.Null(await emptiedStore.GetPropertiesAsync(storeRecordName, token));
        await AssertJoiningSiloRefusedAsync<ObjectLedgerStoreMismatchException>(cluster, emptiedStore, token);
        Assert.Null(await emptiedStore.GetPropertiesAsync(storeRecordName, token));

        // 3. Corrupt ledgers/store record.
        var corruptStore = await CloneArchiveWithoutObjectLedgerRecordAsync(token);
        using (var garbage = ArcBuffer.Wrap("not-a-valid-store-record"u8.ToArray()))
        {
            _ = await corruptStore.CreateAsync(storeRecordName, garbage, crc32c: null, token);
        }

        await AssertJoiningSiloRefusedAsync<InvalidDataException>(cluster, corruptStore, token);

        // 4. Layout mismatch (record has layout=append while joining silo configures PerSlot).
        var wrongLayoutStore = await CloneArchiveWithoutObjectLedgerRecordAsync(token);
        using (var appendRecord = ArcBuffer.Wrap(new ObjectLedgerStoreRecord(recordedStore, ObjectLedgerLayout.Append).Encode()))
        {
            _ = await wrongLayoutStore.CreateAsync(storeRecordName, appendRecord, crc32c: null, token);
        }

        await AssertJoiningSiloRefusedAsync<ObjectLedgerStoreMismatchException>(cluster, wrongLayoutStore, token);

        var after = await ReadRegistryAsync();
        Assert.Equal(recordedStore, after.ObjectLedgerStore);
    }

    /// <summary>
    /// Audit #3 / orleans.md §5.14: when a joining silo's LSM store record differs only in an archive parameter (e.g.
    /// <c>JournalFormatKey</c>), its archive gate passes so it can keep draining, its local journals are refused, and its
    /// object-ledger gate checks independently and passes instead of being faulted by the LSM parameter mismatch.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task JoiningSiloWithLsmParameterMismatchKeepsObjectLedgersGatePassed()
    {
        var token = TestContext.Current.CancellationToken;
        var cluster = await DeployAsync(initialSilos: 1, token);

        _state.NextSiloJournalFormatKey = "orleans-binary"; // differs from default JournalFormatKey
        var mismatchedSilo = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, token);
        _state.NextSiloJournalFormatKey = null;

        try
        {
            var journals = Services(mismatchedSilo).GetRequiredService<LocalJournals>();
            var archive = Services(mismatchedSilo).GetRequiredService<SiloArchive>();
            var objectLedgers = Services(mismatchedSilo).GetRequiredService<SiloObjectLedgers>();

            await EventuallyAsync(() => Task.FromResult(journals.State == LocalJournals.JournalsState.Refused), "the mismatched silo's Journals to be refused");
            Assert.True(archive.Gate.IsPassed);
            _ = await objectLedgers.Gate.Passed.WaitAsync(Patience, token);
            Assert.True(objectLedgers.Gate.IsPassed);
        }
        finally
        {
            await cluster.StopSiloAsync(mismatchedSilo, token).WaitAsync(Patience, token);
        }
    }

    /// <summary>
    /// Row 7.5 + Row 7.4 (<see cref="ObjectLedgerLayout.Append"/>): end-to-end multi-silo cluster configured with
    /// <see cref="ObjectLedgerLayout.Append"/> writes journal entries into <c>CLOE</c> extents under the cluster prefix,
    /// survives a diskless silo join, rolls back to <c>Nodes</c>, and sweeps revoked append ledgers down to their
    /// <c>dropped</c> marker and 17-byte <c>SEAL</c> tombstone while preserving full journal history.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AppendLayoutClusterWritesRollsBackAndSweepsEndToEnd()
    {
        var token = TestContext.Current.CancellationToken;
        _state.ObjectLedgerLayout = ObjectLedgerLayout.Append;
        _state.InitialPlacement = LedgerPlacement.ObjectStore;

        var cluster = await DeployAsync(initialSilos: 3, token);
        var keys = Enumerable.Range(0, 4).Select(static i => $"append-key-{i}").ToArray();
        var expected = keys.ToDictionary(static k => k, static _ => new List<byte>());

        var initialMap = await ServedAsync(cluster, static map =>
            map.Ledgers.Any(static l => l.IsOpen) &&
            map.Ledgers.Where(static l => l.IsOpen).All(static l => l.Location.Value is LedgerLocation.ObjectStore));

        // Verify ledgers/store recorded the Append layout.
        var recordObj = await _state.Objects.ReadVersionedAsync(new ObjectName(_state.Prefix + "/" + ObjectLedgerStore.StoreRecordName.Value), token);
        Assert.NotNull(recordObj);
        using (recordObj.Value.Content)
        {
            var decoded = Assert.IsType<ObjectLedgerStoreRecord>(ObjectLedgerStoreRecord.Decode(recordObj.Value.Content.ToArray()).Value);
            Assert.Equal(ObjectLedgerLayout.Append, decoded.Layout);
        }

        await AppendRoundAsync(cluster, keys, expected, "append-1-", token);

        // Add a diskless silo running Append layout as well.
        _state.NextSiloDiskless = true;
        var diskless = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, token);
        _state.NextSiloDiskless = false;
        await EveryResolverListsEveryNodeAsync(cluster);

        await AppendRoundAsync(cluster, keys, expected, "append-2-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        // Non-vacuous check: verify at least one open Append object-store ledger has a live extent object before rollback.
        var currentMap = await ReadMapAsync();
        var appendLedgerIds = initialMap.Ledgers
            .Concat(currentMap.Ledgers)
            .Where(static l => l.Location.Value is LedgerLocation.ObjectStore)
            .Select(static l => l.Id)
            .Distinct()
            .ToArray();
        List<LedgerId> ledgersWithExtents = [];
        foreach (var id in appendLedgerIds)
        {
            var extents = await ListNamesAsync(_state.Objects, _state.Prefix + "/" + ObjectLedgerNames.Extents(id), token);
            if (extents.Count > 0)
            {
                ledgersWithExtents.Add(id);
            }
        }

        Assert.NotEmpty(ledgersWithExtents);

        // Roll back to Nodes and verify every revoked Append ledger drains, drops, and sweeps to dropped + 17-byte SEAL tombstone.
        SetPlacement(cluster, LedgerPlacement.Nodes);
        _ = await ServedAsync(cluster, static map =>
            map.Ledgers.Any(static l => l.IsOpen) &&
            map.Ledgers.Where(static l => l.IsOpen).All(static l => l.Location.Value is LedgerLocation.Nodes));

        await AppendRoundAsync(cluster, keys, expected, "nodes-after-append-", token);

        await EventuallyAsync(async () =>
        {
            var map = await ReadMapAsync();
            if (ledgersWithExtents.Any(id => map.Ledgers.Any(l => l.Id == id)))
            {
                return false;
            }

            foreach (var id in ledgersWithExtents)
            {
                if (await _state.Objects.GetPropertiesAsync(new ObjectName(_state.Prefix + "/" + ObjectLedgerNames.Dropped(id).Value), token) is null)
                {
                    return false;
                }

                var extents = await ListNamesAsync(_state.Objects, _state.Prefix + "/" + ObjectLedgerNames.Extents(id), token);
                if (extents.Count != 1)
                {
                    return false;
                }

                var props = await _state.Objects.GetPropertiesAsync(new ObjectName(extents[0]), token);
                if (props is null || props.Length != ObjectLedgerExtentFormat.TombstoneLength || props.Crc32c is null)
                {
                    return false;
                }
            }

            return true;
        }, "revoked Append object-store ledgers to drain, drop, and sweep to 17-byte SEAL tombstones");

        await VerifyHistoryAsync(cluster, keys, expected, token);
        _ = diskless;
    }

    /// <summary>
    /// orleans.md §5.10, §5.14, §8 (row 4.8 B): a whole-cluster drain-all on instance-local NVMe rolls every open
    /// <see cref="LedgerLocation.Nodes"/> ledger into <see cref="LedgerLocation.ObjectStore"/>, drains and drops every
    /// closed node-backed ledger, retires every active diskful node in <c>CLNR</c>, and raises the map's floor past those
    /// retirements so <see cref="IClusterControllerGrain.GetDrainAllAsync"/> reports <see cref="DrainAllStatus.IsReady"/>.
    /// Stopping every silo, wiping every silo's <c>Root</c> directory, and restarting fresh silos over empty roots at the
    /// same StatefulSet ordinals registers fresh node identities without <c>OrdinalOccupied</c>, serves every range with
    /// zero lost acknowledged entries, and automatically rolls every range back onto the new <c>Nodes</c> ledgers.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task WholeClusterDrainAllStopsReleasesRootsAndRestartsServingAllHistoryAndRollsBackToNodes()
    {
        var token = TestContext.Current.CancellationToken;
        _state.UseOrdinals = true;
        var cluster = await DeployAsync(initialSilos: 3, token);

        // Phase 1: bootstrap on node-backed quorum ledgers at StatefulSet ordinals 0, 1, 2.
        _ = await ServedAsync(cluster, static map =>
            map.Ledgers.Count(static ledger => ledger.IsOpen) >= 3 &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.Nodes));

        var initialNodes = cluster.Silos.Select(NodeOf).ToHashSet();
        Assert.Equal(3, initialNodes.Count);
        var initialRegistry = await ReadRegistryAsync();
        Assert.Equal(
            [0, 1, 2],
            [.. initialNodes.Select(node => initialRegistry.Find(node)!.Ordinal!.Value).Order()]);

        string[] keys = ["nvme-alpha", "nvme-beta", "nvme-gamma", "nvme-delta"];
        Dictionary<string, List<byte>> expected = keys.ToDictionary(static key => key, static _ => new List<byte>(), StringComparer.Ordinal);
        await AppendRoundAsync(cluster, keys, expected, "pre-drain-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);

        // Phase 2: invoke whole-cluster drain-all and keep writing while it rolls and drains.
        var controller = cluster.GrainFactory.GetGrain<IClusterControllerGrain>(0);
        var started = await controller.StartDrainAllAsync().WaitAsync(Patience, token);
        _ = Assert.IsType<DrainAllStartOutcome.Started>(started.Value);

        await AppendRoundAsync(cluster, keys, expected, "during-drain-", token);

        // Wait until durable readiness holds: zero Nodes ledgers, zero active diskful nodes, floor covers retirements.
        DrainAllStatus? ready = null;
        await EventuallyAsync(async () =>
        {
            var status = await controller.GetDrainAllAsync().WaitAsync(Patience, token);
            if (!status.IsReady)
            {
                return false;
            }

            ready = status;
            return true;
        }, "whole-cluster drain-all to reach durable readiness");

        Assert.NotNull(ready);
        Assert.False(ready.Active);
        Assert.Empty(ready.NodeLedgers);
        Assert.Empty(ready.ActiveDiskfulNodes);
        Assert.True(ready.FloorCovers);
        Assert.True(ready.RegistryRevision > 0);

        var drainedMap = await ReadMapAsync();
        Assert.DoesNotContain(drainedMap.Ledgers, static ledger => ledger.Location.Value is LedgerLocation.Nodes);
        Assert.NotEmpty(drainedMap.Ledgers.Where(static ledger => ledger.IsOpen));
        Assert.All(drainedMap.Ledgers.Where(static ledger => ledger.IsOpen), static ledger =>
        {
            _ = Assert.IsType<LedgerLocation.ObjectStore>(ledger.Location.Value);
        });

        var drainedRegistry = await ReadRegistryAsync();
        Assert.Equal(0UL, drainedRegistry.DrainAllRevision);
        foreach (var node in initialNodes)
        {
            var entry = drainedRegistry.Find(node);
            Assert.NotNull(entry);
            Assert.True(entry.IsRetired);
            Assert.True(drainedMap.RetirementFloor >= entry.RetiredRevision);
        }

        // Write one more round on the drained ObjectStore ledgers before releasing instances.
        await AppendRoundAsync(cluster, keys, expected, "drained-object-", token);

        // Phase 3: stop every silo and wipe every silo's Root directory (simulating instance-local NVMe release).
        foreach (var silo in cluster.Silos.Reverse().ToList())
        {
            await cluster.StopSiloAsync(silo, token).WaitAsync(Patience, token);
        }

        await cluster.DisposeAsync();
        _cluster = null;

        Assert.True(Directory.Exists(_state.Roots));
        Assert.NotEmpty(Directory.EnumerateFiles(_state.Roots, "*", SearchOption.AllDirectories));
        Directory.Delete(_state.Roots, recursive: true);

        // Phase 4: restart 3 fresh silos over empty roots at the exact same StatefulSet ordinals (0, 1, 2).
        var restarted = await DeployAsync(initialSilos: 3, token);
        var restartedNodes = restarted.Silos.Select(NodeOf).ToHashSet();
        Assert.Equal(3, restartedNodes.Count);
        Assert.Empty(initialNodes.Intersect(restartedNodes));
        var restartedRegistry = await ReadRegistryAsync();
        Assert.Equal(
            [0, 1, 2],
            [.. restartedNodes.Select(node => restartedRegistry.Find(node)!.Ordinal!.Value).Order()]);

        // Phase 2 already reset CLNR.DrainAllRevision to 0; after restart fresh diskful nodes are active and not ready to stop.
        var restartedController = restarted.GrainFactory.GetGrain<IClusterControllerGrain>(0);
        var postRestartStatus = await restartedController.GetDrainAllAsync().WaitAsync(Patience, token);
        Assert.False(postRestartStatus.Active);
        Assert.False(postRestartStatus.IsReady);
        Assert.Equal(3, postRestartStatus.ActiveDiskfulNodes.Length);

        // Every journal serves 100% of its acknowledged history across the empty-root restart.
        await VerifyHistoryAsync(restarted, keys, expected, token);

        // The controller automatically rolls every range back onto Nodes ledgers on the 3 fresh nodes.
        var rolledBackMap = await ServedAsync(restarted, map =>
            map.Ledgers.Any(static ledger => ledger.IsOpen) &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(ledger =>
                ledger.Location.Value is LedgerLocation.Nodes &&
                ledger.Replicas.All(restartedNodes.Contains)));
        Assert.All(rolledBackMap.Ledgers.Where(static ledger => ledger.IsOpen), static ledger =>
        {
            Assert.Equal(3, ledger.Replicas.Distinct().Count());
        });

        // Subsequent writes and history reads succeed on the new Nodes ledgers.
        await AppendRoundAsync(restarted, keys, expected, "post-restart-nodes-", token);
        await VerifyHistoryAsync(restarted, keys, expected, token);
    }

    /// <summary>
    /// orleans.md §5.14, §8 (row 4.8 B): invoking <see cref="IClusterControllerGrain.StartDrainAllAsync"/> on a cluster
    /// without <see cref="DurableStorageOptions.ObjectLedgers"/> configured fails closed with
    /// <see cref="DrainAllStartOutcome.ObjectLedgersNotConfigured"/> and leaves the cluster serving on <c>Nodes</c> ledgers.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task StartDrainAllFailsClosedWithoutObjectLedgersConfigured()
    {
        var token = TestContext.Current.CancellationToken;
        _state.NextSiloOmitObjectLedgers = true;
        var cluster = await DeployAsync(initialSilos: 3, token);

        _ = await ServedAsync(cluster, static map =>
            map.Ledgers.Count(static ledger => ledger.IsOpen) >= 3 &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.Nodes));

        var controller = cluster.GrainFactory.GetGrain<IClusterControllerGrain>(0);
        var outcome = await controller.StartDrainAllAsync().WaitAsync(Patience, token);
        _ = Assert.IsType<DrainAllStartOutcome.ObjectLedgersNotConfigured>(outcome.Value);

        var status = await controller.GetDrainAllAsync().WaitAsync(Patience, token);
        Assert.False(status.Active);
        Assert.False(status.IsReady);
        Assert.NotEmpty(status.NodeLedgers);
        Assert.Equal(3, status.ActiveDiskfulNodes.Length);
    }

    /// <summary>
    /// orleans.md §5.14, §8 (row 4.8 B): during Phase 1 of drain-all, <see cref="IClusterControllerGrain.GetDrainAllAsync"/>
    /// reports <see cref="DrainAllStatus.Active"/> with <see cref="DrainAllStatus.IsReady"/> false and non-empty
    /// <see cref="DrainAllStatus.NodeLedgers"/> and <see cref="DrainAllStatus.ActiveDiskfulNodes"/>; cancelling via
    /// <see cref="IClusterControllerGrain.CancelDrainAllAsync"/> clears <c>CLNR.DrainAllRevision</c> without retiring any
    /// diskful node and rolls open ranges back onto the same <c>Nodes</c> ledgers.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task CancelDrainAllAbortsPhase1AndRestoresNodesPlacementWithoutRetiringNodes()
    {
        var token = TestContext.Current.CancellationToken;
        var manifestGate = new ManifestGateObjectStore(_state.Objects);
        _state.ManifestGate = manifestGate;
        var cluster = await DeployAsync(initialSilos: 3, token);

        _ = await ServedAsync(cluster, static map =>
            map.Ledgers.Count(static ledger => ledger.IsOpen) >= 3 &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.Nodes));
        var initialNodes = cluster.Silos.Select(NodeOf).ToHashSet();

        string[] keys = ["cancel-a", "cancel-b", "cancel-c"];
        Dictionary<string, List<byte>> expected = keys.ToDictionary(static key => key, static _ => new List<byte>(), StringComparer.Ordinal);
        await AppendRoundAsync(cluster, keys, expected, "before-cancel-", token);

        var controller = cluster.GrainFactory.GetGrain<IClusterControllerGrain>(0);
        Assert.False(await controller.CancelDrainAllAsync().WaitAsync(Patience, token));

        // Hold manifest replaces so Phase 1's closed Nodes ledgers cannot commit their drain and drop until we observe
        // Phase 1 readiness and cancel.
        manifestGate.HoldManifestReplaces();
        try
        {
            var started = await controller.StartDrainAllAsync().WaitAsync(Patience, token);
            _ = Assert.IsType<DrainAllStartOutcome.Started>(started.Value);

            // Phase 1 rolls every open Nodes ledger to ObjectStore while closed Nodes ledgers remain undropped.
            var phase1Map = await ServedAsync(cluster, static map =>
                map.Ledgers.Any(static ledger => ledger.IsOpen) &&
                map.Ledgers.Where(static ledger => ledger.IsOpen).All(static ledger => ledger.Location.Value is LedgerLocation.ObjectStore));

            var preReady = await controller.GetDrainAllAsync().WaitAsync(Patience, token);
            Assert.True(preReady.Active);
            Assert.False(preReady.IsReady);
            Assert.NotEmpty(preReady.NodeLedgers);
            Assert.Equal(3, preReady.ActiveDiskfulNodes.Length);
            var activeRegistry = await ReadRegistryAsync();
            Assert.True(activeRegistry.DrainAllRevision > 0);
            Assert.True(phase1Map.RetirementFloor >= activeRegistry.DrainAllRevision);

            // Cancel Phase 1: durable DrainAllRevision clears to 0, no diskful node is retired, and second cancel returns false.
            Assert.True(await controller.CancelDrainAllAsync().WaitAsync(Patience, token));
            Assert.False(await controller.CancelDrainAllAsync().WaitAsync(Patience, token));
        }
        finally
        {
            manifestGate.ReleaseManifestReplaces();
        }

        var cancelledRegistry = await ReadRegistryAsync();
        Assert.Equal(0UL, cancelledRegistry.DrainAllRevision);
        foreach (var node in initialNodes)
        {
            var entry = cancelledRegistry.Find(node);
            Assert.NotNull(entry);
            Assert.False(entry.IsRetired);
        }

        // Open ranges automatically roll back onto the same 3 Nodes ledgers.
        _ = await ServedAsync(cluster, map =>
            map.Ledgers.Any(static ledger => ledger.IsOpen) &&
            map.Ledgers.Where(static ledger => ledger.IsOpen).All(ledger =>
                ledger.Location.Value is LedgerLocation.Nodes &&
                ledger.Replicas.All(initialNodes.Contains)));

        await AppendRoundAsync(cluster, keys, expected, "after-cancel-", token);
        await VerifyHistoryAsync(cluster, keys, expected, token);
    }

    /// <summary>
    /// orleans.md §5.6, §5.14: when <c>CLNR</c> already records an <see cref="ObjectLedgerStoreId"/> while the sole silo
    /// hosting <see cref="IClusterControllerGrain"/> has no local <see cref="DurableStorageOptions.ObjectLedgers"/>,
    /// <see cref="IClusterControllerGrain.StartDrainAllAsync"/> reads <c>CLNR</c> and answers
    /// <see cref="DrainAllStartOutcome.ObjectLedgersNotReady"/>; when an eligible silo with object-store ledgers joins,
    /// the stranded controller migrates to it and <see cref="IClusterControllerGrain.StartDrainAllAsync"/> succeeds.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task StartDrainAllReturnsNotReadyOnSiloWithoutLocalObjectLedgersAndMigratesWhenEligibleSiloJoins()
    {
        var token = TestContext.Current.CancellationToken;

        // First boot a 1-silo cluster with ObjectLedgers so CLNR adopts ObjectLedgerStoreId, then stop it while
        // preserving MetaStore and Objects.
        var bootstrapCluster = await DeployAsync(initialSilos: 1, token);
        await EventuallyAsync(async () => (await ReadRegistryAsync()).ObjectLedgerStore is not null, "CLNR to record ObjectLedgerStore");
        await bootstrapCluster.StopAllSilosAsync(token).WaitAsync(Patience, token);
        await bootstrapCluster.DisposeAsync().AsTask().WaitAsync(Patience, token);
        _cluster = null;

        // Restart a 1-silo cluster whose sole silo (Primary, hosting development membership) has no local ObjectLedgers.
        _state.NextSiloOmitObjectLedgers = true;
        var cluster = await DeployAsync(initialSilos: 1, token);
        _state.NextSiloOmitObjectLedgers = false;
        var withoutObjectLedgers = cluster.Primary;
        Assert.NotNull(withoutObjectLedgers);

        var controller = cluster.GrainFactory.GetGrain<IClusterControllerGrain>(0);
        var management = cluster.Client.GetGrain<IManagementGrain>(0);

        var notReady = await controller.StartDrainAllAsync().WaitAsync(Patience, token);
        _ = Assert.IsType<DrainAllStartOutcome.ObjectLedgersNotReady>(notReady.Value);
        Assert.Equal(withoutObjectLedgers.SiloAddress, await management.GetActivationAddress(controller, token));

        // Idempotence (orleans.md §5.14, outcomes.md): when CLNR.DrainAllRevision > 0 is already durable, even a
        // stranded activation without local ObjectLedgers answers Started rather than ObjectLedgersNotReady.
        var registryRegister = await _state.MetaStore.GetRegisterAsync(_state.Cluster, NodeRegistryValue.RegisterName, token);
        await using (var committer = new NodeRegistryCommitter(registryRegister, NodeRegistryCodec.MaxValueLength, NullLogger<NodeRegistryCommitter>.Instance))
        {
            _ = Assert.IsType<NodeDrainAllStartOutcome.Started>((await committer.StartDrainAllAsync(token)).Value);
        }

        var idempotent = await controller.StartDrainAllAsync().WaitAsync(Patience, token);
        _ = Assert.IsType<DrainAllStartOutcome.Started>(idempotent.Value);
        // With zero Nodes ledgers in the empty map, PassIfDue converges Phase 2 and resets DrainAllRevision to 0.
        await EventuallyAsync(
            async () => (await controller.GetDrainAllAsync().WaitAsync(Patience, token)).IsReady,
            "drain-all over an empty map to converge Phase 2 and reset DrainAllRevision");
        _ = Assert.IsType<DrainAllStartOutcome.ObjectLedgersNotReady>((await controller.StartDrainAllAsync().WaitAsync(Patience, token)).Value);

        // Join an eligible silo with ObjectLedgers configured: MigrateIfStranded + ControllerPlacementDirector move the grain.
        var eligible = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, token);
        await EveryResolverListsEveryNodeAsync(cluster);

        await EventuallyAsync(
            async () => await management.GetActivationAddress(controller, token) == eligible.SiloAddress,
            "the controller grain to migrate to the silo whose object-ledger gate passed");

        var started = await controller.StartDrainAllAsync().WaitAsync(Patience, token);
        _ = Assert.IsType<DrainAllStartOutcome.Started>(started.Value);
        _ = await controller.CancelDrainAllAsync().WaitAsync(Patience, token);
    }

    private async Task<InMemoryObjectStore> CloneArchiveWithoutObjectLedgerRecordAsync(CancellationToken cancellationToken)
    {
        var clone = new InMemoryObjectStore();
        await foreach (var entry in _state.Objects.ListAsync(_state.Prefix + "/", cancellationToken: cancellationToken))
        {
            if (entry.Name.Value.EndsWith("/" + ObjectLedgerStore.StoreRecordName.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (await _state.Objects.ReadVersionedAsync(entry.Name, cancellationToken) is { } read)
            {
                using (read.Content)
                {
                    _ = await clone.CreateAsync(entry.Name, read.Content, crc32c: null, cancellationToken);
                }
            }
        }

        return clone;
    }

    private async Task AssertJoiningSiloRefusedAsync<TException>(TestCluster cluster, InMemoryObjectStore overrideStore, CancellationToken cancellationToken)
        where TException : Exception
    {
        _state.OverrideObjects = overrideStore;
        _state.NextSiloDiskless = true;
        var rogueSilo = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, cancellationToken);
        _state.NextSiloDiskless = false;
        _state.OverrideObjects = null;

        try
        {
            var journals = Services(rogueSilo).GetRequiredService<LocalJournals>();
            var status = Services(rogueSilo).GetRequiredService<DurableStorageStatus>();
            var gate = Services(rogueSilo).GetRequiredService<SiloObjectLedgers>().Gate;

            await EventuallyAsync(() => Task.FromResult(journals.State == LocalJournals.JournalsState.Refused), $"the joining silo's Journals to be refused with {typeof(TException).Name}");
            Assert.Equal(LocalJournals.JournalsState.Refused, status.Journals);
            _ = await Assert.ThrowsAsync<TException>(() => gate.Passed.WaitAsync(Patience, cancellationToken));
        }
        finally
        {
            await cluster.StopSiloAsync(rogueSilo, cancellationToken).WaitAsync(Patience, cancellationToken);
        }
    }

    private async Task<TestCluster> DeployAsync(short initialSilos, CancellationToken cancellationToken)
    {
        var builder = new TestClusterBuilder(initialSilos);
        builder.Properties[TestKey] = _state.Id;
        _ = builder.AddSiloBuilderConfigurator<NodeConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync(cancellationToken).WaitAsync(Patience, cancellationToken);
        await EveryResolverListsEveryNodeAsync(_cluster);
        return _cluster;
    }

    private static void SetPlacement(TestCluster cluster, LedgerPlacement placement)
    {
        foreach (var silo in cluster.Silos)
        {
            Services(silo).GetRequiredService<ControlOptions>().LedgerPlacement = placement;
        }
    }

    private static async Task AppendRoundAsync(TestCluster cluster, string[] keys, Dictionary<string, List<byte>> expected, string prefix, CancellationToken cancellationToken)
    {
        var factory = cluster.GrainFactory;
        foreach (var key in keys)
        {
            var chunk = Encoding.UTF8.GetBytes(prefix + key + "|");
            var grain = factory.GetGrain<IJournaledChunksGrain>(key);
            _ = await grain.AppendAsync(chunk).WaitAsync(Patience, cancellationToken);
            expected[key].AddRange(chunk);
        }
    }

    private static async Task VerifyHistoryAsync(TestCluster cluster, string[] keys, Dictionary<string, List<byte>> expected, CancellationToken cancellationToken)
    {
        var factory = cluster.GrainFactory;
        foreach (var key in keys)
        {
            var grain = factory.GetGrain<IJournaledChunksGrain>(key);
            await grain.DeactivateAsync().WaitAsync(Patience, cancellationToken);
            var actual = await grain.ReadAsync().WaitAsync(Patience, cancellationToken);
            Assert.Equal(expected[key].ToArray(), actual);
        }
    }

    private async Task<LedgerMapValue> ServedAsync(TestCluster cluster, Func<LedgerMapValue, bool> predicate)
    {
        LedgerMapValue? latest = null;
        await EventuallyAsync(async () =>
        {
            var map = await ReadMapAsync();
            if (!predicate(map))
            {
                return false;
            }

            // Every open ledger must be served by its primary's live silo.
            var byNode = cluster.Silos
                .Select(static silo => Services(silo).GetRequiredService<NodeLedgers>().Runtime)
                .OfType<NodeRuntime>()
                .ToDictionary(static runtime => runtime.Controller.Node);
            foreach (var ledger in map.Ledgers.Where(static l => l.IsOpen))
            {
                if (!byNode.TryGetValue(ledger.Primary.Node, out var runtime) || runtime.Writers.TryGet(ledger.Id) is null)
                {
                    return false;
                }
            }

            latest = map;
            return true;
        }, "cluster map to satisfy predicate and serve every open ledger");

        return latest!;
    }

    private static async Task EveryResolverListsEveryNodeAsync(TestCluster cluster)
    {
        NodeRegistryResolver[] resolvers = [.. cluster.Silos.Select(static silo => Services(silo).GetRequiredService<NodeRegistryResolver>())];
        SiloAddress[] running = [.. cluster.Silos.Select(static silo => silo.SiloAddress)];
        await EventuallyAsync(
            () => Task.FromResult(resolvers.All(resolver => running.All(silo => resolver.Current?.FindBySilo(silo) is not null))),
            "every silo's registry view to name every running silo");
    }

    private async Task<NodeRegistryValue> ReadRegistryAsync() =>
        (await NodeRegistryCodec.ReadAsync(await _state.MetaStore.GetRegisterAsync(_state.Cluster, NodeRegistryValue.RegisterName, Token), Token)).Value;

    private async Task<LedgerMapValue> ReadMapAsync()
    {
        var register = await _state.MetaStore.GetRegisterAsync(_state.Cluster, LedgerMapRegister.Name, Token);
        if (await register.ReadAsync(Token) is not { } entry)
        {
            return LedgerMapValue.Empty;
        }

        using (entry)
        {
            return Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(entry.Value.ToArray()).Value);
        }
    }

    private static async Task<List<string>> ListNamesAsync(IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        List<string> names = [];
        await foreach (var entry in store.ListAsync(prefix, cancellationToken: cancellationToken))
        {
            names.Add(entry.Name.Value);
        }

        return names;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Patience;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
        }

        Assert.Fail($"Timed out waiting for {what} after {Patience}.");
    }

    private static IServiceProvider Services(SiloHandle silo) => ((InProcessSiloHandle)silo).SiloHost.Services;

    private static NodeId NodeOf(SiloHandle silo) => Services(silo).GetRequiredService<LocalNode>().Node;

    private sealed class TestState
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public string Prefix { get; } = "oltest";

        public string Roots { get; } = Path.Combine(Path.GetTempPath(), "clog-ol-silos-" + Guid.NewGuid().ToString("N"));

        public InMemoryMetaStore MetaStore { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        public InMemoryObjectStore? OverrideObjects { get; set; }

        public ManifestGateObjectStore? ManifestGate { get; set; }

        public bool NextSiloDiskless { get; set; }

        public bool NextSiloOmitObjectLedgers { get; set; }

        public string? NextSiloJournalFormatKey { get; set; }

        public ObjectLedgerLayout ObjectLedgerLayout { get; set; } = ObjectLedgerLayout.PerSlot;

        public LedgerPlacement InitialPlacement { get; set; } = LedgerPlacement.Nodes;

        public bool UseOrdinals { get; set; }
    }

    private sealed class ManifestGateObjectStore(InMemoryObjectStore inner) : IObjectStore, IAppendableObjectStore
    {
        private TaskCompletionSource? _hold;

        public void HoldManifestReplaces()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _hold, gate);
        }

        public void ReleaseManifestReplaces() =>
            Interlocked.Exchange(ref _hold, null)?.TrySetResult();

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(
            ObjectName name,
            ObjectVersion? expectedVersion,
            ArcBuffer content,
            uint? crc32c,
            CancellationToken cancellationToken = default)
        {
            if (name == ObjectName.Manifest || name.Value.EndsWith("/" + ObjectName.Manifest.Value, StringComparison.Ordinal))
            {
                if (Volatile.Read(ref _hold) is { } hold)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }
            }

            return await inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
        }

        public ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.CreateAppendableAsync(name, cancellationToken);

        public ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);

        public ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.SealAsync(name, cancellationToken);

        public ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
    }

    private static int OrdinalOf(string silo) =>
        silo == "Primary" ? 0 : int.Parse(silo["Secondary_".Length..], System.Globalization.CultureInfo.InvariantCulture);

    private sealed class NodeConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var silo = siloBuilder.Configuration["Orleans:Name"]!;
            var diskless = state.NextSiloDiskless;
            var omitObjectLedgers = state.NextSiloOmitObjectLedgers;
            var journalFormatKey = state.NextSiloJournalFormatKey;
            var root = "";
            if (!diskless)
            {
                root = Path.Combine(state.Roots, silo);
                _ = Directory.CreateDirectory(root);
            }

            var baseObjects = state.OverrideObjects ?? state.Objects;
            IObjectStore objectStore = state.ManifestGate is { } gate ? gate : baseObjects;
            IAppendableObjectStore appendableStore = state.ManifestGate is { } appendGate ? appendGate : baseObjects;
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(state.MetaStore);
            _ = siloBuilder.Services.AddSingleton(objectStore);
            _ = siloBuilder.Services.AddSingleton(appendableStore);

            _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });

            var control = new ControlOptions
            {
                LedgersPerNode = 1,
                SequencerWindow = 4,
                LedgerPlacement = state.InitialPlacement,
                TransitionTimeout = TimeSpan.FromSeconds(10),
                ControlInterval = TimeSpan.FromMilliseconds(200),
                BalanceInterval = TimeSpan.FromMilliseconds(200),
                MapRefreshInterval = TimeSpan.FromSeconds(2),
                ReplicaSweepInterval = TimeSpan.FromMilliseconds(500),
                ManifestCommitInterval = TimeSpan.Zero,
                SweepInterval = TimeSpan.FromMilliseconds(200),
            };

            var node = new DurableStorageOptions
            {
                Cluster = state.Cluster,
                Root = root,
                Ordinal = state.UseOrdinals && !diskless ? OrdinalOf(silo) : null,
                WalFileSize = 1 << 20,
                PreallocateWalFiles = false,
                ArchiveStore = ArchiveStoreKind.Lsm,
                ArchiveStorePrefix = state.Prefix,
                MinimumFreeDiskBytes = 0,
                ObjectLedgers = omitObjectLedgers
                    ? null
                    : new ObjectLedgerStoreOptions
                    {
                        Layout = state.ObjectLedgerLayout,
                        CacheBytes = 1 << 20,
                    },
            };
            var siloOptions = new DurableStorageSiloOptions { Node = node, Control = control };
            _ = siloBuilder.AddDurableJournalStorageWith(siloOptions, providerName: null);
            if (journalFormatKey is not null)
            {
                _ = siloBuilder.Services.Configure<global::Orleans.Journaling.JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
            }
            _ = siloBuilder.Services.Configure<global::Orleans.Journaling.Json.JsonJournalOptions>(static options => options.AddTypeInfoResolver(JournaledChunksJsonContext.Default));
        }
    }
}
