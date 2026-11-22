using System.Buffers;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over a <see cref="ReadOnlySequence{T}"/> its caller lends for one call,
/// through which a store hands content to the Azure SDK (conventions.md §1).
/// </summary>
/// <remarks>
/// The store disposes the stream when its call returns, and reads and disposal are serialized, so a read the SDK makes
/// after the call fails with <see cref="ObjectDisposedException"/> rather than reading bytes the caller has since
/// released. A read continues from a cursor where the previous one stopped, so reading the content takes time linear in
/// its length and its segments.
/// </remarks>
internal sealed class SequenceReadStream(ReadOnlySequence<byte> content) : Stream
{
    private readonly Lock _sync = new();
    private SequencePosition _cursor = content.Start;
    private long _position;
    private bool _disposed;

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return content.Length;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }

        set => _ = Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rest = content.Slice(_cursor);
            var count = (int)Math.Min(rest.Length, buffer.Length);
            rest.Slice(0, count).CopyTo(buffer);
            _cursor = content.GetPosition(count, _cursor);
            _position += count;
            return count;
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer.AsSpan(offset, count)));
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(content.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(target, content.Length, nameof(offset));
            _cursor = content.GetPosition(target);
            _position = target;
            return target;
        }
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sync)
            {
                _disposed = true;
            }
        }

        base.Dispose(disposing);
    }
}
