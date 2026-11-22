using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Orleans.Durable.Storage.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Wal.FileSystem;

/// <summary>
/// The file-system calls from which the WAL's directories, the portable storage's files, the node root's cluster claim,
/// the file-system MetaStore's registers, and the file-system object store's objects compose durable publication:
/// namespace operations, file contents, and the flushes which make each durable.
/// </summary>
/// <remarks>
/// <para>
/// This is the local storage seam (conventions.md §14): each public operation reports a failure to
/// <see cref="FatalErrorHandler"/> with its own <see cref="FatalErrorSite"/>, which stops the process, and rethrows
/// it only when a test or simulation intercepts the report. Answers the file system gives are not failures and pass
/// through unreported: a missing file (<see cref="FileNotFoundException"/>), a name which exists
/// (<see cref="WalNameExistsException"/>), and a lock another owner holds (<see cref="WalLockHeldException"/>); so do a
/// refused request, the caller's cancellation, and a volume the WAL does not qualify
/// (<see cref="PlatformNotSupportedException"/>).
/// </para>
/// <para>
/// A path is answered missing only when its own directory exists. A missing directory is an answer only of the
/// directory operations whose contract names it: <see cref="DirectoryExists"/>, <see cref="CreateDirectory"/> (a
/// missing parent), <see cref="DeleteDirectory"/>, and <see cref="EnumerateDirectories"/>. Every other operation
/// names a directory its caller created or was given, so a <see cref="DirectoryNotFoundException"/> from it means that
/// directory has gone, as when its volume vanished, and is a failure.
/// </para>
/// <para>
/// <see cref="Os"/> is the operating system's file system. Crash-state enumeration records every call through a test
/// double and replays from each crash state the file system's persistence model allows (verification.md §7.5).
/// Every directory a caller flushes is its root or lies beneath it (wal.md §3).
/// </para>
/// <para>
/// The WAL component owns this contract (wal.md §9), which the file-system package uses from its own assembly. The
/// members an implementation overrides are internal, so only the Engine and its tests implement it.
/// </para>
/// </remarks>
public abstract partial class WalFileSystem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WalFileSystem"/> class whose waits for contention run on the system
    /// clock.
    /// </summary>
    protected WalFileSystem()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WalFileSystem"/> class whose waits for contention run on
    /// <paramref name="timeProvider"/>.
    /// </summary>
    protected WalFileSystem(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the operating system's file system.
    /// </summary>
    public static WalFileSystem Os { get; } = new OsWalFileSystem(TimeProvider.System, lockFault: null);

    /// <summary>
    /// Gets the clock which bounds and paces a wait for contention (<see cref="FileSystemAnswers.ContentionBound"/>).
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Returns the operating system's file system with its waits for contention on <paramref name="timeProvider"/>, so a
    /// test drives the bound.
    /// </summary>
    internal static WalFileSystem CreateOs(TimeProvider timeProvider) => new OsWalFileSystem(timeProvider, lockFault: null);

    /// <summary>
    /// Creates the operating system's file system with an <c>flock(2)</c> whose errors <paramref name="lockFault"/>
    /// chooses, by lock path, in place of taking the lock, for tests; 0 takes it.
    /// </summary>
    internal static WalFileSystem CreateOs(TimeProvider timeProvider, Func<string, int> lockFault) => new OsWalFileSystem(timeProvider, lockFault);

    /// <summary>
    /// Creates the operating system's file system which calls <paramref name="afterReclaimOpen"/>, for tests, once a
    /// Windows <see cref="TryDeleteFileWrittenBefore"/> holds its file and before it reads the file's last write.
    /// </summary>
    internal static WalFileSystem CreateOs(TimeProvider timeProvider, Action<string> afterReclaimOpen) =>
        new OsWalFileSystem(timeProvider, lockFault: null, afterReclaimOpen);

    /// <summary>
    /// Returns whether a directory exists at <paramref name="path"/>: <see langword="false"/> also when its parent is
    /// missing.
    /// </summary>
    public bool DirectoryExists(string path) => Guard(FatalErrorSite.DirectoryExists, path, () => DirectoryExistsCore(path), IsDirectoryFailure);

    /// <summary>
    /// Creates one directory whose parent exists.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The parent does not exist.</exception>
    public void CreateDirectory(string path) => Guard(FatalErrorSite.CreateDirectory, path, () => CreateDirectoryCore(path), IsDirectoryFailure);

    /// <summary>
    /// Deletes a directory and everything in it, waiting out contention.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public void DeleteDirectory(string path) => Guard(FatalErrorSite.DeleteDirectory, path, () => DeleteDirectoryCore(path), IsDirectoryFailure);

    /// <summary>
    /// Deletes a directory which holds no entries, answering whether it did: <see langword="false"/> when the directory
    /// holds an entry. On Windows it waits out another process's handle on the directory, as contention.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public bool TryDeleteEmptyDirectory(string path) =>
        Guard(FatalErrorSite.DeleteEmptyDirectory, path, () => TryDeleteEmptyDirectoryCore(path), IsDirectoryFailure);

    /// <summary>
    /// Returns the paths of the files in a directory whose names match <paramref name="searchPattern"/>, as
    /// <see cref="Directory.EnumerateFiles(string, string)"/> does, read in full before it returns. The directory is one
    /// its caller holds, so its absence is a failure.
    /// </summary>
    public IReadOnlyList<string> EnumerateFiles(string path, string searchPattern) => ListFiles(path, () => EnumerateFilesCore(path, searchPattern));

    /// <summary>
    /// Returns the files in a directory whose names <paramref name="includeName"/> accepts, with their lengths, read in
    /// full before it returns. The directory is one its caller holds, so its absence is a failure. A file which a
    /// concurrent rename or deletion removes is listed with the length it had, or left out.
    /// </summary>
    /// <remarks>
    /// On Windows the lengths come with the directory's enumeration, so no file is opened: a file whose deletion is
    /// pending keeps its name until the deleting handle closes, and an open of it answers access denied. Elsewhere each
    /// accepted file is opened for its length, and one unlinked since the enumeration is left out.
    /// </remarks>
    /// <param name="path">The directory.</param>
    /// <param name="includeName">Whether to list the file with a given name.</param>
    public IReadOnlyList<WalListedFile> EnumerateFilesWithLengths(string path, WalFileNameFilter includeName) =>
        ListFiles(path, () => EnumerateFilesWithLengthsCore(path, includeName));

    /// <summary>
    /// Returns the files in a directory whose names <paramref name="includeName"/> accepts, with the times of their last
    /// writes, read in full before it returns, opening no file. The directory is one its caller holds, so its absence is a
    /// failure. A file which a concurrent rename or deletion removes is listed with the time it had, or left out.
    /// </summary>
    /// <param name="path">The directory.</param>
    /// <param name="includeName">Whether to list the file with a given name.</param>
    public IReadOnlyList<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimes(string path, WalFileNameFilter includeName) =>
        ListFiles(path, () => EnumerateFilesWithLastWriteTimesCore(path, includeName));

    /// <summary>
    /// Returns the bytes of the files beneath a directory its caller holds, in it and in every directory below it. A
    /// reparse point (a symbolic link, a junction, or a mounted volume) is neither counted nor entered. Below the directory,
    /// an entry which a concurrent change removes, which is being deleted, or which the process cannot read is an answer,
    /// left out or counted with the length it had; the directory's own absence or failure is a failure.
    /// </summary>
    /// <param name="path">The directory.</param>
    public long MeasureDirectory(string path) => Guard(FatalErrorSite.MeasureDirectory, path, () => MeasureDirectoryCore(path), IsFailure);

    /// <summary>
    /// Returns the free space available to the process and the total size of the volume holding a directory its caller
    /// holds, queried for the directory itself, so a directory which is a mount point reports its own volume.
    /// </summary>
    /// <param name="path">The directory.</param>
    public WalVolumeSpace GetVolumeSpace(string path) => Guard(FatalErrorSite.VolumeSpace, path, () => GetVolumeSpaceCore(path), IsFailure);

    /// <summary>
    /// Returns the paths of the directories in a directory, read in full before it returns.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public IReadOnlyList<string> EnumerateDirectories(string path) =>
        Guard<IReadOnlyList<string>>(FatalErrorSite.ListDirectories, path, () => [.. EnumerateDirectoriesCore(path)], IsDirectoryFailure);

    /// <summary>
    /// Returns whether a file exists at <paramref name="path"/>, in a directory which exists.
    /// </summary>
    public bool FileExists(string path) => Guard(FatalErrorSite.FileExists, path, () => FileExistsCore(path), IsFailure);

    /// <summary>
    /// Opens the lock file at <paramref name="path"/>, creating it when absent, and takes its exclusive lock, which
    /// excludes every other owner in every process on the host; the lock lasts until the result is disposed. On Windows
    /// the lock is the handle's exclusive sharing mode; on Linux and macOS it is <c>flock(2)</c>'s exclusive lock, taken
    /// here (<see cref="WalFlock"/>). Lock files need a local file system (wal.md §3).
    /// </summary>
    /// <exception cref="WalLockHeldException">Another owner holds the lock.</exception>
    public IDisposable OpenLock(string path) => GuardOpenLock(path, () => OpenLockCore(path), IsFailure);

    /// <summary>
    /// Opens the lock file of a directory an open found, as <see cref="OpenLock"/> does, except that a directory which
    /// vanished since, as when a concurrent deletion removed it, is an answer (<see cref="DirectoryNotFoundException"/>),
    /// not a failure.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory no longer exists.</exception>
    public IDisposable OpenLockOfFoundDirectory(string path) => GuardOpenLock(path, () => OpenLockCore(path), IsDirectoryFailure);

    /// <summary>
    /// Opens the lock file of a directory a deletion found, as <see cref="OpenLockOfFoundDirectory"/> does, except that on
    /// Windows it waits out another handle on the lock file as contention, for up to the bound, before answering that the
    /// lock is held: no owner holds a directory being deleted (wal.md §3), so such a handle is a scanner's or an
    /// indexer's, which closes on its own, and only one still there past the bound is an owner's.
    /// </summary>
    /// <exception cref="WalLockHeldException">Another owner holds the lock.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory no longer exists.</exception>
    public IDisposable OpenLockForDeletion(string path) => GuardOpenLock(path, () => OpenLockForDeletionCore(path), IsDirectoryFailure);

    /// <summary>
    /// Creates a file for writing, truncating an existing one when <paramref name="replace"/> is set and failing
    /// otherwise. With <paramref name="writeThrough"/>, each write's bytes are durable once it completes; a length it
    /// extends is durable once a later <see cref="IWalFileSystemFile.Flush"/> completes, and on Linux that flush also
    /// advises the kernel to drop the file's cached pages: such a file is a log file's fill, which the direct handles
    /// that later open it never read.
    /// </summary>
    /// <exception cref="WalNameExistsException">The file exists and <paramref name="replace"/> is not set.</exception>
    public IWalFileSystemFile CreateFile(string path, bool replace, bool writeThrough, long preallocationSize) =>
        Guard<IWalFileSystemFile>(FatalErrorSite.CreateFile, path, () => new ReportingFile(CreateFileCore(path, replace, writeThrough, preallocationSize), path), IsFailure);

    /// <summary>
    /// Opens an existing file for reading and writing, shared with other handles to it, waiting out contention, which on
    /// Windows includes the access denial of a file whose deletion is pending
    /// (<see cref="FileSystemAnswers.IsFileOpenContention"/>).
    /// </summary>
    /// <remarks>
    /// With <paramref name="direct"/>, reads and writes bypass the page cache and write through: each write reports its
    /// own errors and its bytes are durable once it completes, a length it extends is durable once a later
    /// <see cref="IWalFileSystemFile.Flush"/> completes, and each read returns the device's bytes. The offset, length, and
    /// memory of each direct read and write are multiples of <see cref="WalDirectIo.Alignment"/>. Without it, I/O is
    /// buffered, and a write is durable once a later flush of its file completes.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The file does not exist in its existing directory.</exception>
    /// <exception cref="WalLockHeldException">Another owner holds the file's lock.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// <paramref name="direct"/> is set outside Linux and Windows, or the file system does not serve the file by direct I/O.
    /// </exception>
    public IWalFileSystemFile OpenFile(string path, bool direct) =>
        Guard<IWalFileSystemFile>(FatalErrorSite.OpenFile, path, () => new ReportingFile(OpenFileCore(path, direct), path), IsFailure);

    /// <summary>
    /// Renames a file within its directory, atomically replacing the destination when <paramref name="replace"/> is
    /// set, waiting out contention.
    /// </summary>
    /// <exception cref="WalNameExistsException">The destination exists and <paramref name="replace"/> is not set.</exception>
    public void MoveFile(string source, string destination, bool replace) => _ = Move(source, destination, replace, contentionStarted: null);

    /// <summary>
    /// Renames a file as <see cref="MoveFile"/> does, in one attempt, for a caller which holds a lock others wait on and
    /// so waits out contention itself, outside the lock: returns <see langword="false"/>, having changed nothing, when
    /// another process's handle refused the rename within <see cref="FileSystemAnswers.ContentionBound"/> of
    /// <paramref name="contentionStarted"/>, a timestamp of <see cref="TimeProvider"/>; past the bound, the refusal is a
    /// failure.
    /// </summary>
    /// <exception cref="WalNameExistsException">The destination exists and <paramref name="replace"/> is not set.</exception>
    public bool TryMoveFile(string source, string destination, bool replace, long contentionStarted) =>
        Move(source, destination, replace, contentionStarted);

    /// <summary>
    /// Atomically publishes <paramref name="existing"/>'s file under <paramref name="link"/> only when no file has that
    /// name: <c>link(2)</c> on Linux and macOS, which keeps the existing name, and a no-replace move on Windows.
    /// </summary>
    /// <exception cref="WalNameExistsException">The name exists.</exception>
    public void LinkFile(string existing, string link) => Guard(FatalErrorSite.LinkFile, link, () => LinkFileCore(existing, link), IsFailure);

    /// <summary>
    /// Returns a file's bytes, or <see langword="null"/> when it does not exist in its existing directory. The read shares
    /// the file with other handles which read or write it, and with its rename and deletion, and its open waits out
    /// contention as <see cref="OpenFile"/>'s does.
    /// </summary>
    public async ValueTask<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadFileCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.ReadFile, path, exception);
            throw;
        }
    }

    /// <summary>
    /// Deletes a file, if it exists in its existing directory, waiting out contention.
    /// </summary>
    public void DeleteFile(string path) => _ = Delete(path, contentionStarted: null);

    /// <summary>
    /// Deletes a file as <see cref="DeleteFile"/> does, in one attempt, for a caller which holds a lock others wait on
    /// and so waits out contention itself, outside the lock: returns <see langword="false"/>, having changed nothing, when
    /// another process's handle refused the deletion within <see cref="FileSystemAnswers.ContentionBound"/> of
    /// <paramref name="contentionStarted"/>, a timestamp of <see cref="TimeProvider"/>; past the bound, the refusal is a
    /// failure.
    /// </summary>
    public bool TryDeleteFile(string path, long contentionStarted) => Delete(path, contentionStarted);

    /// <summary>
    /// Deletes a file whose last write is older than <paramref name="cutoff"/>, in one attempt which holds the file
    /// against every other handle from the check of its last write to its deletion, so no rename moves it meanwhile:
    /// returns <see langword="false"/>, having changed nothing, when another handle refused it; and
    /// <see langword="true"/> when the file is deleted, absent, or written at or after <paramref name="cutoff"/>.
    /// </summary>
    /// <remarks>
    /// On Windows the attempt opens the file sharing nothing, reads its last write on that handle, and marks that handle
    /// for deletion, so a rename of the file while the handle is open meets contention, and an open which another handle
    /// refuses, a writer's among them, is the refusal. Linux and macOS rename and unlink names atomically, so there the
    /// attempt reads the last write and unlinks the name, whatever handle holds the file.
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <param name="cutoff">The time, by the file system's clock, before which the file's last write makes it deletable.</param>
    public bool TryDeleteFileWrittenBefore(string path, DateTimeOffset cutoff) =>
        DeleteFiles(path, () => TryDeleteFileWrittenBeforeCore(path, cutoff));

    /// <summary>
    /// Deletes the lock file at <paramref name="path"/>, if it exists, answering whether no other owner held it. On
    /// Windows another handle refuses the deletion: it waits that out as contention, for up to the bound, since a
    /// scanner's handle closes on its own, and a handle still there past the bound is another owner's lock, answering
    /// <see langword="false"/>. Linux and macOS unlink a lock file whoever holds it, so there the caller holds the lock
    /// across the deletion. It is a step of its directory's
    /// deletion, so a missing directory is an answer.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The lock file's directory does not exist.</exception>
    public bool TryDeleteLockFile(string path) =>
        Guard(FatalErrorSite.DeleteLockFile, path, () => TryDeleteLockFileCore(path), IsDirectoryFailure);

    /// <summary>
    /// Makes a directory's entries of one kind durable; throws <see cref="PlatformNotSupportedException"/> on a volume
    /// whose directory flush is not qualified. The directory is one its caller holds, so its absence is a failure.
    /// </summary>
    /// <param name="path">The directory.</param>
    /// <param name="entries">The kind of entry the caller created, renamed, or deleted in it.</param>
    public void FlushDirectory(string path, DirectoryEntries entries) =>
        Guard(FatalErrorSite.FlushDirectory, path, () => FlushDirectoryCore(path, entries), IsFailure);

    /// <summary>
    /// Returns a path's full form without a trailing separator, so its parent is its containing directory.
    /// </summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Makes a directory's own name, or its absence, durable by flushing its parent, which must exist.
    /// </summary>
    /// <param name="path">The directory's path, in <see cref="Normalize"/>'s form, beneath the caller's root.</param>
    public void FlushParent(string path) =>
        FlushDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException($"'{path}' has no parent.", nameof(path)), DirectoryEntries.Directories);

    /// <summary>
    /// Creates a directory under its existing parent, or accepts an existing one, and makes its name durable by flushing
    /// its parent whether or not this call created it, so repeating it makes durable a name a creator which crashed left
    /// unsynced (WAL-I10). Each step reports a failure as <see cref="DirectoryExists"/>, <see cref="CreateDirectory"/>,
    /// and <see cref="FlushParent"/> do.
    /// </summary>
    /// <param name="path">The directory's path, in <see cref="Normalize"/>'s form, beneath the caller's root.</param>
    /// <exception cref="DirectoryNotFoundException">The parent does not exist.</exception>
    public void CreateDirectoryDurably(string path)
    {
        if (!DirectoryExists(path))
        {
            CreateDirectory(path);
        }

        FlushParent(path);
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/> is a failure of a file operation, or of an operation on a file it
    /// opened, which stops the process, rather than an answer the file system gave, a refusal of the request, or the
    /// caller's cancellation. A missing directory is a failure: the operation names a directory its caller holds.
    /// </summary>
    internal static bool IsFailure(Exception exception) => exception is not (
        FileNotFoundException
        or WalNameExistsException
        or WalLockHeldException
        or PlatformNotSupportedException
        or OperationCanceledException
        or ObjectDisposedException
        or ArgumentException
        or NotSupportedException
        or InvalidOperationException);

    /// <summary>
    /// Returns whether <paramref name="exception"/> is a failure of a directory operation whose contract names a missing
    /// directory as its answer, as <see cref="IsFailure(Exception)"/> decides but for that answer.
    /// </summary>
    internal static bool IsDirectoryFailure(Exception exception) => exception is not DirectoryNotFoundException && IsFailure(exception);

    /// <summary>
    /// Opens a lock file through <paramref name="open"/>, reporting a failure as <paramref name="isFailure"/> tells it
    /// from the open's answers.
    /// </summary>
    private static IDisposable GuardOpenLock(string path, Func<IDisposable> open, Func<Exception, bool> isFailure) =>
        Guard(FatalErrorSite.OpenLock, path, open, isFailure);

    /// <summary>
    /// Lists a directory's files through <paramref name="list"/>, read in full before it returns, reporting a failure
    /// as a failed listing of the directory's files.
    /// </summary>
    private static IReadOnlyList<T> ListFiles<T>(string path, Func<IEnumerable<T>> list) =>
        Guard<IReadOnlyList<T>>(FatalErrorSite.ListFiles, path, () => [.. list()], IsFailure);

    /// <summary>
    /// Renames a file, waiting out contention, or, given when the caller met it first, in one attempt.
    /// </summary>
    private bool Move(string source, string destination, bool replace, long? contentionStarted) =>
        Guard(
            FatalErrorSite.MoveFile,
            destination,
            () =>
            {
                if (contentionStarted is { } started)
                {
                    return TryMoveFileCore(source, destination, replace, started);
                }

                MoveFileCore(source, destination, replace);
                return true;
            },
            IsFailure);

    /// <summary>
    /// Deletes a file, waiting out contention, or, given when the caller met it first, in one attempt.
    /// </summary>
    private bool Delete(string path, long? contentionStarted) =>
        DeleteFiles(
            path,
            () =>
            {
                if (contentionStarted is { } started)
                {
                    return TryDeleteFileCore(path, started);
                }

                DeleteFileCore(path);
                return true;
            });

    /// <summary>
    /// Runs a deletion of the file at <paramref name="path"/>, reporting a failure, which stops the process, as a failed
    /// deletion of a file.
    /// </summary>
    private static bool DeleteFiles(string path, Func<bool> delete) => Guard(FatalErrorSite.DeleteFile, path, delete, IsFailure);

    /// <summary>
    /// Runs a synchronous operation of <paramref name="site"/> on <paramref name="path"/>, reporting a failure, as
    /// <paramref name="isFailure"/> tells it from the operation's answers, which stops the process, and rethrowing it
    /// when a test or simulation intercepts the report; an answer passes through unreported.
    /// </summary>
    private static T Guard<T>(FatalErrorSite site, string path, Func<T> operation, Func<Exception, bool> isFailure)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (isFailure(exception))
        {
            FatalErrorHandler.StopOnStorageFailure(site, path, exception);
            throw;
        }
    }

    /// <inheritdoc cref="Guard{T}(FatalErrorSite, string, Func{T}, Func{Exception, bool})"/>
    private static void Guard(FatalErrorSite site, string path, Action operation, Func<Exception, bool> isFailure)
    {
        try
        {
            operation();
        }
        catch (Exception exception) when (isFailure(exception))
        {
            FatalErrorHandler.StopOnStorageFailure(site, path, exception);
            throw;
        }
    }

    /// <inheritdoc cref="DirectoryExists"/>
    internal abstract bool DirectoryExistsCore(string path);

    /// <inheritdoc cref="CreateDirectory"/>
    internal abstract void CreateDirectoryCore(string path);

    /// <inheritdoc cref="DeleteDirectory"/>
    internal abstract void DeleteDirectoryCore(string path);

    /// <inheritdoc cref="TryDeleteEmptyDirectory"/>
    internal abstract bool TryDeleteEmptyDirectoryCore(string path);

    /// <inheritdoc cref="TryDeleteLockFile"/>
    internal abstract bool TryDeleteLockFileCore(string path);

    /// <inheritdoc cref="EnumerateFiles"/>
    internal abstract IEnumerable<string> EnumerateFilesCore(string path, string searchPattern);

    /// <inheritdoc cref="EnumerateFilesWithLastWriteTimes"/>
    internal abstract IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName);

    /// <inheritdoc cref="EnumerateDirectories"/>
    internal abstract IEnumerable<string> EnumerateDirectoriesCore(string path);

    /// <inheritdoc cref="FileExists"/>
    internal abstract bool FileExistsCore(string path);

    /// <inheritdoc cref="OpenLock"/>
    internal abstract IDisposable OpenLockCore(string path);

    /// <inheritdoc cref="OpenLockForDeletion"/>
    internal abstract IDisposable OpenLockForDeletionCore(string path);

    /// <inheritdoc cref="CreateFile"/>
    internal abstract IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize);

    /// <inheritdoc cref="OpenFile"/>
    internal abstract IWalFileSystemFile OpenFileCore(string path, bool direct);

    /// <inheritdoc cref="MoveFile"/>
    internal abstract void MoveFileCore(string source, string destination, bool replace);

    /// <inheritdoc cref="LinkFile"/>
    internal abstract void LinkFileCore(string existing, string link);

    /// <inheritdoc cref="ReadFileAsync"/>
    internal abstract ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken);

    /// <inheritdoc cref="DeleteFile"/>
    internal abstract void DeleteFileCore(string path);

    /// <inheritdoc cref="FlushDirectory"/>
    internal abstract void FlushDirectoryCore(string path, DirectoryEntries entries);

    /// <summary>
    /// Renames a file in one attempt, as <see cref="TryMoveFile"/> describes. A file system without contention renames
    /// as <see cref="MoveFileCore"/> does.
    /// </summary>
    internal virtual bool TryMoveFileCore(string source, string destination, bool replace, long contentionStarted)
    {
        MoveFileCore(source, destination, replace);
        return true;
    }

    /// <summary>
    /// Deletes a file in one attempt, as <see cref="TryDeleteFile"/> describes. A file system without contention deletes
    /// as <see cref="DeleteFileCore"/> does.
    /// </summary>
    internal virtual bool TryDeleteFileCore(string path, long contentionStarted)
    {
        DeleteFileCore(path);
        return true;
    }

    /// <summary>
    /// Lists a directory's files with their lengths, as <see cref="EnumerateFilesWithLengths"/> describes. A file system
    /// whose opens never meet a file pending deletion opens each file it enumerates, leaving out one deleted since.
    /// </summary>
    internal virtual IEnumerable<WalListedFile> EnumerateFilesWithLengthsCore(string path, WalFileNameFilter includeName) =>
        OpenForLengths(EnumerateFilesCore(path, "*").Where(file => includeName(Path.GetFileName(file.AsSpan()))));

    /// <summary>
    /// Measures a directory, as <see cref="MeasureDirectory"/> describes. A file system without reparse points or
    /// permissions sums its listings, leaving out a directory below the measured one which vanished since its parent's.
    /// </summary>
    internal virtual long MeasureDirectoryCore(string path)
    {
        var used = EnumerateFilesWithLengthsCore(path, static _ => true).Sum(static file => file.Length);
        foreach (var child in EnumerateDirectoriesCore(path))
        {
            try
            {
                used += MeasureDirectoryCore(child);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return used;
    }

    /// <summary>
    /// Reads a directory's volume space, as <see cref="GetVolumeSpace"/> describes. Only the operating system's file
    /// system has volumes.
    /// </summary>
    internal virtual WalVolumeSpace GetVolumeSpaceCore(string path) =>
        throw new NotSupportedException("This file system has no volumes.");

    /// <summary>
    /// Opens each of <paramref name="files"/> for its length, leaving out one deleted since it was enumerated.
    /// </summary>
    private protected List<WalListedFile> OpenForLengths(IEnumerable<string> files)
    {
        var listed = new List<WalListedFile>();
        foreach (var file in files)
        {
            if (LengthOf(file) is { } length)
            {
                listed.Add(new WalListedFile(file, length));
            }
        }

        return listed;
    }

    /// <summary>
    /// Deletes a file written before a cutoff in one attempt, as <see cref="TryDeleteFileWrittenBefore"/> describes. A file
    /// system without other handles or write times deletes as <see cref="DeleteFileCore"/> does.
    /// </summary>
    internal virtual bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff)
    {
        DeleteFileCore(path);
        return true;
    }

    /// <summary>
    /// Returns the length of the file at <paramref name="path"/>, or <see langword="null"/> when it no longer exists.
    /// </summary>
    private long? LengthOf(string path)
    {
        try
        {
            using var file = OpenFileCore(path, direct: false);
            return file.Length;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// A file opened through the seam, which reports each failed operation on it.
    /// </summary>
    private sealed class ReportingFile(IWalFileSystemFile file, string path) : IWalFileSystemFile
    {
        public long Length => Guard(FatalErrorSite.HandleLength, path, () => file.Length, IsFailure);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken)
        {
            try
            {
                return await file.ReadAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleRead, path, exception);
                throw;
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken)
        {
            try
            {
                return await file.ReadAsync(buffers, offset, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleReadScatter, path, exception);
                throw;
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        public async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken)
        {
            try
            {
                await file.WriteAsync(buffers, offset, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWriteGather, path, exception);
                throw;
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken)
        {
            try
            {
                await file.WriteAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWrite, path, exception);
                throw;
            }
        }

        public void SetLength(long length) => Guard(FatalErrorSite.HandleSetLength, path, () => file.SetLength(length), IsFailure);

        public void Flush() => Guard(FatalErrorSite.HandleFlush, path, file.Flush, IsFailure);

        public void Dispose() => file.Dispose();
    }

    private sealed partial class OsWalFileSystem(TimeProvider timeProvider, Func<string, int>? lockFault, Action<string>? afterReclaimOpen = null) : WalFileSystem(timeProvider)
    {
        // FILE_FLAG_NO_BUFFERING: with write-through, reads and writes bypass the system cache, and .NET submits
        // page-sized, page-aligned buffers as one WriteFileGather or ReadFileScatter.
        private const FileOptions NoBuffering = (FileOptions)0x20000000;

        // The HRESULTs .NET gives an IOException for the Win32 errors ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION
        // (33), ERROR_FILE_EXISTS (80), and ERROR_ALREADY_EXISTS (183); on Unix .NET sets an IOException's HResult to the
        // errno, and a held flock answers EWOULDBLOCK.
        private const int WindowsSharingViolation = unchecked((int)0x80070020);
        private const int WindowsLockViolation = unchecked((int)0x80070021);
        private const int WindowsFileExists = unchecked((int)0x80070050);
        private const int WindowsAlreadyExists = unchecked((int)0x800700B7);

        // The Win32 errors a deletion's own open meets: ERROR_FILE_NOT_FOUND, ERROR_ACCESS_DENIED (a deletion pending, or
        // the file's attributes), ERROR_SHARING_VIOLATION, and ERROR_LOCK_VIOLATION.
        private const int WindowsErrorFileNotFound = 2;
        private const int WindowsErrorAccessDenied = 5;
        private const int WindowsErrorSharingViolation = 32;
        private const int WindowsErrorLockViolation = 33;

        // CreateFileW's DELETE and FILE_READ_ATTRIBUTES rights and OPEN_EXISTING disposition; SetFileInformationByHandle's
        // FileDispositionInfoEx class and its FILE_DISPOSITION_FLAG_DELETE and FILE_DISPOSITION_FLAG_POSIX_SEMANTICS.
        private const uint DeleteAccess = 0x00010000;
        private const uint ReadAttributesAccess = 0x80;
        private const uint OpenExisting = 3;
        private const int FileDispositionInfoExClass = 21;
        private const uint DispositionDelete = 0x1;
        private const uint DispositionPosixSemantics = 0x2;

        // The time .NET reports for a file whose stat failed: the earliest Windows file time, 1601-01-01 UTC.
        private static readonly DateTimeOffset UnknownFileTime = DateTimeOffset.FromFileTime(0);

        // EEXIST and ENOENT, which .NET puts in an IOException's HResult on Unix and link(2) sets as errno.
        private const int UnixFileExists = 17;
        private const int UnixNoEntry = 2;

        // ENOTEMPTY on Linux (include/uapi/asm-generic/errno.h) and macOS (sys/errno.h), which .NET sets as an
        // IOException's HResult on Unix, and ERROR_DIR_NOT_EMPTY (145) as .NET's HRESULT on Windows.
        private const int WindowsDirectoryNotEmpty = unchecked((int)0x80070091);
        private const int LinuxDirectoryNotEmpty = 39;
        private const int MacOsDirectoryNotEmpty = 66;

        // Directory.EnumerateFiles's options: every entry, and an inaccessible directory is an error.
        private static readonly EnumerationOptions ListingOptions = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

        // The measurement's options: every entry, hidden and system ones included, through every directory below.
        private static readonly EnumerationOptions MeasuringOptions = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = true };

        internal override bool DirectoryExistsCore(string path) =>
            FileSystemAnswers.GetAttributes(path) is { } attributes && attributes.HasFlag(FileAttributes.Directory);

        internal override void CreateDirectoryCore(string path)
        {
            // Directory.CreateDirectory creates every missing ancestor, whose names no flush would make durable.
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } parent && !DirectoryExistsCore(parent))
            {
                throw new DirectoryNotFoundException($"The parent of the directory '{path}' does not exist.");
            }

            _ = Directory.CreateDirectory(path);
        }

        internal override void DeleteDirectoryCore(string path) => WaitOutContention(() => Directory.Delete(path, recursive: true));

        internal override bool TryDeleteEmptyDirectoryCore(string path) =>
            FileSystemAnswers.WaitOutContention(
                () =>
                {
                    try
                    {
                        Directory.Delete(path, recursive: false);
                        return true;
                    }
                    catch (IOException exception) when (IsDirectoryNotEmpty(exception))
                    {
                        // An entry, such as the lock file of an owner which arrived during a deletion, is the answer.
                        return false;
                    }
                },
                FileSystemAnswers.IsOpenContention,
                TimeProvider);

        internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern) => Directory.EnumerateFiles(path, searchPattern);

        // On Windows each enumerated entry carries its length, so no file is opened. Elsewhere .NET stats an entry for its
        // length and reads a failed stat as length 0, so each included file is opened instead; either way a name is judged
        // before its path is allocated.
        internal override IEnumerable<WalListedFile> EnumerateFilesWithLengthsCore(string path, WalFileNameFilter includeName) =>
            OperatingSystem.IsWindows()
                ? new FileSystemEnumerable<WalListedFile>(path, static (ref entry) => new WalListedFile(entry.ToFullPath(), entry.Length), ListingOptions)
                {
                    ShouldIncludePredicate = (ref entry) => !entry.IsDirectory && includeName(entry.FileName),
                }
                : OpenForLengths(new FileSystemEnumerable<string>(path, static (ref entry) => entry.ToFullPath(), ListingOptions)
                {
                    ShouldIncludePredicate = (ref entry) => !entry.IsDirectory && includeName(entry.FileName),
                });

        internal override long MeasureDirectoryCore(string path)
        {
            using var lengths = new MeasuringEnumerator(path) { Opened = true };
            long used = 0;
            while (lengths.MoveNext())
            {
                used += lengths.Current;
            }

            return used;
        }

        internal override WalVolumeSpace GetVolumeSpaceCore(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                // GetDiskFreeSpaceExW answers for the volume the path itself lies on: a mount point's own, or a share's.
                return GetDiskFreeSpaceEx(path, out var available, out var total, out _)
                    ? new WalVolumeSpace((long)available, (long)total)
                    : throw new IOException($"Reading the space of the volume holding '{path}' failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            // On Unix DriveInfo takes any path, and statvfs answers for the file system holding it.
            var volume = new DriveInfo(path);
            return new WalVolumeSpace(volume.AvailableFreeSpace, volume.TotalSize);
        }

        // Each entry's time comes with the enumeration on Windows, and from a stat elsewhere. .NET reads a failed stat, as
        // of a file removed since the enumeration, as the earliest file time, which would make any file look old; such an
        // entry is left out, since its file is gone or its time unknown.
        internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) =>
            new FileSystemEnumerable<WalListedFileWriteTime>(path, static (ref entry) => new WalListedFileWriteTime(entry.ToFullPath(), entry.LastWriteTimeUtc), ListingOptions)
            {
                ShouldIncludePredicate = (ref entry) => !entry.IsDirectory && includeName(entry.FileName) && entry.LastWriteTimeUtc != UnknownFileTime,
            };

        internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Names are atomic here: unlinking this name never removes the name a concurrent rename gave the file.
                var written = File.GetLastWriteTimeUtc(path);
                if (written == UnknownFileTime.UtcDateTime || written >= cutoff.UtcDateTime)
                {
                    return true;
                }

                File.Delete(path);
                return true;
            }

            using var handle = CreateFile(path, DeleteAccess | ReadAttributesAccess, share: 0, IntPtr.Zero, OpenExisting, flags: 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                return error switch
                {
                    WindowsErrorFileNotFound => true,
                    WindowsErrorAccessDenied or WindowsErrorSharingViolation or WindowsErrorLockViolation => false,
                    _ => throw new IOException($"Opening '{path}' to delete it failed.", new Win32Exception(error)),
                };
            }

            afterReclaimOpen?.Invoke(path);
            if (File.GetLastWriteTimeUtc(handle) >= cutoff.UtcDateTime)
            {
                return true;
            }

            // While this handle shares nothing no rename moves the file, so the deletion removes the name it opened.
            var disposition = new FileDispositionInfoEx(DispositionDelete | DispositionPosixSemantics);
            if (!SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref disposition, sizeof(uint)))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == WindowsErrorAccessDenied
                    ? false
                    : throw new IOException($"Deleting '{path}' failed.", new Win32Exception(error));
            }

            return true;
        }

        internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => Directory.EnumerateDirectories(path);

        internal override bool FileExistsCore(string path)
        {
            try
            {
                return !File.GetAttributes(path).HasFlag(FileAttributes.Directory);
            }
            catch (FileNotFoundException)
            {
                // The path's own directory exists, as .NET gives FileNotFoundException only then: a missing directory,
                // DirectoryNotFoundException, propagates as the failure it is.
                return false;
            }
        }

        internal override IDisposable OpenLockCore(string path) => OpenLockFile(path, waitOutContention: false);

        internal override IDisposable OpenLockForDeletionCore(string path) => OpenLockFile(path, waitOutContention: true);

        /// <summary>
        /// Opens a lock file and takes its lock; with <paramref name="waitOutContention"/>, a Windows sharing or lock
        /// violation is contention for up to the bound before it answers that the lock is held.
        /// </summary>
        private IDisposable OpenLockFile(string path, bool waitOutContention)
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("A lock file requires Windows, Linux, or macOS.");
            }

            while (true)
            {
                SafeFileHandle handle;
                try
                {
                    handle = waitOutContention
                        ? FileSystemAnswers.WaitOutContention(() => OpenExclusively(path), FileSystemAnswers.IsOpenContention, TimeProvider)
                        : OpenExclusively(path);
                }
                catch (IOException exception) when (IsLockHeld(exception))
                {
                    throw new WalLockHeldException($"Another owner holds the lock '{path}'.", exception);
                }

                if (OperatingSystem.IsWindows())
                {
                    return handle;
                }

                try
                {
                    if (lockFault?.Invoke(path) is { } injected and not 0)
                    {
                        throw WalFlock.ExclusiveFailure(injected, path, OperatingSystem.IsMacOS());
                    }

                    // WAL-I11 (wal.md §3): the lock excludes every other owner on the host only if it is taken on the file
                    // the path names, and .NET takes flock(2) only best-effort, so the seam takes it here, fails on every
                    // error but a held lock, and opens the path again when a deletion unlinked the file it opened.
                    if (WalFlock.LockIfNamed(handle, path))
                    {
                        return handle;
                    }
                }
                catch
                {
                    // A lock which could not be taken, but not because another owner holds it or the volume takes no flock, is
                    // a failure the open's guard reports (conventions.md §14).
                    handle.Dispose();
                    throw;
                }

                handle.Dispose();
            }
        }

