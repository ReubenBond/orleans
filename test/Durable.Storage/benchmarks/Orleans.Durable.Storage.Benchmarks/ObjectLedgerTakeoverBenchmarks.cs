using BenchmarkDotNet.Attributes;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures takeover (<see cref="Ledger.RecoverAsync"/>) time and allocations against ledger age (slot/extent count)
/// for both per-slot and append object-ledger layouts (object-ledgers.md §8.2, §10).
/// </summary>
[MemoryDiagnoser]
public class ObjectLedgerTakeoverBenchmarks
{
    private const int Window = 2;
    private static readonly LedgerId BenchmarkLedger = new(1);

    private SnapshotOverlayObjectStore _overlay = null!;
    private ObjectLedgerStore _ledgers = null!;

    /// <summary>Gets or sets the object-ledger layout.</summary>
    [Params(ObjectLedgerLayout.PerSlot, ObjectLedgerLayout.Append)]
    public ObjectLedgerLayout Layout { get; set; } = ObjectLedgerLayout.PerSlot;

    /// <summary>Gets or sets the ledger age measured in committed slots prior to takeover.</summary>
    [Params(10, 100, 1000)]
    public int LedgerAgeSlots { get; set; } = 10;

    [GlobalSetup]
    public async Task Setup()
    {
        var baseStore = new InMemoryObjectStore();
        var options = Layout == ObjectLedgerLayout.Append
            ? new ObjectLedgerStoreOptions
            {
                Layout = ObjectLedgerLayout.Append,
                ExtentTargetBytes = 16 * 1024,
                ExtentMaxAppends = 50,
            }
            : new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.PerSlot };

        var seedLedgers = Layout == ObjectLedgerLayout.Append
            ? ObjectLedgerStore.OverAppendable(baseStore, options, TimeProvider.System)
            : ObjectLedgerStore.Over(baseStore, options, TimeProvider.System);

        await PopulateAsync(seedLedgers).ConfigureAwait(false);

        _overlay = await SnapshotOverlayObjectStore.CreateAsync(baseStore).ConfigureAwait(false);
        _ledgers = Layout == ObjectLedgerLayout.Append
            ? ObjectLedgerStore.OverAppendable(_overlay, options, TimeProvider.System)
            : ObjectLedgerStore.Over(_overlay, options, TimeProvider.System);
    }

    [Benchmark]
    public async Task<long> TakeoverAsync()
    {
        _overlay.Reset();
        await using var taker = new Ledger(BenchmarkLedger, _ledgers.Open(BenchmarkLedger, Window), LedgerLocation.ObjectStore.Instance);
        var outcome = await taker.RecoverAsync().ConfigureAwait(false);
        return outcome switch
        {
            RecoveryOutcome.Recovered recovered => recovered.End.Value,
            ReplicaWriteOutcome.Dropped => throw new InvalidOperationException("Takeover benchmark ledger was unexpectedly dropped."),
            null => throw new InvalidOperationException("Unexpected null recovery outcome."),
        };
    }

    private async Task PopulateAsync(ObjectLedgerStore ledgers)
    {
        await using var ledger = new Ledger(BenchmarkLedger, ledgers.Open(BenchmarkLedger, Window), LedgerLocation.ObjectStore.Instance);
        await using var writer = ledger.CreateWriter(TimeProvider.System);
        var bytes = new byte[128];
        bytes.AsSpan().Fill(0x7E);
        using var payload = ArcBuffer.Wrap(bytes);

        for (var i = 0; i < LedgerAgeSlots; i++)
        {
            var slot = await writer.AcquireAsync().ConfigureAwait(false);
            if (slot is not { } acquired)
            {
                throw new InvalidOperationException("Writer stopped while populating takeover benchmark.");
            }

            var outcome = await writer.AppendAsync(acquired, payload).ConfigureAwait(false);
            if (outcome is not AppendOutcome.Appended)
            {
                throw new InvalidOperationException($"Unexpected outcome {outcome} while populating takeover benchmark.");
            }
        }
    }
}
