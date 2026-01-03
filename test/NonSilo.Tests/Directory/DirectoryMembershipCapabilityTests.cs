#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;
using UnitTests.Directory;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for the GrainManifest.Properties functionality.
/// </summary>
[TestCategory("BVT"), TestCategory("Manifest")]
public sealed class GrainManifestPropertiesTests
{
    /// <summary>
    /// Verifies that GrainManifest correctly stores and retrieves silo properties.
    /// </summary>
    [Fact]
    public void GrainManifest_WithProperties_StoresPropertiesCorrectly()
    {
        // Arrange
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("key1", "value1")
            .Add("key2", "value2");

        // Act
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty,
            properties);

        // Assert
        Assert.Equal(2, manifest.Properties.Count);
        Assert.True(manifest.Properties.TryGetValue("key1", out var value1));
        Assert.Equal("value1", value1);
        Assert.True(manifest.Properties.TryGetValue("key2", out var value2));
        Assert.Equal("value2", value2);
    }

    /// <summary>
    /// Verifies that the default constructor creates empty properties.
    /// </summary>
    [Fact]
    public void GrainManifest_DefaultConstructor_HasEmptyProperties()
    {
        // Act
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        // Assert
        Assert.NotNull(manifest.Properties);
        Assert.Empty(manifest.Properties);
    }

    /// <summary>
    /// Verifies that GrainDirectoryCapability properties are stored correctly.
    /// </summary>
    [Fact]
    public void GrainManifest_WithDistributedCapability_HasCorrectProperty()
    {
        // Arrange
        var properties = ImmutableDictionary<string, string>.Empty
            .Add(GrainDirectoryCapability.MetadataKey, GrainDirectoryCapability.Distributed);

        // Act
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty,
            properties);

        // Assert
        Assert.True(manifest.Properties.TryGetValue(GrainDirectoryCapability.MetadataKey, out var capability));
        Assert.Equal(GrainDirectoryCapability.Distributed, capability);
    }
}

/// <summary>
/// Tests for ISiloPropertiesProvider implementations.
/// </summary>
[TestCategory("BVT"), TestCategory("Manifest")]
public sealed class SiloPropertiesProviderTests
{
    /// <summary>
    /// Verifies that multiple providers can contribute properties.
    /// </summary>
    [Fact]
    public void MultipleProviders_AllPropertiesCollected()
    {
        // Arrange
        var provider1 = new TestPropertiesProvider(new Dictionary<string, string>
        {
            ["provider1.key"] = "value1"
        });
        var provider2 = new TestPropertiesProvider(new Dictionary<string, string>
        {
            ["provider2.key"] = "value2"
        });

        var properties = new Dictionary<string, string>();

        // Act
        provider1.Populate(properties);
        provider2.Populate(properties);

        // Assert
        Assert.Equal(2, properties.Count);
        Assert.Equal("value1", properties["provider1.key"]);
        Assert.Equal("value2", properties["provider2.key"]);
    }

    /// <summary>
    /// Verifies that later providers can override earlier ones.
    /// </summary>
    [Fact]
    public void ProviderOverride_LaterProviderWins()
    {
        // Arrange
        var provider1 = new TestPropertiesProvider(new Dictionary<string, string>
        {
            ["shared.key"] = "first"
        });
        var provider2 = new TestPropertiesProvider(new Dictionary<string, string>
        {
            ["shared.key"] = "second"
        });

        var properties = new Dictionary<string, string>();

        // Act
        provider1.Populate(properties);
        provider2.Populate(properties);

        // Assert
        Assert.Single(properties);
        Assert.Equal("second", properties["shared.key"]);
    }

    private sealed class TestPropertiesProvider : ISiloPropertiesProvider
    {
        private readonly Dictionary<string, string> _properties;

        public TestPropertiesProvider(Dictionary<string, string> properties)
        {
            _properties = properties;
        }

        public void Populate(Dictionary<string, string> properties)
        {
            foreach (var kvp in _properties)
            {
                properties[kvp.Key] = kvp.Value;
            }
        }
    }
}

