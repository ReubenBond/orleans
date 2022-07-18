// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

internal sealed class SocketReceiver : SocketAwaitableEventArgs
{
    public SocketReceiver()
    {
    }

    public ValueTask WaitForDataAsync(Socket socket)
    {
        SetBuffer(Memory<byte>.Empty);

        if (socket.ReceiveAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }

    public ValueTask ReceiveAsync(Socket socket, Memory<byte> buffer)
    {
        SetBuffer(buffer);

        if (socket.ReceiveAsync(this))
        {
            return new ValueTask(this, 0);
        }

        return Error is not null ? ValueTask.FromException(Error) : default;
    }
}
