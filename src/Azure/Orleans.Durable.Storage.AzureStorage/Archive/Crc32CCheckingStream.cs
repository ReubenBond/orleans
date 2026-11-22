using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.AzureStorage.Archive;

/// <summary>
/// A read-only stream over an upload's content which accumulates the CRC-32C of the bytes it hands out, from the start,
/// and, once a read reaches the end, throws if they do not match the writer's (archive LSM §9).
/// </summary>
/// <remarks>
/// The SDK reads the content once to compute its <c>Content-MD5</c>, before any request, then rewinds and sends it. The
/// CRC-32C accumulates over that first pass, so the MD5 the service checks covers exactly the bytes whose CRC-32C
/// matched, and a mismatch throws before any request leaves. Once a pass has matched, later passes, the send and any
/// retry, go unchecked: the MD5 covers them.
/// </remarks>
/// <param name="inner">The content, which the caller keeps and disposes.</param>
/// <param name="name">The object's name.</param>
/// <param name="crc32c">The writer's CRC-32C of the content.</param>
internal sealed class Crc32CCheckingStream(Stream inner, ObjectName name, uint crc32c) : Stream
{
    private readonly Crc32 _crc = new(Crc32ParameterSet.Crc32C);

    /// <summary>Whether the reads so far run contiguously from the start, so the CRC-32C covers them all.</summary>
    private bool _fromStart = true;

    /// <summary>Whether a pass reached the end with a matching CRC-32C.</summary>
    private bool _matched;

    /// <inheritdoc/>
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => inner.Length;

    /// <inheritdoc/>
    public override long Position
    {
        get => inner.Position;
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
        var read = inner.Read(buffer);
        Observe(buffer[..read], requested: !buffer.IsEmpty);
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read], requested: !buffer.IsEmpty);
        return read;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = inner.Seek(offset, origin);
        _crc.Reset();
        _fromStart = position == 0;
        return position;
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Adds the bytes a read handed out to the CRC-32C and, once the reads from the start reach the end, checks it.
    /// </summary>
    /// <exception cref="ObjectContentChangedException">The bytes from the start to the end do not match the writer's CRC-32C.</exception>
    private void Observe(ReadOnlySpan<byte> read, bool requested)
    {
        if (_matched || !_fromStart)
        {
            return;
        }

        _crc.Append(read);
        var ended = requested && read.IsEmpty;
        if (ended || inner.Position == inner.Length)
        {
            if (_crc.GetCurrentHashAsUInt32() != crc32c)
            {
                throw ObjectContentChangedException.For(name);
            }

            _matched = true;
        }
    }
}
