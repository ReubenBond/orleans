using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// <see cref="ILedgerStorage"/> over an <see cref="IAppendableObjectStore"/> using the Azure append layout
/// (<c>CLOE</c> extents, object-ledgers.md §7–§8).
/// </summary>
internal sealed partial class AppendObjectLedgerStorage : ILedgerStorage
{
    /// <summary>Chunk size of ranged reads issued by <see cref="ReadRangeAsync"/> (object-ledgers.md §8.4).</summary>
    internal const int ReadRangeChunkBytes = 8 * 1024 * 1024;

    /// <summary>Tail-chunk size fetched per GET during backward trailer walks and sealed-extent inspection.</summary>
    internal const int TrailerWalkChunkBytes = 512 * 1024;

    /// <summary>Maximum append-span byte length fetched in a single GET when indexing or inspecting frame headers.</summary>
    internal const int SingleReadSpanThresholdBytes = 1024 * 1024;

    private readonly IObjectStore _objects;
    private readonly IAppendableObjectStore _appendable;
    private readonly LedgerId _ledger;
    private readonly string _extentsPrefix;
    private readonly ObjectName _sealed;
    private readonly ObjectName _dropped;
    private readonly ObjectLedgerStoreOptions _options;
    private readonly ResiliencePipeline<LedgerRecoveryOutcome> _recoveryRetry;
    private readonly ObjectLedgerCache _cache;

    private readonly CancellationTokenSource _reads = new();
    private readonly CancellationTokenSource _writes = new();
    private readonly Lock _disposeLock = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _readsStopped;
    private int _disposing;
    private Task? _readsStoppedTask;
    private Task? _disposalTask;

    // Bound (OL-I7): durable slots below this value are safe from delayed-write recreation.
    private long _durableBelow;

    // Writer coalescing state guarded by _writeLock (never held across awaits).
    private readonly Lock _writeLock = new();
    private readonly SortedDictionary<long, QueuedWrite> _queue = [];
    private readonly Dictionary<long, QueuedWrite> _inFlight = [];
    private readonly Dictionary<long, uint> _writtenCrcs = [];
    private long _prunedWrittenCrcsBelow;
    private long _nextSlot;
    private long _extentFirstSlot;
    private long _extentOffset;
    private int _extentAppends;
    private bool _extentCreated;
    private bool _offsetDirty;
    private bool _isSealed;
    private bool _cacheSealed;
    private bool _flushing;
    private Task? _flushTask;
    private Exception? _permanentFailure;

    // Cached extent slot index (object-ledgers.md §8.4).
    private readonly AppendExtentIndex _index = new();

