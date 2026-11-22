using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>The merge property over the LSM, which holds the rows of the ledgers a read's map value no longer lists.</summary>
public sealed class LsmArchiveMergePropertyTests : ArchiveMergeProperty
{
    protected override long Iterations => 40;

    protected override Task<IMergeStore> CreateAsync() => Task.FromResult<IMergeStore>(new LsmMergeStore());

    private sealed class LsmMergeStore : IMergeStore
    {
        private readonly InMemoryObjectStore _objects = new();
        private readonly ManifestView _manifest;
        private readonly ManifestCommitter _committer;

        public LsmMergeStore()
        {
            _manifest = new ManifestView(_objects);
            _committer = new ManifestCommitter(_objects, _manifest, TimeProvider.System, TimeSpan.Zero);
            Store = new LsmArchiveStore(_objects, new ManifestView(_objects), new TailCache(_objects, new ArchiveOptions()), new ArchiveOptions(), TimeProvider.System);
        }

        public IArchiveStore Store { get; }

        public bool HoldsWholeLedgers => true;

        public async Task DrainAsync(LedgerId ledger, IReadOnlyList<KeyedRow[]> segments, Slot end) =>
            _ = Assert.IsType<ManifestCommitOutcome.Applied>((await LsmDrains.DrainAsync(_objects, _manifest, _committer, ledger, segments.SelectMany(static segment => segment), end, null, CancellationToken.None)).Value);

        public ValueTask DisposeAsync() => _committer.DisposeAsync();
    }
}
