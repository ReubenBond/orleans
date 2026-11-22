using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// The archive store contract over the LSM, whose drain uploads the segment's rows as a drain output and commits it
/// from the ledger's watermark to its end, completing the ledger, as <see cref="DrainWorker"/> does.
/// </summary>
public sealed class LsmArchiveStoreContractTests : ArchiveStoreContract
{
    private readonly InMemoryObjectStore _objects = new();
    private readonly ManifestView _manifest;
    private readonly ManifestCommitter _committer;
    private readonly RecordRaceObjectStore _records;
    private readonly LsmArchiveStore _store;

    public LsmArchiveStoreContractTests()
    {
        _manifest = new ManifestView(_objects);
        _committer = new ManifestCommitter(_objects, _manifest, TimeProvider.System, TimeSpan.Zero);
        _records = new RecordRaceObjectStore(_objects, ObjectName.Store);
        _store = Open();
    }

    protected override IArchiveStore Store => _store;

    protected override IComparer<RowToken>? ChainOrder => null;

    /// <summary>
    /// Uploads the rows as one section of the whole keyspace and commits the ledger's drain from its watermark to
    /// <paramref name="end"/>, complete, through the manifest committer, as <see cref="DrainWorker"/> does; a straggler's
    /// drain of a ledger already complete commits from the slot it began at, which the committer answers
    /// its goal, so the manifest is unchanged and its object is garbage.
    /// </summary>
    protected override async ValueTask DrainAsync(LedgerSegmentId segment, Slot end, KeyedRow[] rows, CancellationToken cancellationToken)
    {
        var straggler = (await _manifest.ReadAsync(cancellationToken)).Value.IsComplete(segment.Ledger);
        var outcome = await LsmDrains.DrainAsync(_objects, _manifest, _committer, segment.Ledger, rows, end, straggler ? segment.Start : null, cancellationToken);
        var accepted = straggler ? outcome.Value is ManifestCommitOutcome.Applied or ManifestCommitOutcome.Superseded : outcome.Value is ManifestCommitOutcome.Applied;
        Assert.True(accepted, $"The drain of {segment.Ledger} answered {outcome}.");
    }

    protected override (string Layout, IReadOnlyDictionary<string, string> Fields) ExpectedStoreFields { get; } = (LsmArchiveStore.Layout, new Dictionary<string, string>());

    protected override bool RaceNextRecordCreate(ArchiveStoreRecord? winner)
    {
        _records.RaceNextCreate(winner);
        return true;
    }

    protected override bool LandNextRecordCreateAsExisting()
    {
        _records.LandNextCreateAsExisting();
        return true;
    }

    protected override async ValueTask<byte[]?> StoredRecordBytesAsync()
    {
        if (await _objects.ReadVersionedAsync(ObjectName.Store, TestContext.Current.CancellationToken) is not { } found)
        {
            return null;
        }

        using var content = found.Content;
        return content.ToArray();
    }

    protected override IArchiveStore Reopen() => Open();

    protected override async ValueTask ReplaceRecordAsync(ArchiveStoreRecord record)
    {
        await _objects.DeleteAsync(ObjectName.Store, TestContext.Current.CancellationToken);
        using var content = ArcBuffer.Wrap(record.Encode());
        _ = Assert.IsType<ObjectCreateOutcome.Created>((await _objects.CreateAsync(ObjectName.Store, content, null, TestContext.Current.CancellationToken)).Value);
    }

    /// <summary>Opens an LSM store over the objects, its record through the race hook, with a view and tail cache of its own.</summary>
    private LsmArchiveStore Open() =>
        new(_records, new ManifestView(_objects), new TailCache(_objects, new ArchiveOptions()), new ArchiveOptions(), TimeProvider.System);

    // The contract's teardown is synchronous; the committer is idle by then, so its disposal completes at once.
    protected override void DisposeStore() => _committer.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
