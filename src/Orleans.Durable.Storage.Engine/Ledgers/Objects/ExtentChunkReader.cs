using System.Buffers;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// Sequential 8 MiB ranged reader over one extent (object-ledgers.md §8.4), caching
/// <see cref="ReadOnlySequence{T}"/> once per fetched chunk, yielding zero-copy <see cref="ArcBuffer"/> slices
/// whenever a batch lies within one chunk, and releasing its active chunk on disposal.
/// </summary>
internal sealed class ExtentChunkReader(
    IObjectStore store,
    ObjectName name,
    long startOffset,
    long endOffset,
    SemaphoreSlim? window) : IDisposable
{
    private bool _hasChunk;
    private ArcBuffer _chunk;
    private ReadOnlySequence<byte> _chunkSequence;
    private int _chunkOffset;
    private long _nextFetchOffset = startOffset;

    public long Position { get; private set; } = startOffset;

    public long Remaining => endOffset - Position;

    /// <summary>Gets a monotonically increasing generation incremented each time a new ranged chunk is fetched.</summary>
    public int ChunkGeneration { get; private set; }

    public async ValueTask<byte?> PeekLeadByteAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return _chunkSequence.Slice(_chunkOffset, 1).FirstSpan[0];
    }

    public async ValueTask<bool> ReadBytesAsync(byte[] destination, CancellationToken cancellationToken)
    {
        var copied = 0;
        while (copied < destination.Length)
        {
            if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var available = _chunk.Length - _chunkOffset;
            var take = Math.Min(available, destination.Length - copied);
            _chunkSequence.Slice(_chunkOffset, take).CopyTo(destination.AsSpan(copied, take));
            _chunkOffset += take;
            Position += take;
            copied += take;
        }

        return true;
    }

    public async ValueTask<bool> SkipBytesAsync(int length, CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var available = _chunk.Length - _chunkOffset;
            var take = Math.Min(available, remaining);
            _chunkOffset += take;
            Position += take;
            remaining -= take;
        }

        return true;
    }

    public async ValueTask<ArcBuffer?> ReadBufferAsync(int length, CancellationToken cancellationToken)
    {
        if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var available = _chunk.Length - _chunkOffset;
        if (available >= length)
        {
            // Zero-copy slice out of the current 8 MiB chunk.
            var slice = _chunk.Slice(_chunkOffset, length);
            _chunkOffset += length;
            Position += length;
            return slice;
        }

        using var writer = new ArcBufferWriter(length);
        var remaining = length;
        while (remaining > 0)
        {
            if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var avail = _chunk.Length - _chunkOffset;
            var take = Math.Min(avail, remaining);
            writer.Write(_chunkSequence.Slice(_chunkOffset, take));
            _chunkOffset += take;
            Position += take;
            remaining -= take;
        }

        return writer.ConsumeSlice(writer.Length);
    }

    public void Dispose()
    {
        if (_hasChunk)
        {
            _chunk.Dispose();
            _chunk = default;
            _chunkSequence = default;
            _hasChunk = false;
        }
    }

    private async ValueTask<bool> EnsureBufferedAsync(CancellationToken cancellationToken)
    {
        if (_hasChunk && _chunkOffset < _chunk.Length)
        {
            return true;
        }

        if (_hasChunk)
        {
            _chunk.Dispose();
            _chunk = default;
            _chunkSequence = default;
            _hasChunk = false;
            _chunkOffset = 0;
        }

        if (_nextFetchOffset >= endOffset)
        {
            return false;
        }

        var toRead = (int)Math.Min(AppendObjectLedgerStorage.ReadRangeChunkBytes, endOffset - _nextFetchOffset);
        if (window is not null)
        {
            await window.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        ArcBuffer? fetched;
        try
        {
            fetched = await store.ReadAsync(name, _nextFetchOffset, toRead, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        finally
        {
            _ = window?.Release();
        }

        if (fetched is null || fetched.Value.Length == 0)
        {
            fetched?.Dispose();
            return false;
        }

        _chunk = fetched.Value;
        _chunkSequence = _chunk.AsReadOnlySequence();
        _hasChunk = true;
        _chunkOffset = 0;
        _nextFetchOffset += _chunk.Length;
        ChunkGeneration += 1;
        return true;
    }
}