#pragma warning disable CA2000 // Ownership of the handle transfers to the file.
        internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize)
        {
            try
            {
                return new OsFile(
                    File.OpenHandle(
                        path,
                        replace ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        writeThrough ? FileOptions.Asynchronous | FileOptions.WriteThrough : FileOptions.Asynchronous,
                        preallocationSize),
                    dropCacheOnFlush: OperatingSystem.IsLinux());
            }
            catch (IOException exception) when (!replace && IsNameExists(exception))
            {
                throw new WalNameExistsException($"The file '{path}' already exists.", exception);
            }
        }

        internal override IWalFileSystemFile OpenFileCore(string path, bool direct) =>
            FileSystemAnswers.WaitOutContention(() => OpenFileOnce(path, direct), exception => FileSystemAnswers.IsFileOpenContention(exception, path), TimeProvider);

        private static OsFile OpenFileOnce(string path, bool direct)
        {
            if (!direct)
            {
                return new OsFile(File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous));
            }

            if (OperatingSystem.IsWindows())
            {
                return new OsFile(File.OpenHandle(
                    path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous | FileOptions.WriteThrough | NoBuffering));
            }

            if (OperatingSystem.IsLinux())
            {
                return new OsFile(WalDirectIo.OpenLinux(path));
            }

            throw new PlatformNotSupportedException("Direct write-ahead log I/O requires Linux or Windows.");
        }
