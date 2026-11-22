using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures the append-layout coalescer's allocations and throughput when batching concurrent slot writes into one
/// <c>CLOE</c> append per wave (object-ledgers.md §8.1, §10).
/// </summary>
[MemoryDiagnoser]
public class ObjectLedgerCoalescerBenchmarks : IDisposable
{
    private const int RoundsPerInvoke = 64;

    private DiscardObjectStore _store = null!;
    private ObjectLedgerStore _ledgers = null!;
    private Task<LedgerWriteOutcome>[] _tasks = null!;
    private ArcBuffer _payload;
    private long _nextLedgerId;

    /// <summary>Gets or sets the number of concurrent slot writes submitted per coalescing wave.</summary>
    [Params(1, 4, 16)]
    public int ConcurrentAppends { get; set; } = 1;

    /// <summary>Gets or sets each slot's payload length in bytes.</summary>
    [Params(256, 4096)]
    public int PayloadLength { get; set; } = 256;

    [GlobalSetup]
    public void Setup()
    {
        _store = new DiscardObjectStore(yieldOnWrite: false);
        _ledgers = ObjectLedgerStore.OverAppendable(
            _store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append },
            TimeProvider.System);
        _tasks = new Task<LedgerWriteOutcome>[ConcurrentAppends];
        var bytes = new byte[PayloadLength];
        bytes.AsSpan().Fill(0x5A);
        _payload = ArcBuffer.Wrap(bytes);
    }

    [Benchmark(OperationsPerInvoke = RoundsPerInvoke)]
    public async Task<long> CoalescedAppendWavesAsync()
    {
        // Reopen a fresh ledger per invoke: RoundsPerInvoke (64 waves, at most ~4 MiB) stays strictly below both
        // ExtentTargetBytes (128 MiB) and ExtentMaxAppends (40_000), keeping _index bounded and preventing extent
        // rolls from skewing the exact append count across multi-invoke BDN iterations.
        var ledgerId = new LedgerId((ulong)Interlocked.Increment(ref _nextLedgerId));
        var appendsBefore = _store.AppendCount;
        long written = 0;
        long appendsPerformed;

        try
        {
            await using var storage = _ledgers.Open(ledgerId, windowSize: Math.Max(4, ConcurrentAppends));
            long slot = 0;

            for (var round = 0; round < RoundsPerInvoke; round++)
            {
                var baseSlot = slot;
                var acknowledged = new Slot(baseSlot);

                // Submit higher slots before baseSlot so the flush loop waits until baseSlot arrives, then dequeues
                // the entire contiguous wave [baseSlot .. baseSlot + ConcurrentAppends - 1] into one CLOE append.
                for (var i = 1; i < ConcurrentAppends; i++)
                {
                    _tasks[i] = storage.WriteAsync(new Slot(baseSlot + i), _payload, acknowledged).AsTask();
                }

                _tasks[0] = storage.WriteAsync(new Slot(baseSlot), _payload, acknowledged).AsTask();

                var outcomes = await Task.WhenAll(_tasks).ConfigureAwait(false);
                for (var i = 0; i < outcomes.Length; i++)
                {
                    if (outcomes[i] is not ReplicaWriteOutcome.Written)
                    {
                        throw new InvalidOperationException($"Expected Written outcome, got {outcomes[i]}.");
                    }
                }

                written += outcomes.Length;
                slot = baseSlot + ConcurrentAppends;
            }

            appendsPerformed = _store.AppendCount - appendsBefore;
        }
        finally
        {
            _store.Clear();
        }

        if (appendsPerformed != RoundsPerInvoke)
        {
            throw new InvalidOperationException(
                $"Expected {RoundsPerInvoke} coalesced appends, observed {appendsPerformed}.");
        }

        return written;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _store?.Clear();
        _payload.Dispose();
        _payload = default;
    }
}
