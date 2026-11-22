using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Wal.Portable;

/// <summary>
/// The portable storage's log and named files: positioned file I/O through <see cref="WalFileSystem"/>.
/// </summary>
/// <remarks>
/// A write completes once its bytes, and the length needed to read them, are durable. A direct file, on Linux and
/// Windows, is one handle opened for direct, write-through I/O: each write reports its own errors and its bytes are
/// durable once it completes, so the file is synced only when a write extends it, and reads return the device's bytes
/// (<see cref="WalDirectIo"/>). Otherwise, on macOS, I/O is buffered and every write is followed by a sync of its file.
/// Reads are aligned to <see cref="WalDirectIo.Alignment"/> in either case.
/// </remarks>
internal sealed class PortableWalFile : WalFile
{
    private readonly IWalFileSystemFile _handle;
    private long _length;
    private long _syncedLength;

    private PortableWalFile(IWalFileSystemFile handle, bool direct)
    {
        _handle = handle;
        Direct = direct;
        _length = _syncedLength = handle.Length;
    }

    public override long Length => Interlocked.Read(ref _length);

    /// <summary>
    /// Gets a value indicating whether the file was opened for direct, write-through I/O.
    /// </summary>
    internal bool Direct { get; }

    /// <summary>
    /// Opens a file of <paramref name="fileSystem"/>, first making every byte it holds durable when
    /// <paramref name="flush"/> is set, for direct, write-through I/O when <paramref name="direct"/> is set.
    /// </summary>
    public static WalFile Open(WalFileSystem fileSystem, string path, bool flush, bool direct)
    {
        var handle = fileSystem.OpenFile(path, direct);
        try
        {
            if (flush)
            {
                // A previous process's writes may not yet be durable; reads must observe only durable bytes (WAL-I10).
                handle.Flush();
            }

            return new PortableWalFile(handle, direct);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<ArcBuffer> buffers)
    {
        var memories = new List<ReadOnlyMemory<byte>>();
        long length = 0;
        foreach (var buffer in buffers.Span)
        {
            foreach (var memory in buffer.MemorySegments)
            {
                memories.Add(memory);
                length += memory.Length;
            }
        }

        Debug.Assert(!Direct || offset % WalDirectIo.Alignment == 0 && length % WalDirectIo.Alignment == 0, "A direct write is whole aligned pages.");
        await _handle.WriteAsync(memories, offset, CancellationToken.None).ConfigureAwait(false);
        var end = offset + length;
        UpdateMaximum(ref _length, end);

        // A direct write's bytes are durable once it completes; a sync is still needed for a new file length.
        if (!Direct || end > Interlocked.Read(ref _syncedLength))
        {
            _handle.Flush();
            UpdateMaximum(ref _syncedLength, end);
        }
    }

    public override async ValueTask<ArcBuffer> ReadAsync(long offset, int length, CancellationToken cancellationToken)
    {
        using var writer = ArcBufferWriter.CreateAligned();
        var pages = new List<Memory<byte>>();
        var start = WalDirectIo.RentAligned(writer, offset, length, pages);
        long total = 0;
        for (var first = 0; first < pages.Count;)
        {
            var read = await _handle.ReadAsync(pages[first..], start + total, cancellationToken).ConfigureAwait(false);
            total += read;

            // A read returns fewer bytes than whole pages only at the end of the file.
            if (read == 0 || total % WalDirectIo.Alignment != 0)
            {
                break;
            }

            first = (int)(total / WalDirectIo.Alignment);
        }

        return WalDirectIo.Take(writer, start, offset, length, total);
    }

    public override ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _handle.SetLength(length);
        _handle.Flush();
        _ = Interlocked.Exchange(ref _length, length);
        _ = Interlocked.Exchange(ref _syncedLength, length);
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
