using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.MetaStore.Tests;

/// <summary>
/// A <see cref="WalFileSystem"/> which passes every call to a <see cref="RecordingFileSystem"/>, and can run work just
/// before a file is read and fail directory flushes and renames, so a test places another process's step, or a device
/// error, at the point it names.
/// </summary>
/// <remarks>
/// An injected failure records nothing: the operation it replaces never reaches the file system. The seam reports it,
/// which stops the process there (conventions.md §14), so the inner view crashes and every later call through it throws.
/// </remarks>
internal sealed class InterceptingFileSystem(RecordingFileSystem inner) : WalFileSystem
{
    /// <summary>
    /// Gets or sets work to run once, just before the next file read, with the read file's path.
    /// </summary>
    public Func<string, Task>? BeforeNextRead { get; set; }

    /// <summary>
    /// Gets or sets which directory flushes fail with <see cref="IOException"/>, by path.
    /// </summary>
    public Func<string, bool>? FailDirectoryFlush { get; set; }

    /// <summary>
    /// Gets or sets how many of the next renames fail with <see cref="IOException"/>.
    /// </summary>
    public int FailMoves { get; set; }

    /// <summary>
    /// Gets or sets how many of the next lock opens fail with <see cref="IOException"/>, as a lock on a file system
    /// without working locks fails, rather than answering that another owner holds the lock.
    /// </summary>
    public int FailLocks { get; set; }

    internal override bool DirectoryExistsCore(string path) => inner.DirectoryExistsCore(path);

    internal override void CreateDirectoryCore(string path) => inner.CreateDirectoryCore(path);

    internal override void DeleteDirectoryCore(string path) => inner.DeleteDirectoryCore(path);

    internal override bool TryDeleteEmptyDirectoryCore(string path) => inner.TryDeleteEmptyDirectoryCore(path);

    internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern) => inner.EnumerateFilesCore(path, searchPattern);

    internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) =>
        inner.EnumerateFilesWithLastWriteTimesCore(path, includeName);

    internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff) => inner.TryDeleteFileWrittenBeforeCore(path, cutoff);

    internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => inner.EnumerateDirectoriesCore(path);

    internal override bool FileExistsCore(string path) => inner.FileExistsCore(path);

    internal override IDisposable OpenLockCore(string path)
    {
        InterceptLock();
        return inner.OpenLockCore(path);
    }

    internal override IDisposable OpenLockForDeletionCore(string path)
    {
        InterceptLock();
        return inner.OpenLockForDeletionCore(path);
    }

    internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize) =>
        inner.CreateFileCore(path, replace, writeThrough, preallocationSize);

    internal override IWalFileSystemFile OpenFileCore(string path, bool direct) => inner.OpenFileCore(path, direct);

    internal override void MoveFileCore(string source, string destination, bool replace)
    {
        if (FailMoves > 0)
        {
            FailMoves--;
            throw Stop("An injected rename failure.");
        }

        inner.MoveFileCore(source, destination, replace);
    }

    internal override void LinkFileCore(string existing, string link) => inner.LinkFileCore(existing, link);

    internal override async ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (BeforeNextRead is { } before)
        {
            BeforeNextRead = null;
            await before(path);
        }

        return await inner.ReadFileCoreAsync(path, cancellationToken);
    }

    internal override void DeleteFileCore(string path) => inner.DeleteFileCore(path);

    internal override bool TryDeleteLockFileCore(string path) => inner.TryDeleteLockFileCore(path);

    internal override void FlushDirectoryCore(string path, DirectoryEntries entries)
    {
        if (FailDirectoryFlush?.Invoke(path) == true)
        {
            throw Stop("An injected directory flush failure.");
        }

        inner.FlushDirectoryCore(path, entries);
    }

    private void InterceptLock()
    {
        if (FailLocks > 0)
        {
            FailLocks--;
            throw Stop("An injected lock failure.");
        }
    }

    /// <summary>
    /// Crashes the inner view, where the report of the injected failure stops the process, and returns the failure.
    /// </summary>
    private IOException Stop(string message)
    {
        _ = inner.Restart();
        return new IOException(message);
    }
}
