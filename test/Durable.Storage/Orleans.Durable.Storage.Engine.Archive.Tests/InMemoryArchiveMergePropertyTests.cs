using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>The merge property over the memory store, which holds any first segments of a key's chain.</summary>
public sealed class InMemoryArchiveMergePropertyTests : ArchiveMergeProperty
{
    protected override long Iterations => 300;

    protected override Task<IMergeStore> CreateAsync() => Task.FromResult<IMergeStore>(new MemoryMergeStore());

    private sealed class MemoryMergeStore : IMergeStore
    {
        private readonly InMemoryArchiveStore _store = new();

        public IArchiveStore Store => _store;

        public bool HoldsWholeLedgers => false;

        public Task DrainAsync(LedgerId ledger, IReadOnlyList<KeyedRow[]> segments, Slot end)
        {
            foreach (var segment in segments)
            {
                KeyedRow.DrainByKey(segment, (key, rows) => _store.Drain(key, rows));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
