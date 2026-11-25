using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal.FileSystem;

/// <summary>
/// A write-ahead log directory in the local file system, shared by the portable and io_uring storages, which differ
/// only in how a file performs I/O.
/// </summary>
/// <remarks>
/// A lock file held with exclusive sharing gives one owner. A file is written under a temporary name, synced, opened as
/// its storage opens it (which refuses a file system that cannot serve it), renamed, and the directory is flushed, so a
/// published name always has its durable contents. Each rename or deletion runs with its directory flush under one lock,
/// and an answer from the directory's names takes that lock after it observes them, so every change it observed has
/// finished its flush (WAL-I12). A failed flush stops the process (conventions §14), and the next incarnation's open
/// flushes the directory before any answer. Every file-system call goes through a <see cref="WalFileSystem"/>.
/// </remarks>
internal sealed class FileWalDirectory : WalDirectory
{
    // The zero-fill writes this much at a time, buffered, and syncs every ZeroFillSyncBytes (64 MiB, 16 device flushes
    // per 1 GiB file). On NVMe each sync costs a device flush that contends with live WAL fdatasyncs, while periodic
    // syncing keeps page-cache dirty buildup bounded and drops flushed pages on Linux (io-uring-wal.md).
    private static readonly byte[] Zeros = new byte[4 << 20];
    private const long ZeroFillSyncBytes = 64 << 20;

    private readonly string _path;
    private readonly WalFileSystem _fileSystem;
    private readonly IDisposable _lock;
    private readonly Func<string, bool, WalFile> _openFile;
    private long _nextPreparedId;

    // Held across each name change and its directory flush.
    private readonly Lock _names = new();

    private FileWalDirectory(string path, WalFileSystem fileSystem, IDisposable lockHandle, Func<string, bool, WalFile> openFile)
    {
        _path = path;
        _fileSystem = fileSystem;
        _lock = lockHandle;
        _openFile = openFile;
    }

    /// <summary>
    /// Gets the page size every file-system directory reports: the direct-I/O alignment, which covers 512-byte and
    /// 4 KiB sectors and 4 KiB filesystem blocks.
    /// </summary>
    public override int MinimumPageSize => WalDirectIo.Alignment;

    public override string Path => _path;

    /// <summary>
    /// Creates the directory under its existing parent, or accepts an existing one, and makes its name durable by
    /// flushing the parent. Repeating it makes durable a name a creator which crashed left unsynced (WAL-I10).
    /// </summary>
    /// <param name="path">The directory path; its parent is the caller's root or lies beneath it.</param>
    /// <param name="fileSystem">The file system holding the directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="DirectoryNotFoundException">The parent does not exist.</exception>
    public static void Create(string path, WalFileSystem fileSystem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = WalFileSystem.Normalize(path);
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (parent is null || !fileSystem.DirectoryExists(parent))
        {
            throw new DirectoryNotFoundException($"The parent of the write-ahead log directory '{fullPath}' does not exist.");
        }

        fileSystem.CreateDirectoryDurably(fullPath);
    }

    /// <summary>
    /// Takes ownership of an existing directory and makes its entries durable.
    /// </summary>
    /// <remarks>
    /// The directory's name is durable once a <see cref="Create"/> for it has returned, and a caller repeats that
    /// creation to make durable a name a crashed creator left unsynced. A missing directory is reported once its absence
    /// is durable in its parent (WAL-I12); the parent is the caller's root or lies beneath it, so a missing parent fails
    /// that flush.
    /// </remarks>
    public static FileWalDirectory Open(string path, WalFileSystem fileSystem, Func<string, bool, WalFile> openFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = WalFileSystem.Normalize(path);
        if (!fileSystem.DirectoryExists(fullPath))
        {
            fileSystem.FlushParent(fullPath);
            throw new DirectoryNotFoundException($"The write-ahead log directory '{fullPath}' does not exist.");
        }

        FileWalDirectory directory;
        try
        {
            directory = Own(fullPath, fileSystem, openFile);
        }
        catch (DirectoryNotFoundException exception) when (!FatalErrorHandler.IsReported(exception))
        {
            // A deletion removed the directory after the check; its absence, too, is reported once durable.
            fileSystem.FlushParent(fullPath);
            throw new DirectoryNotFoundException($"The write-ahead log directory '{fullPath}' does not exist.", exception);
        }

        try
        {
            // An earlier, crashed process may have published a name here and left it unsynced (WAL-I10).
            fileSystem.FlushDirectory(fullPath, DirectoryEntries.Files);
            return directory;
        }
        catch
        {
            directory._lock.Dispose();
            throw;
        }
    }

