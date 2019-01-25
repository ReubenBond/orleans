using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal sealed class BufferPool
    {
        private readonly int minimumBufferSize;
        public static BufferPool GlobalPool;

        private readonly ArrayPool<byte> pool;

        public int MinimumSize => this.minimumBufferSize;

        internal static void InitGlobalBufferPool(MessagingOptions messagingOptions)
        {
            GlobalPool = new BufferPool(messagingOptions.BufferPoolMinimumBufferSize);
        }

        /// <summary>
        /// Creates a buffer pool.
        /// </summary>
        /// <param name="minimumBufferSize">The minimum size, in bytes, of each buffer.</param>
        private BufferPool(int minimumBufferSize)
        {
            this.minimumBufferSize = minimumBufferSize;
            this.pool = ArrayPool<byte>.Create();
        }
        
        public byte[] GetBuffer()
        {
            byte[] buffer = this.pool.Rent(this.minimumBufferSize);
            return buffer;
        }

        public byte[] GetBuffer(int minimumSize)
        {
            byte[] buffer = this.pool.Rent(minimumSize);
            return buffer;
        }

        public List<ArraySegment<byte>> GetMultiBuffer(int totalSize)
        {
            var list = new List<ArraySegment<byte>>();
            while (totalSize > 0)
            {
                var buff = this.pool.Rent(totalSize);
                list.Add(new ArraySegment<byte>(buff, 0, Math.Min(buff.Length, totalSize)));
                totalSize -= this.minimumBufferSize;
            }
            return list;
        }

        public void Release(byte[] buffer) => this.pool.Return(buffer);

        public void Release(List<ArraySegment<byte>> list)
        {
            if (list == null) return;

            foreach (var segment in list)
            {
                this.Release(segment.Array);
            }
        }
    }
}
