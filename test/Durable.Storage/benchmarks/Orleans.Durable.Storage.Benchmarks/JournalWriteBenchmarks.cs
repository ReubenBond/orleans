using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures <see cref="JournalStore"/> write latency and throughput (<c>Append</c> 1 KiB &amp; 64 KiB, <c>Replace</c> 1 MiB)
/// over a 3-replica (<c>N = 3, Q = 2</c>) <see cref="Ledger"/>, comparing in-memory replicas (pure admission, batching,
/// and fold CPU) against three local WAL-backed <see cref="LedgerStore"/> replicas (end-to-end quorum durability).
/// </summary>
[MemoryDiagnoser]
public class JournalWriteBenchmarks : IAsyncDisposable
{
    private const int Append1KiBCount = 32;
    private const int Append64KiBCount = 16;
    private const int Replace1MiBCount = 4;

    private static readonly ImmutableArray<NodeId> Nodes = [MakeNodeId(1), MakeNodeId(2), MakeNodeId(3)];

    private string? _rootDirectory;
    private LocalQuorumReplicaTransport? _walTransport;
    private NullDiskLedgerReplicaTransport? _memoryTransport;
    private LedgerMapCommitter? _committer;
    private LedgerReader? _reader;
    private LedgerWriterRegistry? _registry;
    private InMemoryArchiveStore? _archive;
    private JournalStore? _journals;
    private ArcBuffer _payload1KiB;
    private ArcBuffer _payload64KiB;
    private ArcBuffer _payload1MiB;
    private JournalRowKey _key1KiB = null!;
    private JournalRowKey _key64KiB = null!;
    private JournalRowKey _key1MiB = null!;
    private JournalVersion _version1KiB;
    private JournalVersion _version64KiB;
    private JournalVersion _version1MiB;

