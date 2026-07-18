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
    public async Task IdenticalManifestContentIsSharedByMultipleSiloReferences()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo1 = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var silo2 = SiloAddress.FromParsableString("127.0.0.1:101@101");
        var manifest = CreateManifest("grain");
        var hash = ManifestHashCalculator.ComputeHash(manifest);
        var source = new FakeParticipant();
        source.AddContent(hash, manifest);
        source.AddReference(silo1, hash);
        source.AddReference(silo2, hash);
        var (referenceNamespace, contentNamespace) = CreateNamespaces(source, serializer);

        Assert.Equal(2, referenceNamespace.Digests.Count());
        Assert.Single(contentNamespace.Digests);

        var contentRepair = contentNamespace.CreateRepair(CreateRequest(hash.Value));
        var silo1Repair = referenceNamespace.CreateRepair(CreateRequest(silo1));
        var silo2Repair = referenceNamespace.CreateRepair(CreateRequest(silo2));

        var receiver = new FakeParticipant();
        var (receiverReferences, receiverContents) = CreateNamespaces(receiver, serializer);
        Assert.Equal(
            DisseminationApplyResult.Applied,
            await receiverReferences.ApplyValueAsync(Assert.Single(silo1Repair.Values), CancellationToken.None));
        Assert.Equal(
            DisseminationApplyResult.Applied,
            await receiverContents.ApplyValueAsync(Assert.Single(contentRepair.Values), CancellationToken.None));
        Assert.Equal(
            DisseminationApplyResult.Applied,
            await receiverReferences.ApplyValueAsync(Assert.Single(silo2Repair.Values), CancellationToken.None));

        Assert.Equal(hash, receiver.References[silo1]);
        Assert.Equal(hash, receiver.References[silo2]);
        Assert.Equal(manifest, receiver.Contents[hash]);
    }

    [Fact]
    public async Task ConflictingReferenceAndInvalidContentAreRejected()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var original = CreateManifest("original");
        var conflicting = CreateManifest("conflicting");
        var originalHash = ManifestHashCalculator.ComputeHash(original);
        var conflictingHash = ManifestHashCalculator.ComputeHash(conflicting);
        var receiver = new FakeParticipant();
        receiver.AddReference(silo, originalHash);
        var (referenceNamespace, contentNamespace) = CreateNamespaces(receiver, serializer);

        var conflictingReference = new DisseminationValue(
            silo,
            fromVersion: 0,
            toVersion: 1,
            serializer.SerializeToArray(new ClusterManifestReference(conflictingHash)));
        var invalidContent = new DisseminationValue(
            originalHash.Value,
            fromVersion: 0,
            toVersion: 1,
            serializer.SerializeToArray(new ClusterManifestContent(originalHash, conflicting)));

        Assert.Equal(
            DisseminationApplyResult.Rejected,
            await referenceNamespace.ApplyValueAsync(conflictingReference, CancellationToken.None));
        Assert.Equal(
            DisseminationApplyResult.Rejected,
            await contentNamespace.ApplyValueAsync(invalidContent, CancellationToken.None));
    }

    private static DisseminationRepairRequest CreateRequest(DisseminationKey key) => new(
        key,
        fromVersion: null,
        toVersion: 1,
        maxItemCount: 1,
        maxBatchBytes: 1024 * 1024,
        maxPayloadBytes: 1024 * 1024);

    private static (ClusterManifestDisseminationNamespace References, GrainManifestDisseminationNamespace Contents)
        CreateNamespaces(IClusterManifestDisseminationParticipant participant, Serializer serializer)
    {
        var options = new SiloMessagingOptions();
        options.ClusterManifestDissemination.Enabled = true;
        return (
            new(participant, Options.Create(options), serializer),
            new(participant, Options.Create(options), serializer));
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
        public ImmutableDictionary<SiloAddress, ManifestHash> References { get; private set; } =
            ImmutableDictionary<SiloAddress, ManifestHash>.Empty;

        public ImmutableDictionary<ManifestHash, GrainManifest> Contents { get; private set; } =
            ImmutableDictionary<ManifestHash, GrainManifest>.Empty;

        public ImmutableDictionary<SiloAddress, ManifestHash> GetManifestReferencesForDissemination() => References;

        public ImmutableDictionary<ManifestHash, GrainManifest> GetManifestContentsForDissemination() => Contents;

        public DisseminationApplyResult ApplyDisseminatedManifestReference(
            SiloAddress siloAddress,
            ManifestHash manifestHash)
        {
            if (References.TryGetValue(siloAddress, out var existing))
            {
                return existing == manifestHash
                    ? DisseminationApplyResult.Duplicate
                    : DisseminationApplyResult.Rejected;
            }

            References = References.Add(siloAddress, manifestHash);
            return DisseminationApplyResult.Applied;
        }

        public DisseminationApplyResult ApplyDisseminatedManifestContent(
            ManifestHash manifestHash,
            GrainManifest manifest)
        {
            if (Contents.TryGetValue(manifestHash, out var existing))
            {
                return existing.Equals(manifest)
                    ? DisseminationApplyResult.Duplicate
                    : DisseminationApplyResult.Rejected;
            }

            Contents = Contents.Add(manifestHash, manifest);
            return DisseminationApplyResult.Applied;
        }

        public void AddReference(SiloAddress siloAddress, ManifestHash manifestHash) =>
            References = References.Add(siloAddress, manifestHash);

        public void AddContent(ManifestHash manifestHash, GrainManifest manifest) =>
            Contents = Contents.Add(manifestHash, manifest);
    }
}