    public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<ulong>();
        foreach (var path in _fileSystem.EnumerateFiles(_path, "*" + WalFormat.FileExtension))
        {
            if (WalFormat.TryParseFileName(System.IO.Path.GetFileName(path), out var file))
            {
                files.Add(file);
            }
        }

        Settle();
        files.Sort();
        return ValueTask.FromResult<IReadOnlyList<ulong>>(files);
    }

    public override ValueTask<WalFile> CreateFileAsync(
        ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken) =>
        CreateFileCoreAsync(file, header, preallocatedLength, zeroFill: true, pace, cancellationToken);

    public override ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken) =>
        CreateFileCoreAsync(file, header, length, zeroFill: false, pace: null, cancellationToken);

    public override async ValueTask<PreparedWalFile> PrepareFileAsync(
        long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preallocatedLength);
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextPreparedId);
        var temporary = System.IO.Path.Combine(_path, string.Create(CultureInfo.InvariantCulture, $"prealloc-{id}{WalFormat.TemporaryExtension}"));
        try
        {
            using (var handle = _fileSystem.CreateFile(temporary, replace: true, writeThrough: false, preallocatedLength))
            {
                for (var position = 0L; position < preallocatedLength; position += Zeros.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(Zeros.Length, preallocatedLength - position);
                    if (pace is not null)
                    {
                        await pace(position, count, cancellationToken).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await handle.WriteAsync(Zeros.AsMemory(0, count), position, cancellationToken).ConfigureAwait(false);
                    if ((position + count) / ZeroFillSyncBytes != position / ZeroFillSyncBytes)
                    {
                        handle.Flush();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                handle.Flush();
            }

            return new FilePreparedWalFile(this, temporary, preallocatedLength);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await RemoveTemporaryAsync(temporary).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (FatalErrorHandler.IsReported(cleanup))
            {
                // The storage already reported a failure and stopped the process; Own() removes any *.tmp on open.
            }

            throw;
        }
    }

    public override async ValueTask<WalFile> PublishPreparedFileAsync(
        PreparedWalFile prepared, ulong file, ArcBuffer header, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared is not FilePreparedWalFile staged || !ReferenceEquals(staged.Directory, this))
        {
            throw new ArgumentException("The prepared file does not belong to this write-ahead log directory.", nameof(prepared));
        }

        staged.Consume();
        var path = System.IO.Path.Combine(_path, WalFormat.GetFileName(file));
        try
        {
            using (var handle = _fileSystem.OpenFile(staged.Temporary, direct: false))
            {
                await handle.WriteAsync([.. header.MemorySegments], 0, cancellationToken).ConfigureAwait(false);
                handle.Flush();
            }

            await PublishAsync(staged.Temporary, path, cancellationToken).ConfigureAwait(false);
            return _openFile(path, false);
        }
        catch (Exception exception) when (exception is WalNameExistsException or OperationCanceledException)
        {
            try
            {
                await RemoveTemporaryAsync(staged.Temporary).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (FatalErrorHandler.IsReported(cleanup))
            {
                // The storage already reported a failure and stopped the process; Own() removes any *.tmp on open.
            }

            throw;
        }
    }

    private async ValueTask<WalFile> CreateFileCoreAsync(
        ulong file, ArcBuffer header, long preallocatedLength, bool zeroFill, WalFillPace? pace, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(_path, WalFormat.GetFileName(file));
        var temporary = path + WalFormat.TemporaryExtension;

        // Buffered, synced every ZeroFillSyncBytes and at the end: a paced fill loads the device at about its pace,
        // with one device flush per sync rather than one per write or one for the whole file.
        using (var handle = _fileSystem.CreateFile(temporary, replace: false, writeThrough: false, preallocatedLength))
        {
            await handle.WriteAsync([.. header.MemorySegments], 0, cancellationToken).ConfigureAwait(false);

            // Writing the zeros, rather than only setting the length, initializes every block: NTFS's valid data length
            // and ext4's unwritten extents would otherwise change, as metadata, with each later write.
            // An allocated file's length is reserved when the handle is created, without writing it.
            for (var position = (long)header.Length; zeroFill && position < preallocatedLength; position += Zeros.Length)
            {
                var count = (int)Math.Min(Zeros.Length, preallocatedLength - position);
                if (pace is not null)
                {
                    await pace(position, count, cancellationToken).ConfigureAwait(false);
                }

                await handle.WriteAsync(Zeros.AsMemory(0, count), position, cancellationToken).ConfigureAwait(false);
                if ((position + count) / ZeroFillSyncBytes != position / ZeroFillSyncBytes)
                {
                    handle.Flush();
                }
            }

            handle.Flush();
        }

        // The name is published only once the header is durable, so a crash never leaves a published log file without
        // its header. A creation which fails, or is cancelled, leaves its temporary to the next owner, which deletes it.
        await PublishAsync(temporary, path, cancellationToken).ConfigureAwait(false);
        return _openFile(path, false);
    }

    public override async ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await OpenAnsweredAsync(System.IO.Path.Combine(_path, WalFormat.GetFileName(file))).ConfigureAwait(false);
    }

    public override ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RemoveAsync(System.IO.Path.Combine(_path, WalFormat.GetFileName(file)), cancellationToken);
    }

    /// <summary>
    /// Deletes a directory and everything in it, if it exists, and makes its removal durable in its parent. The directory's
    /// lock is taken first, so a lock another owner holds is the answer and nothing is deleted, and its lock file is
    /// deleted last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent is flushed even when the directory is already gone: an earlier deletion whose process crashed, or
    /// stopped on a failed flush (conventions §14), left the removal unsynced, and the deletion completes only once the
    /// removal is durable. The parent is the caller's root or lies beneath it, so a missing parent fails that flush.
    /// </para>
    /// <para>
    /// Every entry but the lock file goes while the lock is held, so no owner can take the directory meanwhile. Then the
    /// lock file and the empty directory go: on Linux and macOS still under the lock, since a lock file is unlinked there
    /// whoever holds it, and on Windows just after the lock is released, since Windows refuses to delete a file which a
    /// handle holds without sharing deletion, its own lock's included. Callers never open a directory while it is being
    /// deleted (wal.md §3); an owner which arrives anyway can lock only the emptied directory: on Windows its lock
    /// refuses the lock file's deletion, and elsewhere its new lock file leaves the directory not empty, so the deletion
    /// answers <see cref="WalLockHeldException"/> and the owner keeps the entries it creates (WAL-I11). On Windows the
    /// lock's open and the lock file's deletion first wait out another handle on the lock file, a scanner's or an
    /// indexer's, for up to the contention bound; only one still there past the bound answers that the lock is held. A
    /// directory which vanishes after the deletion found it, as when a concurrent deletion removes it first, is already
    /// gone: the lock's open and the last steps answer its absence unreported.
    /// </para>
    /// </remarks>
    /// <exception cref="WalLockHeldException">Another owner holds the directory's lock, or took it during the
    /// deletion.</exception>
    public static void Delete(string path, WalFileSystem fileSystem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = WalFileSystem.Normalize(path);
        if (fileSystem.DirectoryExists(fullPath))
        {
            try
            {
                DeleteOwned(fullPath, fileSystem);
            }
            catch (DirectoryNotFoundException exception) when (!FatalErrorHandler.IsReported(exception))
            {
                // Another deletion removed the directory first; the flush below makes its removal durable.
            }
        }

        fileSystem.FlushParent(fullPath);
    }

    private static void DeleteOwned(string path, WalFileSystem fileSystem)
    {
        var lockPath = System.IO.Path.Combine(path, WalFormat.LockFileName);
        using (fileSystem.OpenLockForDeletion(lockPath))
        {
            var others = fileSystem.EnumerateFiles(path, "*")
                .Where(static file => !string.Equals(System.IO.Path.GetFileName(file), WalFormat.LockFileName, StringComparison.Ordinal))
                .ToArray();
            foreach (var file in others)
            {
                fileSystem.DeleteFile(file);
            }

            foreach (var directory in fileSystem.EnumerateDirectories(path).ToArray())
            {
                fileSystem.DeleteDirectory(directory);
            }

            if (!OperatingSystem.IsWindows())
            {
                DeleteLast(path, lockPath, fileSystem);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            DeleteLast(path, lockPath, fileSystem);
        }
    }

    private static void DeleteLast(string path, string lockPath, WalFileSystem fileSystem)
    {
        if (!fileSystem.TryDeleteLockFile(lockPath) || !fileSystem.TryDeleteEmptyDirectory(path))
        {
            throw new WalLockHeldException($"Another owner took the directory '{path}' while it was being deleted.");
        }
    }

    /// <summary>
    /// Measures a directory, as <see cref="WalStorage.MeasureDirectoryAsync"/> describes: the bytes of its files, then the
    /// space of its volume.
    /// </summary>
    public static WalDirectoryUsage Measure(string path, WalFileSystem fileSystem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = WalFileSystem.Normalize(path);
        var used = fileSystem.MeasureDirectory(fullPath);
        var volume = fileSystem.GetVolumeSpace(fullPath);
        return new WalDirectoryUsage(used, volume.FreeBytes, volume.TotalBytes);
    }

    /// <summary>
    /// Lists the names of the directories within a directory in ordinal order, or none when it does not exist, once the
    /// names it reports are durable: after it observes them, it flushes the directory, or its parent when it is missing.
    /// </summary>
    /// <remarks>
    /// A creation or deletion whose process crashed, or stopped on a failed flush (conventions §14), before its parent's
    /// flush left its name unsynced; a listing which reported it, or its absence, would answer from a name a crash can
    /// revoke (WAL-I12). The flush follows the observation, so it covers every change the listing observed. The parent is
    /// the caller's root or lies beneath it, so a missing parent fails its flush: the directory's absence is an answer
    /// only within an existing parent.
    /// </remarks>
    public static IReadOnlyList<string> ListDirectories(string path, WalFileSystem fileSystem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = WalFileSystem.Normalize(path);
        if (fileSystem.DirectoryExists(fullPath))
        {
            IReadOnlyList<string> names = [.. fileSystem.EnumerateDirectories(fullPath).Select(static directory => System.IO.Path.GetFileName(directory)).Order(StringComparer.Ordinal)];
            fileSystem.FlushDirectory(fullPath, DirectoryEntries.Directories);
            return names;
        }

        fileSystem.FlushParent(fullPath);
        return [];
    }

    public override async ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken)
    {
        WalFormat.ValidateNamedFileName(name);
        var path = System.IO.Path.Combine(_path, name);
        var temporary = path + WalFormat.TemporaryExtension;

        // The name is published only once the contents are durable. A creation which fails, or is cancelled, before it
        // publishes leaves its temporary, which the next creation of the name replaces and the next owner deletes.
        using (var handle = _fileSystem.CreateFile(temporary, replace: true, writeThrough: false, contents.Length))
        {
            await handle.WriteAsync([.. contents.MemorySegments], 0, cancellationToken).ConfigureAwait(false);
            handle.Flush();
        }

        await PublishAsync(temporary, path, cancellationToken).ConfigureAwait(false);
        return _openFile(path, false);
    }

    public override async ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WalFormat.ValidateNamedFileName(name);
        return await OpenAnsweredAsync(System.IO.Path.Combine(_path, name)).ConfigureAwait(false);
    }

    public override ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WalFormat.ValidateNamedFileName("x" + extension);
        var names = _fileSystem.EnumerateFiles(_path, "*" + extension)
            .Select(System.IO.Path.GetFileName)
            .Where(name => name!.EndsWith(extension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        Settle();
        return ValueTask.FromResult<IReadOnlyList<string>>(names!);
    }

    public override ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WalFormat.ValidateNamedFileName(name);
        return RemoveAsync(System.IO.Path.Combine(_path, name), cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Publishes a synced temporary under its name and makes the name durable, once the storage opens the file: a file
    /// system which cannot serve the file refuses it before its name is published.
    /// </summary>
    /// <remarks>
    /// The rename runs with its flush under the directory's lock (WAL-I12). When another process's handle refuses the
    /// rename (contention, on Windows), the attempt leaves the lock, so the directory's other changes and answers
    /// proceed, pauses, and repeats; the seam fails the rename once the refusal outlasts its bound (conventions §14).
    /// </remarks>
    /// <exception cref="WalNameExistsException">The name exists.</exception>
    private async ValueTask PublishAsync(string temporary, string path, CancellationToken cancellationToken)
    {
        await _openFile(temporary, false).DisposeAsync().ConfigureAwait(false);
        var started = _fileSystem.TimeProvider.GetTimestamp();
        var pause = FileSystemAnswers.FirstContentionPause;
        while (true)
        {
            lock (_names)
            {
                if (_fileSystem.FileExists(path))
                {
                    throw new WalNameExistsException($"The file '{System.IO.Path.GetFileName(path)}' already exists.");
                }

                if (_fileSystem.TryMoveFile(temporary, path, replace: false, started))
                {
                    _fileSystem.FlushDirectory(_path, DirectoryEntries.Files);
                    return;
                }
            }

            await Task.Delay(pause, _fileSystem.TimeProvider, cancellationToken).ConfigureAwait(false);
            pause = FileSystemAnswers.NextPause(pause);
        }
    }

    /// <summary>
    /// Deletes a file, if it exists, and makes its removal durable.
    /// </summary>
    /// <remarks>
    /// The deletion runs with its flush under the directory's lock (WAL-I12). When another process's handle refuses the
    /// deletion (contention, on Windows), the attempt leaves the lock, so the directory's other changes and answers
    /// proceed, pauses, and repeats; the seam fails the deletion once the refusal outlasts its bound (conventions §14).
    /// </remarks>
    private async ValueTask RemoveAsync(string path, CancellationToken cancellationToken)
    {
        var started = _fileSystem.TimeProvider.GetTimestamp();
        var pause = FileSystemAnswers.FirstContentionPause;
        while (true)
        {
            lock (_names)
            {
                if (_fileSystem.TryDeleteFile(path, started))
                {
                    _fileSystem.FlushDirectory(_path, DirectoryEntries.Files);
                    return;
                }
            }

            await Task.Delay(pause, _fileSystem.TimeProvider, cancellationToken).ConfigureAwait(false);
            pause = FileSystemAnswers.NextPause(pause);
        }
    }

    /// <summary>
    /// Deletes an unpublished temporary file without flushing the directory: temporaries are never published and
    /// <see cref="Own"/> deletes any left by a crash.
    /// </summary>
    private async ValueTask RemoveTemporaryAsync(string temporary)
    {
        var started = _fileSystem.TimeProvider.GetTimestamp();
        var pause = FileSystemAnswers.FirstContentionPause;
        while (!_fileSystem.TryDeleteFile(temporary, started))
        {
            await Task.Delay(pause, _fileSystem.TimeProvider).ConfigureAwait(false);
            pause = FileSystemAnswers.NextPause(pause);
        }
    }

    /// <summary>
    /// Opens a file and answers with it, or with its absence, once the answer is durable.
    /// </summary>
    private async ValueTask<WalFile> OpenAnsweredAsync(string path)
    {
        WalFile? file = null;
        try
        {
            file = _openFile(path, true);
            Settle();
            return file;
        }
        catch (FileNotFoundException) when (file is null)
        {
            Settle();
            throw;
        }
        catch
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Orders an answer from the directory's names after every change it observed: a change in flight holds the lock
    /// until its flush completes (WAL-I12).
    /// </summary>
    private void Settle()
    {
        lock (_names)
        {
            // Entering the lock is the wait.
        }
    }

    private static FileWalDirectory Own(string path, WalFileSystem fileSystem, Func<string, bool, WalFile> openFile)
    {
        var lockHandle = fileSystem.OpenLockOfFoundDirectory(System.IO.Path.Combine(path, WalFormat.LockFileName));
        try
        {
            // A crash can leave a log file or named file which was never published; only published names count.
            foreach (var temporary in fileSystem.EnumerateFiles(path, "*" + WalFormat.TemporaryExtension))
            {
                fileSystem.DeleteFile(temporary);
            }

            return new FileWalDirectory(path, fileSystem, lockHandle, openFile);
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    private sealed class FilePreparedWalFile(FileWalDirectory directory, string temporary, long length) : PreparedWalFile
    {
        private int _consumed;

        public FileWalDirectory Directory => directory;

        public string Temporary => temporary;

        public override long Length => length;

        public void Consume() => ObjectDisposedException.ThrowIf(Interlocked.Exchange(ref _consumed, 1) != 0, this);

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _consumed, 1) == 0)
            {
                await directory.RemoveTemporaryAsync(temporary).ConfigureAwait(false);
            }
        }
    }
}
