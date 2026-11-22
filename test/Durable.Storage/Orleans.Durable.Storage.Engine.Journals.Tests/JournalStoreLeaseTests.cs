using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Verifies that the L2 ledger-replica quorum lease eliminates replica <c>ReadTailAsync</c> round-trips on both
/// <see cref="JournalStore.ReadAsync"/> and cold-key first writes (<c>JournalStore.LoadAsync</c>) while the
/// primary's lease is valid, and falls back seamlessly to quorum checks on expiry or seal (system-model.md §2,
/// journals.md §8, LP-I17).
/// </summary>
public sealed class JournalStoreLeaseTests : IDisposable
{
    private static readonly NodeId PrimaryNode = new(new Guid("00000000-0000-0000-0000-00000000000a"));
    private static readonly NodeId Peer1 = new(new Guid("00000000-0000-0000-0000-00000000000b"));
    private static readonly NodeId Peer2 = new(new Guid("00000000-0000-0000-0000-00000000000c"));
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GuardBand = TimeSpan.FromMilliseconds(500);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LP-I17: once a primary has an active ledger lease, both <see cref="JournalStore.ReadAsync"/> and cold-key first
    /// writes (which load the key via <c>LoadAsync</c>) complete with zero replica <c>ReadTailAsync</c> RPCs; once the
    /// lease enters its guard band without renewal, reads and cold loads fall back to quorum tail checks.
    /// </summary>
    [Fact]
    public async Task LeasedPrimaryServesReadsAndColdKeyFirstWritesWithZeroTailRpcs()
    {
        var clock = new FakeTimeProvider();
        using var backing = new InMemoryLedgerReplicaTransport(clock, LeaseDuration);
        var transport = new CountingTailTransport(backing);
        var register = new InMemoryConditionalRegister();
        var map = await LedgerMapView.OpenAsync(register, Token);
        var leaseOptions = new LedgerLeaseOptions
        {
            LeaseDuration = LeaseDuration,
            ClockDriftFactor = 1.25,
            LeaseGuardBand = GuardBand,
        };

        await using var reader = new LedgerReader(map, transport, clock, PrimaryNode) { LeaseOptions = leaseOptions };
        await using var registry = new LedgerWriterRegistry(reader, 32 << 20, LedgerWriterOptions.Default, clock, NullLoggerFactory.Instance, new Random(17));
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
        await using var committer = new LedgerMapCommitter(register, 32 << 20);
        using var archive = new InMemoryArchiveStore();

        var choice = new TransitionChoice(PrimaryNode, new LedgerLocation.Nodes([PrimaryNode, Peer1, Peer2]), k: 4, chosenAt: 0, epoch: 0);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await executor.ExecuteAsync(new Transition.Bootstrap(choice), committer.CommitAsync, Token)).Value);

        // Zero warm-state budget forces every cold-key touch to run JournalStore.LoadAsync.
        await using var store = new JournalStore(registry, archive, new JournalStoreOptions { WarmStateBytes = 0 }, clock);

        // First write establishes the quorum lease (localLeaseUntil = t0 + 3s / 1.25 = t0 + 2.4s).
        var v1 = await AppendAsync(store, "seed-key", JournalVersion.Origin, "v1");
        Assert.NotEqual(JournalVersion.Origin, v1);

        var tailCallsAfterSeed = transport.ReadTailCalls;

        // 1. Read on existing key -> 0 ReadTailAsync RPCs.
        Assert.Equal((v1, true, "v1"), await ReadAsync(store, "seed-key"));
        Assert.Equal(tailCallsAfterSeed, transport.ReadTailCalls);

        // 2. Cold-key first writes (LoadAsync + ProposeAsync) -> 0 ReadTailAsync RPCs!
        var cold1 = await AppendAsync(store, "cold-key-1", JournalVersion.Origin, "hello");
        var cold2 = await AppendAsync(store, "cold-key-2", JournalVersion.Origin, "world");
        Assert.Equal(tailCallsAfterSeed, transport.ReadTailCalls);

        // 3. Read on cold/evicted keys -> still 0 ReadTailAsync RPCs.
        Assert.Equal((cold1, true, "hello"), await ReadAsync(store, "cold-key-1"));
        Assert.Equal((cold2, true, "world"), await ReadAsync(store, "cold-key-2"));
        Assert.Equal((JournalVersion.Origin, false, string.Empty), await ReadAsync(store, "never-written"));
        Assert.Equal(tailCallsAfterSeed, transport.ReadTailCalls);

        // 4. Advance past localLeaseUntil - guardBand while suppressing heartbeat grants -> fallback to quorum ReadTailAsync.
        transport.DropHeartbeats = true;
        clock.Advance(TimeSpan.FromMilliseconds(2_000));
        var tailCallsBeforeExpiryRead = transport.ReadTailCalls;
        Assert.Equal((cold1, true, "hello"), await ReadAsync(store, "cold-key-1"));
        Assert.True(transport.ReadTailCalls > tailCallsBeforeExpiryRead);
    }

    private static async Task<JournalVersion> AppendAsync(JournalStore store, string key, JournalVersion expected, string body)
    {
        using var payload = ArcBuffer.Wrap(Encoding.UTF8.GetBytes(body));
        using var record = store.EncodeAppend(JournalRowKey.Encode(key), expected, payload);
        var outcome = await store.WriteAsync(record, Token);
        return Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
    }

    private static async Task<(JournalVersion Version, bool Exists, string Content)> ReadAsync(JournalStore store, string key)
    {
        var outcome = await store.ReadAsync(JournalRowKey.Encode(key), Token);
        using var result = Assert.IsType<JournalReadResult>(outcome.Value);
        List<byte> bytes = [];
        await foreach (var chunk in result.ReadPayloadsAsync(Token))
        {
            using (chunk)
            {
                bytes.AddRange(chunk.ToArray());
            }
        }

        return (result.CurrentVersion, result.Exists, Encoding.UTF8.GetString([.. bytes]));
    }

    private sealed class CountingTailTransport(ILedgerReplicaTransport inner) : ILedgerReplicaTransport
    {
        private int _readTailCalls;

        public int ReadTailCalls => Volatile.Read(ref _readTailCalls);

        public bool DropHeartbeats { get; set; }

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            new CountingReplica(inner.Open(node, ledger, options), this);

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            DropHeartbeats
                ? new ValueTask<ReplicaHeartbeatRead>(ReplicaWriteOutcome.Backpressure.Instance)
                : inner.HeartbeatAsync(node, ledgers, cancellationToken);

        private sealed class CountingReplica(ILedgerReplica inner, CountingTailTransport parent) : ILedgerReplica
        {
            public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
                inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

            public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(slot, contiguousTail, cancellationToken);

            public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
            {
                _ = Interlocked.Increment(ref parent._readTailCalls);
                return inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
