using System.Diagnostics.Metrics;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// The LSM's data-plane instruments, on the meter <see cref="MeterName"/> (archive §8.7).
/// </summary>
internal static class ArchiveMetrics
{
    internal const string MeterName = "Orleans.Durable.Storage.Engine.Archive.Lsm";

    private static readonly Meter Meter = new(MeterName);
    private static readonly UpDownCounter<long> CachedTailBytes = Meter.CreateUpDownCounter<long>(
        "orleans.durable.storage.archive.tail_cache.size",
        unit: "By",
        description: "The bytes of verified run object tails the archive stores of the process hold cached.");

    private static readonly Counter<long> ChangedContent = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.object_content_changed",
        unit: "{write}",
        description: "The object writes refused because their content no longer matched the CRC-32C its writer computed when it verified it.");

    private static readonly Counter<long> CorruptCompactions = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.lsm.compaction.corrupt",
        unit: "{compaction}",
        description: "The compactions which found a section corrupt on a read and again on a read past the caches: durable corruption, outside the fault model (archive §8.2).");

    private static readonly Counter<long> BlockCacheHits = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.block_cache.hits",
        unit: "{read}",
        description: "The verified block reads served from the volatile-disk block cache.");

    private static readonly Counter<long> BlockCacheMisses = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.block_cache.misses",
        unit: "{read}",
        description: "The block reads which missed the volatile-disk block cache, including invalidated corrupt blocks.");

    private static readonly Counter<long> BlockCacheEvictions = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.block_cache.evictions",
        unit: "{region}",
        description: "The volatile-disk block cache regions evicted in FIFO order.");

    private static readonly Counter<long> BlockCacheCorrupt = Meter.CreateCounter<long>(
        "orleans.durable.storage.archive.block_cache.corrupt",
        unit: "{block}",
        description: "The cached blocks rejected on read because their CRC-32C, framing, or length failed verification.");

    private static readonly UpDownCounter<long> BlockCacheSizeBytes = Meter.CreateUpDownCounter<long>(
        "orleans.durable.storage.archive.block_cache.size",
        unit: "By",
        description: "The bytes of region files the volatile-disk block caches of the process currently hold on disk.");

    internal static void RecordCachedTailBytes(long delta) => CachedTailBytes.Add(delta);

    internal static void RecordCorruptCompaction() => CorruptCompactions.Add(1);

    internal static void RecordChangedContent() => ChangedContent.Add(1);

    internal static void RecordBlockCacheHit() => BlockCacheHits.Add(1);

    internal static void RecordBlockCacheMiss() => BlockCacheMisses.Add(1);

    internal static void RecordBlockCacheEviction() => BlockCacheEvictions.Add(1);

    internal static void RecordBlockCacheCorrupt() => BlockCacheCorrupt.Add(1);

    internal static void RecordBlockCacheSizeBytes(long delta) => BlockCacheSizeBytes.Add(delta);
}