#pragma warning restore CA2000

        internal override void MoveFileCore(string source, string destination, bool replace) =>
            WaitOutContention(() => MoveOnce(source, destination, replace));

        internal override bool TryMoveFileCore(string source, string destination, bool replace, long contentionStarted) =>
            AttemptOnce(() => MoveOnce(source, destination, replace), contentionStarted);

        internal override void LinkFileCore(string existing, string link)
        {
            if (OperatingSystem.IsWindows())
            {
                MoveFileCore(existing, link, replace: false);
                return;
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("Publishing a file without replacing another requires Windows, Linux, or macOS.");
            }

            // link(2) creates the name only if it is absent; File.Move's Unix no-overwrite implementation checks for
            // absence and then renames, which a competing publisher can overtake.
            if (Link(existing, link) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                var failure = new Win32Exception(error);
                throw error switch
                {
                    UnixFileExists => new WalNameExistsException($"The file '{link}' already exists.", failure),

                    // A missing source is an answer when its directory exists, as File.Move's is, and the directory's
                    // absence is the failure it is everywhere else.
                    UnixNoEntry when DirectoryExistsCore(Path.GetDirectoryName(existing)!) && DirectoryExistsCore(Path.GetDirectoryName(link)!) =>
                        new FileNotFoundException($"The file '{existing}' does not exist.", existing, failure),
                    UnixNoEntry => new DirectoryNotFoundException($"The directory of '{existing}' or '{link}' does not exist.", failure),
                    _ => new IOException($"Publishing '{link}' failed.", failure),
                };
            }
        }

        internal override async ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                // Share every handle the seam opens to read or write a file, and namespace operations, so on Windows
                // the read overlaps an open file's reads and a concurrent no-replace publication succeeds.
                await using var stream = FileSystemAnswers.WaitOutContention(
                    () => new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 1,
                        FileOptions.Asynchronous),
                    exception => FileSystemAnswers.IsFileOpenContention(exception, path),
                    TimeProvider);
                var content = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
                return content;
            }
            catch (FileNotFoundException)
            {
                // The path's own directory exists, as .NET gives FileNotFoundException only then: a missing directory,
                // DirectoryNotFoundException, propagates as the failure it is.
                return null;
            }
        }

        internal override void DeleteFileCore(string path) => WaitOutContention(() => File.Delete(path));

        internal override bool TryDeleteFileCore(string path, long contentionStarted) => AttemptOnce(() => File.Delete(path), contentionStarted);

        internal override bool TryDeleteLockFileCore(string path)
        {
            try
            {
                return FileSystemAnswers.WaitOutContention(
                    () =>
                    {
                        File.Delete(path);
                        return true;
                    },
                    FileSystemAnswers.IsOpenContention,
                    TimeProvider);
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && IsLockHeld(exception))
            {
                // A handle still there past the bound is another owner's lock: the answer that the lock is held.
                return false;
            }
        }

        private static SafeFileHandle OpenExclusively(string path) =>
            File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        internal override void FlushDirectoryCore(string path, DirectoryEntries entries) => WalDirectorySync.Flush(path, entries);

        private static void MoveOnce(string source, string destination, bool replace)
        {
            try
            {
                File.Move(source, destination, replace);
            }
            catch (IOException exception) when (!replace && IsNameExists(exception))
            {
                throw new WalNameExistsException($"The file '{destination}' already exists.", exception);
            }
        }

        /// <summary>
        /// Runs a rename or deletion, repeating it while, on Windows, another process holds the file open without
        /// sharing deletion, as antivirus and indexing services briefly do (#106).
        /// </summary>
        private void WaitOutContention(Action operation) =>
            _ = FileSystemAnswers.WaitOutContention(
                () =>
                {
                    operation();
                    return true;
                },
                FileSystemAnswers.IsContention,
                TimeProvider);

        /// <summary>
        /// Runs a rename or deletion once, returning <see langword="false"/> when another process's handle refused it
        /// within the bound of <paramref name="contentionStarted"/>; the caller pauses outside its lock and repeats it.
        /// </summary>
        private bool AttemptOnce(Action operation, long contentionStarted)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception exception) when (FileSystemAnswers.IsContention(exception)
                && TimeProvider.GetElapsedTime(contentionStarted) < FileSystemAnswers.ContentionBound)
            {
                // Another process's handle, not this process's storage, refused the operation, which changed nothing.
                return false;
            }
        }

        private static bool IsDirectoryNotEmpty(IOException exception)
        {
            if (OperatingSystem.IsWindows())
            {
                return exception.HResult == WindowsDirectoryNotEmpty;
            }

            return exception.HResult == (OperatingSystem.IsMacOS() ? MacOsDirectoryNotEmpty : LinuxDirectoryNotEmpty);
        }

        private static bool IsNameExists(IOException exception) =>
            OperatingSystem.IsWindows()
                ? exception.HResult is WindowsFileExists or WindowsAlreadyExists
                : exception.HResult == UnixFileExists;

        private static bool IsLockHeld(IOException exception) =>
            OperatingSystem.IsWindows()
                ? exception.HResult is WindowsSharingViolation or WindowsLockViolation
                : exception.HResult == WalFlock.WouldBlock;

        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int Link(string existingPath, string newPath);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

        [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDispositionInfoEx information, uint size);

        /// <summary>
        /// Enumerates the lengths of the files beneath a directory, entering no reparse point and counting none. The
        /// directory's own open runs in the base constructor, before the creator's initializer sets
        /// <see cref="Opened"/>: an error there throws, and one below it skips the entry, which a concurrent change
        /// removed or the process cannot read.
        /// </summary>
        private sealed class MeasuringEnumerator(string path) : FileSystemEnumerator<long>(path, MeasuringOptions)
        {
            /// <summary>Gets or sets a value indicating whether the base constructor opened the directory itself.</summary>
            public bool Opened { get; init; }

            protected override bool ContinueOnError(int error) => Opened;

            protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => !entry.IsDirectory && !IsReparsePoint(ref entry);

            protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) => !IsReparsePoint(ref entry);

            protected override long TransformEntry(ref FileSystemEntry entry) => entry.Length;

            private static bool IsReparsePoint(ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        /// <summary>FILE_DISPOSITION_INFO_EX: the flags of a handle's disposition.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct FileDispositionInfoEx(uint flags)
        {
            private readonly uint _flags = flags;
        }
    }

    private sealed partial class OsFile(SafeFileHandle handle, bool dropCacheOnFlush = false) : IWalFileSystemFile
    {
        // POSIX_FADV_DONTNEED (include/uapi/linux/fadvise.h).
        private const int FadviseDontNeed = 4;

        public long Length => RandomAccess.GetLength(handle);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken);

        public ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken) =>
            RandomAccess.ReadAsync(handle, buffers, offset, cancellationToken);

        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken) =>
            RandomAccess.WriteAsync(handle, buffers, offset, cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken);

        public void SetLength(long length) => RandomAccess.SetLength(handle, length);

        public void Flush()
        {
            RandomAccess.FlushToDisk(handle);
            if (dropCacheOnFlush)
            {
                // The created file is published and then opened for direct I/O, which never reads these pages and would
                // otherwise invalidate them at each write (and keep io_uring from issuing the write inline). The advice
                // is advisory, and only performance depends on it.
                _ = PosixFadvise((int)handle.DangerousGetHandle(), 0, 0, FadviseDontNeed);
            }
        }

        public void Dispose() => handle.Dispose();

        [LibraryImport("libc", EntryPoint = "posix_fadvise")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int PosixFadvise(int descriptor, long offset, long length, int advice);
    }
}

