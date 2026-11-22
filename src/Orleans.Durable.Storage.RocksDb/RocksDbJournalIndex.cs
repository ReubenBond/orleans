using System.Buffers;
using System.Buffers.Binary;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using RocksDbSharp;

namespace Orleans.Durable.Storage.RocksDb;

/// <summary>
/// A node's durable derived journal index backed by WAL-less RocksDB with atomic multi-column-family flush
/// (journals.md §8.3).
/// </summary>
/// <remarks>
/// <para>
/// A node's index lives in <c>{root}/index/</c> (<see cref="ForNode"/>) with three column families:
/// <list type="bullet">
/// <item><description><c>default</c>: per-ledger watermarks (<c>LedgerId (8B BE)</c> → <c>NextSlot (8B LE)</c>).</description></item>
/// <item><description><c>key_index</c>: <c>LedgerId (8B BE) || SHA256(RowKey) (32B) || Slot (8B BE) || Index (4B BE)</c> (52 bytes) → <c>DefiningSlot (8B LE) || Ordinal (8B LE)</c> (16 bytes), with a 40-byte fixed-prefix bloom filter.</description></item>
/// <item><description><c>tombstones</c>: <c>LedgerId (8B BE) || SHA256(RowKey) (32B)</c> (40 bytes) → <c>DeletionTicks (8B LE) || DeleteSlot (8B LE) || DeleteIndex (4B LE) || RawKey</c>, with an 8-byte fixed-prefix bloom filter.</description></item>
/// </list>
/// </para>
/// <para>
/// Writes disable RocksDB's WAL (<c>disableWAL = true</c>) and land directly in RocksDB's native memtables; periodic
/// and clean-shutdown flushes invoke an atomic multi-CF flush (<c>atomic_flush = true</c>, <c>rocksdb_flush_cfs</c>)
/// so <c>key_index</c>, <c>tombstones</c>, and watermarks advance together in SSTs and <c>MANIFEST</c>. Memory-mapped
/// I/O is disabled (<c>allow_mmap_reads = false</c>, <c>allow_mmap_writes = false</c>) so storage I/O errors return as
/// status codes rather than <c>SIGBUS</c>.
/// </para>
/// <para>
/// This is the journal index's local storage seam (conventions.md §14): each operation reports storage failures to
/// <see cref="FatalErrorHandler"/> with its own <see cref="FatalErrorSite"/>, which stops the process. RocksDB's
/// <c>Corruption</c> status is reported as <see cref="JournalIndexCorruptException"/> (triggering discard-and-rebuild),
/// and a lock file held by another owner is reported as <see cref="JournalIndexLockHeldException"/>.
/// </para>
/// </remarks>
public sealed class RocksDbJournalIndex : IJournalIndex, IAsyncDisposable
{
    /// <summary>The default column family storing per-ledger watermarks.</summary>
    public const string DefaultColumnFamily = "default";

    /// <summary>The column family storing per-ledger key-index entries.</summary>
    public const string KeyIndexColumnFamily = "key_index";

    /// <summary>The column family storing per-ledger tombstone records for expiry scans.</summary>
    public const string TombstonesColumnFamily = "tombstones";

    /// <summary>The default total memtable write-buffer budget across column families (64 MiB).</summary>
    public const long DefaultWriteBufferBytes = 64L * 1024 * 1024;

    /// <summary>The fixed prefix length on <see cref="KeyIndexColumnFamily"/> (<c>LedgerId (8B) || SHA256(RowKey) (32B)</c>).</summary>
    public const int KeyIndexPrefixLength = 8 + JournalKeyHash.Length;

    /// <summary>The fixed key length on <see cref="KeyIndexColumnFamily"/> (<c>Prefix (40B) || Slot (8B) || Index (4B)</c>).</summary>
    public const int KeyIndexKeyLength = KeyIndexPrefixLength + 8 + 4;

    /// <summary>The fixed value length on <see cref="KeyIndexColumnFamily"/> (<c>DefiningSlot (8B) || Ordinal (8B)</c>).</summary>
    public const int KeyIndexValueLength = 8 + 8;

    /// <summary>The fixed prefix length on <see cref="TombstonesColumnFamily"/> (<c>LedgerId (8B)</c>).</summary>
    public const int TombstonePrefixLength = 8;

