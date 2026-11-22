using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// The volatile-disk block cache (archive-lsm.md §8.2, §8.7, §8.8): log of append-only region files over
/// <see cref="WalFileSystem"/> with FIFO region eviction, S3-FIFO admission (<c>Small</c>, <c>Main</c>, <c>Ghost</c>),
/// per-block CRC-32C verification on read, past-cache corruption confirmation before answering
/// <see cref="ArchiveCorruptException"/>, compaction input bypass, drain/compaction output write-through,
/// cached-equals-uncached equivalence, crash/restart recovery, and fail-stop reporting through <see cref="WalFileSystem"/>.
/// </summary>
public sealed class BlockCacheTests : IDisposable
{
    private static readonly string RecordedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-block-cache-tests"));
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// New blocks admit to <c>Small</c>; filling <c>Small</c> past its region budget evicts its oldest region in FIFO
    /// order, sending unread one-hit wonders into <c>Ghost</c> rather than <c>Main</c>.
    /// </summary>
    [Fact]
    public Task NewBlocksEnterSmallAndOneHitWondersEvictToGhostInFifoOrder() =>
        SmallAdmissionAndFifoEvictionAsync(BlockCacheMutant.None);

    /// <summary>The Small admission and FIFO eviction test catches each mutant of those rules.</summary>
    [Theory]
    [InlineData(BlockCacheMutant.AdmitsDirectlyToMain, "entered Small")]
    [InlineData(BlockCacheMutant.SkipsFifoRegionEviction, "evicted from Small")]
    internal async Task TheSmallAdmissionAndFifoEvictionTestCatchesEachMutant(BlockCacheMutant mutant, string message) =>
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => SmallAdmissionAndFifoEvictionAsync(mutant))).Message, StringComparison.Ordinal);

    /// <summary>
    /// A block accessed while in <c>Small</c> promotes to <c>Main</c> when its <c>Small</c> region is evicted, while an
    /// unread peer in the same region leaves to <c>Ghost</c>.
    /// </summary>
    [Fact]
    public Task AccessedSmallBlocksPromoteToMainOnSmallRegionEviction() =>
        SmallPromotionAsync(BlockCacheMutant.None);

    /// <summary>The Small promotion test catches a cache that skips promotion from Small to Main.</summary>
    [Fact]
    public async Task TheSmallPromotionTestCatchesSkipsSmallPromotion() =>
        Assert.Contains("promoted to Main", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => SmallPromotionAsync(BlockCacheMutant.SkipsSmallPromotion))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Re-admitting a key present in <c>Ghost</c> promotes it directly into <c>Main</c>, bypassing <c>Small</c>.
    /// </summary>
    [Fact]
    public Task GhostHitPromotesDirectlyToMainOnReAdmission() =>
        GhostPromotionAsync(BlockCacheMutant.None);

    /// <summary>The Ghost promotion test catches a cache that ignores Ghost on re-admission.</summary>
    [Fact]
    public async Task TheGhostPromotionTestCatchesSkipsGhostPromotion() =>
        Assert.Contains("promoted directly to Main", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => GhostPromotionAsync(BlockCacheMutant.SkipsGhostPromotion))).Message, StringComparison.Ordinal);

    /// <summary>
    /// When <c>Main</c> evicts its oldest region in FIFO order, blocks accessed since entering <c>Main</c> are reinserted
    /// into the new <c>Main</c> region with decremented frequency, while unaccessed blocks are evicted.
    /// </summary>
    [Fact]
    public Task MainReinsertsAccessedBlocksWithDecrementedFrequency() =>
        MainReinsertionAsync(BlockCacheMutant.None);

    /// <summary>The Main reinsertion test catches a cache that drops accessed Main blocks instead of reinserting them.</summary>
    [Fact]
    public async Task TheMainReinsertionTestCatchesSkipsMainReinsertion() =>
        Assert.Contains("reinserted into Main", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => MainReinsertionAsync(BlockCacheMutant.SkipsMainReinsertion))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Corrupting a cached block on local disk causes CRC-32C verification to reject it as a miss and invalidate the entry;
    /// reading through <see cref="LsmArchiveStore"/> then re-fetches the valid block from the object store.
    /// </summary>
    [Fact]
    public Task CorruptCachedBlockFailsCrcInvalidatesAndRefetchesFromObjectStore() =>
        CrcRejectionAsync(BlockCacheMutant.None);

    /// <summary>The CRC verification test catches a cache that skips per-block CRC-32C checks.</summary>
    [Fact]
    public async Task TheCrcTestCatchesSkipsCrcCheck() =>
        Assert.Contains("rejected the bit-flipped cached block", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => CrcRejectionAsync(BlockCacheMutant.SkipsCrcCheck))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Even when a cache returns corrupt bytes (such as under <see cref="BlockCacheMutant.SkipsCrcCheck"/>),
    /// <see cref="RunObjectReader"/> invalidates the cache and confirms against the object store before answering
    /// <see cref="ArchiveCorruptException"/>: a valid object store recovers, while a durably corrupt object throws after
    /// confirming past the cache (archive-lsm.md §8.2, §8.7).
    /// </summary>
    [Fact]
    public async Task CorruptionIsConfirmedPastTheBlockCacheBeforeThrowingArchiveCorruptException()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4096, capacityBytes: 16 * 4096);
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "confirm-past-cache");
        var ledger = world.Ledgers.OpenContaining(Bytes("a")).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of("a", RowKind.Append, new string('v', 100))], Token);
        var section = await CloseCompleteAndDropReturningSectionAsync(world, options, ledger, blocks: null);

        // Case A: cache skips CRC and holds a bit-flipped copy while the object store is intact.
        // RunObjectReader invalidates the cached block and re-reads directly from the object store.
        using (var cache = await BlockCache.OpenAsync(cacheDir, options, fs, BlockCacheMutant.SkipsCrcCheck, Token))
        {
            var store = world.ArchiveStore(world.Manifest, options, blocks: cache);
            Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('v', 100))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));

            // Flip a byte inside the cached payload on disk.
            await FlipPayloadByteInRegionFilesAsync(fs, cacheDir, Token);

            var readsBefore = world.RangedReads(section.Object);
            Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('v', 100))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
            Assert.Equal(readsBefore + 1, world.RangedReads(section.Object));
        }

        // Case B: the object store itself is corrupted after the block was cached cleanly.
        // Corrupt both the cached block and the object-store copy so RunObjectReader confirms past the cache.
        var fs2 = new RecordingFileSystem(RecordedRoot);
        var cacheDir2 = Path.Combine(RecordedRoot, "confirm-durable-corrupt");
        using (var cache = await BlockCache.OpenAsync(cacheDir2, options, fs2, BlockCacheMutant.SkipsCrcCheck, Token))
        {
            var store = world.ArchiveStore(world.Manifest, options, blocks: cache);
            _ = Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token));

            await FlipPayloadByteInRegionFilesAsync(fs2, cacheDir2, Token);
            await CorruptFirstBlockInObjectStoreAsync(world, section.Object, Token);

            var readsBefore = world.RangedReads(section.Object);
            _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token));
            Assert.True(world.RangedReads(section.Object) > readsBefore, "RunObjectReader must confirm corruption directly against the object store past the cache.");
        }
    }

    /// <summary>
    /// Drain outputs write through to the block cache so subsequent reads serve from cache without object-store block reads;
    /// compaction inputs bypass cache admission so cold scans do not pollute the cache, while compaction outputs write
    /// through to the cache (archive-lsm.md §8.7, §8.8).
    /// </summary>
    [Fact]
    public Task CompactionInputsBypassAdmissionWhileDrainAndCompactionOutputsWriteThrough() =>
        DrainAndCompactionCachePolicyAsync(BlockCacheMutant.None);

    /// <summary>The compaction bypass test catches a cache/reader that admits compaction inputs.</summary>
    [Fact]
    public async Task TheCompactionBypassTestCatchesAdmitsCompactionInputs() =>
        Assert.Contains("compaction input", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => DrainAndCompactionCachePolicyAsync(BlockCacheMutant.AdmitsCompactionInputs))).Message, StringComparison.Ordinal);

    /// <summary>
    /// Metamorphic property test (archive-lsm.md §8.7): across random drains, compactions, multi-block keys, absent keys,
    /// fills, FIFO region evictions, and S3-FIFO promotions/demotions, a cached <see cref="LsmArchiveStore"/> answers
    /// identically to an uncached store.
    /// </summary>
    [Fact]
    public void CachedLsmArchiveStoreMatchesUncachedAcrossFillsEvictionsAndPromotions()
    {
        var gen = Gen.Select(
            Gen.Int[1, 3],
            Gen.Int[10, 40].Array[6, 12],
            Gen.Int[0, 15].Array[24, 48]);

        gen.Sample((seed, payloadSizes, readOrder) =>
        {
            _leaks.InScope(() => RunCachedVsUncachedScenarioAsync(seed, payloadSizes, readOrder).GetAwaiter().GetResult());
        }, iter: 12);
    }

    /// <summary>
    /// Next incarnation recovers valid regions over <see cref="RecordingFileSystem"/>, deletes corrupt region headers,
    /// stops at truncated entry tails while keeping valid prefix entries, rejects bit-flipped payloads on read via CRC-32C
    /// (falling back to the object store), and trims queues when <see cref="ArchiveOptions.BlockCacheBytes"/> shrinks.
    /// </summary>
    [Fact]
    public async Task RestartRecoversValidBlocksAndRejectsCorruptOrTruncatedRegionsAndBlocks()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4096, capacityBytes: 12 * 4096);
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "restart-recovery");

        var s1 = await DrainAndDropKeyAsync(world, options, "a", new string('a', 300));
        var s2 = await DrainAndDropKeyAsync(world, options, "b", new string('b', 300));
        var s3 = await DrainAndDropKeyAsync(world, options, "c", new string('c', 300));

        // First incarnation: admit s1 (region 1), force new region via restart, admit s2 (region 2) and s3 (region 2).
        using (var first = await BlockCache.OpenAsync(cacheDir, options, fs, Token))
        {
            var store = world.ArchiveStore(world.Manifest, options, blocks: first);
            _ = Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token));
            Assert.Equal(1, first.RegionCount);
        }

        var fsRestart1 = fs.Restart();
        using (var second = await BlockCache.OpenAsync(cacheDir, options, fsRestart1, Token))
        {
            // Recovered region 1 is sealed; new writes open region 2.
            var store = world.ArchiveStore(world.Manifest, options, blocks: second);
            _ = Answer(await store.ReadAsync(Bytes("b"), world.Ledgers.Map.Current, Token));
            _ = Answer(await store.ReadAsync(Bytes("c"), world.Ledgers.Map.Current, Token));
            Assert.Equal(2, second.RegionCount);
        }

        // Simulate crash + disk damage before third incarnation:
        // 1. Add a region file with a corrupt region header -> must be deleted on open.
        // 2. Append a truncated entry header to region 2 -> prefix entries for b and c survive.
        // 3. Flip a payload byte in region 1 (entry for a) -> opens cleanly, then CRC fails on read and refetches from object store.
        var fsRestart2 = fsRestart1.Restart();
        var corruptHeaderPath = Path.Combine(cacheDir, "r-0000000000000099.reg");
        using (var bad = fsRestart2.CreateFile(corruptHeaderPath, replace: true, writeThrough: false, preallocationSize: 0))
        {
            await bad.WriteAsync(new byte[64], 0, Token);
        }

        var regionFiles = fsRestart2.EnumerateFiles(cacheDir, "*.reg").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, regionFiles.Length);
        var region1Path = regionFiles[0];
        var region2Path = regionFiles[1];

        // Corrupt a payload byte inside region 1 (near the end of the file, before the 4-byte payload CRC).
        using (var r1 = fsRestart2.OpenFile(region1Path, direct: false))
        {
            var flipOffset = r1.Length - 8;
            var single = new byte[1];
            Assert.Equal(1, await r1.ReadAsync(single, flipOffset, Token));
            single[0] ^= 0xff;
            await r1.WriteAsync(single, flipOffset, Token);
        }

        // Append a partial/truncated entry to region 2.
        using (var r2 = fsRestart2.OpenFile(region2Path, direct: false))
        {
            var garbage = new byte[15];
            garbage[0] = 0x42;
            await r2.WriteAsync(garbage, r2.Length, Token);
        }

        using (var third = await BlockCache.OpenAsync(cacheDir, options, fsRestart2, Token))
        {
            // Corrupt header region file was deleted; valid regions 1 and 2 recovered.
            Assert.False(fsRestart2.FileExists(corruptHeaderPath));
            Assert.Equal(2, third.RegionCount);

            var store = world.ArchiveStore(world.Manifest, options, blocks: third);
            // Warm tail cache so we isolate block reads.
            await store.PreloadAsync([KeyRange.Root]);

            // a fails CRC-32C on read in region 1, invalidates, and refetches from the object store!
            // Read "a" before "b"/"c" so a Bloom false positive on s1 during "b"/"c" cannot refetch and repair s1 first
            // (s2 and s3 start at "b"/"c" > "a", so reading "a" never touches s2 or s3).
            var readsBeforeCorrupt = world.RangedReads(s1.Object);
            Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('a', 300))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
            Assert.Equal(readsBeforeCorrupt + 1, world.RangedReads(s1.Object));

            // b and c serve directly from recovered region 2 with zero object-store reads!
            var readsBeforeValid = world.RangedReads(s2.Object) + world.RangedReads(s3.Object);
            Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('b', 300))), Answer(await store.ReadAsync(Bytes("b"), world.Ledgers.Map.Current, Token)));
            Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('c', 300))), Answer(await store.ReadAsync(Bytes("c"), world.Ledgers.Map.Current, Token)));
            Assert.Equal(readsBeforeValid, world.RangedReads(s2.Object) + world.RangedReads(s3.Object));
        }

        // Fourth incarnation with shrunken budget (2 regions total => Small max 1, Main max 1):
        // excess recovered Small regions are trimmed down to the new budget.
        var shrunk = SmallCacheOptions(blockBytes: 512, regionBytes: 4096, capacityBytes: 2 * 4096);
        var fsRestart3 = fsRestart2.Restart();
        using var fourth = await BlockCache.OpenAsync(cacheDir, shrunk, fsRestart3, Token);
        Assert.True(fourth.RegionCount <= 2, $"Expected at most 2 regions after shrinking capacity, found {fourth.RegionCount}.");
        Assert.True(fourth.DiskBytes <= shrunk.BlockCacheBytes);
    }

    /// <summary>
    /// Local storage write and read failures on <see cref="BlockCache"/> flow through <see cref="WalFileSystem"/> and
    /// report to <see cref="FatalErrors"/> with their exact <see cref="FatalErrorSite"/> (conventions.md §14).
    /// </summary>
    [Fact]
    public async Task LocalStorageFailuresReportToFatalErrorsThroughWalFileSystem()
    {
        var recording = new RecordingFileSystem(RecordedRoot);
        var faulting = new FaultInjectingWalFileSystem(recording);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4096, capacityBytes: 4 * 4096);
        var cacheDir = Path.Combine(RecordedRoot, "fatal-errors");
        var obj = new ObjectName("d/0000000000000001/0000000000000001");
        var payload = Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray();

        using var cache = await BlockCache.OpenAsync(cacheDir, options, faulting, Token);

        // 1. Write failure on AdmitAsync reports FatalErrorSite.HandleWrite (region header) or HandleWriteGather (entry).
        faulting.FailNextWrite = true;
        var sinceWrite = FatalErrors.ReportedCount;
        _ = await Assert.ThrowsAsync<IOException>(async () => await cache.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(payload), Token));
        var writeReport = Assert.Single(FatalErrors.TakeMine(sinceWrite));
        Assert.True(writeReport.Site is FatalErrorSite.HandleWrite or FatalErrorSite.HandleWriteGather, $"Unexpected write failure site: {writeReport.Site}");

        // 2. Admit cleanly, then fail the next read on TryReadAsync -> reports FatalErrorSite.HandleRead with no leaked pages.
        await cache.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(payload), Token);
        faulting.FailNextRead = true;
        var sinceRead = FatalErrors.ReportedCount;
        _ = await Assert.ThrowsAsync<IOException>(async () => _ = await cache.TryReadAsync(obj, 0, payload.Length, recordAccess: true, Token));
        var readReport = Assert.Single(FatalErrors.TakeMine(sinceRead));
        Assert.Equal(FatalErrorSite.HandleRead, readReport.Site);
    }

    private static async Task SmallAdmissionAndFifoEvictionAsync(BlockCacheMutant mutant)
    {
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "small-fifo-" + mutant);
        // 4 regions of 4096 bytes => Small max 2 regions, Main max 2 regions (Small is ~10% min 2 when total >= 4).
        // Actually ComputeLayout gives Small=2 for total=4..29, or Small=1 for total=2..3.
        // Use 20 regions (total=20 => Small=2, Main=18) so Small holds at most 2 regions.
        var options = SmallCacheOptions(blockBytes: 1024, regionBytes: 4096, capacityBytes: 20 * 4096);
        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, mutant, Token);

        var obj = new ObjectName("d/0000000000000001/0000000000000001");
        var block = MakeBlock(1, 2000);

        await cache.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(block), Token);
        Assert.True(cache.IsInQueue(obj, 0, BlockCache.CacheQueue.Small), "A newly admitted block must have entered Small, not Main.");

        // Each 2000-byte entry + header (~56 bytes) allows only 1 entry per 4096-byte region (2*2056 = 4112 > 4072).
        // Region 1 holds offset 0; region 2 holds offset 2000; admitting offset 4000 opens region 3 and evicts region 1 in FIFO order.
        await cache.AdmitAsync(obj, 2000, new ReadOnlySequence<byte>(MakeBlock(2, 2000)), Token);
        await cache.AdmitAsync(obj, 4000, new ReadOnlySequence<byte>(MakeBlock(3, 2000)), Token);

        Assert.True(!cache.Contains(obj, 0), "The oldest unread block must be evicted from Small when Small exceeds its region budget.");
        Assert.True(cache.IsInGhost(obj, 0), "An unread block evicted from Small must be recorded in Ghost.");
        Assert.True(cache.IsInQueue(obj, 2000, BlockCache.CacheQueue.Small));
        Assert.True(cache.IsInQueue(obj, 4000, BlockCache.CacheQueue.Small));
    }

    private static async Task SmallPromotionAsync(BlockCacheMutant mutant)
    {
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "small-promote-" + mutant);
        var options = SmallCacheOptions(blockBytes: 1024, regionBytes: 4096, capacityBytes: 20 * 4096);
        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, mutant, Token);

        var obj = new ObjectName("d/0000000000000001/0000000000000002");
        // Two 1400-byte blocks fit in Region 1 of Small (2 * ~1456 = 2912 <= 4072).
        await cache.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(MakeBlock(1, 1400)), Token);
        await cache.AdmitAsync(obj, 1400, new ReadOnlySequence<byte>(MakeBlock(2, 1400)), Token);

        // Access block 0 once so its frequency becomes 1; leave block 1400 unread (frequency 0).
        using (var hit = (await cache.TryReadAsync(obj, 0, 1400, recordAccess: true, Token))!.Value)
        {
            Assert.Equal(MakeBlock(1, 1400), hit.ToArray());
        }

        // Fill region 2 and roll to region 3 to evict Small region 1.
        await cache.AdmitAsync(obj, 2800, new ReadOnlySequence<byte>(MakeBlock(3, 2200)), Token);
        await cache.AdmitAsync(obj, 5000, new ReadOnlySequence<byte>(MakeBlock(4, 2200)), Token);

        Assert.True(cache.IsInQueue(obj, 0, BlockCache.CacheQueue.Main), "The accessed Small block must be promoted to Main on Small eviction.");
        Assert.False(cache.Contains(obj, 1400));
        Assert.True(cache.IsInGhost(obj, 1400));
    }

    private static async Task GhostPromotionAsync(BlockCacheMutant mutant)
    {
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "ghost-promote-" + mutant);
        var options = SmallCacheOptions(blockBytes: 1024, regionBytes: 4096, capacityBytes: 20 * 4096);
        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, mutant, Token);

        var obj = new ObjectName("d/0000000000000001/0000000000000003");
        // Evict offset 0 unread from Small -> Ghost.
        await cache.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(MakeBlock(1, 2200)), Token);
        await cache.AdmitAsync(obj, 2200, new ReadOnlySequence<byte>(MakeBlock(2, 2200)), Token);
        await cache.AdmitAsync(obj, 4400, new ReadOnlySequence<byte>(MakeBlock(3, 2200)), Token);
        Assert.True(cache.IsInGhost(obj, 0));

        // Re-admit offset 0 -> Ghost hit promotes directly to Main.
        await cache.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(MakeBlock(1, 2200)), Token);
        Assert.True(cache.IsInQueue(obj, 0, BlockCache.CacheQueue.Main), "A Ghost hit must be promoted directly to Main on re-admission.");
        Assert.False(cache.IsInGhost(obj, 0));
    }

    private static async Task MainReinsertionAsync(BlockCacheMutant mutant)
    {
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "main-reinsert-" + mutant);
        // total=3 regions => Small=1, Main=2.
        var options = SmallCacheOptions(blockBytes: 1024, regionBytes: 4096, capacityBytes: 3 * 4096);
        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, mutant, Token);

        var obj = new ObjectName("d/0000000000000001/0000000000000004");

        // Helper: promote a 1400-byte block into Main via Small access + eviction.
        async Task PromoteToMainAsync(long offset, byte fill)
        {
            await cache.AdmitAsync(obj, offset, new ReadOnlySequence<byte>(MakeBlock(fill, 1400)), Token);
            using var hit = (await cache.TryReadAsync(obj, offset, 1400, recordAccess: true, Token))!.Value;
            // Push Small (max 1 region) with a 2800-byte filler so the accessed block promotes to Main.
            await cache.AdmitAsync(obj, offset + 100_000, new ReadOnlySequence<byte>(MakeBlock(0xee, 2800)), Token);
        }

        // Promote two 1400-byte blocks (offset 0 and offset 1400) into Main region 1 (both fit in one 4096-byte region).
        await PromoteToMainAsync(0, 1);
        await PromoteToMainAsync(1400, 2);
        Assert.True(cache.IsInQueue(obj, 0, BlockCache.CacheQueue.Main));
        Assert.True(cache.IsInQueue(obj, 1400, BlockCache.CacheQueue.Main));

        // Access offset 0 in Main so its frequency = 1; leave offset 1400 at frequency = 0.
        using (var hit = (await cache.TryReadAsync(obj, 0, 1400, recordAccess: true, Token))!.Value)
        {
            Assert.Equal(MakeBlock(1, 1400), hit.ToArray());
        }

        // Promote two more 1400-byte blocks (Main region 2), then a 5th block which opens Main region 3 and evicts Main region 1.
        await PromoteToMainAsync(2800, 3);
        await PromoteToMainAsync(4200, 4);
        await PromoteToMainAsync(5600, 5);

        Assert.True(cache.IsInQueue(obj, 0, BlockCache.CacheQueue.Main), "An accessed Main block must be reinserted into Main with decremented frequency.");
        Assert.False(cache.Contains(obj, 1400));
    }

    private static async Task CrcRejectionAsync(BlockCacheMutant mutant)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4096, capacityBytes: 10 * 4096);
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "crc-" + mutant);

        var section = await DrainAndDropKeyAsync(world, options, "a", new string('x', 200));

        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, mutant, Token);
        var store = world.ArchiveStore(world.Manifest, options, blocks: cache);
        await store.PreloadAsync([KeyRange.Root]);

        // First read populates cache; second read hits cache with zero object-store reads.
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('x', 200))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
        var readsAfterFill = world.RangedReads(section.Object);
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('x', 200))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
        Assert.Equal(readsAfterFill, world.RangedReads(section.Object));

        // Corrupt a payload byte inside the region file on disk.
        await FlipPayloadByteInRegionFilesAsync(fs, cacheDir, Token);

        var tail = await RunObjectReader.ReadTailAsync(world.Store, section, Token);
        var meta = RunObjectReader.SectionOf(tail!, section);
        var block = meta.Index.Blocks[0];
        var direct = await cache.TryReadAsync(section.Object, block.Offset, (int)block.Length, recordAccess: true, Token);
        try
        {
            Assert.True(direct is null, "BlockCache.TryReadAsync must have rejected the bit-flipped cached block via CRC-32C.");
        }
        finally
        {
            direct?.Dispose();
        }

        Assert.False(cache.Contains(section.Object, block.Offset));

        // Reading through LsmArchiveStore re-fetches from object store and returns the valid value.
        var readsBeforeRefetch = world.RangedReads(section.Object);
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('x', 200))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
        Assert.Equal(readsBeforeRefetch + 1, world.RangedReads(section.Object));
    }

    private static async Task DrainAndCompactionCachePolicyAsync(BlockCacheMutant mutant)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var options = new ArchiveOptions
        {
            GridBits = 1,
            MaxSectionsPerPartition = 2,
            SectionBlockBytes = 512,
            SectionBytes = 200,
            BlockCacheBytes = 20 * 4096,
            BlockCacheRegionBytes = 4096,
        };
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, "compaction-policy-" + mutant);
        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, mutant, Token);

        // 1. Drain 1 writes through to BlockCache!
        var s1 = await DrainAndDropKeyAsync(world, options, "a", new string('a', 120), blocks: cache);

        var store = world.ArchiveStore(world.Manifest, options, blocks: cache);
        await store.PreloadAsync([KeyRange.Root]);
        var readsBeforeHot = world.RangedReads(s1.Object);
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('a', 120))), Answer(await store.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
        Assert.Equal(readsBeforeHot, world.RangedReads(s1.Object));

        // 2. Drain 2 and Drain 3 WITHOUT cache so their sections are cold (not in BlockCache).
        var s2 = await DrainAndDropKeyAsync(world, options, "b", new string('b', 120), blocks: null);
        var s3 = await DrainAndDropKeyAsync(world, options, "é", new string('e', 120), blocks: null);

        Assert.False(cache.Contains(s2.Object, 0));
        Assert.False(cache.Contains(s3.Object, 0));

        // 3. Run CompactionWorker with BlockCache attached:
        //    - Cold inputs s2, s3 MUST NOT be admitted to BlockCache (bypass admission).
        //    - Compaction outputs MUST be written through to BlockCache.
        var compactor = new CompactionWorker(world.Store, new TailCache(world.Store, options), world.Manifest, options, blocks: cache);
        var manifest = (await world.Manifest.ReadAsync(Token)).Value;
        var compacted = Assert.IsType<CompactionOutcome.Compacted>(
            (await compactor.CompactAsync(KeyRange.Root, manifest, world.Ledgers.Map.Current, Token)).Value).Change;
        _ = Assert.IsType<ManifestCommitOutcome.Applied>((await world.CommitAsync(compacted, Token)).Value);

        Assert.Contains(s2, compacted.Inputs);
        Assert.Contains(s3, compacted.Inputs);
        Assert.True(!cache.Contains(s2.Object, 0), "A cold compaction input block must bypass admission and not enter BlockCache.");
        Assert.True(!cache.Contains(s3.Object, 0), "A cold compaction input block must bypass admission and not enter BlockCache.");

        // Every compaction output's first block was written through to BlockCache!
        Assert.NotEmpty(compacted.Outputs);
        foreach (var output in compacted.Outputs)
        {
            Assert.True(cache.Contains(output.Object, output.Offset), "Compaction output blocks must be written through to BlockCache.");
        }

        // Reading compacted keys through LsmArchiveStore after preloading tails hits BlockCache with zero block reads!
        var storeAfter = world.ArchiveStore(world.Manifest, options, blocks: cache);
        await storeAfter.PreloadAsync([KeyRange.Root]);
        var outputReadsBefore = compacted.Outputs.Sum(o => world.RangedReads(o.Object));
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('a', 120))), Answer(await storeAfter.ReadAsync(Bytes("a"), world.Ledgers.Map.Current, Token)));
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('b', 120))), Answer(await storeAfter.ReadAsync(Bytes("b"), world.Ledgers.Map.Current, Token)));
        Assert.Equal("Read " + Convert.ToHexString(Bytes(new string('e', 120))), Answer(await storeAfter.ReadAsync(Bytes("é"), world.Ledgers.Map.Current, Token)));
        Assert.Equal(outputReadsBefore, compacted.Outputs.Sum(o => world.RangedReads(o.Object)));
    }

    private static async Task RunCachedVsUncachedScenarioAsync(int seed, int[] payloadSizes, int[] readOrder)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        // Tiny block size (128 B) and tiny cache (4 * 4096 B) to force multi-block values, frequent fills,
        // FIFO region evictions, Small->Main promotions, Ghost hits, and Main reinsertions.
        var options = new ArchiveOptions
        {
            GridBits = 1,
            MaxSectionsPerPartition = 4,
            SectionBlockBytes = 128,
            SectionBytes = 1,
            BlockCacheBytes = 4 * 4096,
            BlockCacheRegionBytes = 4096,
        };
        var fs = new RecordingFileSystem(RecordedRoot);
        var cacheDir = Path.Combine(RecordedRoot, string.Create(CultureInfo.InvariantCulture, $"prop-{seed}-{payloadSizes.Length}"));
        using var cache = await BlockCache.OpenAsync(cacheDir, options, fs, Token);

        var keys = new List<string>(payloadSizes.Length + 2);

        for (var i = 0; i < payloadSizes.Length; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"k{i % 8:D2}");
            if (!keys.Contains(key))
            {
                keys.Add(key);
            }

            // Multiply payload size to exercise both single-block and multi-block rows.
            var value = new string((char)('a' + (seed + i) % 26), payloadSizes[i] * 8);
            var kind = i % 5 == 4 ? RowKind.Replace : RowKind.Append;
            var ledger = world.Ledgers.OpenContaining(Bytes(key)).Id;
            _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of(key, kind, value)], Token);
            if (i % 3 == 2 || i == payloadSizes.Length - 1)
            {
                _ = await CloseCompleteAndDropReturningSectionAsync(world, options, ledger, blocks: cache);
            }
        }

        // Add absent keys as well.
        keys.Add("absent-low");
        keys.Add("z-absent-high");

        var cached = world.ArchiveStore(world.Manifest, options, blocks: cache);
        var uncached = world.ArchiveStore(world.Manifest, options with { TailCacheBytes = 0 }, blocks: null);
        var nonAbsentReads = 0;

        for (var step = 0; step < readOrder.Length; step++)
        {
            var key = keys[readOrder[step] % keys.Count];
            var expected = Answer(await uncached.ReadAsync(Bytes(key), world.Ledgers.Map.Current, Token));
            var actual = Answer(await cached.ReadAsync(Bytes(key), world.Ledgers.Map.Current, Token));
            Assert.Equal(expected, actual);
            if (expected.StartsWith("Read ", StringComparison.Ordinal))
            {
                nonAbsentReads++;
            }

            // Trigger a compaction midway so the cache also handles compaction input bypass & output write-through.
            if (step == readOrder.Length / 2)
            {
                var compactor = new CompactionWorker(world.Store, new TailCache(world.Store, options), world.Manifest, options, blocks: cache);
                var manifest = (await world.Manifest.ReadAsync(Token)).Value;
                if ((await compactor.CompactAsync(KeyRange.Root, manifest, world.Ledgers.Map.Current, Token)).Value is CompactionOutcome.Compacted compacted)
                {
                    _ = await world.CommitAsync(compacted.Change, Token);
                }
            }
        }

        Assert.True(nonAbsentReads > 0, "The property workload must exercise archived key reads, not only Absent answers.");
    }

    /// <summary>
    /// When the oldest <c>Main</c> region is completely full of hot entries (<c>Frequency &gt; 0</c>), admitting one new
    /// entry to <c>Main</c> writes the incoming entry first and evicts at most one <c>Main</c> region, preventing
    /// cascading evictions and livelock.
    /// </summary>
    [Fact]
    public async Task FullHotMainRegionAtCapacityEvictsAtMostOneRegionWithoutLivelock()
    {
        var dir = Path.Combine(RecordedRoot, "full-hot-main");
        var fs = new RecordingFileSystem(dir);
        // 3 regions of 4 KiB: Small gets 1 region, Main gets 2 regions.
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4 * 1024, capacityBytes: 12 * 1024);
        using var cache = await BlockCache.OpenAsync(dir, options, fs, Token);

        var payload = MakeBlock(0x4a, 900);
        var sequence = new ReadOnlySequence<byte>(payload);

        // Fill Main (2 regions, 4 blocks per 4 KiB region) via Small -> hit -> promote, leaving Main completely full.
        var mainKeys = new List<(ObjectName Obj, long Offset)>();
        for (var i = 0; i < 8; i++)
        {
            var obj = new ObjectName($"m-{i:d2}");
            await cache.AdmitAsync(obj, 0, sequence, Token);
            using (var hit = await cache.TryReadAsync(obj, 0, payload.Length, Token))
            {
                Assert.NotNull(hit);
            }

            mainKeys.Add((obj, 0));

            // Evict Small by filling it with cold keys so the hot block promotes to Main.
            for (var c = 0; c < 4; c++)
            {
                await cache.AdmitAsync(new ObjectName($"c-{i:d2}-{c}"), 0, sequence, Token);
            }
        }

        // Verify all 8 keys are in Main and pump every Main block's frequency to MaxFrequency (3).
        Assert.Equal(8, cache.MainBlockCount);
        foreach (var (obj, offset) in mainKeys)
        {
            Assert.True(cache.IsInQueue(obj, offset, BlockCache.CacheQueue.Main));
            for (var r = 0; r < 3; r++)
            {
                using var hit = await cache.TryReadAsync(obj, offset, payload.Length, Token);
                Assert.NotNull(hit);
            }
        }

        // Pick a ghost key (from the cold Small evictions) so admitting it targets Main directly.
        var ghostObj = new ObjectName("c-06-3");
        Assert.True(cache.IsInGhost(ghostObj, 0));

        var maxIdBefore = MaxRegionId(fs, dir);
        await cache.AdmitAsync(ghostObj, 0, sequence, Token);
        var maxIdAfter = MaxRegionId(fs, dir);

        Assert.Equal(maxIdBefore + 1, maxIdAfter);
        Assert.True(cache.IsInQueue(ghostObj, 0, BlockCache.CacheQueue.Main));
        Assert.True(cache.RegionCount <= 3, $"RegionCount {cache.RegionCount} must stay within the 3-region budget.");
        Assert.True(cache.DiskBytes <= options.BlockCacheBytes, $"DiskBytes {cache.DiskBytes} exceeded capacity.");
    }

    /// <summary>
    /// Recovery discards a corrupt trailing entry whose <c>blockLength</c> is <see cref="int.MaxValue"/> without
    /// throwing <see cref="OverflowException"/>, and deletes any stray <c>r-ffffffffffffffff.reg</c> file so the next
    /// region ID does not overflow.
    /// </summary>
    [Fact]
    public async Task RecoveryDiscardsCorruptTailWithIntMaxBlockLengthAndDeletesMaxRegionIdFile()
    {
        var dir = Path.Combine(RecordedRoot, "overflow-recovery");
        var fs = new RecordingFileSystem(dir);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 8 * 1024, capacityBytes: 32 * 1024);

        var obj = new ObjectName("valid-before-corrupt-tail");
        var payload = MakeBlock(0x5b, 256);

        using (var first = await BlockCache.OpenAsync(dir, options, fs, Token))
        {
            await first.AdmitAsync(obj, 0, new ReadOnlySequence<byte>(payload), Token);
        }

        // Append a corrupt entry prefix claiming blockLength == int.MaxValue to the recovered region file.
        var regionPath = fs.EnumerateFiles(dir, "*.reg").Single();
        using (var file = fs.OpenFile(regionPath, direct: false))
        {
            var corruptPrefix = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(corruptPrefix, 0x454B4C42); // EntryMagic "BLKE"
            BinaryPrimitives.WriteUInt16LittleEndian(corruptPrefix.AsSpan(4), 8); // nameLength
            BinaryPrimitives.WriteUInt16LittleEndian(corruptPrefix.AsSpan(6), 0); // flags
            BinaryPrimitives.WriteInt64LittleEndian(corruptPrefix.AsSpan(8), 0); // blockOffset
            BinaryPrimitives.WriteInt32LittleEndian(corruptPrefix.AsSpan(16), int.MaxValue); // blockLength
            await file.WriteAsync(corruptPrefix, file.Length, Token);
        }

        // Also plant a stray ulong.MaxValue region file.
        var maxIdPath = Path.Combine(dir, "r-ffffffffffffffff.reg");
        using (var stray = fs.CreateFile(maxIdPath, replace: true, writeThrough: false, preallocationSize: 0))
        {
            await stray.WriteAsync(new byte[24], 0, Token);
        }

        using var reopened = await BlockCache.OpenAsync(dir, options, fs, Token);
        Assert.False(fs.FileExists(maxIdPath));

        using (var read = await reopened.TryReadAsync(obj, 0, payload.Length, Token))
        {
            Assert.NotNull(read);
            Assert.Equal(payload, read.Value.ToArray());
        }

        // Subsequent admission succeeds without overflowing _nextRegionId.
        var obj2 = new ObjectName("admitted-after-recovery");
        await reopened.AdmitAsync(obj2, 0, new ReadOnlySequence<byte>(payload), Token);
        using var read2 = await reopened.TryReadAsync(obj2, 0, payload.Length, Token);
        Assert.NotNull(read2);
        Assert.Equal(payload, read2.Value.ToArray());
    }

    /// <summary>
    /// Disposing <see cref="BlockCache"/> while concurrent readers and writers (including evictions) are in flight
    /// drains cleanly without <see cref="ObjectDisposedException"/>, handle leaks, or premature directory-lock release.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadsAdmitsAndDisposeDoNotRaceOrLeakHandles()
    {
        var dir = Path.Combine(RecordedRoot, "concurrent-dispose");
        var fs = new RecordingFileSystem(dir);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4 * 1024, capacityBytes: 12 * 1024);
        var payload = MakeBlock(0x6c, 900);
        var sequence = new ReadOnlySequence<byte>(payload);

        for (var round = 0; round < 10; round++)
        {
            var cache = await BlockCache.OpenAsync(dir, options, fs, Token);
            for (var i = 0; i < 6; i++)
            {
                await cache.AdmitAsync(new ObjectName($"seed-{round}-{i}"), 0, sequence, Token);
            }

            using var start = new ManualResetEventSlim(false);
            var tasks = new List<Task>(4);
            for (var w = 0; w < 2; w++)
            {
                var workerId = w;
                tasks.Add(Task.Run(async () =>
                {
                    start.Wait(Token);
                    for (var i = 0; i < 40; i++)
                    {
                        await cache.AdmitAsync(new ObjectName($"w{workerId}-{round}-{i}"), 0, sequence, Token);
                    }
                }, Token));
            }

            for (var r = 0; r < 2; r++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    start.Wait(Token);
                    for (var i = 0; i < 60; i++)
                    {
                        using var hit = await cache.TryReadAsync(new ObjectName($"seed-{round}-{i % 6}"), 0, payload.Length, Token);
                    }
                }, Token));
            }

            start.Set();
            await Task.Yield();
            cache.Dispose();
            await Task.WhenAll(tasks);

            // Reopening immediately verifies the directory lock and all region handles drained cleanly.
            using var reopened = await BlockCache.OpenAsync(dir, options, fs, Token);
        }
    }

    /// <summary>
    /// On a cache miss, a corrupt block returned by the object store fails decoding and is never admitted into
    /// <see cref="BlockCache"/>.
    /// </summary>
    [Fact]
    public async Task CorruptObjectStoreBlockOnCacheMissIsNotAdmitted()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var dir = Path.Combine(RecordedRoot, "corrupt-miss-not-admitted");
        var fs = new RecordingFileSystem(dir);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 8 * 1024, capacityBytes: 32 * 1024);

        var section = await DrainAndDropKeyAsync(world, options, "corrupt-key", "original-value", blocks: null);
        await CorruptFirstBlockInObjectStoreAsync(world, section.Object, Token);

        using var cache = await BlockCache.OpenAsync(dir, options, fs, Token);
        var store = world.ArchiveStore(world.Manifest, options, blocks: cache);

        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () =>
            _ = await store.ReadAsync(Bytes("corrupt-key"), world.Ledgers.Map.Current, Token));

        Assert.False(cache.Contains(section.Object, 0));
        Assert.Equal(0, cache.CachedBlockCount);
    }

    /// <summary>
    /// Cancelling a caller's token before acquiring the write lock cancels cleanly without mutating the cache, while
    /// cancelling the caller's token after shared eviction has started does not abort the eviction mid-loop or drop
    /// remaining hot entries.
    /// </summary>
    [Fact]
    public async Task CallerCancellationDuringAdmitDoesNotDropHotEntriesOrLeaveOrphanRegions()
    {
        var dir = Path.Combine(RecordedRoot, "caller-cancel-admit");
        var inner = new RecordingFileSystem(dir);
        var fs = new FaultInjectingWalFileSystem(inner);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 4 * 1024, capacityBytes: 12 * 1024);
        using var cache = await BlockCache.OpenAsync(dir, options, fs, Token);

        var payload = MakeBlock(0x7d, 900);
        var sequence = new ReadOnlySequence<byte>(payload);

        // Pre-cancelled token throws before mutating state or creating any region file.
        using (var preCancelled = new CancellationTokenSource())
        {
            await preCancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await cache.AdmitAsync(new ObjectName("cancelled-early"), 0, sequence, preCancelled.Token));
        }

        Assert.Equal(0, cache.RegionCount);
        Assert.Equal(0, cache.CachedBlockCount);

        // Fill the single Small region with 4 hot entries (Frequency > 0).
        var hotKeys = new ObjectName[4];
        for (var i = 0; i < hotKeys.Length; i++)
        {
            hotKeys[i] = new ObjectName($"hot-{i}");
            await cache.AdmitAsync(hotKeys[i], 0, sequence, Token);
            using var hit = await cache.TryReadAsync(hotKeys[i], 0, payload.Length, Token);
            Assert.NotNull(hit);
        }

        // Trigger Small eviction with a caller token that cancels as soon as eviction reads the first hot entry.
        using var callerCts = new CancellationTokenSource();
        fs.OnNextRead = () => callerCts.Cancel();
        await cache.AdmitAsync(new ObjectName("triggers-small-eviction"), 0, sequence, callerCts.Token);

        Assert.True(callerCts.IsCancellationRequested);
        foreach (var hot in hotKeys)
        {
            Assert.True(cache.IsInQueue(hot, 0, BlockCache.CacheQueue.Main), $"Hot entry {hot} must not be dropped when caller cancels.");
        }
    }

    /// <summary>
    /// A block-cache hit reads payload and trailing CRC-32C in one file read, returns only the payload slice, and treats
    /// either a corrupted trailing CRC byte or a short read/EOF as a cache miss without leaking buffer pages.
    /// </summary>
    [Fact]
    public async Task SingleReadVerifiesTrailingCrcAndRejectsCorruptCrcOrShortRead()
    {
        var dir = Path.Combine(RecordedRoot, "single-read-crc");
        var inner = new RecordingFileSystem(dir);
        var fs = new FaultInjectingWalFileSystem(inner);
        var options = SmallCacheOptions(blockBytes: 512, regionBytes: 8 * 1024, capacityBytes: 32 * 1024);
        var payload = MakeBlock(0x3e, 300);

        using var cache = await BlockCache.OpenAsync(dir, options, fs, Token);
        var validObj = new ObjectName("single-read-valid");
        await cache.AdmitAsync(validObj, 0, new ReadOnlySequence<byte>(payload), Token);

        // 1. Valid hit issues a single ReadAsync and returns exact payload bytes.
        var readsBefore = fs.ReadCount;
        using (var hit = await cache.TryReadAsync(validObj, 0, payload.Length, Token))
        {
            Assert.NotNull(hit);
            Assert.Equal(payload.Length, hit.Value.Length);
            Assert.Equal(payload, hit.Value.ToArray());
        }

        Assert.Equal(readsBefore + 1, fs.ReadCount);

        // 2. Short read / premature EOF returns null, invalidates the entry, and leaks no pages.
        fs.ReturnEofOnNextRead = true;
        using (var shortRead = await cache.TryReadAsync(validObj, 0, payload.Length, Token))
        {
            Assert.Null(shortRead);
        }

        Assert.False(cache.Contains(validObj, 0));

        // 3. Corrupting the trailing 4-byte CRC (last byte of the entry) rejects the block and invalidates without leaks.
        var crcObj = new ObjectName("single-read-corrupt-crc");
        await cache.AdmitAsync(crcObj, 0, new ReadOnlySequence<byte>(payload), Token);
        var regionPath = inner.EnumerateFiles(dir, "*.reg").Single();
        using (var file = inner.OpenFile(regionPath, direct: false))
        {
            var lastByteOffset = file.Length - 1;
            var single = new byte[1];
            Assert.Equal(1, await file.ReadAsync(single, lastByteOffset, Token));
            single[0] ^= 0xff;
            await file.WriteAsync(single, lastByteOffset, Token);
        }

        using (var corruptCrc = await cache.TryReadAsync(crcObj, 0, payload.Length, Token))
        {
            Assert.Null(corruptCrc);
        }

        Assert.False(cache.Contains(crcObj, 0));
    }

    private static ulong MaxRegionId(WalFileSystem fs, string cacheDir)
    {
        ulong max = 0;
        foreach (var path in fs.EnumerateFiles(cacheDir, "*.reg"))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 22 && name.StartsWith("r-", StringComparison.Ordinal) && name.EndsWith(".reg", StringComparison.Ordinal)
                && ulong.TryParse(name.AsSpan(2, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
                && id > max)
            {
                max = id;
            }
        }

        return max;
    }

    private static async Task<SectionRef> DrainAndDropKeyAsync(ArchiveWorld world, ArchiveOptions options, string key, string payload, BlockCache? blocks = null)
    {
        var ledger = world.Ledgers.OpenContaining(Bytes(key)).Id;
        _ = await world.Ledgers.ProposeAsync(ledger, [LiveRow.Of(key, RowKind.Append, payload)], Token);
        return await CloseCompleteAndDropReturningSectionAsync(world, options, ledger, blocks);
    }

    private static async Task<SectionRef> CloseCompleteAndDropReturningSectionAsync(ArchiveWorld world, ArchiveOptions options, LedgerId ledger, BlockCache? blocks)
    {
        await world.Ledgers.ReplaceAsync(ledger, Token);
        var change = await world.CompleteAsync(world.Worker(world.Manifest, options, blocks: blocks), ledger, Token);
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, Microsoft.Extensions.Logging.Abstractions.NullLogger<LedgerDropper>.Instance);
        Assert.Equal([ledger], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        return change.Sections[0];
    }

    private static async Task FlipPayloadByteInRegionFilesAsync(WalFileSystem fs, string cacheDir, CancellationToken cancellationToken)
    {
        var files = fs.EnumerateFiles(cacheDir, "*.reg");
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            using var file = fs.OpenFile(path, direct: false);
            var flipOffset = file.Length - 8;
            var single = new byte[1];
            Assert.Equal(1, await file.ReadAsync(single, flipOffset, cancellationToken));
            single[0] ^= 0xff;
            await file.WriteAsync(single, flipOffset, cancellationToken);
        }
    }

    private static async Task CorruptFirstBlockInObjectStoreAsync(ArchiveWorld world, ObjectName name, CancellationToken cancellationToken)
    {
        var length = (await world.Objects.ListAsync(name.Value, cancellationToken: cancellationToken).ToListAsync(cancellationToken)).Single().Length;
        byte[] bytes;
        using (var content = (await world.Objects.ReadAsync(name, 0, (int)length, cancellationToken))!.Value)
        {
            bytes = content.ToArray();
        }

        bytes[4] ^= 0xff;
        await world.Objects.DeleteAsync(name, cancellationToken);
        using var corrupted = ArcBuffer.Wrap(bytes);
        _ = await world.Objects.CreateAsync(name, corrupted, null, cancellationToken);
    }

    private static ArchiveOptions SmallCacheOptions(int blockBytes, long regionBytes, long capacityBytes) =>
        new()
        {
            SectionBlockBytes = blockBytes,
            BlockCacheRegionBytes = regionBytes,
            BlockCacheBytes = capacityBytes,
        };

    private static byte[] MakeBlock(byte fill, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Answer(ArchiveStoreRead answer)
    {
        switch (answer.Value)
        {
            case ArchiveStoreRead.Read read:
                using (read)
                {
                    return "Read " + Convert.ToHexString(read.Prefix.Content.ToArray());
                }

            case ArchiveStoreRead.Absent:
                return "Absent";
            case ArchiveStoreRead.Stale:
                return "Stale";
            case null:
            default:
                throw new InvalidOperationException("An archive store's read answers a case.");
        }
    }

    /// <summary>
    /// A <see cref="WalFileSystem"/> over another which throws an <see cref="IOException"/> on the next read or write
    /// requested by a test, exercising <see cref="WalFileSystem"/>'s fatal-error reporting seam.
    /// </summary>
    private sealed class FaultInjectingWalFileSystem(WalFileSystem inner) : WalFileSystem
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public bool FailNextWrite { get; set; }

        public bool FailNextRead { get; set; }

        public bool ReturnEofOnNextRead { get; set; }

        public Action? OnNextRead { get; set; }

        internal override bool DirectoryExistsCore(string path) => inner.DirectoryExistsCore(path);

        internal override void CreateDirectoryCore(string path) => inner.CreateDirectoryCore(path);

        internal override void DeleteDirectoryCore(string path) => inner.DeleteDirectoryCore(path);

        internal override bool TryDeleteEmptyDirectoryCore(string path) => inner.TryDeleteEmptyDirectoryCore(path);

        internal override bool TryDeleteLockFileCore(string path) => inner.TryDeleteLockFileCore(path);

        internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern) => inner.EnumerateFilesCore(path, searchPattern);

        internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) =>
            inner.EnumerateFilesWithLastWriteTimesCore(path, includeName);

        internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff) => inner.TryDeleteFileWrittenBeforeCore(path, cutoff);

        internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => inner.EnumerateDirectoriesCore(path);

        internal override bool FileExistsCore(string path) => inner.FileExistsCore(path);

        internal override IDisposable OpenLockCore(string path) => inner.OpenLockCore(path);

        internal override IDisposable OpenLockForDeletionCore(string path) => inner.OpenLockForDeletionCore(path);

        internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize) =>
            new FaultInjectingFile(this, inner.CreateFileCore(path, replace, writeThrough, preallocationSize));

        internal override IWalFileSystemFile OpenFileCore(string path, bool direct) =>
            new FaultInjectingFile(this, inner.OpenFileCore(path, direct));

        internal override void MoveFileCore(string source, string destination, bool replace) => inner.MoveFileCore(source, destination, replace);

        internal override void LinkFileCore(string existing, string link) => inner.LinkFileCore(existing, link);

        internal override ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken) => inner.ReadFileCoreAsync(path, cancellationToken);

        internal override void DeleteFileCore(string path) => inner.DeleteFileCore(path);

        internal override void FlushDirectoryCore(string path, DirectoryEntries entries) => inner.FlushDirectoryCore(path, entries);

        private sealed class FaultInjectingFile(FaultInjectingWalFileSystem owner, IWalFileSystemFile file) : IWalFileSystemFile
        {
            public long Length => file.Length;

            public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken)
            {
                _ = Interlocked.Increment(ref owner._readCount);
                if (owner.OnNextRead is { } hook)
                {
                    owner.OnNextRead = null;
                    hook();
                }

                if (owner.FailNextRead)
                {
                    owner.FailNextRead = false;
                    return ValueTask.FromException<int>(new IOException("Scripted local storage read failure."));
                }

                if (owner.ReturnEofOnNextRead)
                {
                    owner.ReturnEofOnNextRead = false;
                    return ValueTask.FromResult(0);
                }

                return file.ReadAsync(buffer, offset, cancellationToken);
            }

            public ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken)
            {
                if (owner.FailNextRead)
                {
                    owner.FailNextRead = false;
                    return ValueTask.FromException<long>(new IOException("Scripted local storage scatter read failure."));
                }

                return file.ReadAsync(buffers, offset, cancellationToken);
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken)
            {
                if (owner.FailNextWrite)
                {
                    owner.FailNextWrite = false;
                    return ValueTask.FromException(new IOException("Scripted local storage write failure."));
                }

                return file.WriteAsync(buffer, offset, cancellationToken);
            }

            public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken)
            {
                if (owner.FailNextWrite)
                {
                    owner.FailNextWrite = false;
                    return ValueTask.FromException(new IOException("Scripted local storage gather write failure."));
                }

                return file.WriteAsync(buffers, offset, cancellationToken);
            }

            public void SetLength(long length) => file.SetLength(length);

            public void Flush() => file.Flush();

            public void Dispose() => file.Dispose();
        }
    }
}
