using System;

namespace Orleans.Serialization.Buffers.Adaptors;
using static Orleans.Serialization.Buffers.Adaptors.PooledBuffer;

public struct BufferSliceReaderInput
{
    private static readonly SequenceSegment InitialSegmentSentinel = new();
    private static readonly SequenceSegment FinalSegmentSentinel = new();
    private readonly BufferSlice _slice;
    private SequenceSegment _segment;

    public BufferSliceReaderInput(in BufferSlice slice)
    {
        _slice = slice;
        _segment = InitialSegmentSentinel;
    }

    internal readonly PooledBuffer Writer => _slice._buffer;
    internal int Position { get; private set; }
    internal readonly int Offset => _slice._offset;
    internal readonly int Length => _slice._length;

    public ReadOnlySpan<byte> GetNext()
    {
        if (ReferenceEquals(_segment, InitialSegmentSentinel))
        {
            _segment = _slice._buffer._first;
        }

        var endPosition = Offset + Length;
        while (_segment != null && _segment != FinalSegmentSentinel)
        {
            var segment = _segment.CommittedMemory.Span;

            // Find the starting segment and the offset to copy from.
            int segmentOffset;
            if (Position < Offset)
            {
                if (Position + segment.Length <= Offset)
                {
                    // Start is in a subsequent segment
                    Position += segment.Length;
                    _segment = _segment.Next as SequenceSegment;
                    continue;
                }
                else
                {
                    // Start is in this segment
                    segmentOffset = Offset - Position;
                }
            }
            else
            {
                segmentOffset = 0;
            }

            var result = segment[segmentOffset..Math.Min(segment.Length - segmentOffset, endPosition - (Position + segmentOffset))];
            Position += result.Length;
            _segment = _segment.Next as SequenceSegment;
            return result;
        }

        if (_segment != FinalSegmentSentinel && Writer._currentPosition > 0 && Writer._writeHead is { } head && Position < endPosition)
        {
            var offset = Math.Max(Offset - Position, 0);
            var result = head.Array.AsSpan(offset, Math.Min(Writer._currentPosition, endPosition - (Position + offset)));
            _segment = FinalSegmentSentinel;
            Position = endPosition;
            return result;
        }

        return ReadOnlySpan<byte>.Empty;
    }
}
