using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// One process's view of a <see cref="RecordingFileSystem"/> which can crash the process just after a chosen call, and,
/// as a mutant of the store above it, skip a directory flush or a file's flush.
/// </summary>
/// <remarks>
/// The view keeps the calls made since its last directory flush, each marked with whether the process held the store's
/// lock. <see cref="SkipFlush"/> sees them before each flush and skips it when it answers true, so a mutant names the
/// flush it removes by the calls which precede it, as a flush before an answer follows the calls whose effects it makes
/// durable. <see cref="CrashAfter"/> sees each call once it has completed and crashes the process when it answers true:
/// every later call through the view throws. <see cref="BeforeWrite"/> runs before each write to a file the view
/// created, so a test can place another process's call between two steps of a store operation.
/// </remarks>
internal sealed class ScriptedFileSystem(RecordingFileSystem inner) : WalFileSystem
{
    private readonly List<SeamCall> _sinceFlush = [];
    private bool _locked;

    /// <summary>
    /// Gets the process's view of the recorded file system.
    /// </summary>
    public RecordingFileSystem Inner => inner;

    /// <summary>
    /// Gets or sets which completed call crashes the process.
    /// </summary>
    public Func<SeamCall, bool>? CrashAfter { get; set; }

    /// <summary>
    /// Gets or sets which directory flushes the view skips, given the calls since the last flush.
    /// </summary>
    public Func<IReadOnlyList<SeamCall>, bool>? SkipFlush { get; set; }

    /// <summary>
    /// Gets or sets which flushes of files created through the view it skips, given whether the process holds the
    /// store's lock.
    /// </summary>
    public Func<bool, bool>? SkipFileFlush { get; set; }

    /// <summary>
    /// Gets or sets work to run before each write to a file created through the view, given whether the process holds
    /// the store's lock.
    /// </summary>
    public Func<bool, Task>? BeforeWrite { get; set; }

    /// <summary>
    /// Gets or sets work to run at the start of each directory flush, on the flushing thread.
    /// </summary>
    public Action? BeforeFlush { get; set; }

    /// <summary>
    /// Gets the number of directory flushes the view made.
    /// </summary>
    public int Flushes { get; private set; }

    /// <summary>
    /// Gets the number of directory flushes the view skipped.
    /// </summary>
    public int SkippedFlushes { get; private set; }

    /// <summary>
    /// Gets the number of file flushes the view skipped.
    /// </summary>
    public int SkippedFileFlushes { get; private set; }

    internal override bool DirectoryExistsCore(string path) => inner.DirectoryExistsCore(path);

    internal override void CreateDirectoryCore(string path) => inner.CreateDirectoryCore(path);

    internal override void DeleteDirectoryCore(string path) => inner.DeleteDirectoryCore(path);

    internal override bool TryDeleteEmptyDirectoryCore(string path) => inner.TryDeleteEmptyDirectoryCore(path);

    internal override bool TryDeleteLockFileCore(string path) => inner.TryDeleteLockFileCore(path);

    internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern)
    {
        var files = inner.EnumerateFilesCore(path, searchPattern);
        Completed(Call(SeamCallKind.List, path, found: true));
        return files;
    }

    internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) =>
        inner.EnumerateFilesWithLastWriteTimesCore(path, includeName);

    internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff) => inner.TryDeleteFileWrittenBeforeCore(path, cutoff);

    internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => inner.EnumerateDirectoriesCore(path);

    internal override bool FileExistsCore(string path) => inner.FileExistsCore(path);

    internal override IDisposable OpenLockCore(string path)
    {
        var held = inner.OpenLockCore(path);
        _locked = true;
        Completed(Call(SeamCallKind.Lock, path, found: true));
        return new HeldLock(this, held);
    }

    internal override IDisposable OpenLockForDeletionCore(string path) => inner.OpenLockForDeletionCore(path);

    internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize) =>
        new ScriptedFile(this, inner.CreateFileCore(path, replace, writeThrough, preallocationSize));

    internal override IWalFileSystemFile OpenFileCore(string path, bool direct)
    {
        IWalFileSystemFile file;
        try
        {
            file = inner.OpenFileCore(path, direct);
        }
        catch (FileNotFoundException)
        {
            Completed(Call(SeamCallKind.Open, path, found: false));
            throw;
        }

        Completed(Call(SeamCallKind.Open, path, found: true));
        return file;
    }

    internal override void MoveFileCore(string source, string destination, bool replace)
    {
        inner.MoveFileCore(source, destination, replace);
        Completed(Call(SeamCallKind.Move, destination, found: true));
    }

    internal override void LinkFileCore(string existing, string link)
    {
        try
        {
            inner.LinkFileCore(existing, link);
        }
        catch (WalNameExistsException)
        {
            Completed(Call(SeamCallKind.Link, link, found: false));
            throw;
        }

        Completed(Call(SeamCallKind.Link, link, found: true));
    }

    internal override async ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken)
    {
        var content = await inner.ReadFileCoreAsync(path, cancellationToken);
        Completed(Call(SeamCallKind.Read, path, found: content is not null));
        return content;
    }

    internal override void DeleteFileCore(string path)
    {
        inner.DeleteFileCore(path);
        Completed(Call(SeamCallKind.Delete, path, found: true));
    }

    internal override void FlushDirectoryCore(string path, DirectoryEntries entries)
    {
        BeforeFlush?.Invoke();
        Flushes++;
        if (SkipFlush?.Invoke(_sinceFlush) == true)
        {
            SkippedFlushes++;
        }
        else
        {
            inner.FlushDirectoryCore(path, entries);
        }

        _sinceFlush.Clear();
    }

    /// <summary>
    /// Marks the store's lock released.
    /// </summary>
    internal void Released() => _locked = false;

    /// <summary>
    /// Runs <see cref="BeforeWrite"/> before a write to a file the view created.
    /// </summary>
    internal Task WritingAsync() => BeforeWrite?.Invoke(_locked) ?? Task.CompletedTask;

    /// <summary>
    /// Returns whether the view skips the flush of a file it created, as <see cref="SkipFileFlush"/> chooses.
    /// </summary>
    internal bool SkipsFileFlush()
    {
        if (SkipFileFlush?.Invoke(_locked) != true)
        {
            return false;
        }

        SkippedFileFlushes++;
        return true;
    }

    private SeamCall Call(SeamCallKind kind, string path, bool found) => new(kind, path, found, _locked);

    private void Completed(SeamCall call)
    {
        _sinceFlush.Add(call);
        if (CrashAfter?.Invoke(call) == true)
        {
            _ = inner.Restart();
        }
    }
}

/// <summary>
/// A file created through a <see cref="ScriptedFileSystem"/>, which runs the view's <see cref="ScriptedFileSystem.BeforeWrite"/>
/// before each write, and skips the flushes its <see cref="ScriptedFileSystem.SkipFileFlush"/> names.
/// </summary>
file sealed class ScriptedFile(ScriptedFileSystem view, IWalFileSystemFile file) : IWalFileSystemFile
{
    public long Length => file.Length;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) => file.ReadAsync(buffer, offset, cancellationToken);

    public ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken) =>
        file.ReadAsync(buffers, offset, cancellationToken);

    public async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken)
    {
        await view.WritingAsync();
        await file.WriteAsync(buffers, offset, cancellationToken);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken)
    {
        await view.WritingAsync();
        await file.WriteAsync(buffer, offset, cancellationToken);
    }

    public void SetLength(long length) => file.SetLength(length);

    public void Flush()
    {
        if (!view.SkipsFileFlush())
        {
            file.Flush();
        }
    }

    public void Dispose() => file.Dispose();
}

/// <summary>
/// The store's lock as the view hands it out: releasing it marks the view's later calls as made without it.
/// </summary>
file sealed class HeldLock(ScriptedFileSystem view, IDisposable held) : IDisposable
{
    public void Dispose()
    {
        view.Released();
        held.Dispose();
    }
}

/// <summary>
/// The kinds of <see cref="SeamCall"/>.
/// </summary>
internal enum SeamCallKind
{
    List,
    Lock,
    Move,
    Link,
    Open,
    Read,
    Delete,
}

/// <summary>
/// A call through a <see cref="ScriptedFileSystem"/>: its kind, the path it named, whether it found its file, or, for a
/// link, published it, and whether its process held the store's lock.
/// </summary>
internal sealed record SeamCall(SeamCallKind Kind, string Path, bool Found, bool UnderLock)
{
    /// <summary>
    /// Gets a value indicating whether the call named a temporary file.
    /// </summary>
    public bool IsTemporary => Path.EndsWith(".tmp", StringComparison.Ordinal);
}