    public AppendObjectLedgerStorage(
        IObjectStore objects,
        IAppendableObjectStore appendable,
        LedgerId ledger,
        int windowSize,
        ObjectLedgerStoreOptions options,
        ResiliencePipeline<LedgerRecoveryOutcome> recoveryRetry,
        ObjectLedgerCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(appendable);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recoveryRetry);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);

        _objects = objects;
        _appendable = appendable;
        _ledger = ledger;
        _extentsPrefix = ObjectLedgerNames.Extents(ledger);
        _sealed = ObjectLedgerNames.Sealed(ledger);
        _dropped = ObjectLedgerNames.Dropped(ledger);
        _options = options;
        _recoveryRetry = recoveryRetry;
        _cache = cache ?? new ObjectLedgerCache(capacityBytes: 0, directory: null);
        WindowSize = windowSize;
    }

    /// <inheritdoc />
    public int WindowSize { get; }

    /// <inheritdoc />
    public async ValueTask<LedgerTailRead> CheckAsync(Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var scope = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        var tail = Raise(acknowledged);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_reads.Token, cancellationToken);
        try
        {
            var isSealed = await _objects.GetPropertiesAsync(_sealed, linked.Token).ConfigureAwait(false) is not null;
            return new LedgerTail(tail, isSealed);
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
    }

    /// <inheritdoc />
    public async ValueTask<LedgerRecoveryOutcome> RecoverAsync(Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var scope = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        _ = Raise(acknowledged);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_reads.Token, cancellationToken);
        try
        {
            var outcome = await _recoveryRetry.ExecuteAsync(
                static (storage, token) => storage.RecoverOnceAsync(token),
                this,
                linked.Token).ConfigureAwait(false);
            if (outcome is LedgerRecovery recovery)
            {
                _ = Raise(recovery.End);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot acknowledged,
        CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(slot);
        ArgumentOutOfRangeException.ThrowIfNegative(acknowledged.Value);

        using var scope = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        _ = Raise(acknowledged);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_reads.Token, cancellationToken);
        return await ReadPointAsync(slot, range: null, linked.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot acknowledged,
        CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(slot);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegative(acknowledged.Value);

        using var scope = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        _ = Raise(acknowledged);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_reads.Token, cancellationToken);
        return await ReadPointAsync(slot, (offset, length), linked.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ReplicaRead> ReadRangeAsync(
        Slot from,
        Slot below,
        Slot acknowledged,
        SemaphoreSlim? window = null,
        CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(below.Value, from.Value);
        ArgumentOutOfRangeException.ThrowIfNegative(acknowledged.Value);
        _ = Raise(acknowledged);
        return ReadRangeCoreAsync(from, below, window, cancellationToken);
    }

    /// <inheritdoc />
    public void WriteContiguousTail(Slot acknowledged) => _ = Raise(acknowledged);

    /// <inheritdoc />
    public ValueTask<Slot> LearnTailAsync(Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var scope = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Raise(acknowledged));
    }

    /// <inheritdoc />
    public Task StopReadsAsync()
    {
        lock (_disposeLock)
        {
            _ = Interlocked.Exchange(ref _readsStopped, 1);
            return _readsStoppedTask ??= _reads.CancelAsync();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposalTask ??= CompleteDisposalAsync();
            return new ValueTask(_disposalTask);
        }
    }

    private async Task CompleteDisposalAsync()
    {
        _ = Interlocked.Exchange(ref _disposing, 1);
        Task readsCancel;
        lock (_disposeLock)
        {
            _ = Interlocked.Exchange(ref _readsStopped, 1);
            readsCancel = _readsStoppedTask ??= _reads.CancelAsync();
        }

        await readsCancel.ConfigureAwait(false);
        await _writes.CancelAsync().ConfigureAwait(false);
        CancelAllQueuedForDisposal(activeBatch: null);

        Task? flushTask;
        lock (_writeLock)
        {
            flushTask = _flushTask;
        }

        if (flushTask is not null)
        {
            try
            {
                await flushTask.ConfigureAwait(false);
            }
            catch
            {
                // Flush loop reports failures through queued completions; disposal only drains it.
            }
        }

        if (Volatile.Read(ref _active) > 0)
        {
            await _drained.Task.ConfigureAwait(false);
        }

        SealAndEvictCache();
        _reads.Dispose();
        _writes.Dispose();
    }

    private async ValueTask<bool> IsDroppedAsync(CancellationToken cancellationToken)
    {
        if (await _objects.GetPropertiesAsync(_dropped, cancellationToken).ConfigureAwait(false) is not null)
        {
            MarkSealed();
            SealAndEvictCache();
            return true;
        }

        return false;
    }

    private void CacheDecidedBatch(Slot slot, ArcBuffer payload)
    {
        if (!_cache.IsEnabled || payload.Length == 0 || payload.Length > _cache.CapacityBytes)
        {
            return;
        }

        var bytes = ObjectLedgerCache.ToArray(payload);
        var crc32c = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes);
        lock (_writeLock)
        {
            if (_cacheSealed)
            {
                return;
            }

            _cache.PutBytes(_ledger, slot, bytes, crc32c);
        }
    }

    private void SealAndEvictCache()
    {
        if (!_cache.IsEnabled)
        {
            return;
        }

        lock (_writeLock)
        {
            _cacheSealed = true;
        }

        _cache.EvictLedger(_ledger);
    }

    private async ValueTask<bool> IsDroppedSafeAsync(CancellationToken linkedToken, CancellationToken callerToken)
    {
        try
        {
            return await IsDroppedAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ReadsStopped(callerToken))
        {
            throw ReadsStoppedException();
        }
    }

    private async ValueTask<ArcBuffer?> ReadExactOrDroppedAsync(
        ObjectName name,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        ArcBuffer? read;
        try
        {
            read = await _objects.ReadAsync(name, offset, length, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            throw Corrupt(name, "returned fewer bytes than its listed length");
        }

        if (read is null)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            throw Corrupt(name, "disappeared without a dropped marker");
        }

        var buffer = read.Value;
        if (buffer.Length != length)
        {
            buffer.Dispose();
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            throw Corrupt(name, "returned fewer bytes than requested");
        }

        return buffer;
    }

    private static ObjectLedgerTrailerDecoding DecodeTrailerSlice(ReadOnlySequence<byte> slice)
    {
        Span<byte> bytes = stackalloc byte[ObjectLedgerExtentFormat.TrailerLength];
        slice.CopyTo(bytes);
        return ObjectLedgerExtentFormat.DecodeTrailer(bytes);
    }

    private static ObjectLedgerFrameHeaderDecoding DecodeFrameHeaderBuffer(ArcBuffer buffer)
    {
        Span<byte> bytes = stackalloc byte[ObjectLedgerExtentFormat.FrameHeaderLength];
        buffer.AsReadOnlySequence().Slice(0, ObjectLedgerExtentFormat.FrameHeaderLength).CopyTo(bytes);
        return ObjectLedgerExtentFormat.DecodeFrameHeader(bytes);
    }

    private static ObjectLedgerFrameHeaderDecoding DecodeFrameHeaderSlice(ReadOnlySequence<byte> slice)
    {
        Span<byte> bytes = stackalloc byte[ObjectLedgerExtentFormat.FrameHeaderLength];
        slice.CopyTo(bytes);
        return ObjectLedgerExtentFormat.DecodeFrameHeader(bytes);
    }

    private LedgerStorageCorruptException Corrupt(Slot slot, string detail) =>
        new($"Ledger {_ledger} slot {slot.Value} {detail}.");

    private LedgerStorageCorruptException Corrupt(ObjectName extentName, string detail) =>
        new($"Ledger {_ledger} extent '{extentName}' {detail}.");

    private LedgerStorageCorruptException Corrupt(ObjectName extentName, string detail, Exception inner) =>
        new($"Ledger {_ledger} extent '{extentName}' {detail}: {inner.Message}", inner);

    private Slot Raise(Slot candidate)
    {
        var value = candidate.Value;
        while (true)
        {
            var current = Volatile.Read(ref _durableBelow);
            if (value <= current)
            {
                return new Slot(current);
            }

            if (Interlocked.CompareExchange(ref _durableBelow, value, current) == current)
            {
                return candidate;
            }
        }
    }

    private CallScope Admit()
    {
        _ = Interlocked.Increment(ref _active);
        if (Volatile.Read(ref _disposing) != 0)
        {
            ReleaseActive();
            throw new ObjectDisposedException(nameof(AppendObjectLedgerStorage));
        }

        return new CallScope(this);
    }

    private CallScope AdmitRead()
    {
        var scope = Admit();
        if (Volatile.Read(ref _readsStopped) != 0)
        {
            scope.Dispose();
            throw ReadsStoppedException();
        }

        return scope;
    }

    private CallScope AcquireDetachedScope()
    {
        // Called only while the caller holds an active CallScope so DisposeAsync waits for detached walks too.
        Debug.Assert(Volatile.Read(ref _active) > 0);
        _ = Interlocked.Increment(ref _active);
        return new CallScope(this);
    }

    private void ReleaseActive()
    {
        if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _disposing) != 0)
        {
            _ = _drained.TrySetResult();
        }
    }

    private bool ReadsStopped(CancellationToken callerToken) =>
        _reads.IsCancellationRequested && !callerToken.IsCancellationRequested;

    private ObjectDisposedException ReadsStoppedException() =>
        Volatile.Read(ref _disposing) != 0
            ? new ObjectDisposedException(nameof(AppendObjectLedgerStorage), "The storage was disposed while reading.")
            : new ObjectDisposedException(nameof(AppendObjectLedgerStorage), "Reads on this storage have been stopped.");

    private readonly struct CallScope(AppendObjectLedgerStorage owner) : IDisposable
    {
        public void Dispose() => owner.ReleaseActive();
    }
}
