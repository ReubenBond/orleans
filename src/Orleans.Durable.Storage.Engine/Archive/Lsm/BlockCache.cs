using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Caches LSM run object blocks on local volatile disk, keyed by <c>(object, block)</c> (archive-lsm.md §8.7): a log of
/// append-only region files with FIFO region eviction, S3-FIFO admission (<c>Small</c>, <c>Main</c>, and <c>Ghost</c>),
/// and a per-block CRC-32C verified on every read.
/// </summary>
/// <remarks>
/// <para>
/// Every file-system operation runs through <see cref="WalFileSystem"/>, so a failed operation on the volatile disk reports
/// to <c>FatalErrorHandler</c> and stops the process (conventions.md §14). Because the cache is derived volatile state
/// (formats.md §7), writes are buffered without fsync; on restart, <see cref="OpenAsync(string, ArchiveOptions, WalFileSystem, CancellationToken)"/>
/// scans existing region headers, discards corrupt or truncated tails, and every subsequent read verifies the block's
/// CRC-32C before serving it, treating a mismatch as a miss so the caller fetches from the object store.
/// </para>
/// </remarks>
public sealed class BlockCache : IDisposable
{
    private const string LockFileName = "cache.lock";
    private const string RegionPrefix = "r-";
    private const string RegionSuffix = ".reg";
    private const int RegionFileNameLength = 2 + 16 + 4;

    private const uint RegionMagic = 0x43424C43; // "CLBC"
    private const uint FormatVersion = 1;
    private const int RegionHeaderBytes = 24;

    private const uint EntryMagic = 0x454B4C42; // "BLKE"
    private const int EntryPrefixBytes = 20;
    private const int CrcBytes = 4;
    private const int MaxFrequency = 3;

    private readonly string _directory;
    private readonly WalFileSystem _fileSystem;
    private readonly IDisposable _directoryLock;
    private readonly long _capacityBytes;
    private readonly long _regionBytes;
    private readonly long _ghostBudgetBytes;
    private readonly BlockCacheMutant _mutant;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Dictionary<BlockKey, CacheEntry> _index = [];
    private readonly QueueState _small;
    private readonly QueueState _main;
    private readonly Dictionary<BlockKey, LinkedListNode<GhostEntry>> _ghostIndex = [];
    private readonly LinkedList<GhostEntry> _ghostOrder = [];

    private ulong _nextRegionId;
    private long _ghostBytes;
    private long _diskBytes;
    private long _reportedMetricBytes;
    private int _activeOps = 1;
    private bool _disposed;

    private BlockCache(
        string directory,
        WalFileSystem fileSystem,
        IDisposable directoryLock,
        long capacityBytes,
        long regionBytes,
        int smallMaxRegions,
        int mainMaxRegions,
        long ghostBudgetBytes,
        BlockCacheMutant mutant)
    {
        _directory = directory;
        _fileSystem = fileSystem;
        _directoryLock = directoryLock;
        _capacityBytes = capacityBytes;
        _regionBytes = regionBytes;
        _small = new QueueState(CacheQueue.Small, smallMaxRegions);
        _main = new QueueState(CacheQueue.Main, mainMaxRegions);
        _ghostBudgetBytes = ghostBudgetBytes;
        _mutant = mutant;
        _nextRegionId = 1;
    }

    /// <summary>Gets the cache's byte budget on volatile disk.</summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>Gets the target size of each region file.</summary>
    public long RegionBytes => _regionBytes;

    /// <summary>Gets the active test mutant, or <see cref="BlockCacheMutant.None"/> in production.</summary>
    internal BlockCacheMutant Mutant => _mutant;

    /// <summary>Gets the number of cached blocks currently indexed.</summary>
    internal int CachedBlockCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>Gets the number of cached blocks currently in the S3-FIFO Small queue.</summary>
    internal int SmallBlockCount => CountInQueue(CacheQueue.Small);

    /// <summary>Gets the number of cached blocks currently in the S3-FIFO Main queue.</summary>
    internal int MainBlockCount => CountInQueue(CacheQueue.Main);

    /// <summary>Gets the number of keys currently held in the S3-FIFO Ghost queue.</summary>
    internal int GhostKeyCount
    {
        get
        {
            lock (_gate)
            {
                return _ghostIndex.Count;
            }
        }
    }

    /// <summary>Gets the number of live region files currently held by the cache.</summary>
    internal int RegionCount
    {
        get
        {
            lock (_gate)
            {
                return _small.Regions.Count + _main.Regions.Count;
            }
        }
    }

    /// <summary>Gets the total bytes of live region files currently held on disk.</summary>
    internal long DiskBytes
    {
        get
        {
            lock (_gate)
            {
                return _diskBytes;
            }
        }
    }

    /// <summary>
    /// Opens the block cache rooted at <paramref name="directory"/> on the operating system's file system, recovering any
    /// valid region files left by a prior incarnation.
    /// </summary>
    public static ValueTask<BlockCache> OpenAsync(string directory, ArchiveOptions options, CancellationToken cancellationToken = default) =>
        OpenAsync(directory, options, WalFileSystem.Os, BlockCacheMutant.None, cancellationToken);

