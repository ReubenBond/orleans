using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures a read below the known contiguous tail of a ledger of three replicas whose first replica, the reader's own,
/// holds the batch and answers synchronously, as a cached read does, or after yielding, as a disk read does: its time and
/// allocations, the replicas' own included (ledger-protocol.md §8.3). The baseline reads at the tail, which asks every
/// replica, as every read did before reads below the tail asked one replica at a time. Each read runs under no token or
/// under a cancellable one, as a caller's is in production, and the concurrent row runs eight readers of one ledger at
/// once, so a cost shared by the ledger's reads, such as a registration on one token, shows as contention.
/// </summary>
[MemoryDiagnoser]
public class LedgerQuorumReadBenchmarks : IAsyncDisposable
{
    private static readonly Slot Below = new(3);
    private static readonly Slot Tail = new(5);
    private const int ConcurrentReaders = 8;
    private const int ReadsPerReader = 256;

    private ArcBufferWriter _writer = null!;
    private ArcBuffer _batch;
    private LedgerQuorum _quorum = null!;
    private CancellationTokenSource _caller = null!;

    [Params(false, true)]
    public bool Asynchronous { get; set; }

    [Params(false, true)]
    public bool Cancellable { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _writer = new ArcBufferWriter();
        _writer.Write(new byte[1024]);
        _batch = _writer.PeekSlice(_writer.Length);
        _caller = new CancellationTokenSource();
        _quorum = new LedgerQuorum(
            Enumerable.Range(0, 3).Select(_ => new HoldingReplica(_batch, Asynchronous)),
            quorum: 2,
            new LedgerReplicaOptions { WindowSize = 64 },
            TimeProvider.System,
            preferredReplica: 0);
    }

    [Benchmark(Baseline = true)]
    public Task<int> UndecidedRead() => ReadAsync(Tail);

    [Benchmark]
    public Task<int> DecidedRead() => ReadAsync(Below);

    [Benchmark(OperationsPerInvoke = ConcurrentReaders * ReadsPerReader)]
    public Task ConcurrentDecidedReads() =>
        Task.WhenAll(Enumerable.Range(0, ConcurrentReaders).Select(_ => Task.Run(async () =>
        {
            for (var read = 0; read < ReadsPerReader; read++)
            {
                _ = await ReadAsync(Below).ConfigureAwait(false);
            }
        })));

    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_quorum is not null)
        {
            await _quorum.DisposeAsync().ConfigureAwait(false);
            _quorum = null!;
        }

        _batch.Dispose();
        _caller?.Dispose();
        _writer?.Dispose();
        _writer = null!;
    }

    private async Task<int> ReadAsync(Slot slot)
    {
        var read = await _quorum.ReadAsync(slot, Tail, Cancellable ? _caller.Token : CancellationToken.None).ConfigureAwait(false);
        _ = read.TryGetValue(out ArcBuffer batch);
        using (batch)
        {
            return batch.Length;
        }
    }

    /// <summary>A replica which holds every slot's batch, a slice of one buffer, and answers at once or after yielding.</summary>
    private sealed class HoldingReplica(ArcBuffer batch, bool asynchronous) : ILedgerReplica
    {
        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            asynchronous ? ReadLaterAsync() : ValueTask.FromResult(new ReplicaRead(batch.Slice(0, batch.Length)));

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async ValueTask<ReplicaRead> ReadLaterAsync()
        {
            await Task.Yield();
            return new ReplicaRead(batch.Slice(0, batch.Length));
        }
    }
}
