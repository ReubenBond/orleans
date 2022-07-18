#nullable enable

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.Networking.Transport;

public readonly struct ReadResult
{
    public int BytesRead { get; init; }
};

public readonly struct WriteResult
{
    public int BytesWritten { get; init; }
};

public abstract class ReadRequest
{
    public abstract Memory<byte> Buffer { get; }

    // returns true if the request is complete
    public abstract bool OnProgress(int bytesRead);
    public abstract void OnError(Exception error);

}

public abstract class WriteRequest
{
    public bool IsSingleBuffer { get; set; }
    public abstract ReadOnlyMemory<byte> Buffer { get; }
    public abstract ReadOnlySequence<byte> Buffers { get; } 
    public abstract void OnCompleted();
    public abstract void OnError(Exception error);
}

internal abstract class NetworkTransport : IAsyncDisposable
{
    public abstract bool ReadAsync(ReadRequest request);
    public abstract bool WriteAsync(WriteRequest request);
    public abstract ValueTask CloseAsync(Exception? closeException);
    public abstract ValueTask DisposeAsync();
    public abstract CancellationToken Closed { get; }
}
