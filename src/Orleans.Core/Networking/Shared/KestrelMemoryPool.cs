using System.Buffers;

namespace Orleans.Networking.Shared
{
    public static class KestrelMemoryPool
    {
        public static MemoryPool<byte> Create()
        {
            return CreateSlabMemoryPool();
        }

        public static MemoryPool<byte> CreateSlabMemoryPool() => ReferenceCountingPinnedMemoryPool.Shared;

        public static readonly int MinimumSegmentSize = 4096;
    }
}
