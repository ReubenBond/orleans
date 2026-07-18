using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public sealed class SiloMetadataDisseminationNamespaceTests
{
    [Fact]
    public async Task ProducesAndAppliesImmutableMetadata()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var metadata = CreateMetadata(("region", "west"), ("zone", "1"));
        var source = new FakeParticipant();
        source.Add(silo, metadata);
        var sourceNamespace = CreateNamespace(source, serializer);

        var repair = sourceNamespace.CreateRepair(new DisseminationRepairRequest(
            silo,
            fromVersion: null,
            toVersion: 1,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));

        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal((0, 1), (value.FromVersion, value.ToVersion));

        var receiver = new FakeParticipant();
        var receiverNamespace = CreateNamespace(receiver, serializer);
        Assert.Equal(
            DisseminationApplyResult.Applied,
            await receiverNamespace.ApplyValueAsync(value, CancellationToken.None));
        Assert.Equal(
            DisseminationApplyResult.Duplicate,
            await receiverNamespace.ApplyValueAsync(value, CancellationToken.None));
        Assert.True(SiloMetadataDisseminationNamespace.SiloMetadataEquals(metadata, receiver.Metadata[silo]));
    }

    [Fact]
    public async Task RejectsConflictingMetadataForSameSiloAddress()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var receiver = new FakeParticipant();
        receiver.Add(silo, CreateMetadata(("region", "west")));
        var receiverNamespace = CreateNamespace(receiver, serializer);
        var conflicting = new DisseminationValue(
            silo,
            fromVersion: 0,
            toVersion: 1,
            serializer.SerializeToArray(CreateMetadata(("region", "east"))));

        var result = await receiverNamespace.ApplyValueAsync(conflicting, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.Equal("west", receiver.Metadata[silo].Metadata["region"]);
    }

    private static SiloMetadataDisseminationNamespace CreateNamespace(
        ISiloMetadataDisseminationParticipant participant,
        Serializer serializer)
    {
        var options = new SiloMessagingOptions();
        options.SiloMetadataDissemination.Enabled = true;
        return new(participant, Options.Create(options), serializer);
    }

    private static SiloMetadata CreateMetadata(params (string Key, string Value)[] entries)
    {
        var metadata = new SiloMetadata();
        foreach (var (key, value) in entries)
        {
            metadata.AddMetadata(key, value);
        }

        return metadata;
    }

    private sealed class FakeParticipant : ISiloMetadataDisseminationParticipant
    {
        public ImmutableDictionary<SiloAddress, SiloMetadata> Metadata { get; private set; } =
            ImmutableDictionary<SiloAddress, SiloMetadata>.Empty;

        public ImmutableDictionary<SiloAddress, SiloMetadata> GetSiloMetadataForDissemination() => Metadata;

        public DisseminationApplyResult ApplyDisseminatedSiloMetadata(
            SiloAddress siloAddress,
            SiloMetadata metadata)
        {
            if (Metadata.TryGetValue(siloAddress, out var existing))
            {
                return SiloMetadataDisseminationNamespace.SiloMetadataEquals(existing, metadata)
                    ? DisseminationApplyResult.Duplicate
                    : DisseminationApplyResult.Rejected;
            }

            Metadata = Metadata.Add(siloAddress, metadata);
            return DisseminationApplyResult.Applied;
        }

        public void Add(SiloAddress siloAddress, SiloMetadata metadata) =>
            Metadata = Metadata.Add(siloAddress, metadata);
    }
}
