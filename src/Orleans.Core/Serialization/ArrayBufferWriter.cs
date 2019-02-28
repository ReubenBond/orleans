using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization
{
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
}
