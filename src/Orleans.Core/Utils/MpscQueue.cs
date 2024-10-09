#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace BitFaster.Caching.Buffers
{
    internal static class BitOps
    {
        /// <summary>
        /// Calculate the smallest power of 2 greater than the input parameter.
        /// </summary>
        /// <param name="x">The input parameter.</param>
        /// <returns>Smallest power of two greater than or equal to x.</returns>
        public static int CeilingPowerOfTwo(int x) => (int)CeilingPowerOfTwo((uint)x);

        /// <summary>
        /// Calculate the smallest power of 2 greater than the input parameter.
        /// </summary>
        /// <param name="x">The input parameter.</param>
        /// <returns>Smallest power of two greater than or equal to x.</returns>
        public static uint CeilingPowerOfTwo(uint x) => 1u << -BitOperations.LeadingZeroCount(x - 1);
    }

    internal static class Padding
    {
#if TARGET_ARM64 || TARGET_LOONGARCH64
        internal const int CACHE_LINE_SIZE = 128;
#else
        internal const int CACHE_LINE_SIZE = 64;
#endif
    }

    [DebuggerDisplay("Head = {Head}, Tail = {Tail}")]
    [StructLayout(LayoutKind.Explicit, Size = 3 * Padding.CACHE_LINE_SIZE)] // padding before/between/after fields
    internal struct PaddedHeadAndTail
    {
        [FieldOffset(1 * Padding.CACHE_LINE_SIZE)] public int Head;
        [FieldOffset(2 * Padding.CACHE_LINE_SIZE)] public int Tail;
    }

    /// <summary>
    /// Specifies the status of buffer operations.
    /// </summary>
    internal enum BufferStatus
    {
        /// <summary>
        /// The buffer is full.
        /// </summary>
        Full,

        /// <summary>
        /// The buffer is empty.
        /// </summary>
        Empty,

        /// <summary>
        /// The buffer operation succeeded.
        /// </summary>
        Success,

        /// <summary>
        /// The buffer operation was contended.
        /// </summary>
        Contended,
    }

    internal sealed class MpscQueue<T> where T : class
    {
        private readonly object _crossSegmentLock = new();
        private Segment<T> _head; 
        private Segment<T> _tail;

        public MpscQueue() : this(16)
        {
        }

        public MpscQueue(int capacity)
        {
            _head = _tail = new Segment<T>(capacity);
        }

        public bool TryTake([NotNullWhen(true)] out T? item)
        {
            SpinWait spinWait = default;

            while (true)
            {
                var head = Volatile.Read(ref _head);
                var status = head.TryTake(out item);
                if (status == BufferStatus.Success)
                {
                    Debug.Assert(item is not null);
                    return true;
                }
                else if (status == BufferStatus.Empty)
                {
                    var next = Volatile.Read(ref head.NextSegment);
                    if (next is null)
                    {
                        return false;
                    }

                    // Advance to the next, larger buffer.
                    _head = next;
                }
                else if (status == BufferStatus.Contended)
                {
                    spinWait.SpinOnce();
                }
            }
        }

        public bool TryPeek([NotNullWhen(true)] out T? item)
        {
            SpinWait spinWait = default;

            while (true)
            {
                var head = Volatile.Read(ref _head);
                var status = head.TryPeek(out item);
                if (status == BufferStatus.Success)
                {
                    Debug.Assert(item is not null);
                    return true;
                }
                else if (status == BufferStatus.Empty)
                {
                    var next = Volatile.Read(ref head.NextSegment);
                    if (next is null)
                    {
                        return false;
                    }

                    // Advance to the next, larger buffer.
                    _head = next;
                }
                else if (status == BufferStatus.Contended)
                {
                    spinWait.SpinOnce();
                }
            }
        }

        public void ConsumePeeked() => _head.ConsumePeeked();

        public void Enqueue(T item)
        {
            var spinWait = new SpinWait();
            while (true)
            {
                var tail = Volatile.Read(ref _tail);
                var status = tail.TryAdd(item);
                switch (status)
                {
                    case BufferStatus.Success:
                        return;
                    case BufferStatus.Contended:
                        spinWait.SpinOnce();
                        break;
                    case BufferStatus.Full:
                        // Add an new buffer with double the capacity.
                        lock (_crossSegmentLock)
                        {
                            tail = Volatile.Read(ref _tail);
                            if (tail.NextSegment is not null) continue;
                            _tail = tail.NextSegment = new Segment<T>(tail.Capacity * 2);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// Provides a multi-producer, single-consumer thread-safe ring buffer. When the buffer is full,
    /// TryAdd fails and returns false. When the buffer is empty, TryTake fails and returns false.
    /// </summary>
    /// Based on the BoundedBuffer class in the Caffeine library by ben.manes@gmail.com (Ben Manes).
    [DebuggerDisplay("Count = {Count}/{Capacity}")]
    internal sealed class Segment<T> where T : class
    {
        private readonly T?[] _buffer;
        private readonly int _mask;
        private PaddedHeadAndTail _headAndTail; // mutable struct, don't mark readonly

        // The next queue segment
        public Segment<T>? NextSegment;

        /// <summary>
        /// Initializes a new segment with the specified bounded capacity.
        /// </summary>
        /// <param name="capacity">The bounded length.</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public Segment(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 0);

            // must be power of 2 to use & slotsMask instead of %
            capacity = BitOps.CeilingPowerOfTwo(capacity);

            _buffer = new T[capacity];
            _mask = capacity - 1;
        }

        /// <summary>
        /// The bounded capacity.
        /// </summary>
        public int Capacity => _buffer.Length;

        /// <summary>
        /// Gets the number of items contained in the buffer.
        /// </summary>
        public int Count
        {
            get
            {
                var spinner = new SpinWait();
                while (true)
                {
                    var headNow = Volatile.Read(ref _headAndTail.Head);
                    var tailNow = Volatile.Read(ref _headAndTail.Tail);

                    if (headNow == Volatile.Read(ref _headAndTail.Head) &&
                        tailNow == Volatile.Read(ref _headAndTail.Tail))
                    {
                        return GetCount(headNow, tailNow);
                    }

                    spinner.SpinOnce();
                }
            }
        }

        private int GetCount(int head, int tail)
        {
            if (head != tail)
            {
                head &= _mask;
                tail &= _mask;

                return head < tail ? tail - head : _buffer.Length - head + tail;
            }
            return 0;
        }

        /// <summary>
        /// Tries to add the specified item.
        /// </summary>
        /// <param name="item">The item to be added.</param>
        /// <returns>A BufferStatus value indicating whether the operation succeeded.</returns>
        /// <remarks>
        /// Thread safe.
        /// </remarks>
        public BufferStatus TryAdd(T item)
        {
            var head = Volatile.Read(ref _headAndTail.Head);
            var tail = _headAndTail.Tail;
            var size = tail - head;

            if (size >= _buffer.Length)
            {
                return BufferStatus.Full;
            }

            if (Interlocked.CompareExchange(ref _headAndTail.Tail, tail + 1, tail) == tail)
            {
                var index = tail & _mask;
                Volatile.Write(ref _buffer[index], item);

                return BufferStatus.Success;
            }

            return BufferStatus.Contended;
        }

        /// <summary>
        /// Tries to remove an item.
        /// </summary>
        /// <param name="item">The item to be removed.</param>
        /// <returns>A BufferStatus value indicating whether the operation succeeded.</returns>
        /// <remarks>
        /// Thread safe for single try take/drain + multiple try add.
        /// </remarks>
        public BufferStatus TryTake(out T? item)
        {
            var head = Volatile.Read(ref _headAndTail.Head);
            var tail = _headAndTail.Tail;
            var size = tail - head;

            if (size == 0)
            {
                item = default;
                return BufferStatus.Empty;
            }

            var index = head & _mask;

            item = Volatile.Read(ref _buffer[index]);

            if (item == null)
            {
                // not published yet
                return BufferStatus.Contended;
            }

            _buffer[index] = null;
            Volatile.Write(ref _headAndTail.Head, ++head);
            return BufferStatus.Success;
        }

        /// <summary>
        /// Tries to peek an item.
        /// </summary>
        /// <param name="item">The item to be peeked.</param>
        /// <returns>A BufferStatus value indicating whether the operation succeeded.</returns>
        /// <remarks>
        /// Thread safe for single try take/drain + multiple try add.
        /// </remarks>
        public BufferStatus TryPeek(out T? item)
        {
            var head = Volatile.Read(ref _headAndTail.Head);
            var tail = _headAndTail.Tail;
            var size = tail - head;

            if (size == 0)
            {
                item = default;
                return BufferStatus.Empty;
            }

            var index = head & _mask;

            item = Volatile.Read(ref _buffer[index]);

            if (item == null)
            {
                // not published yet
                return BufferStatus.Contended;
            }

            _buffer[index] = null;
            return BufferStatus.Success;
        }

        /// <summary>
        /// Consume the previously peeked item.
        /// </summary>
        public void ConsumePeeked()
        {
            var head = Volatile.Read(ref _headAndTail.Head);
            Volatile.Write(ref _headAndTail.Head, ++head);
        }

        /// <summary>
        /// Drains the buffer into the specified array segment.
        /// </summary>
        /// <param name="output">The output buffer</param>
        /// <returns>The number of items written to the output buffer.</returns>
        /// <remarks>
        /// Thread safe for single try take/drain + multiple try add.
        /// </remarks>
        public int DrainTo(ArraySegment<T> output) => DrainTo(output.AsSpan());

        /// <summary>
        /// Drains the buffer into the specified span.
        /// </summary>
        /// <param name="output">The output buffer</param>
        /// <returns>The number of items written to the output buffer.</returns>
        /// <remarks>
        /// Thread safe for single try take/drain + multiple try add.
        /// </remarks>
        public int DrainTo(Span<T> output) => DrainToImpl(output);

        // use an outer wrapper method to force the JIT to inline the inner adaptor methods
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DrainToImpl(Span<T> output)
        {
            var head = Volatile.Read(ref _headAndTail.Head);
            var tail = _headAndTail.Tail;
            var size = tail - head;

            if (size == 0)
            {
                return 0;
            }

            Span<T?> localBuffer = _buffer;

            var outCount = 0;

            do
            {
                var index = head & _mask;

                var item = Volatile.Read(ref localBuffer[index]);

                if (item == null)
                {
                    // not published yet
                    break;
                }

                localBuffer[index] = null;
                Write(output, outCount++, item);
                head++;
            }
            while (head != tail && outCount < Length(output));

            _headAndTail.Head = head;

            return outCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Write(Span<T> output, int index, T item) => output[index] = item;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Length(Span<T> output) => output.Length;

        /// <summary>
        /// Removes all values from the buffer.
        /// </summary>
        /// <remarks>
        /// Clear must be called from the single consumer thread.
        /// </remarks>
        public void Clear()
        {
            while (TryTake(out _) != BufferStatus.Empty)
            {
            }
        }
    }
}
