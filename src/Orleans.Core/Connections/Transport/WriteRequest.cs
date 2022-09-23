#nullable enable

using System;
using System.Buffers;

namespace Orleans.Networking.Transport;

public abstract class WriteRequest
{
    public bool IsSingleBuffer { get; set; }
    public abstract ReadOnlyMemory<byte> Buffer { get; }
    public abstract ReadOnlySequence<byte> Buffers { get; }
    public abstract void OnCompleted();
    public abstract void OnError(Exception error);
}
