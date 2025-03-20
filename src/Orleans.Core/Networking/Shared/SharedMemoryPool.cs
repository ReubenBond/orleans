// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Orleans.Networking.Shared;

internal sealed class SharedMemoryPool
{
    public MemoryPool<byte> Pool { get; } = KestrelMemoryPool.Create();
}
