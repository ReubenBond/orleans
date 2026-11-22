using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures durable appends to the LedgerStore replica, one at a time and in K=64 windows.
/// </summary>
[MemoryDiagnoser]
public class LedgerStoreReplicaWriteBenchmarks : IAsyncDisposable
{
    private const int Operations = 512;

    private LedgerStoreBenchmarkReplicas _replicas = null!;

    [Params(128, 4096, 65536)]
    public int PayloadSize { get; set; }

    [IterationSetup]
    public void Setup() => _replicas = LedgerStoreBenchmarkReplicas.CreateAsync(PayloadSize).AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    public Task LedgerStoreWindow() => _replicas.WriteWindowsAsync(_replicas.Store, Operations);

    [Benchmark(OperationsPerInvoke = 64)]
    public Task LedgerStoreSequential() => _replicas.WriteSequentialAsync(_replicas.Store, 64);

    [IterationCleanup]
    public void Cleanup() => _replicas.DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _replicas?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Measures point reads and reopening, which replays the WAL.
/// </summary>
[MemoryDiagnoser]
public class LedgerStoreReplicaReadBenchmarks : IAsyncDisposable
{
    private const int Slots = 1024;

    private LedgerStoreBenchmarkReplicas _replicas = null!;
    private long _next;

    [GlobalSetup]
    public async Task Setup()
    {
        _replicas = await LedgerStoreBenchmarkReplicas.CreateAsync(4096).ConfigureAwait(false);
        await _replicas.WriteWindowsAsync(_replicas.Store, Slots).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> LedgerStorePointRead() => ReadAsync(_replicas.Store, _next++ % Slots);

    [Benchmark]
    public async ValueTask<ReplicaTailRead> LedgerStoreReopen()
    {
        await using var store = await LedgerStoreBenchmarkReplicas.OpenAsync(_replicas.StoreOptions with { DirectoryPath = _replicas.CopyStore() }, create: false).ConfigureAwait(false);
        await using var replica = store.OpenReplica(LedgerStoreBenchmarkReplicas.Ledger, LedgerStoreBenchmarkReplicas.Window);
        return await replica.ReadTailAsync(Slot.Zero, seal: false).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public Task Cleanup() => _replicas.DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        await Cleanup().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static async ValueTask<int> ReadAsync(ILedgerReplica replica, long address)
    {
        var result = await replica.ReadAsync(new Slot(address), Slot.Zero).ConfigureAwait(false);
        if (!result.TryGetValue(out ArcBuffer payload))
        {
            throw new InvalidOperationException("The seeded benchmark slot is missing.");
        }

        using (payload)
        {
            return payload.Length;
        }
    }
}

internal sealed class LedgerStoreBenchmarkReplicas : IAsyncDisposable
{
    internal static readonly LedgerId Ledger = new(1);

    internal static readonly Guid Cluster = new("00112233-4455-6677-8899-aabbccddeeff");

    internal static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    private readonly string _directory;
    private readonly LedgerStore _store;
    private readonly ArcBuffer _payload;
    private readonly Dictionary<ILedgerReplica, long> _next = [with(ReferenceEqualityComparer.Instance)];

    private LedgerStoreBenchmarkReplicas(string directory, int payloadSize, LedgerStore store, LedgerStoreOptions storeOptions)
    {
        _directory = directory;
        _store = store;
        Store = store.OpenReplica(Ledger, Window);
        StoreOptions = storeOptions;
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[payloadSize]);
        _payload = writer.PeekSlice(writer.Length);
    }

    internal ILedgerReplica Store { get; }

    internal LedgerStoreOptions StoreOptions { get; }

    internal static async ValueTask<LedgerStoreBenchmarkReplicas> CreateAsync(int payloadSize)
    {
        var directory = Path.Combine(Path.GetTempPath(), "clog-ledgerstore-benchmark-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        var storeOptions = new LedgerStoreOptions { DirectoryPath = Path.Combine(directory, "ledgerstore"), FileSize = 16777216 };
        _ = Directory.CreateDirectory(storeOptions.DirectoryPath);
        var store = await OpenAsync(storeOptions, create: true).ConfigureAwait(false);
        _ = await store.CreateLiveAsync(Ledger, Window.WindowSize, CancellationToken.None).ConfigureAwait(false);
        return new LedgerStoreBenchmarkReplicas(directory, payloadSize, store, storeOptions);
    }

    internal static ValueTask<LedgerStore> OpenAsync(LedgerStoreOptions options, bool create) =>
        LedgerStore.OpenAsync(new PortableWalStorage(), options, Cluster, create, new LedgerStoreCheckpointOptions(), TimeProvider.System);

    /// <summary>
    /// Appends in windows of 64 concurrent writes, as a ledger's writer does with the default K.
    /// </summary>
    internal async Task WriteWindowsAsync(ILedgerReplica replica, int count)
    {
        var first = Next(replica, count);
        for (var start = first; start < first + count; start += 64)
        {
            var writes = new Task<ReplicaWriteOutcome>[64];
            for (var index = 0; index < writes.Length; index++)
            {
                writes[index] = replica.WriteAsync(new Slot(start + index), _payload, Slot.Zero, recovery: false).AsTask();
            }

            if ((await Task.WhenAll(writes).ConfigureAwait(false)).Any(static result => result.Value is not ReplicaWriteOutcome.Written))
            {
                throw new InvalidOperationException("The benchmark failed to append its complete window.");
            }
        }
    }

    /// <summary>
    /// Appends one write at a time, measuring the durable latency of a single append.
    /// </summary>
    internal async Task WriteSequentialAsync(ILedgerReplica replica, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if ((await replica.WriteAsync(new Slot(Next(replica, 1)), _payload, Slot.Zero, recovery: false).ConfigureAwait(false)).Value is not ReplicaWriteOutcome.Written)
            {
                throw new InvalidOperationException("The benchmark failed to append.");
            }
        }
    }

    private long Next(ILedgerReplica replica, int count)
    {
        var first = _next.GetValueOrDefault(replica);
        _next[replica] = first + count;
        return first;
    }

    /// <summary>
    /// Copies the store's cluster claim, node file, WAL, and catalog checkpoints without its slot-index checkpoints, so
    /// each reopen replays the same whole log without the open store's ownership.
    /// </summary>
    internal string CopyStore()
    {
        var copy = Path.Combine(_directory, "store-" + Guid.NewGuid().ToString("N"));
        var wal = Path.Combine(copy, LedgerStore.WalDirectoryName);
        _ = Directory.CreateDirectory(Path.Combine(copy, LedgerStore.SlotsDirectoryName));
        _ = Directory.CreateDirectory(wal);
        File.Copy(Path.Combine(StoreOptions.DirectoryPath, "cluster"), Path.Combine(copy, "cluster"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(StoreOptions.DirectoryPath, LedgerStore.WalDirectoryName)).Where(static file => IsStoreFile(Path.GetFileName(file))))
        {
            File.Copy(file, Path.Combine(wal, Path.GetFileName(file)));
        }

        return copy;

        static bool IsStoreFile(string name) =>
            name == NodeFile.Name
            || name.EndsWith(".clfl", StringComparison.Ordinal)
            || name.EndsWith(CatalogCheckpoints.Extension, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync().ConfigureAwait(false);
        _payload.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
