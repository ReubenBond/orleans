using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Portable;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// An in-memory file system which records every mutating operation, beneath the real <c>FileWalDirectory</c>, the
/// portable storage's files, and the node root's cluster claim, so <see cref="CrashStateEnumerator"/> can build every
/// crash state its trace allows (verification.md §7.5).
/// </summary>
/// <remarks>
/// <para>
/// The operations it records reach the page cache, which <see cref="Image"/> shows, and become durable only as the
/// enumerator's persistence model says. The root and its ancestors exist durably: the root is the caller's, and a flush
/// of a directory above it throws (wal.md §3).
/// </para>
/// <para>
/// <see cref="Storage"/> composes the real directory implementation and the portable storage's files over this file
/// system. Its files follow <see cref="PortableWalStorage"/> on macOS, where I/O is buffered and every write is followed
/// by a sync of its file, or, when the file system writes through, on Linux and Windows, where I/O is direct and
/// write-through and a sync follows only a write which extends its file. A write-through write is recorded as the
/// write, flagged <see cref="FileSystemOperation.WriteThrough"/>, and, once it completes, a
/// <see cref="FileSystemOperationKind.SyncWrite"/> naming it, or, once it fails or its process crashes, a
/// <see cref="FileSystemOperationKind.FailWrite"/>. The page cache stands for what reads return: for direct I/O, the
/// device's bytes, so a failed write's bytes, which reads return, are durable once a later sync of its file completes.
/// </para>
/// <para>
/// With <see cref="HoldWrites"/>, write-through writes to matching files stay in flight until the test completes or
/// fails their <see cref="HeldWrite"/>, so writes complete out of order, or fail with their bytes on the device. With
/// <see cref="HoldFlushes"/>, an fsync of a matching file waits, before it is recorded, until the test releases its
/// <see cref="HeldFlush"/>, so a write which is not write-through stays in the page cache for as long as the test
/// chooses. With <see cref="HoldCreations"/>, a matching file's creation waits, before anything is recorded, until the
/// test releases its <see cref="HeldCreation"/>, so background work starts at a point the test chooses. With
/// <see cref="FailFlushes"/>, a matching directory's flush fails, leaving the operations it would have made durable
/// unsynced. With <see cref="RefuseOpens"/>, a matching file's open fails, as a file system which cannot serve it does.
/// </para>
/// <para>
/// One instance is one process's view of the file system. The process crashes once the trace holds
/// <see cref="CrashAfter"/> operations: every later call through this view, or a file or lock it opened, throws, and no
/// further operation is recorded. <see cref="Restart"/> returns the view of a restarted process, which sees the same
/// page cache and continues the same trace, so an operating-system crash enumerated after it follows a process crash.
/// <see cref="StartProcess"/> returns the view of another process running beside this one, so one process can crash
/// while the other keeps running. A lock excludes every other process until its holder releases it or crashes.
/// </para>
/// <para>
/// With <see cref="LinkByRenaming"/>, a publication without replacement is recorded as a rename, as the seam publishes
/// on Windows, in place of a hard link, as it publishes on Linux and macOS.
/// </para>
/// </remarks>
internal sealed class RecordingFileSystem : WalFileSystem
{
    private readonly Disk _disk;
    private readonly List<HeldWrite> _heldWrites = [];
    private readonly List<HeldCreation> _heldCreations = [];
    private readonly List<HeldFlush> _heldFlushes = [];
    private bool _crashed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingFileSystem"/> class whose root, an absolute path which
    /// exists durably, holds <paramref name="image"/> or nothing; the portable storage's files are direct and write
    /// through, as on Linux and Windows, when <paramref name="writeThrough"/> is set. Each file's creation, write, and
    /// truncation is its last write at <paramref name="timeProvider"/>'s time, the system clock's by default.
    /// </summary>
    public RecordingFileSystem(string root, FileSystemImage? image = null, bool writeThrough = false, TimeProvider? timeProvider = null)
        : this(new Disk(Normalize(root), image?.Clone() ?? new FileSystemImage(), writeThrough, timeProvider ?? TimeProvider.System))
    {
    }