/// <summary>
/// The kind of entry a directory flush makes durable: the kind its caller created, renamed, or deleted. On Windows it
/// chooses the right the flush opens the directory with, the right to create that kind of entry, which the caller holds.
/// </summary>
public enum DirectoryEntries
{
    /// <summary>Files; on Windows the flush needs <c>FILE_ADD_FILE</c>.</summary>
    Files,

    /// <summary>Subdirectories; on Windows the flush needs <c>FILE_ADD_SUBDIRECTORY</c>.</summary>
    Directories,
}

/// <summary>
/// A file <see cref="WalFileSystem.EnumerateFilesWithLengths"/> listed: its path and its length.
/// </summary>
/// <param name="Path">The file's path.</param>
/// <param name="Length">The file's length, in bytes.</param>
public readonly record struct WalListedFile(string Path, long Length);

/// <summary>
/// The space of the volume holding a directory, which <see cref="WalFileSystem.GetVolumeSpace"/> read.
/// </summary>
/// <param name="FreeBytes">The free space available to the process.</param>
/// <param name="TotalBytes">The volume's size.</param>
public readonly record struct WalVolumeSpace(long FreeBytes, long TotalBytes);

/// <summary>
/// A file <see cref="WalFileSystem.EnumerateFilesWithLastWriteTimes"/> listed: its path and the time of its last write.
/// </summary>
/// <param name="Path">The file's path.</param>
/// <param name="LastWriteTime">The time of the file's last write, by the file system's clock.</param>
public readonly record struct WalListedFileWriteTime(string Path, DateTimeOffset LastWriteTime);

