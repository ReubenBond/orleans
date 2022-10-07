using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
#if NETCOREAPP3_1_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using static Orleans.Serialization.Buffers.PooledBuffer;
#if !NETCOREAPP3_1_OR_GREATER
using Orleans.Serialization.Utilities;
#endif

namespace Orleans.Serialization.Buffers
{
    /// <summary>
    /// An input type for <see cref="Reader{T}"/> which reads from a stream.
    /// </summary>
    public struct StreamReaderInput
    {
        [ThreadStatic]
        private static byte[] Scratch;
        private readonly Stream _stream;
        private readonly ArrayPool<byte> _memoryPool;
        internal long GlobalOffset;

        /// <summary>
        /// Gets the position.
        /// </summary>
        /// <value>The position.</value>
        public long Position => _stream.Position;

        /// <summary>
        /// Gets the length.
        /// </summary>
        /// <value>The length.</value>
        public long Length => _stream.Length;

        public StreamReaderInput(Stream stream, ArrayPool<byte> memoryPool)
        {
            _stream = stream;
            _memoryPool = memoryPool;
        }

        /// <summary>
        /// Reads a byte from the input.
        /// </summary>
        /// <returns>The byte which was read.</returns>
        public byte ReadByte()
        {
            var c = _stream.ReadByte();
            if (c < 0)
            {
                ThrowInsufficientData();
            }

            return (byte)c;
        }

        /// <summary>
        /// Fills the destination span with data from the input.
        /// </summary>
        /// <param name="destination">The destination.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadBytes(Span<byte> destination)
        {
#if NETCOREAPP3_1_OR_GREATER
            var count = _stream.Read(destination);
            if (count < destination.Length)
            {
                ThrowInsufficientData();
            }
#else
            byte[] array = default;
            try
            {
                array = _memoryPool.Rent(destination.Length);
                var count = _stream.Read(array, 0, destination.Length);
                if (count < destination.Length)
                {
                    ThrowInsufficientData();
                }

                array.CopyTo(destination);
            }
            finally
            {
                if (array is object)
                {
                    _memoryPool.Return(array);
                }
            }
#endif
        }

        /// <summary>
        /// Reads bytes from the input into the destination array.
        /// </summary>
        /// <param name="destination">The destination array.</param>
        /// <param name="offset">The offset into the destination to start writing bytes.</param>
        /// <param name="length">The number of bytes to copy into destination.</param>
        public void ReadBytes(byte[] destination, int offset, int length)
        {
            var count = _stream.Read(destination, offset, length);
            if (count < length)
            {
                ThrowInsufficientData();
            }
        }

        /// <summary>
        /// Reads a <see cref="uint"/> from the input.
        /// </summary>
        /// <returns>The <see cref="uint"/> which was read.</returns>
#if NET5_0_OR_GREATER
        [SkipLocalsInit]
#endif
        public uint ReadUInt32()
        {
#if NETCOREAPP3_1_OR_GREATER
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            ReadBytes(buffer);
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
#else
            var buffer = GetScratchBuffer();
            ReadBytes(buffer, 0, sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)));
#endif
        }

        /// <summary>
        /// Reads a <see cref="ulong"/> from the input.
        /// </summary>
        /// <returns>The <see cref="ulong"/> which was read.</returns>
#if NET5_0_OR_GREATER
        [SkipLocalsInit]
