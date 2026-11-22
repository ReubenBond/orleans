using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A registration call is made again only after a failure which leaves its outcome unknown (orleans.md §5.2,
/// conventions.md §6): the registry's own unknown outcome, an Orleans timeout or unavailability, a deactivating controller,
/// or a cancellation the silo did not ask for. Every other failure is definite and stops the silo. The options name a node
/// for replacement only as an identity, at the silo's ordinal.
/// </summary>
[NoPageLeakCheck("The classification rents no pages.")]
public sealed class DurableStorageNodeTests
{
    public static TheoryData<Exception, bool> Failures => new()
    {
        { new NodeRegistryUnavailableException("The MetaStore is unavailable.", new IOException()), true },
        { new TimeoutException(), true },
        { new SiloUnavailableException(), true },

        // Orleans constructs its message rejections internally.
        { (Exception)RuntimeHelpers.GetUninitializedObject(typeof(OrleansMessageRejectionException)), true },
        { new ObjectDisposedException("controller"), true },
        { new OperationCanceledException(), true },
        { new InvalidOperationException("A defect."), false },
        { new InvalidDataException("A corrupt registry."), false },
        { new ArgumentException("A bad entry."), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void OnlyAFailureWithAnUnknownOutcomeIsRetried(Exception failure, bool unknown) =>
        Assert.Equal(unknown, DurableStorageNode.IsUnknownOutcome(failure, CancellationToken.None));

    [Fact]
    public void ACancellationTheSiloAskedForEndsTheRegistration() =>
        Assert.False(DurableStorageNode.IsUnknownOutcome(new OperationCanceledException(), new CancellationToken(canceled: true)));

    /// <summary>
    /// A silo whose start stops before its active stage, failed or cancelled, never registers: stopping either stage the
    /// node joined cancels its <see cref="LocalNode.Registered"/>, so nothing waits on it forever.
    /// </summary>
    [Theory]
    [InlineData(ServiceLifecycleStage.RuntimeInitialize)]
    [InlineData(ServiceLifecycleStage.Active)]
    public async Task StoppingBeforeRegistrationCancelsRegistered(int stage)
    {
        var lifecycle = new RecordingLifecycle();
        var local = new LocalNode();
        using var node = new DurableStorageNode(
            TestReplicaLimits.Options(),
            new ControlOptions(),
            local,
            TestReplicaLimits.Of(),
            new SiloArchive.Lsm(new InMemoryObjectStore(), new ArchiveStorePrefix("t")),
            SiloObjectLedgers.CreateUnconfigured(),
            Options.Create(new JournaledStateManagerOptions()),
            storage: null!,
            silo: null!,
            grains: null!,
            metaStore: null!,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableStorageNode>.Instance);
        node.Participate(lifecycle);

        await lifecycle.Observers[stage].OnStop(TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => local.Registered);
    }

    /// <summary>
    /// A node named for replacement is a node identity, and is named only with the silo's ordinal, since a node is replaced
    /// only at an ordinal (orleans.md §5.3).
    /// </summary>
    [Fact]
    public void ANodeToReplaceIsAnIdentityAtTheSilosOrdinal()
    {
        static DurableStorageOptions Options(int? ordinal, Guid? nodeToReplace) => new()
        {
            Cluster = Guid.NewGuid(),
            Root = Path.Combine(Path.GetTempPath(), "unused"),
            ArchiveStore = ArchiveStoreKind.Lsm,
            Ordinal = ordinal,
            NodeToReplace = nodeToReplace,
        };

        Options(ordinal: 0, NodeRegistrySamples.Node(1).Value).Validate();
        Options(ordinal: null, nodeToReplace: null).Validate();
        Assert.Equal(nameof(DurableStorageOptions.NodeToReplace), Assert.Throws<ArgumentException>(() => Options(ordinal: 0, Guid.Empty).Validate()).ParamName);
        Assert.Equal(nameof(DurableStorageOptions.NodeToReplace), Assert.Throws<ArgumentException>(() => Options(ordinal: null, NodeRegistrySamples.Node(1).Value).Validate()).ParamName);
    }

    /// <summary>
    /// orleans.md §6: the node root is a fully qualified path, whose volumes the node measures (§5.9); a relative one is
    /// refused.
    /// </summary>
    [Fact]
    public void ARelativeRootIsRefused()
    {
        var options = new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = "relative", ArchiveStore = ArchiveStoreKind.Lsm };

        Assert.Equal(nameof(DurableStorageOptions.Root), Assert.Throws<ArgumentException>(options.Validate).ParamName);
    }

    /// <summary>
    /// orleans.md §6, OR-I20: a silo selects its archive store's kind, the LSM or archive logs, and never its identity, which
    /// it takes from the node registry or its store's record; no kind and the memory store are refused.
    /// </summary>
    [Fact]
    public void TheArchiveStoreIsTheLsmOrArchiveLogs()
    {
        var options = new DurableStorageOptions { Cluster = Guid.NewGuid(), Root = Path.Combine(Path.GetTempPath(), "unused") };

        Assert.Equal(nameof(DurableStorageOptions.ArchiveStore), Assert.Throws<ArgumentException>(options.Validate).ParamName);
        options.ArchiveStore = ArchiveStoreKind.Memory;
        Assert.Equal(nameof(DurableStorageOptions.ArchiveStore), Assert.Throws<ArgumentException>(options.Validate).ParamName);
        options.ArchiveStore = ArchiveStoreKind.Lsm;
        options.Validate();
        options.ArchiveStore = ArchiveStoreKind.Logs;
        options.Validate();
    }

    /// <summary>
    /// orleans.md §6: the silo's options bind from configuration, the archive store from its kind's name.
    /// </summary>
    [Fact]
    public void TheSilosOptionsBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DurableStorage:Node:Cluster"] = "6f1c2a7e-0b9d-4f4e-9a51-3c2d1e0f9a8b",
            ["DurableStorage:Node:Root"] = Path.Combine(Path.GetTempPath(), "bound"),
            ["DurableStorage:Node:Ordinal"] = "2",
            ["DurableStorage:Node:ArchiveStore"] = "Logs",
            ["DurableStorage:Node:ArchiveLogs:DrainConcurrency"] = "16",
            ["DurableStorage:Control:ArchiveLogRequestsPerSecond"] = "2000",
        }).Build();

        var options = configuration.GetSection("DurableStorage").Get<DurableStorageSiloOptions>()!;

        options.Validate();
        Assert.Equal((2, ArchiveStoreKind.Logs, 16), (options.Node.Ordinal, options.Node.ArchiveStore, options.Node.ArchiveLogs.DrainConcurrency));
        Assert.Equal(2000, options.Control.ArchiveLogRequestsPerSecond);
    }

    /// <summary>A silo lifecycle which records the observer the node subscribes at each stage.</summary>
    private sealed class RecordingLifecycle : ISiloLifecycle
    {
        public Dictionary<int, ILifecycleObserver> Observers { get; } = [];

        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => 0;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Observers.Add(stage, observer);
            return new NoSubscription();
        }

        private sealed class NoSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
