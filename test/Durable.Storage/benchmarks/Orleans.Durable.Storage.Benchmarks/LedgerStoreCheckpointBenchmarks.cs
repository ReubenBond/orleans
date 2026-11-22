using System.Globalization;
using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures the LedgerStore's slot-table checkpoints over real WALs: the encoded checkpoint size per slot, and reopening a
/// replica from its checkpoint against replaying the whole WAL, for appends issued one at a time or in concurrent
/// windows.
/// </summary>
[MemoryDiagnoser]
public class LedgerStoreCheckpointBenchmarks : IAsyncDisposable
{
    private const int Slots = 32_768;

    private string _directory = null!;
    private string _source = null!;
    private LedgerStoreOptions _options = null!;
    private string _copy = null!;
    private LedgerStore? _opened;

    [Params(1, 16, 64)]
    public int Concurrency { get; set; }

    [Params(128, 4096)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clog-ledgerstore-checkpoint-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_directory, "source");
        _options = new LedgerStoreOptions { DirectoryPath = _source, FileSize = 64L << 20 };
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[PayloadSize]);
        using var payload = writer.PeekSlice(writer.Length);
        _ = Directory.CreateDirectory(_source);
        await using (var store = await LedgerStoreBenchmarkReplicas.OpenAsync(_options, create: true).ConfigureAwait(false))
        {
            _ = await store.CreateLiveAsync(LedgerStoreBenchmarkReplicas.Ledger, LedgerStoreBenchmarkReplicas.Window.WindowSize, CancellationToken.None).ConfigureAwait(false);
            var replica = store.OpenReplica(LedgerStoreBenchmarkReplicas.Ledger, LedgerStoreBenchmarkReplicas.Window);
            for (var first = 0L; first < Slots; first += Concurrency)
            {
                await WriteWindowAsync(replica, payload, first).ConfigureAwait(false);
            }

            await replica.CheckpointAsync().ConfigureAwait(false);
        }

        var checkpointBytes = Directory.EnumerateFiles(_source, "*.clsi", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
        var walBytes = Directory.EnumerateFiles(_source, "*.clfl", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"// LedgerStore checkpoint: concurrency {Concurrency}, payload {PayloadSize} B: {8.0 * checkpointBytes / Slots:F2} bits per slot ({checkpointBytes} checkpoint bytes, {walBytes} WAL bytes)."));
    }

    [IterationSetup]
    public void CopyStore()
    {
        _copy = Path.Combine(_directory, "copy-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_copy);
        foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Where(static file => Path.GetFileName(file) != "lock"))
        {
            var target = Path.Combine(_copy, Path.GetRelativePath(_source, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);

            // A restarted process finds its files durable; flushing here keeps the copy out of the measurement.
            using var handle = File.OpenHandle(target, FileMode.Open, FileAccess.ReadWrite);
            RandomAccess.FlushToDisk(handle);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<ReplicaTailRead> ReopenByFullScan()
    {
        // Without a root, recovery replays the whole WAL.
        File.Delete(Path.Combine(_copy, "slots", "0", "slots.root"));
        return await ReopenAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public Task<ReplicaTailRead> ReopenFromCheckpoint() => ReopenAsync();

    [IterationCleanup]
    public void DeleteCopy()
    {
        _opened?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _opened = null;
        Directory.Delete(_copy, recursive: true);
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_opened is not null)
        {
            await _opened.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteWindowAsync(LedgerStoreReplica replica, ArcBuffer payload, long first)
    {
        var writes = new Task<ReplicaWriteOutcome>[Concurrency];
        for (var index = 0; index < writes.Length; index++)
        {
            writes[index] = replica.WriteAsync(new Slot(first + index), payload, Slot.Zero, recovery: false).AsTask();
        }

        if ((await Task.WhenAll(writes).ConfigureAwait(false)).Any(static result => result.Value is not ReplicaWriteOutcome.Written))
        {
            throw new InvalidOperationException("The benchmark failed to append its window.");
        }
    }

    private async Task<ReplicaTailRead> ReopenAsync()
    {
        _opened = await LedgerStoreBenchmarkReplicas.OpenAsync(_options with { DirectoryPath = _copy }, create: false).ConfigureAwait(false);
        await using var replica = _opened.OpenReplica(LedgerStoreBenchmarkReplicas.Ledger, LedgerStoreBenchmarkReplicas.Window);
        return await replica.ReadTailAsync(Slot.Zero, seal: false).ConfigureAwait(false);
    }
}