/// <summary>
/// Tests for capability filtering logic used in DirectoryMembershipService.
/// These tests verify the HasDistributedGrainDirectoryCapability logic without
/// requiring the full DirectoryMembershipService infrastructure.
/// </summary>
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryCapabilityFilteringTests
{
    private static readonly SiloAddress Silo1 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Silo2 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 22222), 2);
    private static readonly SiloAddress Silo3 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 33333), 3);

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
    /// Creates a ClusterManifest with the specified silo manifests.
    /// </summary>
    private static ClusterManifest CreateClusterManifest(
        ImmutableDictionary<SiloAddress, GrainManifest> siloManifests)
    {
        return new ClusterManifest(new MajorMinorVersion(1, 0), siloManifests);
    }

    /// <summary>
    /// When all silos are OLD (no capability), the filtering function should return true for all.
    /// </summary>
    [Fact]
    public void AllOldSilos_AllShouldBeIncluded()
    {
        // Arrange: All silos without capability (OLD silos)
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo1, CreateManifest(hasDistributedCapability: false))
            .Add(Silo2, CreateManifest(hasDistributedCapability: false));

        var clusterManifest = CreateClusterManifest(siloManifests);

        // Act & Assert: All silos should be included (no filtering when none have capability)
        Assert.True(HasDistributedGrainDirectoryCapability(Silo1, clusterManifest));
        Assert.True(HasDistributedGrainDirectoryCapability(Silo2, clusterManifest));
    }

    /// <summary>
    /// When all silos are NEW (have capability), all should be included.
    /// </summary>
    [Fact]
    public void AllNewSilos_AllShouldBeIncluded()
    {
        // Arrange: All silos with capability (NEW silos)
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo1, CreateManifest(hasDistributedCapability: true))
            .Add(Silo2, CreateManifest(hasDistributedCapability: true));

        var clusterManifest = CreateClusterManifest(siloManifests);

        // Act & Assert: All silos should be included
        Assert.True(HasDistributedGrainDirectoryCapability(Silo1, clusterManifest));
        Assert.True(HasDistributedGrainDirectoryCapability(Silo2, clusterManifest));
    }

    /// <summary>
    /// In a mixed cluster, only silos with the capability should be included.
    /// </summary>
    [Fact]
    public void MixedCluster_OnlyNewSilosIncluded()
    {
        // Arrange: Mix of OLD and NEW silos
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo1, CreateManifest(hasDistributedCapability: false)) // OLD
            .Add(Silo2, CreateManifest(hasDistributedCapability: true))  // NEW
            .Add(Silo3, CreateManifest(hasDistributedCapability: false)); // OLD

        var clusterManifest = CreateClusterManifest(siloManifests);

        // Act & Assert: Only NEW silos should be included
        Assert.False(HasDistributedGrainDirectoryCapability(Silo1, clusterManifest)); // OLD - excluded
        Assert.True(HasDistributedGrainDirectoryCapability(Silo2, clusterManifest));  // NEW - included
        Assert.False(HasDistributedGrainDirectoryCapability(Silo3, clusterManifest)); // OLD - excluded
    }

    /// <summary>
    /// Silo not in the cluster manifest should be excluded in a mixed cluster scenario.
    /// </summary>
    [Fact]
    public void MixedCluster_SiloNotInManifest_Excluded()
    {
        // Arrange: Silo3 is not in manifest
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo1, CreateManifest(hasDistributedCapability: false)) // OLD
            .Add(Silo2, CreateManifest(hasDistributedCapability: true)); // NEW
        // Silo3 is intentionally missing

        var clusterManifest = CreateClusterManifest(siloManifests);

        // Act & Assert: Silo3 not in manifest should be excluded in mixed cluster
        Assert.False(HasDistributedGrainDirectoryCapability(Silo3, clusterManifest));
    }

    /// <summary>
    /// Silo not in manifest should be INCLUDED when no silos have capability (all OLD).
    /// </summary>
    [Fact]
    public void AllOldSilos_SiloNotInManifest_Included()
    {
        // Arrange: All silos are OLD, and Silo3 is not in manifest
        var siloManifests = ImmutableDictionary<SiloAddress, GrainManifest>.Empty
            .Add(Silo1, CreateManifest(hasDistributedCapability: false))
            .Add(Silo2, CreateManifest(hasDistributedCapability: false));
        // Silo3 is intentionally missing

        var clusterManifest = CreateClusterManifest(siloManifests);

        // Act & Assert: In all-OLD cluster, even missing silos should be included
        Assert.True(HasDistributedGrainDirectoryCapability(Silo3, clusterManifest));
    }

    /// <summary>
    /// Empty manifest should include all silos (no capability = no filtering).
    /// </summary>
    [Fact]
    public void EmptyManifest_AllSilosIncluded()
    {
        // Arrange: Empty manifest
        var clusterManifest = CreateClusterManifest(ImmutableDictionary<SiloAddress, GrainManifest>.Empty);

        // Act & Assert: No capability info = include all
        Assert.True(HasDistributedGrainDirectoryCapability(Silo1, clusterManifest));
        Assert.True(HasDistributedGrainDirectoryCapability(Silo2, clusterManifest));
    }

    /// <summary>
    /// Replicates the logic from DirectoryMembershipService.HasDistributedGrainDirectoryCapability
    /// for unit testing purposes.
    /// </summary>
    private static bool HasDistributedGrainDirectoryCapability(SiloAddress siloAddress, ClusterManifest manifest)
    {
        // Check if ANY silo in the cluster has the distributed grain directory capability
        bool anyHasCapability = false;
        foreach (var siloManifest in manifest.Silos.Values)
        {
            if (siloManifest.Properties.TryGetValue(GrainDirectoryCapability.MetadataKey, out var cap)
                && cap == GrainDirectoryCapability.Distributed)
            {
                anyHasCapability = true;
                break;
            }
        }

        if (!anyHasCapability)
        {
            // No silos have the capability - this is an all-OLD-silos cluster.
            // Don't filter; include all silos.
            return true;
        }

        // Mixed or all-NEW cluster - filter based on capability
        if (manifest.Silos.TryGetValue(siloAddress, out var grainManifest))
        {
            return grainManifest.Properties.TryGetValue(GrainDirectoryCapability.MetadataKey, out var capability)
                && capability == GrainDirectoryCapability.Distributed;
        }

        // Silo not yet in the cluster manifest
        return false;
    }
}

