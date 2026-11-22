using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Tests.LazyFs;

/// <summary>
/// A buffered <see cref="WalStorage"/> over <see cref="PortableWalFile"/> (<c>direct: false</c>) for Linux FUSE
/// filesystems such as LazyFS that do not support <c>O_DIRECT</c> / <c>STATX_DIOALIGN</c>.
/// </summary>
/// <remarks>
/// Every write enters the FUSE page cache through buffered <c>pwrite</c> and immediately invokes
/// <c>RandomAccess.FlushToDisk</c> (<c>fdatasync</c>/<c>fsync</c>), while directory creations, renames, and deletions
/// issue directory <c>fsync</c> through <see cref="FileWalDirectory"/>. Any write or rename missing its flush is
/// dropped when LazyFS clears its unsynced page cache.
/// </remarks>
internal sealed class BufferedPortableWalStorage : WalStorage
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
        PortableWalFile.Open(WalFileSystem.Os, path, flush, direct: false);
}
