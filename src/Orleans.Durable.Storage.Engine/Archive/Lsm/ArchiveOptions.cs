using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Configures the sections a process writes and the partition geometry its drains and compactions follow (archive §8.7,
/// §8.8).
/// </summary>
/// <remarks>
/// A reader needs none of these: each block index lists its blocks, each Bloom filter records its hash count, and each
/// section records its partition, so a changed setting applies to the sections written after it. The grid step is fixed
/// for a cluster's life, since pushes from one grid would land between another's depths.
/// </remarks>
public sealed record ArchiveOptions
{
    /// <summary>The most key bits a grid step spans: a partition then has 2^16 children.</summary>
    public const int MaxGridBits = 16;

    /// <summary>The smallest region file size the volatile-disk block cache accepts: 4 KiB.</summary>
    public const long MinBlockCacheRegionBytes = 4096;

    /// <summary>The largest region file size the volatile-disk block cache accepts: 1 GiB.</summary>
    public const long MaxBlockCacheRegionBytes = 1L << 30;

    /// <summary>
    /// Gets or sets the block size of the sections a process writes: a block ends before a row which would make it longer,
    /// unless the block is empty. At least 1.
    /// </summary>
    public int SectionBlockBytes { get; set; } = 256 * 1024;

    /// <summary>Gets or sets the Bloom filter's bits per key of the sections a process writes, 1 to 64.</summary>
    public int BloomBitsPerKey { get; set; } = 14;

    /// <summary>
    /// Gets or sets the grid step <c>b</c>: partitions sit at depths which are multiples of it, so each has <c>F = 2^b</c>
    /// children one step deeper. 1 to <see cref="MaxGridBits"/>.
    /// </summary>
    public int GridBits { get; set; } = 4;

    /// <summary>
    /// Gets or sets <c>k</c>, the sections a partition holds above which it compacts; at least <c>F = 2^b</c>.
    /// </summary>
    public int MaxSectionsPerPartition { get; set; } = 16;


    /// <summary>
    /// Gets or sets <c>s</c>, the section size: a partition whose more than <c>k</c> sections total at least <c>F × s</c> pushes
    /// them, giving each child about <c>s</c>, and otherwise merges in place only its sections smaller than <c>s</c>.
    /// <c>(k + 1) × s</c>, which bounds a merge in place, lies within the bound on a run object's length.
    /// </summary>
    public long SectionBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the most memory a process's drains hold at once, a bound each drain enforces: a drain charges each row it scans
    /// (its entry, its body's pages, its key once a segment, and its projected length in the object it writes) as it scans
    /// it, and one which would pass the budget beside the others releases its rows and scans again alone, holding the whole
    /// budget; a segment which passes it alone fails its drain. At least <see cref="SectionBytes"/>, since a drain holds its
    /// rows and the object of about that size it writes.
    /// </summary>
    public long DrainMemoryBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Gets or sets about the most memory a process's compactions hold at once: a compaction compacts one output partition at a
    /// time, and each acquires an estimate of the memory its reads hold (their blocks' bytes and their rows' objects), or
    /// the whole budget when that passes it, before it reads them, and releases it once its section is uploaded, so one
    /// which passes the budget runs alone.
    /// </summary>
    public long CompactionBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the most bytes of verified run object tails a process keeps cached outside its preloaded ranges, those
    /// its reads load on demand; a tail fetched past it serves its reads without joining the cache. Tails in the preloaded
    /// ranges stay cached whatever their bytes (archive-lsm.md §8.7).
    /// </summary>
    public long TailCacheBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the least time between a archive store's adoptions of a newer manifest value its process holds, which keep its
    /// view from lagging far behind compactions and sweeps when no read needs it to advance.
    /// </summary>
    public TimeSpan ViewAdoptionInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the directory on local volatile disk where the process keeps its LSM block cache (archive-lsm.md §8.7),
    /// or <see langword="null"/> when no block cache is configured.
    /// </summary>
    public string? BlockCachePath { get; set; }

    /// <summary>
    /// Gets or sets the byte budget of the LSM block cache on local volatile disk, outside the node's persistent disk budget
    /// (archive-lsm.md §8.7, orleans.md §5.9). Zero disables the block cache; when positive, at least twice
    /// <see cref="BlockCacheRegionBytes"/> so FIFO region eviction holds both the Small and Main queues.
    /// </summary>
    public long BlockCacheBytes { get; set; } = 16L << 30;

    /// <summary>
    /// Gets or sets the target size of each region file in the volatile-disk block cache (archive-lsm.md §8.7), between
    /// <see cref="MinBlockCacheRegionBytes"/> and <see cref="MaxBlockCacheRegionBytes"/>.
    /// </summary>
    public long BlockCacheRegionBytes { get; set; } = 64L << 20;

    /// <summary>Gets the block size and Bloom bits per key of the sections a process writes.</summary>
    internal RunSectionSettings SectionSettings => new(SectionBlockBytes, BloomBitsPerKey);

    /// <summary>
    /// Validates the options.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option lies outside its range, <c>k</c> is below <c>2^b</c>,
    /// <c>(k + 1) × s</c> passes the bound on a run object's length, or the drain memory is below <c>s</c>.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SectionBlockBytes, 1, nameof(SectionBlockBytes));
        if (BloomBitsPerKey is < 1 or > RunBloomFilter.MaxBitsPerKey)
        {
            throw new ArgumentOutOfRangeException(nameof(BloomBitsPerKey), BloomBitsPerKey, string.Create(CultureInfo.InvariantCulture, $"A Bloom filter takes 1 to {RunBloomFilter.MaxBitsPerKey} bits per key."));
        }

        if (GridBits is < 1 or > MaxGridBits)
        {
            throw new ArgumentOutOfRangeException(nameof(GridBits), GridBits, string.Create(CultureInfo.InvariantCulture, $"A grid step spans 1 to {MaxGridBits} key bits."));
        }

        if (MaxSectionsPerPartition < 1 << GridBits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSectionsPerPartition),
                MaxSectionsPerPartition,
                string.Create(CultureInfo.InvariantCulture, $"A partition compacts above at least as many sections as its {1 << GridBits} children, so a merge in place takes at least two."));
        }


        ArgumentOutOfRangeException.ThrowIfLessThan(SectionBytes, 1, nameof(SectionBytes));
        if (SectionBytes > RunObjectFormat.MaxObjectLength / ((long)MaxSectionsPerPartition + 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SectionBytes),
                SectionBytes,
                string.Create(CultureInfo.InvariantCulture, $"(k + 1) × s, which bounds a merge in place, lies within {RunObjectFormat.MaxObjectLength} bytes, the longest run object."));
        }

        if (DrainMemoryBytes < SectionBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DrainMemoryBytes),
                DrainMemoryBytes,
                string.Create(CultureInfo.InvariantCulture, $"A drain holds its rows and an object of about the section size, {SectionBytes} bytes."));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(CompactionBudgetBytes, 1, nameof(CompactionBudgetBytes));
        ArgumentOutOfRangeException.ThrowIfNegative(TailCacheBytes, nameof(TailCacheBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(ViewAdoptionInterval, TimeSpan.Zero, nameof(ViewAdoptionInterval));
        if (BlockCachePath is not null && (string.IsNullOrWhiteSpace(BlockCachePath) || !Path.IsPathFullyQualified(BlockCachePath)))
        {
            throw new ArgumentException("The block cache path is a fully qualified directory path, or null when no block cache is configured.", nameof(BlockCachePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(BlockCacheBytes, nameof(BlockCacheBytes));
        if (BlockCacheRegionBytes is < MinBlockCacheRegionBytes or > MaxBlockCacheRegionBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockCacheRegionBytes),
                BlockCacheRegionBytes,
                string.Create(CultureInfo.InvariantCulture, $"A block cache region size lies between {MinBlockCacheRegionBytes} and {MaxBlockCacheRegionBytes} bytes."));
        }

        if (BlockCacheBytes > 0 && BlockCacheBytes < 2 * BlockCacheRegionBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockCacheBytes),
                BlockCacheBytes,
                string.Create(CultureInfo.InvariantCulture, $"A positive block cache budget holds at least two regions ({2 * BlockCacheRegionBytes} bytes)."));
        }
    }
}