/// <summary>
/// Whether a listing includes the file named <paramref name="fileName"/>, judged on the name without allocating it.
/// </summary>
/// <param name="fileName">The file's name, without its directory.</param>
public delegate bool WalFileNameFilter(ReadOnlySpan<char> fileName);

/// <summary>
/// A file <see cref="WalFileSystem.CreateFile"/> or <see cref="WalFileSystem.OpenFile"/> opened. Each operation on it reports
/// a failure as its file system's operations do, which stops the process (conventions.md §14).
/// </summary>
public interface IWalFileSystemFile : IDisposable
{
    /// <summary>
    /// Gets the file's length.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Reads into <paramref name="buffer"/> from <paramref name="offset"/>, returning the number of bytes read, which is
    /// zero at the end of the file.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken);

    /// <summary>
    /// Reads into <paramref name="buffers"/>, back to back, from <paramref name="offset"/>, returning the number of bytes
    /// read, which is fewer than the buffers hold only at the end of the file or when the read is interrupted.
    /// </summary>
    ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="buffers"/> back to back at <paramref name="offset"/>.
    /// </summary>
    ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="buffer"/> at <paramref name="offset"/>.
    /// </summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the file's length.
    /// </summary>
    void SetLength(long length);

    /// <summary>
    /// Makes the file's contents and length durable.
    /// </summary>
    void Flush();
}

/// <summary>
/// The answer that a name an operation would create already exists: the file system's answer, not a failure of the
/// operation (conventions.md §14).
/// </summary>
public sealed class WalNameExistsException : IOException
{
    public WalNameExistsException()
        : base("The name already exists.")
    {
    }

    public WalNameExistsException(string message)
        : base(message)
    {
    }

    public WalNameExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The answer that another owner holds a lock file: the file system's answer, not a failure of the operation
/// (conventions.md §14).
/// </summary>
public sealed class WalLockHeldException : IOException
{
    public WalLockHeldException()
        : base("Another owner holds the lock.")
    {
    }

    public WalLockHeldException(string message)
        : base(message)
    {
    }

    public WalLockHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