    /// <summary>The fixed key length on <see cref="TombstonesColumnFamily"/> (<c>LedgerId (8B) || SHA256(RowKey) (32B)</c>).</summary>
    public const int TombstoneKeyLength = TombstonePrefixLength + JournalKeyHash.Length;

    /// <summary>The fixed header length of a <see cref="TombstonesColumnFamily"/> value (<c>DeletionTicks (8B) || DeleteSlot (8B) || DeleteIndex (4B)</c>).</summary>
    public const int TombstoneValueHeaderLength = 8 + 8 + 4;

    private const int ReaderThreads = 4;
    private const int WatermarkKeyLength = 8;
    private const int WatermarkValueLength = 8;
    private const int BloomBitsPerKey = 10;
    private const double MemtablePrefixBloomRatio = 0.1;
    private const string IOErrorPrefix = "IO error: ";

    private static readonly Lazy<DedicatedThreads> Readers =
        new(static () => new DedicatedThreads("journal-index-reader", ReaderThreads, ThreadPriority.Normal));

    private static readonly Lazy<DedicatedThreads> Checker =
        new(static () => new DedicatedThreads("journal-index-check", 1, ThreadPriority.Lowest));

    private static readonly WriteOptions WalDisabledWriteOptions = new WriteOptions().DisableWal(1);
    private static readonly ReadOptions PrefixReadOptions = new ReadOptions().SetPrefixSameAsStart(true);
    private static readonly ReadOptions CheckReadOptions = new ReadOptions().SetVerifyChecksums(true).SetFillCache(false).SetTotalOrderSeek(true);
    private static readonly FlushOptions AtomicFlushOptions = CreateAtomicFlushOptions();

    private readonly long _maxWriteBufferBytes;
    private readonly TimeProvider _timeProvider;
    private readonly ReaderWriterLockSlim _open = new();
    private readonly Lock _writerLock = new();
    private readonly List<PendingFileDeleteRange> _pendingFileDeletes = [];
    private DedicatedThreads? _writer;
    private RocksDbSharp.RocksDb? _database;
    private OpenState? _state;

    /// <summary>
    /// Initializes a new instance of <see cref="RocksDbJournalIndex"/> for the RocksDB directory at
    /// <paramref name="path"/>.
    /// </summary>
    public RocksDbJournalIndex(string path, long maxWriteBufferBytes = DefaultWriteBufferBytes, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWriteBufferBytes);
        Path = System.IO.Path.GetFullPath(path);
        _maxWriteBufferBytes = maxWriteBufferBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the RocksDB directory path.</summary>
    public string Path { get; }

    /// <summary>Gets the total memtable write-buffer budget across column families.</summary>
    public long MaxWriteBufferBytes => _maxWriteBufferBytes;

    /// <summary>
    /// Gets or sets a test fault hook: before each operation, the seam calls it with the operation's site and throws
    /// the returned exception, if any, in place of the operation.
    /// </summary>
    internal Func<FatalErrorSite, Exception?>? Fault { get; set; }

    /// <summary>
    /// Returns the index for the node whose root is <paramref name="root"/>: the directory <c>{root}/index/</c>.
    /// </summary>
    public static RocksDbJournalIndex ForNode(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return new RocksDbJournalIndex(System.IO.Path.Combine(root, "index"));
    }