    private RecordingFileSystem(Disk disk)
        : base(disk.Clock)
    {
        _disk = disk;
        Storage = new RecordingWalStorage(this);
    }

    /// <summary>
    /// Gets the absolute path of the root directory.
    /// </summary>
    public string Root => _disk.Root;

    /// <summary>
    /// Gets the storage whose directories are the real directory implementation over this file system.
    /// </summary>
    public WalStorage Storage { get; }

    /// <summary>
    /// Gets or sets which files' write-through writes wait for <see cref="HeldWrite.Complete"/> or
    /// <see cref="HeldWrite.Fail"/>, by path relative to the root.
    /// </summary>
    public Func<string, bool>? HoldWrites { get; set; }

    /// <summary>
    /// Gets or sets which files' creations wait for <see cref="HeldCreation.Release"/>, by path relative to the root.
    /// </summary>
    public Func<string, bool>? HoldCreations { get; set; }

    /// <summary>
    /// Gets or sets which files' fsyncs wait for <see cref="HeldFlush.Release"/>, by path relative to the root.
    /// </summary>
    public Func<string, bool>? HoldFlushes { get; set; }

    /// <summary>
    /// Gets or sets which directories' flushes fail, as a device error fails them, by path relative to the root (empty
    /// for the root): such a flush throws <see cref="IOException"/>, which the file-system seam reports, records nothing,
    /// and stops the process, so every later call through this view throws.
    /// </summary>
    public Func<string, bool>? FailFlushes { get; set; }

    /// <summary>
    /// Gets or sets which files' opens fail, as a file system which cannot serve a file refuses it, by path relative to
    /// the root: such an open throws <see cref="IOException"/>, which the file-system seam reports, and stops the
    /// process, so every later call through this view throws.
    /// </summary>
    public Func<string, bool>? RefuseOpens { get; set; }

