using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

internal sealed partial class AppendObjectLedgerStorage
{
    private readonly record struct ExtentListing(ExtentIndex Extent, long ListedLength);

    private enum ExtentScanStepKind
    {
        Data,
        Dropped,
        EndOfExtent,
    }

    private readonly record struct ExtentScanStep(ExtentScanStepKind Kind, ArcBuffer Payload);

    private sealed class ExtentScanState(
        ExtentIndex extent,
        ExtentChunkReader reader,
        Slot nextSlot,
        Slot below,
        Slot initialAppendFirstSlot,
        long initialAppendStartOffset,
        long bound)
    {
        public ExtentIndex Extent { get; } = extent;
        public ExtentChunkReader Reader { get; } = reader;
        public Slot NextSlot { get; set; } = nextSlot;
        public Slot Below { get; } = below;
        public Slot AppendFirstSlot { get; set; } = initialAppendFirstSlot;
        public long AppendStartOffset { get; set; } = initialAppendStartOffset;
        public int AppendFrameCount { get; set; }
        public long Bound { get; } = bound;
        public int DroppedCheckedGeneration { get; set; } = -1;
        public byte[] HeaderBytes { get; } = new byte[ObjectLedgerExtentFormat.FrameHeaderLength];
        public byte[] TrailerBytes { get; } = new byte[ObjectLedgerExtentFormat.TrailerLength];
        public List<AppendSpan> PendingSpans { get; } = [];
        public long PendingWalkedUpTo { get; set; }
        public Slot? PendingRollNext { get; set; }
        public int LastFlushedGeneration { get; set; } = -1;
    }

