using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures per-entry write latency, throughput, and allocations across window sizes <c>K</c> and saturating
/// concurrent load for both per-slot and append object-ledger layouts (object-ledgers.md §8.6, §10).
/// </summary>
[MemoryDiagnoser]
public class ObjectLedgerWriteBenchmarks : IAsyncDisposable
{
    private const int EntriesPerInvoke = 64;

    private DiscardObjectStore _store = null!;
    private ObjectLedgerStore _ledgers = null!;
    private Ledger? _ledger;
    private LedgerWriter? _writer;
    private Task<int>[] _workers = null!;
    private ArcBuffer _payload;
    private long _nextLedgerId;
    private int _remaining;

    /// <summary>A (window size, concurrent writer load) configuration pair.</summary>
    public readonly record struct WriteShape(int Window, int Load)
    {
        public override string ToString() => $"K={Window},L={Load}";
    }

    /// <summary>Gets or sets the object-ledger layout.</summary>
    [Params(ObjectLedgerLayout.PerSlot, ObjectLedgerLayout.Append)]
    public ObjectLedgerLayout Layout { get; set; } = ObjectLedgerLayout.PerSlot;

    /// <summary>Gets or sets the window size <c>K</c> and concurrent writer load.</summary>
    [ParamsSource(nameof(Shapes))]
    public WriteShape Shape { get; set; } = new(1, 1);

    /// <summary>Returns non-degenerate (window, load) configurations covering single-writer and saturated windows.</summary>
    public static IEnumerable<WriteShape> Shapes =>
    [
        new(1, 1),
        new(1, 4),
        new(2, 4),
        new(4, 8),
    ];

    [GlobalSetup]
    public void Setup()
    {
        _store = new DiscardObjectStore(yieldOnWrite: true);
        var options = new ObjectLedgerStoreOptions { Layout = Layout };
        _ledgers = Layout == ObjectLedgerLayout.Append
            ? ObjectLedgerStore.OverAppendable(_store, options, TimeProvider.System)
            : ObjectLedgerStore.Over(_store, options, TimeProvider.System);
        _workers = new Task<int>[Shape.Load];
        var bytes = new byte[512];
        bytes.AsSpan().Fill(0x3C);
        _payload = ArcBuffer.Wrap(bytes);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _store.Clear();
        var ledgerId = new LedgerId((ulong)Interlocked.Increment(ref _nextLedgerId));
        _ledger = new Ledger(ledgerId, _ledgers.Open(ledgerId, Shape.Window), LedgerLocation.ObjectStore.Instance);
        _writer = _ledger.CreateWriter(TimeProvider.System);
    }

    [Benchmark(OperationsPerInvoke = EntriesPerInvoke)]
    public async Task<int> WriteEntriesAsync()
    {
        _remaining = EntriesPerInvoke;
        for (var worker = 0; worker < _workers.Length; worker++)
        {
            _workers[worker] = RunWorkerAsync();
        }

        var counts = await Task.WhenAll(_workers).ConfigureAwait(false);
        var total = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            total += counts[i];
        }

        if (total != EntriesPerInvoke)
        {
            throw new InvalidOperationException($"Expected {EntriesPerInvoke} appended entries, got {total}.");
        }

        return total;
    }

    private async Task<int> RunWorkerAsync()
    {
        var writer = _writer ?? throw new InvalidOperationException("Writer was not initialized in IterationSetup.");
        var completed = 0;
        while (Interlocked.Decrement(ref _remaining) >= 0)
        {
            var slot = await writer.AcquireAsync().ConfigureAwait(false);
            if (slot is not { } acquired)
            {
                throw new InvalidOperationException("Writer stopped unexpectedly during write benchmark.");
            }

            var outcome = await writer.AppendAsync(acquired, _payload).ConfigureAwait(false);
            if (outcome is not AppendOutcome.Appended)
            {
                throw new InvalidOperationException($"Expected Appended outcome, got {outcome}.");
            }

            completed++;
        }

        return completed;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_writer is { } writer)
        {
            _writer = null;
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_ledger is { } ledger)
        {
            _ledger = null;
            ledger.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _store.Clear();
    }

    [GlobalCleanup]
    public Task CleanupAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        if (_ledger is not null)
        {
            await _ledger.DisposeAsync().ConfigureAwait(false);
            _ledger = null;
        }

        _store?.Clear();
        _payload.Dispose();
        _payload = default;
    }
}
