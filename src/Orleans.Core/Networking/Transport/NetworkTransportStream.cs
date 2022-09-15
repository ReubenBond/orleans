#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Networking.Transport;

internal class NetworkTransportStream : Stream
{
    private readonly MemoryPool<byte> _memoryPool;
    private readonly NetworkTransport _transport;
    private readonly StreamWriteRequest _writeRequest;
    private readonly StreamReadRequest _readRequest;

    public NetworkTransportStream(NetworkTransport transport, MemoryPool<byte> memoryPool)
    {
        _transport = transport;
        _memoryPool = memoryPool;
        _writeRequest = new();
        _readRequest = new();
    }

    public override bool CanTimeout => true;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));
    public override void Write(byte[] buffer, int offset, int count) => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override int Read(Span<byte> buffer) => base.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _readRequest.SetBuffer(buffer);
        _transport.ReadAsync(_readRequest);
        return _readRequest.OnProgressAsync();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        // TODO: rent once and reuse, only returning on dispose / to rent a larger buffer / to restore a standard-sized buffer (in the case of huge writes)
        using var bytes = _memoryPool.Rent(buffer.Length);
        buffer.CopyTo(bytes.Memory.Span);
        WriteAsync(bytes.Memory, CancellationToken.None).AsTask().Wait();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _writeRequest.SetBuffer(buffer);
        if (!_transport.WriteAsync(_writeRequest))
        {
            return ValueTask.FromException(new ObjectDisposedException("Network transport is unable to satisfy the request"));
        }

        // Wait for the request to complete;
        return _writeRequest.OnCompleteAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Flush() { }

    private sealed class StreamWriteRequest : WriteRequest
    {
        private readonly SingleWaiterInlineSignal _signal = new();
        private ReadOnlyMemory<byte> _buffer;
        public StreamWriteRequest()
        {
            IsSingleBuffer = true;
        }

        public Exception? Error { get; private set; }
        public override ReadOnlySequence<byte> Buffers => throw null!;
        public void SetBuffer(ReadOnlyMemory<byte> buffer) => _buffer = buffer;
        public override ReadOnlyMemory<byte> Buffer => _buffer;
        public ValueTask OnCompleteAsync() => _signal.WaitAsync();
        public override void OnCompleted() => _signal.Signal();
        public override void OnError(Exception error) => _signal.SetException(error);
    }

    private sealed class StreamReadRequest : ReadRequest
    {
        private readonly UnsafeInlineSignal<int> _signal = new();
        private Memory<byte> _buffer;
        public void SetBuffer(Memory<byte> buffer) => _buffer = buffer;
        public override Memory<byte> Buffer => _buffer;

        public override bool OnProgress(int bytesRead)
        {
            _signal.SetResult(bytesRead);
            return true;
        }

        public ValueTask<int> OnProgressAsync() => _signal.WaitAsync();
        public override void OnError(Exception error) => _signal.SetException(error);
    }
}