    /// <summary>
    /// Opens the block cache rooted at <paramref name="directory"/> over <paramref name="fileSystem"/>, recovering any valid
    /// region files left by a prior incarnation.
    /// </summary>
    public static ValueTask<BlockCache> OpenAsync(
        string directory,
        ArchiveOptions options,
        WalFileSystem fileSystem,
        CancellationToken cancellationToken = default) =>
        OpenAsync(directory, options, fileSystem, BlockCacheMutant.None, cancellationToken);

    /// <summary>
    /// Opens the block cache with an explicit test mutant, for verifying that the test suite detects each broken rule.
    /// </summary>
    internal static async ValueTask<BlockCache> OpenAsync(
        string directory,
        ArchiveOptions options,
        WalFileSystem fileSystem,
        BlockCacheMutant mutant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystem);
        options.Validate();

        var normalized = WalFileSystem.Normalize(directory);
        EnsureDirectory(fileSystem, normalized);

        var directoryLock = fileSystem.OpenLock(Path.Combine(normalized, LockFileName));
        var (smallMaxRegions, mainMaxRegions, ghostBudgetBytes) = ComputeLayout(options.BlockCacheBytes, options.BlockCacheRegionBytes);
        var cache = new BlockCache(
            normalized,
            fileSystem,
            directoryLock,
            options.BlockCacheBytes,
            options.BlockCacheRegionBytes,
            smallMaxRegions,
            mainMaxRegions,
            ghostBudgetBytes,
            mutant);

        try
        {
            await cache.RecoverAsync(cancellationToken).ConfigureAwait(false);
            return cache;
        }
        catch
        {
            cache.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the cached block of <paramref name="object"/> at <paramref name="blockOffset"/> of <paramref name="blockLength"/>
    /// bytes, verifying its CRC-32C before returning it, or returns <see langword="null"/> on a miss or corrupt block.
    /// </summary>
    /// <remarks>
    /// Ownership of the returned <see cref="ArcBuffer"/> transfers to the caller (conventions.md §1). A block whose CRC-32C
    /// or framing fails is invalidated and treated as a miss so the caller re-fetches from the object store.
    /// </remarks>
    public ValueTask<ArcBuffer?> TryReadAsync(
        ObjectName @object,
        long blockOffset,
        int blockLength,
        CancellationToken cancellationToken = default) =>
        TryReadAsync(@object, blockOffset, blockLength, recordAccess: true, cancellationToken);

    /// <summary>
    /// Reads a cached block, optionally recording the access against S3-FIFO frequency counters; compaction input reads pass
    /// <paramref name="recordAccess"/> as <see langword="false"/> so they neither admit nor promote cold blocks.
    /// </summary>
    internal async ValueTask<ArcBuffer?> TryReadAsync(
        ObjectName @object,
        long blockOffset,
        int blockLength,
        bool recordAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentOutOfRangeException.ThrowIfNegative(blockOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockLength, 1);

        var key = new BlockKey(@object, blockOffset);
        CacheEntry? entry;
        Region? region;
        lock (_gate)
        {
            if (_disposed || !_index.TryGetValue(key, out entry) || entry.BlockLength != blockLength)
            {
                ArchiveMetrics.RecordBlockCacheMiss();
                return null;
            }

            if (recordAccess && entry.Frequency < MaxFrequency)
            {
                entry.Frequency++;
            }

            region = entry.Region;
            region.PinCount++;
            _activeOps++;
        }

        try
        {
            var payload = await ReadVerifiedPayloadAsync(region.File, entry, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                InvalidateEntry(key, entry);
                ArchiveMetrics.RecordBlockCacheCorrupt();
                ArchiveMetrics.RecordBlockCacheMiss();
                return null;
            }

            ArchiveMetrics.RecordBlockCacheHit();
            return payload;
        }
        finally
        {
            ReleasePin(region);
            ExitOp();
        }
    }

    /// <summary>
    /// Admits <paramref name="block"/> for <c>(object, blockOffset)</c> into the cache according to S3-FIFO admission rules
    /// (archive-lsm.md §8.7), evicting the oldest region in FIFO order when its queue reaches budget.
    /// </summary>
    public async ValueTask AdmitAsync(
        ObjectName @object,
        long blockOffset,
        ReadOnlySequence<byte> block,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentOutOfRangeException.ThrowIfNegative(blockOffset);
        if (block.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(block), "A cached block holds at least one byte.");
        }

        if (_capacityBytes == 0 || block.Length > int.MaxValue)
        {
            return;
        }

        var payloadLength = (int)block.Length;
        var entryLength = EntryTotalLength(@object.Value.Length, payloadLength);
        if (entryLength > _regionBytes - RegionHeaderBytes)
        {
            return;
        }

        var key = new BlockKey(@object, blockOffset);
        lock (_gate)
        {
            if (_disposed || _index.ContainsKey(key))
            {
                return;
            }

            _activeOps++;
        }

        try
        {
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                    await _writeLock.WaitAsync(waitCts.Token).ConfigureAwait(false);
                }
                else
                {
                    await _writeLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                CacheQueue target;
                lock (_gate)
                {
                    if (_disposed || _index.ContainsKey(key))
                    {
                        return;
                    }

                    var wasGhost = RemoveGhostLocked(key);
                    if (_mutant == BlockCacheMutant.AdmitsDirectlyToMain)
                    {
                        target = CacheQueue.Main;
                    }
                    else if (wasGhost)
                    {
                        target = _mutant == BlockCacheMutant.SkipsGhostPromotion ? CacheQueue.Small : CacheQueue.Main;
                    }
                    else
                    {
                        target = CacheQueue.Small;
                    }
                }

                // Shared write and eviction I/O uses the cache's shutdown token rather than the caller's token so one
                // cancelled reader cannot abort an eviction mid-loop and drop remaining hot entries.
                try
                {
                    await AppendToQueueAsync(target, key, block, payloadLength, initialFrequency: 0, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    // Cache shutdown interrupted the in-flight append; any partial active region is already sealed.
                }
            }
            finally
            {
                _ = _writeLock.Release();
            }
        }
        finally
        {
            ExitOp();
        }
    }

