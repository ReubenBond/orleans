namespace Orleans.Durable.Storage.Engine.Buffers;

/// <summary>
/// A write-only <see cref="Stream"/> which appends to an <see cref="ArcBufferWriter"/>, through which a storage SDK
/// which writes a download to a stream fills pooled pages (buffers.md §2).
/// </summary>
/// <remarks>
/// The writer stays its caller's: the stream neither consumes nor disposes it, and every call after the stream's
/// disposal throws <see cref="ObjectDisposedException"/>.
/// </remarks>
public sealed class ArcBufferWriteStream(ArcBufferWriter writer) : Stream
{
    private long _written;
    private bool _disposed;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _written;
        }

        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Reads <paramref name="source"/> to its end straight into <paramref name="writer"/>'s pages.
    /// </summary>
    public static async ValueTask CopyFromAsync(Stream source, ArcBufferWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);
        while (true)
        {
            var read = await source.ReadAsync(writer.GetMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            writer.AdvanceWriter(read);
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        writer.Write(buffer);
        _written += buffer.Length;
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
