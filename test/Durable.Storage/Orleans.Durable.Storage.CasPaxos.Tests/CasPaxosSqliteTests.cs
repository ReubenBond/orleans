using System.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// The adapter over in-process Fast CASPaxos register services and durable SQLite acceptors and ballot allocators.
/// Remote voters, addressed over Orleans RPC, are the Orleans-hosted consensus integration's to qualify.
/// </summary>
public sealed class CasPaxosSqliteTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact(Timeout = 90_000)]
    [Trait("Category", "Integration")]
    public async Task FactoryUsesFastSqliteQuorumAcrossProposersAndReopensDurableIdentities()
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
                Assert.Equal(3, cluster.Registers.Count);
                using var factory = new CasPaxosMetaStoreFactory(cluster.Registers[0]);
                var missing = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "missing", token);
                Assert.Null(await missing.ReadAsync(token));
                var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "sqlite", token);
                Assert.True((await store.WriteAsync(null, "first"u8.ToArray(), token)).IsApplied());

                // Inspect the fresh-key create before later reads can replace the accepted ballot.
                // Idle engines may be retired between calls; Fast includes legitimate classic recovery.
                var states = await Task.WhenAll(cluster.Acceptors.Select(acceptor =>
                    acceptor.InspectAsync("0123456789abcdef0123456789abcdef/sqlite", token).AsTask()));
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

                // Another proposer, with its own durable ballot allocator, reads the first proposer's write and advances
                // the register from that version; the first proposer then observes the new version.
                using var otherFactory = new CasPaxosMetaStoreFactory(cluster.Registers[2]);
                var other = await otherFactory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "sqlite", token);
                using var observed = Assert.IsType<MetaStoreEntry>(await other.ReadAsync(token));
                Assert.Equal(sameBytes.Version, observed.Version);
                Assert.Equal("first"u8.ToArray(), observed.Value.ToArray());
                saved = (await other.WriteAsync(observed.Version, "other-proposer"u8.ToArray(), token)).AppliedVersion();
                using var afterOther = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(token));
                Assert.Equal(saved, afterOther.Version);
                Assert.Equal("other-proposer"u8.ToArray(), afterOther.Value.ToArray());
                Assert.True(priorVersions.Add(saved));
                Assert.False((await store.WriteAsync(sameBytes.Version, "stale"u8.ToArray(), token)).IsApplied());
            }

            // All register services, allocators, and acceptor writers have been closed, and so has every SQLite file.
            // The fixture reuses the same member-N/proposer-N files and proposer identities 1..3.
            FileHandleLeakCheck.AssertClosed(directory.FullName);
            await using var reopened = await SqliteFastRegisterCluster.OpenAsync(directory.FullName, token);
            using var restoredFactory = new CasPaxosMetaStoreFactory(reopened.Registers[0]);
            var restoredStore = await restoredFactory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "sqlite", token);
            using var restored = Assert.IsType<MetaStoreEntry>(await restoredStore.ReadAsync(token));
            Assert.Equal(saved, restored.Version);
            Assert.Equal("other-proposer"u8.ToArray(), restored.Value.ToArray());
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
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public void Dispose() => _leaks.Dispose();
}
