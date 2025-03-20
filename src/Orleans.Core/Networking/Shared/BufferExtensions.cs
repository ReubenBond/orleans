// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Orleans.Networking.Shared
{
    internal static class BufferExtensions
    {
        public static ArraySegment<byte> GetArray(this Memory<byte> memory) => ((ReadOnlyMemory<byte>)memory).GetArray();

        public static ArraySegment<byte> GetArray(this ReadOnlyMemory<byte> memory)
        {
            if (!MemoryMarshal.TryGetArray(memory, out var result))
            {
                ThrowInvalid();
            }

            return result;
            void ThrowInvalid() => throw new InvalidOperationException("Buffer backed by array was expected");
        }
    }
}
