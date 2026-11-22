using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Wal.Portable;

/// <summary>
/// Stores write-ahead logs with positioned file I/O on every platform.
/// </summary>
/// <remarks>
/// A write completes after its bytes and the length needed to read them are durable. On Linux and Windows, each log and
/// named file is one handle opened for direct, write-through I/O (<see cref="WalDirectIo"/>): each write reports its
/// own errors and its bytes are durable once it completes, the file is synced only when a write extends it, and reads
/// return the device's bytes. On macOS, which serves development and tests, I/O is buffered and every write is followed
/// by <c>F_FULLFSYNC</c>. New names are made durable with a directory flush: <c>fsync</c> on Linux,
/// <c>F_FULLFSYNC</c> on macOS, and <c>FlushFileBuffers</c> on a directory handle on Windows.
/// </remarks>
public sealed class PortableWalStorage : WalStorage
{
    /// <inheritdoc/>
    public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        FileWalDirectory.Create(path, WalFileSystem.Os, cancellationToken);
        return ValueTask.CompletedTask;
    }

#pragma warning disable CA2000 // Ownership of the directory transfers to the caller.
    /// <inheritdoc/>
    public override ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult<WalDirectory>(FileWalDirectory.Open(path, WalFileSystem.Os, OpenFile, cancellationToken));
#pragma warning restore CA2000

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

    private static WalFile OpenFile(string path, bool flush) =>
        PortableWalFile.Open(WalFileSystem.Os, path, flush, direct: !OperatingSystem.IsMacOS());
}
