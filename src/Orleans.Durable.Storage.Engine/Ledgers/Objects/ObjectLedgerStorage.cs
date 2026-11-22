using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Threading;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// The storage of one object-store ledger in the per-slot layout (object-ledgers.md §2.1, §8): each slot one object, a
/// batch or an empty SEAL, created once and settled by its properties (OL-I2); a recovery fences the ledger with SEALs
/// over a window after its last batch (OL-I3). It keeps one copy, so its bound, the greatest <c>acknowledged</c> any
/// member carried, decides its reads (OL-I7), and nothing publishes a tail to other processes.
/// </summary>
/// <remarks>
/// <para>
/// A drop answers from the ledger's <c>dropped</c> marker, which the sweeper creates before it deletes a batch (OL-I4):
/// a read which meets an absent object or a SEAL, at any slot, answers <c>Dropped</c> once the marker exists; a read at
/// or above the bound reads the marker before it answers a batch, since a delayed write may re-create one there after a
/// sweep; and a recovery reads it before it creates anything and before it answers. The sweeper keeps every SEAL, so a
/// stale write at <c>T</c> still meets its fence, and a SEAL a delayed recovery creates after a sweep is read only as the
/// drop (object-store.md §2.4, object-ledgers.md §1).
/// </para>
/// <para>
/// Reads, checks, and recoveries stop at <see cref="StopReadsAsync"/>, and writes at disposal, each then failing with
/// <see cref="ObjectDisposedException"/> (ledger-protocol.md §6.3). Each operation awaits every object-store call it
/// makes, so disposal waits for no call it left running.
/// </para>
/// </remarks>
internal sealed class ObjectLedgerStorage : ILedgerStorage
{
    private readonly IObjectStore _store;
    private readonly LedgerId _id;
    private readonly string _slots;
    private readonly ObjectName _sealed;
    private readonly ObjectName _dropped;
    private readonly int _maxBatchBytes;
    private readonly ResiliencePipeline<LedgerRecoveryOutcome> _recoveryRetry;
    private readonly ObjectLedgerCache _cache;
    private readonly AdmissionGate _operations = new();
    private readonly CancellationTokenSource _reads = new();
    private readonly CancellationTokenSource _writes = new();
    private readonly Lock _disposeLock = new();
    private readonly Lock _pendingLock = new();
    private Dictionary<long, PendingWrite>? _pendingWrites;
    private long _decidedCacheTail;
    private bool _cacheSealed;
    private long _bound;
    private Task? _writesStopped;
    private Task? _readsStopped;
    private Task? _disposal;

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerStorage"/> class, without I/O.</summary>
    /// <param name="store">The object store, under the cluster's store prefix.</param>
    /// <param name="id">The ledger.</param>
    /// <param name="windowSize">The ledger's window <c>K</c>.</param>
    /// <param name="maxBatchBytes">The largest batch payload admitted by the store options.</param>
    /// <param name="recoveryRetry">The retry pipeline of the store's recoveries, which repeats a transient failure.</param>
    /// <param name="cache">The process's derived local cache of verified batches (OL-I11).</param>
    public ObjectLedgerStorage(
        IObjectStore store,
        LedgerId id,
        int windowSize,
        int maxBatchBytes,
        ResiliencePipeline<LedgerRecoveryOutcome> recoveryRetry,
        ObjectLedgerCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recoveryRetry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchBytes);
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        _store = store;
        _id = id;
        _slots = ObjectLedgerNames.Slots(id);
        _sealed = ObjectLedgerNames.Sealed(id);
        _dropped = ObjectLedgerNames.Dropped(id);
        _maxBatchBytes = maxBatchBytes;
        _recoveryRetry = recoveryRetry;
        _cache = cache ?? new ObjectLedgerCache(capacityBytes: 0, directory: null);
        WindowSize = windowSize;
    }

    /// <inheritdoc/>
    public int WindowSize { get; }

    /// <summary>
    /// Writes the slot's object if absent, with the batch and its CRC-32C, and settles a create which met an occupant by its
    /// properties (object-ledgers.md §8.1, OL-I2): the writer's own batch answers <c>Written</c>, a SEAL <c>Sealed</c>, and
    /// absence repeats the create. A failure, after the object store's own retries, leaves the outcome unknown; the writer
    /// repeats the write (LP-I12).
    /// </summary>
    /// <exception cref="LedgerStorageCorruptException">The slot holds bytes the writer did not write.</exception>
    /// <exception cref="ObjectContentChangedException">
    /// The batch changed after its CRC-32C was computed: a defect, reported to the fatal-error handler, which stops the
    /// process; the exception reaches the caller only when a test intercepts the report.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The storage was disposed, before the write or during it.</exception>
    public async ValueTask<LedgerWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var admission = Admit();
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        if (payload.Length > _maxBatchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                string.Create(CultureInfo.InvariantCulture, $"The batch holds {payload.Length} bytes, past the store's limit of {_maxBatchBytes}."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = Raise(acknowledged);
        var name = ObjectLedgerNames.Slot(_id, slot);
        var crc32c = ObjectStoreChecksums.Crc32C(payload);
        using var scope = new CallScope(cancellationToken, _writes.Token);
        try
        {
            while (true)
            {
                switch (await CreateBatchAsync(name, payload, crc32c, scope.Token).ConfigureAwait(false))
                {
                    case ObjectCreateOutcome.Created:
                        RecordWrittenForCache(slot, payload, crc32c);
                        return ReplicaWriteOutcome.Written.Instance;
                    case ObjectCreateOutcome.Exists:
                        break;
                    case null:
                    default:
                        throw new UnreachableException();
                }

                switch (ObjectLedgerSlots.Settle(await _store.GetPropertiesAsync(name, scope.Token).ConfigureAwait(false), payload.Length, crc32c))
                {
                    case ObjectSlotSettlement.Written:
                        RecordWrittenForCache(slot, payload, crc32c);
                        return ReplicaWriteOutcome.Written.Instance;
                    case ObjectSlotSettlement.Sealed:
                        // LL-I4: the slot's SEAL is a completed or running recovery's fence, which no batch ever replaces.
                        SealPendingWrites();
                        return ReplicaWriteOutcome.Sealed.Instance;
                    case ObjectSlotSettlement.Corrupt:
                        throw Corrupt(slot, "holds bytes its writer did not write");
                    case ObjectSlotSettlement.Repeat:
                        // Absent: the create may still land, so the write repeats it, byte-identical (OL-I2).
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        catch (OperationCanceledException) when (_writes.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ObjectLedgerStorage), "The storage's disposal ended the write.");
        }
    }

    /// <summary>
    /// Fixes the tail at the bound, then reads the <c>sealed</c> marker (OL-I5): sealed once a recovery created it. A drop
    /// follows the ledger's close, so a dropped ledger's marker exists (LL-I9). Never <c>Backpressure</c>.
    /// </summary>
    public async ValueTask<LedgerTailRead> CheckAsync(Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var admission = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        var tail = Raise(acknowledged);
        using var scope = new CallScope(cancellationToken, _reads.Token);
        try
        {
            var isSealed = await _store.GetPropertiesAsync(_sealed, scope.Token).ConfigureAwait(false) is not null;
            return new LedgerTail(tail, isSealed);
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
    }

    /// <summary>
    /// Seals the ledger and recovers its end (object-ledgers.md §8.2): reads <c>dropped</c>, creates <c>sealed</c>, places
    /// the fence, and re-reads <c>dropped</c> (OL-I4, OL-I6). A transient failure repeats the recovery from its start.
    /// </summary>
    /// <exception cref="LedgerStorageCorruptException">The ledger's listing names an object no slot names.</exception>
    /// <exception cref="ObjectDisposedException">The storage's reads stopped, before the recovery or during it.</exception>
    public async ValueTask<LedgerRecoveryOutcome> RecoverAsync(Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var admission = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        SealPendingWrites();
        var evidence = Raise(acknowledged);
        using var scope = new CallScope(cancellationToken, _reads.Token);
        try
        {
            var outcome = await _recoveryRetry.ExecuteAsync(
                static (state, token) => state.Storage.RecoverOnceAsync(state.Evidence, token),
                (Storage: this, Evidence: evidence),
                scope.Token).ConfigureAwait(false);
            if (outcome.Value is LedgerRecovery recovery)
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

    /// <summary>
    /// Reads the slot's object (object-ledgers.md §8.4): its properties, then its bytes, which its CRC-32C checks.
    /// </summary>
    /// <exception cref="LedgerStorageCorruptException">
    /// The batch fails its CRC-32C or is longer than any batch, or the slot holds none below the bound while the ledger is
    /// not dropped (OL-I7).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The storage's reads stopped, before the read or during it.</exception>
    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot acknowledged, CancellationToken cancellationToken = default) =>
        ReadAsync(slot, range: null, acknowledged, cancellationToken);

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of the slot's batch (object-ledgers.md §8.4) with one ranged read, which
    /// no CRC-32C covers; when the range is absent, the object's properties tell a batch the range passes, which answers
    /// <see cref="ReplicaRead.OutsideBatch"/>, from an absent slot or a SEAL.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="length"/> not positive.</exception>
    /// <exception cref="LedgerStorageCorruptException">The slot holds no batch below the bound while the ledger is not dropped (OL-I7).</exception>
    /// <exception cref="ObjectDisposedException">The storage's reads stopped, before the read or during it.</exception>
    public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot acknowledged, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return ReadAsync(slot, (offset, length), acknowledged, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ReplicaRead> ReadRangeAsync(
        Slot from,
        Slot below,
        Slot acknowledged,
        SemaphoreSlim? window = null,
        CancellationToken cancellationToken = default) =>
        LedgerRangeReads.ReadPointWiseAsync(this, from, below, acknowledged, LedgerReader.DefaultMaxReadAheadBytes, window, onReadIssued: null, cancellationToken);

    /// <summary>
    /// Advances this storage's local bound to <paramref name="acknowledged"/> without issuing an object-store request: a
    /// process reads only below its own bound, and no other process learns a tail from the store (object-ledgers.md §2.1).
    /// </summary>
    public void WriteContiguousTail(Slot acknowledged) => _ = Raise(acknowledged);

    /// <summary>
    /// Learns a contiguous tail with no request: the greater of <paramref name="acknowledged"/> and the bound the storage
    /// holds, every slot below which is decided (ledger-protocol.md §8.2). The store keeps no tail of its own to read, so
    /// no other process's progress reaches it (object-ledgers.md §2.1).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The storage's reads stopped, or it was disposed.</exception>
    public ValueTask<Slot> LearnTailAsync(Slot acknowledged, CancellationToken cancellationToken = default)
    {
        using var admission = AdmitRead();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Raise(acknowledged));
    }

    /// <inheritdoc/>
    public Task StopReadsAsync()
    {
        lock (_disposeLock)
        {
            // One cancellation serves the stop and the disposal, which disposes the source only once its callbacks ran.
            return _readsStopped ??= _reads.CancelAsync();
        }
    }

    /// <summary>
    /// Closes admission, stops the reads, checks, and recoveries, ends the writes' object-store calls, and waits for the
    /// admitted operations (ledger-protocol.md §6.3). A write it ends may still land, so its outcome is unknown.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            // CancelAsync runs each cancellation's callbacks, which end the operations' calls, off the caller's lock; the
            // sources are disposed only once both cancellations and the admitted operations completed.
            _disposal ??= CompleteDisposalAsync(_operations.CloseAsync(), _writesStopped ??= _writes.CancelAsync(), _readsStopped ??= _reads.CancelAsync());
            return new ValueTask(_disposal);
        }
    }

    private async Task CompleteDisposalAsync(Task drained, Task writesStopped, Task readsStopped)
    {
        await writesStopped.ConfigureAwait(false);
        await readsStopped.ConfigureAwait(false);
        await drained.ConfigureAwait(false);
        SealAndEvictCache();
        _reads.Dispose();
        _writes.Dispose();
    }

    /// <summary>
    /// Creates a slot's batch, reporting content which changed after its CRC-32C was computed as the defect it is, so no
    /// repetition writes the changed bytes under a checksum computed afresh (conventions.md §14).
    /// </summary>
    private async ValueTask<ObjectCreateOutcome> CreateBatchAsync(ObjectName name, ArcBuffer payload, uint crc32c, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.CreateAsync(name, payload, crc32c, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectContentChangedException exception)
        {
            ObjectContentChangedDefect.Stop(exception);
            throw;
        }
    }

    private async ValueTask<LedgerRecoveryOutcome> RecoverOnceAsync(Slot evidence, CancellationToken cancellationToken)
    {
        // OL-I6: a dropped ledger's recovery creates nothing.
        if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        // OL-I4: the marker precedes every SEAL, so a check which finds it unsealed began before the fence.
        _ = await _store.CreateAsync(_sealed, default, crc32c: null, cancellationToken).ConfigureAwait(false);
        var end = await FenceAsync(evidence, cancellationToken).ConfigureAwait(false);

        // OL-I6, LL-I9: a sweep which began meanwhile may have deleted batches below the end, so the drop decides.
        return await IsDroppedAsync(cancellationToken).ConfigureAwait(false)
            ? ReplicaWriteOutcome.Dropped.Instance
            : new LedgerRecovery(end, end);
    }

    /// <summary>
    /// Places the fence (object-ledgers.md §8.2, OL-I3) and answers the end <c>T</c>: the first slot, at or above the
    /// listing's window, without a batch. A SEAL the listing shows there ends the ledger, since no batch ever replaces
    /// one; otherwise the recovery creates a SEAL at every slot of <c>[s, s + K)</c> the listing did not show, settles
    /// each, and ends the ledger at the first without a batch. When the writer filled the whole window first, the
    /// recovery lists again above it. Either way it then fences <c>(T, T + K)</c> too, so every write the writer can
    /// still issue, below its completed prefix, at most <c>T</c>, plus <c>K</c> (LP-I2), meets a SEAL or a batch which
    /// landed before the fence.
    /// </summary>
    private async ValueTask<Slot> FenceAsync(Slot evidence, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (low, known) = await ListWindowAsync(evidence, cancellationToken).ConfigureAwait(false);
            var start = low;
            long length;
            while (known.TryGetValue(start.Value, out length) && length > 0)
            {
                start += 1;
            }

            if (known.ContainsKey(start.Value))
            {
                await FenceAboveAsync(start, known, cancellationToken).ConfigureAwait(false);
                return start;
            }

            // The window stops at the greatest slot a writer writes, past which no batch lies.
            var window = new Task<bool>[(int)Math.Min(WindowSize, ObjectLedgerNames.MaxSlot - start.Value + 1)];
            for (var index = 0; index < window.Length; index++)
            {
                var slot = start + index;
                window[index] = known.TryGetValue(slot.Value, out length) ? Task.FromResult(length > 0) : PlaceSealAsync(slot, cancellationToken);
            }

            var holdsBatch = await Task.WhenAll(window).ConfigureAwait(false);
            var first = Array.IndexOf(holdsBatch, false);
            if (first < 0 && window.Length == WindowSize)
            {
                // Every slot below the window's end now holds a batch, the evidence of the next listing (OL-I6).
                evidence = start + WindowSize;
                continue;
            }

            for (var index = 0; index < window.Length; index++)
            {
                known[start.Value + index] = holdsBatch[index] ? 1 : 0;
            }

            var end = start + (first >= 0 ? first : window.Length);
            await FenceAboveAsync(end, known, cancellationToken).ConfigureAwait(false);
            return end;
        }
    }

    /// <summary>
    /// Creates a SEAL at every slot of <c>(end, end + K)</c> whose object neither the listing nor the window settled, so a
    /// write the writer pipelined there after the end meets a fence (LL-I4); a slot a batch holds keeps it, which never
    /// completes, since <paramref name="end"/> never does (LP-I3).
    /// </summary>
    private async ValueTask FenceAboveAsync(Slot end, Dictionary<long, long> known, CancellationToken cancellationToken)
    {
        List<Task<bool>>? seals = null;
        for (var index = 1L; index < WindowSize && index <= ObjectLedgerNames.MaxSlot - end.Value; index++)
        {
            if (!known.ContainsKey(end.Value + index))
            {
                (seals ??= []).Add(PlaceSealAsync(end + index, cancellationToken));
            }
        }

        if (seals is not null)
        {
            _ = await Task.WhenAll(seals).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lists the ledger's slots newest first down to the window's low slot, <c>max(evidence, h + 1 − K)</c> for the newest
    /// listed slot <c>h</c>, below which every slot holds a batch (LP-I2, OL-I6); answers that slot and each listed slot at
    /// or above it with its object's length.
    /// </summary>
    private async ValueTask<(Slot Low, Dictionary<long, long> Listed)> ListWindowAsync(Slot evidence, CancellationToken cancellationToken)
    {
        var listed = new Dictionary<long, long>(WindowSize + 1);
        var low = evidence;
        var first = true;
        await foreach (var item in _store.ListAsync(_slots, startAfter: null, cancellationToken).ConfigureAwait(false))
        {
            var slot = ObjectLedgerNames.ParseSlot(_slots, item.Name)
                ?? throw new LedgerStorageCorruptException(string.Create(CultureInfo.InvariantCulture, $"Ledger {_id}'s slots hold an object named {item.Name}, which names no slot."));
            if (first)
            {
                low = new Slot(Math.Max(evidence.Value, slot.Value + 1 - WindowSize));
                first = false;
            }

            if (slot < low)
            {
                break;
            }

            listed[slot.Value] = item.Length;
            if (slot == low)
            {
                break;
            }
        }

        return (low, listed);
    }

    /// <summary>
    /// Creates a SEAL at the slot if absent, settling a create which met an occupant by its properties; answers whether the
    /// slot holds a batch. An absent occupant repeats the create.
    /// </summary>
    private async Task<bool> PlaceSealAsync(Slot slot, CancellationToken cancellationToken)
    {
        var name = ObjectLedgerNames.Slot(_id, slot);
        while (true)
        {
            switch (await _store.CreateAsync(name, default, crc32c: null, cancellationToken).ConfigureAwait(false))
            {
                case ObjectCreateOutcome.Created:
                    return false;
                case ObjectCreateOutcome.Exists:
                    break;
                case null:
                default:
                    throw new UnreachableException();
            }

            if (await _store.GetPropertiesAsync(name, cancellationToken).ConfigureAwait(false) is { } occupant)
            {
                return occupant.Length > 0;
            }
        }
    }

    private async ValueTask<ReplicaRead> ReadAsync(Slot slot, (int Offset, int Length)? range, Slot acknowledged, CancellationToken cancellationToken)
    {
        using var admission = AdmitRead();
        LedgerReplicaOperations.ValidateSlot(slot);
        cancellationToken.ThrowIfCancellationRequested();
        var bound = Raise(acknowledged);
        using var scope = new CallScope(cancellationToken, _reads.Token);
        try
        {
            if (await _cache.TryGetAsync(_id, slot, scope.Token).ConfigureAwait(false) is { } cached)
            {
                if (await IsDroppedAboveAsync(slot, bound, scope.Token).ConfigureAwait(false))
                {
                    _cache.EvictLedger(_id);
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                if (range is { } cachedPart)
                {
                    return cachedPart.Offset + (long)cachedPart.Length > cached.Length
                        ? new ReplicaRead(new ReplicaRead.OutsideBatch(cached.Length))
                        : new ReplicaRead(ArcBuffer.Wrap(cached.AsMemory(cachedPart.Offset, cachedPart.Length)));
                }

                return new ReplicaRead(ArcBuffer.Wrap(cached));
            }

            var name = ObjectLedgerNames.Slot(_id, slot);
            var read = range is { } part
                ? await ReadRangeAsync(name, slot, part.Offset, part.Length, bound, scope.Token).ConfigureAwait(false)
                : await ReadBatchAsync(name, slot, bound, scope.Token).ConfigureAwait(false);
            if (read is { } answer)
            {
                return answer;
            }

            // OL-I7: absent, or a SEAL, is a drop once the marker exists, and below the bound corruption otherwise.
            if (await IsDroppedAsync(scope.Token).ConfigureAwait(false))
            {
                _cache.EvictLedger(_id);
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            return slot < bound ? throw Corrupt(slot, "holds no batch below the bound which decided it") : ReplicaRead.Missing.Instance;
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
    }

    /// <summary>
    /// Reads the slot's whole batch: its properties, then its bytes, which the properties' CRC-32C checks; or
    /// <see langword="null"/> when the slot holds no batch, which the read decides.
    /// </summary>
    private async ValueTask<ReplicaRead?> ReadBatchAsync(ObjectName name, Slot slot, Slot bound, CancellationToken cancellationToken)
    {
        if (await _store.GetPropertiesAsync(name, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } properties)
        {
            return null;
        }

        if (await ReadBytesAsync(name, 0, BatchLength(slot, properties), cancellationToken).ConfigureAwait(false) is not { } bytes)
        {
            // The object went between its properties and its bytes, which only a sweep does, after its drop marker.
            return null;
        }

        try
        {
            if (await IsDroppedAboveAsync(slot, bound, cancellationToken).ConfigureAwait(false))
            {
                bytes.Dispose();
                _cache.EvictLedger(_id);
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            if (ObjectLedgerSlots.Classify(properties, bytes.AsReadOnlySequence()) != ObjectSlotKind.Batch)
            {
                throw Corrupt(slot, "holds a batch which fails its CRC-32C");
            }

            // OL-I11: only decided batches (strictly below the bound) enter the cache, gated against drop/disposal sealing.
            if (slot < bound)
            {
                CacheDecidedBatch(slot, bytes, properties.Crc32c ?? ObjectStoreChecksums.Crc32C(bytes));
            }
        }
        catch
        {
            bytes.Dispose();
            throw;
        }

        return new ReplicaRead(bytes);
    }

    /// <summary>
    /// Reads a byte range of the slot's batch with one ranged read; when the range is absent, reads the slot's properties
    /// to tell a range past its batch from an absent slot or a SEAL, for which it answers <see langword="null"/>.
    /// </summary>
    private async ValueTask<ReplicaRead?> ReadRangeAsync(ObjectName name, Slot slot, int offset, int length, Slot bound, CancellationToken cancellationToken)
    {
        if (await ReadBytesAsync(name, offset, length, cancellationToken).ConfigureAwait(false) is { } bytes)
        {
            try
            {
                if (await IsDroppedAboveAsync(slot, bound, cancellationToken).ConfigureAwait(false))
                {
                    bytes.Dispose();
                    _cache.EvictLedger(_id);
                    return ReplicaWriteOutcome.Dropped.Instance;
                }
            }
            catch
            {
                bytes.Dispose();
                throw;
            }

            return new ReplicaRead(bytes);
        }

        if (await _store.GetPropertiesAsync(name, cancellationToken).ConfigureAwait(false) is { Length: > 0 } properties)
        {
            if (await IsDroppedAboveAsync(slot, bound, cancellationToken).ConfigureAwait(false))
            {
                _cache.EvictLedger(_id);
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            var batchLength = BatchLength(slot, properties);
            if (offset + (long)length > batchLength)
            {
                return new ReplicaRead(new ReplicaRead.OutsideBatch(batchLength));
            }

            // The batch holds the range now, which the read did not find: it was created meanwhile, at or above the bound,
            // or re-created after a sweep, which the drop marker answers.
        }

        return null;
    }

    /// <summary>
    /// Reads an object's bytes, or <see langword="null"/> when it is absent or shorter than the range: a sweep deleted it,
    /// a delayed SEAL may have re-created it empty, or a byte range passes its batch.
    /// </summary>
    private async ValueTask<ArcBuffer?> ReadBytesAsync(ObjectName name, int offset, int length, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ReadAsync(name, offset, length, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private async ValueTask<bool> IsDroppedAsync(CancellationToken cancellationToken)
    {
        if (await _store.GetPropertiesAsync(_dropped, cancellationToken).ConfigureAwait(false) is not null)
        {
            SealAndEvictCache();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns whether a batch a read found at or above the bound answers the drop: a delayed write may re-create one there
    /// after a sweep, which no read returns. Read after the batch, an absent marker proves the batch preceded any sweep.
    /// </summary>
    private ValueTask<bool> IsDroppedAboveAsync(Slot slot, Slot bound, CancellationToken cancellationToken) =>
        slot >= bound ? IsDroppedAsync(cancellationToken) : ValueTask.FromResult(false);

    /// <summary>Returns a batch's length, which no batch makes longer than an object's read takes.</summary>
    private int BatchLength(Slot slot, ObjectProperties properties) =>
        properties.Length <= int.MaxValue ? (int)properties.Length : throw Corrupt(slot, "holds an object longer than any batch");

    /// <summary>Raises the bound to <paramref name="acknowledged"/>, a decided prefix, and returns the bound.</summary>
    private Slot Raise(Slot acknowledged)
    {
        var bound = Volatile.Read(ref _bound);
        while (acknowledged.Value > bound)
        {
            var seen = Interlocked.CompareExchange(ref _bound, acknowledged.Value, bound);
            if (seen == bound)
            {
                PromotePendingWritesToCache(acknowledged.Value);
                return acknowledged;
            }

            bound = seen;
        }

        return new Slot(bound);
    }

    /// <summary>
    /// OL-I11: only decided batches enter the cache. A written slot is decided once every lower slot is decided (either
    /// below the storage's bound or contiguously written in this storage); pipelined writes above a gap stay buffered until
    /// the gap closes or the storage seals/disposes. Inserts into <see cref="_cache"/> under <see cref="_pendingLock"/> so
    /// a concurrent drop or disposal never resurrects evicted entries, and never blocks on local disk I/O.
    /// </summary>
    private void RecordWrittenForCache(Slot slot, ArcBuffer payload, uint crc32c)
    {
        if (!_cache.IsEnabled || payload.Length == 0 || payload.Length > _cache.CapacityBytes)
        {
            return;
        }

        var bytes = ObjectLedgerCache.ToArray(payload);
        lock (_pendingLock)
        {
            if (_cacheSealed)
            {
                return;
            }

            if (slot.Value < _decidedCacheTail)
            {
                _cache.PutBytes(_id, slot, bytes, crc32c);
            }
            else if (slot.Value == _decidedCacheTail)
            {
                _cache.PutBytes(_id, slot, bytes, crc32c);
                _decidedCacheTail++;
                if (_pendingWrites is { Count: > 0 } pending)
                {
                    while (pending.Remove(_decidedCacheTail, out var next))
                    {
                        _cache.PutBytes(_id, new Slot(_decidedCacheTail), next.Bytes, next.Crc32c);
                        _decidedCacheTail++;
                    }
                }
            }
            else
            {
                (_pendingWrites ??= [])[slot.Value] = new PendingWrite(bytes, crc32c);
            }
        }
    }

    private void CacheDecidedBatch(Slot slot, ArcBuffer payload, uint crc32c)
    {
        if (!_cache.IsEnabled || payload.Length == 0 || payload.Length > _cache.CapacityBytes)
        {
            return;
        }

        var bytes = ObjectLedgerCache.ToArray(payload);
        lock (_pendingLock)
        {
            if (_cacheSealed)
            {
                return;
            }

            _cache.PutBytes(_id, slot, bytes, crc32c);
        }
    }

    private void PromotePendingWritesToCache(long newBound)
    {
        if (!_cache.IsEnabled)
        {
            return;
        }

        lock (_pendingLock)
        {
            if (_cacheSealed)
            {
                return;
            }

            if (newBound > _decidedCacheTail)
            {
                _decidedCacheTail = newBound;
            }

            if (_pendingWrites is { Count: > 0 } pending)
            {
                List<long>? below = null;
                foreach (var slot in pending.Keys)
                {
                    if (slot < _decidedCacheTail)
                    {
                        (below ??= []).Add(slot);
                    }
                }

                if (below is not null)
                {
                    foreach (var slot in below)
                    {
                        if (pending.Remove(slot, out var entry))
                        {
                            _cache.PutBytes(_id, new Slot(slot), entry.Bytes, entry.Crc32c);
                        }
                    }
                }

                while (pending.Remove(_decidedCacheTail, out var next))
                {
                    _cache.PutBytes(_id, new Slot(_decidedCacheTail), next.Bytes, next.Crc32c);
                    _decidedCacheTail++;
                }
            }
        }
    }

    private void SealPendingWrites()
    {
        if (!_cache.IsEnabled)
        {
            return;
        }

        lock (_pendingLock)
        {
            _cacheSealed = true;
            _pendingWrites?.Clear();
        }
    }

    private void SealAndEvictCache()
    {
        SealPendingWrites();
        _cache.EvictLedger(_id);
    }

    private readonly record struct PendingWrite(byte[] Bytes, uint Crc32c);

    private LedgerStorageCorruptException Corrupt(Slot slot, string what) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Ledger {_id}'s slot {slot} {what} (object-ledgers.md §6, OL-I7)."));

    private AdmissionGate.Admission Admit()
    {
        var admission = _operations.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        return admission;
    }

    private AdmissionGate.Admission AdmitRead()
    {
        var admission = Admit();
        if (_reads.IsCancellationRequested)
        {
            admission.Dispose();
            throw ReadsStoppedException();
        }

        return admission;
    }

    private bool ReadsStopped(CancellationToken cancellationToken) => _reads.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    private static ObjectDisposedException ReadsStoppedException() =>
        new(nameof(ObjectLedgerStorage), "The storage's reads stopped: its owner is disposed.");

    /// <summary>
    /// The token one operation's calls run under: the caller's linked with the storage's, or the storage's alone when the
    /// caller's cannot be cancelled, so a call without a deadline allocates no linked source.
    /// </summary>
    private readonly struct CallScope : IDisposable
    {
        private readonly CancellationTokenSource? _linked;

        public CallScope(CancellationToken cancellationToken, CancellationToken storage)
        {
            _linked = cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, storage) : null;
            Token = _linked?.Token ?? storage;
        }

        public CancellationToken Token { get; }

        public void Dispose() => _linked?.Dispose();
    }
}
