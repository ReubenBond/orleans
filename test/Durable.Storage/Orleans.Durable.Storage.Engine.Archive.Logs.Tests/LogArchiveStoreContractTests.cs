using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The archive store contract (archive.md §3, §10) over archive logs on the memory store, with small blocks and limits
/// which the contract's histories meet, so its writes respect <c>Admits</c>; each key's write of the segment lands through
/// <see cref="ArchiveLogWriter"/>.
/// </summary>
public sealed class LogArchiveStoreContractTests : ArchiveStoreContract
{
    private readonly LogArchiveStoreHarness _harness = new(new MemoryArchiveLogStore(new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 6, 5, MemoryArchiveLogStore.NameRule)));

    protected override IArchiveStore Store => _harness.Store;

    protected override (string Layout, IReadOnlyDictionary<string, string> Fields) ExpectedStoreFields { get; } = ("memory", LogArchiveStoreHarness.ExpectedFields("any"));

    protected override IComparer<RowToken>? ChainOrder => LogArchiveStoreHarness.ChainOrder;

    protected override ValueTask DrainAsync(LedgerSegmentId segment, Slot end, KeyedRow[] rows, CancellationToken cancellationToken) =>
        _harness.DrainAsync(rows, cancellationToken);

    protected override ValueTask ReplaceRecordAsync(ArchiveStoreRecord record) => _harness.ReplaceRecordAsync(record);

    protected override bool RaceNextRecordCreate(ArchiveStoreRecord? winner) => _harness.RaceNextRecordCreate(winner);

    protected override bool LandNextRecordCreateAsExisting() => _harness.LandNextRecordCreateAsExisting();

    protected override ValueTask<byte[]?> StoredRecordBytesAsync() => _harness.StoredRecordBytesAsync();

    protected override IArchiveStore Reopen() => _harness.Reopen();

    protected override void DisposeStore()
    {
    }
}

/// <summary>The archive store contract over archive logs on Azure's layout, through the real SDK over the fake transport.</summary>
public sealed class AzureLogArchiveStoreContractTests : ArchiveStoreContract
{
    private readonly LogArchiveStoreHarness _harness = new(new AzureArchiveLogStore(
        new FakeAzureBlobService().Client(),
        "json",
        new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 4, 6, 5, AzureArchiveLogMetadata.NameRule)));

    protected override IArchiveStore Store => _harness.Store;

    protected override (string Layout, IReadOnlyDictionary<string, string> Fields) ExpectedStoreFields { get; } = ("azure-append-blob", LogArchiveStoreHarness.ExpectedFields(AzureArchiveLogMetadata.NameRule));

    protected override IComparer<RowToken>? ChainOrder => LogArchiveStoreHarness.ChainOrder;

    protected override ValueTask DrainAsync(LedgerSegmentId segment, Slot end, KeyedRow[] rows, CancellationToken cancellationToken) =>
        _harness.DrainAsync(rows, cancellationToken);

    protected override ValueTask ReplaceRecordAsync(ArchiveStoreRecord record) => _harness.ReplaceRecordAsync(record);

    protected override bool RaceNextRecordCreate(ArchiveStoreRecord? winner) => _harness.RaceNextRecordCreate(winner);

    protected override bool LandNextRecordCreateAsExisting() => _harness.LandNextRecordCreateAsExisting();

    protected override ValueTask<byte[]?> StoredRecordBytesAsync() => _harness.StoredRecordBytesAsync();

    protected override IArchiveStore Reopen() => _harness.Reopen();

    protected override void DisposeStore()
    {
    }
}

/// <summary>
/// The archive store contract over archive logs on the chunk layout, whose objects lie beside the store's record in one
/// object store, with a layout of 6 chunks and a request at 5.
/// </summary>
public sealed class ChunkLogArchiveStoreContractTests : ArchiveStoreContract
{
    private readonly LogArchiveStoreHarness _harness = new(static objects => new ChunkArchiveLogStore(
        objects,
        new ArchiveLogLayout(ChunkArchiveLogStore.LayoutName, ArchiveLogPacking.Chunks, 4, 6, 5, ChunkArchiveLogStore.NameRule)));

    protected override IArchiveStore Store => _harness.Store;

    protected override (string Layout, IReadOnlyDictionary<string, string> Fields) ExpectedStoreFields { get; } = ("chunks", LogArchiveStoreHarness.ExpectedFields(ChunkArchiveLogStore.NameRule));

    protected override IComparer<RowToken>? ChainOrder => LogArchiveStoreHarness.ChainOrder;

    protected override ValueTask DrainAsync(LedgerSegmentId segment, Slot end, KeyedRow[] rows, CancellationToken cancellationToken) =>
        _harness.DrainAsync(rows, cancellationToken);

