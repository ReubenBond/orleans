using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.Engine.Wal.Simulation;

/// <summary>
/// A deterministic in-memory storage with separate cached and durable bytes, torn writes, process crashes, and
/// operating-system crashes.
/// </summary>
/// <remarks>
/// <para>
/// A write's bytes reach the cache when it is issued and become durable when it completes. With
/// <see cref="HoldWrites"/>, writes stay in flight until <see cref="CompleteWrite()"/> completes them, so a test chooses
/// the crash point. <see cref="Crash"/> returns the storage a restarted process observes: a process crash keeps every
/// cached byte; an operating-system crash keeps durable bytes and, for each unfinished write, an arbitrary subset of
/// its sectors, each old, new, or torn. Choices come from the seed.
/// </para>
/// <para>
/// A file name is published durably with its header, as <see cref="WalDirectory.CreateFileAsync"/> requires, unless
/// <see cref="DirectoryFlushFault"/> fails the flush, which leaves the name change unsynced until the directory's next
/// flush.
/// </para>
/// <para>
/// Each fault hook injects a failed local storage operation, which the storage reports to
/// <see cref="FatalErrorHandler"/> with the operation the file-system seam would report, and which stops the process
/// (conventions.md §14). The storage then stops where the process stops: every later call throws, carrying the reported
/// failure as its inner exception, without reaching the device or reporting, and <see cref="Crash"/> returns the storage
/// the restarted process observes. A call through a crashed process's storage, and a write it left in flight, fails with
/// <see cref="CrashedProcessException"/>: the process crashed, so it reports nothing.
/// </para>
/// </remarks>
public sealed class SimulatedWalStorage : WalStorage
{
    // The zero-fill write size whose pacing a created file awaits.
    private const int FillChunkLength = 64 << 10;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, SimulatedDirectory> _directories;
    private readonly List<PendingWrite> _pending = [];
    private readonly Random _random;
    private readonly int _minimumPageSize;
    private readonly int _sectorSize;
    private readonly bool _requiresAlignedBuffers;
    private bool _crashed;

    // The injected failure which stopped this process's storage, or null while it runs.
    private Exception? _stopFault;
    private int _openHandles;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedWalStorage"/> class.
    /// </summary>
    /// <param name="seed">The seed for every choice the storage makes.</param>
    /// <param name="minimumPageSize">The page size the storage reports as its minimum.</param>
    /// <param name="sectorSize">The unit in which a crash tears an unfinished write.</param>
    /// <param name="requiresAlignedBuffers">Whether every write must consist of whole aligned pages.</param>
    public SimulatedWalStorage(int seed = 0, int minimumPageSize = WalOptions.MinimumPageSize, int sectorSize = 512, bool requiresAlignedBuffers = false)
        : this(seed, minimumPageSize, sectorSize, requiresAlignedBuffers, [with(StringComparer.Ordinal)])
    {
    }