    /// <summary>
    /// Removes the entry for <c>(object, blockOffset)</c> from the cache index, if present. Its space in its region file is
    /// reclaimed when that region is evicted in FIFO order.
    /// </summary>
    public void Invalidate(ObjectName @object, long blockOffset)
    {
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentOutOfRangeException.ThrowIfNegative(blockOffset);
        lock (_gate)
        {
            if (!_disposed)
            {
                _ = _index.Remove(new BlockKey(@object, blockOffset));
            }
        }
    }

    /// <summary>
    /// Writes every block of <paramref name="encoded"/> through the cache after its object has been uploaded
    /// (archive-lsm.md §8.8).
    /// </summary>
    internal async ValueTask AdmitEncodedObjectAsync(ObjectName @object, EncodedRunObject encoded, CancellationToken cancellationToken)
    {
        var sequence = encoded.Content.AsReadOnlySequence();
        foreach (var block in encoded.Blocks)
        {
            await AdmitAsync(@object, block.Offset, sequence.Slice(block.Offset, block.Length), cancellationToken).ConfigureAwait(false);
        }
    }

    internal bool Contains(ObjectName @object, long blockOffset)
    {
        lock (_gate)
        {
            return !_disposed && _index.ContainsKey(new BlockKey(@object, blockOffset));
        }
    }

    internal bool IsInQueue(ObjectName @object, long blockOffset, CacheQueue queue)
    {
        lock (_gate)
        {
            return !_disposed && _index.TryGetValue(new BlockKey(@object, blockOffset), out var entry) && entry.Region.Queue == queue;
        }
    }

    internal bool IsInGhost(ObjectName @object, long blockOffset)
    {
        lock (_gate)
        {
            return !_disposed && _ghostIndex.ContainsKey(new BlockKey(@object, blockOffset));
        }
    }

    /// <summary>
    /// Closes open region file handles and releases the cache directory lock without deleting region files, so the next
    /// incarnation can recover verified blocks from disk.
    /// </summary>
    public void Dispose()
    {
        List<Region>? toClose = null;
        long metricDelta = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _index.Clear();
            _ghostIndex.Clear();
            _ghostOrder.Clear();
            _ghostBytes = 0;

            CollectForShutdown(_small, ref toClose);
            CollectForShutdown(_main, ref toClose);
            _diskBytes = 0;
            metricDelta = -_reportedMetricBytes;
            _reportedMetricBytes = 0;
        }

        if (metricDelta != 0)
        {
            ArchiveMetrics.RecordBlockCacheSizeBytes(metricDelta);
        }

        _shutdown.Cancel();

        if (toClose is not null)
        {
            foreach (var region in toClose)
            {
                region.File.Dispose();
            }
        }