    private async ValueTask<ReplicaRead> ReadPointAsync(
        Slot slot,
        (int Offset, int Length)? range,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            var bound = Volatile.Read(ref _durableBelow);

            // Fast path 0: process-local verified cache of decided batches (OL-I11).
            if (await _cache.TryGetAsync(_ledger, slot, linkedToken).ConfigureAwait(false) is { } cached)
            {
                if (slot.Value >= bound && await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                {
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

            // Fast path 1: slot already cached in the per-slot index.
            if (_index.TryGetCachedSlot(slot, out var cachedExtentName, out var cachedLocation))
            {
                return await ReadLocatedSlotAsync(
                    slot,
                    cachedExtentName!,
                    cachedLocation,
                    range,
                    bound,
                    linkedToken).ConfigureAwait(false);
            }

            // Fast path 2: whole-slot read hitting a cached single-frame AppendSpan reads header + payload in one GET.
            if (range is null &&
                _index.TryGetCachedAppendSpan(slot, out var singleExtent, out var singleSpan) &&
                singleSpan.Count == 1)
            {
                return await ReadSingleFrameSpanAsync(slot, singleExtent!, singleSpan, bound, linkedToken)
                    .ConfigureAwait(false);
            }

            var located = await TryLocateSlotAsync(slot, linkedToken, allowSingleFrameRead: range is null, bound)
                .ConfigureAwait(false);

            if (located.Dropped)
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            if (located.DirectPayload is { } direct)
            {
                return direct;
            }

            if (located.Location is null)
            {
                if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                return slot.Value < bound
                    ? throw Corrupt(slot, "holds no batch below the bound which decided it")
                    : ReplicaRead.Missing.Instance;
            }

            return await ReadLocatedSlotAsync(
                slot,
                located.ExtentName!,
                located.Location.Value,
                range,
                bound,
                linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ReadsStopped(callerToken))
        {
            throw ReadsStoppedException();
        }
    }

    private async ValueTask<ReplicaRead> ReadLocatedSlotAsync(
        Slot slot,
        ObjectName extentName,
        SlotLocation loc,
        (int Offset, int Length)? range,
        long bound,
        CancellationToken cancellationToken)
    {
        long readOffset;
        int readLength;
        if (range is { } slice)
        {
            if (slice.Offset > loc.BatchLength || loc.BatchLength - slice.Offset < slice.Length)
            {
                if (slot.Value >= bound && await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                return new ReplicaRead(new ReplicaRead.OutsideBatch(loc.BatchLength));
            }

            readOffset = (long)loc.BatchOffset + slice.Offset;
            readLength = slice.Length;
        }
        else
        {
            readOffset = loc.BatchOffset;
            readLength = loc.BatchLength;
        }

        ArcBuffer? read;
        try
        {
            read = await _objects.ReadAsync(extentName, readOffset, readLength, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            throw Corrupt(slot, "could not be read at its indexed offset");
        }

        if (read is null)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            throw Corrupt(slot, "vanished without a dropped marker");
        }

        var buffer = read.Value;
        try
        {
            if (buffer.Length != readLength)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    buffer.Dispose();
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                throw Corrupt(slot, "returned fewer bytes than its frame header length");
            }

            if (range is null)
            {
                var actualCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, slot, buffer);
                if (actualCrc != loc.FrameCrc32c)
                {
                    if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        buffer.Dispose();
                        return ReplicaWriteOutcome.Dropped.Instance;
                    }

                    throw Corrupt(slot, "failed its CLOE frame CRC-32C check");
                }
            }

            // OL-I7: a read at or above the bound reads dropped after the batch.
            if (slot.Value >= bound && await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                buffer.Dispose();
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            // OL-I11: only verified whole decided batches (strictly below the bound) enter the cache.
            if (range is null && slot.Value < bound)
            {
                CacheDecidedBatch(slot, buffer);
            }

            return new ReplicaRead(buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private async ValueTask<ReplicaRead> ReadSingleFrameSpanAsync(
        Slot slot,
        ExtentIndex extent,
        AppendSpan span,
        long bound,
        CancellationToken cancellationToken)
    {
        var spanLength = checked((int)(span.TrailerOffset - span.StartOffset));
        var buf = await ReadExactOrDroppedAsync(extent.Name, span.StartOffset, spanLength, cancellationToken)
            .ConfigureAwait(false);
        if (buf is null)
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        using var arc = buf.Value;
        var headerOutcome = DecodeFrameHeaderBuffer(arc);
        if (headerOutcome is not ObjectLedgerFrameHeader header)
        {
            var rejection = headerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            throw Corrupt(extent.Name, "has a corrupt frame header", rejection);
        }

        if (header.Kind == ObjectLedgerFrameKind.Seal ||
            header.Slot != span.FirstSlot ||
            ObjectLedgerExtentFormat.FrameHeaderLength + header.Length != spanLength)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            throw Corrupt(extent.Name, "has an inconsistent single-frame append");
        }

        if (header.Kind == ObjectLedgerFrameKind.Roll)
        {
            var expectedRollCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, header.Slot, []);
            if (header.Crc32c != expectedRollCrc || header.Slot <= extent.FirstSlot)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                throw Corrupt(extent.Name, "has an invalid ROLL frame");
            }

            _index.RecordRolledExtent(
                extent.FirstSlot.Value,
                extent.Name,
                header.Slot,
                span.TrailerOffset + ObjectLedgerExtentFormat.TrailerLength);

            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            return slot.Value < bound
                ? throw Corrupt(slot, "holds no batch below the bound which decided it")
                : ReplicaRead.Missing.Instance;
        }

        var payload = arc.Slice(ObjectLedgerExtentFormat.FrameHeaderLength, header.Length);
        try
        {
            var actualCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, slot, payload);
            if (actualCrc != header.Crc32c)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    payload.Dispose();
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                throw Corrupt(slot, "failed its CLOE frame CRC-32C check");
            }

            _index.RecordSingleSlot(
                extent,
                slot,
                new SlotLocation(
                    checked((int)(span.StartOffset + ObjectLedgerExtentFormat.FrameHeaderLength)),
                    header.Length,
                    header.Crc32c));

            if (slot.Value >= bound && await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                payload.Dispose();
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            // OL-I11: only verified decided batches (strictly below the bound) enter the cache.
            if (slot.Value < bound)
            {
                CacheDecidedBatch(slot, payload);
            }

            return new ReplicaRead(payload);
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    private async ValueTask<(bool Dropped, ObjectName? ExtentName, SlotLocation? Location, ReplicaRead? DirectPayload)> TryLocateSlotAsync(
        Slot slot,
        CancellationToken cancellationToken,
        bool allowSingleFrameRead = false,
        long bound = 0)
    {
        if (_index.TryGetCachedSlot(slot, out var cachedName, out var cachedLoc))
        {
            return (false, cachedName, cachedLoc, null);
        }

        if (_index.TryGetCachedAppendSpan(slot, out var spanExtent, out var span))
        {
            if (allowSingleFrameRead && span.Count == 1)
            {
                var outcome = await ReadSingleFrameSpanAsync(slot, spanExtent!, span, bound, cancellationToken)
                    .ConfigureAwait(false);
                return (outcome is ReplicaWriteOutcome.Dropped, null, null, outcome);
            }

            var dropped = await IndexAppendSpanAsync(spanExtent!, span, cancellationToken).ConfigureAwait(false);
            if (dropped)
            {
                return (true, null, null, null);
            }

            if (_index.TryGetCachedSlot(slot, out cachedName, out cachedLoc))
            {
                return (false, cachedName, cachedLoc, null);
            }
        }

        var extent = await FindExtentContainingAsync(slot, cancellationToken).ConfigureAwait(false);
        if (extent is null)
        {
            return (false, null, null, null);
        }

        var walk = await WalkExtentTrailersAsync(extent, cancellationToken).ConfigureAwait(false);
        if (walk.Dropped)
        {
            return (true, null, null, null);
        }

        if (!_index.TryGetCachedAppendSpan(extent, slot, out span))
        {
            if (walk.JoinedExisting && !_index.GetWalkState(extent).IsFinalized)
            {
                // Reader joined an in-flight walk whose GetPropertiesAsync snapshot may predate this call (OL-I5 / LL-I6).
                // Re-walk once so this reader observes a snapshot taken after its call began.
                var freshWalk = await WalkExtentTrailersAsync(extent, cancellationToken).ConfigureAwait(false);
                if (freshWalk.Dropped)
                {
                    return (true, null, null, null);
                }
            }

            if (!_index.TryGetCachedAppendSpan(extent, slot, out span))
            {
                return (false, null, null, null);
            }
        }

        if (allowSingleFrameRead && span.Count == 1)
        {
            var outcome = await ReadSingleFrameSpanAsync(slot, extent, span, bound, cancellationToken)
                .ConfigureAwait(false);
            return (outcome is ReplicaWriteOutcome.Dropped, null, null, outcome);
        }

        if (await IndexAppendSpanAsync(extent, span, cancellationToken).ConfigureAwait(false))
        {
            return (true, null, null, null);
        }

        if (_index.TryGetCachedSlot(slot, out cachedName, out cachedLoc))
        {
            return (false, cachedName, cachedLoc, null);
        }

        return (false, null, null, null);
    }

    private ValueTask<(bool Dropped, bool JoinedExisting)> WalkExtentTrailersAsync(
        ExtentIndex extent,
        CancellationToken cancellationToken)
    {
        var (walkTask, owner, joinedExisting) = _index.TryJoinOrClaimWalk(extent);
        if (owner is not null)
        {
            var detachedScope = AcquireDetachedScope();
            _ = RunOwnedExtentTrailerWalkAsync(extent, owner, detachedScope);
        }

        return WaitForWalkAsync(walkTask, joinedExisting, cancellationToken);
    }

    private static async ValueTask<(bool Dropped, bool JoinedExisting)> WaitForWalkAsync(
        Task<bool> walkTask,
        bool joinedExisting,
        CancellationToken cancellationToken)
    {
        var dropped = walkTask.IsCompletedSuccessfully
            ? walkTask.Result
            : await walkTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (dropped, joinedExisting);
    }

    private async Task RunOwnedExtentTrailerWalkAsync(
        ExtentIndex extent,
        TaskCompletionSource<bool> owner,
        CallScope detachedScope)
    {
        using (detachedScope)
        {
            try
            {
                var dropped = await WalkExtentTrailersCoreAsync(extent, _writes.Token).ConfigureAwait(false);
                _index.ClearActiveWalk(extent, owner.Task);
                _ = owner.TrySetResult(dropped);
            }
            catch (OperationCanceledException oce)
            {
                _index.ClearActiveWalk(extent, owner.Task);
                _ = owner.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                _index.ClearActiveWalk(extent, owner.Task);
                _ = owner.TrySetException(ex);
                _ = owner.Task.Exception;
            }
        }
    }

    private async Task<bool> WalkExtentTrailersCoreAsync(ExtentIndex extent, CancellationToken cancellationToken)
    {
        var (alreadyWalked, isFinalized) = _index.GetWalkState(extent);
        if (isFinalized)
        {
            return false;
        }

        var props = await _objects.GetPropertiesAsync(extent.Name, cancellationToken).ConfigureAwait(false);
        if (props is null)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (alreadyWalked > 0)
            {
                throw Corrupt(extent.Name, "disappeared without a dropped marker");
            }

            return false;
        }

        if (props.Length == 0)
        {
            if (alreadyWalked > 0)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                throw Corrupt(extent.Name, "shrank to zero length without a dropped marker");
            }

            return false;
        }

        if (props.Length == ObjectLedgerExtentFormat.TombstoneLength)
        {
            var tombstoneBuf = await ReadExactOrDroppedAsync(extent.Name, 0, ObjectLedgerExtentFormat.TombstoneLength, cancellationToken)
                .ConfigureAwait(false);
            if (tombstoneBuf is null)
            {
                return true;
            }

            using var tomb = tombstoneBuf.Value;
            Span<byte> tombBytes = stackalloc byte[ObjectLedgerExtentFormat.TombstoneLength];
            tomb.AsReadOnlySequence().CopyTo(tombBytes);
            if (ObjectLedgerExtentFormat.DecodeTombstone(tombBytes) is Slot)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                throw Corrupt(extent.Name, "contains a tombstone without a dropped marker");
            }

            throw Corrupt(extent.Name, "is shorter than a CLOE append");
        }

        if (props.Length < ObjectLedgerExtentFormat.MinAppendLength)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            throw Corrupt(extent.Name, "is shorter than a CLOE append");
        }

