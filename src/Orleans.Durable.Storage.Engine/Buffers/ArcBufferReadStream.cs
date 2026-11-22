using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Buffers;

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over an <see cref="ArcBuffer"/>, through which a provider hands a payload to
/// a storage SDK (buffers.md §2).
/// </summary>
/// <remarks>
/// <para>
/// The stream retains its own lease on the buffer and releases it when it is disposed. Reads and disposal are
/// serialized: a read completes before the stream releases its lease, and every read after disposal throws
/// <see cref="ObjectDisposedException"/>, so no read copies from a page the stream released, and a send which an SDK
/// still makes after its caller disposed the stream fails rather than sending other bytes.
/// </para>
/// <para>
/// A read continues from a cursor over the buffer's pages where the previous one stopped, so reading a payload of
/// <c>s</c> segments in <c>r</c> reads takes <c>O(s + r)</c> segment steps whatever the reads' sizes; a seek moves the
/// cursor from the first page.
/// </para>
/// </remarks>
public sealed class ArcBufferReadStream : Stream
{
    private readonly Lock _sync = new();
    private readonly ArcBuffer _content;
    private ArcBuffer.PageSegmentEnumerator _pages;
    private ArcBuffer.PageSegment _segment;
    private int _segmentOffset;
    private long _position;
    private bool _disposed;
    private Task<int>? _lastRead;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArcBufferReadStream"/> class over <paramref name="content"/>, on which
    /// it retains its own lease; the caller keeps its own and disposes it when it chooses.
    /// </summary>
    /// <param name="content">The payload.</param>
    public ArcBufferReadStream(ArcBuffer content)
    {
        _content = content.Retain();
        _pages = _content.PageSegments;
    }

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
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _content.Length;
            }
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
            var read = 0;
            while (!buffer.IsEmpty)
            {
                if (_segmentOffset == _segment.Length)
                {
                    if (!_pages.MoveNext())
                    {
                        break;
                    }

                    _segment = _pages.Current;
                    _segmentOffset = 0;
                    continue;
                }

                var count = Math.Min(_segment.Length - _segmentOffset, buffer.Length);
                _segment.Span.Slice(_segmentOffset, count).CopyTo(buffer);
                _segmentOffset += count;
                _position += count;
                buffer = buffer[count..];
                read += count;
            }

            return read;
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        cancellationToken.ThrowIfCancellationRequested();

        // An SDK which reads through Stream's array overload reads in equal chunks, so the completed task repeats.
        var read = Read(buffer.AsSpan(offset, count));
        var last = _lastRead;
        return last is not null && last.Result == read ? last : _lastRead = Task.FromResult(read);
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
                SeekOrigin.End => checked(_content.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(target, _content.Length, nameof(offset));
            _pages = _content.PageSegments;
            _segment = default;
            _segmentOffset = 0;
            for (var skip = target; skip > 0;)
            {
                if (!_pages.MoveNext())
                {
                    throw new UnreachableException("A target within the payload lies in one of its pages.");
                }

                _segment = _pages.Current;
                _segmentOffset = (int)Math.Min(skip, _segment.Length);
                skip -= _segmentOffset;
            }

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
                if (!_disposed)
                {
                    _disposed = true;
                    _content.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }
}