    private SimulatedWalStorage(int seed, int minimumPageSize, int sectorSize, bool requiresAlignedBuffers, Dictionary<string, SimulatedDirectory> directories)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
#pragma warning disable CA5394 // Simulation choices come from a reproducible seed, not a security boundary.
        _random = new Random(seed);
#pragma warning restore CA5394
        _minimumPageSize = minimumPageSize;
        _sectorSize = sectorSize;
        _requiresAlignedBuffers = requiresAlignedBuffers;
        _directories = directories;
    }

    /// <summary>
    /// Gets or sets a value indicating whether writes stay in flight until <see cref="CompleteWrite()"/>.
    /// </summary>
    public bool HoldWrites { get; set; }

    /// <summary>
    /// Gets or sets the space of the volume holding each simulated directory, which a measurement reports: by default
    /// 1 TiB free of 2 TiB.
    /// </summary>
    public Func<string, (long FreeBytes, long TotalBytes)> VolumeSpace { get; set; } = static _ => (1L << 40, 1L << 41);

    /// <summary>
    /// Gets or sets a hook which may fail a write before any of its bytes are written. It receives the directory
    /// path, file number, offset, and length.
    /// </summary>
    public Func<string, ulong, long, int, Exception?>? WriteFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a write of a log file after a seeded subset of its sectors, each old, new, or
    /// torn, reached the device, as a failed direct write leaves them: reads return exactly those bytes, and they are
    /// durable. It receives the directory path, file number, offset, and length.
    /// </summary>
    public Func<string, ulong, long, int, Exception?>? TornWriteFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a read before it returns any bytes. It receives the directory path,
    /// file number, offset, and length.
    /// </summary>
    public Func<string, ulong, long, int, Exception?>? ReadFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a named-file read before it returns any bytes. It receives the directory
    /// path, file name, offset, and length.
    /// </summary>
    public Func<string, string, long, int, Exception?>? NamedReadFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a named-file creation or write before any of its bytes are written. It
    /// receives the directory path, file name, offset, and length; a creation writes at offset 0.
    /// </summary>
    public Func<string, string, long, int, Exception?>? NamedWriteFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail the directory flush with which a log or named file's creation or deletion
    /// makes its name change durable. The change stays unsynced, and an operating-system crash keeps or loses it, until
    /// the directory's next flush. The failure stops the process (conventions §14), so a test models the next
    /// incarnation, whose open makes that flush, as the file-system directories do. It receives the directory path.
    /// </summary>
    public Func<string, Exception?>? DirectoryFlushFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a named file's deletion before it removes the file. It receives the directory
    /// path and the file name.
    /// </summary>
    public Func<string, string, Exception?>? NamedDeleteFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a log file's deletion before it removes the file. It receives the directory path
    /// and the file number.
    /// </summary>
    public Func<string, ulong, Exception?>? DeleteFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a log file's staged preparation (<see cref="WalDirectory.PrepareFileAsync"/>)
    /// before it completes. It receives the directory path and the preallocated length.
    /// </summary>
    public Func<string, long, Exception?>? PrepareFault { get; set; }

    /// <summary>
    /// Gets or sets a hook which may fail a named-file creation after its name and contents are durable, as a creation
    /// whose rename published the file can fail afterwards. It receives the directory path and the file name.
    /// </summary>
    public Func<string, string, Exception?>? NamedPublishedFault { get; set; }

    /// <summary>
    /// Gets the number of writes in flight.
    /// </summary>
    public int PendingWriteCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether an injected fault stopped this process's storage, so that the process answers
    /// nothing more (conventions.md §14).
    /// </summary>
    public bool IsStopped
    {
        get
        {
            lock (_sync)
            {
                return _stopFault is not null;
            }
        }
    }

    /// <summary>
    /// Gets the number of writes issued so far.
    /// </summary>
    public long IssuedWriteCount { get; private set; }

    /// <summary>
    /// Gets the number of directory and file handles opened on this storage and not yet disposed.
    /// </summary>
    public int OpenHandleCount
    {
        get
        {
            lock (_sync)
            {
                return _openHandles;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Simulated directories are keyed by path and need no parent; a name is durable once created.
    /// </remarks>
    public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfCrashed();
            _ = _directories.TryAdd(path, new SimulatedDirectory(path));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfCrashed();
            if (!_directories.TryGetValue(path, out var directory))
            {
                throw new DirectoryNotFoundException($"The simulated directory '{path}' does not exist.");
            }

            // Taking ownership flushes the directory, as the file-system directories do.
            var handle = OpenDirectoryHandle(directory);
            directory.Flush();
            return ValueTask.FromResult<WalDirectory>(handle);
        }
    }

    /// <inheritdoc/>
    public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfCrashed();
            if (_directories.TryGetValue(path, out var directory) && directory.Owned)
            {
                throw new WalLockHeldException($"The simulated directory '{path}' is owned.");
            }

            _ = _directories.Remove(path);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = System.IO.Path.TrimEndingDirectorySeparator(path);
        lock (_sync)
        {
            ThrowIfCrashed();
            return ValueTask.FromResult<IReadOnlyList<string>>(
            [
                .. _directories.Keys
                    .Where(key => string.Equals(System.IO.Path.GetDirectoryName(key), parent, StringComparison.Ordinal))
                    .Select(static key => System.IO.Path.GetFileName(key))
                    .Order(StringComparer.Ordinal),
            ]);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Simulated directories have no reparse points or permissions: the measurement sums the cached lengths of the files
    /// in the directory and in every directory whose path lies beneath it, and reports <see cref="VolumeSpace"/>.
    /// </remarks>
    public override ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = System.IO.Path.TrimEndingDirectorySeparator(path);
        var prefix = root + System.IO.Path.DirectorySeparatorChar;
        long used;
        lock (_sync)
        {
            ThrowIfCrashed();
            if (!_directories.ContainsKey(root))
            {
                throw new DirectoryNotFoundException($"The simulated directory '{path}' does not exist.");
            }

            used = _directories.Values
                .Where(directory => directory.Path == root || directory.Path.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(static directory => directory.Files.Values.Concat(directory.Named.Values))
                .Sum(static file => (long)file.Cached.Count);
        }

        var (free, total) = VolumeSpace(root);
        return ValueTask.FromResult(new WalDirectoryUsage(used, free, total));
    }

    /// <summary>
    /// Completes one write in flight, chosen by the seed, making its bytes durable.
    /// </summary>
    /// <returns><see langword="false"/> when no write is in flight.</returns>
    public bool CompleteWrite()
    {
        PendingWrite write;
        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return false;
            }

            var index = _random.Next(_pending.Count);
            write = _pending[index];
            _pending.RemoveAt(index);
            write.File.MakeDurable(write.Offset, write.Bytes);
        }

        _ = write.Completion.TrySetResult();
        return true;
    }

    /// <summary>
    /// Completes the first write in flight, in issue order, which <paramref name="select"/> chooses by its named file's
    /// name (<see langword="null"/> for a log file), file number, and offset, making its bytes durable.
    /// </summary>
    /// <returns><see langword="false"/> when no write in flight is chosen.</returns>
    public bool CompleteWrite(Func<string?, ulong, long, bool> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        PendingWrite write;
        lock (_sync)
        {
            var index = _pending.FindIndex(pending => select(pending.File.Name, pending.File.Number, pending.Offset));
            if (index < 0)
            {
                return false;
            }

            write = _pending[index];
            _pending.RemoveAt(index);
            write.File.MakeDurable(write.Offset, write.Bytes);
        }

        _ = write.Completion.TrySetResult();
        return true;
    }

    /// <summary>
    /// Crashes the process, or the operating system when <paramref name="operatingSystem"/> is set, and returns the
    /// storage a restarted process observes. Every write in flight here then fails.
    /// </summary>
    public SimulatedWalStorage Crash(bool operatingSystem)
    {
        List<PendingWrite> unfinished;
        SimulatedWalStorage restarted;
        lock (_sync)
        {
            if (_crashed)
            {
                throw new CrashedProcessException("The simulated system crashed.");
            }

            _crashed = true;
            unfinished = [.. _pending];
            _pending.Clear();
            var directories = new Dictionary<string, SimulatedDirectory>(StringComparer.Ordinal);
            foreach (var (path, directory) in _directories)
            {
                directories.Add(path, directory.Restart(operatingSystem, _random));
            }

            if (operatingSystem)
            {
                foreach (var write in unfinished)
                {
                    var directory = directories[write.File.Directory.Path];
                    if (write.File.Name is { } name ? directory.Named.TryGetValue(name, out var target) : directory.Files.TryGetValue(write.File.Number, out target))
                    {
                        TearInto(target, write);
                    }
                }
            }

            restarted = new SimulatedWalStorage(_random.Next(), _minimumPageSize, _sectorSize, _requiresAlignedBuffers, directories);
        }

        foreach (var write in unfinished)
        {
            _ = write.Completion.TrySetException(new CrashedProcessException("The simulated system crashed."));
        }

        return restarted;
    }

    /// <summary>
    /// Returns a copy of a file's durable bytes.
    /// </summary>
    public byte[] ReadDurable(string path, ulong file)
    {
        lock (_sync)
        {
            return [.. _directories[path].Files[file].Durable];
        }
    }

    /// <summary>
    /// Replaces bytes of a file, both cached and durable, as media damage would.
    /// </summary>
    public void Damage(string path, ulong file, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            _directories[path].Files[file].MakeDurable(offset, bytes.ToArray());
        }
    }

    /// <summary>
    /// Returns a copy of a named file's durable bytes, or <see langword="null"/> when it does not exist.
    /// </summary>
    public byte[]? ReadDurableFile(string path, string name)
    {
        lock (_sync)
        {
            return _directories[path].Named.TryGetValue(name, out var file) ? [.. file.Durable] : null;
        }
    }

    /// <summary>
    /// Lists the named files of a directory in ordinal order.
    /// </summary>
    public IReadOnlyList<string> ListNamedFiles(string path)
    {
        lock (_sync)
        {
            return [.. _directories[path].Named.Keys.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Replaces bytes of a named file, both cached and durable, as media damage would.
    /// </summary>
    public void DamageFile(string path, string name, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            _directories[path].Named[name].MakeDurable(offset, bytes.ToArray());
        }
    }

    /// <summary>
    /// Lists a directory's file numbers in ascending order.
    /// </summary>
    public IReadOnlyList<ulong> ListFiles(string path)
    {
        lock (_sync)
        {
            return [.. _directories[path].Files.Keys.Order()];
        }
    }

    /// <summary>
    /// Removes a file, as losing a file would.
    /// </summary>
    public void RemoveFile(string path, ulong file)
    {
        lock (_sync)
        {
            _ = _directories[path].Files.Remove(file);
        }
    }

    /// <summary>
    /// Truncates a file's durable and cached bytes, as losing its tail would.
    /// </summary>
    public void TruncateFile(string path, ulong file, long length)
    {
        lock (_sync)
        {
            _directories[path].Files[file].Truncate(length);
        }
    }

    /// <summary>
    /// Writes a complete file durably, as a format test constructs one.
    /// </summary>
    public void WriteFile(string path, ulong file, ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            if (!_directories.TryGetValue(path, out var directory))
            {
                directory = new SimulatedDirectory(path);
                _directories.Add(path, directory);
            }

            var simulated = new SimulatedFile(directory, file);
            simulated.MakeDurable(0, bytes.ToArray());
            directory.Files[file] = simulated;
        }
    }

    // Called under _sync.
    private SimulatedDirectoryHandle OpenDirectoryHandle(SimulatedDirectory directory)
    {
        if (directory.Owned)
        {
            throw new IOException($"The simulated directory '{directory.Path}' is owned by another log.");
        }

        directory.Owned = true;
        _openHandles++;
        return new SimulatedDirectoryHandle(this, directory);
    }

    // Called under _sync.
    private SimulatedFileHandle OpenFileHandle(SimulatedFile file)
    {
        _openHandles++;
        return new SimulatedFileHandle(this, file);
    }

    /// <summary>
    /// Closes a handle once; closing a directory handle also releases its directory's ownership.
    /// </summary>
    private void CloseHandle(ref bool closed, SimulatedDirectory? owned = null)
    {
        lock (_sync)
        {
            if (closed)
            {
                return;
            }

            closed = true;
            _openHandles--;
            if (owned is not null)
            {
                owned.Owned = false;
            }
        }
    }

    private void TearInto(SimulatedFile target, PendingWrite write)
    {
        for (var start = 0; start < write.Bytes.Length; start += _sectorSize)
        {
            var length = Math.Min(_sectorSize, write.Bytes.Length - start);
            switch (_random.Next(3))
            {
                case 0:
                    break;
                case 1:
                    target.MakeDurable(write.Offset + start, write.Bytes.AsSpan(start, length).ToArray());
                    break;
                default:
                    var torn = new byte[length];
                    _random.NextBytes(torn);
                    target.MakeDurable(write.Offset + start, torn);
                    break;
            }
        }
    }

    private void ThrowIfCrashed()
    {
        if (_crashed)
        {
            throw new CrashedProcessException("The simulated system crashed.");
        }

        if (_stopFault is { } fault)
        {
            // The fault, which the stop reported, rides along, so the failure reads as reported
            // (FatalErrorHandler.IsReported) and no component reports it again.
            throw new IOException("The simulated process stopped at a local storage failure.", fault);
        }
    }

    /// <summary>
    /// Reports an injected failure of <paramref name="operation"/> to the fatal-error handler, as the file-system seam
    /// reports the failure it simulates, and stops this process's storage where the process stops: every later call
    /// throws without reaching the device or reporting, until <see cref="Crash"/> returns the restarted process's storage.
    /// </summary>
    private Exception Stop(FatalErrorSite operation, string path, Exception fault)
    {
        _stopFault ??= fault;
        FatalErrorHandler.StopOnStorageFailure(operation, path, fault);
        return fault;
    }

    private ValueTask Write(SimulatedFile file, long offset, ReadOnlyMemory<ArcBuffer> buffers)
    {
        var bytes = new List<byte>();
        foreach (var buffer in buffers.Span)
        {
            if (_requiresAlignedBuffers)
            {
                foreach (var piece in buffer.PageSegments)
                {
                    if (!piece.Page.IsAligned || piece.Offset % _minimumPageSize != 0 || piece.Length % _minimumPageSize != 0)
                    {
                        throw new ArgumentException("Direct I/O requires whole aligned pages.", nameof(buffers));
                    }
                }
            }

            foreach (var span in buffer.SpanSegments)
            {
                bytes.AddRange(span);
            }
        }

        PendingWrite write;
        lock (_sync)
        {
            ThrowIfCrashed();
            IssuedWriteCount++;
            var fault = file.Name is { } name
                ? NamedWriteFault?.Invoke(file.Directory.Path, name, offset, bytes.Count)
                : WriteFault?.Invoke(file.Directory.Path, file.Number, offset, bytes.Count);
            if (fault is not null)
            {
                return ValueTask.FromException(Stop(FatalErrorSite.HandleWriteGather, file.Path, fault));
            }

            write = new PendingWrite(file, offset, [.. bytes]);
            if (file.Name is null && TornWriteFault?.Invoke(file.Directory.Path, file.Number, offset, bytes.Count) is { } failure)
            {
                TearInto(file, write);
                return ValueTask.FromException(Stop(FatalErrorSite.HandleWriteGather, file.Path, failure));
            }

            file.Cache(offset, write.Bytes);
            if (!HoldWrites)
            {
                file.MakeDurable(offset, write.Bytes);
                return ValueTask.CompletedTask;
            }

            _pending.Add(write);
        }

        return new ValueTask(write.Completion.Task);
    }

    private void Truncate(SimulatedFile file, long length)
    {
        lock (_sync)
        {
            ThrowIfCrashed();
            file.Truncate(length);
        }
    }

    private ArcBuffer Read(SimulatedFile file, long offset, int length)
    {
        byte[] bytes;
        lock (_sync)
        {
            ThrowIfCrashed();
            var fault = file.Name is null
                ? ReadFault?.Invoke(file.Directory.Path, file.Number, offset, length)
                : NamedReadFault?.Invoke(file.Directory.Path, file.Name, offset, length);
            if (fault is not null)
            {
                throw Stop(FatalErrorSite.HandleReadScatter, file.Path, fault);
            }

            var available = (int)Math.Clamp(file.Cached.Count - offset, 0, length);
            bytes = new byte[available];
            file.Cached.CopyTo((int)offset, bytes, 0, available);
        }

        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    private sealed record PendingWrite(SimulatedFile File, long Offset, byte[] Bytes)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SimulatedDirectory(string path)
    {
        public string Path { get; } = path;

        public Dictionary<ulong, SimulatedFile> Files { get; } = [];

        public Dictionary<string, SimulatedFile> Named { get; } = [with(StringComparer.Ordinal)];

        /// <summary>
        /// Gets the name changes no flush of the directory has made durable, in issue order.
        /// </summary>
        public List<NameChange> Unsynced { get; } = [];

        public bool Owned { get; set; }

        /// <summary>
        /// Makes every name change durable, as a directory flush does.
        /// </summary>
        public void Flush() => Unsynced.Clear();

        /// <summary>
        /// Flushes the directory after <paramref name="changed"/> gained or lost its name, or, when
        /// <paramref name="failure"/> fails the flush, leaves the change unsynced and returns the failure.
        /// </summary>
        public Exception? FlushChange(SimulatedFile? changed, bool removed, Exception? failure)
        {
            if (failure is null)
            {
                Flush();
            }
            else if (changed is not null)
            {
                Unsynced.Add(new NameChange(changed, removed));
            }

            return failure;
        }

        /// <summary>
        /// Returns the directory a restarted process observes. A process crash keeps every name change, still
        /// unsynced; an operating-system crash keeps or loses each unsynced change, by <paramref name="random"/>.
        /// </summary>
        public SimulatedDirectory Restart(bool operatingSystem, Random random)
        {
            var restarted = new SimulatedDirectory(Path);
            foreach (var (number, file) in Files)
            {
                restarted.Files.Add(number, file.Restart(restarted, operatingSystem));
            }

            foreach (var (name, file) in Named)
            {
                restarted.Named.Add(name, file.Restart(restarted, operatingSystem));
            }

            if (!operatingSystem)
            {
                restarted.Unsynced.AddRange(Unsynced);
                return restarted;
            }

            // Undo the lost changes, latest first.
            for (var index = Unsynced.Count - 1; index >= 0; index--)
            {
                if (random.Next(2) == 0)
                {
                    continue;
                }

                var (file, removed) = Unsynced[index];
                if (removed)
                {
                    var restored = file.Restart(restarted, operatingSystem: true);
                    if (file.Name is { } name)
                    {
                        restarted.Named[name] = restored;
                    }
                    else
                    {
                        restarted.Files[file.Number] = restored;
                    }
                }
                else if (file.Name is { } name)
                {
                    _ = restarted.Named.Remove(name);
                }
                else
                {
                    _ = restarted.Files.Remove(file.Number);
                }
            }

            return restarted;
        }
    }

    /// <summary>
    /// A name which <paramref name="File"/> gained, or, when <paramref name="Removed"/>, lost along with the file, and
    /// which no flush of its directory has made durable.
    /// </summary>
    private sealed record NameChange(SimulatedFile File, bool Removed);

    private sealed class SimulatedFile(SimulatedDirectory directory, ulong number, string? name = null)
    {
        public SimulatedDirectory Directory { get; } = directory;

        public ulong Number { get; } = number;

        public string? Name { get; } = name;

        /// <summary>
        /// Gets the file's path, by which a failure of its storage is reported.
        /// </summary>
        public string Path => System.IO.Path.Combine(Directory.Path, Name ?? WalFormat.GetFileName(Number));

        public List<byte> Cached { get; private set; } = [];

        public List<byte> Durable { get; private set; } = [];

        public void Cache(long offset, byte[] bytes) => Store(Cached, offset, bytes);

        public void MakeDurable(long offset, byte[] bytes)
        {
            Store(Cached, offset, bytes);
            Store(Durable, offset, bytes);
        }

        public void Flush() => Durable = [.. Cached];

        public void Truncate(long length)
        {
            Cached = [.. Cached.Take((int)length)];
            Durable = [.. Durable.Take((int)length)];
        }

        public SimulatedFile Restart(SimulatedDirectory owner, bool operatingSystem) => new(owner, Number, Name)
        {
            Cached = operatingSystem ? [.. Durable] : [.. Cached],
            Durable = [.. Durable],
        };

        private static void Store(List<byte> target, long offset, byte[] bytes)
        {
            var end = (int)offset + bytes.Length;
            if (target.Count < end)
            {
                target.AddRange(new byte[end - target.Count]);
            }

            for (var index = 0; index < bytes.Length; index++)
            {
                target[(int)offset + index] = bytes[index];
            }
        }
    }

    private sealed class SimulatedDirectoryHandle(SimulatedWalStorage storage, SimulatedDirectory directory) : WalDirectory
    {
        private bool _closed;

        public override int MinimumPageSize => storage._minimumPageSize;

        public override string Path => directory.Path;

        public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken)
        {
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                return ValueTask.FromResult<IReadOnlyList<ulong>>([.. directory.Files.Keys.Order()]);
            }
        }

        // The simulation holds every byte, so an allocated file is a zero-filled one created without a fill to pace.
        public override ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken) =>
            CreateFileAsync(file, header, length, pace: null, cancellationToken);

        public override async ValueTask<PreparedWalFile> PrepareFileAsync(
            long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preallocatedLength);
            cancellationToken.ThrowIfCancellationRequested();
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (storage.PrepareFault?.Invoke(directory.Path, preallocatedLength) is { } fault)
                {
                    throw storage.Stop(FatalErrorSite.CreateFile, System.IO.Path.Combine(directory.Path, "prealloc.tmp"), fault);
                }
            }

            if (pace is not null)
            {
                for (var position = 0L; position < preallocatedLength; position += FillChunkLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pace(position, (int)Math.Min(FillChunkLength, preallocatedLength - position), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (pace is not null && storage.PrepareFault?.Invoke(directory.Path, preallocatedLength) is { } fault)
                {
                    throw storage.Stop(FatalErrorSite.CreateFile, System.IO.Path.Combine(directory.Path, "prealloc.tmp"), fault);
                }

                storage._openHandles++;
                return new SimulatedPreparedWalFile(storage, directory, preallocatedLength);
            }
        }

        public override ValueTask<WalFile> PublishPreparedFileAsync(
            PreparedWalFile prepared, ulong file, ArcBuffer header, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(prepared);
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared is not SimulatedPreparedWalFile staged || !ReferenceEquals(staged.Directory, directory))
            {
                throw new ArgumentException("The prepared file does not belong to this simulated directory.", nameof(prepared));
            }

            var bytes = new byte[Math.Max(staged.Length, header.Length)];
            _ = header.CopyTo(bytes);
            lock (storage._sync)
            {
                staged.ConsumeLocked();
                storage.ThrowIfCrashed();
                if (directory.Files.ContainsKey(file))
                {
                    throw new IOException($"File {file} already exists.");
                }

                var simulated = new SimulatedFile(directory, file);
                simulated.MakeDurable(0, bytes);
                directory.Files.Add(file, simulated);
                if (directory.FlushChange(simulated, removed: false, storage.DirectoryFlushFault?.Invoke(directory.Path)) is { } unsynced)
                {
                    throw storage.Stop(FatalErrorSite.FlushDirectory, directory.Path, unsynced);
                }

#pragma warning disable CA2000 // Ownership of the file transfers to the caller.
                return ValueTask.FromResult<WalFile>(storage.OpenFileHandle(simulated));
#pragma warning restore CA2000
            }
        }

        public override async ValueTask<WalFile> CreateFileAsync(
            ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
        {
            var bytes = new byte[Math.Max(preallocatedLength, header.Length)];
            _ = header.CopyTo(bytes);
            if (pace is not null)
            {
                for (var position = (long)header.Length; position < preallocatedLength; position += FillChunkLength)
                {
                    await pace(position, (int)Math.Min(FillChunkLength, preallocatedLength - position), cancellationToken).ConfigureAwait(false);
                }
            }

            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (directory.Files.ContainsKey(file))
                {
                    throw new IOException($"File {file} already exists.");
                }

                var simulated = new SimulatedFile(directory, file);
                simulated.MakeDurable(0, bytes);
                directory.Files.Add(file, simulated);
                if (directory.FlushChange(simulated, removed: false, storage.DirectoryFlushFault?.Invoke(directory.Path)) is { } unsynced)
                {
                    throw storage.Stop(FatalErrorSite.FlushDirectory, directory.Path, unsynced);
                }

#pragma warning disable CA2000 // Ownership of the file transfers to the caller.
                return storage.OpenFileHandle(simulated);
#pragma warning restore CA2000
            }
        }

        public override ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken)
        {
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (!directory.Files.TryGetValue(file, out var simulated))
                {
                    throw new FileNotFoundException($"File {file} does not exist.");
                }

                simulated.Flush();
#pragma warning disable CA2000 // Ownership of the file transfers to the caller.
                return ValueTask.FromResult<WalFile>(storage.OpenFileHandle(simulated));
#pragma warning restore CA2000
            }
        }

        public override ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken)
        {
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (storage.DeleteFault?.Invoke(directory.Path, file) is { } fault)
                {
                    return ValueTask.FromException(storage.Stop(FatalErrorSite.DeleteFile, System.IO.Path.Combine(directory.Path, WalFormat.GetFileName(file)), fault));
                }

                _ = directory.Files.Remove(file, out var deleted);
                if (directory.FlushChange(deleted, removed: true, storage.DirectoryFlushFault?.Invoke(directory.Path)) is { } unsynced)
                {
                    return ValueTask.FromException(storage.Stop(FatalErrorSite.FlushDirectory, directory.Path, unsynced));
                }
            }

            return ValueTask.CompletedTask;
        }

        public override ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken)
        {
            WalFormat.ValidateNamedFileName(name);
            var bytes = contents.ToArray();
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (directory.Named.ContainsKey(name))
                {
                    throw new IOException($"The file '{name}' already exists.");
                }

                if (storage.NamedWriteFault?.Invoke(directory.Path, name, 0, bytes.Length) is { } fault)
                {
                    return ValueTask.FromException<WalFile>(storage.Stop(FatalErrorSite.CreateFile, System.IO.Path.Combine(directory.Path, name), fault));
                }

                var file = new SimulatedFile(directory, 0, name);
                file.MakeDurable(0, bytes);
                directory.Named.Add(name, file);
                if (directory.FlushChange(file, removed: false, storage.DirectoryFlushFault?.Invoke(directory.Path)) is { } unsynced)
                {
                    return ValueTask.FromException<WalFile>(storage.Stop(FatalErrorSite.FlushDirectory, directory.Path, unsynced));
                }

                if (storage.NamedPublishedFault?.Invoke(directory.Path, name) is { } published)
                {
                    return ValueTask.FromException<WalFile>(storage.Stop(FatalErrorSite.FlushDirectory, directory.Path, published));
                }

#pragma warning disable CA2000 // Ownership of the file transfers to the caller.
                return ValueTask.FromResult<WalFile>(storage.OpenFileHandle(file));
#pragma warning restore CA2000
            }
        }

        public override ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken)
        {
            WalFormat.ValidateNamedFileName(name);
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (!directory.Named.TryGetValue(name, out var file))
                {
                    throw new FileNotFoundException($"The file '{name}' does not exist.", name);
                }

                file.Flush();
#pragma warning disable CA2000 // Ownership of the file transfers to the caller.
                return ValueTask.FromResult<WalFile>(storage.OpenFileHandle(file));
#pragma warning restore CA2000
            }
        }

        public override ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken)
        {
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                return ValueTask.FromResult<IReadOnlyList<string>>(
                    [.. directory.Named.Keys.Where(name => name.EndsWith(extension, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
            }
        }

        public override ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken)
        {
            WalFormat.ValidateNamedFileName(name);
            lock (storage._sync)
            {
                storage.ThrowIfCrashed();
                if (storage.NamedDeleteFault?.Invoke(directory.Path, name) is { } fault)
                {
                    return ValueTask.FromException(storage.Stop(FatalErrorSite.DeleteFile, System.IO.Path.Combine(directory.Path, name), fault));
                }

                _ = directory.Named.Remove(name, out var deleted);
                if (directory.FlushChange(deleted, removed: true, storage.DirectoryFlushFault?.Invoke(directory.Path)) is { } unsynced)
                {
                    return ValueTask.FromException(storage.Stop(FatalErrorSite.FlushDirectory, directory.Path, unsynced));
                }
            }

            return ValueTask.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            storage.CloseHandle(ref _closed, directory);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SimulatedPreparedWalFile(SimulatedWalStorage storage, SimulatedDirectory directory, long length) : PreparedWalFile
    {
        private bool _closed;

        public SimulatedDirectory Directory => directory;

        public override long Length => length;

        // Called under storage._sync.
        public void ConsumeLocked()
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _closed = true;
            storage._openHandles--;
        }

        public override ValueTask DisposeAsync()
        {
            storage.CloseHandle(ref _closed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SimulatedFileHandle(SimulatedWalStorage storage, SimulatedFile file) : WalFile
    {
        private bool _closed;

        public override long Length
        {
            get
            {
                lock (storage._sync)
                {
                    return file.Cached.Count;
                }
            }
        }

        public override ValueTask WriteAsync(long offset, ReadOnlyMemory<ArcBuffer> buffers) => storage.Write(file, offset, buffers);

        public override ValueTask<ArcBuffer> ReadAsync(long offset, int length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(storage.Read(file, offset, length));
        }

        public override ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            storage.Truncate(file, length);
            return ValueTask.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            storage.CloseHandle(ref _closed);
            return ValueTask.CompletedTask;
        }
    }
}