    /// <summary>
    /// Gets or sets an action which runs before each whole-file read, given the path the read names, so a test can
    /// interleave another process's operations with the read.
    /// </summary>
    public Func<string, ValueTask>? BeforeRead { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each read and write yields to the scheduler before it touches the image,
    /// so concurrent callers' I/O steps interleave; in a simulation the explorer chooses among them (verification.md
    /// §4.1). A simulation yields through the simulation kit, as the test assembly is not rewritten (§7.1).
    /// </summary>
    public bool YieldsAtIo { get; set; }

    private Task YieldAtIoAsync() => YieldsAtIo ? YieldAsync() : Task.CompletedTask;

#if CLOG_SIMULATION_TESTS
    private static Task YieldAsync() => Orleans.Durable.Storage.Simulation.SimulationTasks.Yield();
#else
    private static async Task YieldAsync() => await Task.Yield();
#endif

    /// <summary>
    /// Gets or sets a value indicating whether a publication without replacement renames its file, as the seam's
    /// no-replace move on Windows does, rather than linking it, as <c>link(2)</c> on Linux and macOS does.
    /// </summary>
    public bool LinkByRenaming { get; set; }

    /// <summary>
    /// Gets or sets the number of operations in the trace after which this view's process crashes.
    /// </summary>
    public int? CrashAfter { get; set; }

    /// <summary>
    /// Gets or sets the number of operations in the trace at which the next operation fails, as a device error fails
    /// it: it records nothing and throws <see cref="IOException"/>, which the file-system seam reports, and the process
    /// stops there, as the report stops it, so every later call through this view throws.
    /// </summary>
    public int? FailOperation { get; set; }

    /// <summary>
    /// Gets a value indicating whether this view's process has crashed.
    /// </summary>
    public bool Crashed
    {
        get
        {
            lock (_disk.Sync)
            {
                return _crashed;
            }
        }
    }

    /// <summary>
    /// Gets the image the recording started from.
    /// </summary>
    public FileSystemImage InitialImage => _disk.Initial;

    /// <summary>
    /// Gets the number of operations recorded so far.
    /// </summary>
    public int OperationCount
    {
        get
        {
            lock (_disk.Sync)
            {
                return _disk.Operations.Count;
            }
        }
    }

    /// <summary>
    /// Gets a copy of the recorded operations, in issue order.
    /// </summary>
    public IReadOnlyList<FileSystemOperation> Operations
    {
        get
        {
            lock (_disk.Sync)
            {
                return [.. _disk.Operations];
            }
        }
    }

    /// <summary>
    /// Gets a copy of the page cache: every recorded operation applied.
    /// </summary>
    public FileSystemImage Image
    {
        get
        {
            lock (_disk.Sync)
            {
                return _disk.Live.Clone();
            }
        }
    }

    /// <summary>
    /// Gets the held writes, in issue order.
    /// </summary>
    public IReadOnlyList<HeldWrite> HeldWrites
    {
        get
        {
            lock (_disk.Sync)
            {
                return [.. _heldWrites];
            }
        }
    }

    /// <summary>
    /// Gets the held file creations, in the order they arrived.
    /// </summary>
    public IReadOnlyList<HeldCreation> HeldCreations
    {
        get
        {
            lock (_disk.Sync)
            {
                return [.. _heldCreations];
            }
        }
    }

    /// <summary>
    /// Gets the fsyncs waiting for <see cref="HeldFlush.Release"/>, in the order they arrived.
    /// </summary>
    public IReadOnlyList<HeldFlush> HeldFlushes
    {
        get
        {
            lock (_disk.Sync)
            {
                return [.. _heldFlushes];
            }
        }
    }

    /// <summary>
    /// Crashes this view's process, if it is still running, and returns the view of a restarted process: the same page
    /// cache and trace. Every held write of the crashed process fails, as its process's crash leaves it to the device,
    /// and its held creations and fsyncs fail once released.
    /// </summary>
    public RecordingFileSystem Restart()
    {
        HeldWrite[] writes;
        HeldCreation[] creations;
        HeldFlush[] flushes;
        lock (_disk.Sync)
        {
            _crashed = true;
            writes = [.. _heldWrites];
            creations = [.. _heldCreations];
            flushes = [.. _heldFlushes];
            _heldWrites.Clear();
            _heldCreations.Clear();
            _heldFlushes.Clear();
            foreach (var write in writes)
            {
                var failed = new FileSystemOperation(FileSystemOperationKind.FailWrite, write.Node, write.Path, CompletedWrite: write.Operation);
                _disk.Live.Apply(failed);
                _disk.Operations.Add(failed);
            }
        }

        foreach (var flush in flushes)
        {
            flush.Release();
        }

        foreach (var write in writes)
        {
            write.Crash();
        }

        foreach (var creation in creations)
        {
            creation.Release();
        }

        return new RecordingFileSystem(_disk) { LinkByRenaming = LinkByRenaming };
    }

    /// <summary>
    /// Returns the view of another process which runs beside this one over the same page cache and trace, so either
    /// can crash while the other keeps running.
    /// </summary>
    public RecordingFileSystem StartProcess() => new(_disk) { LinkByRenaming = LinkByRenaming };

    internal override bool DirectoryExistsCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            return IsAboveRoot(path) || Locate(path) is { Node: >= 0 } location && _disk.Live.IsDirectory(location.Node);
        }
    }

    internal override void CreateDirectoryCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            if (location.Parent < 0)
            {
                throw new DirectoryNotFoundException($"The parent of '{location.Relative}' does not exist.");
            }

            if (location.Node >= 0)
            {
                if (!_disk.Live.IsDirectory(location.Node))
                {
                    throw new IOException($"'{location.Relative}' is a file.");
                }

                return;
            }

            Record(new FileSystemOperation(FileSystemOperationKind.MakeDirectory, _disk.NextNode++, location.Relative, location.Parent, location.Name));
        }
    }

    internal override void DeleteDirectoryCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            if (location.Node < 0 || !_disk.Live.IsDirectory(location.Node) || location.Parent < 0)
            {
                throw new DirectoryNotFoundException($"The directory '{location.Relative}' does not exist.");
            }

            Remove(location.Parent, location.Name, location.Node, location.Relative);
        }
    }

    internal override bool TryDeleteEmptyDirectoryCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            if (location.Node < 0 || !_disk.Live.IsDirectory(location.Node) || location.Parent < 0)
            {
                throw new DirectoryNotFoundException($"The directory '{location.Relative}' does not exist.");
            }

            if (_disk.Live.Entries(location.Node).Count != 0)
            {
                return false;
            }

            Remove(location.Parent, location.Name, location.Node, location.Relative);
            return true;
        }
    }

    internal override bool TryDeleteLockFileCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();

            // As the operating system does, Windows refuses to delete a lock file another owner holds, and Linux and
            // macOS unlink it.
            if (OperatingSystem.IsWindows() && Locate(path) is { Node: >= 0 } location
                && _disk.Locks.TryGetValue(location.Node, out var holder) && !holder._crashed)
            {
                return false;
            }

            DeleteFileCore(path);
            return true;
        }
    }

    internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern)
    {
        if (searchPattern.Length == 0 || searchPattern[0] != '*' || searchPattern.AsSpan(1).IndexOfAny('*', '?') >= 0)
        {
            throw new NotSupportedException($"The recording file system matches only '*suffix' patterns, not '{searchPattern}'.");
        }

        var suffix = searchPattern[1..];
        return [.. List(path, directories: false).Where(name => name.EndsWith(suffix, StringComparison.Ordinal)).Select(name => Path.Combine(path, name))];
    }

    internal override IEnumerable<string> EnumerateDirectoriesCore(string path) =>
        [.. List(path, directories: true).Select(name => Path.Combine(path, name))];

    internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName)
    {
        var listed = new List<WalListedFileWriteTime>();
        foreach (var name in List(path, directories: false).Where(name => includeName(name)))
        {
            var file = Path.Combine(path, name);
            lock (_disk.Sync)
            {
                if (Locate(file).Node is var node and >= 0)
                {
                    listed.Add(new WalListedFileWriteTime(file, _disk.Live.LastWrite(node)));
                }
            }
        }

        return listed;
    }

    /// <summary>
    /// Deletes a file written before <paramref name="cutoff"/>, by its recorded last write, as an unlink; the recording
    /// models no other process's handle on a file, so nothing refuses it.
    /// </summary>
    internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = InExistingDirectory(Locate(path));
            if (location.Node < 0 || _disk.Live.LastWrite(location.Node) >= cutoff)
            {
                return true;
            }
        }

        DeleteFileCore(path);
        return true;
    }

    internal override bool FileExistsCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = InExistingDirectory(Locate(path));
            return location.Node >= 0 && !_disk.Live.IsDirectory(location.Node);
        }
    }

    /// <summary>
    /// Opens a deletion's lock as any lock: the recording models no other process's handles.
    /// </summary>
    internal override IDisposable OpenLockForDeletionCore(string path) => OpenLockCore(path);

    internal override IDisposable OpenLockCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            var node = location.Node >= 0 ? location.Node : Create(location);

            // A crashed process's locks end with it, as the operating system releases them.
            if (_disk.Locks.TryGetValue(node, out var holder) && !holder._crashed)
            {
                throw new WalLockHeldException($"The lock '{location.Relative}' is held.");
            }

            _disk.Locks[node] = this;
            return new HeldLock(this, node);
        }
    }

    internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize)
    {
        HeldCreation? held = null;
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var relative = Locate(path).Relative;
            if (HoldCreations?.Invoke(relative) == true)
            {
                held = new HeldCreation(relative);
                _heldCreations.Add(held);
            }
        }

        // The creating thread waits, as a slow creation would, before anything is recorded.
        held?.Wait();
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            int node;
            if (location.Node < 0)
            {
                node = Create(location);
            }
            else if (!replace || _disk.Live.IsDirectory(location.Node))
            {
                throw new WalNameExistsException($"The file '{location.Relative}' already exists.");
            }
            else
            {
                node = location.Node;
                Record(new FileSystemOperation(FileSystemOperationKind.Truncate, node, location.Relative));
            }

            // Preallocation reserves space without changing the length, so it records nothing.
            return new Handle(this, node, location.Relative, writeThrough);
        }
    }

    internal override void MoveFileCore(string source, string destination, bool replace)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var from = InExistingDirectory(Locate(source));
            var to = InExistingDirectory(Locate(destination));
            if (from.Parent != to.Parent)
            {
                throw new NotSupportedException("The recording file system renames only within one directory.");
            }

            if (from.Node < 0 || _disk.Live.IsDirectory(from.Node))
            {
                throw new FileNotFoundException($"The file '{from.Relative}' does not exist.", source);
            }

            if (to.Node >= 0 && (!replace || _disk.Live.IsDirectory(to.Node)))
            {
                throw new WalNameExistsException($"The file '{to.Relative}' already exists.");
            }

            Record(new FileSystemOperation(FileSystemOperationKind.Rename, from.Node, from.Relative, from.Parent, from.Name, to.Name));
        }
    }

    internal override void LinkFileCore(string existing, string link)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var from = InExistingDirectory(Locate(existing));
            var to = InExistingDirectory(Locate(link));
            if (from.Parent != to.Parent)
            {
                throw new NotSupportedException("The recording file system links only within one directory.");
            }

            if (from.Node < 0 || _disk.Live.IsDirectory(from.Node))
            {
                throw new FileNotFoundException($"The file '{from.Relative}' does not exist.", existing);
            }

            if (to.Node >= 0)
            {
                throw new WalNameExistsException($"'{to.Relative}' already exists.");
            }

            Record(LinkByRenaming
                ? new FileSystemOperation(FileSystemOperationKind.Rename, from.Node, from.Relative, from.Parent, from.Name, to.Name)
                : new FileSystemOperation(FileSystemOperationKind.Link, from.Node, from.Relative, from.Parent, to.Name));
        }
    }

    internal override async ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await YieldAtIoAsync();
        if (BeforeRead is { } beforeRead)
        {
            await beforeRead(path).ConfigureAwait(false);
        }

        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = InExistingDirectory(Locate(path));
            return location.Node >= 0 && !_disk.Live.IsDirectory(location.Node)
                ? _disk.Live.ReadAll(location.Node)
                : null;
        }
    }

    internal override void DeleteFileCore(string path)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            if (location.Parent < 0)
            {
                throw new DirectoryNotFoundException($"The directory of '{location.Relative}' does not exist.");
            }

            if (location.Node >= 0)
            {
                if (_disk.Live.IsDirectory(location.Node))
                {
                    throw new UnauthorizedAccessException($"'{location.Relative}' is a directory.");
                }

                Record(new FileSystemOperation(FileSystemOperationKind.Unlink, location.Node, location.Relative, location.Parent, location.Name));
            }
        }
    }

    internal override void FlushDirectoryCore(string path, DirectoryEntries entries)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            if (IsAboveRoot(path))
            {
                // The root is the caller's: every directory Clog flushes is the root or lies beneath it (wal.md §3).
                throw new InvalidOperationException($"'{path}' lies above the root '{_disk.Root}', which no flush may reach.");
            }

            var location = Locate(path);
            if (location.Node < 0 || !_disk.Live.IsDirectory(location.Node))
            {
                throw new DirectoryNotFoundException($"The directory '{location.Relative}' does not exist.");
            }

            if (FailFlushes?.Invoke(location.Relative) == true)
            {
                // The failure, which the seam reports, stops the process there (conventions.md §14).
                _crashed = true;
                throw new IOException($"The flush of the directory '{location.Relative}' failed.");
            }

            Record(new FileSystemOperation(FileSystemOperationKind.FlushDirectory, location.Node, location.Relative));
        }
    }

    // Direct I/O changes only which buffers a read or write may take, so the recording file system's direct handles are
    // its write-through ones; reads see the page cache, which holds what the device holds.
    internal override IWalFileSystemFile OpenFileCore(string path, bool direct)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = InExistingDirectory(Locate(path));
            if (location.Node < 0 || _disk.Live.IsDirectory(location.Node))
            {
                throw new FileNotFoundException($"The file '{location.Relative}' does not exist.", path);
            }

            if (RefuseOpens?.Invoke(location.Relative) == true)
            {
                // The failure, which the seam reports, stops the process there (conventions.md §14).
                _crashed = true;
                throw new IOException($"The file system refuses to serve '{location.Relative}'.");
            }

            return new Handle(this, location.Node, location.Relative, direct);
        }
    }

    /// <summary>
    /// Records a write and returns its completion: immediate, or once the test completes the held write.
    /// </summary>
    private ValueTask Write(int node, string path, long offset, byte[] bytes, bool writeThrough)
    {
        HeldWrite? held = null;
        lock (_disk.Sync)
        {
            var operation = _disk.Operations.Count;
            Record(new FileSystemOperation(FileSystemOperationKind.Write, node, path, Offset: offset, Bytes: bytes, WriteThrough: writeThrough));
            if (HoldWrites?.Invoke(path) == true && writeThrough)
            {
                held = new HeldWrite(this, node, path, operation);
                _heldWrites.Add(held);
            }
            else if (writeThrough)
            {
                Record(new FileSystemOperation(FileSystemOperationKind.SyncWrite, node, path, CompletedWrite: operation));
            }
        }

        return held is null ? ValueTask.CompletedTask : new ValueTask(held.Completion.Task);
    }

    private void ReleaseHeld(HeldWrite write, bool completed)
    {
        lock (_disk.Sync)
        {
            if (!_heldWrites.Remove(write))
            {
                throw new InvalidOperationException("The write is no longer held.");
            }

            Record(new FileSystemOperation(
                completed ? FileSystemOperationKind.SyncWrite : FileSystemOperationKind.FailWrite, write.Node, write.Path, CompletedWrite: write.Operation));

            // A failed write stops the process where the seam reports it (conventions.md §14).
            _crashed |= !completed;
        }
    }

    private List<string> List(string path, bool directories)
    {
        lock (_disk.Sync)
        {
            ThrowIfCrashed();
            var location = Locate(path);
            if (location.Node < 0 || !_disk.Live.IsDirectory(location.Node))
            {
                throw new DirectoryNotFoundException($"The directory '{location.Relative}' does not exist.");
            }

            return [.. _disk.Live.Entries(location.Node).Where(entry => _disk.Live.IsDirectory(entry.Node) == directories).Select(static entry => entry.Name)];
        }
    }

    /// <summary>
    /// Returns <paramref name="location"/>, a file's, when its directory exists, and throws
    /// <see cref="DirectoryNotFoundException"/> otherwise, as the operating system's file system answers a missing file
    /// only within an existing directory.
    /// </summary>
    private static Location InExistingDirectory(Location location) =>
        location.Parent >= 0 || location.Node >= 0 ? location : throw new DirectoryNotFoundException($"The directory of '{location.Relative}' does not exist.");

    private int Create(Location location)
    {
        if (location.Parent < 0)
        {
            throw new DirectoryNotFoundException($"The directory of '{location.Relative}' does not exist.");
        }

        var node = _disk.NextNode++;
        Record(new FileSystemOperation(FileSystemOperationKind.CreateFile, node, location.Relative, location.Parent, location.Name));
        return node;
    }

    private void Remove(int parent, string name, int node, string relative)
    {
        if (_disk.Live.IsDirectory(node))
        {
            foreach (var (childName, child) in _disk.Live.Entries(node))
            {
                Remove(node, childName, child, relative + "/" + childName);
            }

            Record(new FileSystemOperation(FileSystemOperationKind.RemoveDirectory, node, relative, parent, name));
        }
        else
        {
            Record(new FileSystemOperation(FileSystemOperationKind.Unlink, node, relative, parent, name));
        }
    }

    /// <summary>
    /// Records an operation, unless the process has crashed, and crashes the process once the trace reaches
    /// <see cref="CrashAfter"/> operations; fails it instead, and crashes the process, when the trace holds
    /// <see cref="FailOperation"/> operations.
    /// </summary>
    private void Record(FileSystemOperation operation)
    {
        ThrowIfCrashed();
        if (_disk.Operations.Count == FailOperation)
        {
            // The seam reports the failure, which stops the process there (conventions.md §14).
            _crashed = true;
            throw new IOException($"An injected device error failed the {operation.Kind} of '{operation.Path}'.");
        }

        if (operation.Kind is FileSystemOperationKind.CreateFile or FileSystemOperationKind.Write or FileSystemOperationKind.Truncate)
        {
            operation = operation with { Time = _disk.Clock.GetUtcNow() };
        }

        _disk.Live.Apply(operation);
        _disk.Operations.Add(operation);
        _crashed = _disk.Operations.Count == CrashAfter;
    }

    private void ThrowIfCrashed()
    {
        if (_crashed)
        {
            throw new CrashedProcessException();
        }
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> is a proper ancestor of the root.
    /// </summary>
    private bool IsAboveRoot(string path)
    {
        var directory = Normalize(path);
        return _disk.Root.Length > directory.Length
            && _disk.Root.StartsWith(directory, StringComparison.Ordinal)
            && (directory.EndsWith(Path.DirectorySeparatorChar) || _disk.Root[directory.Length] == Path.DirectorySeparatorChar);
    }

    private IEnumerable<string> Components(string path)
    {
        var relative = Path.GetRelativePath(_disk.Root, Path.GetFullPath(path));
        if (relative == ".")
        {
            return [];
        }

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{path}' lies outside the recording file system's root '{_disk.Root}'.");
        }

        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Resolves a path to its parent directory, its name, and its inode, each -1 or empty when absent.
    /// </summary>
    private Location Locate(string path)
    {
        var components = Components(path).ToArray();
        if (components.Length == 0)
        {
            return new Location(-1, string.Empty, FileSystemImage.Root, string.Empty);
        }

        var parent = FileSystemImage.Root;
        foreach (var name in components[..^1])
        {
            parent = _disk.Live.Lookup(parent, name);
            if (parent < 0 || !_disk.Live.IsDirectory(parent))
            {
                return new Location(-1, components[^1], -1, string.Join('/', components));
            }
        }

        return new Location(parent, components[^1], _disk.Live.Lookup(parent, components[^1]), string.Join('/', components));
    }

    /// <summary>
    /// A write-through write held in the page cache until the test completes it.
    /// </summary>
    internal sealed class HeldWrite
    {
        private readonly RecordingFileSystem _fileSystem;

        internal HeldWrite(RecordingFileSystem fileSystem, int node, string path, int operation)
        {
            _fileSystem = fileSystem;
            Node = node;
            Path = path;
            Operation = operation;
        }

        /// <summary>
        /// Gets the written file's path relative to the root.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets the index of the write in the trace.
        /// </summary>
        public int Operation { get; }

        internal int Node { get; }

        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes the write, making it durable alone.
        /// </summary>
        public void Complete()
        {
            _fileSystem.ReleaseHeld(this, completed: true);
            _ = Completion.TrySetResult();
        }

        /// <summary>
        /// Fails the write as a device error does once its bytes reached the device: reads return them, and they stay
        /// unsynced until a later flush of the file completes, which makes them durable. The failure, which the
        /// file-system seam reports, stops the process, so every later call through this view throws.
        /// </summary>
        public void Fail()
        {
            _fileSystem.ReleaseHeld(this, completed: false);
            _ = Completion.TrySetException(new IOException("The write failed."));
        }

        internal void Crash() => _ = Completion.TrySetException(new CrashedProcessException());
    }

    /// <summary>
    /// A file creation which waits until the test releases it.
    /// </summary>
    internal sealed class HeldCreation(string path)
    {
        private readonly ManualResetEventSlim _released = new();

        /// <summary>
        /// Gets the created file's path relative to the root.
        /// </summary>
        public string Path { get; } = path;

        /// <summary>
        /// Lets the creation proceed.
        /// </summary>
        public void Release() => _released.Set();

        internal void Wait() => _released.Wait();
    }

    /// <summary>
    /// An fsync which waits until the test releases it.
    /// </summary>
    internal sealed class HeldFlush(string path)
    {
        private readonly ManualResetEventSlim _released = new();

        /// <summary>
        /// Gets the flushed file's path relative to the root.
        /// </summary>
        public string Path { get; } = path;

        /// <summary>
        /// Lets the fsync proceed.
        /// </summary>
        public void Release() => _released.Set();

        internal void Wait() => _released.Wait();
    }

    private readonly record struct Location(int Parent, string Name, int Node, string Relative);

    /// <summary>
    /// The page cache and the trace.
    /// </summary>
    private sealed class Disk(string root, FileSystemImage image, bool writeThrough, TimeProvider clock)
    {
        public bool WriteThrough { get; } = writeThrough;

        /// <summary>
        /// Gets the clock whose time each creation, write, and truncation records as its file's last write.
        /// </summary>
        public TimeProvider Clock { get; } = clock;

        public System.Threading.Lock Sync { get; } = new();

        public string Root { get; } = root;

        public FileSystemImage Initial { get; } = image.Clone();

        public FileSystemImage Live { get; } = image;

        public List<FileSystemOperation> Operations { get; } = [];

        public int NextNode { get; set; } = image.NextNode;

        /// <summary>
        /// Gets the process holding each lock file's inode.
        /// </summary>
        public Dictionary<int, RecordingFileSystem> Locks { get; } = [];
    }

    private sealed class HeldLock(RecordingFileSystem fileSystem, int node) : IDisposable
    {
        public void Dispose()
        {
            lock (fileSystem._disk.Sync)
            {
                if (fileSystem._disk.Locks.TryGetValue(node, out var holder) && holder == fileSystem)
                {
                    _ = fileSystem._disk.Locks.Remove(node);
                }
            }
        }
    }

    /// <summary>
    /// A created or opened file.
    /// </summary>
    private sealed class Handle(RecordingFileSystem fileSystem, int node, string path, bool writeThrough) : IWalFileSystemFile
    {
        public long Length
        {
            get
            {
                lock (fileSystem._disk.Sync)
                {
                    fileSystem.ThrowIfCrashed();
                    return fileSystem._disk.Live.Length(node);
                }
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await fileSystem.YieldAtIoAsync();
            lock (fileSystem._disk.Sync)
            {
                fileSystem.ThrowIfCrashed();
                var bytes = fileSystem._disk.Live.Read(node, offset, buffer.Length);
                bytes.CopyTo(buffer);
                return bytes.Length;
            }
        }

        public async ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken)
        {
            long total = 0;
            foreach (var buffer in buffers)
            {
                var read = await ReadAsync(buffer, offset + total, cancellationToken).ConfigureAwait(false);
                total += read;
                if (read < buffer.Length)
                {
                    break;
                }
            }

            return total;
        }

        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(buffers.SelectMany(static buffer => buffer.ToArray()).ToArray(), offset, cancellationToken);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = buffer.ToArray();
            await fileSystem.YieldAtIoAsync();
            await fileSystem.Write(node, path, offset, bytes, writeThrough);
        }

        public void SetLength(long length)
        {
            lock (fileSystem._disk.Sync)
            {
                fileSystem.Record(new FileSystemOperation(FileSystemOperationKind.Truncate, node, path, Offset: length));
            }
        }

        public void Flush()
        {
            HeldFlush? held = null;
            lock (fileSystem._disk.Sync)
            {
                fileSystem.ThrowIfCrashed();
                if (fileSystem.HoldFlushes?.Invoke(path) == true)
                {
                    held = new HeldFlush(path);
                    fileSystem._heldFlushes.Add(held);
                }
            }

            // The flushing thread waits, as a slow fsync would, before the fsync is recorded.
            held?.Wait();
            lock (fileSystem._disk.Sync)
            {
                if (held is not null)
                {
                    _ = fileSystem._heldFlushes.Remove(held);
                }

                fileSystem.Record(new FileSystemOperation(FileSystemOperationKind.Flush, node, path));
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The real directory implementation and the portable storage's files over the recording file system.
    /// </summary>
    private sealed class RecordingWalStorage(RecordingFileSystem fileSystem) : WalStorage
    {
        public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            FileWalDirectory.Create(path, fileSystem, cancellationToken);
            return ValueTask.CompletedTask;
        }

#pragma warning disable CA2000 // Ownership of the directory transfers to the caller.
        public override ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult<WalDirectory>(FileWalDirectory.Open(path, fileSystem, OpenFile, cancellationToken));
#pragma warning restore CA2000

        public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            FileWalDirectory.Delete(path, fileSystem, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FileWalDirectory.ListDirectories(path, fileSystem, cancellationToken));

        public override ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FileWalDirectory.Measure(path, fileSystem, cancellationToken));

        private WalFile OpenFile(string path, bool flush) => PortableWalFile.Open(fileSystem, path, flush, direct: fileSystem._disk.WriteThrough);
    }
}
