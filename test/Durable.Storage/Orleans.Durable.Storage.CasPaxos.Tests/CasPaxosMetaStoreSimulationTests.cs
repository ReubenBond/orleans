using System.Buffers;
using System.Globalization;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Consensus.CasPaxos.Testing;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

public sealed class CasPaxosMetaStoreSimulationTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public Task MismatchingNativeTokenPreservesExistingAndMissingKeys() =>
        CasPaxosTestFixture.RunAndReplayAsync(823, async simulation =>
        {
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var first = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "first", TestContext.Current.CancellationToken);
            var second = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "second", TestContext.Current.CancellationToken);
            var missing = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "missing", TestContext.Current.CancellationToken);
            Assert.True((await first.WriteAsync(null, "first"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            Assert.True((await second.WriteAsync(null, "second"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            using var initial = Assert.IsType<MetaStoreEntry>(await first.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True((await first.WriteAsync(initial.Version, "advanced"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            using var foreign = Assert.IsType<MetaStoreEntry>(await first.ReadAsync(TestContext.Current.CancellationToken));
            using var original = Assert.IsType<MetaStoreEntry>(await second.ReadAsync(TestContext.Current.CancellationToken));
            Assert.NotEqual(foreign.Version, original.Version);

            Assert.False((await missing.WriteAsync(foreign.Version, "create"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            Assert.Null(await missing.ReadAsync(TestContext.Current.CancellationToken));
            Assert.False((await second.WriteAsync(foreign.Version, "overwrite"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());

            using var current = Assert.IsType<MetaStoreEntry>(await second.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(original.Version, current.Version);
            Assert.Equal("second"u8.ToArray(), current.Value.ToArray());
        });

    [Fact]
    public Task PresentEmptyAndSameByteWritesAdvanceVersion() =>
        CasPaxosTestFixture.RunAsync(811, async simulation =>
        {
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "empty", TestContext.Current.CancellationToken);
            Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True((await store.WriteAsync(null, ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken)).IsApplied());
            using var first = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Empty(first.Value.ToArray());

            Assert.True((await store.WriteAsync(first.Version, ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken)).IsApplied());
            using var second = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.NotEqual(first.Version, second.Version);
            Assert.Empty(second.Value.ToArray());
            Assert.False((await store.WriteAsync(first.Version, "stale"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            using var observed = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(second.Version, observed.Version);
            Assert.Empty(observed.Value.ToArray());
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HealthyStickyClientHasOneWinnerAndThirtyOneMismatches(bool existing) =>
        CasPaxosTestFixture.RunAsync(812, async simulation =>
        {
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "race", TestContext.Current.CancellationToken);
            MetaStoreVersion? expected = null;
            if (existing)
            {
                expected = (await store.WriteAsync(null, "d"u8.ToArray(), TestContext.Current.CancellationToken)).AppliedVersion();
            }

            var attempts = Enumerable.Range(0, 32).Select(index =>
                store.WriteAsync(
                    expected,
                    [existing ? (byte)100 : (byte)index],
                    TestContext.Current.CancellationToken).AsTask()).ToArray();
            Assert.All(attempts, static attempt => Assert.False(attempt.IsCompleted));
            var results = await Task.WhenAll(attempts);
            var winner = Assert.Single(results, static result => result.IsApplied());
            Assert.Equal(31, results.Count(static result => !result.IsApplied()));
            using var current = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { existing ? (byte)100 : (byte)Array.FindIndex(results, static result => result.IsApplied()) }, current.Value.ToArray());
            Assert.Equal(winner.AppliedVersion(), current.Version);
            Assert.NotEqual(expected, current.Version);
            Assert.DoesNotContain(simulation.Trace, static entry => entry.Contains("status=Unknown", StringComparison.Ordinal));
        });

    [Fact]
    public Task ConcurrentProposersResolveSameRevisionContendersDefinitively() =>
        CasPaxosTestFixture.RunAsync(746, async simulation =>
        {
            var attempts = Enumerable.Range(1, 5).Select(ProposeAsync).ToArray();
            var outcomes = await Task.WhenAll(attempts);
            var winner = Assert.Single(outcomes, static outcome => outcome.Applied is not null);
            Assert.All(outcomes, static outcome => Assert.Null(outcome.Error));
            using var observer = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await observer.GetRegisterAsync(CasPaxosTestFixture.Cluster, "contended", TestContext.Current.CancellationToken);
            using var current = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { (byte)winner.Node }, current.Value.ToArray());
            Assert.Equal(winner.Applied, current.Version);

            async Task<(int Node, MetaStoreVersion? Applied, IOException? Error)> ProposeAsync(int node)
            {
                using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient(node));
                var candidate = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "contended", TestContext.Current.CancellationToken);
                try
                {
                    var outcome = await candidate.WriteAsync(null, [(byte)node], TestContext.Current.CancellationToken);
                    return (node, outcome.IsApplied() ? outcome.AppliedVersion() : null, null);
                }
                catch (IOException error)
                {
                    return (node, null, error);
                }
            }
        });

    [Fact]
    public Task FactoriesShareCompleteKeysWithoutMergingClustersOrOrdinalNames() =>
        CasPaxosTestFixture.RunAsync(813, async simulation =>
        {
            using var first = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            using var second = new CasPaxosMetaStoreFactory(simulation.CreateClient(2));
            var otherCluster = new Guid("01234567-89ab-cdef-0123-456789abcdee");
            var keys = new[]
            {
                (CasPaxosTestFixture.Cluster, "l/orders"),
                (CasPaxosTestFixture.Cluster, "l/orders2"),
                (otherCluster, "l/orders"),
                (CasPaxosTestFixture.Cluster, "a/l/b"),
                (CasPaxosTestFixture.Cluster, "a/lb"),
                (CasPaxosTestFixture.Cluster, "l/a.b"),
                (CasPaxosTestFixture.Cluster, "bucket-map"),
                (CasPaxosTestFixture.Cluster, "node-registry"),
            };
            for (var index = 0; index < keys.Length; index++)
            {
                var store = await first.GetRegisterAsync(keys[index].Item1, keys[index].Item2, TestContext.Current.CancellationToken);
                Assert.True((await store.WriteAsync(null, [(byte)index], TestContext.Current.CancellationToken)).IsApplied());
            }

            for (var index = 0; index < keys.Length; index++)
            {
                var store = await second.GetRegisterAsync(keys[index].Item1, keys[index].Item2, TestContext.Current.CancellationToken);
                using var entry = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(new byte[] { (byte)index }, entry.Value.ToArray());
                Assert.False((await store.WriteAsync(null, "collision"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            }
        });

    [Fact]
    public Task FastPathNeedsFourthMemberButNotFifth() =>
        CasPaxosTestFixture.RunAsync(814, async simulation =>
        {
            simulation.SetLinkDelay(1, 4, TimeSpan.FromMilliseconds(20));
            simulation.SetLinkDelay(1, 5, TimeSpan.FromSeconds(1));
            simulation.SetReplyDelay(1, TimeSpan.Zero);
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "quorum", TestContext.Current.CancellationToken);
            var started = simulation.TimeProvider.GetUtcNow();
            var pending = store.WriteAsync(null, "fast"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();

            // Members 1, 2 and 3 cannot finish a fast write before member 4's delayed round trip.
            await simulation.DelayAsync(TimeSpan.FromMilliseconds(15));
            Assert.False(pending.IsCompleted);
            Assert.True((await pending).IsApplied());
            Assert.InRange(
                simulation.TimeProvider.GetUtcNow() - started,
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(999));
            Assert.Contains(simulation.Trace, static entry => entry.Contains("commit node=1 fast=True", StringComparison.Ordinal));
        });

    [Fact]
    public Task ClassicRecoveryWithTwoMembersDownPreservesChosenState() =>
        CasPaxosTestFixture.RunAsync(815, async simulation =>
        {
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "recovery", TestContext.Current.CancellationToken);
            Assert.True((await store.WriteAsync(null, "first"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
            using var initial = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Contains(simulation.Trace, static entry => entry.Contains("commit node=1 fast=True", StringComparison.Ordinal));
            simulation.CrashNode(4);
            simulation.CrashNode(5);
            var traceStart = simulation.Trace.Length;

            var recovered = (await store.WriteAsync(initial.Version, "recovered"u8.ToArray(), TestContext.Current.CancellationToken)).AppliedVersion();
            Assert.Contains(simulation.Trace.Skip(traceStart), static entry => entry.Contains("commit node=1 fast=False", StringComparison.Ordinal));
            simulation.RestartNode(4);
            simulation.RestartNode(5);
            using var restored = new CasPaxosMetaStoreFactory(simulation.CreateClient(5));
            var restoredStore = await restored.GetRegisterAsync(CasPaxosTestFixture.Cluster, "recovery", TestContext.Current.CancellationToken);
            using var current = Assert.IsType<MetaStoreEntry>(await restoredStore.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("recovered"u8.ToArray(), current.Value.ToArray());
            Assert.Equal(recovered, current.Version);
            Assert.NotEqual(initial.Version, current.Version);
            Assert.False((await restoredStore.WriteAsync(initial.Version, "stale"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
        });

    [Fact]
    public Task LostReplyThrowsUnknownAlthoughValueCommitted() =>
        CasPaxosTestFixture.RunAsync(816, async simulation =>
        {
            simulation.SetReplyLoss(1, true);
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "lost", TestContext.Current.CancellationToken);
            var started = simulation.TimeProvider.GetUtcNow();

            var error = await Assert.ThrowsAsync<IOException>(
                () => store.WriteAsync(null, "landed"u8.ToArray(), TestContext.Current.CancellationToken).AsTask());

            Assert.IsType<RegisterUnavailableException>(error.InnerException);
            Assert.Equal(started + simulation.OperationTimeout, simulation.TimeProvider.GetUtcNow());
            Assert.Single(simulation.Trace, static entry => entry.Contains("operation=compare-exchange", StringComparison.Ordinal));
            simulation.Heal();
            using var current = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("landed"u8.ToArray(), current.Value.ToArray());
            Assert.False((await store.WriteAsync(null, "replay"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());
        });

    [Fact]
    public Task SupersededLostReplyNeverBecomesMismatchOrFreshMutation() =>
        CasPaxosTestFixture.RunAsync(817, async simulation =>
        {
            simulation.SetReplyLoss(1, true);
            using var first = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            using var second = new CasPaxosMetaStoreFactory(simulation.CreateClient(2));
            var store = await first.GetRegisterAsync(CasPaxosTestFixture.Cluster, "lost", TestContext.Current.CancellationToken);
            var other = await second.GetRegisterAsync(CasPaxosTestFixture.Cluster, "lost", TestContext.Current.CancellationToken);
            var traceStart = simulation.Trace.Length;
            var lost = store.WriteAsync(null, "first"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();
            await CasPaxosTestFixture.WaitForTraceAsync(simulation, traceStart, "commit node=1");
            Assert.False(lost.IsCompleted);
            using var initial = Assert.IsType<MetaStoreEntry>(await other.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("first"u8.ToArray(), initial.Value.ToArray());
            Assert.True((await other.WriteAsync(initial.Version, "second"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied());

            var error = await Assert.ThrowsAsync<IOException>(() => lost);

            Assert.IsType<RegisterUnavailableException>(error.InnerException);
            simulation.Heal();
            using var current = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("second"u8.ToArray(), current.Value.ToArray());
            Assert.NotEqual(initial.Version, current.Version);
            Assert.Equal(2, simulation.Trace.Count(static entry => entry.Contains("operation=compare-exchange", StringComparison.Ordinal)));
        });

    [Fact]
    public Task CancellationAfterCommitPreservesStateAndCallerToken() =>
        CasPaxosTestFixture.RunAsync(818, async simulation =>
        {
            simulation.SetReplyLoss(1, true);
            using var cancellation = new CancellationTokenSource();
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "cancelled", TestContext.Current.CancellationToken);
            var start = simulation.Trace.Length;
            var pending = store.WriteAsync(null, "committed"u8.ToArray(), cancellation.Token).AsTask();
            await CasPaxosTestFixture.WaitForTraceAsync(simulation, start, "commit node=1");

            // Synchronous cancellation keeps its callback on Clockwork's sole owning thread.
#pragma warning disable CA1849 // CancelAsync dispatches callbacks to the thread pool, outside the simulation.
            cancellation.Cancel();
#pragma warning restore CA1849
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

            Assert.Equal(cancellation.Token, error.CancellationToken);
            simulation.Heal();
            using var current = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("committed"u8.ToArray(), current.Value.ToArray());
            Assert.Single(simulation.Trace, static entry => entry.Contains("operation=compare-exchange", StringComparison.Ordinal));
        });

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task ProductionAdapterReplaysIdenticalDelayedFaultedContinuationTrace()
    {
        var first = await ReplayAsync();
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var next = await ReplayAsync();
            Assert.Equal(first.Protocol, next.Protocol);
            Assert.Equal(first.Consumer, next.Consumer);
        }
    }

    private static async Task<(string[] Protocol, string[] Consumer)> ReplayAsync()
    {
        string[] protocol = [];
        var consumer = new List<string>();
        await CasPaxosTestFixture.RunAsync(819, async simulation =>
        {
            var context = SynchronizationContext.Current;
            Assert.NotNull(context);
            simulation.SetReplyDelay(1, TimeSpan.FromMilliseconds(25));
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var store = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "replay", TestContext.Current.CancellationToken);
            var attempts = Enumerable.Range(0, 8).Select(index => AttemptAsync(index)).ToArray();
            await simulation.DelayAsync(TimeSpan.FromMilliseconds(10));
            simulation.CrashNode(4);
            simulation.CrashNode(5);
            var results = await Task.WhenAll(attempts);
            Assert.Single(results, static result => result);
            simulation.RestartNode(4);
            simulation.RestartNode(5);
            using var current = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { (byte)Array.IndexOf(results, true) }, current.Value.ToArray());

            simulation.SetReplyLoss(1, true);
            var error = await Assert.ThrowsAsync<IOException>(
                () => store.WriteAsync(current.Version, "after"u8.ToArray(), TestContext.Current.CancellationToken).AsTask());
            Assert.IsType<RegisterUnavailableException>(error.InnerException);
            simulation.Heal();
            using var after = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("after"u8.ToArray(), after.Value.ToArray());
            Observe("healed");
            protocol = [.. simulation.Trace];

            async Task<bool> AttemptAsync(int index)
            {
                Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));
                Observe(string.Create(CultureInfo.InvariantCulture, $"read-{index}"));
                var applied = (await store.WriteAsync(null, [(byte)index], TestContext.Current.CancellationToken)).IsApplied();
                Observe(string.Create(CultureInfo.InvariantCulture, $"write-{index}-{applied}"));
                return applied;
            }

            // Each continuation runs on the simulation's logical thread, in its context: the kernel's operation worker,
            // which the thread that started the scenario joins, so the managed thread may differ from that thread's.
            void Observe(string stage)
            {
                Assert.True(simulation.IsSimulationThread, stage);
                Assert.Same(context, SynchronizationContext.Current);
                consumer.Add(string.Create(CultureInfo.InvariantCulture, $"{simulation.TimeProvider.GetTimestamp()} {stage}"));
            }
        });
        Assert.NotEmpty(protocol);
        Assert.Equal(17, consumer.Count);
        return (protocol, [.. consumer]);
    }
}
