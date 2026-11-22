using System.Buffers;
using Orleans.Consensus.CasPaxos.Grpc;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

public sealed class CasPaxosGrpcSqliteTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact(Timeout = 90_000)]
    [Trait("Category", "Integration")]
    public async Task EndpointOwnedFactoryUsesFastSqliteQuorumFailsOverAndReopensDurableIdentities()
    {
        var directory = Directory.CreateTempSubdirectory("clog-caspaxos-");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60), TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            watchdog.Token,
            TestContext.Current.CancellationToken);
        var token = cancellation.Token;
        var priorVersions = new HashSet<MetaStoreVersion>();
        MetaStoreVersion saved;
        MetaStoreVersion savedEmpty;
        try
        {
            await using (var cluster = await SqliteFastRegisterCluster.OpenAsync(directory.FullName, token))
            {
                Assert.Equal(5, cluster.Acceptors.Count);
                Assert.Equal(3, cluster.Frontends.Count);
                using var factory = CreateFactory(cluster.Frontends);
                var missing = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "missing", token);
                Assert.Null(await missing.ReadAsync(token));
                var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "http2", token);
                Assert.True((await store.WriteAsync(null, "first"u8.ToArray(), token)).IsApplied());

                // Inspect the fresh-key create before later reads can replace the accepted ballot.
                // Idle engines may be retired between calls; Fast includes legitimate classic recovery.
                var states = await Task.WhenAll(cluster.Acceptors.Select(acceptor =>
                    acceptor.InspectAsync("0123456789abcdef0123456789abcdef/http2", token).AsTask()));
                Assert.InRange(states.Count(static state => state?.AcceptedBallot.IsFastRoundBallot is true), 4, 5);
                using var first = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(token));
                Assert.Equal("first"u8.ToArray(), first.Value.ToArray());
                Assert.True(priorVersions.Add(first.Version));
                Assert.False((await store.WriteAsync(null, "duplicate"u8.ToArray(), token)).IsApplied());

                Assert.True((await store.WriteAsync(first.Version, "first"u8.ToArray(), token)).IsApplied());
                using var sameBytes = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(token));
                Assert.Equal("first"u8.ToArray(), sameBytes.Value.ToArray());
                Assert.True(priorVersions.Add(sameBytes.Version));
                Assert.False((await store.WriteAsync(first.Version, "stale"u8.ToArray(), token)).IsApplied());

                var empty = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "empty", token);
                savedEmpty = (await empty.WriteAsync(null, ReadOnlySequence<byte>.Empty, token)).AppliedVersion();
                using (var readEmpty = Assert.IsType<MetaStoreEntry>(await empty.ReadAsync(token)))
                {
                    Assert.Equal(savedEmpty, readEmpty.Version);
                    Assert.Empty(readEmpty.Value.ToArray());
                }

                Assert.NotEmpty(cluster.Frontends.SelectMany(static host => host.Requests));

                // Initial reads may fail over before reaching a server. Stop both earlier origins so
                // the same factory must reach the sole survivor, irrespective of its initial affinity.
                await Task.WhenAll(cluster.Frontends.Take(2).Select(host => host.StopAsync(token)));
                var survivor = cluster.Frontends[2];
                var requestsBefore = survivor.Requests.Length;
                using var failedOver = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(token));
                Assert.Equal(sameBytes.Version, failedOver.Version);
                Assert.Equal("first"u8.ToArray(), failedOver.Value.ToArray());
                Assert.EndsWith("/Read", Assert.Single(survivor.Requests.Skip(requestsBefore)).Path, StringComparison.Ordinal);
                saved = (await store.WriteAsync(failedOver.Version, "after-failover"u8.ToArray(), token)).AppliedVersion();
                using var afterFailover = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(token));
                Assert.Equal(saved, afterFailover.Version);
                Assert.Equal("after-failover"u8.ToArray(), afterFailover.Value.ToArray());
                Assert.True(priorVersions.Add(saved));
                Assert.Contains(survivor.Requests.Skip(requestsBefore), static request =>
                    request.Path.EndsWith("/CompareExchange", StringComparison.Ordinal));
                Assert.All(cluster.Frontends.SelectMany(static host => host.Requests), static request =>
                    Assert.Equal("HTTP/2", request.Protocol));
            }

            // All frontends, register services, allocators, and acceptor writers have been closed, and so has every SQLite file.
            // The fixture reuses the same member-N/proposer-N files and proposer identities 1..3.
            FileHandleLeakCheck.AssertClosed(directory.FullName);
            await using var reopened = await SqliteFastRegisterCluster.OpenAsync(directory.FullName, token);
            using var restoredFactory = CreateFactory(reopened.Frontends);
            var restoredStore = await restoredFactory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "http2", token);
            using var restored = Assert.IsType<MetaStoreEntry>(await restoredStore.ReadAsync(token));
            Assert.Equal(saved, restored.Version);
            Assert.Equal("after-failover"u8.ToArray(), restored.Value.ToArray());
            var restoredEmpty = await restoredFactory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "empty", token);
            using var presentEmpty = Assert.IsType<MetaStoreEntry>(await restoredEmpty.ReadAsync(token));
            Assert.Equal(savedEmpty, presentEmpty.Version);
            Assert.Empty(presentEmpty.Value.ToArray());

            Assert.True((await restoredStore.WriteAsync(restored.Version, "after-restart"u8.ToArray(), token)).IsApplied());
            using var advanced = Assert.IsType<MetaStoreEntry>(await restoredStore.ReadAsync(token));
            Assert.Equal("after-restart"u8.ToArray(), advanced.Value.ToArray());
            Assert.DoesNotContain(advanced.Version, priorVersions);
            Assert.False((await restoredStore.WriteAsync(restored.Version, "stale"u8.ToArray(), token)).IsApplied());
            using var final = Assert.IsType<MetaStoreEntry>(await restoredStore.ReadAsync(token));
            Assert.Equal(advanced.Version, final.Version);
            Assert.Equal("after-restart"u8.ToArray(), final.Value.ToArray());
            Assert.All(reopened.Frontends.SelectMany(static host => host.Requests), static request =>
                Assert.Equal("HTTP/2", request.Protocol));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public void Dispose() => _leaks.Dispose();

    private static CasPaxosMetaStoreFactory CreateFactory(IReadOnlyList<LoopbackRegisterHost> frontends) =>
        new(
            frontends.Select(static host => host.Address),
            TimeProvider.System,
            new GrpcRegisterClientOptions
            {
                CallTimeout = TimeSpan.FromSeconds(15),
                AttemptTimeout = TimeSpan.FromSeconds(5),
            });
}