        ExitOp();
    }

    private void ExitOp()
    {
        bool disposeShared;
        lock (_gate)
        {
            _activeOps--;
            disposeShared = _activeOps == 0;
        }

        if (disposeShared)
        {
            _directoryLock.Dispose();
            _writeLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private static void CollectForShutdown(QueueState queue, ref List<Region>? toClose)
    {
        queue.Active = null;
        while (queue.Regions.Count > 0)
        {
            var region = queue.Regions.Dequeue();
            region.Retired = true;
            region.DeleteOnRetire = false;
            if (region.PinCount == 0 && !region.Closed)
            {
                region.Closed = true;
                (toClose ??= []).Add(region);
            }
        }
    }

    private static void EnsureDirectory(WalFileSystem fileSystem, string directory)
    {
        if (fileSystem.DirectoryExists(directory))
        {
            return;
        }

        var missing = new Stack<string>();
        var current = directory;
        while (!string.IsNullOrEmpty(current) && !fileSystem.DirectoryExists(current))
        {
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        while (missing.Count > 0)
        {
            fileSystem.CreateDirectory(missing.Pop());
        }
    }

    private static (int SmallMaxRegions, int MainMaxRegions, long GhostBudgetBytes) ComputeLayout(long capacityBytes, long regionBytes)
    {
        if (capacityBytes == 0)
        {
            return (0, 0, 0);
        }

        var totalRegions = (int)Math.Min(capacityBytes / regionBytes, int.MaxValue);
        Debug.Assert(totalRegions >= 2, "ArchiveOptions.Validate requires a positive budget of at least two regions.");

        // S3-FIFO allocates ~10% to Small and ~90% to Main; with at least 4 regions, keep at least 2 regions in Small so
        // blocks written late in a Small region remain resident while the next Small region fills.
        var smallMaxRegions = totalRegions >= 4 ? Math.Max(2, totalRegions / 10) : 1;
        var mainMaxRegions = totalRegions - smallMaxRegions;
        var ghostBudgetBytes = mainMaxRegions * regionBytes;
        return (smallMaxRegions, mainMaxRegions, ghostBudgetBytes);
    }

    private static long EntryTotalLength(int nameLength, int payloadLength) =>
        (long)EntryPrefixBytes + nameLength + CrcBytes + payloadLength + CrcBytes;

    private static bool IsRegionFileCandidate(ReadOnlySpan<char> fileName) =>
        fileName.Length == RegionFileNameLength
        && fileName.StartsWith(RegionPrefix, StringComparison.Ordinal)
        && fileName.EndsWith(RegionSuffix, StringComparison.Ordinal)
        && ulong.TryParse(fileName.Slice(RegionPrefix.Length, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);

    private static bool TryParseRegionId(ReadOnlySpan<char> fileName, out ulong regionId)
    {
        regionId = 0;
        if (fileName.Length != RegionFileNameLength
            || !fileName.StartsWith(RegionPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(RegionSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        return ulong.TryParse(fileName.Slice(RegionPrefix.Length, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out regionId)
            && regionId is > 0 and < ulong.MaxValue;
    }

    private string RegionPath(ulong regionId) =>
        Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"{RegionPrefix}{regionId:x16}{RegionSuffix}"));

    private async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        var files = _fileSystem.EnumerateFilesWithLengths(_directory, IsRegionFileCandidate);
        var discovered = new List<(ulong Id, string Path, long Length)>(files.Count);
        foreach (var file in files)
        {
            if (TryParseRegionId(Path.GetFileName(file.Path.AsSpan()), out var id))
            {
                discovered.Add((id, file.Path, file.Length));
            }
            else
            {
                _fileSystem.DeleteFile(file.Path);
            }
        }

        discovered.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        ulong maxId = 0;
        foreach (var (id, path, fileLength) in discovered)
        {
            if (id > maxId)
            {
                maxId = id;
            }

            if (_capacityBytes == 0 || fileLength < RegionHeaderBytes || fileLength > _regionBytes)
            {
                _fileSystem.DeleteFile(path);
                continue;
            }

            await RecoverRegionAsync(id, path, fileLength, cancellationToken).ConfigureAwait(false);
        }

        _nextRegionId = maxId + 1;
        TrimRecoveredQueue(_small);
        TrimRecoveredQueue(_main);
    }

    private async ValueTask RecoverRegionAsync(ulong id, string path, long fileLength, CancellationToken cancellationToken)
    {
        if (fileLength < RegionHeaderBytes)
        {
            _fileSystem.DeleteFile(path);
            return;
        }

        IWalFileSystemFile? file = _fileSystem.OpenFile(path, direct: false);
        var deleteFile = true;
        try
        {
            var headerBytes = new byte[RegionHeaderBytes];
            if (!await ReadExactAsync(file, headerBytes, 0, cancellationToken).ConfigureAwait(false)
                || !TryParseRegionHeader(headerBytes, id, out var queue))
            {
                return;
            }

            List<(BlockKey Key, long PayloadOffset, int Length)> recovered = [];
            long offset = RegionHeaderBytes;
            var prefix = new byte[EntryPrefixBytes];
            var nameAndCrc = new byte[ObjectName.MaxLength + CrcBytes];

            while (offset + EntryPrefixBytes <= fileLength)
            {
                if (!await ReadExactAsync(file, prefix, offset, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != EntryMagic)
                {
                    break;
                }

                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(4));
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(6));
                var blockOffset = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(8));
                var blockLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(16));
                if (nameLength is < 1 or > ObjectName.MaxLength || flags != 0 || blockOffset < 0 || blockLength < 1)
                {
                    break;
                }

                var entryLength = EntryTotalLength(nameLength, blockLength);
                if (entryLength > _regionBytes - RegionHeaderBytes || entryLength > fileLength - offset)
                {
                    break;
                }

                var tailLength = nameLength + CrcBytes;
                if (!await ReadExactAsync(file, nameAndCrc.AsMemory(0, tailLength), offset + EntryPrefixBytes, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var expectedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(nameAndCrc.AsSpan(nameLength, CrcBytes));
                if (ComputeEntryHeaderCrc(prefix, nameAndCrc.AsSpan(0, nameLength)) != expectedHeaderCrc)
                {
                    break;
                }

                var nameText = Encoding.ASCII.GetString(nameAndCrc, 0, nameLength);
                if (!ObjectName.IsValid(nameText))
                {
                    break;
                }

                var payloadOffset = offset + EntryPrefixBytes + tailLength;
                recovered.Add((new BlockKey(new ObjectName(nameText), blockOffset), payloadOffset, blockLength));
                offset += entryLength;
            }

            if (recovered.Count == 0)
            {
                return;
            }

            var region = new Region(id, path, queue, file, fileLength);
            file = null;
            deleteFile = false;

            foreach (var (key, payloadOffset, blockLength) in recovered)
            {
                region.Entries.Add(new CacheEntry(key, region, payloadOffset, blockLength, initialFrequency: 0));
            }

            lock (_gate)
            {
                var target = queue == CacheQueue.Small ? _small : _main;
                target.Regions.Enqueue(region);
                foreach (var entry in region.Entries)
                {
                    _index[entry.Key] = entry;
                }

                _diskBytes += region.Length;
                _reportedMetricBytes += region.Length;
            }

            ArchiveMetrics.RecordBlockCacheSizeBytes(region.Length);
        }
        finally
        {
            file?.Dispose();
            if (deleteFile)
            {
                _fileSystem.DeleteFile(path);
            }
        }
    }

    private void TrimRecoveredQueue(QueueState queue)
    {
        while (true)
        {
            Region? oldest = null;
            lock (_gate)
            {
                if (queue.Regions.Count <= queue.MaxRegions)
                {
                    return;
                }

                oldest = queue.Regions.Dequeue();
                foreach (var entry in oldest.Entries)
                {
                    if (_index.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    {
                        _ = _index.Remove(entry.Key);
                    }
                }
            }

            RetireRegion(oldest, deleteFile: true);
            ArchiveMetrics.RecordBlockCacheEviction();
        }
    }

    private async ValueTask AppendToQueueAsync(
        CacheQueue queue,
        BlockKey key,
        ReadOnlySequence<byte> block,
        int payloadLength,
        int initialFrequency,
        CancellationToken cancellationToken)
    {
        var queueState = queue == CacheQueue.Small ? _small : _main;
        var entryLength = EntryTotalLength(key.Object.Value.Length, payloadLength);

        Region? active = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (queueState.Active is { } current && current.Length + entryLength <= _regionBytes)
            {
                active = current;
                active.PinCount++;
            }
            else
            {
                queueState.Active = null;
            }
        }

        if (active is not null)
        {
            try
            {
                await WriteEntryToRegionAsync(queueState, active, key, block, payloadLength, initialFrequency, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReleasePin(active);
            }

            return;
        }

        if (queue == CacheQueue.Small)
        {
            bool shouldEvict;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                shouldEvict = queueState.Regions.Count >= queueState.MaxRegions && _mutant != BlockCacheMutant.SkipsFifoRegionEviction;
            }

            if (shouldEvict)
            {
                await EvictOldestSmallRegionAsync(cancellationToken).ConfigureAwait(false);
            }

            var opened = await OpenActiveRegionAsync(CacheQueue.Small, cancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                return;
            }

            try
            {
                await WriteEntryToRegionAsync(queueState, opened, key, block, payloadLength, initialFrequency, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReleasePin(opened);
            }

            return;
        }

        // CacheQueue.Main: write the incoming entry into the freshly opened region first, then evict at most one oldest
        // Main region into that region's remaining capacity so a full hot Main region cannot cascade or livelock.
        var target = await OpenActiveRegionAsync(CacheQueue.Main, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return;
        }

        try
        {
            await WriteEntryToRegionAsync(queueState, target, key, block, payloadLength, initialFrequency, cancellationToken).ConfigureAwait(false);

            bool shouldEvictMain;
            lock (_gate)
            {
                shouldEvictMain = !_disposed
                    && queueState.Regions.Count > queueState.MaxRegions
                    && _mutant != BlockCacheMutant.SkipsFifoRegionEviction;
            }

            if (shouldEvictMain)
            {
                await EvictOldestMainRegionAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleasePin(target);
        }
    }

    private async ValueTask EvictOldestSmallRegionAsync(CancellationToken cancellationToken)
    {
        Region oldest;
        List<CacheEntry> candidates;
        lock (_gate)
        {
            if (_disposed || _small.Regions.Count == 0)
            {
                return;
            }

            oldest = _small.Regions.Dequeue();
            oldest.PinCount++;
            candidates = CollectLiveEntriesLocked(oldest);
        }

        var recordEviction = false;
        try
        {
            foreach (var entry in candidates)
            {
                bool shouldPromote;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        break;
                    }

                    if (!_index.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                    {
                        continue;
                    }

                    shouldPromote = entry.Frequency > 0 && _mutant != BlockCacheMutant.SkipsSmallPromotion;
                    if (!shouldPromote)
                    {
                        _ = _index.Remove(entry.Key);
                        AddGhostLocked(entry.Key, entry.BlockLength);
                    }
                }

                if (!shouldPromote)
                {
                    continue;
                }

                var payload = await ReadVerifiedPayloadAsync(oldest.File, entry, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    InvalidateEntry(entry.Key, entry);
                    ArchiveMetrics.RecordBlockCacheCorrupt();
                    continue;
                }

                using (payload.Value)
                {
                    await AppendToQueueAsync(
                        CacheQueue.Main,
                        entry.Key,
                        payload.Value.AsReadOnlySequence(),
                        entry.BlockLength,
                        initialFrequency: 0,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    RemoveRegionEntriesLocked(oldest);
                    recordEviction = true;
                }
            }

            RetireRegion(oldest, deleteFile: true);
            ReleasePin(oldest);
        }

        if (recordEviction)
        {
            ArchiveMetrics.RecordBlockCacheEviction();
        }
    }

    private async ValueTask EvictOldestMainRegionAsync(Region target, CancellationToken cancellationToken)
    {
        Region oldest;
        List<CacheEntry> candidates;
        lock (_gate)
        {
            if (_disposed || _main.Regions.Count == 0)
            {
                return;
            }

            oldest = _main.Regions.Dequeue();
            Debug.Assert(!ReferenceEquals(oldest, target), "Evicted Main region must precede the newly opened target region.");
            oldest.PinCount++;
            candidates = CollectLiveEntriesLocked(oldest);
        }

        var recordEviction = false;
        try
        {
            foreach (var entry in candidates)
            {
                int nextFrequency;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        break;
                    }

                    if (!_index.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                    {
                        continue;
                    }

                    var needed = EntryTotalLength(entry.Key.Object.Value.Length, entry.BlockLength);
                    if (entry.Frequency <= 0
                        || _mutant == BlockCacheMutant.SkipsMainReinsertion
                        || !ReferenceEquals(_main.Active, target)
                        || target.Length + needed > _regionBytes)
                    {
                        _ = _index.Remove(entry.Key);
                        continue;
                    }

                    nextFrequency = entry.Frequency - 1;
                }

                var payload = await ReadVerifiedPayloadAsync(oldest.File, entry, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    InvalidateEntry(entry.Key, entry);
                    ArchiveMetrics.RecordBlockCacheCorrupt();
                    continue;
                }

                using (payload.Value)
                {
                    await WriteEntryToRegionAsync(
                        _main,
                        target,
                        entry.Key,
                        payload.Value.AsReadOnlySequence(),
                        entry.BlockLength,
                        nextFrequency,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    RemoveRegionEntriesLocked(oldest);
                    recordEviction = true;
                }
            }

            RetireRegion(oldest, deleteFile: true);
            ReleasePin(oldest);
        }

        if (recordEviction)
        {
            ArchiveMetrics.RecordBlockCacheEviction();
        }
    }

    private List<CacheEntry> CollectLiveEntriesLocked(Region region)
    {
        var live = new List<CacheEntry>(region.Entries.Count);
        foreach (var entry in region.Entries)
        {
            if (_index.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                live.Add(entry);
            }
        }

        return live;
    }

    private void RemoveRegionEntriesLocked(Region region)
    {
        foreach (var entry in region.Entries)
        {
            if (_index.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _ = _index.Remove(entry.Key);
            }
        }
    }

    private async ValueTask<Region?> OpenActiveRegionAsync(CacheQueue queue, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }
        }

        var id = _nextRegionId++;
        var path = RegionPath(id);
        IWalFileSystemFile? file = _fileSystem.CreateFile(path, replace: true, writeThrough: false, preallocationSize: 0);
        var deletePath = true;
        try
        {
            var header = new byte[RegionHeaderBytes];
            WriteRegionHeader(header, id, queue);
            await file.WriteAsync(header, 0, cancellationToken).ConfigureAwait(false);
            var region = new Region(id, path, queue, file, RegionHeaderBytes);
            file = null;

            lock (_gate)
            {
                if (_disposed)
                {
                    file = region.File;
                    return null;
                }

                var state = queue == CacheQueue.Small ? _small : _main;
                region.PinCount = 1;
                state.Regions.Enqueue(region);
                state.Active = region;
                _diskBytes += RegionHeaderBytes;
                _reportedMetricBytes += RegionHeaderBytes;
                deletePath = false;
            }

            ArchiveMetrics.RecordBlockCacheSizeBytes(RegionHeaderBytes);
            return region;
        }
        finally
        {
            file?.Dispose();
            if (deletePath)
            {
                _fileSystem.DeleteFile(path);
            }
        }
    }

    private async ValueTask WriteEntryToRegionAsync(
        QueueState queueState,
        Region region,
        BlockKey key,
        ReadOnlySequence<byte> block,
        int payloadLength,
        int initialFrequency,
        CancellationToken cancellationToken)
    {
        var nameBytes = Encoding.ASCII.GetBytes(key.Object.Value);
        var headerLength = EntryPrefixBytes + nameBytes.Length + CrcBytes;
        var header = new byte[headerLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, EntryMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), key.BlockOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), payloadLength);
        nameBytes.CopyTo(header.AsSpan(EntryPrefixBytes));
        var headerCrc = ComputeEntryHeaderCrc(header.AsSpan(0, EntryPrefixBytes), nameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(EntryPrefixBytes + nameBytes.Length), headerCrc);

        var payloadCrcBytes = new byte[CrcBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payloadCrcBytes, ComputePayloadCrc(key, payloadLength, block));

        var entryOffset = region.Length;
        var payloadOffset = entryOffset + headerLength;
        var entryLength = (long)headerLength + payloadLength + CrcBytes;

        var buffers = new List<ReadOnlyMemory<byte>>(4) { header };
        foreach (var segment in block)
        {
            if (!segment.IsEmpty)
            {
                buffers.Add(segment);
            }
        }

        buffers.Add(payloadCrcBytes);
        try
        {
            await region.File.WriteAsync(buffers, entryOffset, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(queueState.Active, region))
                {
                    queueState.Active = null;
                }
            }

            throw;
        }

        var entry = new CacheEntry(key, region, payloadOffset, payloadLength, initialFrequency);
        var recordMetric = false;
        lock (_gate)
        {
            region.Length += entryLength;
            if (!_disposed && !region.Retired)
            {
                region.Entries.Add(entry);
                _index[key] = entry;
                _diskBytes += entryLength;
                _reportedMetricBytes += entryLength;
                recordMetric = true;
            }
        }

        if (recordMetric)
        {
            ArchiveMetrics.RecordBlockCacheSizeBytes(entryLength);
        }
    }

    private async ValueTask<ArcBuffer?> ReadVerifiedPayloadAsync(
        IWalFileSystemFile file,
        CacheEntry entry,
        CancellationToken cancellationToken)
    {
        var payloadLength = entry.BlockLength;
        var totalLength = payloadLength + CrcBytes;
        using var writer = new ArcBufferWriter(totalLength);
        long offset = entry.PayloadOffset;
        for (var read = 0; read < totalLength;)
        {
            var page = writer.GetMemory();
            var count = await file.ReadAsync(page[..Math.Min(page.Length, totalLength - read)], offset + read, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }

            writer.AdvanceWriter(count);
            read += count;
        }

        var payload = writer.ConsumeSlice(payloadLength);
        if (_mutant != BlockCacheMutant.SkipsCrcCheck)
        {
            Span<byte> crcBytes = stackalloc byte[CrcBytes];
            writer.Reader.Consume(crcBytes);
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
            var actualCrc = ComputePayloadCrc(entry.Key, payloadLength, payload.AsReadOnlySequence());
            if (actualCrc != expectedCrc)
            {
                payload.Dispose();
                return null;
            }
        }

        return payload;
    }

    private void InvalidateEntry(BlockKey key, CacheEntry expected)
    {
        lock (_gate)
        {
            if (!_disposed && _index.TryGetValue(key, out var current) && ReferenceEquals(current, expected))
            {
                _ = _index.Remove(key);
            }
        }
    }

    private void RetireRegion(Region region, bool deleteFile)
    {
        bool closeNow;
        long bytesRemoved = 0;
        lock (_gate)
        {
            var wasRetired = region.Retired;
            region.Retired = true;
            if (_disposed)
            {
                region.DeleteOnRetire = false;
            }
            else
            {
                region.DeleteOnRetire = deleteFile;
                if (!wasRetired && deleteFile)
                {
                    bytesRemoved = region.Length;
                    _diskBytes -= bytesRemoved;
                    _reportedMetricBytes -= bytesRemoved;
                }
            }

            closeNow = region.PinCount == 0 && !region.Closed;
            if (closeNow)
            {
                region.Closed = true;
            }
        }

        if (bytesRemoved > 0)
        {
            ArchiveMetrics.RecordBlockCacheSizeBytes(-bytesRemoved);
        }

        if (closeNow)
        {
            FinalizeRetiredRegion(region);
        }
    }

    private void ReleasePin(Region region)
    {
        bool closeNow;
        lock (_gate)
        {
            region.PinCount--;
            closeNow = region.Retired && region.PinCount == 0 && !region.Closed;
            if (closeNow)
            {
                region.Closed = true;
            }
        }

        if (closeNow)
        {
            FinalizeRetiredRegion(region);
        }
    }

    private void FinalizeRetiredRegion(Region region)
    {
        region.File.Dispose();
        if (region.DeleteOnRetire)
        {
            _fileSystem.DeleteFile(region.Path);
        }
    }

    private void AddGhostLocked(BlockKey key, int blockLength)
    {
        if (_ghostBudgetBytes <= 0)
        {
            return;
        }

        if (_ghostIndex.TryGetValue(key, out var existing))
        {
            _ghostOrder.Remove(existing);
            _ghostBytes -= existing.Value.BlockLength;
        }

        var node = _ghostOrder.AddLast(new GhostEntry(key, blockLength));
        _ghostIndex[key] = node;
        _ghostBytes += blockLength;

        while (_ghostBytes > _ghostBudgetBytes && _ghostOrder.First is { } oldest)
        {
            _ghostOrder.RemoveFirst();
            _ = _ghostIndex.Remove(oldest.Value.Key);
            _ghostBytes -= oldest.Value.BlockLength;
        }
    }

    private bool RemoveGhostLocked(BlockKey key)
    {
        if (!_ghostIndex.Remove(key, out var node))
        {
            return false;
        }

        _ghostOrder.Remove(node);
        _ghostBytes -= node.Value.BlockLength;
        return true;
    }

    private int CountInQueue(CacheQueue queue)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var entry in _index.Values)
            {
                if (entry.Region.Queue == queue)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private static async ValueTask<bool> ReadExactAsync(
        IWalFileSystemFile file,
        Memory<byte> destination,
        long offset,
        CancellationToken cancellationToken)
    {
        for (var read = 0; read < destination.Length;)
        {
            var count = await file.ReadAsync(destination[read..], offset + read, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static void WriteRegionHeader(Span<byte> header, ulong regionId, CacheQueue queue)
    {
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, RegionMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], regionId);
        header[16] = (byte)queue;
        var crc = ComputeCrc32C(header[..20]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], crc);
    }

    private static bool TryParseRegionHeader(ReadOnlySpan<byte> header, ulong expectedRegionId, out CacheQueue queue)
    {
        queue = default;
        if (header.Length != RegionHeaderBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != RegionMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != FormatVersion
            || BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) != expectedRegionId
            || header[17] != 0
            || header[18] != 0
            || header[19] != 0)
        {
            return false;
        }

        if (header[16] is not ((byte)CacheQueue.Small or (byte)CacheQueue.Main))
        {
            return false;
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (ComputeCrc32C(header[..20]) != expectedCrc)
        {
            return false;
        }

        queue = (CacheQueue)header[16];
        return true;
    }

    private static uint ComputeCrc32C(ReadOnlySpan<byte> bytes)
    {
        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(bytes);
        return crc.GetCurrentHashAsUInt32();
    }

    private static uint ComputeEntryHeaderCrc(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> nameBytes)
    {
        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(prefix);
        crc.Append(nameBytes);
        return crc.GetCurrentHashAsUInt32();
    }

    private static uint ComputePayloadCrc(BlockKey key, int payloadLength, ReadOnlySequence<byte> payload)
    {
        Span<byte> metadata = stackalloc byte[12 + ObjectName.MaxLength];
        BinaryPrimitives.WriteInt64LittleEndian(metadata, key.BlockOffset);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[8..], payloadLength);
        var nameLength = Encoding.ASCII.GetBytes(key.Object.Value, metadata[12..]);

        var crc = new Crc32(Crc32ParameterSet.Crc32C);
        crc.Append(metadata[..(12 + nameLength)]);
        foreach (var segment in payload)
        {
            crc.Append(segment.Span);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    internal enum CacheQueue : byte
    {
        Small = 0,
        Main = 1,
    }

    internal readonly record struct BlockKey(ObjectName Object, long BlockOffset);

    private readonly record struct GhostEntry(BlockKey Key, int BlockLength);

    private sealed class QueueState(CacheQueue queue, int maxRegions)
    {
        public CacheQueue Queue { get; } = queue;

        public int MaxRegions { get; } = maxRegions;

        public Queue<Region> Regions { get; } = new();

        public Region? Active { get; set; }
    }

    private sealed class Region(ulong id, string path, CacheQueue queue, IWalFileSystemFile file, long length)
    {
        public ulong Id { get; } = id;

        public string Path { get; } = path;

        public CacheQueue Queue { get; } = queue;

        public IWalFileSystemFile File { get; } = file;

        public long Length { get; set; } = length;

        public List<CacheEntry> Entries { get; } = [];

        public int PinCount { get; set; }

        public bool Retired { get; set; }

        public bool DeleteOnRetire { get; set; }

        public bool Closed { get; set; }
    }

    private sealed class CacheEntry(BlockKey key, Region region, long payloadOffset, int blockLength, int initialFrequency)
    {
        public BlockKey Key { get; } = key;

        public Region Region { get; } = region;

        public long PayloadOffset { get; } = payloadOffset;

        public int BlockLength { get; } = blockLength;

        public int Frequency { get; set; } = initialFrequency;
    }
}

/// <summary>
/// Fault mutants of <see cref="BlockCache"/>, each violating one rule of archive-lsm.md §8.7 so its dedicated test proves
/// it catches the violation.
/// </summary>
internal enum BlockCacheMutant
{
    /// <summary>Production behavior.</summary>
    None,

    /// <summary>Skips per-block CRC-32C verification on read.</summary>
    SkipsCrcCheck,

    /// <summary>Admits new blocks directly into Main instead of Small, defeating one-hit-wonder scan resistance.</summary>
    AdmitsDirectlyToMain,

    /// <summary>Admits Ghost hits into Small instead of promoting them directly into Main.</summary>
    SkipsGhostPromotion,

    /// <summary>Evicts accessed Small entries to Ghost instead of promoting them into Main.</summary>
    SkipsSmallPromotion,

    /// <summary>Drops accessed Main entries on region eviction instead of reinserting them with decremented frequency.</summary>
    SkipsMainReinsertion,

    /// <summary>Never evicts old regions in FIFO order, letting volatile-disk usage grow past budget.</summary>
    SkipsFifoRegionEviction,

    /// <summary>Admits compaction input misses even when reads request no admission.</summary>
    AdmitsCompactionInputs,
}
