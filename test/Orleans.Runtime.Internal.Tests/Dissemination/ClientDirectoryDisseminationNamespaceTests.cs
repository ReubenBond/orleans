using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public sealed class ClientDirectoryDisseminationNamespaceTests
{
    [Fact]
    public async Task ProducesAndAppliesDeltaFromRetainedRoute()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var originalClients = Enumerable.Range(0, 32)
            .Select(index => Client($"client-{index}"))
            .ToImmutableHashSet();
        var addedClient = Client("added");
        var removedClient = originalClients.First();
        var updatedClients = originalClients.Remove(removedClient).Add(addedClient);
        var source = new FakeClientDirectoryDisseminationParticipant();
        source.SetRoute(silo, originalClients, version: 1);
        var sourceNamespace = CreateNamespace(source, serializer);
        _ = Assert.Single(sourceNamespace.Digests);
        source.SetRoute(silo, updatedClients, version: 2);
        _ = Assert.Single(sourceNamespace.Digests);

        var repair = sourceNamespace.CreateRepair(new DisseminationRepairRequest(
            silo,
            fromVersion: 1,
            toVersion: 2,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));

        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal((1, 2), (value.FromVersion, value.ToVersion));
        var update = serializer.Deserialize<ClientDirectoryRouteUpdate>(value.Payload);
        Assert.Null(update.Snapshot);
        var delta = Assert.IsType<ClientDirectoryRouteDelta>(update.Delta);
        Assert.Equal([addedClient], delta.AddedClients);
        Assert.Equal([removedClient], delta.RemovedClients);

        var receiver = new FakeClientDirectoryDisseminationParticipant();
        receiver.SetRoute(silo, originalClients, version: 1);
        var receiverNamespace = CreateNamespace(receiver, serializer);
        var result = await receiverNamespace.ApplyValueAsync(value, CancellationToken.None);

        Assert.Equal(DisseminationApplyResult.Applied, result);
        Assert.True(receiver.Routes[silo].ConnectedClients.SetEquals(updatedClients));
        Assert.Equal(2, receiver.Routes[silo].Version);
        var forwardedRepair = receiverNamespace.CreateRepair(new DisseminationRepairRequest(
            silo,
            fromVersion: 1,
            toVersion: 2,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));
        Assert.Equal(1, Assert.Single(forwardedRepair.Values).FromVersion);
    }

    [Fact]
    public void FallsBackToFullRouteWithoutRetainedBaseline()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var clients = ImmutableHashSet.Create(Client("client"));
        var source = new FakeClientDirectoryDisseminationParticipant();
        source.SetRoute(silo, clients, version: 2);
        var disseminationNamespace = CreateNamespace(source, serializer);

        var repair = disseminationNamespace.CreateRepair(new DisseminationRepairRequest(
            silo,
            fromVersion: 1,
            toVersion: 2,
            maxItemCount: 1,
            maxBatchBytes: 1024 * 1024,
            maxPayloadBytes: 1024 * 1024));

        Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
        var value = Assert.Single(repair.Values);
        Assert.Equal((0, 2), (value.FromVersion, value.ToVersion));
        var update = serializer.Deserialize<ClientDirectoryRouteUpdate>(value.Payload);
        Assert.NotNull(update.Snapshot);
        Assert.Null(update.Delta);
        Assert.True(update.Snapshot.SetEquals(clients));
    }

    private static ClientDirectoryDisseminationNamespace CreateNamespace(
        IClientDirectoryDisseminationParticipant participant,
        Serializer serializer)
    {
        var options = new SiloMessagingOptions();
        options.ClientDirectoryDissemination.Enabled = true;
        return new(participant, Options.Create(options), serializer);
    }

    private static GrainId Client(string id) => ClientGrainId.Create(id).GrainId;

    private sealed class FakeClientDirectoryDisseminationParticipant : IClientDirectoryDisseminationParticipant
    {
        public ImmutableDictionary<SiloAddress, ClientDirectoryRoute> Routes { get; private set; } =
            ImmutableDictionary<SiloAddress, ClientDirectoryRoute>.Empty;

        public ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> GetRoutesForDissemination() =>
            Routes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => (pair.Value.ConnectedClients, pair.Value.Version));

        public DisseminationApplyResult ApplyDisseminatedRoute(
            SiloAddress siloAddress,
            long? expectedVersion,
            ClientDirectoryRoute route)
        {
            if (Routes.TryGetValue(siloAddress, out var current))
            {
                if (route.Version < current.Version)
                {
                    return DisseminationApplyResult.Obsolete;
                }

                if (route.Version == current.Version)
                {
                    return current.ConnectedClients.SetEquals(route.ConnectedClients)
                        ? DisseminationApplyResult.Duplicate
                        : DisseminationApplyResult.Rejected;
                }

                if (expectedVersion is { } version && current.Version != version)
                {
                    return DisseminationApplyResult.Rejected;
                }
            }
            else if (expectedVersion is not null)
            {
                return DisseminationApplyResult.Rejected;
            }

            Routes = Routes.SetItem(siloAddress, route);
            return DisseminationApplyResult.Applied;
        }

        public void SetRoute(SiloAddress siloAddress, ImmutableHashSet<GrainId> clients, long version) =>
            Routes = Routes.SetItem(siloAddress, new(clients, version));
    }
}