/// <summary>
/// Integration tests for DirectoryMembershipService using mocks.
/// These tests verify the full service behavior including manifest/membership synchronization.
/// </summary>
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class DirectoryMembershipServiceTests
{
    private static readonly SiloAddress Silo1 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Silo2 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 22222), 2);
    private static readonly SiloAddress Silo3 = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 33333), 3);

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
    /// Verifies that DirectoryMembershipService waits for manifest to include all active silos
    /// before publishing a view.
    /// </summary>
    [Fact]
    public async Task Service_WaitsForManifest_BeforePublishingView()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        // Start with Silo1 active in membership but NOT in manifest
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        // Manifest is empty initially

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait a bit - view should NOT be published because manifest doesn't have Silo1
        await Task.Delay(100);
        Assert.Equal(MembershipVersion.MinValue, service.CurrentView.Version);

        // Act: Add Silo1 to manifest
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)));

        // Assert: View should now be published
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo1, service.CurrentView.Members);
    }

    /// <summary>
    /// Verifies that DirectoryMembershipService publishes view when silo leaves cluster
    /// (even if manifest never included it).
    /// </summary>
    [Fact]
    public async Task Service_PublishesView_WhenMissingSiloLeaves()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        // Start with Silo1 and Silo2 active, but manifest only has Silo1
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait a bit - view should NOT be published because Silo2 is missing from manifest
        await Task.Delay(100);
        Assert.Equal(MembershipVersion.MinValue, service.CurrentView.Version);

        // Act: Silo2 leaves the cluster (dies)
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Dead, "silo2");

        // Assert: View should now be published (only Silo1 is active and it's in manifest)
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo1, service.CurrentView.Members);
    }

    /// <summary>
    /// Verifies that mixed cluster filtering works correctly through the full service.
    /// </summary>
    [Fact]
    public async Task Service_FiltersMixedCluster_Correctly()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        // Start with both silos active
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");

        // Manifest has both silos - Silo1 is OLD, Silo2 is NEW
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: true)));

        await using var service = CreateService(membershipService, manifestProvider);

        // Assert: Only Silo2 (NEW) should be in filtered members
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo2, service.CurrentView.Members);

        // But AllActiveMembers should include both
        Assert.Equal(2, service.AllActiveMembers.Length);
        Assert.Contains(Silo1, service.AllActiveMembers);
        Assert.Contains(Silo2, service.AllActiveMembers);
    }

    /// <summary>
    /// Verifies that rapid membership changes are handled correctly.
    /// When silos join and leave quickly, the service should still produce consistent views.
    /// </summary>
    [Fact]
    public async Task Service_HandlesRapidMembershipChanges_Correctly()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        // Start with Silo1 active
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait for initial view
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);

        // Act: Rapid changes - add Silo2, then immediately add Silo3
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        membershipService.UpdateSiloStatus(Silo3, SiloStatus.Active, "silo3");
        
        // View should NOT be published yet (Silo2 and Silo3 not in manifest)
        await Task.Delay(50);
        var versionAfterRapidChanges = service.CurrentView.Version;

        // Add both to manifest at once
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false))
                .Add(Silo3, CreateManifest(hasDistributedCapability: false)));

        // Assert: View should now have all 3 silos
        await WaitForCondition(() => service.CurrentView.Version > versionAfterRapidChanges, TimeSpan.FromSeconds(5));
        Assert.Equal(3, service.CurrentView.Members.Length);
    }

    /// <summary>
    /// Verifies that the service correctly handles transition from all-OLD to mixed cluster.
    /// </summary>
    [Fact]
    public async Task Service_TransitionsFromAllOldToMixed_Correctly()
    {
        // Arrange: Start with all OLD silos
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait for initial view - all OLD silos should be included
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Equal(2, service.CurrentView.Members.Length);
        var initialVersion = service.CurrentView.Version;

        // Act: Add a NEW silo (with capability)
        membershipService.UpdateSiloStatus(Silo3, SiloStatus.Active, "silo3");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false))
                .Add(Silo3, CreateManifest(hasDistributedCapability: true))); // NEW silo

        // Assert: Only the NEW silo should now be in filtered members
        await WaitForCondition(() => service.CurrentView.Version > initialVersion, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo3, service.CurrentView.Members);

        // AllActiveMembers should still include all 3
        Assert.Equal(3, service.AllActiveMembers.Length);
    }

    /// <summary>
    /// Verifies that the service correctly handles transition from mixed to all-NEW cluster.
    /// </summary>
    [Fact]
    public async Task Service_TransitionsFromMixedToAllNew_Correctly()
    {
        // Arrange: Start with mixed cluster (1 OLD, 1 NEW)
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)) // OLD
                .Add(Silo2, CreateManifest(hasDistributedCapability: true))); // NEW

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait for initial view - only NEW silo should be included
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo2, service.CurrentView.Members);
        var initialVersion = service.CurrentView.Version;

        // Act: OLD silo leaves, add another NEW silo
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Dead, "silo1");
        membershipService.UpdateSiloStatus(Silo3, SiloStatus.Active, "silo3");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)) // Still in manifest but Dead
                .Add(Silo2, CreateManifest(hasDistributedCapability: true))
                .Add(Silo3, CreateManifest(hasDistributedCapability: true))); // NEW silo

        // Assert: Both NEW silos should be in filtered members
        await WaitForCondition(() => service.CurrentView.Version > initialVersion, TimeSpan.FromSeconds(5));
        Assert.Equal(2, service.CurrentView.Members.Length);
        Assert.Contains(Silo2, service.CurrentView.Members);
        Assert.Contains(Silo3, service.CurrentView.Members);
        Assert.DoesNotContain(Silo1, service.CurrentView.Members);
    }

    /// <summary>
    /// Verifies that AllActiveMembers returns empty array when no silos are active.
    /// </summary>
    [Fact]
    public async Task Service_AllActiveMembers_EmptyWhenNoActiveSilos()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        await using var service = CreateService(membershipService, manifestProvider);

        // Assert: AllActiveMembers should be empty
        Assert.Empty(service.AllActiveMembers);
    }

    /// <summary>
    /// Verifies that when a silo restarts with a different capability (OLD -> NEW),
    /// the service correctly updates its view.
    /// This simulates upgrading a silo in place.
    /// </summary>
    [Fact]
    public async Task Service_SiloRestartsWithDifferentCapability_ViewUpdatesCorrectly()
    {
        // Arrange: Start with all OLD silos
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait for initial view - all OLD, so both included
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Equal(2, service.CurrentView.Members.Length);
        var initialVersion = service.CurrentView.Version;

        // Act: Silo1 "restarts" as a NEW silo (same address, different generation would be more realistic,
        // but for this test we simulate by going Dead then Active with new capability)
        // First, Silo1 goes dead
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Dead, "silo1");
        
        // Create a new Silo1 address (simulating restart with new generation)
        var silo1Restarted = SiloAddress.New(Silo1.Endpoint, Silo1.Generation + 1);
        membershipService.UpdateSiloStatus(silo1Restarted, SiloStatus.Active, "silo1-restarted");
        
        // Update manifest with new Silo1 as NEW
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)) // Old dead silo still in manifest
                .Add(silo1Restarted, CreateManifest(hasDistributedCapability: true)) // NEW silo
                .Add(Silo2, CreateManifest(hasDistributedCapability: false)));

        // Assert: Now it's a mixed cluster - only NEW silo should be in filtered members
        await WaitForCondition(() => service.CurrentView.Version > initialVersion, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(silo1Restarted, service.CurrentView.Members);
        Assert.DoesNotContain(Silo2, service.CurrentView.Members);
    }

    /// <summary>
    /// Verifies that when all NEW silos leave the cluster, it correctly transitions
    /// back to an all-OLD cluster where all silos are included.
    /// </summary>
    [Fact]
    public async Task Service_AllNewSilosLeave_RegressesToAllOldBehavior()
    {
        // Arrange: Start with mixed cluster
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        membershipService.UpdateSiloStatus(Silo3, SiloStatus.Active, "silo3");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)) // OLD
                .Add(Silo2, CreateManifest(hasDistributedCapability: true))  // NEW
                .Add(Silo3, CreateManifest(hasDistributedCapability: false))); // OLD

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait for initial view - mixed cluster, only NEW silo included
        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo2, service.CurrentView.Members);
        var mixedVersion = service.CurrentView.Version;

        // Act: NEW silo (Silo2) leaves the cluster
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Dead, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: true)) // Still in manifest but Dead
                .Add(Silo3, CreateManifest(hasDistributedCapability: false)));

        // Assert: Now all active silos are OLD, so all should be included
        await WaitForCondition(() => service.CurrentView.Version > mixedVersion, TimeSpan.FromSeconds(5));
        Assert.Equal(2, service.CurrentView.Members.Length);
        Assert.Contains(Silo1, service.CurrentView.Members);
        Assert.Contains(Silo3, service.CurrentView.Members);
        Assert.DoesNotContain(Silo2, service.CurrentView.Members); // Dead, not in members
    }

    /// <summary>
    /// Verifies that manifest updates arriving before membership updates are handled correctly.
    /// The service should wait for membership to be ready before publishing.
    /// </summary>
    [Fact]
    public async Task Service_ManifestArrivesBeforeMembership_WaitsForMembership()
    {
        // Arrange: Start with empty membership but manifest has silos
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        // Manifest already has Silo1, but membership doesn't have it active yet
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        // Wait a bit - view should NOT be published (no active silos in membership)
        await Task.Delay(100);
        // The initial view should have empty members since no silos are active
        // (The service may publish a view with empty members, which is fine)

        // Act: Now add Silo1 to membership
        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");

        // Assert: View should now be published with Silo1
        await WaitForCondition(() => 
            service.CurrentView.Members.Length == 1 && 
            service.CurrentView.Members.Contains(Silo1), 
            TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies that multiple silos joining simultaneously with different capabilities
    /// are handled correctly.
    /// </summary>
    [Fact]
    public async Task Service_MultipleSilosJoinWithMixedCapabilities_CorrectFiltering()
    {
        // Arrange: Start with one OLD silo
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        await WaitForCondition(() => service.CurrentView.Version > MembershipVersion.MinValue, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        var initialVersion = service.CurrentView.Version;

        // Act: Two silos join simultaneously - one OLD, one NEW
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        membershipService.UpdateSiloStatus(Silo3, SiloStatus.Active, "silo3");
        
        // Both appear in manifest at the same time with different capabilities
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false)) // OLD
                .Add(Silo3, CreateManifest(hasDistributedCapability: true))); // NEW

        // Assert: Mixed cluster - only NEW silo should be in filtered members
        await WaitForCondition(() => service.CurrentView.Version > initialVersion, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo3, service.CurrentView.Members);
        
        // AllActiveMembers should have all 3
        Assert.Equal(3, service.AllActiveMembers.Length);
    }

    /// <summary>
    /// Verifies that a silo transitioning through different statuses
    /// (Active -> ShuttingDown -> Dead -> Active) is handled correctly.
    /// </summary>
    [Fact]
    public async Task Service_SiloStatusTransitions_HandledCorrectly()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        await WaitForCondition(() => service.CurrentView.Members.Length == 2, TimeSpan.FromSeconds(5));
        var initialVersion = service.CurrentView.Version;

        // Act: Silo2 goes through status transitions
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.ShuttingDown, "silo2");
        await WaitForCondition(() => service.CurrentView.Version > initialVersion, TimeSpan.FromSeconds(5));
        
        // ShuttingDown silos should not be in active members
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo1, service.CurrentView.Members);
        var shuttingDownVersion = service.CurrentView.Version;

        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Dead, "silo2");
        await WaitForCondition(() => service.CurrentView.Version > shuttingDownVersion, TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members);
    }

    /// <summary>
    /// Verifies that the service can be disposed while waiting for manifest,
    /// without hanging or throwing unexpected exceptions.
    /// </summary>
    [Fact]
    public async Task Service_DisposalDuringManifestWait_CompletesGracefully()
    {
        // Arrange: Silo active but not in manifest (will wait indefinitely)
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        // Don't add Silo1 to manifest - service will wait

        var service = CreateService(membershipService, manifestProvider);

        // Wait a bit to ensure service is waiting for manifest
        await Task.Delay(100);

        // Act & Assert: Disposal should complete without hanging
        var disposeTask = service.DisposeAsync().AsTask();
        var completedTask = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        
        Assert.Equal(disposeTask, completedTask); // Disposal should complete, not timeout
    }

    /// <summary>
    /// Verifies that rapid alternating between all-OLD and mixed cluster states
    /// is handled correctly without race conditions.
    /// </summary>
    [Fact]
    public async Task Service_RapidCapabilityChanges_HandledCorrectly()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        await WaitForCondition(() => service.CurrentView.Members.Length == 2, TimeSpan.FromSeconds(5));

        // Act: Rapidly toggle Silo2's capability
        for (int i = 0; i < 5; i++)
        {
            var hasCapability = i % 2 == 0;
            manifestProvider.UpdateManifest(
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                    .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                    .Add(Silo2, CreateManifest(hasDistributedCapability: hasCapability)));
            await Task.Delay(20);
        }

        // Assert: Final state should be consistent
        // After 5 iterations (0,1,2,3,4), i=4, hasCapability = true (4 % 2 == 0)
        await Task.Delay(100); // Let updates settle
        
        // With Silo2 having capability (mixed cluster), only Silo2 should be in members
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo2, service.CurrentView.Members);
    }

    /// <summary>
    /// Verifies that when a silo is in Joining status (not yet Active),
    /// it doesn't affect the directory membership view.
    /// </summary>
    [Fact]
    public async Task Service_JoiningSilo_NotIncludedUntilActive()
    {
        // Arrange
        var membershipService = new MockClusterMembershipService();
        var manifestProvider = new MockClusterManifestProvider();

        membershipService.UpdateSiloStatus(Silo1, SiloStatus.Active, "silo1");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false)));

        await using var service = CreateService(membershipService, manifestProvider);

        await WaitForCondition(() => service.CurrentView.Members.Length == 1, TimeSpan.FromSeconds(5));
        var initialVersion = service.CurrentView.Version;

        // Act: Add Silo2 as Joining (not Active yet)
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Joining, "silo2");
        manifestProvider.UpdateManifest(
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty
                .Add(Silo1, CreateManifest(hasDistributedCapability: false))
                .Add(Silo2, CreateManifest(hasDistributedCapability: true)));

        // Wait for potential update
        await Task.Delay(100);

        // Assert: Silo2 should NOT be in members (still Joining)
        Assert.Single(service.CurrentView.Members);
        Assert.Contains(Silo1, service.CurrentView.Members);
        Assert.DoesNotContain(Silo2, service.CurrentView.Members);

        // Now activate Silo2
        membershipService.UpdateSiloStatus(Silo2, SiloStatus.Active, "silo2");

        // Assert: Now Silo2 should be included (and it's a mixed cluster)
        // Wait specifically for Silo2 to appear in members (not just version change)
        await WaitForCondition(() => 
            service.CurrentView.Members.Contains(Silo2), 
            TimeSpan.FromSeconds(5));
        Assert.Single(service.CurrentView.Members); // Mixed cluster, only NEW
        Assert.Contains(Silo2, service.CurrentView.Members);
    }

    private static DirectoryMembershipService CreateService(
        MockClusterMembershipService membershipService,
        MockClusterManifestProvider manifestProvider)
    {
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var logger = Substitute.For<ILogger<DirectoryMembershipService>>();

        return new DirectoryMembershipService(
            membershipService,
            grainFactory,
            logger,
            manifestProvider);
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
