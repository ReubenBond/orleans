using System.IO.Hashing;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Objects;

/// <summary>
/// The local cache and request pacing of <see cref="ObjectLedgerStore"/> (object-ledgers.md §2.1, §2.2, §6, OL-I11;
/// orleans.md §5.14): memory LRU hits and eviction, decided-only caching, volatile disk cache persistence with CRC-32C
/// verification, fallback to object storage on memory/disk corruption or I/O error without raising a fatal storage error,
/// concurrent cache/eviction races, lifecycle eviction on dispose, metamorphic equivalence against an uncached store, and
/// uniform request pacing through <see cref="ObjectStoreRequestPacer"/> and <see cref="PacedObjectStore"/>.
/// </summary>
public sealed class ObjectLedgerCacheTests : IDisposable
{
    private const int Window = 2;
    private static readonly LedgerId Id = new(42);
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly List<string> _tempDirs = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        _leaks.Dispose();
    }

    /// <summary>OL-I11: cached batches answer whole and range reads without hitting the object store, and evict LRU within budget.</summary>
    [Fact]
    public async Task MemoryCacheServesReadsWithoutObjectStoreCallsAndEvictsLeastRecentlyUsed()
    {
        var store = new CountingObjectStore();
        var ledgers = ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions { CacheBytes = 10 }, TimeProvider.System);
        await using var storage = ledgers.Open(Id, Window);

        byte[] payload0 = [1, 2, 3, 4, 5];
        byte[] payload1 = [6, 7, 8, 9, 10];
        byte[] payload2 = [11, 12, 13, 14, 15];

        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, payload0));
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 1, payload1));

        store.ResetReads();
        Assert.Equal(payload0, await ReadBytesAsync(storage, Slot.Zero, new Slot(2)));
        Assert.Equal([2, 3, 4], await ReadRangeBytesAsync(storage, Slot.Zero, 1, 3, new Slot(2)));

        Assert.Equal(0, store.ReadCalls);

        // Slot 0 was touched more recently than slot 1, so writing slot 2 evicts slot 1.
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 2, payload2));

        Assert.Equal(payload0, await ReadBytesAsync(storage, Slot.Zero, new Slot(3)));
        Assert.Equal(0, store.ReadCalls);

        Assert.Equal(payload1, await ReadBytesAsync(storage, new Slot(1), new Slot(3)));
        Assert.Equal(1, store.ReadCalls);

        ledgers.EvictCachedLedger(Id);
        Assert.Equal(payload2, await ReadBytesAsync(storage, new Slot(2), new Slot(3)));
        Assert.Equal(2, store.ReadCalls);
    }

    /// <summary>OL-I11: pipelined writes above an undecided gap and reads at <c>slot &gt;= bound</c> are not cached until decided.</summary>
    [Fact]
    public async Task UndecidedPipelinedWritesAndUnboundedReadsAreNotCachedUntilDecided()
    {
        var store = new CountingObjectStore();
        var ledgers = ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions { CacheBytes = 1024 }, TimeProvider.System);
        await using var storage = ledgers.Open(Id, 4);

        byte[] payload0 = [1, 2, 3, 4];
        byte[] payload1 = [5, 6, 7, 8];

        // Write slot 1 before slot 0: slot 1 is above the decided tail (0), so it must not enter the cache yet.
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 1, payload1));

        store.ResetReads();
        // Reading slot 1 with bound = 0 (slot >= bound) must read object storage and must not populate the cache.
        Assert.Equal(payload1, await ReadBytesAsync(storage, new Slot(1), Slot.Zero));
        Assert.Equal(1, store.ReadCalls);

        // Reading slot 1 again with bound = 0 still hits object storage (not cached while undecided).
        Assert.Equal(payload1, await ReadBytesAsync(storage, new Slot(1), Slot.Zero));
        Assert.Equal(2, store.ReadCalls);

        // Writing slot 0 closes the gap and promotes both slots 0 and 1 into the decided cache.
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, payload0));
        store.ResetReads();
        Assert.Equal(payload0, await ReadBytesAsync(storage, Slot.Zero, new Slot(2)));
        Assert.Equal(payload1, await ReadBytesAsync(storage, new Slot(1), new Slot(2)));
        Assert.Equal(0, store.ReadCalls);
    }

    /// <summary>object-ledgers.md §2.1: disposing an <see cref="ObjectLedgerStorage"/> evicts its ledger's cache entries.</summary>
    [Fact]
    public async Task DisposingStorageEvictsItsCachedEntries()
    {
        var dir = CreateTempDir();
        var store = new CountingObjectStore();
        var ledgers = ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions { CacheBytes = 1024, CacheDirectory = dir }, TimeProvider.System);
        byte[] payload = [11, 22, 33, 44];

        await using (var storage = ledgers.Open(Id, Window))
        {
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, payload));
            await ledgers.Cache.FlushDiskWritesForTestAsync(Token);
            Assert.Single(Directory.GetFiles(dir, "*.batch"));
        }

        Assert.Empty(Directory.GetFiles(dir, "*.batch"));
        await using var reopened = ledgers.Open(Id, Window);
        store.ResetReads();
        Assert.Equal(payload, await ReadBytesAsync(reopened, Slot.Zero, new Slot(1)));
        Assert.Equal(1, store.ReadCalls);
    }

    /// <summary>Audit #4 / OL-I11: starting a fresh <see cref="ObjectLedgerCache"/> clears pre-existing directory files instead of adopting unverified cross-store entries.</summary>
    [Fact]
    public async Task StartupClearsPreExistingCacheFilesInsteadOfAdoptingCrossStoreEntries()
    {
        var dir = CreateTempDir();
        var first = new ObjectLedgerCache(1024, dir);
        byte[] payload = [1, 2, 3, 4, 5, 6];
        first.PutBytes(Id, Slot.Zero, payload, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload));
        await first.FlushDiskWritesForTestAsync(Token);
        Assert.Single(Directory.GetFiles(dir, "*.batch"));

        // Leave a stray temp file as well.
        var strayTemp = Path.Combine(dir, "stale.batch.tmp");
        await File.WriteAllBytesAsync(strayTemp, [9, 9, 9], Token);

        var second = new ObjectLedgerCache(1024, dir);
        Assert.Null(await second.TryGetAsync(Id, Slot.Zero, Token));
        Assert.Empty(Directory.GetFiles(dir, "*.batch"));
        Assert.False(File.Exists(strayTemp));
    }

    /// <summary>OL-I11: a corrupt cached file on disk is deleted and falls back to object storage without reporting a fatal error.</summary>
    [Fact]
    public async Task CorruptDiskCacheEntryIsDeletedAndFallsBackToObjectStoreWithoutFatalError()
    {
        var dir = CreateTempDir();
        var store = new CountingObjectStore();
        byte[] payload = [10, 20, 30, 40, 50, 60];

        var ledgers = ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions { CacheBytes = 1024, CacheDirectory = dir }, TimeProvider.System);
        await using var storage = ledgers.Open(Id, Window);
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, payload));
        await ledgers.Cache.FlushDiskWritesForTestAsync(Token);

        var cachedFile = Assert.Single(Directory.GetFiles(dir, "*.batch"));

        // Evict memory only so the next read hits the disk cache without reading object storage.
        ledgers.Cache.ClearMemoryForTest();
        store.ResetReads();
        Assert.Equal(payload, await ReadBytesAsync(storage, Slot.Zero, new Slot(1)));
        Assert.Equal(0, store.ReadCalls);

        // Corrupt the on-disk file and clear memory: direct lookup deletes the corrupt file and returns null without fatal errors.
        var raw = await File.ReadAllBytesAsync(cachedFile, Token);
        raw[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(cachedFile, raw, Token);
        ledgers.Cache.ClearMemoryForTest();

        var since = FatalErrors.ReportedCount;
        Assert.Null(await ledgers.Cache.TryGetAsync(Id, Slot.Zero, Token));
        Assert.False(File.Exists(cachedFile));

        // Corrupt a second slot's on-disk file and read through ObjectLedgerStorage: falls back to object storage and repopulates valid bytes.
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 1, payload));
        await ledgers.Cache.FlushDiskWritesForTestAsync(Token);
        var slot1File = Assert.Single(Directory.GetFiles(dir, "*.batch"));
        await File.WriteAllBytesAsync(slot1File, raw, Token);
        ledgers.Cache.ClearMemoryForTest();

        store.ResetReads();
        Assert.Equal(payload, await ReadBytesAsync(storage, new Slot(1), new Slot(2)));
        Assert.Equal(1, store.ReadCalls);
        await ledgers.Cache.FlushDiskWritesForTestAsync(Token);
        Assert.False(File.Exists(slot1File));
        Assert.Single(Directory.GetFiles(dir, "*.batch"));

        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>OL-I11: a truncated disk cache file (at most 4 bytes) is deleted and falls back without fatal errors.</summary>
    [Fact]
    public async Task TruncatedDiskCacheFileIsDeletedWithoutFatalError()
    {
        var dir = CreateTempDir();
        var cache = new ObjectLedgerCache(1024, dir);
        byte[] payload = [1, 2, 3, 4, 5];
        cache.PutBytes(Id, Slot.Zero, payload, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload));
        await cache.FlushDiskWritesForTestAsync(Token);

        var cachedFile = Assert.Single(Directory.GetFiles(dir, "*.batch"));
        await File.WriteAllBytesAsync(cachedFile, [1, 2, 3, 4], Token);
        cache.ClearMemoryForTest();

        var since = FatalErrors.ReportedCount;
        Assert.Null(await cache.TryGetAsync(Id, Slot.Zero, Token));
        Assert.False(File.Exists(cachedFile));
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>OL-I11: a corrupted in-memory cache entry (CRC mismatch) is discarded and returns a miss without fatal errors.</summary>
    [Fact]
    public async Task MemoryCrcMismatchDiscardsEntryWithoutFatalError()
    {
        var since = FatalErrors.ReportedCount;
        var cache = new ObjectLedgerCache(1024, directory: null);
        byte[] payload = [9, 8, 7, 6];
        var badCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload) ^ 1u;
        cache.PutBytes(Id, Slot.Zero, payload, badCrc);

        Assert.Null(await cache.TryGetAsync(Id, Slot.Zero, Token));
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>OL-I11: disk LRU eviction deletes evicted batch files so disk usage stays within the byte budget.</summary>
    [Fact]
    public async Task DiskCacheLruEvictionDeletesEvictedFilesWithinBudget()
    {
        var dir = CreateTempDir();
        var cache = new ObjectLedgerCache(10, dir);
        byte[] a = [1, 2, 3, 4, 5];
        byte[] b = [6, 7, 8, 9, 10];
        byte[] c = [11, 12, 13, 14, 15];

        cache.PutBytes(Id, Slot.Zero, a, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, a));
        cache.PutBytes(Id, new Slot(1), b, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, b));
        await cache.FlushDiskWritesForTestAsync(Token);
        Assert.Equal(2, Directory.GetFiles(dir, "*.batch").Length);

        // Touch slot 0 so slot 1 becomes the LRU victim.
        Assert.Equal(a, await cache.TryGetAsync(Id, Slot.Zero, Token));
        cache.PutBytes(Id, new Slot(2), c, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, c));
        await cache.FlushDiskWritesForTestAsync(Token);

        var remaining = Directory.GetFiles(dir, "*.batch");
        Assert.Equal(2, remaining.Length);
        cache.ClearMemoryForTest();
        Assert.Equal(a, await cache.TryGetAsync(Id, Slot.Zero, Token));
        Assert.Null(await cache.TryGetAsync(Id, new Slot(1), Token));
        Assert.Equal(c, await cache.TryGetAsync(Id, new Slot(2), Token));
    }

    /// <summary>OL-I11 / Audit #7 / conventions.md §14: disk write, cold-read I/O errors, and externally deleted cache directories fall back without triggering <see cref="Diagnostics.FatalErrorHandler"/>.</summary>
    [Fact]
    public async Task DiskCacheWriteReadIoErrorsAndDeletedDirectoryRecoverWithoutFatalError()
    {
        // Write error: point CacheDirectory at an existing regular file so Directory.CreateDirectory / file writes fail.
        var blocker = Path.Combine(CreateTempDir(), "not-a-directory");
        await File.WriteAllBytesAsync(blocker, [1], Token);

        var since = FatalErrors.ReportedCount;
        var writeFailCache = new ObjectLedgerCache(1024, blocker);
        byte[] payload = [7, 8, 9];
        using (var buffer = Copy(payload))
        {
            writeFailCache.Put(Id, Slot.Zero, buffer, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload));
        }

        await writeFailCache.FlushDiskWritesForTestAsync(Token);
        Assert.Equal(payload, await writeFailCache.TryGetAsync(Id, Slot.Zero, Token));
        Assert.Null(await writeFailCache.TryGetAsync(Id, new Slot(1), Token));

        // Cold read error: replace the cached batch path with an empty directory so File.OpenHandle throws UnauthorizedAccess/IOException.
        var dir = CreateTempDir();
        var cache = new ObjectLedgerCache(1024, dir);
        cache.PutBytes(Id, Slot.Zero, payload, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload));
        await cache.FlushDiskWritesForTestAsync(Token);

        var batchPath = Assert.Single(Directory.GetFiles(dir, "*.batch"));
        File.Delete(batchPath);
        _ = Directory.CreateDirectory(batchPath);
        cache.ClearMemoryForTest();

        Assert.Null(await cache.TryGetAsync(Id, Slot.Zero, Token));

        // Audit #7: externally deleting the cache directory resets _directoryCreated and allows subsequent disk writes to recreate it.
        Directory.Delete(dir, recursive: true);
        cache.PutBytes(Id, new Slot(1), payload, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload));
        await cache.FlushDiskWritesForTestAsync(Token);
        cache.PutBytes(Id, new Slot(2), payload, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload));
        await cache.FlushDiskWritesForTestAsync(Token);
        Assert.True(Directory.Exists(dir));
        Assert.NotEmpty(Directory.GetFiles(dir, "*.batch"));

        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>OL-I11: concurrent puts, reads, and ledger evictions never resurrect evicted entries or corrupt cache invariants.</summary>
    [Fact]
    public async Task ConcurrentPutGetAndEvictMaintainInvariantsWithoutResurrection()
    {
        var dir = CreateTempDir();
        var cache = new ObjectLedgerCache(4096, dir);
        var ledger = new LedgerId(77);
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        var crc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload);

        var tasks = new List<Task>();
        for (var worker = 0; worker < 4; worker++)
        {
            var w = worker;
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var slot = new Slot(w * 10 + i % 10);
                    cache.PutBytes(ledger, slot, payload, crc);
                    var hit = await cache.TryGetAsync(ledger, slot, Token);
                    if (hit is not null)
                    {
                        Assert.Equal(payload, hit);
                    }

                    if (i % 7 == 0)
                    {
                        cache.EvictLedger(ledger);
                    }
                }
            }, Token));
        }

        await Task.WhenAll(tasks);
        await cache.FlushDiskWritesForTestAsync(Token);

        // A final eviction after all writers complete must leave no memory or disk residue.
        cache.EvictLedger(ledger);
        for (var s = 0; s < 40; s++)
        {
            Assert.Null(await cache.TryGetAsync(ledger, new Slot(s), Token));
        }

        Assert.Empty(Directory.GetFiles(dir, "*.batch"));
    }

    /// <summary>
    /// OL-I11 / object-ledgers.md §2.1: <see cref="ObjectLedgerLayout.Append"/> caches decided batches in memory and on
    /// disk, answers whole and range reads without object-store calls, enforces OL-I7 drop visibility and eviction on
    /// dispose, and matches an uncached append store across randomized operations.
    /// </summary>
    [Fact]
    public async Task AppendLayoutCachesDecidedBatchesEvictsOnDisposeAndMatchesUncachedStore()
    {
        var dir = CreateTempDir();
        var store = new CountingObjectStore();
        var ledgers = ObjectLedgerStore.OverAppendable(
            store,
            new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, CacheBytes = 1024, CacheDirectory = dir, ExtentTargetBytes = 64, ExtentMaxAppends = 2 },
            TimeProvider.System);
        byte[] payload0 = [1, 2, 3, 4, 5];
        byte[] payload1 = [6, 7, 8, 9, 10];

        await using (var storage = ledgers.Open(Id, Window))
        {
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, payload0));
            Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 1, payload1));
            await ledgers.Cache.FlushDiskWritesForTestAsync(Token);
            Assert.Equal(2, Directory.GetFiles(dir, "*.batch").Length);

            store.ResetReads();
            Assert.Equal(payload0, await ReadBytesAsync(storage, Slot.Zero, new Slot(2)));
            Assert.Equal([7, 8, 9], await ReadRangeBytesAsync(storage, new Slot(1), 1, 3, new Slot(2)));
            Assert.Equal(0, store.ReadCalls);

            // Clearing memory still serves from the volatile disk cache.
            ledgers.Cache.ClearMemoryForTest();
            Assert.Equal(payload1, await ReadBytesAsync(storage, new Slot(1), new Slot(2)));
            Assert.Equal(0, store.ReadCalls);
        }

        // Disposing the append storage evicts its ledger's cache entries.
        Assert.Empty(Directory.GetFiles(dir, "*.batch"));

        // Metamorphic equivalence for Append layout: cached (memory / memory+disk) == uncached.
        var steps = Gen.Select(
            Gen.Int[0, 5],   // op kind
            Gen.Int[0, 4],   // slot
            Gen.Int[0, 5],   // bound
            Gen.Int[0, 10],  // offset
            Gen.Int[1, 10],  // length
            Gen.Byte.Array[1, 8]).Array[1, 16];

        await Gen.Select(Gen.Bool, Gen.Int[16, 64], steps).SampleAsync(async (useDisk, cacheBytes, ops) => await _leaks.InScopeAsync(async () =>
        {
            var metaDir = useDisk ? CreateTempDir() : null;
            var rawUncached = new InMemoryObjectStore();
            var rawCached = new InMemoryObjectStore();
            var uncachedStore = ObjectLedgerStore.OverAppendable(
                rawUncached,
                new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, CacheBytes = 0, ExtentTargetBytes = 64, ExtentMaxAppends = 2 },
                TimeProvider.System);
            var cachedStore = ObjectLedgerStore.OverAppendable(
                rawCached,
                new ObjectLedgerStoreOptions { Layout = ObjectLedgerLayout.Append, CacheBytes = cacheBytes, CacheDirectory = metaDir, ExtentTargetBytes = 64, ExtentMaxAppends = 2 },
                TimeProvider.System);

            await using var uncached = uncachedStore.Open(Id, 3);
            await using var cached = cachedStore.Open(Id, 3);
            var nextWrite = 0;
            var dropped = false;

            foreach (var (kind, slotNum, boundNum, offset, length, rawPayload) in ops)
            {
                var slot = new Slot(slotNum);
                var bound = new Slot(boundNum);
                switch (kind)
                {
                    case 0 or 1: // Sequential append write
                        {
                            var writeSlot = nextWrite++;
                            var expected = await WriteAsync(uncached, writeSlot, rawPayload);
                            var actual = await WriteAsync(cached, writeSlot, rawPayload);
                            Assert.Equal(expected, actual);
                            break;
                        }
                    case 2:
                        {
                            var (expected, expectedCorrupt) = await TryReadAsync(() => uncached.ReadAsync(slot, bound, Token));
                            var (actual, actualCorrupt) = await TryReadAsync(() => cached.ReadAsync(slot, bound, Token));
                            Assert.Equal(expectedCorrupt, actualCorrupt);
                            if (!expectedCorrupt)
                            {
                                AssertReadOutcomesEqual(expected, actual);
                            }

                            break;
                        }
                    case 3:
                        {
                            var (expected, expectedCorrupt) = await TryReadAsync(() => uncached.ReadAsync(slot, offset, length, bound, Token));
                            var (actual, actualCorrupt) = await TryReadAsync(() => cached.ReadAsync(slot, offset, length, bound, Token));
                            Assert.Equal(expectedCorrupt, actualCorrupt);
                            if (!expectedCorrupt)
                            {
                                AssertReadOutcomesEqual(expected, actual);
                            }

                            break;
                        }
                    case 4:
                        {
                            if (!dropped)
                            {
                                dropped = true;
                                var marker = ObjectLedgerNames.Dropped(Id);
                                _ = await rawUncached.CreateAsync(marker, default, crc32c: null, Token);
                                _ = await rawCached.CreateAsync(marker, default, crc32c: null, Token);
                            }

                            break;
                        }
                    default:
                        {
                            cachedStore.EvictCachedLedger(Id);
                            break;
                        }
                }
            }
        }), iter: 40);
    }

    /// <summary>
    /// OL-I11 metamorphic property: an <see cref="ObjectLedgerStore"/> with memory-only or memory+disk caching answers
    /// identically to an uncached store across writes, whole/ranged reads (<c>OutsideBatch</c>, <c>Missing</c>),
    /// <c>RecoverAsync</c>, OL-I7 drop visibility at <c>slot &gt;= bound</c>, and <c>EvictCachedLedger</c>.
    /// </summary>
    [Fact]
    public async Task CachedStoreMatchesUncachedStoreAcrossWritesReadsRecoveryDropsAndEvictions()
    {
        var steps = Gen.Select(
            Gen.Int[0, 6],   // op kind
            Gen.Int[0, 5],   // slot
            Gen.Int[0, 6],   // bound
            Gen.Int[0, 12],  // offset
            Gen.Int[1, 12],  // length
            Gen.Byte.Array[1, 10]).Array[1, 20];

        await Gen.Select(Gen.Bool, Gen.Int[8, 64], steps).SampleAsync(async (useDisk, cacheBytes, ops) => await _leaks.InScopeAsync(async () =>
        {
            var dir = useDisk ? CreateTempDir() : null;
            var rawUncached = new InMemoryObjectStore();
            var rawCached = new InMemoryObjectStore();
            var uncachedStore = ObjectLedgerStore.Over(rawUncached, new ObjectLedgerStoreOptions { CacheBytes = 0 }, TimeProvider.System);
            var cachedStore = ObjectLedgerStore.Over(rawCached, new ObjectLedgerStoreOptions { CacheBytes = cacheBytes, CacheDirectory = dir }, TimeProvider.System);

            await using var uncached = uncachedStore.Open(Id, 3);
            await using var cached = cachedStore.Open(Id, 3);
            var dropped = false;
            var writtenBySlot = new Dictionary<long, byte[]>();

            foreach (var (kind, slotNum, boundNum, offset, length, rawPayload) in ops)
            {
                var slot = new Slot(slotNum);
                var bound = new Slot(boundNum);
                switch (kind)
                {
                    case 0 or 1: // Write (idempotent per slot like a real writer)
                        {
                            if (!writtenBySlot.TryGetValue(slotNum, out var payload))
                            {
                                payload = rawPayload;
                                writtenBySlot[slotNum] = payload;
                            }

                            var expected = await WriteAsync(uncached, slotNum, payload);
                            var actual = await WriteAsync(cached, slotNum, payload);
                            Assert.Equal(expected, actual);
                            break;
                        }
                    case 2: // Whole read
                        {
                            var (expected, expectedCorrupt) = await TryReadAsync(() => uncached.ReadAsync(slot, bound, Token));
                            var (actual, actualCorrupt) = await TryReadAsync(() => cached.ReadAsync(slot, bound, Token));
                            Assert.Equal(expectedCorrupt, actualCorrupt);
                            if (!expectedCorrupt)
                            {
                                AssertReadOutcomesEqual(expected, actual);
                            }

                            break;
                        }
                    case 3: // Range read
                        {
                            var (expected, expectedCorrupt) = await TryReadAsync(() => uncached.ReadAsync(slot, offset, length, bound, Token));
                            var (actual, actualCorrupt) = await TryReadAsync(() => cached.ReadAsync(slot, offset, length, bound, Token));
                            Assert.Equal(expectedCorrupt, actualCorrupt);
                            if (!expectedCorrupt)
                            {
                                AssertReadOutcomesEqual(expected, actual);
                            }

                            break;
                        }
                    case 4: // Recover
                        {
                            var expected = await uncached.RecoverAsync(bound, Token);
                            var actual = await cached.RecoverAsync(bound, Token);
                            Assert.Equal(expected, actual);
                            break;
                        }
                    case 5: // Drop (once, simulating the sweeper placing the dropped marker)
                        {
                            if (!dropped)
                            {
                                dropped = true;
                                var marker = ObjectLedgerNames.Dropped(Id);
                                _ = await rawUncached.CreateAsync(marker, default, crc32c: null, Token);
                                _ = await rawCached.CreateAsync(marker, default, crc32c: null, Token);
                            }

                            break;
                        }
                    default: // Evict cache
                        {
                            cachedStore.EvictCachedLedger(Id);
                            break;
                        }
                }
            }
        }), iter: 60);
    }

    /// <summary>Zero <see cref="ObjectLedgerStoreOptions.CacheBytes"/> disables caching completely.</summary>
    [Fact]
    public async Task ZeroCacheBytesDisablesCaching()
    {
        var store = new CountingObjectStore();
        var ledgers = ObjectLedgerStore.Over(store, new ObjectLedgerStoreOptions { CacheBytes = 0 }, TimeProvider.System);
        await using var storage = ledgers.Open(Id, Window);

        byte[] payload = [1, 2, 3];
        Assert.Equal<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance, await WriteAsync(storage, 0, payload));

        store.ResetReads();
        Assert.Equal(payload, await ReadBytesAsync(storage, Slot.Zero, new Slot(1)));
        Assert.Equal(1, store.ReadCalls);
    }

    /// <summary>orleans.md §5.14: <see cref="ObjectStoreRequestPacer"/> paces calls, detects budget exhaustion, handles cancellation before charging debt, clamps extreme rates, and bounds queued debt.</summary>
    [Fact]
    public async Task RequestPacerEnforcesRateCancellationOverflowClampingAndDebtBound()
    {
        var clock = new FakeTimeProvider();
        var pacer = new ObjectStoreRequestPacer(clock)
        {
            RateSource = static () => 2.0, // 500 ms per request
        };

        Assert.False(pacer.IsBudgetExhausted);

        // Cancelled token throws before charging debt.
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(async () => await pacer.PaceAsync(cancelled.Token));
        }

        Assert.False(pacer.IsBudgetExhausted);

        // Request 1 starts immediately, reserving [0, 500ms).
        var first = pacer.PaceAsync(Token).AsTask();
        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(pacer.IsBudgetExhausted);

        // Request 2 reserves [500ms, 1000ms) -> debt reaches 1s -> exhausted.
        var second = pacer.PaceAsync(Token).AsTask();
        Assert.False(second.IsCompleted);
        Assert.True(pacer.IsBudgetExhausted);

        // Advancing 500ms lets request 2 complete and lowers remaining debt below 1s.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await second;
        Assert.False(pacer.IsBudgetExhausted);
        clock.Advance(TimeSpan.FromMilliseconds(500));

        // Non-finite or non-positive rates disable throttling cleanly.
        foreach (var inactive in new[] { 0.0, -5.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var r = inactive;
            pacer.RateSource = () => r;
            Assert.False(pacer.IsBudgetExhausted);
            await pacer.PaceAsync(Token);
        }

        // Extremely small positive rate clamps span (no tick overflow) and never wedges debt past its bound.
        pacer.RateSource = static () => 1e-300;
        var slow = pacer.PaceAsync(Token).AsTask();
        Assert.True(slow.IsCompletedSuccessfully);
        Assert.True(pacer.IsBudgetExhausted);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(pacer.IsBudgetExhausted);

        // Clearing the source unthrottles immediately.
        pacer.RateSource = null;
        Assert.False(pacer.IsBudgetExhausted);
    }

    /// <summary>
    /// Audit #2: high-concurrency callers beyond <c>2 * R</c> and low rates (<c>&lt; 0.5 req/s</c>) never burst past the
    /// configured rate limit because <see cref="ObjectStoreRequestPacer"/> waits without clamping or granting a slot when
    /// debt is already at <c>maxDebt</c>.
    /// </summary>
    [Theory]
    [InlineData(2.0, 12)]
    [InlineData(0.25, 6)]
    public async Task RequestPacerPreventsBurstsUnderHighConcurrencyAndLowRates(double ratePerSecond, int callers)
    {
        var clock = new FakeTimeProvider();
        var pacer = new ObjectStoreRequestPacer(clock)
        {
            RateSource = () => ratePerSecond,
        };

        var grantTimes = new List<TimeSpan>();
        var gateLock = new Lock();
        var tasks = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(async () =>
            {
                await pacer.PaceAsync(Token);
                lock (gateLock)
                {
                    grantTimes.Add(clock.GetUtcNow() - DateTimeOffset.MinValue);
                }
            }, Token))
            .ToArray();

        var step = TimeSpan.FromMilliseconds(50);
        var maxSteps = (int)Math.Ceiling((callers / ratePerSecond + 5.0) / step.TotalSeconds);
        for (var i = 0; i < maxSteps && !tasks.All(static t => t.IsCompleted); i++)
        {
            // Yield so waiters register their timers before advancing the fake clock.
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
            clock.Advance(step);
        }

        await Task.WhenAll(tasks);
        Assert.Equal(callers, grantTimes.Count);

        var expectedMinSpacing = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        grantTimes.Sort();
        for (var i = 1; i < grantTimes.Count; i++)
        {
            var delta = grantTimes[i] - grantTimes[i - 1];
            Assert.True(
                delta + step >= expectedMinSpacing,
                $"Burst detected at grant {i}: spacing {delta} < {expectedMinSpacing} for rate={ratePerSecond} req/s.");
        }
    }

    /// <summary>orleans.md §5.14: <see cref="PacedAppendableObjectStore"/> paces every plain and appendable RPC uniformly.</summary>
    [Fact]
    public async Task PacedAppendableObjectStorePacesEveryRpcUniformly()
    {
        var clock = new FakeTimeProvider();
        var inner = new InMemoryObjectStore();
        var pacer = new ObjectStoreRequestPacer(clock);
        var paced = PacedAppendableObjectStore.Create(inner, pacer);
        var calls = 0;
        pacer.RateSource = () =>
        {
            _ = Interlocked.Increment(ref calls);
            return 1_000_000.0;
        };

        var plain = new ObjectName("plain");
        var appendable = new ObjectName("appendable");
        using (var body = Copy([1, 2, 3]))
        {
            _ = await paced.CreateAsync(plain, body, null, Token);
            _ = await paced.GetPropertiesAsync(plain, Token);
            using var read = await paced.ReadAsync(plain, 0, 3, Token);
            var versioned = await paced.ReadVersionedAsync(plain, Token);
            Assert.NotNull(versioned);
            versioned.Value.Content.Dispose();
            _ = await paced.ReplaceAsync(plain, versioned.Value.Version, body, null, Token);
            await foreach (var _ in paced.ListAsync("", cancellationToken: Token))
            {
            }

            await paced.DeleteAsync(plain, Token);
            _ = await paced.CreateAppendableAsync(appendable, Token);
            _ = await paced.AppendAsync(appendable, 0, body, null, Token);
            _ = await paced.SealAsync(appendable, Token);
            await paced.ReplaceWithTombstoneAsync(appendable, body, null, Token);
        }

        Assert.Equal(11, calls);
    }

    private static async ValueTask<(ReplicaRead Read, bool Corrupt)> TryReadAsync(Func<ValueTask<ReplicaRead>> read)
    {
        try
        {
            return (await read(), false);
        }
        catch (LedgerStorageCorruptException)
        {
            return (default, true);
        }
    }

    private static void AssertReadOutcomesEqual(ReplicaRead expected, ReplicaRead actual)
    {
        switch ((expected.Value, actual.Value))
        {
            case (ArcBuffer exp, ArcBuffer act):
                using (exp)
                using (act)
                {
                    Assert.Equal(exp.ToArray(), act.ToArray());
                }

                break;
            default:
                if (expected.TryGetValue(out ArcBuffer e))
                {
                    e.Dispose();
                }

                if (actual.TryGetValue(out ArcBuffer a))
                {
                    a.Dispose();
                }

                Assert.Equal(expected.Value, actual.Value);
                break;
        }
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "clog-ol-cache-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static async Task<byte[]> ReadBytesAsync(ILedgerStorage storage, Slot slot, Slot bound)
    {
        var outcome = await storage.ReadAsync(slot, bound, Token);
        Assert.True(outcome.TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            return batch.ToArray();
        }
    }

    private static async Task<byte[]> ReadRangeBytesAsync(ILedgerStorage storage, Slot slot, int offset, int length, Slot bound)
    {
        var outcome = await storage.ReadAsync(slot, offset, length, bound, Token);
        Assert.True(outcome.TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            return batch.ToArray();
        }
    }

    private static async Task<LedgerWriteOutcome> WriteAsync(ILedgerStorage storage, long slot, byte[] bytes)
    {
        using var buffer = Copy(bytes);
        return await storage.WriteAsync(new Slot(slot), buffer, Slot.Zero, Token);
    }

    private static ArcBuffer Copy(byte[] bytes)
    {
        using var writer = new ArcBufferWriter(bytes.Length);
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    private sealed class CountingObjectStore : ForwardingObjectStore
    {
        private int _readCalls;

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public void ResetReads() => Volatile.Write(ref _readCalls, 0);

        public override ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _readCalls);
            return base.ReadAsync(name, offset, length, cancellationToken);
        }
    }
}