        if (props.Length < alreadyWalked)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            throw Corrupt(extent.Name, "shrank below its walked offset");
        }

        if (props.Length == alreadyWalked)
        {
            return false;
        }

        // Walk trailers backwards in 512 KiB tail chunks so cold extents need ~1-4 GETs instead of 1 per append.
        var newlyWalked = new List<AppendSpan>();
        Slot? rollNext = null;
        var end = props.Length;
        var hasChunk = false;
        ArcBuffer chunk = default;
        ReadOnlySequence<byte> chunkSeq = default;
        long chunkStart = 0;
        long chunkEnd = 0;

        try
        {
            while (end > alreadyWalked)
            {
                var trailerOffset = end - ObjectLedgerExtentFormat.TrailerLength;
                if (trailerOffset < alreadyWalked + ObjectLedgerExtentFormat.FrameHeaderLength)
                {
                    if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    throw Corrupt(extent.Name, "has a truncated trailer");
                }

                if (!hasChunk || trailerOffset < chunkStart || end > chunkEnd)
                {
                    if (hasChunk)
                    {
                        chunk.Dispose();
                        chunk = default;
                        chunkSeq = default;
                        hasChunk = false;
                    }

                    chunkEnd = end;
                    chunkStart = Math.Max(alreadyWalked, chunkEnd - TrailerWalkChunkBytes);
                    var fetchLength = (int)(chunkEnd - chunkStart);
                    var fetched = await ReadExactOrDroppedAsync(extent.Name, chunkStart, fetchLength, cancellationToken)
                        .ConfigureAwait(false);
                    if (fetched is null)
                    {
                        return true;
                    }

                    chunk = fetched.Value;
                    chunkSeq = chunk.AsReadOnlySequence();
                    hasChunk = true;
                }

                var trailerOutcome = DecodeTrailerSlice(
                    chunkSeq.Slice(trailerOffset - chunkStart, ObjectLedgerExtentFormat.TrailerLength));
                if (trailerOutcome is not ObjectLedgerTrailer trailer)
                {
                    var rejection = trailerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
                    if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    throw Corrupt(extent.Name, "has a corrupt trailer", rejection);
                }

                if (trailer.StartOffset < alreadyWalked ||
                    trailer.StartOffset >= trailerOffset ||
                    trailer.FirstSlot < extent.FirstSlot)
                {
                    if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    throw Corrupt(extent.Name, "has a trailer whose start offset or first slot is out of range");
                }

                var isRollOnlyCandidate =
                    end == props.Length &&
                    trailer.Count == 1 &&
                    trailerOffset - trailer.StartOffset == ObjectLedgerExtentFormat.FrameHeaderLength;

                if (isRollOnlyCandidate)
                {
                    // Inspect the 17-byte ROLL header directly from the chunk (or fetch if chunkStart == trailerOffset).
                    ObjectLedgerFrameHeaderDecoding hdrOutcome;
                    if (trailer.StartOffset >= chunkStart)
                    {
                        hdrOutcome = DecodeFrameHeaderSlice(
                            chunkSeq.Slice(trailer.StartOffset - chunkStart, ObjectLedgerExtentFormat.FrameHeaderLength));
                    }
                    else
                    {
                        var hdrBuf = await ReadExactOrDroppedAsync(
                            extent.Name,
                            trailer.StartOffset,
                            ObjectLedgerExtentFormat.FrameHeaderLength,
                            cancellationToken).ConfigureAwait(false);
                        if (hdrBuf is null)
                        {
                            return true;
                        }

                        using var hdr = hdrBuf.Value;
                        hdrOutcome = DecodeFrameHeaderBuffer(hdr);
                    }

                    if (hdrOutcome is not ObjectLedgerFrameHeader header ||
                        header.Kind != ObjectLedgerFrameKind.Roll ||
                        header.Slot != trailer.FirstSlot ||
                        header.Slot <= extent.FirstSlot ||
                        header.Crc32c != ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, header.Slot, []))
                    {
                        if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return true;
                        }

                        throw Corrupt(extent.Name, "has an invalid trailing ROLL append");
                    }

                    // Do not insert a ROLL-only append into extent.Appends (collides with successor extent's FirstSlot).
                    rollNext = header.Slot;
                }
                else
                {
                    newlyWalked.Add(new AppendSpan(trailer.FirstSlot, trailer.Count, trailer.StartOffset, trailerOffset));
                }

                end = trailer.StartOffset;
            }
        }
        finally
        {
            if (hasChunk)
            {
                chunk.Dispose();
            }
        }

        newlyWalked.Reverse();
        _index.RecordWalkedAppends(extent, newlyWalked, props.Length, rollNext);
        return false;
    }

    private async ValueTask<bool> IndexAppendSpanAsync(
        ExtentIndex extent,
        AppendSpan span,
        CancellationToken cancellationToken)
    {
        var framesBytes = span.TrailerOffset - span.StartOffset;
        var maxPossibleFrames = (int)Math.Min(int.MaxValue, Math.Max(1L, framesBytes / ObjectLedgerExtentFormat.FrameHeaderLength));
        var parsed = new List<(long Slot, SlotLocation Location)>(Math.Min(span.Count, maxPossibleFrames));
        Slot? rollNext = null;

        if (framesBytes <= SingleReadSpanThresholdBytes)
        {
            var spanBuf = await ReadExactOrDroppedAsync(extent.Name, span.StartOffset, (int)framesBytes, cancellationToken)
                .ConfigureAwait(false);
            if (spanBuf is null)
            {
                return true;
            }

            using var arc = spanBuf.Value;
            var seq = arc.AsReadOnlySequence();
            var cursor = span.StartOffset;
            for (var i = 0; i < span.Count; i++)
            {
                if (cursor + ObjectLedgerExtentFormat.FrameHeaderLength > span.TrailerOffset)
                {
                    if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    throw Corrupt(extent.Name, "has a truncated append");
                }

                var hdrOutcome = DecodeFrameHeaderSlice(
                    seq.Slice(cursor - span.StartOffset, ObjectLedgerExtentFormat.FrameHeaderLength));
                var step = await ProcessIndexedFrameHeaderAsync(
                    extent,
                    span,
                    i,
                    cursor,
                    hdrOutcome,
                    parsed,
                    cancellationToken).ConfigureAwait(false);

                if (step.Dropped)
                {
                    return true;
                }

                if (step.RollNext is not null)
                {
                    rollNext = step.RollNext;
                }

                cursor = step.NextCursor;
            }

            if (cursor != span.TrailerOffset)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                throw Corrupt(extent.Name, "has trailing bytes before its append trailer");
            }
        }
        else
        {
            var cursor = span.StartOffset;
            for (var i = 0; i < span.Count; i++)
            {
                if (cursor + ObjectLedgerExtentFormat.FrameHeaderLength > span.TrailerOffset)
                {
                    if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    throw Corrupt(extent.Name, "has a truncated append");
                }

                var hdrBuf = await ReadExactOrDroppedAsync(
                    extent.Name,
                    cursor,
                    ObjectLedgerExtentFormat.FrameHeaderLength,
                    cancellationToken).ConfigureAwait(false);
                if (hdrBuf is null)
                {
                    return true;
                }

                using var hdr = hdrBuf.Value;
                var hdrOutcome = DecodeFrameHeaderBuffer(hdr);
                var step = await ProcessIndexedFrameHeaderAsync(
                    extent,
                    span,
                    i,
                    cursor,
                    hdrOutcome,
                    parsed,
                    cancellationToken).ConfigureAwait(false);

                if (step.Dropped)
                {
                    return true;
                }

                if (step.RollNext is not null)
                {
                    rollNext = step.RollNext;
                }

                cursor = step.NextCursor;
            }

            if (cursor != span.TrailerOffset)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                throw Corrupt(extent.Name, "has trailing bytes before its append trailer");
            }
        }

        _index.RecordIndexedSlots(extent, parsed, rollNext);
        return false;
    }

    private async ValueTask<(bool Dropped, Slot? RollNext, long NextCursor)> ProcessIndexedFrameHeaderAsync(
        ExtentIndex extent,
        AppendSpan span,
        int frameIndex,
        long cursor,
        ObjectLedgerFrameHeaderDecoding headerOutcome,
        List<(long Slot, SlotLocation Location)> parsed,
        CancellationToken cancellationToken)
    {
        if (headerOutcome is not ObjectLedgerFrameHeader header)
        {
            var rejection = headerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, cursor);
            }

            throw Corrupt(extent.Name, "has a corrupt frame header", rejection);
        }

        var expectedSlot = new Slot(span.FirstSlot.Value + frameIndex);
        if (header.Kind == ObjectLedgerFrameKind.Seal || header.Slot != expectedSlot)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, cursor);
            }

            throw Corrupt(extent.Name, "has an unexpected frame kind or slot in an indexed append");
        }

        var nextCursor = cursor + ObjectLedgerExtentFormat.FrameHeaderLength + header.Length;
        if (nextCursor > span.TrailerOffset)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, cursor);
            }

            throw Corrupt(extent.Name, "has a frame extending past its trailer");
        }

        if (header.Kind == ObjectLedgerFrameKind.Roll)
        {
            var expectedCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, header.Slot, []);
            if (frameIndex != span.Count - 1 ||
                nextCursor != span.TrailerOffset ||
                header.Crc32c != expectedCrc ||
                header.Slot <= extent.FirstSlot)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, null, cursor);
                }

                throw Corrupt(extent.Name, "has an invalid ROLL frame in an indexed append");
            }

            return (false, header.Slot, nextCursor);
        }

        parsed.Add((
            header.Slot.Value,
            new SlotLocation(
                checked((int)(cursor + ObjectLedgerExtentFormat.FrameHeaderLength)),
                header.Length,
                header.Crc32c)));

        return (false, null, nextCursor);
    }

    private async IAsyncEnumerable<ReplicaRead> ReadRangeCoreAsync(
        Slot from,
        Slot below,
        SemaphoreSlim? window,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var scope = AdmitRead();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_reads.Token, cancellationToken);
        var linkedToken = linked.Token;

        var bound = Volatile.Read(ref _durableBelow);
        List<ExtentListing> listings;
        try
        {
            listings = await ListExtentsAscendingAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }

        var nextSlot = from;
        for (var extentIndex = 0; extentIndex < listings.Count && nextSlot < below; extentIndex++)
        {
            var (extent, listedLength) = listings[extentIndex];
            if (extent.FirstSlot > nextSlot)
            {
                // Gap before nextSlot: missing.
                break;
            }

            // Skip extents strictly earlier than the extent that covers nextSlot.
            if (extentIndex + 1 < listings.Count && listings[extentIndex + 1].Extent.FirstSlot <= nextSlot)
            {
                continue;
            }

            var (alreadyWalked, isFinalized) = _index.GetWalkState(extent);
            var prep = await PrepareExtentRangeScanAsync(
                extent,
                listedLength,
                alreadyWalked,
                isFinalized,
                nextSlot,
                below,
                linkedToken,
                cancellationToken).ConfigureAwait(false);

            if (prep.Dropped)
            {
                yield return ReplicaWriteOutcome.Dropped.Instance;
                yield break;
            }

            if (prep.SkipExtent)
            {
                continue;
            }

            using var reader = new ExtentChunkReader(_objects, extent.Name, prep.StartOffset, prep.EndOffset, window);
            var state = new ExtentScanState(
                extent,
                reader,
                nextSlot,
                below,
                prep.ExpectedAppendStartSlot,
                prep.StartOffset,
                bound);

            while (state.NextSlot < below)
            {
                var step = await ReadNextSlotFromExtentAsync(state, linkedToken, cancellationToken)
                    .ConfigureAwait(false);

                var finishedExtent = false;
                switch (step.Kind)
                {
                    case ExtentScanStepKind.Data:
                        yield return new ReplicaRead(step.Payload);
                        break;

                    case ExtentScanStepKind.Dropped:
                        yield return ReplicaWriteOutcome.Dropped.Instance;
                        yield break;

                    case ExtentScanStepKind.EndOfExtent:
                        finishedExtent = true;
                        break;

                    default:
                        throw new UnreachableException();
                }

                if (finishedExtent)
                {
                    break;
                }
            }

            if (state.AppendFrameCount > 0 && reader.Remaining == 0)
            {
                if (await IsDroppedSafeAsync(linkedToken, cancellationToken).ConfigureAwait(false))
                {
                    yield return ReplicaWriteOutcome.Dropped.Instance;
                    yield break;
                }

                throw Corrupt(extent.Name, "ended with a truncated append missing its trailer");
            }

            FlushPendingScannedSpans(state);
            nextSlot = state.NextSlot;
        }

        if (nextSlot < below)
        {
            if (await IsDroppedSafeAsync(linkedToken, cancellationToken).ConfigureAwait(false))
            {
                yield return ReplicaWriteOutcome.Dropped.Instance;
                yield break;
            }

            if (nextSlot.Value < bound)
            {
                throw Corrupt(nextSlot, "holds no batch below the bound which decided it");
            }

            yield return ReplicaRead.Missing.Instance;
        }
    }

    private async ValueTask<(bool Dropped, bool SkipExtent, long StartOffset, long EndOffset, Slot ExpectedAppendStartSlot)> PrepareExtentRangeScanAsync(
        ExtentIndex extent,
        long listedLength,
        long alreadyWalked,
        bool isFinalized,
        Slot nextSlot,
        Slot below,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            if (listedLength == ObjectLedgerExtentFormat.TombstoneLength)
            {
                if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                {
                    return (Dropped: true, SkipExtent: false, 0, 0, default);
                }

                throw Corrupt(extent.Name, "contains a tombstone without a dropped marker");
            }

            if (listedLength < alreadyWalked)
            {
                // A concurrent write may have raised WalkedUpToOffset after ListExtentsAscendingAsync took its snapshot.
                // Confirm against fresh properties before diagnosing a shrink.
                var fresh = await _objects.GetPropertiesAsync(extent.Name, linkedToken).ConfigureAwait(false);
                if (fresh is null)
                {
                    if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                    {
                        return (Dropped: true, SkipExtent: false, 0, 0, default);
                    }

                    throw Corrupt(extent.Name, "disappeared without a dropped marker");
                }

                if (fresh.Length == ObjectLedgerExtentFormat.TombstoneLength)
                {
                    if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                    {
                        return (Dropped: true, SkipExtent: false, 0, 0, default);
                    }

                    throw Corrupt(extent.Name, "contains a tombstone without a dropped marker");
                }

                if (fresh.Length < alreadyWalked)
                {
                    if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                    {
                        return (Dropped: true, SkipExtent: false, 0, 0, default);
                    }

                    throw Corrupt(extent.Name, "shrank below its walked offset");
                }

                listedLength = fresh.Length;
            }

            var startOffset = 0L;
            var expectedAppendStartSlot = extent.FirstSlot;
            var extentLength = isFinalized ? Math.Max(listedLength, alreadyWalked) : listedLength;

            if (nextSlot > extent.FirstSlot)
            {
                if (_index.TryGetCachedAppendSpan(extent, nextSlot, out var span))
                {
                    startOffset = span.StartOffset;
                    expectedAppendStartSlot = span.FirstSlot;
                }
                else if (extentLength > ReadRangeChunkBytes)
                {
                    var walk = await WalkExtentTrailersAsync(extent, linkedToken).ConfigureAwait(false);
                    if (walk.Dropped)
                    {
                        return (Dropped: true, SkipExtent: false, 0, 0, default);
                    }

                    var (walkedAfter, finalizedAfter) = _index.GetWalkState(extent);
                    if (walk.JoinedExisting && !finalizedAfter)
                    {
                        var freshWalk = await WalkExtentTrailersAsync(extent, linkedToken).ConfigureAwait(false);
                        if (freshWalk.Dropped)
                        {
                            return (Dropped: true, SkipExtent: false, 0, 0, default);
                        }

                        (walkedAfter, _) = _index.GetWalkState(extent);
                    }

                    alreadyWalked = walkedAfter;
                    extentLength = Math.Max(extentLength, alreadyWalked);
                    if (!_index.TryGetCachedAppendSpan(extent, nextSlot, out span))
                    {
                        return (Dropped: false, SkipExtent: true, 0, 0, default);
                    }

                    startOffset = span.StartOffset;
                    expectedAppendStartSlot = span.FirstSlot;
                }
                // For unwalked extents <= ReadRangeChunkBytes (8 MiB), skip WalkExtentTrailersAsync and scan
                // from offset 0 in one chunk read, skipping pre-nextSlot frames in memory.
            }

            if (extentLength == 0)
            {
                if (alreadyWalked > 0)
                {
                    if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                    {
                        return (Dropped: true, SkipExtent: false, 0, 0, default);
                    }

                    throw Corrupt(extent.Name, "shrank to zero length without a dropped marker");
                }

                return (Dropped: false, SkipExtent: true, 0, 0, default);
            }

            if (extentLength < startOffset)
            {
                if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                {
                    return (Dropped: true, SkipExtent: false, 0, 0, default);
                }

                throw Corrupt(extent.Name, "shrank below its indexed start offset");
            }

            // Clamp endOffset when below - 1's append end is cached inside this same extent.
            var endOffset = extentLength;
            if (below > nextSlot &&
                _index.TryGetCachedSpanEndOffset(extent, new Slot(below.Value - 1), out var cachedEndOffset) &&
                cachedEndOffset >= startOffset &&
                cachedEndOffset < endOffset)
            {
                endOffset = cachedEndOffset;
            }

            if (startOffset >= endOffset)
            {
                return (Dropped: false, SkipExtent: true, 0, 0, default);
            }

            return (Dropped: false, SkipExtent: false, startOffset, endOffset, expectedAppendStartSlot);
        }
        catch (OperationCanceledException) when (ReadsStopped(callerToken))
        {
            throw ReadsStoppedException();
        }
    }

    private async ValueTask<ExtentScanStep> ReadNextSlotFromExtentAsync(
        ExtentScanState state,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            var reader = state.Reader;
            while (state.NextSlot < state.Below && reader.Remaining > 0)
            {
                var lead = await reader.PeekLeadByteAsync(linkedToken).ConfigureAwait(false);
                if (lead is null)
                {
                    return await HandleUnexpectedScanEndAsync(state, linkedToken).ConfigureAwait(false);
                }

                if (lead.Value == ObjectLedgerExtentFormat.Magic[0])
                {
                    var trailerOffset = reader.Position;
                    if (!await reader.ReadBytesAsync(state.TrailerBytes, linkedToken).ConfigureAwait(false))
                    {
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "ended with a truncated trailer",
                            linkedToken).ConfigureAwait(false);
                    }

                    var trailerOutcome = ObjectLedgerExtentFormat.DecodeTrailer(state.TrailerBytes);
                    if (trailerOutcome is not ObjectLedgerTrailer trailer)
                    {
                        var rejection = trailerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "contains a corrupt trailer",
                            linkedToken,
                            rejection).ConfigureAwait(false);
                    }

                    if (trailer.StartOffset != state.AppendStartOffset ||
                        trailer.Count != state.AppendFrameCount ||
                        trailer.FirstSlot != state.AppendFirstSlot)
                    {
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "has a trailer that does not match its preceding frames",
                            linkedToken).ConfigureAwait(false);
                    }

                    var appendEndOffset = trailerOffset + ObjectLedgerExtentFormat.TrailerLength;
                    state.PendingSpans.Add(new AppendSpan(trailer.FirstSlot, trailer.Count, state.AppendStartOffset, trailerOffset));
                    state.PendingWalkedUpTo = appendEndOffset;
                    if (reader.ChunkGeneration != state.LastFlushedGeneration)
                    {
                        FlushPendingScannedSpans(state);
                    }

                    state.AppendStartOffset = appendEndOffset;
                    state.AppendFirstSlot = new Slot(trailer.FirstSlot.Value + trailer.Count);
                    state.AppendFrameCount = 0;
                    continue;
                }

                if (!await reader.ReadBytesAsync(state.HeaderBytes, linkedToken).ConfigureAwait(false))
                {
                    return await DroppedOrCorruptStepAsync(
                        state.Extent.Name,
                        "ended with a truncated frame header",
                        linkedToken).ConfigureAwait(false);
                }

                var headerOutcome = ObjectLedgerExtentFormat.DecodeFrameHeader(state.HeaderBytes);
                if (headerOutcome is not ObjectLedgerFrameHeader header)
                {
                    var rejection = headerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
                    return await DroppedOrCorruptStepAsync(
                        state.Extent.Name,
                        "contains a corrupt frame header",
                        linkedToken,
                        rejection).ConfigureAwait(false);
                }

                if (header.Kind == ObjectLedgerFrameKind.Seal)
                {
                    return await DroppedOrCorruptStepAsync(
                        state.Extent.Name,
                        "contains a SEAL frame inside an extent",
                        linkedToken).ConfigureAwait(false);
                }

                if (header.Kind == ObjectLedgerFrameKind.Roll)
                {
                    var expectedRollCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, header.Slot, []);
                    if (header.Crc32c != expectedRollCrc ||
                        header.Slot != new Slot(state.AppendFirstSlot.Value + state.AppendFrameCount) ||
                        header.Slot <= state.Extent.FirstSlot)
                    {
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "contains a corrupt ROLL frame",
                            linkedToken).ConfigureAwait(false);
                    }

                    var rollTrailerOffset = reader.Position;
                    if (!await reader.ReadBytesAsync(state.TrailerBytes, linkedToken).ConfigureAwait(false))
                    {
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "ended with a truncated ROLL trailer",
                            linkedToken).ConfigureAwait(false);
                    }

                    var rollTrailerOutcome = ObjectLedgerExtentFormat.DecodeTrailer(state.TrailerBytes);
                    if (rollTrailerOutcome is not ObjectLedgerTrailer rollTrailer ||
                        rollTrailer.StartOffset != state.AppendStartOffset ||
                        rollTrailer.Count != state.AppendFrameCount + 1 ||
                        rollTrailer.FirstSlot != state.AppendFirstSlot)
                    {
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "has an invalid ROLL trailer",
                            linkedToken).ConfigureAwait(false);
                    }

                    var rollEndOffset = rollTrailerOffset + ObjectLedgerExtentFormat.TrailerLength;
                    if (state.AppendFrameCount > 0)
                    {
                        state.PendingSpans.Add(new AppendSpan(
                            rollTrailer.FirstSlot,
                            rollTrailer.Count,
                            state.AppendStartOffset,
                            rollTrailerOffset));
                    }

                    state.PendingWalkedUpTo = rollEndOffset;
                    state.PendingRollNext = header.Slot;
                    state.AppendFrameCount = 0;
                    return new ExtentScanStep(ExtentScanStepKind.EndOfExtent, default);
                }

                var expectedFrameSlot = new Slot(state.AppendFirstSlot.Value + state.AppendFrameCount);
                if (header.Slot != expectedFrameSlot)
                {
                    return await DroppedOrCorruptStepAsync(
                        state.Extent.Name,
                        $"has out-of-order slot {header.Slot.Value} (expected {expectedFrameSlot.Value})",
                        linkedToken).ConfigureAwait(false);
                }

                state.AppendFrameCount += 1;

                // Skip frames below nextSlot without slicing or CRCing discarded payloads.
                if (header.Slot < state.NextSlot)
                {
                    if (!await reader.SkipBytesAsync(header.Length, linkedToken).ConfigureAwait(false))
                    {
                        return await DroppedOrCorruptStepAsync(
                            state.Extent.Name,
                            "ended with a truncated frame payload",
                            linkedToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var payload = await reader.ReadBufferAsync(header.Length, linkedToken).ConfigureAwait(false);
                if (payload is null)
                {
                    return await DroppedOrCorruptStepAsync(
                        state.Extent.Name,
                        "ended with a truncated frame payload",
                        linkedToken).ConfigureAwait(false);
                }

                var arc = payload.Value;
                try
                {
                    var actualCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Data, header.Slot, arc);
                    if (actualCrc != header.Crc32c)
                    {
                        arc.Dispose();
                        arc = default;
                        return await DroppedOrCorruptStepAsync(
                            header.Slot,
                            "failed its CLOE frame CRC-32C check",
                            linkedToken).ConfigureAwait(false);
                    }

                    // OL-I7: check dropped at most once per fetched chunk when yielding slots >= bound.
                    if (header.Slot.Value >= state.Bound && state.DroppedCheckedGeneration != reader.ChunkGeneration)
                    {
                        if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
                        {
                            arc.Dispose();
                            arc = default;
                            return new ExtentScanStep(ExtentScanStepKind.Dropped, default);
                        }

                        state.DroppedCheckedGeneration = reader.ChunkGeneration;
                    }

                    state.NextSlot = new Slot(state.NextSlot.Value + 1);
                    return new ExtentScanStep(ExtentScanStepKind.Data, arc);
                }
                catch
                {
                    arc.Dispose();
                    throw;
                }
            }

            if (reader.Remaining == 0 && state.AppendFrameCount > 0)
            {
                return await DroppedOrCorruptStepAsync(
                    state.Extent.Name,
                    "ended in a truncated append without a trailer",
                    linkedToken).ConfigureAwait(false);
            }

            return new ExtentScanStep(ExtentScanStepKind.EndOfExtent, default);
        }
        catch (OperationCanceledException) when (ReadsStopped(callerToken))
        {
            throw ReadsStoppedException();
        }
    }

    private async ValueTask<ExtentScanStep> HandleUnexpectedScanEndAsync(
        ExtentScanState state,
        CancellationToken linkedToken)
    {
        if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
        {
            return new ExtentScanStep(ExtentScanStepKind.Dropped, default);
        }

        throw Corrupt(state.Extent.Name, "ended unexpectedly before its listed length");
    }

    private async ValueTask<ExtentScanStep> DroppedOrCorruptStepAsync(
        ObjectName extentName,
        string detail,
        CancellationToken linkedToken,
        InvalidDataException? inner = null)
    {
        if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
        {
            return new ExtentScanStep(ExtentScanStepKind.Dropped, default);
        }

        throw inner is null ? Corrupt(extentName, detail) : Corrupt(extentName, detail, inner);
    }

    private async ValueTask<ExtentScanStep> DroppedOrCorruptStepAsync(
        Slot slot,
        string detail,
        CancellationToken linkedToken)
    {
        if (await IsDroppedAsync(linkedToken).ConfigureAwait(false))
        {
            return new ExtentScanStep(ExtentScanStepKind.Dropped, default);
        }

        throw Corrupt(slot, detail);
    }

    private void FlushPendingScannedSpans(ExtentScanState state)
    {
        if (state.PendingSpans.Count == 0 && state.PendingRollNext is null)
        {
            return;
        }

        _index.RecordWalkedAppends(state.Extent, state.PendingSpans, state.PendingWalkedUpTo, state.PendingRollNext);
        state.PendingSpans.Clear();
        state.LastFlushedGeneration = state.Reader.ChunkGeneration;
    }

    private async ValueTask<List<ExtentListing>> ListExtentsAscendingAsync(CancellationToken cancellationToken)
    {
        // ObjectLedgerNames.Extents uses reverse keys so ListAsync returns extents newest first (descending firstSlot).
        var listings = new List<ExtentListing>();
        await foreach (var item in _objects.ListAsync(_extentsPrefix, startAfter: null, cancellationToken).ConfigureAwait(false))
        {
            var firstSlot = ObjectLedgerNames.ParseExtent(_extentsPrefix, item.Name)
                ?? throw Corrupt(item.Name, "is not a valid extent name");
            var extent = _index.GetOrAddExtent(firstSlot, item.Name);
            listings.Add(new ExtentListing(extent, item.Length));
        }

        listings.Reverse();
        return listings;
    }

    private async ValueTask<ExtentIndex?> FindNewestExtentAsync(CancellationToken cancellationToken)
    {
        // Reverse-key order: the first listed extent has the largest firstSlot.
        await foreach (var item in _objects.ListAsync(_extentsPrefix, startAfter: null, cancellationToken).ConfigureAwait(false))
        {
            var firstSlot = ObjectLedgerNames.ParseExtent(_extentsPrefix, item.Name)
                ?? throw Corrupt(item.Name, "is not a valid extent name");
            return _index.GetOrAddExtent(firstSlot, item.Name);
        }

        return null;
    }

    private async ValueTask<ExtentIndex?> FindExtentContainingAsync(Slot slot, CancellationToken cancellationToken)
    {
        // Reverse-key order (descending firstSlot): the first extent with firstSlot <= slot covers slot.
        await foreach (var item in _objects.ListAsync(_extentsPrefix, startAfter: null, cancellationToken).ConfigureAwait(false))
        {
            var firstSlot = ObjectLedgerNames.ParseExtent(_extentsPrefix, item.Name)
                ?? throw Corrupt(item.Name, "is not a valid extent name");
            if (firstSlot <= slot)
            {
                return _index.GetOrAddExtent(firstSlot, item.Name);
            }
        }

        return null;
    }
}
