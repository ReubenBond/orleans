using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Buffers;

/// <summary>
/// A <see cref="IBufferWriter{T}"/> implementation implemented using pooled arrays which is specialized for creating <see cref="ReadOnlySequence{T}"/> instances.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public partial struct PooledBuffer : IBufferWriter<byte>, IDisposable
{
    internal SequenceSegment _first;
    internal SequenceSegment _last;
    internal SequenceSegment _writeHead;
    internal int _totalLength;
    internal int _currentPosition;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledBuffer"/> struct.
    /// </summary>
    public PooledBuffer()
    {
        _first = _last = null;
        _writeHead = null;
        _totalLength = 0;
        _currentPosition = 0;
    }

    /// <summary>Gets the total length which has been written.</summary>
    public readonly int Length => _totalLength + _currentPosition;

    /// <summary>
    /// Returns the data which has been written as an array.
    /// </summary>
    /// <returns>The data which has been written.</returns>
    public readonly byte[] ToArray()
    {
        var result = new byte[Length];
        var resultSpan = result.AsSpan();
        var current = _first;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            span.CopyTo(resultSpan);
            resultSpan = resultSpan[span.Length..];
            current = current.Next as SequenceSegment;
        }

        if (_writeHead is not null && _currentPosition > 0)
        {
            _writeHead.Array.AsSpan(0, _currentPosition).CopyTo(resultSpan);
        }

        return result;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int bytes)
    {
        if (_writeHead is null || _currentPosition > _writeHead.Array.Length)
        {
            ThrowInvalidOperation();
        }

        _currentPosition += bytes;

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInvalidOperation() => throw new InvalidOperationException("Attempted to advance past the end of a buffer.");
    }

    public void Reset()
    {
        var current = _first;
        while (current != null)
        {
            var previous = current;
            current = previous.Next as SequenceSegment;
            previous.Return();
        }

        _writeHead?.Return();

        _first = _last = _writeHead = null;
        _currentPosition = _totalLength = 0;
    }

    /// <inheritdoc/>
    public void Dispose() => Reset();

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_writeHead is null || sizeHint >= _writeHead.Array.Length - _currentPosition)
        {
            return GetMemorySlow(sizeHint);
        }

        return _writeHead.AsMemory(_currentPosition);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (_writeHead is null || sizeHint >= _writeHead.Array.Length - _currentPosition)
        {
            return GetSpanSlow(sizeHint);
        }

        return _writeHead.Array.AsSpan(_currentPosition);
    }

    /// <summary>Copies the contents of this writer to a span.</summary>
    public readonly void CopyTo(Span<byte> output)
    {
        var current = _first;
        while (output.Length > 0 && current != null)
        {
            var segment = current.CommittedMemory.Span;
            var slice = segment[..Math.Min(segment.Length, output.Length)];
            slice.CopyTo(output);
            output = output[slice.Length..];
            current = current.Next as SequenceSegment;
        }

        if (output.Length > 0 && _currentPosition > 0 && _writeHead is not null)
        {
            var span = _writeHead.Array.AsSpan(0, Math.Min(output.Length, _currentPosition));
            span.CopyTo(output);
        }
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = _first;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            writer.Write(span);
            current = current.Next as SequenceSegment;
        }

        if (_currentPosition > 0 && _writeHead is not null)
        {
            writer.Write(_writeHead.Array.AsSpan(0, _currentPosition));
        }
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref TBufferWriter writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = _first;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            writer.Write(span);
            current = current.Next as SequenceSegment;
        }

        if (_currentPosition > 0 && _writeHead is not null)
        {
            Write(ref writer, _writeHead.Array.AsSpan(0, _currentPosition));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Write(ref TBufferWriter writer, ReadOnlySpan<byte> value)
        {
            Span<byte> destination = writer.GetSpan();

            // Fast path, try copying to the available memory directly
            if (value.Length <= destination.Length)
            {
                value.CopyTo(destination);
                writer.Advance(value.Length);
            }
            else
            {
                WriteMultiSegment(ref writer, value, destination);
            }
        }

        static void WriteMultiSegment(ref TBufferWriter writer, in ReadOnlySpan<byte> source, Span<byte> destination)
        {
            ReadOnlySpan<byte> input = source;
            while (true)
            {
                int writeSize = Math.Min(destination.Length, input.Length);
                input.Slice(0, writeSize).CopyTo(destination);
                writer.Advance(writeSize);
                input = input.Slice(writeSize);
                if (input.Length > 0)
                {
                    destination = writer.GetSpan();

                    if (destination.IsEmpty)
                    {
                        throw new ArgumentOutOfRangeException(nameof(writer));
                    }

                    continue;
                }

                return;
            }
        }
    }

    /// <summary>
    /// Returns a new <see cref="ReadOnlySequence{T}"/> which must not be accessed after disposing this instance.
    /// </summary>
    public ReadOnlySequence<byte> AsReadOnlySequence()
    {
        if (Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        Commit();
        if (_first == _last)
        {
            return new ReadOnlySequence<byte>(_first!.CommittedMemory);
        }

        return new ReadOnlySequence<byte>(_first!, 0, _last!, _last!.CommittedMemory.Length);
    }

    public BufferSlice Slice() => new(this, 0, Length);

    public BufferSlice Slice(int offset) => new(this, offset, Length - offset);

    public BufferSlice Slice(int offset, int length) => new(this, offset, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySequence<byte> input)
    {
        foreach (var segment in input)
        {
            Write(segment.Span);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> value)
    {
        var destination = GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(value, destination);
        }
    }

    private void WriteMultiSegment(in ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input.Slice(0, writeSize).CopyTo(destination);
            Advance(writeSize);
            input = input.Slice(writeSize);
            if (input.Length > 0)
            {
                destination = GetSpan();

                continue;
            }

            return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> GetSpanSlow(int sizeHint) => Grow(sizeHint).Array;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint) => Grow(sizeHint).AsMemory(0);

    private SequenceSegment Grow(int sizeHint)
    {
        Commit();
        var newBuffer = SequenceSegmentPool.Shared.Rent(sizeHint);
        return _writeHead = newBuffer;
    }

    private void Commit()
    {
        if (_currentPosition == 0 || _writeHead is null)
        {
            return;
        }

        _writeHead.Commit(_totalLength, _currentPosition);
        _totalLength += _currentPosition;
        if (_first is null)
        {
            _first = _writeHead;
            _last = _writeHead;
        }
        else
        {

            Debug.Assert(_last is not null);
            _last.SetNext(_writeHead);
            _last = _writeHead;
        }

        _writeHead = null;
        _currentPosition = 0;
    }

    public BufferSlice.MemorySequence AsMemorySequence() => Slice().AsMemorySequence();

    public BufferSlice.SpanSequence AsSpanSequence() => Slice().AsSpanSequence();

    public struct BufferSlice
    {
        internal readonly int _offset;
        internal readonly int _length;
        internal PooledBuffer _buffer;

        public BufferSlice(in PooledBuffer buffer, int start, int length)
        {
            _buffer = buffer;
            _offset = start;
            _length = length;
        }

        public readonly PooledBuffer Buffer => _buffer;

        public readonly int Offset => _offset;

        public readonly int Length => _length;

        public readonly BufferSlice Slice(int offset) => new(in _buffer, _offset + offset, _length - offset);

        public readonly BufferSlice Slice(int offset, int length) => new(in _buffer, _offset + offset, length);

        /// <summary>Copies the contents of this writer to a span.</summary>
        public readonly int CopyTo(Span<byte> output)
        {
            var copied = 0;
            foreach (var span in this)
            {
                var slice = span[..Math.Min(span.Length, output.Length)];
                slice.CopyTo(output);
                output = output[slice.Length..];
                copied += slice.Length;
            }

            return copied;
        }

        /// <summary>Copies the contents of this writer to a pooled buffer.</summary>
        public readonly void CopyTo(ref PooledBuffer output)
        {
            foreach (var span in this)
            {
                output.Write(span);
            }
        }

        /// <summary>Copies the contents of this writer to a buffer writer.</summary>
        public readonly void CopyTo<TBufferWriter>(ref TBufferWriter output) where TBufferWriter : struct, IBufferWriter<byte>
        {
            foreach (var span in this)
            {
                output.Write(span);
            }
        }

        /// <summary>
        /// Returns the data which has been written as an array.
        /// </summary>
        /// <returns>The data which has been written.</returns>
        public readonly byte[] ToArray()
        {
            var result = new byte[_length];
            CopyTo(result);
            return result;
        }

        public readonly MemorySequence AsMemorySequence() => new(in this);

        public readonly SpanSequence AsSpanSequence() => new(in this);

        public readonly SpanEnumerator GetEnumerator() => new(in this);

        public ref struct MemorySequence
        {
            private BufferSlice _slice;
            public MemorySequence(in BufferSlice slice) => _slice = slice;
            public MemoryEnumerator GetEnumerator() => new(ref _slice);
        }

        public ref struct SpanSequence
        {
            private BufferSlice _slice;
            public SpanSequence(in BufferSlice slice) => _slice = slice;
            public SpanEnumerator GetEnumerator() => new(in _slice);
        }

        public ref struct SpanEnumerator
        {
            private static readonly SequenceSegment InitialSegmentSentinel = new();
            private static readonly SequenceSegment FinalSegmentSentinel = new();
            private BufferSlice _slice;
            private int _position;
            private SequenceSegment _segment;

            public SpanEnumerator(in BufferSlice slice)
            {
                _slice = slice;
                _segment = InitialSegmentSentinel;
                Current = Span<byte>.Empty;
            }

            internal readonly PooledBuffer Buffer => _slice._buffer;
            internal readonly int Offset => _slice._offset;
            internal readonly int Length => _slice._length;

            public ReadOnlySpan<byte> Current { get; private set; }

            public bool MoveNext()
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
                    if (_position < Offset)
                    {
                        if (_position + segment.Length <= Offset)
                        {
                            // Start is in a subsequent segment
                            _position += segment.Length;
                            _segment = _segment.Next as SequenceSegment;
                            continue;
                        }
                        else
                        {
                            // Start is in this segment
                            segmentOffset = Offset;
                        }
                    }
                    else
                    {
                        segmentOffset = 0;
                    }

                    var segmentLength = Math.Min(segment.Length - segmentOffset, endPosition - (_position + segmentOffset));
                    if (segmentLength == 0)
                    {
                        Current = Span<byte>.Empty;
                        _segment = FinalSegmentSentinel;
                        return false;
                    }

                    Current = segment.Slice(segmentOffset, segmentLength);
                    _position += segmentOffset + segmentLength;
                    _segment = _segment.Next as SequenceSegment;
                    return true;
                }

                if (_segment != FinalSegmentSentinel && Buffer._currentPosition > 0 && Buffer._writeHead is { } head && _position < endPosition)
                {
                    var finalOffset = Math.Max(Offset - _position, 0);
                    var finalLength = Math.Min(Buffer._currentPosition, endPosition - (_position + finalOffset));
                    if (finalLength == 0)
                    {
                        Current = Span<byte>.Empty;
                        _segment = FinalSegmentSentinel;
                        return false;
                    }

                    Current = head.Array.AsSpan(finalOffset, finalLength);
                    _position += finalOffset + finalLength;
                    Debug.Assert(_position == endPosition);
                    _segment = FinalSegmentSentinel;
                    return true;
                }

                return false;
            }
        }

        public ref struct MemoryEnumerator
        {
            private static readonly SequenceSegment InitialSegmentSentinel = new();
            private static readonly SequenceSegment FinalSegmentSentinel = new();
            private readonly ref BufferSlice _slice;
            private int _position;
            private SequenceSegment _segment;

            public MemoryEnumerator(ref BufferSlice slice)
            {
                _slice = ref Unsafe.AsRef(in slice);
                _segment = InitialSegmentSentinel;
                Current = Memory<byte>.Empty;
            }

            internal readonly PooledBuffer Buffer => _slice._buffer;
            internal readonly int Offset => _slice._offset;
            internal readonly int Length => _slice._length;

            public ReadOnlyMemory<byte> Current { get; private set; }

            public bool MoveNext()
            {
                if (ReferenceEquals(_segment, InitialSegmentSentinel))
                {
                    _segment = _slice._buffer._first;
                }

                var endPosition = Offset + Length;
                while (_segment != null && _segment != FinalSegmentSentinel)
                {
                    var segment = _segment.CommittedMemory;

                    // Find the starting segment and the offset to copy from.
                    int segmentOffset;
                    if (_position < Offset)
                    {
                        if (_position + segment.Length <= Offset)
                        {
                            // Start is in a subsequent segment
                            _position += segment.Length;
                            _segment = _segment.Next as SequenceSegment;
                            continue;
                        }
                        else
                        {
                            // Start is in this segment
                            segmentOffset = Offset;
                        }
                    }
                    else
                    {
                        segmentOffset = 0;
                    }

                    var segmentLength = Math.Min(segment.Length - segmentOffset, endPosition - (_position + segmentOffset));
                    if (segmentLength == 0)
                    {
                        Current = Memory<byte>.Empty;
                        return false;
                    }

                    Current = segment.Slice(segmentOffset, segmentLength);
                    _position += segmentOffset + segmentLength;
                    _segment = _segment.Next as SequenceSegment;
                    return true;
                }

                if (_segment != FinalSegmentSentinel && Buffer._currentPosition > 0 && Buffer._writeHead is { } head && _position < endPosition)
                {
                    var finalOffset = Math.Max(Offset - _position, 0);
                    var finalLength = Math.Min(Buffer._currentPosition, endPosition - (_position + finalOffset));
                    if (finalLength == 0)
                    {
                        Current = Memory<byte>.Empty;
                        return false;
                    }

                    Current = head.Array.AsMemory(finalOffset, finalLength);
                    _position += finalOffset + finalLength;
                    Debug.Assert(_position == endPosition);
                    _segment = FinalSegmentSentinel;
                    return true;
                }

                return false;
            }
        }
    }

    private sealed class SequenceSegmentPool
    {
        public static SequenceSegmentPool Shared { get; } = new();
        public const int MinimumBlockSize = 4 * 1024;
        private readonly ConcurrentQueue<SequenceSegment> _blocks = new();
        private readonly ConcurrentQueue<SequenceSegment> _largeBlocks = new();

        private SequenceSegmentPool() { }

        public SequenceSegment Rent(int size = -1)
        {
            SequenceSegment block;
            if (size <= MinimumBlockSize)
            {
                if (!_blocks.TryDequeue(out block))
                {
                    block = new SequenceSegment(size);
                }
            }
            else if (_largeBlocks.TryDequeue(out block))
            {
                block.ResizeLargeSegment(size);
                return block;
            }

            return block ?? new SequenceSegment(size);
        }

        internal void Return(SequenceSegment block)
        {
            Debug.Assert(block.IsValid);
            if (block.IsMinimumSize)
            {
                _blocks.Enqueue(block);
            }
            else
            {
                _largeBlocks.Enqueue(block);
            }
        }
    }

    internal sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        internal SequenceSegment()
        {
            Array = System.Array.Empty<byte>();
        }

        internal SequenceSegment(int length)
        {
            InitializeArray(length);
        }

        public void ResizeLargeSegment(int length)
        {
            Debug.Assert(length > SequenceSegmentPool.MinimumBlockSize);
            InitializeArray(length);
        }

        [MemberNotNull(nameof(Array))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeArray(int length)
        {
            if (length <= SequenceSegmentPool.MinimumBlockSize)
            {
                Debug.Assert(Array is null);
                var pinnedArray = GC.AllocateUninitializedArray<byte>(SequenceSegmentPool.MinimumBlockSize, pinned: true);
                Array = pinnedArray;
            }
            else
            {
                // Round up to a power of two.
                length = (int)BitOperations.RoundUpToPowerOf2((uint)length);

                if (Array is not null)
                {
                    // The segment has an appropriate size already.
                    if (Array.Length == length)
                    {
                        return;
                    }

                    // The segment is being resized.
                    ArrayPool<byte>.Shared.Return(Array);
                }

                Array = ArrayPool<byte>.Shared.Rent(length);
            }
        }

        public byte[] Array;

        public ReadOnlyMemory<byte> CommittedMemory => Memory;

        public bool IsValid => Array is { Length: > 0 };
        public bool IsMinimumSize => Array.Length == SequenceSegmentPool.MinimumBlockSize;

        public Memory<byte> AsMemory(int offset) => AsMemory(offset, Array.Length - offset);

        public Memory<byte> AsMemory(int offset, int length)
        {
            if (IsMinimumSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, length);
            }

            return Array.AsMemory(offset, length);
        }

        public void Commit(long runningIndex, int length)
        {
            RunningIndex = runningIndex;
            Memory = AsMemory(0, length);
        }

        public void SetNext(SequenceSegment next) => Next = next;

        public void Return()
        {
            RunningIndex = default;
            Next = default;
            Memory = default;

            SequenceSegmentPool.Shared.Return(this);
        }
    }
}
