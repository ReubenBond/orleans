using System.Buffers;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

internal sealed partial class AppendObjectLedgerStorage
{
    private async ValueTask<LedgerRecoveryOutcome> RecoverOnceAsync(CancellationToken cancellationToken)
    {
        if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        // Write sealed before sealing the newest extent so any concurrent sweep sees sealed
        // and any future reader can distinguish a sealed ledger (OL-I4, object-ledgers.md §8.2).
        _ = await _objects.CreateAsync(
            _sealed,
            default,
            crc32c: null,
            cancellationToken).ConfigureAwait(false);

        if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReplicaWriteOutcome.Dropped.Instance;
        }

        var newest = await FindNewestExtentAsync(cancellationToken).ConfigureAwait(false);
        var targetFirstSlot = newest?.FirstSlot ?? new Slot(0);

        while (true)
        {
            var extentName = ObjectLedgerNames.Extent(_ledger, targetFirstSlot);
            var sealedOutcome = await _appendable.SealAsync(extentName, cancellationToken).ConfigureAwait(false);

            if (sealedOutcome is ObjectSealOutcome.Sealed)
            {
                var inspection = await InspectSealedExtentEndAsync(extentName, targetFirstSlot, cancellationToken)
                    .ConfigureAwait(false);

                if (inspection.Dropped)
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                if (inspection.RollNextSlot is { } nextExtentSlot)
                {
                    if (nextExtentSlot <= targetFirstSlot)
                    {
                        if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return ReplicaWriteOutcome.Dropped.Instance;
                        }

                        throw Corrupt(extentName, "contains a non-advancing ROLL frame");
                    }

                    targetFirstSlot = nextExtentSlot;
                    continue;
                }

                var end = inspection.EndSlot;
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                MarkSealed();
                _ = Raise(end);
                return new LedgerRecovery(end, end);
            }

            if (sealedOutcome is ObjectSealOutcome.Absent)
            {
                // Create the empty appendable extent and immediately loop to seal it (OL-I3).
                var created = await _appendable.CreateAppendableAsync(extentName, cancellationToken).ConfigureAwait(false);
                if (created is ObjectCreateOutcome.Created or ObjectCreateOutcome.Exists)
                {
                    continue;
                }

                throw new UnreachableException();
            }

            if (sealedOutcome is ObjectSealOutcome.NotAppendable)
            {
                // Sweeper replaced the newest extent with a block-blob tombstone after writing dropped.
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                throw Corrupt(extentName, "is not appendable and no dropped marker exists");
            }