#endif
        public ulong ReadUInt64()
        {
#if NETCOREAPP3_1_OR_GREATER
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            ReadBytes(buffer);
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
#else
            var buffer = GetScratchBuffer();
            ReadBytes(buffer, 0, sizeof(ulong));
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, sizeof(ulong)));
#endif
        }

        /// <summary>
        /// Skips the specified number of bytes.
        /// </summary>
        /// <param name="count">The number of bytes to skip.</param>
        public void Skip(long count) => _ = _stream.Seek(count, SeekOrigin.Current);

        /// <summary>
        /// Seeks to the specified position.
        /// </summary>
        /// <param name="position">The position.</param>
        public void Seek(long position) => _ = _stream.Seek(position, SeekOrigin.Begin);

        /// <summary>
        /// Tries to read the specified number of bytes from the input.
        /// </summary>
        /// <param name="length">The number of bytes to read..</param>
        /// <param name="destination">The bytes which were read..</param>
        /// <returns><see langword="true"/> if the number of bytes were successfully read, <see langword="false"/> otherwise.</returns>
        public bool TryReadBytes(int length, out ReadOnlySpan<byte> destination)
        {
            // Cannot get a span pointing to a stream's internal buffer.
            destination = default;
            return false;
        }

        private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");

        private static byte[] GetScratchBuffer() => Scratch ??= new byte[1024];
    }

    /// <summary>
    /// Helper methods for <see cref="Reader{TInput}"/>.
    /// </summary>
    public static class Reader
    {
        /// <summary>
        /// Creates a reader for the provided input stream.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<BufferSliceReaderInput> Create(PooledBuffer input, SerializerSession session) => Create(input.Slice(), session);

        /// <summary>
        /// Creates a reader for the provided input stream.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<BufferSliceReaderInput> Create(BufferSlice input, SerializerSession session) => Create(new BufferSliceReaderInput(input), session);

        /// <summary>
        /// Creates a reader for the provided input stream.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<BufferSliceReaderInput> Create(BufferSliceReaderInput input, SerializerSession session) => new Reader<BufferSliceReaderInput>(input, session, 0);

        /// <summary>
        /// Creates a reader for the provided input stream.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<StreamReaderInput> Create(Stream stream, SerializerSession session) => new Reader<StreamReaderInput>(new StreamReaderInput(stream, ArrayPool<byte>.Shared), session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="sequence">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<ReadOnlySequenceReaderInput> Create(ReadOnlySequence<byte> sequence, SerializerSession session) => new(new ReadOnlySequenceReaderInput(sequence), session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="buffer">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(ReadOnlySpan<byte> buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer, session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="buffer">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(byte[] buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer, session, 0);

        /// <summary>
        /// Creates a reader for the provided input data.
        /// </summary>
        /// <param name="buffer">The input data.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new <see cref="Reader{TInput}"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader<SpanReaderInput> Create(ReadOnlyMemory<byte> buffer, SerializerSession session) => new Reader<SpanReaderInput>(buffer.Span, session, 0);
    }

    /// <summary>
    /// Marker type for <see cref="Reader{TInput}"/> objects which operate over <see cref="ReadOnlySpan{Byte}"/> buffers.
    /// </summary>
    public struct SpanReaderInput
    {
        internal long Length;
    }

    /// <summary>
    /// <see cref="Reader{TInput}"/> provider which operates over <see cref="ReadOnlySequence{Byte}"/> buffers.
    /// </summary>
    public struct ReadOnlySequenceReaderInput
    {
        internal readonly ReadOnlySequence<byte> Sequence;
        internal SequencePosition NextSequencePosition;
        internal long VisitedBuffersLength;

        public ReadOnlySequenceReaderInput(ReadOnlySequence<byte> sequence)
        {
            Sequence = sequence;
            NextSequencePosition = sequence.Start;
        }
    }

    /// <summary>
    /// Provides functionality for parsing data from binary input.
    /// </summary>
    /// <typeparam name="TInput">The underlying buffer reader type.</typeparam>
    public ref struct Reader<TInput>
    {
        private readonly static bool IsSpanInput = typeof(TInput) == typeof(SpanReaderInput);
        private readonly static bool IsReadOnlySequenceInput = typeof(TInput) == typeof(ReadOnlySequenceReaderInput);
        private readonly static bool IsStreamReaderInput = typeof(TInput) == typeof(StreamReaderInput);
        private readonly static bool IsBufferSliceInput = typeof(TInput) == typeof(BufferSliceReaderInput);
        
        internal ref byte _cursor;
        internal ref byte _end;
        private TInput _input;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Reader(TInput input, SerializerSession session, long globalOffset)
        {
            if (IsReadOnlySequenceInput)
            {
                _input = input;
                ref var typedInput = ref Unsafe.As<TInput, ReadOnlySequenceReaderInput>(ref _input);
                var span = typedInput.Sequence.First.Span;
                _cursor = ref MemoryMarshal.GetReference(span);
                _end = ref Unsafe.AddByteOffset(ref _cursor, span.Length);
                typedInput.VisitedBuffersLength = globalOffset + span.Length;
            }
            else if (IsBufferSliceInput)
            {
                _input = input;
                ref var typedInput = ref Unsafe.As<TInput, BufferSliceReaderInput>(ref _input);
                typedInput.VisitedBuffersLength = globalOffset;
                MoveNext();
            }
            else if (IsStreamReaderInput)
            {
                _input = input;
                ref var typedInput = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                typedInput.GlobalOffset = globalOffset;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported by this constructor");
            }

            Session = session;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Reader(ReadOnlySpan<byte> input, SerializerSession session, long globalOffset)
        {
            if (IsSpanInput)
            {
                _cursor = ref MemoryMarshal.GetReference(input); 
                _end = ref Unsafe.AddByteOffset(ref _cursor, input.Length);
                ref var spanInput = ref Unsafe.As<TInput, SpanReaderInput>(ref _input);
                spanInput.Length = globalOffset + input.Length;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported by this constructor");
            }

            Session = session;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private nint GetBufferUnconsumedLength()
        {
            if (IsReadOnlySequenceInput || IsBufferSliceInput || IsSpanInput)
            {
                return Unsafe.ByteOffset(ref _cursor, ref _end);
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets the serializer session.
        /// </summary>
        /// <value>The serializer session.</value>
        public SerializerSession Session { get; }

        /// <summary>
        /// Gets the current reader position.
        /// </summary>
        /// <value>The current position.</value>
        public long Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsReadOnlySequenceInput)
                {
                    ref var input = ref Unsafe.As<TInput, ReadOnlySequenceReaderInput>(ref _input);
                    return input.VisitedBuffersLength - GetBufferUnconsumedLength();
                }
                else if (IsBufferSliceInput)
                {
                    ref var input = ref Unsafe.As<TInput, BufferSliceReaderInput>(ref _input);
                    return input.VisitedBuffersLength - GetBufferUnconsumedLength();
                }
                else if (IsSpanInput)
                {
                    ref var input = ref Unsafe.As<TInput, SpanReaderInput>(ref _input);
                    return input.Length - GetBufferUnconsumedLength();
                }
                else if (IsStreamReaderInput)
                {
                    ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                    return input.Position;
                }
                else
                {
                    return ThrowNotSupportedInput<long>();
                }
            }
        }

        /// <summary>
        /// Gets the input length.
        /// </summary>
        /// <value>The input length.</value>
        public long Length
        {
            get
            {
                if (IsReadOnlySequenceInput)
                {
                    ref var input = ref Unsafe.As<TInput, ReadOnlySequenceReaderInput>(ref _input);
                    return input.Sequence.Length;
                }
                else if (IsBufferSliceInput)
                {
                    ref var input = ref Unsafe.As<TInput, BufferSliceReaderInput>(ref _input);
                    return input.Length;
                }
                else if (IsSpanInput)
                {
                    ref var input = ref Unsafe.As<TInput, SpanReaderInput>(ref _input);
                    return input.Length;
                }
                else if (IsStreamReaderInput)
                {
                    ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                    return input.Length;
                }
                else
                {
                    return ThrowNotSupportedInput<long>();
                }
            }
        }

        /// <summary>
        /// Skips the specified number of bytes.
        /// </summary>
        /// <param name="count">The number of bytes to skip.</param>
        public void Skip(long count)
        {
            if (IsReadOnlySequenceInput || IsBufferSliceInput)
            {
                while (count > 0)
                {
                    if (count <= GetBufferUnconsumedLength())
                    {
                        UnsafeAdvance((int)count);
                        break;
                    }
                    else
                    {
                        var initialPosition = Position;
                        MoveNext();
                        count -= Position - initialPosition;
                    }
                }
            }
            else if (IsSpanInput)
            {
                if (count > GetBufferUnconsumedLength())
                {
                    ThrowInsufficientData();
                }

                UnsafeAdvance((int)count);
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                input.Skip(count);
            }
            else
            {
                ThrowNotSupportedInput();
            }
        }

        /// <summary>
        /// Creates a new reader beginning at the specified position.
        /// </summary>        
        /// <param name="position">
        /// The position in the input stream to fork from.
        /// </param>        
        /// <param name="forked">
        /// The forked reader instance.
        /// </param>        
        public void ForkFrom(long position, out Reader<TInput> forked)
        {
            if (IsReadOnlySequenceInput)
            {
                ref var input = ref Unsafe.As<TInput, ReadOnlySequenceReaderInput>(ref _input);
                var slicedSequence = input.Sequence.Slice(position);
                var slicedInput = new ReadOnlySequenceReaderInput(slicedSequence);
                forked = new Reader<TInput>(Unsafe.As<ReadOnlySequenceReaderInput, TInput>(ref slicedInput), Session, position);

                if (forked.Position != position)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else if (IsBufferSliceInput)
            {
                ref var input = ref Unsafe.As<TInput, BufferSliceReaderInput>(ref _input);
                var newInput = input.ForkFrom(checked((int)position));
                forked = new Reader<TInput>(Unsafe.As<BufferSliceReaderInput, TInput>(ref newInput), Session, position);

                if (forked.Position != position)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else if (IsSpanInput)
            {
                var slicedSpan = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref _cursor, (int)position), (int)(Length - position));
                forked = new Reader<TInput>(slicedSpan, Session, position);
                if (forked.Position != position || position > int.MaxValue)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                input.Seek(position);
                forked = new Reader<TInput>(_input, Session, 0);

                if (forked.Position != position)
                {
                    ThrowInvalidPosition(position, forked.Position);
                }
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported");
            }
            
            static void ThrowInvalidPosition(long expectedPosition, long actualPosition)
            {
                throw new InvalidOperationException($"Expected to arrive at position {expectedPosition} after ForkFrom, but resulting position is {actualPosition}");
            }
        }
        
        /// <summary>
        /// Resumes the reader from the specified position after forked readers are no longer in use.
        /// </summary>
        /// <param name="position">
        /// The position to resume reading from.
        /// </param>
        public void ResumeFrom(long position)
        {
            if (IsReadOnlySequenceInput)
            {
                // Nothing is required.
            }
            else if (IsBufferSliceInput)
            {
                // Nothing is required.
            }
            else if (IsSpanInput)
            {
                // Nothing is required.
            }
            else if (IsStreamReaderInput)
            {
                // Seek the input stream.
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                input.Seek(Position);
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(TInput)} is not supported");
            }

            if (position != Position)
            {
                ThrowInvalidPosition(position, Position);
            }

            static void ThrowInvalidPosition(long expectedPosition, long actualPosition)
            {
                throw new InvalidOperationException($"Expected to arrive at position {expectedPosition} after ResumeFrom, but resulting position is {actualPosition}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveNext()
        {
            if (IsReadOnlySequenceInput)
            {
                ref var input = ref Unsafe.As<TInput, ReadOnlySequenceReaderInput>(ref _input);

                // If this is the first call to MoveNext then nextSequencePosition is invalid and must be moved to the second position.
                if (input.NextSequencePosition.Equals(input.Sequence.Start))
                {
                    _ = input.Sequence.TryGet(ref input.NextSequencePosition, out _);
                }

                if (!input.Sequence.TryGet(ref input.NextSequencePosition, out var memory))
                {
                    ThrowInsufficientData();
                }

                var span = memory.Span;
                _cursor = ref MemoryMarshal.GetReference(span);
                _end  = ref Unsafe.Add(ref _cursor, span.Length);
                input.VisitedBuffersLength += span.Length;
            }
            else if (IsBufferSliceInput)
            {
                ref var input = ref Unsafe.As<TInput, BufferSliceReaderInput>(ref _input);
                MoveNext(ref input);
            }
            else if (IsSpanInput)
            {
                ThrowInsufficientData();
            }
            else
            {
                ThrowNotSupportedInput();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveNext(ref BufferSliceReaderInput input)
        {
            if (input._segment is not null)
            {
                MoveSubsequent(ref input);
                return;
            }

            ref var buffer = ref input._slice._buffer;
            var endPosition = input.Offset + input.Length;
            var finalOffset = Math.Max(input.Offset - input._position, 0);
            var finalLength = Math.Min(buffer._currentPosition, endPosition - (input._position + finalOffset));
            if (finalLength > 0)
            {
                _cursor = ref Unsafe.AddByteOffset(ref MemoryMarshal.GetArrayDataReference(buffer._writeHead.Array), finalOffset);
                _end = ref Unsafe.AddByteOffset(ref _cursor, finalLength);
                input._position += finalOffset + finalLength;
                Debug.Assert(input._position == endPosition);
                input._segment = BufferSliceReaderInput.FinalSegmentSentinel;
                input.VisitedBuffersLength += GetBufferUnconsumedLength();
                return;
            }

            ThrowInsufficientData();
        }

        private void MoveSubsequent(ref BufferSliceReaderInput input)
        {
            ref var segment = ref input._segment;
            if (segment != BufferSliceReaderInput.FinalSegmentSentinel)
            {
                while (segment != null)
                {
                    var span = segment.CommittedMemory.Span;

                    // Find the starting segment and the offset to copy from.
                    int segmentOffset;
                    if (input._position < input.Offset)
                    {
                        if (input._position + span.Length <= input.Offset)
                        {
                            // Start is in a subsequent segment
                            input._position += span.Length;
                            segment = Unsafe.As<SequenceSegment>(segment.Next);
                            continue;
                        }
                        else
                        {
                            // Start is in this segment
                            segmentOffset = input.Offset;
                        }
                    }
                    else
                    {
                        segmentOffset = 0;
                    }

                    var segmentLength = Math.Min(span.Length - segmentOffset, input.Offset + input.Length - (input._position + segmentOffset));
                    _cursor = ref Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(span), segmentOffset);
                    _end = ref Unsafe.AddByteOffset(ref _cursor, segmentLength);
                    input._position += segmentOffset + segmentLength;
                    segment = Unsafe.As<SequenceSegment>(segment.Next);
                    input.VisitedBuffersLength += GetBufferUnconsumedLength();
                    return;
                }
            }

            ThrowInsufficientData();
        }

        /// <summary>
        /// Reads a byte from the input.
        /// </summary>
        /// <returns>The byte which was read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                if (Unsafe.AreSame(ref _cursor, ref _end))
                {
                    return ReadByteSlow(ref this);
                }

                var result = _cursor;
                UnsafeAdvance(1);
                return result;
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                return input.ReadByte();
            }
            else
            {
                return ThrowNotSupportedInput<byte>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte ReadByteSlow(ref Reader<TInput> reader)
        {
            reader.MoveNext();
            var result = reader._cursor;
            reader.UnsafeAdvance(1);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ReadOnlySpan<byte> UnsafeConsumeSpan(int length)
        {
            var result = MemoryMarshal.CreateReadOnlySpan(ref _cursor, length);
            UnsafeAdvance(length);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UnsafeAdvance(int offset)
        {
            _cursor = ref Unsafe.Add(ref _cursor, offset);
        }

        /// <summary>
        /// Reads an <see cref="int"/> from the input.
        /// </summary>
        /// <returns>The <see cref="int"/> which was read.</returns>
        public int ReadInt32() => (int)ReadUInt32();

        /// <summary>
        /// Reads a <see cref="uint"/> from the input.
        /// </summary>
        /// <returns>The <see cref="uint"/> which was read.</returns>
        public uint ReadUInt32()
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                const int width = 4;
                if (width > GetBufferUnconsumedLength())
                {
                    return ReadSlower(ref this);
                }

                var readHead = MemoryMarshal.CreateReadOnlySpan(ref _cursor, width);
                var result = BinaryPrimitives.ReadUInt32LittleEndian(readHead);
                UnsafeAdvance(width);
                return result;

                static uint ReadSlower(ref Reader<TInput> r)
                {
                    Span<byte> span = stackalloc byte[width];
                    r.ReadBytes(span);
                    return BinaryPrimitives.ReadUInt32LittleEndian(span);
                }
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                return input.ReadUInt32();
            }
            else
            {
                return ThrowNotSupportedInput<uint>();
            }
        }

        /// <summary>
        /// Reads a <see cref="long"/> from the input.
        /// </summary>
        /// <returns>The <see cref="long"/> which was read.</returns>
        public long ReadInt64() => (long)ReadUInt64();

        /// <summary>
        /// Reads a <see cref="ulong"/> from the input.
        /// </summary>
        /// <returns>The <see cref="ulong"/> which was read.</returns>
        public ulong ReadUInt64()
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                const int width = 8;
                if (width > GetBufferUnconsumedLength())
                {
                    return ReadSlower(ref this);
                }

                var readHead = MemoryMarshal.CreateReadOnlySpan(ref _cursor, width);
                var result = BinaryPrimitives.ReadUInt64LittleEndian(readHead);
                UnsafeAdvance(width);
                return result;

                static ulong ReadSlower(ref Reader<TInput> r)
                {
                    Span<byte> span = stackalloc byte[width];
                    r.ReadBytes(span);
                    return BinaryPrimitives.ReadUInt64LittleEndian(span);
                }
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                return input.ReadUInt64();
            }
            else
            {
                return ThrowNotSupportedInput<uint>();
            }
        }

        [DoesNotReturn]
        private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");

        /// <summary>
        /// Reads the specified number of bytes into the provided writer.
        /// </summary>
        public void ReadBytes<TBufferWriter>(scoped ref TBufferWriter writer, int count) where TBufferWriter : IBufferWriter<byte>
        {
            int chunkSize;
            for (var remaining = count; remaining > 0; remaining -= chunkSize)
            {
                var span = writer.GetSpan();
                if (span.Length > remaining)
                {
                    span = span[..remaining];
                }

                ReadBytes(span);
                chunkSize = span.Length;
                writer.Advance(chunkSize);
            }
        }

        /// <summary>
        /// Reads an array of bytes from the input.
        /// </summary>
        /// <param name="count">The length of the array to read.</param>
        /// <returns>The array wihch was read.</returns>
        public byte[] ReadBytes(uint count)
        {
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            if (count > 10240 && count > Length)
            {
                ThrowInvalidSizeException(count);
            }

            var bytes = new byte[count];
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                var destination = new Span<byte>(bytes);
                ReadBytes(destination);
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                input.ReadBytes(bytes, 0, (int)count);
            }

            return bytes;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> with bytes read from the input.
        /// </summary>
        /// <param name="destination">The destination.</param>
        public void ReadBytes(scoped Span<byte> destination)
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                if (destination.Length <= GetBufferUnconsumedLength())
                {
                    UnsafeConsumeSpan(destination.Length).CopyTo(destination);
                    return;
                }

                ReadBytesMultiSegment(destination);
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                input.ReadBytes(destination);
            }
            else
            {
                ThrowNotSupportedInput();
            }
        }

        private void ReadBytesMultiSegment(scoped Span<byte> dest)
        {
            while (true)
            {
                var writeSize = Math.Min(dest.Length, (int)GetBufferUnconsumedLength());
                UnsafeConsumeSpan(writeSize).CopyTo(dest);
                dest = dest[writeSize..];

                if (dest.Length == 0)
                {
                    break;
                }

                MoveNext();
            }
        }

        /// <summary>
        /// Tries the read the specified number of bytes from the input.
        /// </summary>
        /// <param name="length">The length.</param>
        /// <param name="bytes">The bytes which were read.</param>
        /// <returns><see langword="true"/> if the specified number of bytes were read from the input, <see langword="false"/> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                if (length <= GetBufferUnconsumedLength())
                {
                    bytes = UnsafeConsumeSpan(length);
                    return true;
                }

                bytes = default;
                return false;
            }
            else if (IsStreamReaderInput)
            {
                ref var input = ref Unsafe.As<TInput, StreamReaderInput>(ref _input);
                return input.TryReadBytes(length, out bytes);
            }
            else
            {
                bytes = default;
                return ThrowNotSupportedInput<bool>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal uint ReadVarUInt32NoInlining() => ReadVarUInt32();

        /// <summary>
        /// Reads a variable-width <see cref="uint"/> from the input.
        /// </summary>
        /// <returns>The <see cref="uint"/> which was read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint ReadVarUInt32()
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                if (!BitConverter.IsLittleEndian || 8 > GetBufferUnconsumedLength())
                {
                    return ReadVarUInt32Slow();
                }

                // The number of zeros in the msb position dictates the number of bytes to be read.
                // Up to a maximum of 5 for a 32bit integer.
                ulong result = Unsafe.ReadUnaligned<ulong>(ref _cursor);
                var bytesNeeded = BitOperations.TrailingZeroCount((uint)result) + 1;
                if (bytesNeeded > 5) ThrowOverflowException();
                UnsafeAdvance(bytesNeeded);
                result &= (1UL << (bytesNeeded * 8)) - 1;
                result >>= bytesNeeded;
                return checked((uint)result);
            }
            else
            {
                return ReadVarUInt32Slow();
            }
        }

        private static void ThrowOverflowException() => throw new OverflowException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private uint ReadVarUInt32Slow()
        {
            var header = ReadByte();
            var numBytes = BitOperations.TrailingZeroCount(0x0100U | header) + 1;

            // Widen to a ulong for the 5-byte case
            ulong result = header;

            // Read additional bytes as needed
            var shiftBy = 8;
            var i = numBytes;
            while (--i > 0)
            {
                result |= (ulong)ReadByte() << shiftBy;
                shiftBy += 8;
            }

            result >>= numBytes;
            return checked((uint)result);
        }

        /// <summary>
        /// Reads a variable-width <see cref="ulong"/> from the input.
        /// </summary>
        /// <returns>The <see cref="ulong"/> which was read.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadVarUInt64()
        {
            if (IsReadOnlySequenceInput || IsSpanInput || IsBufferSliceInput)
            {
                if (!BitConverter.IsLittleEndian || 10 > GetBufferUnconsumedLength())
                {
                    return ReadVarUInt64Slow();
                }

                // The number of zeros in the msb position dictates the number of bytes to be read.
                // Up to a maximum of 5 for a 32bit integer.
                ulong result = Unsafe.ReadUnaligned<ulong>(ref _cursor);

                var bytesNeeded = BitOperations.TrailingZeroCount(result) + 1;
                result >>= bytesNeeded;

                ushort upper = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref _cursor, sizeof(ulong)));
                result |= ((ulong)upper) << (64 - bytesNeeded);
                UnsafeAdvance(bytesNeeded);

                // Mask off invalid data
                var fullWidthReadMask = ~((ulong)bytesNeeded - 10 + 1);
                var mask = ((1UL << (bytesNeeded * 7)) - 1) | fullWidthReadMask;
                result &= mask;

                return result;
            }
            else
            {
                return ReadVarUInt64Slow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ulong ReadVarUInt64Slow()
        {
            var header = ReadByte();
            var numBytes = BitOperations.TrailingZeroCount(0x0100U | header) + 1;

            // Widen to a ulong for the 5-byte case
            ulong result = header;

            // Read additional bytes as needed
            if (numBytes < 9)
            {
                var shiftBy = 8;
                var i = numBytes;
                while (--i > 0)
                {
                    result |= (ulong)ReadByte() << shiftBy;
                    shiftBy += 8;
                }

                result >>= numBytes;
                return result;
            }
            else
            {
                result |= (ulong)ReadByte() << 8;

                // If there was more than one byte worth of trailing zeros, read again now that we have more data.
                numBytes = BitOperations.TrailingZeroCount(result) + 1;

                if (numBytes == 9)
                {
                    result |= (ulong)ReadByte() << 16;
                    result |= (ulong)ReadByte() << 24;
                    result |= (ulong)ReadByte() << 32;

                    result |= (ulong)ReadByte() << 40;
                    result |= (ulong)ReadByte() << 48;
                    result |= (ulong)ReadByte() << 56;
                    result >>= 9;

                    var upper = (ushort)ReadByte();
                    result |= ((ulong)upper) << (64 - 9);
                    return result;
                }
                else if (numBytes == 10)
                {
                    result |= (ulong)ReadByte() << 16;
                    result |= (ulong)ReadByte() << 24;
                    result |= (ulong)ReadByte() << 32;

                    result |= (ulong)ReadByte() << 40;
                    result |= (ulong)ReadByte() << 48;
                    result |= (ulong)ReadByte() << 56;
                    result >>= 10;

                    var upper = (ushort)(ReadByte() | (ushort)(ReadByte() << 8));
                    result |= ((ulong)upper) << (64 - 10);
                    return result;
                }
            }

            return ExceptionHelper.ThrowArgumentOutOfRange<ulong>("value");
        }

        private static T ThrowNotSupportedInput<T>() => throw new NotSupportedException($"Type {typeof(TInput)} is not supported");

        private static void ThrowNotSupportedInput() => throw new NotSupportedException($"Type {typeof(TInput)} is not supported");

        private static void ThrowInvalidSizeException(uint length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(byte[])}, {length}, is greater than total length of input.");
    }
}