using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// A process's derived local cache of decided object-store ledger batches (object-ledgers.md §2.2, §6, OL-I11): holds
/// verified batches in memory (and optionally in a volatile local directory), each with its CRC-32C checked on every
/// hit. Any mismatch or disk error discards the entry and falls back to the object store without raising a fatal storage
/// error.
/// </summary>
internal sealed class ObjectLedgerCache
{
    private const int CrcLength = sizeof(uint);
    private const int MaxPendingDiskWrites = 256;
    private const string BatchExtension = ".batch";
    private const string TempExtension = ".tmp";

    private readonly long _capacityBytes;
    private readonly string? _directory;
    private readonly Lock _sync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<MemoryEntry>> _memory = [];
    private readonly LinkedList<MemoryEntry> _memoryLru = new();
    private readonly Dictionary<LedgerId, HashSet<Slot>> _memoryByLedger = [];
    private readonly Dictionary<CacheKey, LinkedListNode<DiskEntry>> _disk = [];
    private readonly LinkedList<DiskEntry> _diskLru = new();
    private readonly Dictionary<LedgerId, HashSet<Slot>> _diskByLedger = [];
    private readonly Dictionary<LedgerId, LedgerEpoch> _ledgerEpochs = [];
    private readonly Channel<DiskWriteRequest>? _diskWrites;
    private long _memoryBytes;
    private long _diskBytes;
    private long _fileSequence;
    private bool _directoryCreated;
    private Task? _diskWriterTask;

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerCache"/> class.</summary>
    /// <param name="capacityBytes">The byte budget of the cache; 0 disables caching.</param>
    /// <param name="directory">An optional volatile local directory for disk caching.</param>
    public ObjectLedgerCache(long capacityBytes, string? directory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
        _directory = directory;
        if (IsEnabled && _directory is not null)
        {
            _diskWrites = Channel.CreateBounded<DiskWriteRequest>(new BoundedChannelOptions(MaxPendingDiskWrites)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            ClearDiskDirectoryOnStartup();
        }
    }

    /// <summary>Gets whether the cache holds entries.</summary>
    public bool IsEnabled => _capacityBytes > 0;

    /// <summary>Gets the byte budget of each cache tier.</summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>
    /// Stores a verified decided batch in the memory cache (and, when configured, queues an asynchronous write to the
    /// volatile disk directory, dropping the disk write if the bounded queue is full). Never blocks on local disk I/O.
    /// </summary>
    public void Put(LedgerId ledger, Slot slot, ArcBuffer payload, uint crc32c)
    {
        if (!IsEnabled || payload.Length == 0 || payload.Length > _capacityBytes)
        {
            return;
        }

        PutBytes(ledger, slot, ToArray(payload), crc32c);
    }

    /// <summary>
    /// Stores a verified decided batch already copied into <paramref name="bytes"/> in the memory cache (and, when
    /// configured, queues an asynchronous write to the volatile disk directory, dropping the disk write if the bounded
    /// queue is full). Never blocks on local disk I/O.
    /// </summary>
    public void PutBytes(LedgerId ledger, Slot slot, byte[] bytes, uint crc32c)
    {
        if (!IsEnabled || bytes.Length == 0 || bytes.Length > _capacityBytes)
        {
            return;
        }

        var key = new CacheKey(ledger, slot);
        lock (_sync)
        {
            PutMemoryLocked(key, bytes, crc32c);
            if (_diskWrites is { } writes)
            {
                var generation = PeekGenerationLocked(ledger);
                if (writes.Writer.TryWrite(new DiskWriteRequest(key, bytes, crc32c, generation)))
                {
                    _ = EnterDiskIoLocked(ledger);
                    EnsureDiskWriterStartedLocked();
                }
            }

            AssertInvariantsLocked();
        }
    }

    /// <summary>
    /// Returns a verified cached batch for <paramref name="ledger"/> at <paramref name="slot"/>, or <see langword="null"/>
    /// on miss, checksum mismatch, or disk error (OL-I11). Memory hits complete synchronously without allocating a state
    /// machine.
    /// </summary>
    public ValueTask<byte[]?> TryGetAsync(LedgerId ledger, Slot slot, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        var key = new CacheKey(ledger, slot);
        MemoryEntry? hit;
        string? diskPath = null;
        long generation = 0;
        lock (_sync)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _memoryLru.Remove(node);
                _memoryLru.AddFirst(node);
                if (_disk.TryGetValue(key, out var diskNode))
                {
                    _diskLru.Remove(diskNode);
                    _diskLru.AddFirst(diskNode);
                }

                hit = node.Value;
            }
            else
            {
                hit = null;
                if (_directory is not null && _disk.TryGetValue(key, out var diskNode))
                {
                    diskPath = diskNode.Value.FilePath;
                    generation = EnterDiskIoLocked(ledger);
                }
            }
        }