            throw new UnreachableException();
        }
    }

    private async ValueTask<(bool Dropped, Slot EndSlot, Slot? RollNextSlot)> InspectSealedExtentEndAsync(
        ObjectName extentName,
        Slot extentFirstSlot,
        CancellationToken cancellationToken)
    {
        var props = await _objects.GetPropertiesAsync(extentName, cancellationToken).ConfigureAwait(false);
        if (props is null)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, default, null);
            }

            throw Corrupt(extentName, "disappeared after sealing without a dropped marker");
        }

        if (props.Length == 0)
        {
            return (false, extentFirstSlot, null);
        }

        if (props.Length == ObjectLedgerExtentFormat.TombstoneLength)
        {
            var tombstoneBuf = await ReadExactOrDroppedAsync(extentName, 0, ObjectLedgerExtentFormat.TombstoneLength, cancellationToken)
                .ConfigureAwait(false);
            if (tombstoneBuf is null)
            {
                return (true, default, null);
            }

            using var tomb = tombstoneBuf.Value;
            Span<byte> tombBytes = stackalloc byte[ObjectLedgerExtentFormat.TombstoneLength];
            tomb.AsReadOnlySequence().CopyTo(tombBytes);
            if (ObjectLedgerExtentFormat.DecodeTombstone(tombBytes) is Slot)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, default, null);
                }

                throw Corrupt(extentName, "contains a tombstone without a dropped marker");
            }

            throw Corrupt(extentName, "is shorter than a CLOE append");
        }

        if (props.Length < ObjectLedgerExtentFormat.MinAppendLength)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, default, null);
            }

            throw Corrupt(extentName, "is shorter than a CLOE append");
        }

        // Fetch a tail chunk so the trailer and last append's frames are usually validated in one GET.
        var tailLength = (int)Math.Min(props.Length, TrailerWalkChunkBytes);
        var tailStart = props.Length - tailLength;
        var tailBuf = await ReadExactOrDroppedAsync(extentName, tailStart, tailLength, cancellationToken)
            .ConfigureAwait(false);
        if (tailBuf is null)
        {
            return (true, default, null);
        }

        using var tail = tailBuf.Value;
        var tailSeq = tail.AsReadOnlySequence();
        var trailerOffset = props.Length - ObjectLedgerExtentFormat.TrailerLength;
        var trailerOutcome = DecodeTrailerSlice(tailSeq.Slice(trailerOffset - tailStart, ObjectLedgerExtentFormat.TrailerLength));
        if (trailerOutcome is not ObjectLedgerTrailer trailer)
        {
            var rejection = trailerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, default, null);
            }

            throw Corrupt(extentName, "has a corrupt trailing trailer", rejection);
        }

        if (trailer.StartOffset < 0 ||
            trailer.StartOffset >= trailerOffset ||
            trailer.FirstSlot < extentFirstSlot)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, default, null);
            }

            throw Corrupt(extentName, "has a trailer whose start offset or first slot is out of range");
        }

        // Validate every frame header in the final append uniformly (Count == 1 and Count > 1).
        var framesBytes = trailerOffset - trailer.StartOffset;
        if (trailer.StartOffset >= tailStart)
        {
            return await ValidateSealedAppendFramesInMemoryAsync(
                extentName,
                extentFirstSlot,
                trailer,
                trailerOffset,
                tailSeq,
                tailStart,
                cancellationToken).ConfigureAwait(false);
        }

        if (framesBytes <= SingleReadSpanThresholdBytes)
        {
            var spanBuf = await ReadExactOrDroppedAsync(
                extentName,
                trailer.StartOffset,
                (int)framesBytes,
                cancellationToken).ConfigureAwait(false);
            if (spanBuf is null)
            {
                return (true, default, null);
            }

            using var spanArc = spanBuf.Value;
            return await ValidateSealedAppendFramesInMemoryAsync(
                extentName,
                extentFirstSlot,
                trailer,
                trailerOffset,
                spanArc.AsReadOnlySequence(),
                trailer.StartOffset,
                cancellationToken).ConfigureAwait(false);
        }

        // Large final append (> 1 MiB): read 17-byte frame headers directly without downloading payload bytes.
        var cursor = trailer.StartOffset;
        for (var i = 0; i < trailer.Count; i++)
        {
            if (cursor + ObjectLedgerExtentFormat.FrameHeaderLength > trailerOffset)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, default, null);
                }

                throw Corrupt(extentName, "has a truncated final append");
            }

            var hdrBuf = await ReadExactOrDroppedAsync(
                extentName,
                cursor,
                ObjectLedgerExtentFormat.FrameHeaderLength,
                cancellationToken).ConfigureAwait(false);
            if (hdrBuf is null)
            {
                return (true, default, null);
            }

            using var hdr = hdrBuf.Value;
            var hdrOutcome = DecodeFrameHeaderBuffer(hdr);
            var step = await ValidateSealedAppendFrameHeaderAsync(
                extentName,
                extentFirstSlot,
                trailer,
                trailerOffset,
                i,
                cursor,
                hdrOutcome,
                cancellationToken).ConfigureAwait(false);

            if (step.Dropped)
            {
                return (true, default, null);
            }

            if (step.RollNextSlot is { } rollNext)
            {
                return (false, rollNext, rollNext);
            }

            cursor = step.NextCursor;
        }

        if (cursor != trailerOffset)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, default, null);
            }

            throw Corrupt(extentName, "has trailing bytes between its final frame and trailer");
        }

        return (false, new Slot(trailer.FirstSlot.Value + trailer.Count), null);
    }

    private async ValueTask<(bool Dropped, Slot EndSlot, Slot? RollNextSlot)> ValidateSealedAppendFramesInMemoryAsync(
        ObjectName extentName,
        Slot extentFirstSlot,
        ObjectLedgerTrailer trailer,
        long trailerOffset,
        ReadOnlySequence<byte> buffer,
        long bufferBaseOffset,
        CancellationToken cancellationToken)
    {
        var cursor = trailer.StartOffset;
        for (var i = 0; i < trailer.Count; i++)
        {
            if (cursor + ObjectLedgerExtentFormat.FrameHeaderLength > trailerOffset)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, default, null);
                }

                throw Corrupt(extentName, "has a truncated final append");
            }

            var hdrOutcome = DecodeFrameHeaderSlice(buffer.Slice(cursor - bufferBaseOffset, ObjectLedgerExtentFormat.FrameHeaderLength));
            var step = await ValidateSealedAppendFrameHeaderAsync(
                extentName,
                extentFirstSlot,
                trailer,
                trailerOffset,
                i,
                cursor,
                hdrOutcome,
                cancellationToken).ConfigureAwait(false);

            if (step.Dropped)
            {
                return (true, default, null);
            }

            if (step.RollNextSlot is { } rollNext)
            {
                return (false, rollNext, rollNext);
            }

            cursor = step.NextCursor;
        }

        if (cursor != trailerOffset)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, default, null);
            }

            throw Corrupt(extentName, "has trailing bytes between its final frame and trailer");
        }

        return (false, new Slot(trailer.FirstSlot.Value + trailer.Count), null);
    }

    private async ValueTask<(bool Dropped, Slot? RollNextSlot, long NextCursor)> ValidateSealedAppendFrameHeaderAsync(
        ObjectName extentName,
        Slot extentFirstSlot,
        ObjectLedgerTrailer trailer,
        long trailerOffset,
        int frameIndex,
        long cursor,
        ObjectLedgerFrameHeaderDecoding headerOutcome,
        CancellationToken cancellationToken)
    {
        if (headerOutcome is not ObjectLedgerFrameHeader header)
        {
            var rejection = headerOutcome is InvalidDataException ex ? ex : throw new UnreachableException();
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, cursor);
            }

            throw Corrupt(extentName, "has a corrupt frame header in its final append", rejection);
        }

        var expectedSlot = new Slot(trailer.FirstSlot.Value + frameIndex);
        if (header.Kind == ObjectLedgerFrameKind.Seal || header.Slot != expectedSlot)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, cursor);
            }

            throw Corrupt(extentName, "has an unexpected frame kind or slot in its final append");
        }

        var nextCursor = cursor + ObjectLedgerExtentFormat.FrameHeaderLength + header.Length;
        if (nextCursor > trailerOffset)
        {
            if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, null, cursor);
            }

            throw Corrupt(extentName, "has a frame extending past its trailer");
        }

        if (header.Kind == ObjectLedgerFrameKind.Roll)
        {
            var expectedCrc = ObjectLedgerExtentFormat.ComputeFrameCrc32c(ObjectLedgerFrameKind.Roll, header.Slot, []);
            if (frameIndex != trailer.Count - 1 ||
                nextCursor != trailerOffset ||
                header.Crc32c != expectedCrc ||
                header.Slot <= extentFirstSlot)
            {
                if (await IsDroppedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, null, cursor);
                }

                throw Corrupt(extentName, "has an invalid ROLL frame in its final append");
            }

            return (false, header.Slot, nextCursor);
        }

        return (false, null, nextCursor);
    }
}
