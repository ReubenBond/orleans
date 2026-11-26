using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.RocksDb;
using Orleans.Durable.Storage.Testing;
using RocksDbSharp;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Real RocksDB tests for <see cref="RocksDbJournalIndex"/> (journals.md §8.3, conventions.md §14): WAL-less writes,
/// atomic multi-CF flush, disabled mmap I/O, fixed-prefix bloom filters, whole-ledger <c>DeleteRange</c> +
/// <c>DeleteFilesInRange</c> (including <c>ulong.MaxValue</c>), corruption detection and reset, lock-held vs fail-stop
/// error boundaries, and DI wiring.
/// </summary>
public sealed class RocksDbJournalIndexTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clog-rocksdb-journal-index", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of temp directory.
        }
        finally
        {
            _leaks.Dispose();
        }
    }

    private string NewDirectory(string name) => Path.Combine(_root, name);

    /// <summary>
    /// RocksDB opens with <c>allow_mmap_reads = false</c>, <c>allow_mmap_writes = false</c>, <c>atomic_flush = true</c>,
    /// <c>avoid_flush_during_shutdown = true</c>, and fixed-prefix extractors of 40 bytes on <c>key_index</c> and
    /// 8 bytes on <c>tombstones</c> (journals.md §8.3, conventions.md §14).
    /// </summary>
    [Fact]
    public async Task OptionsDisableMmapEnableAtomicFlushAndConfigurePrefixExtractors()
    {
        var dir = NewDirectory("options");
        await using (var index = new RocksDbJournalIndex(dir))
        {
            var watermarks = await index.OpenAsync(Token);
            Assert.Empty(watermarks);
            await index.FlushAsync(Token);
        }

        var optionsFiles = Directory.GetFiles(dir, "OPTIONS-*");
        Assert.NotEmpty(optionsFiles);
        var optionsText = await File.ReadAllTextAsync(optionsFiles[^1], Token);

        Assert.Contains("allow_mmap_reads=false", optionsText, StringComparison.Ordinal);
        Assert.Contains("allow_mmap_writes=false", optionsText, StringComparison.Ordinal);
        Assert.Contains("atomic_flush=true", optionsText, StringComparison.Ordinal);
        Assert.Contains("avoid_flush_during_shutdown=true", optionsText, StringComparison.Ordinal);
        Assert.Contains($"prefix_extractor=rocksdb.FixedPrefix.{RocksDbJournalIndex.KeyIndexPrefixLength}", optionsText, StringComparison.Ordinal);
        Assert.Contains($"prefix_extractor=rocksdb.FixedPrefix.{RocksDbJournalIndex.TombstonePrefixLength}", optionsText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes disable RocksDB's WAL: unflushed batches are visible in memory while open, produce no durable WAL replay
    /// across close/reopen, and become atomically durable across all three column families after <see cref="RocksDbJournalIndex.FlushAsync"/>.
    /// </summary>
    [Fact]
    public async Task WalLessWritesDiscardUnflushedSuffixOnCloseAndPersistAtomicallyOnFlush()
    {
        var dir = NewDirectory("wal-less");
        var ledger = new LedgerId(7);
        var hashA = JournalKeyHash.Compute("alpha"u8);
        var hashB = JournalKeyHash.Compute("beta"u8);
        var rawBeta = Encoding.UTF8.GetBytes("beta");

        await using (var index = new RocksDbJournalIndex(dir))
        {
            Assert.Empty(await index.OpenAsync(Token));

            // Batch 1: apply and atomically flush.
            await index.ApplyBatchAsync(
                new JournalIndexBatch(
                    ledger,
                    new Slot(1),
                    new[] { new JournalIndexKeyWrite(hashA, new Slot(0), 0, new Slot(0), 1) },
                    new[] { JournalIndexTombstoneWrite.Put(hashB, 12345L, new Slot(0), 1, rawBeta) }),
                Token);
            await index.FlushAsync(Token);

            // Batch 2: apply WITHOUT flushing, verify visible while open.
            await index.ApplyBatchAsync(
                new JournalIndexBatch(
                    ledger,
                    new Slot(2),
                    new[] { new JournalIndexKeyWrite(hashA, new Slot(1), 0, new Slot(0), 1) },
                    new[] { JournalIndexTombstoneWrite.Delete(hashB) }),
                Token);

            var liveEntries = await index.GetKeyEntriesAsync(ledger, hashA, Slot.Zero, 0, new Slot(10), Token);
            Assert.Equal(2, liveEntries.Count);
            Assert.Empty(await index.ScanTombstonesAsync(ledger, Token));

            // Close without flushing (simulating crash / shutdown with avoid_flush_during_shutdown=true).
            await index.CloseAsync();
        }

        // Reopen: only the atomically flushed Batch 1 state is durable across all 3 CFs!
        await using var reopened = new RocksDbJournalIndex(dir);
        var watermarks = await reopened.OpenAsync(Token);
        Assert.Equal(new Slot(1), Assert.Single(watermarks).Value);

        var entries = await reopened.GetKeyEntriesAsync(ledger, hashA, Slot.Zero, 0, new Slot(10), Token);
        var only = Assert.Single(entries);
        Assert.Equal(new JournalIndexKeyEntry(new Slot(0), 0, new Slot(0), 1), only);

        var tombstones = await reopened.ScanTombstonesAsync(ledger, Token);
        var tomb = Assert.Single(tombstones);
        Assert.Equal(hashB, tomb.KeyHash);
        Assert.Equal(12345L, tomb.DeletionTicks);
        Assert.Equal(new Slot(0), tomb.DeleteSlot);
        Assert.Equal(1, tomb.DeleteIndex);
        Assert.Equal(rawBeta, tomb.RawKey);
    }

    /// <summary>
    /// <see cref="RocksDbJournalIndex.GetKeyEntriesAsync"/> and <see cref="RocksDbJournalIndex.HasKeyEntryAsync"/> honor
    /// ledger and key-hash prefix isolation, <c>(startSlot, startIndex)</c> inclusive lower bounds, and
    /// <c>exclusiveEndSlot</c> upper bounds.
    /// </summary>
    [Fact]
    public async Task KeyQueriesIsolatePrefixesAndHonorSlotIndexBounds()
    {
        var dir = NewDirectory("queries");
        var l1 = new LedgerId(10);
        var l2 = new LedgerId(11);
        var hashA = JournalKeyHash.Compute("key-a"u8);
        var hashB = JournalKeyHash.Compute("key-b"u8);

        await using var index = new RocksDbJournalIndex(dir);
        _ = await index.OpenAsync(Token);

        await index.ApplyBatchAsync(
            new JournalIndexBatch(
                l1,
                new Slot(5),
                new JournalIndexKeyWrite[]
                {
                    new(hashA, new Slot(1), 0, new Slot(1), 1),
                    new(hashA, new Slot(1), 2, new Slot(1), 1),
                    new(hashA, new Slot(3), 1, new Slot(1), 1),
                    new(hashB, new Slot(2), 0, new Slot(2), 2),
                },
                ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty),
            Token);

        await index.ApplyBatchAsync(
            new JournalIndexBatch(
                l2,
                new Slot(2),
                new[] { new JournalIndexKeyWrite(hashA, new Slot(0), 0, new Slot(0), 9) },
                ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty),
            Token);

        await index.FlushAsync(Token);

        var allA = await index.GetKeyEntriesAsync(l1, hashA, Slot.Zero, 0, new Slot(5), Token);
        Assert.Equal(
            [
                new JournalIndexKeyEntry(new Slot(1), 0, new Slot(1), 1),
                new JournalIndexKeyEntry(new Slot(1), 2, new Slot(1), 1),
                new JournalIndexKeyEntry(new Slot(3), 1, new Slot(1), 1),
            ],
            allA);

        // Inclusive (startSlot=1, startIndex=1) skips (1, 0) and exclusiveEndSlot=3 skips (3, 1).
        var bounded = await index.GetKeyEntriesAsync(l1, hashA, new Slot(1), 1, new Slot(3), Token);
        Assert.Equal([new JournalIndexKeyEntry(new Slot(1), 2, new Slot(1), 1)], bounded);
        Assert.True(await index.HasKeyEntryAsync(l1, hashA, new Slot(1), 1, new Slot(3), Token));
        Assert.False(await index.HasKeyEntryAsync(l1, hashA, new Slot(2), 0, new Slot(3), Token));
        Assert.False(await index.HasKeyEntryAsync(l1, hashA, new Slot(4), 0, new Slot(4), Token));

        // Neighbor ledger l2 is isolated.
        var l2Entries = await index.GetKeyEntriesAsync(l2, hashA, Slot.Zero, 0, new Slot(5), Token);
        Assert.Equal([new JournalIndexKeyEntry(new Slot(0), 0, new Slot(0), 9)], l2Entries);
    }

    /// <summary>
    /// <see cref="RocksDbJournalIndex.DropLedgersAsync"/> drops whole ledgers via <c>DeleteRange</c> + post-flush
    /// <c>DeleteFilesInRange</c>, including the <see cref="ulong.MaxValue"/> boundary ledger ID, without affecting
    /// neighboring ledgers.
    /// </summary>
    [Fact]
    public async Task DropLedgersRemovesDroppedRangesIncludingUlongMaxValueWithoutTouchingNeighbors()
    {
        var dir = NewDirectory("drop-ledgers");
        var keepLow = new LedgerId(1);
        var dropMid = new LedgerId(2);
        var keepPrevMax = new LedgerId(ulong.MaxValue - 1);
        var dropMax = new LedgerId(ulong.MaxValue);
        var hash = JournalKeyHash.Compute("shared-key"u8);
        var rawKey = Encoding.UTF8.GetBytes("shared-key");

        await using (var index = new RocksDbJournalIndex(dir))
        {
            _ = await index.OpenAsync(Token);
            foreach (var id in new[] { keepLow, dropMid, keepPrevMax, dropMax })
            {
                await index.ApplyBatchAsync(
                    new JournalIndexBatch(
                        id,
                        new Slot(3),
                        new JournalIndexKeyWrite[]
                        {
                            new(hash, new Slot(0), 0, new Slot(0), 1),
                            new(hash, new Slot(2), 4, new Slot(0), 1),
                        },
                        new[] { JournalIndexTombstoneWrite.Put(hash, 999L, new Slot(2), 5, rawKey) }),
                    Token);
            }

            await index.FlushAsync(Token);

            // Drop mid and ulong.MaxValue ledgers and flush durably (triggering DeleteFilesInRange).
            await index.DropLedgersAsync([dropMid, dropMax], Token);
            await index.FlushAsync(Token);
            await index.CloseAsync();
        }

        await using var reopened = new RocksDbJournalIndex(dir);
        var watermarks = await reopened.OpenAsync(Token);
        Assert.Equal([keepLow, keepPrevMax], [.. watermarks.Keys]);

        Assert.Empty(await reopened.GetKeyEntriesAsync(dropMid, hash, Slot.Zero, 0, new Slot(10), Token));
        Assert.Empty(await reopened.ScanTombstonesAsync(dropMid, Token));
        Assert.Empty(await reopened.GetKeyEntriesAsync(dropMax, hash, Slot.Zero, 0, new Slot(10), Token));
        Assert.Empty(await reopened.ScanTombstonesAsync(dropMax, Token));

        Assert.Equal(2, (await reopened.GetKeyEntriesAsync(keepLow, hash, Slot.Zero, 0, new Slot(10), Token)).Count);
        Assert.Single(await reopened.ScanTombstonesAsync(keepLow, Token));
        Assert.Equal(2, (await reopened.GetKeyEntriesAsync(keepPrevMax, hash, Slot.Zero, 0, new Slot(10), Token)).Count);
        Assert.Single(await reopened.ScanTombstonesAsync(keepPrevMax, Token));
    }

    /// <summary>
    /// Corrupting a flushed SST file is detected by <see cref="RocksDbJournalIndex.CheckAsync"/> as
    /// <see cref="JournalIndexCorruptException"/>, and <see cref="RocksDbJournalIndex.ResetAsync"/> wipes and reopens
    /// cleanly with empty watermarks.
    /// </summary>
    [Fact]
    public async Task CorruptSstIsDetectedByCheckAndRecoveredByReset()
    {
        var dir = NewDirectory("corrupt-sst");
        var ledger = new LedgerId(42);
        var hash = JournalKeyHash.Compute("victim"u8);

        await using (var index = new RocksDbJournalIndex(dir))
        {
            _ = await index.OpenAsync(Token);
            await index.ApplyBatchAsync(
                new JournalIndexBatch(
                    ledger,
                    new Slot(1),
                    new[] { new JournalIndexKeyWrite(hash, new Slot(0), 0, new Slot(0), 1) },
                    new[] { JournalIndexTombstoneWrite.Put(hash, 100L, new Slot(0), 0, Encoding.UTF8.GetBytes("victim")) }),
                Token);
            await index.FlushAsync(Token);
            await index.CheckAsync(Token);
            await index.CloseAsync();
        }

        // Flip bytes inside every flushed .sst file so RocksDB block checksum verification fails.
        var sstFiles = Directory.GetFiles(dir, "*.sst");
        Assert.NotEmpty(sstFiles);
        foreach (var sst in sstFiles)
        {
            var bytes = await File.ReadAllBytesAsync(sst, Token);
            bytes[bytes.Length / 2] ^= 0xFF;
            await File.WriteAllBytesAsync(sst, bytes, Token);
        }

        await using var reopened = new RocksDbJournalIndex(dir);
        var openOrCheck = await Record.ExceptionAsync(async () =>
        {
            _ = await reopened.OpenAsync(Token);
            await reopened.CheckAsync(Token);
        });
        _ = Assert.IsType<JournalIndexCorruptException>(openOrCheck);

        await reopened.ResetAsync(Token);
        Assert.Empty(await reopened.OpenAsync(Token));
        await reopened.CheckAsync(Token);
    }

    /// <summary>
    /// Malformed raw rows in <c>default</c> (watermarks), <c>key_index</c>, or <c>tombstones</c> are rejected with
    /// <see cref="JournalIndexCorruptException"/>.
    /// </summary>
    [Fact]
    public async Task MalformedStoredRowsThrowJournalIndexCorruptException()
    {
        var dir = NewDirectory("malformed-rows");
        var ledger = new LedgerId(5);
        var hash = JournalKeyHash.Compute("bad-key"u8);

        await using (var index = new RocksDbJournalIndex(dir))
        {
            _ = await index.OpenAsync(Token);
            await index.FlushAsync(Token);
            await index.CloseAsync();
        }

        // Inject a negative DefiningSlot directly into key_index via raw RocksDbSharp.
        var badKey = new byte[RocksDbJournalIndex.KeyIndexKeyLength];
        RocksDbJournalIndex.EncodeKeyIndexKey(badKey, ledger, hash, new Slot(0), 0);
        var badVal = new byte[RocksDbJournalIndex.KeyIndexValueLength];
        BinaryPrimitives.WriteInt64LittleEndian(badVal.AsSpan(0, 8), -1);
        BinaryPrimitives.WriteUInt64LittleEndian(badVal.AsSpan(8, 8), 0);

        var dbOptions = new DbOptions().SetCreateIfMissing(false);
        var families = new ColumnFamilies
        {
            { RocksDbJournalIndex.DefaultColumnFamily, new ColumnFamilyOptions() },
            { RocksDbJournalIndex.KeyIndexColumnFamily, new ColumnFamilyOptions() },
            { RocksDbJournalIndex.TombstonesColumnFamily, new ColumnFamilyOptions() },
        };
        using (var raw = RocksDbSharp.RocksDb.Open(dbOptions, dir, families))
        {
            raw.Put(badKey, badVal, raw.GetColumnFamily(RocksDbJournalIndex.KeyIndexColumnFamily));
        }

        GC.KeepAlive(dbOptions);
        GC.KeepAlive(families);

        await using (var index = new RocksDbJournalIndex(dir))
        {
            _ = await index.OpenAsync(Token);
            _ = await Assert.ThrowsAsync<JournalIndexCorruptException>(
                () => index.GetKeyEntriesAsync(ledger, hash, Slot.Zero, 0, new Slot(1), Token).AsTask());
        }
    }

    /// <summary>
    /// Persisted <see cref="LedgerSegmentView"/> snapshots flush atomically with watermarks in the <c>default</c> column
    /// family without interfering with watermark recovery, discard unflushed updates on crash, and delete when their
    /// ledger is dropped.
    /// </summary>
    [Fact]
    public async Task LedgerSegmentViewSnapshotsPersistAtomicallyWithWatermarksAndDeleteOnDrop()
    {
        var dir = NewDirectory("segment-snapshots");
        var ledger = new LedgerId(77);
        var snapshot = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        await using (var index = new RocksDbJournalIndex(dir))
        {
            Assert.Empty(await index.OpenAsync(Token));
            Assert.Null(await index.LoadSegmentViewAsync(ledger, Token));

            await index.ApplyBatchAsync(new JournalIndexBatch(ledger, new Slot(10), ReadOnlyMemory<JournalIndexKeyWrite>.Empty, ReadOnlyMemory<JournalIndexTombstoneWrite>.Empty), Token);
            await index.SaveSegmentViewAsync(ledger, snapshot, Token);
            await index.FlushAsync(Token);

            // Overwrite without flushing to model a crash before the next flush.
            await index.SaveSegmentViewAsync(ledger, new byte[] { 0xFF, 0xFE, 0xFD }, Token);
            await index.CloseAsync();
        }

        await using var reopened = new RocksDbJournalIndex(dir);
        var watermarks = await reopened.OpenAsync(Token);
        Assert.Equal(new Slot(10), Assert.Single(watermarks).Value);
        Assert.Equal(snapshot, await reopened.LoadSegmentViewAsync(ledger, Token));

        await reopened.DropLedgersAsync([ledger], Token);
        await reopened.FlushAsync(Token);
        Assert.Null(await reopened.LoadSegmentViewAsync(ledger, Token));
        await reopened.CloseAsync();
    }

    /// <summary>
    /// Opening a second <see cref="RocksDbJournalIndex"/> over a directory locked by another instance throws
    /// <see cref="JournalIndexLockHeldException"/> without triggering <see cref="FatalErrorHandler"/>, while an I/O
    /// failure on open reports <see cref="FatalErrorSite.JournalIndexOpen"/> to <see cref="FatalErrorHandler"/>.
    /// </summary>
    [Fact]
    public async Task LockHeldThrowsTypedExceptionWhileIoFailureReportsFatalErrorSite()
    {
        var dir = NewDirectory("lock-and-fatal");
        await using var first = new RocksDbJournalIndex(dir);
        _ = await first.OpenAsync(Token);

        await using (var second = new RocksDbJournalIndex(dir))
        {
            _ = await Assert.ThrowsAsync<JournalIndexLockHeldException>(() => second.OpenAsync(Token).AsTask());
        }

        // Put a regular file where the RocksDB directory path points so Open hits a real local storage I/O error.
        var fileAsDir = NewDirectory("not-a-dir");
        _ = Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(fileAsDir, "blocking-file", Token);

        var before = FatalErrors.ReportedCount;
        await using var broken = new RocksDbJournalIndex(fileAsDir);
        _ = await Assert.ThrowsAnyAsync<Exception>(() => broken.OpenAsync(Token).AsTask());
        var report = Assert.Single(FatalErrors.TakeMine(before));
        Assert.Equal(FatalErrorSite.JournalIndexOpen, report.Site);
    }

    /// <summary>
    /// <see cref="RocksDbJournalIndexServiceCollectionExtensions.AddRocksDbJournalIndex"/> and
    /// <see cref="RocksDbJournalIndexServiceCollectionExtensions.AddRocksDbJournalIndexForNode"/> wire
    /// <see cref="RocksDbJournalIndex"/> into DI at the expected paths.
    /// </summary>
    [Fact]
    public async Task ServiceCollectionExtensionsRegisterExpectedPaths()
    {
        var nodeRoot = NewDirectory("di-node");
        var servicesForNode = new ServiceCollection();
        _ = servicesForNode.AddRocksDbJournalIndexForNode(nodeRoot);
        await using var nodeProvider = servicesForNode.BuildServiceProvider();
        var nodeIndex = Assert.IsType<RocksDbJournalIndex>(nodeProvider.GetRequiredService<IJournalIndex>());
        Assert.Equal(Path.Combine(nodeRoot, "index"), nodeIndex.Path);

        var directPath = NewDirectory("di-direct");
        var servicesDirect = new ServiceCollection();
        _ = servicesDirect.AddRocksDbJournalIndex(directPath);
        await using var directProvider = servicesDirect.BuildServiceProvider();
        var directIndex = Assert.IsType<RocksDbJournalIndex>(directProvider.GetRequiredService<IJournalIndex>());
        Assert.Equal(directPath, directIndex.Path);
    }
}