    [Params(false, true)]
    public bool RealWal { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        try
        {
            ILedgerReplicaTransport transport;
            if (RealWal)
            {
                _rootDirectory = Path.Combine(Path.GetTempPath(), $"clog-journal-write-bench-{Guid.NewGuid():N}");
                _ = Directory.CreateDirectory(_rootDirectory);
                _walTransport = await LocalQuorumReplicaTransport.OpenAsync(_rootDirectory, Nodes, walFileSize: 128L << 20).ConfigureAwait(false);
                transport = _walTransport;
            }
            else
            {
                _memoryTransport = new NullDiskLedgerReplicaTransport();
                transport = _memoryTransport;
            }

            var register = new InMemoryConditionalRegister();
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
            _committer = new LedgerMapCommitter(register, int.MaxValue);
            _reader = new LedgerReader(map, transport, TimeProvider.System, Nodes[0]);
            var writerOptions = new LedgerWriterOptions(
                maxSegmentBytes: 1L << 30,
                maxSegmentKeys: int.MaxValue,
                maxSegmentAge: TimeSpan.FromHours(1),
                maxProposalBytes: 32L << 20);
            _registry = new LedgerWriterRegistry(_reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
            var executor = new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance);
            var choice = new TransitionChoice(Nodes[0], Nodes, 64, 0, map.Current.Epoch);
            var bootstrap = await executor.ExecuteAsync(new Transition.Bootstrap(choice), _committer.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (bootstrap.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Bootstrap failed: {bootstrap.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

            var journalOptions = new JournalStoreOptions
            {
                MaxBodyBytes = 16 << 20,
                MaxQueuedBytes = 128 << 20,
                WarmStateBytes = 128 << 20,
            };
            _archive = new InMemoryArchiveStore();
            _journals = new JournalStore(_registry, _archive, journalOptions, TimeProvider.System);

            _payload1KiB = MakeBuffer(1024);
            _payload64KiB = MakeBuffer(64 * 1024);
            _payload1MiB = MakeBuffer(1024 * 1024);
            _key1KiB = JournalRowKey.Encode("bench-journal-1k");
            _key64KiB = JournalRowKey.Encode("bench-journal-64k");
            _key1MiB = JournalRowKey.Encode("bench-journal-1m");

            // Pre-warm each journal with an initial Replace so writes measure steady-state warm admission & replication.
            _version1KiB = await WriteReplaceAsync(_key1KiB, JournalVersion.Origin, _payload1KiB).ConfigureAwait(false);
            _version64KiB = await WriteReplaceAsync(_key64KiB, JournalVersion.Origin, _payload64KiB).ConfigureAwait(false);
            _version1MiB = await WriteReplaceAsync(_key1MiB, JournalVersion.Origin, _payload1MiB).ConfigureAwait(false);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    [IterationCleanup(Target = nameof(Append1KiB))]
    public void ResetAppend1KiB() =>
        _version1KiB = WriteReplaceAsync(_key1KiB, _version1KiB, _payload1KiB).AsTask().GetAwaiter().GetResult();

    [IterationCleanup(Target = nameof(Append64KiB))]
    public void ResetAppend64KiB() =>
        _version64KiB = WriteReplaceAsync(_key64KiB, _version64KiB, _payload64KiB).AsTask().GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = Append1KiBCount)]
    public async Task Append1KiB()
    {
        for (var i = 0; i < Append1KiBCount; i++)
        {
            _version1KiB = await WriteAppendAsync(_key1KiB, _version1KiB, _payload1KiB).ConfigureAwait(false);
        }
    }

    [Benchmark(OperationsPerInvoke = Append64KiBCount)]
    public async Task Append64KiB()
    {
        for (var i = 0; i < Append64KiBCount; i++)
        {
            _version64KiB = await WriteAppendAsync(_key64KiB, _version64KiB, _payload64KiB).ConfigureAwait(false);
        }
    }

    [Benchmark(OperationsPerInvoke = Replace1MiBCount)]
    public async Task Replace1MiB()
    {
        for (var i = 0; i < Replace1MiBCount; i++)
        {
            _version1MiB = await WriteReplaceAsync(_key1MiB, _version1MiB, _payload1MiB).ConfigureAwait(false);
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _payload1KiB.Dispose();
        _payload64KiB.Dispose();
        _payload1MiB.Dispose();
        if (_journals is not null)
        {
            await _journals.DisposeAsync().ConfigureAwait(false);
            _journals = null;
        }

        _archive?.Dispose();
        _archive = null;

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
            _registry = null;
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
        }

        if (_committer is not null)
        {
            await _committer.DisposeAsync().ConfigureAwait(false);
            _committer = null;
        }

        if (_walTransport is not null)
        {
            await _walTransport.DisposeAsync().ConfigureAwait(false);
            _walTransport = null;
        }

        _memoryTransport?.Dispose();
        _memoryTransport = null;

        if (_rootDirectory is not null && Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
            _rootDirectory = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask<JournalVersion> WriteAppendAsync(JournalRowKey key, JournalVersion expected, ArcBuffer payload)
    {
        using var record = _journals!.EncodeAppend(key, expected, payload);
        var outcome = await _journals.WriteAsync(record).ConfigureAwait(false);
        return outcome.Value is JournalWriteOutcome.Accepted accepted
            ? accepted.Version
            : throw new InvalidOperationException($"Unexpected append outcome: {outcome.Value}");
    }

    private async ValueTask<JournalVersion> WriteReplaceAsync(JournalRowKey key, JournalVersion expected, ArcBuffer payload)
    {
        using var record = _journals!.EncodeReplace(key, expected, payload);
        var outcome = await _journals.WriteAsync(record).ConfigureAwait(false);
        return outcome.Value is JournalWriteOutcome.Accepted accepted
            ? accepted.Version
            : throw new InvalidOperationException($"Unexpected replace outcome: {outcome.Value}");
    }

    internal static NodeId MakeNodeId(int index) =>
        new(new Guid(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

    internal static ArcBuffer MakeBuffer(int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer.PeekSlice(writer.Length);
    }
}

/// <summary>
/// Direct in-process <see cref="ILedgerReplicaTransport"/> backed by real local <see cref="LedgerStore"/> instances
/// (one WAL root per replica node), isolating local quorum WAL durability without Orleans transport overhead.
/// </summary>
internal sealed class LocalQuorumReplicaTransport : ILedgerReplicaTransport, IAsyncDisposable
{
    private static readonly Guid BenchmarkCluster = new("11223344-5566-7788-99aa-bbccddeeff00");

    private readonly Dictionary<NodeId, LedgerStore> _stores;

    private LocalQuorumReplicaTransport(Dictionary<NodeId, LedgerStore> stores) => _stores = stores;

    public static async Task<LocalQuorumReplicaTransport> OpenAsync(string rootDirectory, IReadOnlyList<NodeId> nodes, long walFileSize = 256L << 20)
    {
        var stores = new Dictionary<NodeId, LedgerStore>(nodes.Count);
        try
        {
            foreach (var node in nodes)
            {
                var nodeDir = Path.Combine(rootDirectory, $"replica-{node.Value}");
                _ = Directory.CreateDirectory(nodeDir);
                var options = new LedgerStoreOptions
                {
                    DirectoryPath = nodeDir,
                    FileSize = walFileSize,
                    PreallocateFiles = false,
                };
                var store = await LedgerStore.OpenAsync(
                    new PortableWalStorage(),
                    options,
                    BenchmarkCluster,
                    create: true,
                    new LedgerStoreCheckpointOptions(),
                    TimeProvider.System).ConfigureAwait(false);
                stores[node] = store;
            }

            return new LocalQuorumReplicaTransport(stores);
        }
        catch
        {
            foreach (var store in stores.Values)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
        _stores[node].OpenReplica(ledger, options);

    public async ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
        new(await _stores[node].HeartbeatReplicasAsync(ledgers, cancellationToken).ConfigureAwait(false));

    public ValueTask DropAsync(NodeId node, LedgerId ledger, CancellationToken cancellationToken = default) =>
        _stores[node].DropReplicaAsync(ledger, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores.Values)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }

        _stores.Clear();
    }
}

/// <summary>
/// Zero-copy, bounded-ring in-memory <see cref="ILedgerReplicaTransport"/> that isolates Clog's CPU and allocation
/// overhead from disk I/O without retaining gigabytes of old payloads in RAM or copying payloads into LOH arrays.
/// </summary>
internal sealed class NullDiskLedgerReplicaTransport : ILedgerReplicaTransport, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(NodeId Node, LedgerId Ledger), RingReplicaState> _states = [];
    private bool _disposed;

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_states.TryGetValue((node, ledger), out var state))
            {
                state = new RingReplicaState();
                _states.Add((node, ledger), state);
            }

            return new RingReplicaFacade(state);
        }
    }

    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ReplicaHeartbeatRead(new ReplicaHeartbeatReply(TimeSpan.Zero, new ReplicaHeartbeatStatus[ledgers.Length])));

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var state in _states.Values)
            {
                state.Dispose();
            }

            _states.Clear();
        }
    }

    private sealed class RingReplicaState : IDisposable
    {
        private const int RingSize = 256;
        private readonly Lock _lock = new();
        private readonly long[] _slots = new long[RingSize];
        private readonly ArcBuffer[] _buffers = new ArcBuffer[RingSize];
        private readonly bool[] _occupied = new bool[RingSize];
        private long _contiguousTail;
        private bool _sealed;
        private bool _disposed;

        public ReplicaWriteOutcome Write(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (contiguousTail.Value > _contiguousTail)
                {
                    _contiguousTail = contiguousTail.Value;
                }

                if (!recovery && _sealed)
                {
                    return ReplicaWriteOutcome.Sealed.Instance;
                }

                var idx = (int)((ulong)slot.Value % RingSize);
                if (_occupied[idx] && _slots[idx] == slot.Value)
                {
                    return ReplicaWriteOutcome.Written.Instance;
                }

                if (_occupied[idx])
                {
                    _buffers[idx].Dispose();
                }

                _slots[idx] = slot.Value;
                _buffers[idx] = payload.Retain();
                _occupied[idx] = true;
                return ReplicaWriteOutcome.Written.Instance;
            }
        }

        public ReplicaRead Read(Slot slot, Slot contiguousTail)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (contiguousTail.Value > _contiguousTail)
                {
                    _contiguousTail = contiguousTail.Value;
                }

                var idx = (int)((ulong)slot.Value % RingSize);
                if (_occupied[idx] && _slots[idx] == slot.Value)
                {
                    return _buffers[idx].Retain();
                }

                return ReplicaRead.Missing.Instance;
            }
        }

        public ReplicaRead ReadRange(Slot slot, int offset, int length, Slot contiguousTail)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (contiguousTail.Value > _contiguousTail)
                {
                    _contiguousTail = contiguousTail.Value;
                }

                var idx = (int)((ulong)slot.Value % RingSize);
                if (!_occupied[idx] || _slots[idx] != slot.Value)
                {
                    return ReplicaRead.Missing.Instance;
                }

                var buf = _buffers[idx];
                if ((long)offset + length > buf.Length)
                {
                    return new ReplicaRead.OutsideBatch(buf.Length);
                }

                return buf.Slice(offset, length);
            }
        }

        public ReplicaTailRead ReadTail(Slot contiguousTail, bool seal)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (contiguousTail.Value > _contiguousTail)
                {
                    _contiguousTail = contiguousTail.Value;
                }

                _sealed |= seal;
                return new LedgerReplicaTail(new Slot(_contiguousTail), _sealed);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                for (var i = 0; i < RingSize; i++)
                {
                    if (_occupied[i])
                    {
                        _buffers[i].Dispose();
                        _buffers[i] = default;
                        _occupied[i] = false;
                    }
                }
            }
        }
    }

    private sealed class RingReplicaFacade(RingReplicaState state) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state.Write(slot, payload, contiguousTail, recovery));
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state.Read(slot, contiguousTail));
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state.ReadRange(slot, offset, length, contiguousTail));
        }

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state.ReadTail(contiguousTail, seal));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
