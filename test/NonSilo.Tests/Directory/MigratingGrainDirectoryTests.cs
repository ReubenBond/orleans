#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Internal;
using UnitTests.Directory;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for MigratingGrainDirectory behavior during rolling upgrades.
/// </summary>
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class MigratingGrainDirectoryTests : IDisposable
{
    private static readonly SiloAddress Silo1 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Silo2 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 22222), 2);
    private static readonly SiloAddress Silo3 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 33333), 3);

    private readonly MockClusterMembershipService _membershipService;
    private readonly MockClusterManifestProvider _manifestProvider;
    private readonly DirectoryMembershipService _directoryMembershipService;
    private readonly ILocalGrainDirectory _dhtDirectory;
    private readonly IGrainDirectory _distributedDirectory;
    private readonly MigratingGrainDirectory _migratingDirectory;

    public MigratingGrainDirectoryTests()
    {
        _membershipService = new MockClusterMembershipService();
        _manifestProvider = new MockClusterManifestProvider();
        _dhtDirectory = Substitute.For<ILocalGrainDirectory>();
        _distributedDirectory = Substitute.For<IGrainDirectory>();

        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var logger = Substitute.For<ILogger<DirectoryMembershipService>>();
        _directoryMembershipService = new DirectoryMembershipService(
            _membershipService,
            grainFactory,
            logger,
            _manifestProvider);

        var migratingLogger = Substitute.For<ILogger<MigratingGrainDirectory>>();
        _migratingDirectory = new MigratingGrainDirectory(
            _distributedDirectory,
            _dhtDirectory,
            _directoryMembershipService,
            migratingLogger);
    }

    public void Dispose()
    {
        _migratingDirectory.Dispose();
        _ = _directoryMembershipService.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Creates a GrainManifest with optional distributed grain directory capability.
    /// </summary>
    private static GrainManifest CreateManifest(bool hasDistributedCapability)
    {
        var properties = hasDistributedCapability
            ? ImmutableDictionary<string, string>.Empty.Add(
                GrainDirectoryCapability.MetadataKey,
                GrainDirectoryCapability.Distributed)
            : ImmutableDictionary<string, string>.Empty;

        return new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty,
            properties);
    }

    /// <summary>
    /// Sets up a mixed cluster where Silo1 is OLD (no capability) and Silo2 is NEW (has capability).
    /// The dhtOwner specifies which silo should be returned as the DHT owner for the grainId.
    /// </summary>
    private async Task SetupMixedCluster(SiloAddress dhtOwner, GrainId grainId)
    {
        // Silo1 is OLD (no capability), Silo2 is NEW (has capability)
        _membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        _membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");

        // Update manifest - Silo1 has no capability, Silo2 has capability
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo1, CreateManifest(hasDistributedCapability: false))
            .Add(Silo2, CreateManifest(hasDistributedCapability: true));
        _manifestProvider.UpdateManifest(siloManifests);

        // Wait for DirectoryMembershipService to process the updates
        await WaitForCondition(() => 
            _directoryMembershipService.CurrentView.Members.Length == 1 &&
            _directoryMembershipService.CurrentView.Members.Contains(Silo2),
            TimeSpan.FromSeconds(5));

        // Setup DHT to return the specified owner
        _dhtDirectory.GetPrimaryForGrain(grainId).Returns(dhtOwner);
    }

    /// <summary>
    /// Sets up a cluster where all silos are NEW (have distributed capability).
    /// </summary>
    private async Task SetupAllNewCluster(SiloAddress dhtOwner, GrainId grainId)
    {
        // Both Silo2 and Silo3 are NEW (have capability)
        _membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        _membershipService.UpdateSiloStatus(Silo3, SiloStatus.Active, "silo3");

        // Update manifest - both silos have capability
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo2, CreateManifest(hasDistributedCapability: true))
            .Add(Silo3, CreateManifest(hasDistributedCapability: true));
        _manifestProvider.UpdateManifest(siloManifests);

        // Wait for DirectoryMembershipService to process the updates
        await WaitForCondition(() => 
            _directoryMembershipService.CurrentView.Members.Length == 2,
            TimeSpan.FromSeconds(5));

        // Setup DHT to return the specified owner
        _dhtDirectory.GetPrimaryForGrain(grainId).Returns(dhtOwner);
    }

    /// <summary>
    /// When DHT owner is an OLD silo, MigratingGrainDirectory should forward Register to DHT.
    /// </summary>
    [Fact]
    public async Task Register_DhtOwnerIsOldSilo_ForwardsToDht()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var address = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        
        await SetupMixedCluster(dhtOwner: Silo1, grainId); // Silo1 is OLD
        
        var expectedResult = new AddressAndTag(address, 1);
        _dhtDirectory.RegisterAsync(address, Arg.Any<int>()).Returns(Task.FromResult(expectedResult));

        // Act
        var result = await _migratingDirectory.Register(address);

        // Assert: Should have called DHT, not DistributedGrainDirectory
        await _dhtDirectory.Received(1).RegisterAsync(address, 0);
        await _distributedDirectory.DidNotReceive().Register(Arg.Any<GrainAddress>());
        Assert.Equal(address, result);
    }

    /// <summary>
    /// When DHT owner is a NEW silo, MigratingGrainDirectory should use DistributedGrainDirectory.
    /// </summary>
    [Fact]
    public async Task Register_DhtOwnerIsNewSilo_UsesDistributedDirectory()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var address = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        
        await SetupAllNewCluster(dhtOwner: Silo2, grainId); // Silo2 is NEW
        
        _distributedDirectory.Register(address).Returns(Task.FromResult<GrainAddress?>(address));

        // Act
        var result = await _migratingDirectory.Register(address);

        // Assert: Should have called DistributedGrainDirectory, not DHT
        await _distributedDirectory.Received(1).Register(address);
        await _dhtDirectory.DidNotReceive().RegisterAsync(Arg.Any<GrainAddress>(), Arg.Any<int>());
        Assert.Equal(address, result);
    }

    /// <summary>
    /// When DHT owner is an OLD silo, MigratingGrainDirectory should forward Lookup to DHT.
    /// </summary>
    [Fact]
    public async Task Lookup_DhtOwnerIsOldSilo_ForwardsToDht()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var expectedAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo1, ActivationId = ActivationId.NewId() };
        
        await SetupMixedCluster(dhtOwner: Silo1, grainId); // Silo1 is OLD
        
        _dhtDirectory.LookupAsync(grainId, Arg.Any<int>()).Returns(Task.FromResult(new AddressAndTag(expectedAddress, 1)));

        // Act
        var result = await _migratingDirectory.Lookup(grainId);

        // Assert: Should have called DHT
        await _dhtDirectory.Received(1).LookupAsync(grainId, 0);
        await _distributedDirectory.DidNotReceive().Lookup(Arg.Any<GrainId>());
        Assert.Equal(expectedAddress, result);
    }

    /// <summary>
    /// When DHT owner is a NEW silo, MigratingGrainDirectory should use DistributedGrainDirectory for Lookup.
    /// </summary>
    [Fact]
    public async Task Lookup_DhtOwnerIsNewSilo_UsesDistributedDirectory()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var expectedAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        
        await SetupAllNewCluster(dhtOwner: Silo2, grainId); // Silo2 is NEW
        
        _distributedDirectory.Lookup(grainId).Returns(Task.FromResult<GrainAddress?>(expectedAddress));

        // Act
        var result = await _migratingDirectory.Lookup(grainId);

        // Assert: Should have called DistributedGrainDirectory
        await _distributedDirectory.Received(1).Lookup(grainId);
        await _dhtDirectory.DidNotReceive().LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>());
        Assert.Equal(expectedAddress, result);
    }

    /// <summary>
    /// When DHT owner is an OLD silo, MigratingGrainDirectory should forward Unregister to DHT.
    /// </summary>
    [Fact]
    public async Task Unregister_DhtOwnerIsOldSilo_ForwardsToDht()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var address = new GrainAddress { GrainId = grainId, SiloAddress = Silo1, ActivationId = ActivationId.NewId() };
        
        await SetupMixedCluster(dhtOwner: Silo1, grainId); // Silo1 is OLD

        // Act
        await _migratingDirectory.Unregister(address);

        // Assert: Should have called DHT
        await _dhtDirectory.Received(1).UnregisterAsync(address, UnregistrationCause.Force, 0);
        await _distributedDirectory.DidNotReceive().Unregister(Arg.Any<GrainAddress>());
    }

    /// <summary>
    /// When DHT owner is a NEW silo, MigratingGrainDirectory should use DistributedGrainDirectory for Unregister.
    /// </summary>
    [Fact]
    public async Task Unregister_DhtOwnerIsNewSilo_UsesDistributedDirectory()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var address = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        
        await SetupAllNewCluster(dhtOwner: Silo2, grainId); // Silo2 is NEW

        // Act
        await _migratingDirectory.Unregister(address);

        // Assert: Should have called DistributedGrainDirectory
        await _distributedDirectory.Received(1).Unregister(address);
        await _dhtDirectory.DidNotReceive().UnregisterAsync(Arg.Any<GrainAddress>(), Arg.Any<UnregistrationCause>(), Arg.Any<int>());
    }

    /// <summary>
    /// When DHT returns null owner (e.g., shutting down), should fall back to DistributedGrainDirectory.
    /// </summary>
    [Fact]
    public async Task Lookup_DhtOwnerIsNull_UsesDistributedDirectory()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var expectedAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        
        // Setup cluster but DHT returns null owner
        await SetupAllNewCluster(dhtOwner: Silo2, grainId);
        _dhtDirectory.GetPrimaryForGrain(grainId).Returns((SiloAddress?)null);
        
        _distributedDirectory.Lookup(grainId).Returns(Task.FromResult<GrainAddress?>(expectedAddress));

        // Act
        var result = await _migratingDirectory.Lookup(grainId);

        // Assert: Should have called DistributedGrainDirectory
        await _distributedDirectory.Received(1).Lookup(grainId);
        Assert.Equal(expectedAddress, result);
    }

    /// <summary>
    /// Register with previousAddress should also forward to DHT when owner is OLD.
    /// </summary>
    [Fact]
    public async Task RegisterWithPrevious_DhtOwnerIsOldSilo_ForwardsToDht()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var newAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        var previousAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo1, ActivationId = ActivationId.NewId() };
        
        await SetupMixedCluster(dhtOwner: Silo1, grainId); // Silo1 is OLD
        
        var expectedResult = new AddressAndTag(newAddress, 1);
        _dhtDirectory.RegisterAsync(newAddress, previousAddress, Arg.Any<int>()).Returns(Task.FromResult(expectedResult));

        // Act
        var result = await _migratingDirectory.Register(newAddress, previousAddress);

        // Assert: Should have called DHT with previousAddress
        await _dhtDirectory.Received(1).RegisterAsync(newAddress, previousAddress, 0);
        await _distributedDirectory.DidNotReceive().Register(Arg.Any<GrainAddress>(), Arg.Any<GrainAddress?>());
        Assert.Equal(newAddress, result);
    }

    /// <summary>
    /// Register with previousAddress should use distributed directory when owner is NEW.
    /// </summary>
    [Fact]
    public async Task RegisterWithPrevious_DhtOwnerIsNewSilo_UsesDistributedDirectory()
    {
        // Arrange
        var grainId = GrainId.Create("test", "grain1");
        var newAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo2, ActivationId = ActivationId.NewId() };
        var previousAddress = new GrainAddress { GrainId = grainId, SiloAddress = Silo3, ActivationId = ActivationId.NewId() };
        
        await SetupAllNewCluster(dhtOwner: Silo2, grainId); // Silo2 is NEW
        
        _distributedDirectory.Register(newAddress, previousAddress).Returns(Task.FromResult<GrainAddress?>(newAddress));

        // Act
        var result = await _migratingDirectory.Register(newAddress, previousAddress);

        // Assert: Should have called DistributedGrainDirectory
        await _distributedDirectory.Received(1).Register(newAddress, previousAddress);
        await _dhtDirectory.DidNotReceive().RegisterAsync(Arg.Any<GrainAddress>(), Arg.Any<GrainAddress?>(), Arg.Any<int>());
        Assert.Equal(newAddress, result);
    }

    private static async Task WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not met within {timeout}");
    }

    /// <summary>
    /// Mock IClusterManifestProvider for testing.
    /// </summary>
    private sealed class MockClusterManifestProvider : IClusterManifestProvider
    {
        private readonly Orleans.Runtime.Utilities.AsyncEnumerable<ClusterManifest> _updates;
        private ClusterManifest _current;
        private int _version = 0;

        public MockClusterManifestProvider()
        {
            _current = new ClusterManifest(new MajorMinorVersion(0, 0), ImmutableDictionary<SiloAddress, GrainManifest>.Empty);
            _updates = new Orleans.Runtime.Utilities.AsyncEnumerable<ClusterManifest>(
                initialValue: _current,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Interlocked.Exchange(ref _current, update));
        }

        public ClusterManifest Current => _current;

        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        public GrainManifest LocalGrainManifest => new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        public void UpdateManifest(ImmutableDictionary<SiloAddress, GrainManifest> siloManifests)
        {
            var newVersion = new MajorMinorVersion(++_version, 0);
            _updates.TryPublish(new ClusterManifest(newVersion, siloManifests));
        }
    }
}
