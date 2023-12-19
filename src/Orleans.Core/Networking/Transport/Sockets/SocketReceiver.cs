// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport.Sockets;

internal sealed class SocketReceiver : SocketAwaitableEventArgs
{
    public readonly byte[] Array = GC.AllocateArray<byte>(32 * 1024, pinned: true);
    public SocketReceiver()
    {
        SetBuffer(MemoryMarshal.CreateFromPinnedArray(Array, 0, Array.Length));
    }

    public ValueTask ReceiveAsync(Socket socket)
    {
        if (socket.ReceiveAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }
}
