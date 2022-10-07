using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers.Adaptors;
using static Orleans.Serialization.Buffers.PooledBuffer;

public struct BufferSliceReaderInput
{
    internal static readonly SequenceSegment InitialSegmentSentinel = new();
    internal static readonly SequenceSegment FinalSegmentSentinel = new();
    internal BufferSlice _slice;
    internal SequenceSegment _segment;
    internal int _position;
    internal long VisitedBuffersLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferSliceReaderInput(BufferSlice slice)
    {
        _slice = slice;
        _segment = slice._buffer._first;
    }

    internal readonly int Position => _position;
    internal readonly int Offset => _slice._offset;
    internal readonly int Length => _slice._length;

    public BufferSliceReaderInput ForkFrom(int position)
    {
        var sliced = _slice.Slice(position);
        return new BufferSliceReaderInput(sliced);
    }
}
