#nullable enable

using System;

namespace Orleans.Connections.Transport;

public abstract class ReadRequest
{
    public abstract Memory<byte> Buffer { get; }
    public abstract bool OnRead(int bytesRead);
    public abstract void OnError(Exception error);
}