        if (hit is { } entry)
        {
            if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, entry.Bytes) == entry.Crc32c)
            {
                return ValueTask.FromResult<byte[]?>(entry.Bytes);
            }

            // OL-I11: corrupt cached bytes are discarded and fall back to the object store.
            Remove(key);
            return ValueTask.FromResult<byte[]?>(null);
        }

        if (diskPath is null)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        return TryReadDiskAsync(key, diskPath, generation, cancellationToken);
    }

    /// <summary>
    /// Evicts every cached batch belonging to <paramref name="ledger"/> from memory and disk in
    /// <c>O(entries of ledger)</c> time (<c>O(1)</c> on miss).
    /// </summary>
    public void EvictLedger(LedgerId ledger)
    {
        if (!IsEnabled)
        {
            return;
        }

        List<string>? filesToDelete = null;
        lock (_sync)
        {
            // Bump the ledger's generation if any disk read/write for it is queued or in flight so it cannot resurrect entries.
            if (_ledgerEpochs.TryGetValue(ledger, out var epoch))
            {
                _ledgerEpochs[ledger] = epoch with { Generation = epoch.Generation + 1 };
            }

            if (_memoryByLedger.Remove(ledger, out var memSlots))
            {
                foreach (var slot in memSlots)
                {
                    var key = new CacheKey(ledger, slot);
                    if (_memory.Remove(key, out var node))
                    {
                        _memoryLru.Remove(node);
                        _memoryBytes -= node.Value.Bytes.Length;
                    }
                }
            }

            if (_diskByLedger.Remove(ledger, out var diskSlots))
            {
                filesToDelete = [];
                foreach (var slot in diskSlots)
                {
                    var key = new CacheKey(ledger, slot);
                    if (_disk.Remove(key, out var node))
                    {
                        _diskLru.Remove(node);
                        _diskBytes -= node.Value.Length;
                        filesToDelete.Add(node.Value.FilePath);
                    }
                }
            }

            AssertInvariantsLocked();
        }

        if (filesToDelete is not null)
        {
            foreach (var path in filesToDelete)
            {
                TryDeleteFile(path);
            }
        }
    }

    /// <summary>Drops in-memory entries while keeping the volatile disk tier intact, so tests can exercise cold-memory disk hits.</summary>
    internal void ClearMemoryForTest()
    {
        lock (_sync)
        {
            _memory.Clear();
            _memoryLru.Clear();
            _memoryByLedger.Clear();
            _memoryBytes = 0;
            AssertInvariantsLocked();
        }
    }

    /// <summary>Waits until every currently queued background disk write has finished.</summary>
    internal async Task FlushDiskWritesForTestAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? writer;
            lock (_sync)
            {
                writer = _diskWriterTask;
            }

            if (writer is null)
            {
                return;
            }

            await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears any leftover volatile cache files from prior runs (OL-I1, OL-I11): a process never adopts pre-existing files
    /// from <see cref="_directory"/> because they could belong to another store, cluster, or prefix.
    /// </summary>
    private void ClearDiskDirectoryOnStartup()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            Volatile.Write(ref _directoryCreated, true);
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(BatchExtension, StringComparison.Ordinal) || name.EndsWith(TempExtension, StringComparison.Ordinal))
                {
                    TryDeleteFile(path);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // OL-I11: cache directory startup failure never stops the process.
            if (exception is DirectoryNotFoundException)
            {
                Volatile.Write(ref _directoryCreated, false);
            }
        }
    }

    private void EnsureDiskWriterStartedLocked()
    {
        if (_diskWriterTask is not null)
        {
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            _diskWriterTask = Task.Run(DrainDiskWritesAsync, CancellationToken.None);
        }
    }

    private async Task DrainDiskWritesAsync()
    {
        var reader = _diskWrites!.Reader;
        while (true)
        {
            DiskWriteRequest request;
            lock (_sync)
            {
                if (!reader.TryRead(out request))
                {
                    _diskWriterTask = null;
                    return;
                }
            }

            await WriteDiskEntryAsync(request).ConfigureAwait(false);
        }
    }

    private void PutMemoryLocked(CacheKey key, byte[] bytes, uint crc32c)
    {
        if (_memory.TryGetValue(key, out var existing))
        {
            _memoryLru.Remove(existing);
            _memoryBytes -= existing.Value.Bytes.Length;
            RemoveIndexLocked(_memoryByLedger, key);
        }

        while (_memoryBytes + bytes.Length > _capacityBytes && _memoryLru.Last is { } oldest)
        {
            _ = _memory.Remove(oldest.Value.Key);
            _memoryLru.RemoveLast();
            _memoryBytes -= oldest.Value.Bytes.Length;
            RemoveIndexLocked(_memoryByLedger, oldest.Value.Key);
        }

        if (_memoryBytes + bytes.Length <= _capacityBytes)
        {
            var node = new LinkedListNode<MemoryEntry>(new MemoryEntry(key, bytes, crc32c));
            _memory[key] = node;
            _memoryLru.AddFirst(node);
            AddIndexLocked(_memoryByLedger, key);
            _memoryBytes += bytes.Length;
        }
    }

    private async ValueTask<byte[]?> TryReadDiskAsync(CacheKey key, string path, long generation, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        uint storedCrc = 0;
        var deleteCorrupt = false;
        var ioError = false;
        var exited = false;

        try
        {
            try
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var fileLength = RandomAccess.GetLength(handle);
                var payloadLength = fileLength - CrcLength;
                if (payloadLength <= 0 || payloadLength > _capacityBytes || payloadLength > int.MaxValue)
                {
                    deleteCorrupt = true;
                }
                else
                {
                    var header = new byte[CrcLength];
                    var payload = new byte[(int)payloadLength];
                    if (await ReadExactAsync(handle, header, payload, cancellationToken).ConfigureAwait(false))
                    {
                        storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header);
                        if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload) == storedCrc)
                        {
                            bytes = payload;
                        }
                        else
                        {
                            deleteCorrupt = true;
                        }
                    }
                    else
                    {
                        deleteCorrupt = true;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                ioError = true;
            }
            catch (DirectoryNotFoundException)
            {
                Volatile.Write(ref _directoryCreated, false);
                ioError = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                ioError = true;
            }

            var invalidated = false;
            lock (_sync)
            {
                invalidated = ExitDiskIoLocked(key.Ledger, generation);
                exited = true;
                if (deleteCorrupt || ioError)
                {
                    _ = RemoveDiskRecordIfSamePathLocked(key, path);
                }
                else if (bytes is not null && !invalidated)
                {
                    if (_disk.TryGetValue(key, out var existing) && string.Equals(existing.Value.FilePath, path, StringComparison.Ordinal))
                    {
                        _diskLru.Remove(existing);
                        _diskLru.AddFirst(existing);
                    }

                    PutMemoryLocked(key, bytes, storedCrc);
                }

                AssertInvariantsLocked();
            }

            if (deleteCorrupt)
            {
                TryDeleteFile(path);
            }

            return invalidated ? null : bytes;
        }
        finally
        {
            if (!exited)
            {
                lock (_sync)
                {
                    _ = ExitDiskIoLocked(key.Ledger, generation);
                    AssertInvariantsLocked();
                }
            }
        }
    }

    private async Task WriteDiskEntryAsync(DiskWriteRequest request)
    {
        var key = request.Key;
        var exited = false;
        var moved = false;
        string? temp = null;

        try
        {
            lock (_sync)
            {
                if (IsInvalidatedLocked(key.Ledger, request.Generation))
                {
                    _ = ExitDiskIoLocked(key.Ledger, request.Generation);
                    exited = true;
                    AssertInvariantsLocked();
                    return;
                }
            }

            var path = NewFilePath(key);
            temp = string.Create(CultureInfo.InvariantCulture, $"{path}.{Guid.NewGuid():N}{TempExtension}");
            var written = false;

            try
            {
                if (!Volatile.Read(ref _directoryCreated))
                {
                    _ = Directory.CreateDirectory(_directory!);
                    Volatile.Write(ref _directoryCreated, true);
                }

                var header = new byte[CrcLength];
                BinaryPrimitives.WriteUInt32LittleEndian(header, request.Crc32c);
                using (var handle = File.OpenHandle(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    FileOptions.Asynchronous))
                {
                    ReadOnlyMemory<byte>[] buffers = [header, request.Bytes];
                    await RandomAccess.WriteAsync(handle, buffers, fileOffset: 0, CancellationToken.None).ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: false);
                moved = true;
                written = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                // OL-I11 / object-ledgers.md §6: disk cache failure never stops the process and never removes an existing
                // unrelated disk entry for the same key.
                if (exception is DirectoryNotFoundException)
                {
                    Volatile.Write(ref _directoryCreated, false);
                }
            }

            List<string>? evicted = null;
            var invalidated = false;
            lock (_sync)
            {
                invalidated = ExitDiskIoLocked(key.Ledger, request.Generation);
                exited = true;
                if (written && !invalidated)
                {
                    evicted = TouchDiskLocked(key, request.Bytes.Length, path);
                }

                AssertInvariantsLocked();
            }

            if (written && invalidated)
            {
                // Unique filename per write: deleting path never touches a newer generation's file.
                TryDeleteFile(path);
            }

            if (evicted is not null)
            {
                foreach (var evictedPath in evicted)
                {
                    TryDeleteFile(evictedPath);
                }
            }
        }
        finally
        {
            if (!moved && temp is not null)
            {
                TryDeleteFile(temp);
            }

            if (!exited)
            {
                lock (_sync)
                {
                    _ = ExitDiskIoLocked(key.Ledger, request.Generation);
                    AssertInvariantsLocked();
                }
            }
        }
    }

    private static async ValueTask<bool> ReadExactAsync(SafeFileHandle handle, byte[] header, byte[] payload, CancellationToken cancellationToken)
    {
        var total = header.Length + payload.Length;
        long offset = 0;
        Memory<byte>[] buffers = [header, payload];
        while (offset < total)
        {
            long read;
            if (offset == 0)
            {
                read = await RandomAccess.ReadAsync(handle, buffers, fileOffset: 0, cancellationToken).ConfigureAwait(false);
            }
            else if (offset < header.Length)
            {
                buffers[0] = header.AsMemory((int)offset);
                buffers[1] = payload;
                read = await RandomAccess.ReadAsync(handle, buffers, fileOffset: offset, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var payloadOffset = (int)(offset - header.Length);
                read = await RandomAccess.ReadAsync(handle, payload.AsMemory(payloadOffset), fileOffset: offset, cancellationToken).ConfigureAwait(false);
            }

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private List<string>? TouchDiskLocked(CacheKey key, int length, string filePath)
    {
        if (length <= 0 || length > _capacityBytes)
        {
            return [filePath];
        }

        List<string>? evicted = null;
        if (_disk.TryGetValue(key, out var existing))
        {
            _diskLru.Remove(existing);
            _diskBytes -= existing.Value.Length;
            RemoveIndexLocked(_diskByLedger, key);
            (evicted ??= []).Add(existing.Value.FilePath);
        }

        while (_diskBytes + length > _capacityBytes && _diskLru.Last is { } oldest)
        {
            _ = _disk.Remove(oldest.Value.Key);
            _diskLru.RemoveLast();
            _diskBytes -= oldest.Value.Length;
            RemoveIndexLocked(_diskByLedger, oldest.Value.Key);
            (evicted ??= []).Add(oldest.Value.FilePath);
        }

        if (_diskBytes + length <= _capacityBytes)
        {
            var node = new LinkedListNode<DiskEntry>(new DiskEntry(key, length, filePath));
            _disk[key] = node;
            _diskLru.AddFirst(node);
            AddIndexLocked(_diskByLedger, key);
            _diskBytes += length;
        }
        else
        {
            (evicted ??= []).Add(filePath);
        }

        return evicted;
    }

    private void Remove(CacheKey key)
    {
        string? diskPath = null;
        lock (_sync)
        {
            if (_memory.Remove(key, out var node))
            {
                _memoryLru.Remove(node);
                _memoryBytes -= node.Value.Bytes.Length;
                RemoveIndexLocked(_memoryByLedger, key);
            }

            if (_disk.Remove(key, out var diskNode))
            {
                _diskLru.Remove(diskNode);
                _diskBytes -= diskNode.Value.Length;
                RemoveIndexLocked(_diskByLedger, key);
                diskPath = diskNode.Value.FilePath;
            }

            AssertInvariantsLocked();
        }

        if (diskPath is not null)
        {
            TryDeleteFile(diskPath);
        }
    }

    private bool RemoveDiskRecordIfSamePathLocked(CacheKey key, string path)
    {
        if (!_disk.TryGetValue(key, out var diskNode) || !string.Equals(diskNode.Value.FilePath, path, StringComparison.Ordinal))
        {
            return false;
        }

        _ = _disk.Remove(key);
        _diskLru.Remove(diskNode);
        _diskBytes -= diskNode.Value.Length;
        RemoveIndexLocked(_diskByLedger, key);
        return true;
    }

    private long PeekGenerationLocked(LedgerId ledger) =>
        _ledgerEpochs.TryGetValue(ledger, out var existing) ? existing.Generation : 0;

    private bool IsInvalidatedLocked(LedgerId ledger, long snapshotGeneration) =>
        !_ledgerEpochs.TryGetValue(ledger, out var epoch) || epoch.Generation != snapshotGeneration;

    private long EnterDiskIoLocked(LedgerId ledger)
    {
        var epoch = _ledgerEpochs.TryGetValue(ledger, out var existing)
            ? existing with { InFlight = existing.InFlight + 1 }
            : new LedgerEpoch(Generation: 0, InFlight: 1);
        _ledgerEpochs[ledger] = epoch;
        return epoch.Generation;
    }

    private bool ExitDiskIoLocked(LedgerId ledger, long snapshotGeneration)
    {
        var epoch = _ledgerEpochs[ledger];
        var invalidated = epoch.Generation != snapshotGeneration;
        if (epoch.InFlight <= 1)
        {
            _ = _ledgerEpochs.Remove(ledger);
        }
        else
        {
            _ledgerEpochs[ledger] = epoch with { InFlight = epoch.InFlight - 1 };
        }

        return invalidated;
    }

    private static void AddIndexLocked(Dictionary<LedgerId, HashSet<Slot>> index, CacheKey key)
    {
        if (!index.TryGetValue(key.Ledger, out var slots))
        {
            index[key.Ledger] = slots = [];
        }

        _ = slots.Add(key.Slot);
    }

    private static void RemoveIndexLocked(Dictionary<LedgerId, HashSet<Slot>> index, CacheKey key)
    {
        if (index.TryGetValue(key.Ledger, out var slots))
        {
            _ = slots.Remove(key.Slot);
            if (slots.Count == 0)
            {
                _ = index.Remove(key.Ledger);
            }
        }
    }

    [Conditional("DEBUG")]
    private void AssertInvariantsLocked()
    {
        Debug.Assert(_memory.Count == _memoryLru.Count, "Memory dictionary and LRU list must have equal counts.");
        Debug.Assert(_disk.Count == _diskLru.Count, "Disk dictionary and LRU list must have equal counts.");
        Debug.Assert(_memoryBytes >= 0 && _memoryBytes <= _capacityBytes, "Memory byte count must remain within [0, capacity].");
        Debug.Assert(_diskBytes >= 0 && _diskBytes <= _capacityBytes, "Disk byte count must remain within [0, capacity].");
        var indexedMem = 0;
        foreach (var set in _memoryByLedger.Values)
        {
            Debug.Assert(set.Count > 0, "Empty ledger set must be removed from memory index.");
            indexedMem += set.Count;
        }

        Debug.Assert(indexedMem == _memory.Count, "Memory per-ledger index count must match memory cache count.");
        var indexedDisk = 0;
        foreach (var set in _diskByLedger.Values)
        {
            Debug.Assert(set.Count > 0, "Empty ledger set must be removed from disk index.");
            indexedDisk += set.Count;
        }

        Debug.Assert(indexedDisk == _disk.Count, "Disk per-ledger index count must match disk cache count.");
    }

    private string NewFilePath(CacheKey key)
    {
        var seq = Interlocked.Increment(ref _fileSequence);
        return Path.Combine(_directory!, string.Create(CultureInfo.InvariantCulture, $"l{key.Ledger.Value:D20}_s{key.Slot.Value:D20}_{seq:x}{BatchExtension}"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
        }
    }

    internal static byte[] ToArray(ArcBuffer payload)
    {
        var sequence = payload.AsReadOnlySequence();
        var bytes = new byte[sequence.Length];
        sequence.CopyTo(bytes);
        return bytes;
    }

    private readonly record struct CacheKey(LedgerId Ledger, Slot Slot);

    private readonly record struct MemoryEntry(CacheKey Key, byte[] Bytes, uint Crc32c);

    private readonly record struct DiskEntry(CacheKey Key, int Length, string FilePath);

    private readonly record struct DiskWriteRequest(CacheKey Key, byte[] Bytes, uint Crc32c, long Generation);

    private readonly record struct LedgerEpoch(long Generation, int InFlight);
}