    protected override ValueTask ReplaceRecordAsync(ArchiveStoreRecord record) => _harness.ReplaceRecordAsync(record);

    protected override bool RaceNextRecordCreate(ArchiveStoreRecord? winner) => _harness.RaceNextRecordCreate(winner);

    protected override bool LandNextRecordCreateAsExisting() => _harness.LandNextRecordCreateAsExisting();

    protected override ValueTask<byte[]?> StoredRecordBytesAsync() => _harness.StoredRecordBytesAsync();

    protected override IArchiveStore Reopen() => _harness.Reopen();

    protected override void DisposeStore()
    {
    }
}

/// <summary>A <see cref="LogArchiveStore"/> over an archive-log store, and its drain through <see cref="ArchiveLogWriter"/>.</summary>
internal sealed class LogArchiveStoreHarness
{
    private readonly ArchiveLogWriter _writer;
    private readonly IArchiveLogStore _logs;
    private readonly RecordRaceObjectStore _objects = new(new InMemoryObjectStore(), new ObjectName(ArchiveLogNames.StoreRecord));

    public LogArchiveStoreHarness(IArchiveLogStore logs)
    {
        _logs = logs;
        _writer = new(logs, TimeProvider.System);
        Store = Reopen();
    }

    /// <summary>
    /// Initializes a harness whose logs lie in the same object store as the store's record, as the chunk layout's do.
    /// </summary>
    public LogArchiveStoreHarness(Func<IObjectStore, IArchiveLogStore> logs)
    {
        _logs = logs(_objects);
        _writer = new(_logs, TimeProvider.System);
        Store = Reopen();
    }

    /// <summary>Gets archive logs' order of a key's chain: their tokens' positions.</summary>
    public static IComparer<RowToken> ChainOrder { get; } =
        Comparer<RowToken>.Create(static (left, right) => ArchiveLogTokens.PositionOf(left).CompareTo(ArchiveLogTokens.PositionOf(right)));

    public LogArchiveStore Store { get; }

    /// <summary>
    /// Returns the store fields archive-logs.md §7.5 gives the record of the contract's layout, 4-byte appends, 6 units, a
    /// replace request at 5, and the hexadecimal names, under <paramref name="nameRule"/>.
    /// </summary>
    public static Dictionary<string, string> ExpectedFields(string nameRule) => new(StringComparer.Ordinal)
    {
        ["max-append-bytes"] = "4",
        ["max-units"] = "6",
        ["replace-request-units"] = "5",
        ["name-rule"] = nameRule,
        ["name-scheme"] = "hex-1",
    };

    /// <summary>Returns another store over the same logs and objects, as a restarted process opens it.</summary>
    public LogArchiveStore Reopen() => new(_logs, _objects, ArchiveLogNames.Hexadecimal);

    /// <summary>Makes the store's next create of its record race another process's (<see cref="RecordRaceObjectStore"/>).</summary>
    public bool RaceNextRecordCreate(ArchiveStoreRecord? winner)
    {
        _objects.RaceNextCreate(winner);
        return true;
    }

    /// <summary>Makes the store's next create of its record land and answer that it exists, as a retried attempt does.</summary>
    public bool LandNextRecordCreateAsExisting()
    {
        _objects.LandNextCreateAsExisting();
        return true;
    }

    /// <summary>Returns the bytes of the store's record, <c>durable-storage/store</c>, or <see langword="null"/> when it is absent.</summary>
    public async ValueTask<byte[]?> StoredRecordBytesAsync()
    {
        if (await _objects.ReadVersionedAsync(new ObjectName(ArchiveLogNames.StoreRecord), TestContext.Current.CancellationToken) is not { } found)
        {
            return null;
        }

        using var content = found.Content;
        return content.ToArray();
    }

    /// <summary>Replaces the store's record, <c>durable-storage/store</c>, with <paramref name="record"/>.</summary>
    public async ValueTask ReplaceRecordAsync(ArchiveStoreRecord record)
    {
        var name = new ObjectName(ArchiveLogNames.StoreRecord);
        await _objects.DeleteAsync(name, TestContext.Current.CancellationToken);
        using var content = ArcBuffer.Wrap(record.Encode());
        _ = Assert.IsType<ObjectCreateOutcome.Created>((await _objects.CreateAsync(name, content, null, TestContext.Current.CancellationToken)).Value);
    }

    /// <summary>Writes each key's write of one segment, and requires each held: a straggler's too.</summary>
    public async ValueTask DrainAsync(KeyedRow[] rows, CancellationToken cancellationToken)
    {
        var verdicts = await ArchiveLogDrains.DrainAsync(_writer, ArchiveLogNames.Hexadecimal, rows, cancellationToken);
        Assert.All(verdicts.Values, static verdict => Assert.IsType<ArchiveLogConditionOutcome.Held>(verdict.Value));
    }
}
