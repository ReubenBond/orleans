// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Orleans.Networking.Shared;

internal static class KestrelMemoryPool
{
    public static MemoryPool<byte> Create()
    {
        return CreateSlabMemoryPool();
    }

    public static MemoryPool<byte> CreateSlabMemoryPool()
    {
        return new SlabMemoryPool();
    }

    public static readonly int MinimumSegmentSize = 4096;
}
