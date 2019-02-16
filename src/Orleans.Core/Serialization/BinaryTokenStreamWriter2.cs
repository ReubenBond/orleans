using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Serialization
{

    // NOTE: This is broken - do not use it.
    public sealed class MultiSegmentBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private static readonly ArraySegment<byte> EmptySegment = new ArraySegment<byte>(Array.Empty<byte>(), 0, 0);
        private readonly int maxAllocationSize;
        private readonly int segmentStartIndex;
        private ArraySegment<byte> current = EmptySegment;

        public MultiSegmentBufferWriter(int maxAllocationSize, List<ArraySegment<byte>> bufferList)
        {
            this.segmentStartIndex = bufferList.Count;
            this.Committed = bufferList;
            this.maxAllocationSize = maxAllocationSize;
        }

        public MultiSegmentBufferWriter(int maxAllocationSize = int.MaxValue)
        {
            this.segmentStartIndex = 0;
            this.Committed = new List<ArraySegment<byte>>();
            this.maxAllocationSize = maxAllocationSize;
        }

        public List<ArraySegment<byte>> Committed { get; }
        public int CommitedByteCount { get; private set; }

        public List<ArraySegment<byte>> ExpensiveGetOwned => this.Committed.Skip(this.segmentStartIndex).ToList();

        public void Advance(int bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            if (bytes > this.current.Count)
            {
                ThrowAdvancedPastSegment();
            }

            var last = this.Committed.Count - 1;
            ArraySegment<byte> lastSegment;
            if (last >= 0 && ReferenceEquals((lastSegment = this.Committed[last]).Array, this.current.Array))
            {
                // Extend the existing committed segment.
                this.Committed[last] = new ArraySegment<byte>(lastSegment.Array, lastSegment.Offset, lastSegment.Count + bytes);
                this.current = new ArraySegment<byte>(lastSegment.Array, lastSegment.Offset + bytes, lastSegment.Count - bytes);
            }
            else
            {
                // Append a new segment.
                this.Committed.Add(new ArraySegment<byte>(this.current.Array, this.current.Offset, bytes));
                this.current = EmptySegment;
            }

            this.CommitedByteCount += bytes;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint == 0)
            {
                sizeHint = this.current.Count + 1;
            }
            else if (sizeHint < this.current.Count) ThrowSufficientSpace();

            var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(sizeHint, this.maxAllocationSize));
            this.current.AsSpan().CopyTo(newBuffer.AsSpan());
            if (this.current != null && this.current.Count > 0) ArrayPool<byte>.Shared.Return(this.current.Array);
            this.current = new ArraySegment<byte>(newBuffer, 0, newBuffer.Length);
            return this.current;
        }

        public Span<byte> GetSpan(int sizeHint)
        {
            if (sizeHint == 0)
            {
                sizeHint = this.current.Count + 1;
            }
            else if (sizeHint < this.current.Count) ThrowSufficientSpace();

            var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(sizeHint, this.maxAllocationSize));
            this.current.AsSpan().CopyTo(newBuffer.AsSpan());
            if (this.current != null && this.current.Count > 0) ArrayPool<byte>.Shared.Return(this.current.Array);
            this.current = new ArraySegment<byte>(newBuffer, 0, newBuffer.Length);
            return this.current;
        }

        private static void ThrowSufficientSpace() => throw new InvalidOperationException("Attempted to allocate a new buffer when the existing buffer has sufficient free space.");

        private static void ThrowAdvancedPastSegment() => throw new InvalidOperationException("Attempted to advance past the end of the current segment.");

        public void Dispose()
        {
            var last = this.Committed.Count - 1;
            ArraySegment<byte> lastSegment;
            if (last == 0 || !ReferenceEquals((lastSegment = this.Committed[last]).Array, this.current.Array))
            {
                // The last segment was not committed, so it needs to be handled separately to the committed segments.
                if (lastSegment.Count > 0)
                {
                    ArrayPool<byte>.Shared.Return(lastSegment.Array);
                }
            }

            for (var i = this.segmentStartIndex; i < this.Committed.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(this.Committed[i].Array);
            }

            this.current = EmptySegment;
            this.Committed.Clear();
        }
    }

    internal sealed class MemoryBufferWriter : IDisposable, IBufferWriter<byte>
    {
        private readonly int _minimumSegmentSize;
        private int _bytesWritten;

        private List<ArraySegment<byte>> _completedSegments;
        private byte[] _currentSegment;
        private int _position;

        public MemoryBufferWriter(int minimumSegmentSize = 4096)
        {
            _minimumSegmentSize = minimumSegmentSize;
        }

        public void Reset()
        {
            if (_completedSegments != null)
            {
                for (var i = 0; i < _completedSegments.Count; i++)
                {
                    ArrayPool<byte>.Shared.Return(_completedSegments[i].Array);
                }

                _completedSegments.Clear();
            }

            if (_currentSegment != null)
            {
                ArrayPool<byte>.Shared.Return(_currentSegment);
                _currentSegment = null;
            }

            _bytesWritten = 0;
            _position = 0;
        }

        public void Advance(int count)
        {
            _bytesWritten += count;
            _position += count;
        }

        public int BytesWritten => _bytesWritten;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);

            return _currentSegment.AsMemory(_position, _currentSegment.Length - _position);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);

            return _currentSegment.AsSpan(_position, _currentSegment.Length - _position);
        }
        
        private void EnsureCapacity(int sizeHint)
        {
            // This does the Right Thing. It only subtracts _position from the current segment length if it's non-null.
            // If _currentSegment is null, it returns 0.
            var remainingSize = _currentSegment?.Length - _position ?? 0;

            // If the sizeHint is 0, any capacity will do
            // Otherwise, the buffer must have enough space for the entire size hint, or we need to add a segment.
            if (sizeHint == 0 && remainingSize > 0 || sizeHint > 0 && remainingSize >= sizeHint)
            {
                // We have capacity in the current segment
                return;
            }

            AddSegment(sizeHint);
        }

        private void AddSegment(int sizeHint = 0)
        {
            if (_currentSegment != null)
            {
                // We're adding a segment to the list
                if (_completedSegments == null)
                {
                    _completedSegments = new List<ArraySegment<byte>>();
                }

                // Position might be less than the segment length if there wasn't enough space to satisfy the sizeHint when
                // GetMemory was called. In that case we'll take the current segment and call it "completed", but need to
                // ignore any empty space in it.

                IncludeCurrentSegment();
            }

            // Get a new buffer using the minimum segment size, unless the size hint is larger than a single segment.
            _currentSegment = ArrayPool<byte>.Shared.Rent(Math.Max(_minimumSegmentSize, sizeHint));
            _position = 0;
        }

        private void IncludeCurrentSegment()
        {
            var last = this._completedSegments.Count - 1;
            if (last >= 0 && ReferenceEquals(this._completedSegments[last].Array, _currentSegment))
            {
                // Extend the existing committed segment.
                _completedSegments[last] = new ArraySegment<byte>(_currentSegment, 0, _position);
            }
            else
            {
                _completedSegments.Add(new ArraySegment<byte>(_currentSegment, 0, _position));
            }
        }

        public List<ArraySegment<byte>> GetSegments()
        {
            if (_completedSegments == null)
            {
                _completedSegments = new List<ArraySegment<byte>>(1);
            }

            IncludeCurrentSegment();

            return _completedSegments;
        }

        public byte[] ToArray()
        {
            if (_currentSegment == null)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[_bytesWritten];

            var totalWritten = 0;

            if (_completedSegments != null)
            {
                // Copy full segments
                var count = _completedSegments.Count;
                for (var i = 0; i < count; i++)
                {
                    var segment = _completedSegments[i];
                    segment.Array.CopyTo(result.AsSpan(totalWritten));
                    totalWritten += segment.Count;
                }
            }

            // Copy current incomplete segment
            _currentSegment.AsSpan(0, _position).CopyTo(result.AsSpan(totalWritten));

            return result;
        }

        public void CopyTo(Span<byte> span)
        {
            Debug.Assert(span.Length >= _bytesWritten);

            if (_currentSegment == null)
            {
                return;
            }

            var totalWritten = 0;

            if (_completedSegments != null)
            {
                // Copy full segments
                var count = _completedSegments.Count;
                for (var i = 0; i < count; i++)
                {
                    var segment = _completedSegments[i];
                    segment.Array.CopyTo(span.Slice(totalWritten));
                    totalWritten += segment.Count;
                }
            }

            // Copy current incomplete segment
            _currentSegment.AsSpan(0, _position).CopyTo(span.Slice(totalWritten));

            Debug.Assert(_bytesWritten == totalWritten + _position);
        }

        public void Dispose()
        {
            this.Dispose(true);
        }
        
        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                Reset();
            }
        }

        /// <summary>
        /// Holds a byte[] from the pool and a size value. Basically a Memory but guaranteed to be backed by an ArrayPool byte[], so that we know we can return it.
        /// </summary>
        private readonly struct CompletedBuffer
        {
            public byte[] Buffer { get; }
            public int Length { get; }

            public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

            public CompletedBuffer(byte[] buffer, int length)
            {
                Buffer = buffer;
                Length = length;
            }

            public void Return()
            {
                ArrayPool<byte>.Shared.Return(Buffer);
            }
        }
    }

    public static class ReadOnlySequenceHelper
    {
        public static ReadOnlySequence<byte> ToReadOnlySequence(this IEnumerable<Memory<byte>> buffers)
        {
            return ReadOnlyBufferSegment.Create(buffers);
        }

        public static ReadOnlySequence<byte> CreateReadOnlySequence(params byte[][] buffers)
        {
            if (buffers.Length == 1)
            {
                return new ReadOnlySequence<byte>(buffers[0]);
            }

            var list = new List<Memory<byte>>();
            foreach (var buffer in buffers)
            {
                list.Add(buffer);
            }

            return ToReadOnlySequence(list.ToArray());
        }

        public static ReadOnlySequence<byte> CreateReadOnlySequence(List<ArraySegment<byte>> buffers, int offset, int length)
        {
            return ReadOnlyBufferSegment.Create(buffers, offset, length);
        }

        public static ReadOnlySequence<byte> CreateReadOnlySequence(List<ArraySegment<byte>> buffers)
        {
            return ReadOnlyBufferSegment.Create(buffers);
        }

        private class ReadOnlyBufferSegment : ReadOnlySequenceSegment<byte>
        {
            public static ReadOnlySequence<byte> Create(List<ArraySegment<byte>> buffers)
            {
                ReadOnlyBufferSegment segment = null;
                ReadOnlyBufferSegment first = null;
                foreach (var buffer in buffers)
                {
                    var newSegment = new ReadOnlyBufferSegment
                    {
                        Memory = buffer,
                    };

                    if (segment != null)
                    {
                        segment.Next = newSegment;
                        newSegment.RunningIndex = segment.RunningIndex + segment.Memory.Length;
                    }
                    else
                    {
                        first = newSegment;
                    }

                    segment = newSegment;
                }

                if (first == null)
                {
                    first = segment = new ReadOnlyBufferSegment();
                }

                return new ReadOnlySequence<byte>(first, 0, segment, segment.Memory.Length);
            }

            public static ReadOnlySequence<byte> Create(IEnumerable<Memory<byte>> buffers)
            {
                ReadOnlyBufferSegment segment = null;
                ReadOnlyBufferSegment first = null;
                foreach (var buffer in buffers)
                {
                    var newSegment = new ReadOnlyBufferSegment
                    {
                        Memory = buffer,
                    };

                    if (segment != null)
                    {
                        segment.Next = newSegment;
                        newSegment.RunningIndex = segment.RunningIndex + segment.Memory.Length;
                    }
                    else
                    {
                        first = newSegment;
                    }

                    segment = newSegment;
                }

                if (first == null)
                {
                    first = segment = new ReadOnlyBufferSegment();
                }

                return new ReadOnlySequence<byte>(first, 0, segment, segment.Memory.Length);
            }

            public static ReadOnlySequence<byte> Create(List<ArraySegment<byte>> buffers, int offset, int length)
            {
                ReadOnlyBufferSegment segment = null;
                ReadOnlyBufferSegment first = null;
                var lengthSoFar = 0;
                var countSoFar = 0;
                foreach (var buffer in buffers)
                {
                    var bytesStillToSkip = Math.Max(0, offset - lengthSoFar);
                    lengthSoFar += buffer.Count;

                    if (buffer.Count <= bytesStillToSkip) // Still skipping past this buffer
                    {
                        continue;
                    }
                    
                    var segmentLength = Math.Min(length - countSoFar, buffer.Count - bytesStillToSkip);
                    var newSegment = new ReadOnlyBufferSegment
                    {
                        Memory = new Memory<byte>(buffer.Array, buffer.Offset + bytesStillToSkip, segmentLength)
                    };

                    countSoFar += segmentLength;

                    if (segment != null)
                    {
                        segment.Next = newSegment;
                        newSegment.RunningIndex = segment.RunningIndex + segmentLength;
                    }
                    else
                    {
                        first = newSegment;
                    }

                    segment = newSegment;

                    if (countSoFar == length)
                    {
                        break;
                    }
                }

                if (first == null)
                {
                    first = segment = new ReadOnlyBufferSegment();
                }

                return new ReadOnlySequence<byte>(first, 0, segment, segment.Memory.Length);
            }
        }
    }

    internal class ArrayBufferWriter : IBufferWriter<byte>, IDisposable
    { 
        private ResizableArray<byte> buffer;

        public ArrayBufferWriter(int capacity = 1024)
        {
            this.buffer = new ResizableArray<byte>(ArrayPool<byte>.Shared.Rent(capacity));
        }

        public int CommitedByteCount => this.buffer.Count;

        public void Clear()
        {
            this.buffer.Count = 0;
        }

        public ArraySegment<byte> Free => this.buffer.Free;

        public ArraySegment<byte> Formatted => this.buffer.Full;

        public byte[] ToArray() => this.Formatted.AsMemory().ToArray();

        public Memory<byte> GetMemory(int minimumLength = 0)
        {
            if (minimumLength < 1)
            {
                minimumLength = 1;
            }

            if (minimumLength > this.buffer.FreeCount)
            {
                int doubleCount = this.buffer.FreeCount * 2;
                int newSize = minimumLength > doubleCount ? minimumLength : doubleCount;
                byte[] newArray = ArrayPool<byte>.Shared.Rent(newSize + this.buffer.Count);
                byte[] oldArray = this.buffer.Resize(newArray);
                ArrayPool<byte>.Shared.Return(oldArray);
            }

            return this.buffer.FreeMemory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetSpan(int minimumLength = 0)
        {
            if (minimumLength < 1)
            {
                minimumLength = 1;
            }

            if (minimumLength > this.buffer.FreeCount)
            {
                int doubleCount = this.buffer.FreeCount * 2;
                int newSize = minimumLength > doubleCount ? minimumLength : doubleCount;
                byte[] newArray = ArrayPool<byte>.Shared.Rent(newSize + this.buffer.Count);
                byte[] oldArray = this.buffer.Resize(newArray);
                ArrayPool<byte>.Shared.Return(oldArray);
            }

            return this.buffer.FreeSpan;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int bytes)
        {
            this.buffer.Count += bytes;
            if (this.buffer.Count > this.buffer.Capacity)
            {
                throw new InvalidOperationException("More bytes commited than returned from FreeBuffer");
            }
        }

        public void Dispose()
        {
            byte[] array = this.buffer.Array;
            this.buffer.Array = null;
            ArrayPool<byte>.Shared.Return(array);
        }

        private struct ResizableArray<T>
        {
            public ResizableArray(T[] array, int count = 0)
            {
                this.Array = array;
                this.Count = count;
            }

            public T[] Array { get; set; }

            public int Count { get; set; }

            public int Capacity => this.Array.Length;

            public T[] Resize(T[] newArray)
            {
                T[] oldArray = this.Array;
                this.Array.AsSpan(0, this.Count).CopyTo(newArray);  // CopyTo will throw if newArray.Length < _count
                this.Array = newArray;
                return oldArray;
            }

            public ArraySegment<T> Full => new ArraySegment<T>(this.Array, 0, this.Count);

            public ArraySegment<T> Free => new ArraySegment<T>(this.Array, this.Count, this.Array.Length - this.Count);

            public Span<T> FreeSpan => new Span<T>(this.Array, this.Count, this.Array.Length - this.Count);

            public Memory<T> FreeMemory => new Memory<T>(this.Array, this.Count, this.Array.Length - this.Count);

            public int FreeCount => this.Array.Length - this.Count;
        }
    }

    /// <summary>
    /// Writer for Orleans binary token streams
    /// </summary>
    public sealed class BinaryTokenStreamWriter2<TBufferWriter> : IBinaryTokenStreamWriter where TBufferWriter : IBufferWriter<byte>
    {
        private static readonly Dictionary<RuntimeTypeHandle, SerializationTokenType> typeTokens;
        private static readonly Dictionary<RuntimeTypeHandle, Action<BinaryTokenStreamWriter2<TBufferWriter>, object>> writers;

        private TBufferWriter output;
        private Memory<byte> currentBuffer;
        private int currentOffset;
        private int completedLength;

        static BinaryTokenStreamWriter2()
        {
            typeTokens = new Dictionary<RuntimeTypeHandle, SerializationTokenType>(RuntimeTypeHandlerEqualityComparer.Instance);
            typeTokens[typeof(bool).TypeHandle] = SerializationTokenType.Boolean;
            typeTokens[typeof(int).TypeHandle] = SerializationTokenType.Int;
            typeTokens[typeof(uint).TypeHandle] = SerializationTokenType.Uint;
            typeTokens[typeof(short).TypeHandle] = SerializationTokenType.Short;
            typeTokens[typeof(ushort).TypeHandle] = SerializationTokenType.Ushort;
            typeTokens[typeof(long).TypeHandle] = SerializationTokenType.Long;
            typeTokens[typeof(ulong).TypeHandle] = SerializationTokenType.Ulong;
            typeTokens[typeof(byte).TypeHandle] = SerializationTokenType.Byte;
            typeTokens[typeof(sbyte).TypeHandle] = SerializationTokenType.Sbyte;
            typeTokens[typeof(float).TypeHandle] = SerializationTokenType.Float;
            typeTokens[typeof(double).TypeHandle] = SerializationTokenType.Double;
            typeTokens[typeof(decimal).TypeHandle] = SerializationTokenType.Decimal;
            typeTokens[typeof(string).TypeHandle] = SerializationTokenType.String;
            typeTokens[typeof(char).TypeHandle] = SerializationTokenType.Character;
            typeTokens[typeof(Guid).TypeHandle] = SerializationTokenType.Guid;
            typeTokens[typeof(DateTime).TypeHandle] = SerializationTokenType.Date;
            typeTokens[typeof(TimeSpan).TypeHandle] = SerializationTokenType.TimeSpan;
            typeTokens[typeof(GrainId).TypeHandle] = SerializationTokenType.GrainId;
            typeTokens[typeof(ActivationId).TypeHandle] = SerializationTokenType.ActivationId;
            typeTokens[typeof(SiloAddress).TypeHandle] = SerializationTokenType.SiloAddress;
            typeTokens[typeof(ActivationAddress).TypeHandle] = SerializationTokenType.ActivationAddress;
            typeTokens[typeof(IPAddress).TypeHandle] = SerializationTokenType.IpAddress;
            typeTokens[typeof(IPEndPoint).TypeHandle] = SerializationTokenType.IpEndPoint;
            typeTokens[typeof(CorrelationId).TypeHandle] = SerializationTokenType.CorrelationId;
            typeTokens[typeof(InvokeMethodRequest).TypeHandle] = SerializationTokenType.Request;
            typeTokens[typeof(Response).TypeHandle] = SerializationTokenType.Response;
            typeTokens[typeof(Dictionary<string, object>).TypeHandle] = SerializationTokenType.StringObjDict;
            typeTokens[typeof(Object).TypeHandle] = SerializationTokenType.Object;
            typeTokens[typeof(List<>).TypeHandle] = SerializationTokenType.List;
            typeTokens[typeof(SortedList<,>).TypeHandle] = SerializationTokenType.SortedList;
            typeTokens[typeof(Dictionary<,>).TypeHandle] = SerializationTokenType.Dictionary;
            typeTokens[typeof(HashSet<>).TypeHandle] = SerializationTokenType.Set;
            typeTokens[typeof(SortedSet<>).TypeHandle] = SerializationTokenType.SortedSet;
            typeTokens[typeof(KeyValuePair<,>).TypeHandle] = SerializationTokenType.KeyValuePair;
            typeTokens[typeof(LinkedList<>).TypeHandle] = SerializationTokenType.LinkedList;
            typeTokens[typeof(Stack<>).TypeHandle] = SerializationTokenType.Stack;
            typeTokens[typeof(Queue<>).TypeHandle] = SerializationTokenType.Queue;
            typeTokens[typeof(Tuple<>).TypeHandle] = SerializationTokenType.Tuple + 1;
            typeTokens[typeof(Tuple<,>).TypeHandle] = SerializationTokenType.Tuple + 2;
            typeTokens[typeof(Tuple<,,>).TypeHandle] = SerializationTokenType.Tuple + 3;
            typeTokens[typeof(Tuple<,,,>).TypeHandle] = SerializationTokenType.Tuple + 4;
            typeTokens[typeof(Tuple<,,,,>).TypeHandle] = SerializationTokenType.Tuple + 5;
            typeTokens[typeof(Tuple<,,,,,>).TypeHandle] = SerializationTokenType.Tuple + 6;
            typeTokens[typeof(Tuple<,,,,,,>).TypeHandle] = SerializationTokenType.Tuple + 7;

            writers = new Dictionary<RuntimeTypeHandle, Action<BinaryTokenStreamWriter2<TBufferWriter>, object>>(RuntimeTypeHandlerEqualityComparer.Instance);
            writers[typeof(bool).TypeHandle] = (stream, obj) => stream.Write((bool)obj);
            writers[typeof(int).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Int); stream.Write((int)obj); };
            writers[typeof(uint).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Uint); stream.Write((uint)obj); };
            writers[typeof(short).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Short); stream.Write((short)obj); };
            writers[typeof(ushort).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Ushort); stream.Write((ushort)obj); };
            writers[typeof(long).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Long); stream.Write((long)obj); };
            writers[typeof(ulong).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Ulong); stream.Write((ulong)obj); };
            writers[typeof(byte).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Byte); stream.Write((byte)obj); };
            writers[typeof(sbyte).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Sbyte); stream.Write((sbyte)obj); };
            writers[typeof(float).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Float); stream.Write((float)obj); };
            writers[typeof(double).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Double); stream.Write((double)obj); };
            writers[typeof(decimal).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Decimal); stream.Write((decimal)obj); };
            writers[typeof(string).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.String); stream.Write((string)obj); };
            writers[typeof(char).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Character); stream.Write((char)obj); };
            writers[typeof(Guid).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Guid); stream.Write((Guid)obj); };
            writers[typeof(DateTime).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.Date); stream.Write((DateTime)obj); };
            writers[typeof(TimeSpan).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.TimeSpan); stream.Write((TimeSpan)obj); };
            writers[typeof(GrainId).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.GrainId); stream.Write((GrainId)obj); };
            writers[typeof(ActivationId).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.ActivationId); stream.Write((ActivationId)obj); };
            writers[typeof(SiloAddress).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.SiloAddress); stream.Write((SiloAddress)obj); };
            writers[typeof(ActivationAddress).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.ActivationAddress); stream.Write((ActivationAddress)obj); };
            writers[typeof(IPAddress).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.IpAddress); stream.Write((IPAddress)obj); };
            writers[typeof(IPEndPoint).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.IpEndPoint); stream.Write((IPEndPoint)obj); };
            writers[typeof(CorrelationId).TypeHandle] = (stream, obj) => { stream.Write(SerializationTokenType.CorrelationId); stream.Write((CorrelationId)obj); };
        }
        
        public BinaryTokenStreamWriter2(TBufferWriter output)
        {
            this.output = output;
            this.currentBuffer = output.GetMemory();
            this.currentOffset = default;
            this.completedLength = default;
        }

        /// <summary> Current write position in the stream. </summary>
        public int CurrentOffset { get { return this.Length; } }

        /// <summary>
        /// Commit the currently written buffers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Commit()
        {
            this.output.Advance(this.currentOffset);
            this.completedLength += this.currentOffset;
            this.currentBuffer = default;
            this.currentOffset = default;
        }

        public void Write(decimal d)
        {
            this.Write(Decimal.GetBits(d));
        }
        
        public void Write(string s)
        {
            if (null == s)
            {
                this.Write(-1);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                this.Write(bytes.Length);
                this.Write(bytes);
            }
        }
        
        public void Write(char c)
        {
            this.Write(Convert.ToInt16(c));
        }
        
        public void Write(bool b)
        {
            this.Write((byte)(b ? SerializationTokenType.True : SerializationTokenType.False));
        }
        
        public void WriteNull()
        {
            this.Write((byte)SerializationTokenType.Null);
        }
        
        public void WriteTypeHeader(Type t, Type expected = null)
        {
            if (t == expected)
            {
                this.Write((byte)SerializationTokenType.ExpectedType);
                return;
            }

            this.Write((byte)SerializationTokenType.SpecifiedType);

            if (t.IsArray)
            {
                this.Write((byte)(SerializationTokenType.Array + (byte)t.GetArrayRank()));
                this.WriteTypeHeader(t.GetElementType());
                return;
            }

            SerializationTokenType token;
            if (typeTokens.TryGetValue(t.TypeHandle, out token))
            {
                this.Write((byte)token);
                return;
            }

            if (t.GetTypeInfo().IsGenericType)
            {
                if (typeTokens.TryGetValue(t.GetGenericTypeDefinition().TypeHandle, out token))
                {
                    this.Write((byte)token);
                    foreach (var tp in t.GetGenericArguments())
                    {
                        this.WriteTypeHeader(tp);
                    }
                    return;
                }
            }

            this.Write((byte)SerializationTokenType.NamedType);
            var typeKey = t.OrleansTypeKey();
            this.Write(typeKey.Length);
            this.Write(typeKey);
        }
                
        public void Write(byte[] b, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if ((offset == 0) && (count == b.Length))
            {
                this.Write(b);
            }
            else
            {
                var temp = new byte[count];
                Buffer.BlockCopy(b, offset, temp, 0, count);
                this.Write(temp);
            }
        }
        
        public void Write(IPEndPoint ep)
        {
            this.Write(ep.Address);
            this.Write(ep.Port);
        }
        
        public void Write(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                for (var i = 0; i < 12; i++)
                {
                    this.Write((byte)0);
                }
                this.Write(ip.GetAddressBytes()); // IPv4 -- 4 bytes
            }
            else
            {
                this.Write(ip.GetAddressBytes()); // IPv6 -- 16 bytes
            }
        }
        
        public void Write(SiloAddress addr)
        {
            this.Write(addr.Endpoint);
            this.Write(addr.Generation);
        }
        
        public void Write(TimeSpan ts)
        {
            this.Write(ts.Ticks);
        }

        public void Write(DateTime dt)
        {
            this.Write(dt.ToBinary());
        }

        public void Write(Guid id)
        {
            this.Write(id.ToByteArray());
        }

        /// <summary>
        /// Try to write a simple type (non-array) value to the stream.
        /// </summary>
        /// <param name="obj">Input object to be written to the output stream.</param>
        /// <returns>Returns <c>true</c> if the value was successfully written to the output stream.</returns>
        public bool TryWriteSimpleObject(object obj)
        {
            if (obj == null)
            {
                this.WriteNull();
                return true;
            }
            Action<BinaryTokenStreamWriter2<TBufferWriter>, object> writer;
            if (writers.TryGetValue(obj.GetType().TypeHandle, out writer))
            {
                writer(this, obj);
                return true;
            }
            return false;
        }

        public int Length => this.currentOffset + this.completedLength;

        private Span<byte> WritableSpan => this.currentBuffer.Slice(this.currentOffset).Span;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureContiguous(int length)
        {
            // The current buffer is adequate.
            if (this.currentOffset + length < this.currentBuffer.Length) return;

            // The current buffer is inadequate, allocate another.
            this.Allocate(length);
#if DEBUG
            // Throw if the allocation does not satisfy the request.
            if (this.currentBuffer.Length < length) ThrowTooLarge(length);

            void ThrowTooLarge(int l) => throw new InvalidOperationException($"Requested buffer length {l} cannot be satisfied by the writer.");
#endif
        }

        public void Allocate(int length)
        {
            // Commit the bytes which have been written.
            this.output.Advance(this.currentOffset);

            // Request a new buffer with at least the requested number of available bytes.
            this.currentBuffer = this.output.GetMemory(length);

            // Update internal state for the new buffer.
            this.completedLength += this.currentOffset;
            this.currentOffset = 0;
        }

        public void Write(byte[] array)
        {
            // Fast path, try copying to the current buffer.
            if (array.Length <= this.currentBuffer.Length - this.currentOffset)
            {
                array.CopyTo(this.WritableSpan);
                this.currentOffset += array.Length;
            }
            else
            {
                var value = new ReadOnlySpan<byte>(array);
                this.WriteMultiSegment(in value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ReadOnlySpan<byte> value)
        {
            // Fast path, try copying to the current buffer.
            if (value.Length <= this.currentBuffer.Length - this.currentOffset)
            {
                value.CopyTo(this.WritableSpan);
                this.currentOffset += value.Length;
            }
            else
            {
                this.WriteMultiSegment(in value);
            }
        }

        private void WriteMultiSegment(in ReadOnlySpan<byte> source)
        {
            var input = source;
            while (true)
            {
                // Write as much as possible/necessary into the current segment.
                var writeSize = Math.Min(this.currentBuffer.Length - this.currentOffset, input.Length);
                input.Slice(0, writeSize).CopyTo(this.WritableSpan);
                this.currentOffset += writeSize;

                input = input.Slice(writeSize);

                if (input.Length == 0) return;

                // The current segment is full but there is more to write.
                this.Allocate(input.Length);
            }
        }

        public void Write(List<ArraySegment<byte>> b)
        {
            foreach (var segment in b)
            {
                this.Write(segment);
            }
        }

        public void Write(short[] array)
        {
            this.Write(MemoryMarshal.Cast<short, byte>(array));
        }

        public void Write(int[] array)
        {
            this.Write(MemoryMarshal.Cast<int, byte>(array));
        }

        public void Write(long[] array)
        {
            this.Write(MemoryMarshal.Cast<long, byte>(array));
        }

        public void Write(ushort[] array)
        {
            this.Write(MemoryMarshal.Cast<ushort, byte>(array));
        }

        public void Write(uint[] array)
        {
            this.Write(MemoryMarshal.Cast<uint, byte>(array));
        }

        public void Write(ulong[] array)
        {
            this.Write(MemoryMarshal.Cast<ulong, byte>(array));
        }

        public void Write(sbyte[] array)
        {
            this.Write(MemoryMarshal.Cast<sbyte, byte>(array));
        }

        public void Write(char[] array)
        {
            this.Write(MemoryMarshal.Cast<char, byte>(array));
        }

        public void Write(bool[] array)
        {
            this.Write(MemoryMarshal.Cast<bool, byte>(array));
        }

        public void Write(float[] array)
        {
            this.Write(MemoryMarshal.Cast<float, byte>(array));
        }

        public void Write(double[] array)
        {
            this.Write(MemoryMarshal.Cast<double, byte>(array));
        }

        public void Write(byte b)
        {
            const int width = sizeof(byte);
            this.EnsureContiguous(width);
            this.WritableSpan[0] = b;
            this.currentOffset += width;
        }

        public void Write(sbyte b)
        {
            const int width = sizeof(sbyte);
            this.EnsureContiguous(width);
            this.WritableSpan[0] = (byte)b;
            this.currentOffset += width;
        }

        public void Write(float i)
        {
            ReadOnlySpan<float> span = stackalloc float[1] { i };
            this.Write(MemoryMarshal.Cast<float, byte>(span));
        }

        public void Write(double i)
        {
            ReadOnlySpan<double> span = stackalloc double[1] { i };
            this.Write(MemoryMarshal.Cast<double, byte>(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(short value)
        {
            const int width = sizeof(short);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteInt16LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int value)
        {
            const int width = sizeof(int);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteInt32LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(long value)
        {
            const int width = sizeof(long);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteInt64LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint value)
        {
            const int width = sizeof(uint);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteUInt32LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ushort value)
        {
            const int width = sizeof(ushort);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteUInt16LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ulong value)
        {
            const int width = sizeof(ulong);
            this.EnsureContiguous(width);
            BinaryPrimitives.WriteUInt64LittleEndian(this.WritableSpan, value);
            this.currentOffset += width;
        }
    }
}
