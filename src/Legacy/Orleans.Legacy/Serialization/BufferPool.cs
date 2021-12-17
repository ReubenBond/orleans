using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Orleans.Configuration;
namespace Orleans.Legacy.Runtime
{
    internal class BufferPool
    {
        private readonly int byteBufferSize;
        private readonly int maxBuffersCount;
        private readonly bool limitBuffersCount;
        private readonly ConcurrentBag<byte[]> buffers;

        private int currentBufferCount;

        public static readonly BufferPool GlobalPool = new BufferPool(bufferSize: 4 * 1024, maxBuffers: 10_000, preallocationSize: 250, "Global");

        public int Size
        {
            get { return byteBufferSize; }
        }

        public int Count
        {
            get { return buffers.Count; }
        }

        public string Name
        {
            get;
            private set;
        }

        /// <summary>
        /// Creates a buffer pool.
        /// </summary>
        /// <param name="bufferSize">The size, in bytes, of each buffer.</param>
        /// <param name="maxBuffers">The maximum number of buffers to keep around, unused; by default, the number of unused buffers is unbounded.</param>
        /// <param name="preallocationSize">Initial number of buffers to allocate.</param>
        /// <param name="name">Name of the buffer pool.</param>
        private BufferPool(int bufferSize, int maxBuffers, int preallocationSize, string name)
        {
            Name = name;
            byteBufferSize = bufferSize;
            maxBuffersCount = maxBuffers;
            limitBuffersCount = maxBuffers > 0;
            buffers = new ConcurrentBag<byte[]>();

            if (preallocationSize <= 0) return;

            var dummy = GetMultiBuffer(preallocationSize * Size);
            Release(dummy);
        }

        public byte[] GetBuffer()
        {
            byte[] buffer;
            if (!buffers.TryTake(out buffer))
            {
                buffer = new byte[byteBufferSize];
            }
            else if (limitBuffersCount)
            {
                Interlocked.Decrement(ref currentBufferCount);
            }

            return buffer;
        }

        public List<ArraySegment<byte>> GetMultiBuffer(int totalSize)
        {
            var list = new List<ArraySegment<byte>>();
            while (totalSize > 0)
            {
                var buff = GetBuffer();
                list.Add(new ArraySegment<byte>(buff, 0, Math.Min(byteBufferSize, totalSize)));
                totalSize -= byteBufferSize;
            }
            return list;
        }

        public void Release(byte[] buffer)
        {
            if (buffer.Length == byteBufferSize)
            {
                if (limitBuffersCount && currentBufferCount > maxBuffersCount)
                {
                    return;
                }

                buffers.Add(buffer);

                if (limitBuffersCount)
                {
                    Interlocked.Increment(ref currentBufferCount);
                }
            }
        }

        public void Release(List<ArraySegment<byte>> list)
        {
            if (list == null) return;

            foreach (var segment in list)
            {
                Release(segment.Array);
            }
        }
    }
}
