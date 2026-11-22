using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Wal.IoUring;

/// <summary>
/// Stores write-ahead logs through Linux io_uring.
/// </summary>
/// <remarks>
/// <para>
/// One engine, with one ring and one issuing thread, serves every log using this storage. Each log and named file is
/// opened for direct I/O (<c>O_DIRECT</c>, <see cref="WalDirectIo"/>): a write group is one vectored
/// <c>IORING_OP_WRITEV</c>, which reports its own transfer errors, followed by <c>IORING_OP_FSYNC</c> with
/// <c>IORING_FSYNC_DATASYNC</c>, which makes its bytes and the file length it needs durable and reports its own flush
/// error. Overlapping groups complete independently. Reads are direct <c>IORING_OP_READV</c>, aligned
/// to <see cref="WalDirectIo.Alignment"/>, which return the device's bytes.
/// </para>
/// <para>
/// The storage requires Linux 6.1 or later on a little-endian 64-bit process: the ring uses
/// <c>IORING_SETUP_SINGLE_ISSUER</c> and <c>IORING_SETUP_DEFER_TASKRUN</c>. Constructing it where io_uring cannot be
/// set up throws <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class IoUringWalStorage : WalStorage, IDisposable
{

    /// <summary>
    /// Initializes a new instance of the <see cref="IoUringWalStorage"/> class.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">io_uring cannot be set up.</exception>
    public IoUringWalStorage()
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("IoUringWalStorage requires a little-endian 64-bit Linux process.");
        }

        Engine = new IoUringEngine();
    }

    /// <inheritdoc/>
    public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        FileWalDirectory.Create(path, WalFileSystem.Os, cancellationToken);
        return ValueTask.CompletedTask;
    }

#pragma warning disable CA2000 // Ownership of directories and files transfers to the caller.
    /// <inheritdoc/>
    public override ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult<WalDirectory>(FileWalDirectory.Open(path, WalFileSystem.Os, OpenFile, cancellationToken));

    /// <inheritdoc/>
    public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        FileWalDirectory.Delete(path, WalFileSystem.Os, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult(FileWalDirectory.ListDirectories(path, WalFileSystem.Os, cancellationToken));

    /// <inheritdoc/>
    public override ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult(FileWalDirectory.Measure(path, WalFileSystem.Os, cancellationToken));

    /// <summary>
    /// Stops the engine once every submitted operation completes.
    /// </summary>
    public void Dispose() => Engine.Dispose();

    /// <summary>
    /// Gets or sets a hook which may fail a file's truncation before it changes the file, given the file's path, so a test
    /// reaches the truncation's report; production code leaves it unset.
    /// </summary>
    internal Func<string, Exception?>? TruncateFault { get; set; }

    /// <summary>
    /// Gets the engine, whose fault hooks a test sets.
    /// </summary>
    internal IoUringEngine Engine { get; }

    /// <summary>
    /// Opens a file for direct I/O through the engine, first making every byte it holds durable when
    /// <paramref name="flush"/> is set.
    /// </summary>
    internal WalFile OpenFile(string path, bool flush)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = WalDirectIo.OpenLinux(path, dataSync: false);
            if (flush)
            {
                // A previous process's writes may not yet be durable; reads must observe only durable bytes (WAL-I10).
                RandomAccess.FlushToDisk(handle);
            }

            return new IoUringWalFile(this, handle, path, RandomAccess.GetLength(handle));
        }
        catch (Exception exception)
        {
            // The report comes first, so nothing the process does after a failure, closing the file included, precedes
            // the stop (conventions.md §14).
            if (WalFileSystem.IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.IoUringOpen, path, exception);
            }

            handle?.Dispose();
            throw;
        }
    }
#pragma warning restore CA2000

    private sealed class IoUringWalFile(IoUringWalStorage storage, SafeFileHandle handle, string path, long length) : WalFile
    {
        private long _length = length;

        public override long Length => Interlocked.Read(ref _length);

        public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<ArcBuffer> buffers)
        {
            var operation = IoUringOperation.Write(handle, offset, buffers);
            storage.Engine.Submit(operation);
            long written;
            try
            {
                written = await operation.Completion.ConfigureAwait(false);
            }
            catch (Exception exception) when (WalFileSystem.IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.IoUringWrite, path, exception);
                throw;
            }

            var end = offset + written;
            var current = Interlocked.Read(ref _length);
            while (end > current)
            {
                var observed = Interlocked.CompareExchange(ref _length, end, current);
                if (observed == current)
                {
                    break;
                }

                current = observed;
            }
        }

        public override async ValueTask<ArcBuffer> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var writer = ArcBufferWriter.CreateAligned();
            var pages = new List<Memory<byte>>();
            var start = WalDirectIo.RentAligned(writer, offset, length, pages);
            var operation = IoUringOperation.Read(handle, start, pages);
            storage.Engine.Submit(operation);
            long read;
            try
            {
                read = await operation.Completion.ConfigureAwait(false);
            }
            catch (Exception exception) when (WalFileSystem.IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.IoUringRead, path, exception);
                throw;
            }

            return WalDirectIo.Take(writer, start, offset, length, read);
        }

        public override ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (storage.TruncateFault?.Invoke(path) is { } fault)
                {
                    throw fault;
                }

                RandomAccess.SetLength(handle, length);
                RandomAccess.FlushToDisk(handle);
            }
            catch (Exception exception) when (WalFileSystem.IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.IoUringTruncate, path, exception);
                throw;
            }

            _ = Interlocked.Exchange(ref _length, length);
            return ValueTask.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            handle.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
