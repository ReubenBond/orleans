using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The archive store contract over the memory store, whose drain hands it each key's rows of a segment, with limits on a
/// generation's appends, which the contract's histories meet, so its writes respect <c>Admits</c>.
/// </summary>
public sealed class InMemoryArchiveStoreContractTests : ArchiveStoreContract
{
    private readonly InMemoryArchiveStore _store = new(maxAppendBytes: 1 << 20, maxAppends: 4, replaceRequestAppends: 3);

    protected override IArchiveStore Store => _store;

    protected override IComparer<RowToken>? ChainOrder { get; } = Comparer<RowToken>.Create(static (left, right) => InMemoryArchiveTokens.Decode(left).CompareTo(InMemoryArchiveTokens.Decode(right)));

    protected override ValueTask DrainAsync(LedgerSegmentId segment, Slot end, KeyedRow[] rows, CancellationToken cancellationToken)
    {
        KeyedRow.DrainByKey(rows, (key, keyRows) => _store.Drain(key, keyRows));
        return ValueTask.CompletedTask;
    }

    protected override ValueTask ReplaceRecordAsync(ArchiveStoreRecord record)
    {
        _store.ReplaceRecord(record);
        return ValueTask.CompletedTask;
    }

    protected override (string Layout, IReadOnlyDictionary<string, string> Fields) ExpectedStoreFields { get; } =
        (InMemoryArchiveStore.Layout, new Dictionary<string, string> { ["max-append-bytes"] = "1048576", ["max-appends"] = "4", ["replace-request-appends"] = "3" });

    protected override bool RaceNextRecordCreate(ArchiveStoreRecord? winner) => false;

    protected override bool LandNextRecordCreateAsExisting() => false;

    protected override ValueTask<byte[]?> StoredRecordBytesAsync() =>
        ValueTask.FromResult(_store.RecordBytes.IsEmpty ? null : _store.RecordBytes.ToArray());

    protected override IArchiveStore Reopen() => _store;

    protected override void DisposeStore() => _store.Dispose();
}
