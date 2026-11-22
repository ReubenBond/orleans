using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

internal sealed partial class AppendObjectLedgerStorage
{
    [ThreadStatic]
    private static Crc32? t_appendCrc32;

    private enum RollSettlement
    {
        Rolled,
        Sealed,
        RetryAtUpdatedOffset,
    }

    private sealed class QueuedWrite(Slot slot, ArcBuffer payload, int length, uint frameCrc)
    {
        private ArcBuffer _payload = payload;
        private int _disposed;

        public Slot Slot { get; } = slot;
        public int Length { get; } = length;
        public uint FrameCrc { get; } = frameCrc;
        public bool InFlight { get; set; }
        public int ActiveWaiters { get; set; } = 1;
        public TaskCompletionSource<LedgerWriteOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ArcBuffer Payload => _payload;

        public void DisposePayload()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _payload.Dispose();
                _payload = default;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<LedgerWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot acknowledged,
        CancellationToken cancellationToken = default)
    {
        using var scope = Admit();
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        if (payload.Length > _options.MaxBatchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                string.Create(CultureInfo.InvariantCulture, $"The batch holds {payload.Length} bytes, past the store's limit of {_options.MaxBatchBytes}."));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(acknowledged.Value);
        cancellationToken.ThrowIfCancellationRequested();

        _ = Raise(acknowledged);
        var frameCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, slot, payload);

        QueuedWrite? queued = null;
        var verifyPrunedWritten = false;
        lock (_writeLock)
        {
            if (_writes.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(AppendObjectLedgerStorage), "The storage's disposal ended the write.");
            }

            PruneWrittenCrcsLocked(acknowledged.Value);

            if (_isSealed)
            {
                return ReplicaWriteOutcome.Sealed.Instance;
            }