    /// <summary>
    /// Creates the <see cref="DbOptions"/> configured for WAL-less atomic-flush, zero-mmap operation.
    /// </summary>
    internal static DbOptions CreateDbOptions(long maxWriteBufferBytes)
    {
        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true)
            .SetParanoidChecks(true)
            .SetAllowMmapReads(false)
            .SetAllowMmapWrites(false)
            .SetKeepLogFileNum(2)
            .SetDbWriteBufferSize((ulong)maxWriteBufferBytes);
        Native.Instance.rocksdb_options_set_atomic_flush(options.Handle, 1);
        Native.Instance.rocksdb_options_set_avoid_flush_during_shutdown(options.Handle, 1);
        return options;
    }

    /// <summary>
    /// Returns whether <paramref name="message"/>, a RocksDB status, reports that another owner holds the database's
    /// lock file: an answer, not a storage failure (conventions.md §14).
    /// </summary>
    internal static bool IsHeldLock(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.StartsWith(IOErrorPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var status = message.AsSpan(IOErrorPrefix.Length).TrimEnd();
        if (status.StartsWith("lock hold by current process, ", StringComparison.Ordinal))
        {
            return true;
        }

        if (status.StartsWith("While lock file: ", StringComparison.Ordinal))
        {
            return status.EndsWith(": Resource temporarily unavailable", StringComparison.Ordinal)
                || status.EndsWith(": Permission denied", StringComparison.Ordinal);
        }

        return status.StartsWith("Failed to create lock file: ", StringComparison.Ordinal)
            && status.EndsWith(": The process cannot access the file because it is being used by another process.", StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public async ValueTask<SortedDictionary<LedgerId, Slot>> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = EnsureWriter();
        return await writer.RunAsync(() => Guard(FatalErrorSite.JournalIndexOpen, () =>
        {
            OpenDatabase();
            return ReadWatermarks();
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask ApplyBatchAsync(in JournalIndexBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ledger = batch.Ledger;
        var nextWatermark = batch.NextWatermark;
        var keyWrites = batch.KeyWrites;
        var tombstoneWrites = batch.TombstoneWrites;
        // Do not detach from the writer task on caller cancellation while it reads the caller's pooled memory slices.
        return new ValueTask(WriteOnWriterThreadAsync(
            () => ApplyBatchCore(ledger, nextWatermark, keyWrites.Span, tombstoneWrites.Span)));
    }

    /// <inheritdoc/>
    public async ValueTask DropLedgersAsync(IReadOnlyList<LedgerId> ledgers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledgers);
        cancellationToken.ThrowIfCancellationRequested();
        if (ledgers.Count == 0)
        {
            return;
        }

        await WriteOnWriterThreadAsync(() => DropLedgersCore(ledgers)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<List<JournalIndexKeyEntry>> GetKeyEntriesAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot,
        CancellationToken cancellationToken = default) =>
        LookupKeyIndexAsync(
            state => GetKeyEntriesCore(state, ledger, keyHash, startSlot, startIndex, exclusiveEndSlot),
            cancellationToken);

    /// <inheritdoc/>
    public ValueTask<bool> HasKeyEntryAsync(
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot,
        CancellationToken cancellationToken = default) =>
        LookupKeyIndexAsync(
            state => HasKeyEntryCore(state, ledger, keyHash, startSlot, startIndex, exclusiveEndSlot),
            cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<List<JournalIndexTombstone>> ScanTombstonesAsync(
        LedgerId ledger,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Readers.Value.RunAsync(() => Guard(FatalErrorSite.JournalIndexScan, () =>
        {
            _open.EnterReadLock();
            try
            {
                return ScanTombstonesCore(RequireOpen(), ledger);
            }
            finally
            {
                _open.ExitReadLock();
            }
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = RequireWriter();
        _ = await writer.RunAsync(() => Guard(FatalErrorSite.JournalIndexFlush, () =>
        {
            FlushCore();
            return true;
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = EnsureWriter();
        _ = await writer.RunAsync(() => Guard(FatalErrorSite.JournalIndexReset, () =>
        {
            _open.EnterWriteLock();
            try
            {
                CloseCore();
                if (FileSystemAnswers.GetAttributes(Path) is not null)
                {
                    _ = FileSystemAnswers.WaitOutContention(
                        () =>
                        {
                            Directory.Delete(Path, recursive: true);
                            return true;
                        },
                        FileSystemAnswers.IsContention,
                        _timeProvider);
                }
            }
            finally
            {
                _open.ExitWriteLock();
            }

            OpenDatabase();
            return true;
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await Checker.Value.RunAsync(() => Guard(FatalErrorSite.JournalIndexCheck, () =>
        {
            _open.EnterReadLock();
            try
            {
                var state = RequireOpen();
                foreach (var family in state.AllColumnFamilies)
                {
                    using var iterator = state.Db.NewIterator(family, CheckReadOptions);
                    for (_ = iterator.SeekToFirst(); iterator.Valid(); _ = iterator.Next())
                    {
                        // Iterating with verify_checksums + total_order_seek verifies every block in the CF.
                    }

                    Native.Instance.rocksdb_iter_get_error(iterator.Handle);
                }
            }
            finally
            {
                _open.ExitReadLock();
            }

            return true;
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask CloseAsync()
    {
        DedicatedThreads? writer;
        lock (_writerLock)
        {
            writer = _writer;
            _writer = null;
        }

        if (writer is null)
        {
            return;
        }

        try
        {
            _ = await writer.RunAsync(() =>
            {
                _open.EnterWriteLock();
                try
                {
                    CloseCore();
                }
                finally
                {
                    _open.ExitWriteLock();
                }

                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _database?.Dispose();
        _database = null;
        _open.Dispose();
    }

    private Task WriteOnWriterThreadAsync(Action write)
    {
        var writer = RequireWriter();
        return writer.RunAsync(() => Guard(FatalErrorSite.JournalIndexWrite, () =>
        {
            write();
            return true;
        }));
    }

    private async ValueTask<T> LookupKeyIndexAsync<T>(Func<OpenState, T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Readers.Value.RunAsync(() => Guard(FatalErrorSite.JournalIndexLookup, () =>
        {
            _open.EnterReadLock();
            try
            {
                return read(RequireOpen());
            }
            finally
            {
                _open.ExitReadLock();
            }
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private DedicatedThreads EnsureWriter()
    {
        lock (_writerLock)
        {
            return _writer ??= new DedicatedThreads("journal-index-writer", 1, ThreadPriority.Normal);
        }
    }

    private DedicatedThreads RequireWriter()
    {
        lock (_writerLock)
        {
            return _writer ?? throw new ObjectDisposedException(nameof(RocksDbJournalIndex), "The journal index is not open.");
        }
    }

    private OpenState RequireOpen() =>
        _state ?? throw new JournalIndexCorruptException("The journal index did not open; it must be reset.");

    private T Guard<T>(FatalErrorSite operation, Func<T> work)
    {
        try
        {
            if (Fault?.Invoke(operation) is { } fault)
            {
                throw fault;
            }

            return work();
        }
        catch (RocksDbException exception) when (exception.Message.Contains("Corruption", StringComparison.Ordinal))
        {
            throw new JournalIndexCorruptException($"RocksDB reported corruption: {exception.Message}", exception);
        }
        catch (RocksDbException exception) when (IsHeldLock(exception.Message))
        {
            throw new JournalIndexLockHeldException(exception.Message, exception);
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            FatalErrorHandler.StopOnStorageFailure(operation, Path, exception);
            throw;
        }
    }

    private static bool IsFailure(Exception exception) => exception switch
    {
        JournalIndexCorruptException or JournalIndexLockHeldException or OperationCanceledException or ObjectDisposedException or ArgumentException or InvalidOperationException => false,
        RocksDbException rocks => !IsHeldLock(rocks.Message),
        _ => true,
    };

    private static FlushOptions CreateAtomicFlushOptions()
    {
        var options = new FlushOptions().SetWaitForFlush(true);
        Native.Instance.rocksdb_flushoptions_set_force_atomic_flush(options.Handle, 1);
        return options;
    }

    private static ColumnFamilyOptions CreatePrefixColumnFamilyOptions(ulong prefixLength, out SliceTransform prefixExtractor, out BloomFilterPolicy bloomFilter, out BlockBasedTableOptions tableOptions)
    {
        prefixExtractor = SliceTransform.CreateFixedPrefix(prefixLength);
        bloomFilter = BloomFilterPolicy.Create(BloomBitsPerKey, use_block_based_builder: false);
        tableOptions = new BlockBasedTableOptions()
            .SetFilterPolicy(bloomFilter)
            .SetWholeKeyFiltering(false)
            .SetCacheIndexAndFilterBlocks(true)
            .SetPinL0FilterAndIndexBlocksInCache(true);
        return new ColumnFamilyOptions()
            .SetPrefixExtractor(prefixExtractor)
            .SetMemtablePrefixBloomSizeRatio(MemtablePrefixBloomRatio)
            .SetBlockBasedTableFactory(tableOptions);
    }

    private void OpenDatabase()
    {
        _ = Directory.CreateDirectory(Path);
        var dbOptions = CreateDbOptions(_maxWriteBufferBytes);
        var defaultCfOptions = new ColumnFamilyOptions();
        var keyIndexCfOptions = CreatePrefixColumnFamilyOptions(KeyIndexPrefixLength, out var keyPrefix, out var keyBloom, out var keyTable);
        var tombstonesCfOptions = CreatePrefixColumnFamilyOptions(TombstonePrefixLength, out var tombPrefix, out var tombBloom, out var tombTable);

        var families = new ColumnFamilies
        {
            { DefaultColumnFamily, defaultCfOptions },
            { KeyIndexColumnFamily, keyIndexCfOptions },
            { TombstonesColumnFamily, tombstonesCfOptions },
        };

        _open.EnterWriteLock();
        try
        {
            CloseCore();
            _database = RocksDbSharp.RocksDb.Open(dbOptions, Path, families);
            var defaultCf = _database.GetDefaultColumnFamily();
            var keyIndexCf = _database.GetColumnFamily(KeyIndexColumnFamily);
            var tombstonesCf = _database.GetColumnFamily(TombstonesColumnFamily);
            _state = new OpenState(
                _database,
                defaultCf,
                keyIndexCf,
                tombstonesCf,
                [dbOptions, defaultCfOptions, keyIndexCfOptions, tombstonesCfOptions, keyPrefix, keyBloom, keyTable, tombPrefix, tombBloom, tombTable, families]);
        }
        catch
        {
            CloseCore();
            throw;
        }
        finally
        {
            _open.ExitWriteLock();
        }
    }

    private SortedDictionary<LedgerId, Slot> ReadWatermarks()
    {
        var state = RequireOpen();
        var watermarks = new SortedDictionary<LedgerId, Slot>();
        using var iterator = state.Db.NewIterator(state.Default, CheckReadOptions);
        for (_ = iterator.SeekToFirst(); iterator.Valid(); _ = iterator.Next())
        {
            var key = iterator.GetKeySpan();
            var value = iterator.GetValueSpan();
            if (key.Length != WatermarkKeyLength || value.Length != WatermarkValueLength)
            {
                throw new JournalIndexCorruptException(
                    $"The watermark row {Convert.ToHexString(key)} = {Convert.ToHexString(value)} is malformed.");
            }

            var rawSlot = BinaryPrimitives.ReadInt64LittleEndian(value);
            if (rawSlot < 0)
            {
                throw new JournalIndexCorruptException(
                    $"The watermark row {Convert.ToHexString(key)} holds negative slot {rawSlot}.");
            }

            watermarks.Add(new LedgerId(BinaryPrimitives.ReadUInt64BigEndian(key)), new Slot(rawSlot));
        }

        Native.Instance.rocksdb_iter_get_error(iterator.Handle);
        return watermarks;
    }

    private void ApplyBatchCore(
        LedgerId ledger,
        Slot nextWatermark,
        ReadOnlySpan<JournalIndexKeyWrite> keyWrites,
        ReadOnlySpan<JournalIndexTombstoneWrite> tombstoneWrites)
    {
        var state = RequireOpen();
        using var writeBatch = new WriteBatch();

        Span<byte> keyBuf = stackalloc byte[KeyIndexKeyLength];
        Span<byte> valBuf = stackalloc byte[KeyIndexValueLength];
        BinaryPrimitives.WriteUInt64BigEndian(keyBuf[..8], ledger.Value);

        for (var index = 0; index < keyWrites.Length; index++)
        {
            var entry = keyWrites[index];
            entry.KeyHash.CopyTo(keyBuf.Slice(8, JournalKeyHash.Length));
            BinaryPrimitives.WriteInt64BigEndian(keyBuf.Slice(40, 8), entry.Slot.Value);
            BinaryPrimitives.WriteInt32BigEndian(keyBuf.Slice(48, 4), entry.Index);
            BinaryPrimitives.WriteInt64LittleEndian(valBuf[..8], entry.DefiningSlot.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(valBuf.Slice(8, 8), entry.Ordinal);
            _ = writeBatch.Put(keyBuf, valBuf, state.KeyIndex);
        }

        if (!tombstoneWrites.IsEmpty)
        {
            Span<byte> tombKey = stackalloc byte[TombstoneKeyLength];
            BinaryPrimitives.WriteUInt64BigEndian(tombKey[..8], ledger.Value);
            Span<byte> stackVal = stackalloc byte[256];
            byte[]? rentedVal = null;
            try
            {
                for (var index = 0; index < tombstoneWrites.Length; index++)
                {
                    var op = tombstoneWrites[index];
                    op.KeyHash.CopyTo(tombKey.Slice(8, JournalKeyHash.Length));
                    if (op.IsDelete)
                    {
                        _ = writeBatch.Delete(tombKey, state.Tombstones);
                    }
                    else
                    {
                        var rawKey = op.RawKey.Span;
                        var totalLength = TombstoneValueHeaderLength + rawKey.Length;
                        if (totalLength <= stackVal.Length)
                        {
                            var target = stackVal[..totalLength];
                            WriteTombstoneValue(target, op.DeletionTicks, op.DeleteSlot, op.DeleteIndex, rawKey);
                            _ = writeBatch.Put(tombKey, target, state.Tombstones);
                        }
                        else
                        {
                            if (rentedVal is null || rentedVal.Length < totalLength)
                            {
                                if (rentedVal is not null)
                                {
                                    ArrayPool<byte>.Shared.Return(rentedVal);
                                }

                                rentedVal = ArrayPool<byte>.Shared.Rent(totalLength);
                            }

                            var target = rentedVal.AsSpan(0, totalLength);
                            WriteTombstoneValue(target, op.DeletionTicks, op.DeleteSlot, op.DeleteIndex, rawKey);
                            _ = writeBatch.Put(tombKey, target, state.Tombstones);
                        }
                    }
                }
            }
            finally
            {
                if (rentedVal is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedVal);
                }
            }
        }

        Span<byte> wmKey = stackalloc byte[WatermarkKeyLength];
        Span<byte> wmVal = stackalloc byte[WatermarkValueLength];
        BinaryPrimitives.WriteUInt64BigEndian(wmKey, ledger.Value);
        BinaryPrimitives.WriteInt64LittleEndian(wmVal, nextWatermark.Value);
        _ = writeBatch.Put(wmKey, wmVal, state.Default);

        state.Db.Write(writeBatch, WalDisabledWriteOptions);
    }

    private void DropLedgersCore(IReadOnlyList<LedgerId> ledgers)
    {
        var state = RequireOpen();
        using var writeBatch = new WriteBatch();
        for (var index = 0; index < ledgers.Count; index++)
        {
            var ledger = ledgers[index];
            var start = new byte[WatermarkKeyLength];
            BinaryPrimitives.WriteUInt64BigEndian(start, ledger.Value);

            byte[] endKeyIndex;
            byte[] endTombstones;
            if (ledger.Value < ulong.MaxValue)
            {
                endKeyIndex = new byte[WatermarkKeyLength];
                BinaryPrimitives.WriteUInt64BigEndian(endKeyIndex, ledger.Value + 1);
                endTombstones = endKeyIndex;
            }
            else
            {
                // Strictly greater than every 52B key_index key and every 40B tombstones key for ulong.MaxValue.
                endKeyIndex = new byte[KeyIndexKeyLength + 1];
                endKeyIndex.AsSpan().Fill(0xFF);
                endTombstones = new byte[TombstoneKeyLength + 1];
                endTombstones.AsSpan().Fill(0xFF);
            }

            _ = writeBatch.DeleteRange(start, (ulong)start.Length, endKeyIndex, (ulong)endKeyIndex.Length, state.KeyIndex);
            _ = writeBatch.DeleteRange(start, (ulong)start.Length, endTombstones, (ulong)endTombstones.Length, state.Tombstones);
            _ = writeBatch.Delete(start, state.Default);
            _pendingFileDeletes.Add(new PendingFileDeleteRange(start, endKeyIndex, endTombstones));
        }

        state.Db.Write(writeBatch, WalDisabledWriteOptions);
    }

    private void FlushCore()
    {
        var state = RequireOpen();
        Native.Instance.rocksdb_flush_cfs(state.Db.Handle, AtomicFlushOptions.Handle, state.AllCfHandles, state.AllCfHandles.Length);

        // Once the atomic flush has durably persisted the DeleteRange tombstones and watermark removals, drop any
        // whole SST files completely contained in dropped ledger ranges without waiting for compaction.
        if (_pendingFileDeletes.Count > 0)
        {
            for (var index = 0; index < _pendingFileDeletes.Count; index++)
            {
                var range = _pendingFileDeletes[index];
                Native.Instance.rocksdb_delete_file_in_range_cf(
                    state.Db.Handle,
                    state.KeyIndex.Handle,
                    range.Start,
                    (UIntPtr)range.Start.Length,
                    range.EndKeyIndex,
                    (UIntPtr)range.EndKeyIndex.Length);
                Native.Instance.rocksdb_delete_file_in_range_cf(
                    state.Db.Handle,
                    state.Tombstones.Handle,
                    range.Start,
                    (UIntPtr)range.Start.Length,
                    range.EndTombstones,
                    (UIntPtr)range.EndTombstones.Length);
            }

            _pendingFileDeletes.Clear();
        }
    }

    private static List<JournalIndexKeyEntry> GetKeyEntriesCore(
        OpenState state,
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot)
    {
        var results = new List<JournalIndexKeyEntry>();
        if (startSlot >= exclusiveEndSlot)
        {
            return results;
        }

        Span<byte> seekKey = stackalloc byte[KeyIndexKeyLength];
        EncodeKeyIndexKey(seekKey, ledger, keyHash, startSlot, startIndex);

        using var iterator = state.Db.NewIterator(state.KeyIndex, PrefixReadOptions);
        for (_ = iterator.Seek(seekKey); iterator.Valid(); _ = iterator.Next())
        {
            var key = iterator.GetKeySpan();
            if (key.Length != KeyIndexKeyLength)
            {
                throw new JournalIndexCorruptException($"A key_index entry has invalid key length {key.Length}.");
            }

            if (BinaryPrimitives.ReadUInt64BigEndian(key[..8]) != ledger.Value
                || !keyHash.Matches(key.Slice(8, JournalKeyHash.Length)))
            {
                break;
            }

            var rawSlot = BinaryPrimitives.ReadInt64BigEndian(key.Slice(40, 8));
            var rowIndex = BinaryPrimitives.ReadInt32BigEndian(key.Slice(48, 4));
            if (rawSlot < 0 || rowIndex < 0)
            {
                throw new JournalIndexCorruptException($"A key_index entry has negative tag ({rawSlot}, {rowIndex}).");
            }

            var slot = new Slot(rawSlot);
            if (slot >= exclusiveEndSlot)
            {
                break;
            }

            var value = iterator.GetValueSpan();
            if (value.Length != KeyIndexValueLength)
            {
                throw new JournalIndexCorruptException($"A key_index entry has invalid value length {value.Length}.");
            }

            var rawDefiningSlot = BinaryPrimitives.ReadInt64LittleEndian(value[..8]);
            if (rawDefiningSlot < 0)
            {
                throw new JournalIndexCorruptException($"A key_index entry has negative defining slot {rawDefiningSlot}.");
            }

            var ordinal = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(8, 8));
            results.Add(new JournalIndexKeyEntry(slot, rowIndex, new Slot(rawDefiningSlot), ordinal));
        }

        Native.Instance.rocksdb_iter_get_error(iterator.Handle);
        return results;
    }

    private static bool HasKeyEntryCore(
        OpenState state,
        LedgerId ledger,
        JournalKeyHash keyHash,
        Slot startSlot,
        int startIndex,
        Slot exclusiveEndSlot)
    {
        if (startSlot >= exclusiveEndSlot)
        {
            return false;
        }

        Span<byte> seekKey = stackalloc byte[KeyIndexKeyLength];
        EncodeKeyIndexKey(seekKey, ledger, keyHash, startSlot, startIndex);

        using var iterator = state.Db.NewIterator(state.KeyIndex, PrefixReadOptions);
        _ = iterator.Seek(seekKey);
        if (!iterator.Valid())
        {
            Native.Instance.rocksdb_iter_get_error(iterator.Handle);
            return false;
        }

        var key = iterator.GetKeySpan();
        if (key.Length != KeyIndexKeyLength)
        {
            throw new JournalIndexCorruptException($"A key_index entry has invalid key length {key.Length}.");
        }

        if (BinaryPrimitives.ReadUInt64BigEndian(key[..8]) != ledger.Value
            || !keyHash.Matches(key.Slice(8, JournalKeyHash.Length)))
        {
            Native.Instance.rocksdb_iter_get_error(iterator.Handle);
            return false;
        }

        var rawSlot = BinaryPrimitives.ReadInt64BigEndian(key.Slice(40, 8));
        if (rawSlot < 0)
        {
            throw new JournalIndexCorruptException($"A key_index entry has negative slot {rawSlot}.");
        }

        Native.Instance.rocksdb_iter_get_error(iterator.Handle);
        return new Slot(rawSlot) < exclusiveEndSlot;
    }

    private static List<JournalIndexTombstone> ScanTombstonesCore(OpenState state, LedgerId ledger)
    {
        var results = new List<JournalIndexTombstone>();
        Span<byte> seekPrefix = stackalloc byte[TombstonePrefixLength];
        BinaryPrimitives.WriteUInt64BigEndian(seekPrefix, ledger.Value);

        using var iterator = state.Db.NewIterator(state.Tombstones, PrefixReadOptions);
        for (_ = iterator.Seek(seekPrefix); iterator.Valid(); _ = iterator.Next())
        {
            var key = iterator.GetKeySpan();
            if (key.Length != TombstoneKeyLength)
            {
                throw new JournalIndexCorruptException($"A tombstones entry has invalid key length {key.Length}.");
            }

            if (BinaryPrimitives.ReadUInt64BigEndian(key[..8]) != ledger.Value)
            {
                break;
            }

            var value = iterator.GetValueSpan();
            if (value.Length <= TombstoneValueHeaderLength)
            {
                throw new JournalIndexCorruptException($"A tombstones entry has invalid value length {value.Length}.");
            }

            var deletionTicks = BinaryPrimitives.ReadInt64LittleEndian(value[..8]);
            var rawSlot = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(8, 8));
            var deleteIndex = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(16, 4));
            if (rawSlot < 0 || deleteIndex < 0)
            {
                throw new JournalIndexCorruptException($"A tombstones entry has negative tag ({rawSlot}, {deleteIndex}).");
            }

            var keyHash = JournalKeyHash.FromSpan(key.Slice(8, JournalKeyHash.Length));
            var rawKey = value[TombstoneValueHeaderLength..].ToArray();
            results.Add(new JournalIndexTombstone(keyHash, deletionTicks, new Slot(rawSlot), deleteIndex, rawKey));
        }

        Native.Instance.rocksdb_iter_get_error(iterator.Handle);
        return results;
    }

    internal static void EncodeKeyIndexKey(Span<byte> destination, LedgerId ledger, JournalKeyHash keyHash, Slot slot, int index)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], ledger.Value);
        keyHash.CopyTo(destination.Slice(8, JournalKeyHash.Length));
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(40, 8), slot.Value);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(48, 4), index);
    }

    private void CloseCore()
    {
        _pendingFileDeletes.Clear();
        var retained = _state?.RetainedOptions;
        _state = null;
        if (_database is { } db)
        {
            _database = null;
            db.Dispose();
        }

        GC.KeepAlive(retained);
    }

    private static void WriteTombstoneValue(
        Span<byte> target,
        long deletionTicks,
        Slot deleteSlot,
        int deleteIndex,
        ReadOnlySpan<byte> rawKey)
    {
        BinaryPrimitives.WriteInt64LittleEndian(target[..8], deletionTicks);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(8, 8), deleteSlot.Value);
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(16, 4), deleteIndex);
        rawKey.CopyTo(target[TombstoneValueHeaderLength..]);
    }

    private readonly record struct PendingFileDeleteRange(byte[] Start, byte[] EndKeyIndex, byte[] EndTombstones);

    private sealed class OpenState(
        RocksDbSharp.RocksDb db,
        ColumnFamilyHandle defaultCf,
        ColumnFamilyHandle keyIndexCf,
        ColumnFamilyHandle tombstonesCf,
        object[] retainedOptions)
    {
        public RocksDbSharp.RocksDb Db { get; } = db;

        public ColumnFamilyHandle Default { get; } = defaultCf;

        public ColumnFamilyHandle KeyIndex { get; } = keyIndexCf;

        public ColumnFamilyHandle Tombstones { get; } = tombstonesCf;

        public ColumnFamilyHandle[] AllColumnFamilies { get; } = [defaultCf, keyIndexCf, tombstonesCf];

        public IntPtr[] AllCfHandles { get; } = [defaultCf.Handle, keyIndexCf.Handle, tombstonesCf.Handle];

        public object[] RetainedOptions { get; } = retainedOptions;
    }
}
