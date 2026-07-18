using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public sealed class ClusterManifestDisseminationNamespaceTests
{
    [Fact]
    public async Task ProducesAndAppliesMembershipVersionedManifest()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var manifest = CreateManifest("grain");
        var source = new FakeParticipant();
        source.SetManifest(new ClusterManifest(
            new MajorMinorVersion(3, 1),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(silo, manifest)));
        var sourceNamespace = CreateNamespace(source, serializer);

        var repair = sourceNamespace.CreateRepair(new DisseminationRepairRequest(
            DisseminationKey.Default,
            fromVersion: 4,
            toVersion: 4,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));

        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal((0, 4), (value.FromVersion, value.ToVersion));

        var receiver = new FakeParticipant();
        var receiverNamespace = CreateNamespace(receiver, serializer);
        var result = await receiverNamespace.ApplyValueAsync(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Applied, result);
        Assert.Equal(new MajorMinorVersion(3, 1), receiver.Manifest.Version);
        Assert.Equal(manifest, receiver.Manifest.Silos[silo]);
    }

    [Fact]
    public async Task RejectsConflictingManifestForSameSiloAddress()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var receiver = new FakeParticipant();
        receiver.SetManifest(new ClusterManifest(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(silo, CreateManifest("original"))));
        var receiverNamespace = CreateNamespace(receiver, serializer);
        var conflicting = new DisseminationValue(
            DisseminationKey.Default,
            fromVersion: 0,
            toVersion: 2,
            serializer.SerializeToArray(new ClusterManifestUpdate(
                new MajorMinorVersion(1, 1),
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(silo, CreateManifest("conflicting")),
                includesAllActiveServers: true)));

        var result = await receiverNamespace.ApplyValueAsync(conflicting, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.Equal(new MajorMinorVersion(1, 0), receiver.Manifest.Version);
    }

    private static ClusterManifestDisseminationNamespace CreateNamespace(
        IClusterManifestDisseminationParticipant participant,
        Serializer serializer)
    {
        var options = new SiloMessagingOptions();
        options.ClusterManifestDissemination.Enabled = true;
        return new(participant, Options.Create(options), serializer);
    }

    private static GrainManifest CreateManifest(string grainType)
    {
        var grains = ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(
            GrainType.Create(grainType),
            new GrainProperties(ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)));
        return new GrainManifest(grains, ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }

    private sealed class FakeParticipant : IClusterManifestDisseminationParticipant
    {
        public ClusterManifest Manifest { get; private set; } = new(
            MajorMinorVersion.MinValue,
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty);

        public ClusterManifestDisseminationSnapshot GetManifestForDissemination() => new(
            Manifest,
            IncludesAllActiveServers: true);

        public DisseminationApplyResult ApplyDisseminatedManifest(ClusterManifestUpdate update)
        {
            var silos = Manifest.Silos.ToBuilder();
            foreach (var entry in update.SiloManifests)
            {
                if (silos.TryGetValue(entry.Key, out var current) && current != entry.Value)
                {
                    return DisseminationApplyResult.Rejected;
                }

                silos[entry.Key] = entry.Value;
            }

            Manifest = new ClusterManifest(update.Version, silos.ToImmutable());
            return DisseminationApplyResult.Applied;
        }

        public void SetManifest(ClusterManifest manifest) => Manifest = manifest;
    }
}