            if (slot.Value < _nextSlot)
            {
                if (_writtenCrcs.TryGetValue(slot.Value, out var existingCrc))
                {
                    if (existingCrc != frameCrc)
                    {
                        throw Corrupt(slot, "was written with different bytes than the retried payload");
                    }

                    return ReplicaWriteOutcome.Written.Instance;
                }

                verifyPrunedWritten = true;
            }
            else if (_permanentFailure is { } failure)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
            else if (_inFlight.TryGetValue(slot.Value, out var inFlight))
            {
                if (inFlight.FrameCrc != frameCrc)
                {
                    throw Corrupt(slot, "queued conflicting bytes for the same slot");
                }

                inFlight.ActiveWaiters += 1;
                queued = inFlight;
            }
            else if (_queue.TryGetValue(slot.Value, out var existing))
            {
                if (existing.FrameCrc != frameCrc)
                {
                    throw Corrupt(slot, "queued conflicting bytes for the same slot");
                }

                existing.ActiveWaiters += 1;
                queued = existing;
            }
            else
            {
                queued = new QueuedWrite(slot, payload.Retain(), payload.Length, frameCrc);
                _queue.Add(slot.Value, queued);
                StartFlushLoopIfReadyLocked();
            }
        }

        if (verifyPrunedWritten)
        {
            return await VerifyPrunedWrittenSlotAsync(slot, frameCrc, cancellationToken).ConfigureAwait(false);
        }

        Debug.Assert(queued is not null);
        try
        {
            return await queued.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseCallerWaiter(queued);
            throw;
        }
        catch (OperationCanceledException) when (_writes.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(AppendObjectLedgerStorage), "The storage's disposal ended the write.");
        }
    }

    private async ValueTask<LedgerWriteOutcome> VerifyPrunedWrittenSlotAsync(
        Slot slot,
        uint frameCrc,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_writes.Token, cancellationToken);
        try
        {
            var located = await TryLocateSlotAsync(slot, linked.Token).ConfigureAwait(false);
            if (located.Dropped)
            {
                MarkSealed();
                return ReplicaWriteOutcome.Sealed.Instance;
            }

            if (located.Location is { } loc)
            {
                if (loc.FrameCrc32c != frameCrc)
                {
                    throw Corrupt(slot, "was written with different bytes than the retried payload");
                }

                return ReplicaWriteOutcome.Written.Instance;
            }

            throw Corrupt(slot, "is below the writer's next slot but cannot be found in its extents");
        }
        catch (OperationCanceledException) when (_writes.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(AppendObjectLedgerStorage), "The storage's disposal ended the write.");
        }
    }

    private void PruneWrittenCrcsLocked(long acknowledged)
    {
        if (acknowledged <= _prunedWrittenCrcsBelow || _writtenCrcs.Count == 0)
        {
            return;
        }

        if (_writtenCrcs.Count <= 256 && acknowledged - _prunedWrittenCrcsBelow < 256)
        {
            return;
        }

        var limit = acknowledged;
        if (limit - _prunedWrittenCrcsBelow <= _writtenCrcs.Count)
        {
            for (var s = _prunedWrittenCrcsBelow; s < limit; s++)
            {
                _ = _writtenCrcs.Remove(s);
            }
        }
        else
        {
            List<long>? stale = null;
            foreach (var key in _writtenCrcs.Keys)
            {
                if (key < limit)
                {
                    (stale ??= []).Add(key);
                }
            }

            if (stale is not null)
            {
                foreach (var key in stale)
                {
                    _ = _writtenCrcs.Remove(key);
                }
            }
        }

        _prunedWrittenCrcsBelow = limit;
    }

    private void StartFlushLoopIfReadyLocked()
    {
        if (!_flushing && !_isSealed && _permanentFailure is null && !_writes.IsCancellationRequested && _queue.ContainsKey(_nextSlot))
        {
            _flushing = true;
            _flushTask = RunFlushLoopAsync();
        }
    }

    private void ReleaseCallerWaiter(QueuedWrite item)
    {
        List<QueuedWrite>? toCancel = null;
        lock (_writeLock)
        {
            item.ActiveWaiters -= 1;
            if (!item.InFlight && item.ActiveWaiters == 0 && _queue.Count > 0)
            {
                toCancel = TrimTrailingWaiterlessLocked();
            }
        }

        if (toCancel is not null)
        {
            DisposeAndCancel(toCancel);
        }
    }

    private List<QueuedWrite>? TrimTrailingWaiterlessLocked()
    {
        long highestActiveSlot = -1;
        foreach (var (slotKey, queued) in _queue)
        {
            if (queued.ActiveWaiters > 0)
            {
                highestActiveSlot = slotKey;
            }
        }

        List<long>? removableKeys = null;
        List<QueuedWrite>? removed = null;
        foreach (var (slotKey, queued) in _queue)
        {
            if (slotKey > highestActiveSlot)
            {
                (removableKeys ??= []).Add(slotKey);
                (removed ??= []).Add(queued);
            }
        }

        if (removableKeys is not null)
        {
            foreach (var key in removableKeys)
            {
                _ = _queue.Remove(key);
            }
        }

        return removed;
    }

    private async Task RunFlushLoopAsync()
    {
        const long maxExtentDataEnd = int.MaxValue - ObjectLedgerExtentFormat.RollAppendLength;

        while (true)
        {
            List<QueuedWrite>? batch = null;
            List<QueuedWrite>? disposedQueue = null;
            var shouldRoll = false;
            var extentFirstSlot = 0L;
            var extentOffset = 0L;
            var nextSlot = 0L;
            var totalAppendBytes = 0;

            lock (_writeLock)
            {
                if (_writes.IsCancellationRequested)
                {
                    disposedQueue = TakeAllQueuedLocked();
                    _flushing = false;
                }
                else if (_isSealed || _permanentFailure is not null || !_queue.TryGetValue(_nextSlot, out var first))
                {
                    _flushing = false;
                    return;
                }
                else
                {
                    extentFirstSlot = _extentFirstSlot;
                    extentOffset = _extentOffset;
                    nextSlot = _nextSlot;

                    var firstAppendBytes =
                        (long)ObjectLedgerExtentFormat.FrameHeaderLength + first.Length + ObjectLedgerExtentFormat.TrailerLength;

                    // Roll before appending if this extent already holds appends and has reached its target size,
                    // append-count ceiling, or int.MaxValue extent byte ceiling (object-ledgers.md §8.2).
                    if (_extentAppends > 0 &&
                        (extentOffset >= _options.ExtentTargetBytes ||
                         _extentAppends >= _options.ExtentMaxAppends ||
                         extentOffset + firstAppendBytes > maxExtentDataEnd))
                    {
                        shouldRoll = true;
                    }
                    else
                    {
                        var maxAppendBytes = Math.Min(
                            IAppendableObjectStore.MaxAppendLength,
                            maxExtentDataEnd - extentOffset);

                        batch = [];
                        var framesBytes = 0L;
                        var cursor = _nextSlot;
                        while (_queue.TryGetValue(cursor, out var item))
                        {
                            var frameBytes = (long)ObjectLedgerExtentFormat.FrameHeaderLength + item.Length;
                            if (batch.Count > 0 && framesBytes + frameBytes + ObjectLedgerExtentFormat.TrailerLength > maxAppendBytes)
                            {
                                break;
                            }

                            _ = _queue.Remove(cursor);
                            item.InFlight = true;
                            _inFlight.Add(cursor, item);
                            batch.Add(item);
                            framesBytes += frameBytes;
                            cursor += 1;
                        }

                        totalAppendBytes = checked((int)(framesBytes + ObjectLedgerExtentFormat.TrailerLength));
                    }
                }
            }

            if (disposedQueue is not null)
            {
                DisposeAndCancel(disposedQueue);
                return;
            }

            if (shouldRoll)
            {
                await ExecuteRollStepAsync(extentFirstSlot, extentOffset, nextSlot).ConfigureAwait(false);
            }
            else if (batch is not null)
            {
                await ExecuteAppendStepAsync(extentFirstSlot, extentOffset, batch, totalAppendBytes).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteRollStepAsync(long extentFirstSlot, long extentOffset, long nextSlot)
    {
        var extentName = ObjectLedgerNames.Extent(_ledger, new Slot(extentFirstSlot));
        try
        {
            using var rollBuffer = ObjectLedgerExtentFormat.EncodeRollAppend(new Slot(nextSlot), extentOffset);
            var rollCrc = ObjectStoreChecksums.Crc32C(rollBuffer.AsReadOnlySequence());
            var outcome = await AppendCheckedAsync(extentName, extentOffset, rollBuffer, rollCrc, _writes.Token)
                .ConfigureAwait(false);

            if (outcome is ObjectAppendOutcome.Appended appended)
            {
                _index.RecordRolledExtent(extentFirstSlot, extentName, new Slot(nextSlot), appended.Length);
                TransitionWriterToNextExtent(nextSlot);
                return;
            }

            if (outcome is ObjectAppendOutcome.Absent)
            {
                MarkSealed();
                return;
            }

            if (outcome is ObjectAppendOutcome.Sealed)
            {
                if (!_offsetDirty)
                {
                    MarkSealed();
                    return;
                }

                await ApplyRollSettlementAsync(
                    extentName,
                    extentFirstSlot,
                    nextSlot,
                    extentOffset,
                    fromLengthMismatch: false).ConfigureAwait(false);
                return;
            }

            if (outcome is ObjectAppendOutcome.LengthMismatch)
            {
                await ApplyRollSettlementAsync(
                    extentName,
                    extentFirstSlot,
                    nextSlot,
                    extentOffset,
                    fromLengthMismatch: true).ConfigureAwait(false);
                return;
            }

            throw new UnreachableException();
        }
        catch (OperationCanceledException) when (_writes.IsCancellationRequested)
        {
            CancelAllQueuedForDisposal(activeBatch: null);
        }
        catch (Exception ex) when (IsPermanentFailure(ex))
        {
            RecordPermanentFailureAndDrain(ex, activeBatch: null);
        }
        catch (Exception ex)
        {
            // Align transient roll failure handling with ExecuteAppendStepAsync: mark offset dirty and fail
            // only the queued front slot waiting on this roll, leaving later slots intact for retry.
            QueuedWrite? front = null;
            lock (_writeLock)
            {
                _offsetDirty = true;
                if (_queue.Remove(nextSlot, out var item))
                {
                    front = item;
                }
            }

            if (front is not null)
            {
                front.DisposePayload();
                _ = front.Completion.TrySetException(ex);
            }
        }
    }

    private async Task ApplyRollSettlementAsync(
        ObjectName extentName,
        long extentFirstSlot,
        long nextSlot,
        long fromOffset,
        bool fromLengthMismatch)
    {
        var settlement = await SettleExtentForRollAsync(
            extentName,
            extentFirstSlot,
            nextSlot,
            fromOffset,
            fromLengthMismatch).ConfigureAwait(false);

        switch (settlement)
        {
            case RollSettlement.Rolled:
                TransitionWriterToNextExtent(nextSlot);
                break;

            case RollSettlement.Sealed:
                MarkSealed();
                break;

            case RollSettlement.RetryAtUpdatedOffset:
                break;

            default:
                throw new UnreachableException();
        }
    }

    private void TransitionWriterToNextExtent(long nextSlot)
    {
        lock (_writeLock)
        {
            _extentFirstSlot = nextSlot;
            _extentOffset = 0;
            _extentAppends = 0;
            _extentCreated = false;
            _offsetDirty = false;
        }
    }

    private async Task ExecuteAppendStepAsync(
        long extentFirstSlot,
        long extentOffset,
        List<QueuedWrite> batch,
        int totalAppendBytes)
    {
        var extentName = ObjectLedgerNames.Extent(_ledger, new Slot(extentFirstSlot));
        var batchHandled = false;

        try
        {
            if (!_extentCreated)
            {
                var created = await _appendable.CreateAppendableAsync(extentName, _writes.Token).ConfigureAwait(false);
                if (created is ObjectCreateOutcome.Created)
                {
                    lock (_writeLock)
                    {
                        _extentCreated = true;
                    }
                }
                else if (created is ObjectCreateOutcome.Exists)
                {
                    // Prior create timed out or a successor took over: inspect before appending.
                    lock (_writeLock)
                    {
                        _extentCreated = true;
                    }

                    var existingProps = await _objects.GetPropertiesAsync(extentName, _writes.Token)
                        .ConfigureAwait(false);
                    if (existingProps is null || existingProps.Length == ObjectLedgerExtentFormat.TombstoneLength)
                    {
                        MarkSealedAndCompleteBatch(batch);
                        batchHandled = true;
                        return;
                    }

                    if (existingProps.Length > extentOffset)
                    {
                        await SettleExtentForBatchAsync(
                            extentName,
                            extentFirstSlot,
                            extentOffset,
                            batch,
                            appendReturnedSealed: false).ConfigureAwait(false);
                        batchHandled = true;
                        return;
                    }
                }
                else
                {
                    throw new UnreachableException();
                }
            }

            var slotLocations = new SlotLocation[batch.Count];
            using var appendBuffer = BuildCoalescedAppend(batch, extentOffset, totalAppendBytes, slotLocations, out var appendCrc);

            var outcome = await AppendCheckedAsync(extentName, extentOffset, appendBuffer, appendCrc, _writes.Token)
                .ConfigureAwait(false);

            if (outcome is ObjectAppendOutcome.Appended appended)
            {
                var newLength = appended.Length;
                _index.RecordAppendedBatch(
                    extentFirstSlot,
                    extentName,
                    batch[0].Slot,
                    batch.Count,
                    extentOffset,
                    newLength,
                    slotLocations);

                lock (_writeLock)
                {
                    foreach (var item in batch)
                    {
                        _ = _inFlight.Remove(item.Slot.Value);
                        item.InFlight = false;
                        _writtenCrcs[item.Slot.Value] = item.FrameCrc;
                    }

                    _extentOffset = newLength;
                    _extentAppends += 1;
                    _nextSlot += batch.Count;
                    _offsetDirty = false;
                }

                CompleteBatchWritten(batch);
                batchHandled = true;
                return;
            }

            if (outcome is ObjectAppendOutcome.Absent)
            {
                MarkSealedAndCompleteBatch(batch);
                batchHandled = true;
                return;
            }

            if (outcome is ObjectAppendOutcome.Sealed)
            {
                if (!_offsetDirty)
                {
                    MarkSealedAndCompleteBatch(batch);
                    batchHandled = true;
                    return;
                }

                await SettleExtentForBatchAsync(
                    extentName,
                    extentFirstSlot,
                    extentOffset,
                    batch,
                    appendReturnedSealed: true).ConfigureAwait(false);
                batchHandled = true;
                return;
            }

            if (outcome is ObjectAppendOutcome.LengthMismatch)
            {
                await SettleExtentForBatchAsync(
                    extentName,
                    extentFirstSlot,
                    extentOffset,
                    batch,
                    appendReturnedSealed: false).ConfigureAwait(false);
                batchHandled = true;
                return;
            }

            throw new UnreachableException();
        }
        catch (OperationCanceledException) when (_writes.IsCancellationRequested)
        {
            CancelAllQueuedForDisposal(batchHandled ? null : batch);
            batchHandled = true;
        }
        catch (Exception ex) when (IsPermanentFailure(ex))
        {
            RecordPermanentFailureAndDrain(ex, batchHandled ? null : batch);
            batchHandled = true;
        }
        catch (Exception ex)
        {
            if (!batchHandled)
            {
                FailTransientBatch(batch, ex);
                batchHandled = true;
            }
        }
        finally
        {
            if (!batchHandled)
            {
                RemoveFromInFlightAndDispose(batch);
            }
        }
    }

    private async Task SettleExtentForBatchAsync(
        ObjectName extentName,
        long extentFirstSlot,
        long fromOffset,
        List<QueuedWrite> batch,
        bool appendReturnedSealed)
    {
        var props = await _objects.GetPropertiesAsync(extentName, _writes.Token).ConfigureAwait(false);
        if (props is null || props.Length == ObjectLedgerExtentFormat.TombstoneLength)
        {
            MarkSealedAndCompleteBatch(batch);
            return;
        }

        if (props.Length < fromOffset)
        {
            if (await IsDroppedAsync(_writes.Token).ConfigureAwait(false))
            {
                MarkSealedAndCompleteBatch(batch);
                return;
            }

            throw Corrupt(batch[0].Slot, "shrank below its written length");
        }

        if (props.Length == fromOffset)
        {
            if (appendReturnedSealed)
            {
                MarkSealedAndCompleteBatch(batch);
                return;
            }

            if (await IsDroppedAsync(_writes.Token).ConfigureAwait(false))
            {
                MarkSealedAndCompleteBatch(batch);
                return;
            }

            throw Corrupt(batch[0].Slot, "reported a length mismatch at its current length");
        }

        var settled = await ReadAndAdvanceExtentTailAsync(
            extentName,
            extentFirstSlot,
            fromOffset,
            props.Length,
            _writes.Token).ConfigureAwait(false);

        if (settled.HitTombstone)
        {
            MarkSealedAndCompleteBatch(batch);
            return;
        }

        var landed = new List<QueuedWrite>(batch.Count);
        var unlanded = new List<QueuedWrite>(batch.Count);
        List<QueuedWrite>? drainedOnSeal = null;
        List<QueuedWrite>? droppedWaiterless = null;

        lock (_writeLock)
        {
            var newNextSlot = _nextSlot;
            foreach (var item in batch)
            {
                _ = _inFlight.Remove(item.Slot.Value);
                item.InFlight = false;
                if (item.Slot.Value < newNextSlot)
                {
                    if (settled.LandedCrcs is null ||
                        !settled.LandedCrcs.TryGetValue(item.Slot.Value, out var landedCrc) ||
                        landedCrc != item.FrameCrc)
                    {
                        throw Corrupt(item.Slot, "contains bytes from a different writer");
                    }

                    landed.Add(item);
                }
                else
                {
                    unlanded.Add(item);
                }
            }

            if (appendReturnedSealed)
            {
                _isSealed = true;
                drainedOnSeal = TakeAllQueuedLocked();
            }
            else if (settled.RollNextSlot is { } rolledTo)
            {
                _extentFirstSlot = rolledTo.Value;
                _extentOffset = 0;
                _extentAppends = 0;
                _extentCreated = false;
                _offsetDirty = false;
                ReenqueueUnlandedLocked(unlanded, out droppedWaiterless);
            }
            else
            {
                ReenqueueUnlandedLocked(unlanded, out droppedWaiterless);
            }
        }

        CompleteBatchWritten(landed);

        if (appendReturnedSealed)
        {
            CompleteBatchSealed(unlanded);
            if (drainedOnSeal is not null)
            {
                CompleteBatchSealed(drainedOnSeal);
            }

            return;
        }

        if (droppedWaiterless is not null)
        {
            DisposeAndCancel(droppedWaiterless);
        }
    }

    private void ReenqueueUnlandedLocked(List<QueuedWrite> unlanded, out List<QueuedWrite>? droppedWaiterless)
    {
        foreach (var item in unlanded)
        {
            Debug.Assert(!_queue.ContainsKey(item.Slot.Value));
            _queue.Add(item.Slot.Value, item);
        }

        droppedWaiterless = TrimTrailingWaiterlessLocked();
    }

    private async Task<RollSettlement> SettleExtentForRollAsync(
        ObjectName extentName,
        long extentFirstSlot,
        long rollTargetSlot,
        long fromOffset,
        bool fromLengthMismatch)
    {
        var props = await _objects.GetPropertiesAsync(extentName, _writes.Token).ConfigureAwait(false);
        if (props is null || props.Length == ObjectLedgerExtentFormat.TombstoneLength && props.Crc32c is not null)
        {
            if (await IsDroppedAsync(_writes.Token).ConfigureAwait(false))
            {
                return RollSettlement.Sealed;
            }

            throw Corrupt(new Slot(rollTargetSlot), "vanished or was tombstoned without a dropped marker during roll");
        }

        if (props.Length < fromOffset || fromLengthMismatch && props.Length == fromOffset)
        {
            if (await IsDroppedAsync(_writes.Token).ConfigureAwait(false))
            {
                return RollSettlement.Sealed;
            }

            throw Corrupt(
                new Slot(rollTargetSlot),
                props.Length < fromOffset
                    ? "shrank below its written length"
                    : "reported a length mismatch at its current length");
        }

        if (props.Length == fromOffset)
        {
            return RollSettlement.Sealed;
        }

        var settled = await ReadAndAdvanceExtentTailAsync(
            extentName,
            extentFirstSlot,
            fromOffset,
            props.Length,
            _writes.Token).ConfigureAwait(false);

        if (settled.HitTombstone)
        {
            return RollSettlement.Sealed;
        }

        if (settled.RollNextSlot is { } rolledTo)
        {
            if (rolledTo.Value != rollTargetSlot)
            {
                throw Corrupt(rolledTo, "rolled to an unexpected slot");
            }

            return RollSettlement.Rolled;
        }

        return fromLengthMismatch ? RollSettlement.RetryAtUpdatedOffset : RollSettlement.Sealed;
    }

    private async Task<(bool HitTombstone, Slot? RollNextSlot, Dictionary<long, uint>? LandedCrcs)> ReadAndAdvanceExtentTailAsync(
        ObjectName extentName,
        long extentFirstSlot,
        long fromOffset,
        long length,
        CancellationToken cancellationToken)
    {
        var tailLength = checked((int)(length - fromOffset));
        ArcBuffer? read;
        try
        {
            read = await _objects.ReadAsync(extentName, fromOffset, tailLength, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, null);
            }

            throw Corrupt(new Slot(_nextSlot), "shrank while settling its written tail");
        }

        if (read is null)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, null);
            }

            throw Corrupt(new Slot(_nextSlot), "vanished without a dropped marker while settling its written tail");
        }

        using var buffer = read.Value;
        var sequence = buffer.AsReadOnlySequence();
        if (fromOffset == 0 && buffer.Length == ObjectLedgerExtentFormat.TombstoneLength)
        {
            Span<byte> tombstoneBytes = stackalloc byte[ObjectLedgerExtentFormat.TombstoneLength];
            sequence.CopyTo(tombstoneBytes);
            if (ObjectLedgerExtentFormat.DecodeTombstone(tombstoneBytes) is Slot)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, null, null);
                }

                throw Corrupt(new Slot(_nextSlot), "contains a tombstone without a dropped marker");
            }
        }

        if (buffer.Length != tailLength)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, null);
            }

            throw Corrupt(new Slot(_nextSlot), "returned a truncated tail while settling");
        }

        long expectedNextSlot;
        lock (_writeLock)
        {
            expectedNextSlot = _nextSlot;
        }

        var decodedOutcome = ObjectLedgerExtentFormat.DecodeExtentRange(
            sequence,
            fromOffset,
            expectedFirstSlot: new Slot(expectedNextSlot));

        if (decodedOutcome is not ObjectLedgerDecodedExtent decoded)
        {
            var rejection = decodedOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
            throw Corrupt(extentName, "contains a corrupt extent tail", rejection);
        }

        if (decoded.RollNextSlot is { } finalRoll && finalRoll.Value <= extentFirstSlot)
        {
            throw Corrupt(finalRoll, "contains a non-advancing ROLL frame");
        }

        var landedCrcs = new Dictionary<long, uint>();
        foreach (var append in decoded.Appends)
        {
            var dataCount = 0;
            Slot? appendRoll = null;
            foreach (var frame in append.Frames)
            {
                if (frame.Kind == ObjectLedgerFrameKind.Data)
                {
                    dataCount += 1;
                }
                else if (frame.Kind == ObjectLedgerFrameKind.Roll)
                {
                    appendRoll = frame.Slot;
                }
            }

            var appendEndOffset = append.TrailerOffset + ObjectLedgerExtentFormat.TrailerLength;
            if (dataCount > 0)
            {
                var locations = new SlotLocation[dataCount];
                var dataIndex = 0;
                foreach (var frame in append.Frames)
                {
                    if (frame.Kind == ObjectLedgerFrameKind.Data)
                    {
                        locations[dataIndex++] = new SlotLocation(
                            checked((int)(fromOffset + frame.PayloadOffset)),
                            frame.PayloadLength,
                            frame.Crc32c);
                        landedCrcs[frame.Slot.Value] = frame.Crc32c;
                    }
                }

                _index.RecordAppendedBatch(
                    extentFirstSlot,
                    extentName,
                    append.Trailer.FirstSlot,
                    append.Trailer.Count,
                    append.Trailer.StartOffset,
                    appendEndOffset,
                    locations);
            }

            if (appendRoll is { } rollTarget)
            {
                _index.RecordRolledExtent(extentFirstSlot, extentName, rollTarget, appendEndOffset);
            }
        }

        lock (_writeLock)
        {
            foreach (var (slotValue, crc) in landedCrcs)
            {
                _writtenCrcs[slotValue] = crc;
            }

            _extentOffset = length;
            _extentAppends += decoded.Appends.Length;
            _nextSlot = decoded.NextSlot.Value;
            _offsetDirty = false;
        }

        return (false, decoded.RollNextSlot, landedCrcs);
    }

    private async ValueTask<ObjectAppendOutcome> AppendCheckedAsync(
        ObjectName extentName,
        long expectedOffset,
        ArcBuffer data,
        uint crc32c,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _appendable.AppendAsync(
                extentName,
                expectedOffset,
                data,
                crc32c,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectContentChangedException exception)
        {
            ObjectContentChangedDefect.Stop(exception);
            throw;
        }
    }

    private static Crc32 RentAppendCrc32C()
    {
        var crc = t_appendCrc32;
        if (crc is null)
        {
            crc = new Crc32(Crc32ParameterSet.Crc32C);
            t_appendCrc32 = crc;
        }
        else
        {
            crc.Reset();
        }

        return crc;
    }

    private static ArcBuffer BuildCoalescedAppend(
        List<QueuedWrite> batch,
        long startOffset,
        int totalBytes,
        SlotLocation[] locations,
        out uint appendCrc32c)
    {
        using var writer = new ArcBufferWriter(totalBytes);
        var crc = RentAppendCrc32C();
        Span<byte> header = stackalloc byte[ObjectLedgerExtentFormat.FrameHeaderLength];
        var offset = startOffset;
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            ObjectLedgerExtentFormat.WriteFrameHeader(
                header,
                ObjectLedgerFrameKind.Data,
                item.Slot,
                item.Length,
                item.FrameCrc);
            writer.Write(header);
            crc.Append(header);
            item.Payload.CopyTo(writer);
            foreach (var span in item.Payload)
            {
                if (!span.IsEmpty)
                {
                    crc.Append(span);
                }
            }

            locations[i] = new SlotLocation(
                checked((int)(offset + ObjectLedgerExtentFormat.FrameHeaderLength)),
                item.Length,
                item.FrameCrc);
            offset += ObjectLedgerExtentFormat.FrameHeaderLength + item.Length;
        }

        Span<byte> trailer = stackalloc byte[ObjectLedgerExtentFormat.TrailerLength];
        ObjectLedgerExtentFormat.WriteTrailer(trailer, batch[0].Slot, batch.Count, startOffset);
        writer.Write(trailer);
        crc.Append(trailer);
        appendCrc32c = crc.GetCurrentHashAsUInt32();
        return writer.ConsumeSlice(writer.Length);
    }

    private static bool IsPermanentFailure(Exception exception) =>
        exception is LedgerStorageCorruptException or ObjectContentChangedException or ObjectStoreRefusedException;

    private List<QueuedWrite> TakeAllQueuedLocked()
    {
        var drained = new List<QueuedWrite>(_queue.Count);
        foreach (var item in _queue.Values)
        {
            drained.Add(item);
        }

        _queue.Clear();
        return drained;
    }

    private void MarkSealed()
    {
        List<QueuedWrite> drained;
        lock (_writeLock)
        {
            _isSealed = true;
            drained = TakeAllQueuedLocked();
        }

        CompleteBatchSealed(drained);
    }

    private void MarkSealedAndCompleteBatch(List<QueuedWrite> batch)
    {
        List<QueuedWrite> drained;
        lock (_writeLock)
        {
            _isSealed = true;
            foreach (var item in batch)
            {
                _ = _inFlight.Remove(item.Slot.Value);
                item.InFlight = false;
            }

            drained = TakeAllQueuedLocked();
        }

        CompleteBatchSealed(batch);
        CompleteBatchSealed(drained);
    }

    private void RecordPermanentFailureAndDrain(Exception exception, List<QueuedWrite>? activeBatch)
    {
        List<QueuedWrite> drained;
        lock (_writeLock)
        {
            _permanentFailure ??= exception;
            if (activeBatch is not null)
            {
                foreach (var item in activeBatch)
                {
                    _ = _inFlight.Remove(item.Slot.Value);
                    item.InFlight = false;
                }
            }

            drained = TakeAllQueuedLocked();
        }

        if (activeBatch is not null)
        {
            FailBatch(activeBatch, exception);
        }

        FailBatch(drained, exception);
    }

    private void FailTransientBatch(List<QueuedWrite> batch, Exception exception)
    {
        lock (_writeLock)
        {
            _offsetDirty = true;
            foreach (var item in batch)
            {
                _ = _inFlight.Remove(item.Slot.Value);
                item.InFlight = false;
            }
        }

        FailBatch(batch, exception);
    }

    private void CancelAllQueuedForDisposal(List<QueuedWrite>? activeBatch)
    {
        List<QueuedWrite> drained;
        lock (_writeLock)
        {
            if (activeBatch is not null)
            {
                foreach (var item in activeBatch)
                {
                    _ = _inFlight.Remove(item.Slot.Value);
                    item.InFlight = false;
                }
            }

            drained = TakeAllQueuedLocked();
        }

        if (activeBatch is not null)
        {
            DisposeAndCancel(activeBatch);
        }

        DisposeAndCancel(drained);
    }

    private void RemoveFromInFlightAndDispose(List<QueuedWrite> batch)
    {
        lock (_writeLock)
        {
            foreach (var item in batch)
            {
                _ = _inFlight.Remove(item.Slot.Value);
                item.InFlight = false;
            }
        }

        foreach (var item in batch)
        {
            item.DisposePayload();
        }
    }

    private void CompleteBatchWritten(List<QueuedWrite> items)
    {
        foreach (var item in items)
        {
            CacheDecidedBatch(item.Slot, item.Payload);
            item.DisposePayload();
            _ = item.Completion.TrySetResult(ReplicaWriteOutcome.Written.Instance);
        }
    }

    private static void CompleteBatchSealed(List<QueuedWrite> items)
    {
        foreach (var item in items)
        {
            item.DisposePayload();
            _ = item.Completion.TrySetResult(ReplicaWriteOutcome.Sealed.Instance);
        }
    }

    private static void FailBatch(List<QueuedWrite> items, Exception exception)
    {
        foreach (var item in items)
        {
            item.DisposePayload();
            _ = item.Completion.TrySetException(exception);
        }
    }

    private static void DisposeAndCancel(List<QueuedWrite> items)
    {
        foreach (var item in items)
        {
            item.DisposePayload();
            _ = item.Completion.TrySetCanceled();
        }
    }
}
