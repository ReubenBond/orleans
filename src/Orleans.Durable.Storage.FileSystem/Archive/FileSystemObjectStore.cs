using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.FileSystem.Archive;

/// <summary>
/// Keeps every object of the archive as one file in one directory, for clusters whose processes share one host's file
/// system, such as a soak or a lab on one machine (archive §9).
/// </summary>
/// <remarks>
/// <para>
/// An object's file is <c>+</c> followed by its name with each <c>/</c> written as <c>+</c>, so no operation creates a
/// directory, and no file name is a Windows device name or holds the <c>~</c> of an NTFS short name, which could
/// otherwise name another object's file. An object name holds no <c>.</c>, and every other file does: a write's
/// temporary file is <c>{file}.{guid:N}.tmp</c>, and <c>store.lock</c> is the lock replaces, deletes, and appendable
/// calls hold. A listing skips every other file. The directory is an operator's: it and every directory above it exist
/// durably before the store is used, and the store flushes only the directory. On Windows the directory lies on NTFS
/// with POSIX delete semantics, so an unlink removes the name at once although a versioned read holds the file open,
/// and the flush after it makes the absence durable. A ranged read holds its file through a handle of the seam's <see
/// cref="WalFileSystem.OpenFile"/>, which shares reading and writing but not deletion, so on Windows a delete or a
/// rename over the file waits out the read, as the seam waits out contention. A published file never changes, so a
/// read's handle sees one content throughout.
/// </para>
/// <para>
/// Every answer rests on durable entries (conventions.md §13). A create writes and flushes a temporary file, publishes it
/// under the name without replacing another (a hard link on Linux and macOS, a no-replace move on Windows), and flushes
/// the directory before it answers <see cref="ObjectCreateOutcome.Created"/>, or, when the name exists,
/// <see cref="ObjectCreateOutcome.Exists"/>, so a name another process published but has not flushed is reported only
/// once durable. A replace holds the lock, reads the object and flushes the directory, so the version it compares is
/// durable, then answers <see cref="ObjectReplaceOutcome.Mismatch"/>, or writes and flushes a temporary file, renames it
/// over the object, or publishes it without replacing when it expected no object, and flushes the directory before it
/// answers <see cref="ObjectReplaceOutcome.Applied"/>. A delete unlinks the file under the lock and flushes the directory
/// before it completes. A read, a versioned read, and a listing flush the directory after they observe and before they
/// answer, so the flush covers every publication or unlink they saw, whichever process made it; a flush before the read
/// would miss one landing between them. A ranged or properties read which finds a plain object whose publication this
/// process has already seen durable answers without a flush: by the callers' demand, such an object keeps its content
/// until it is deleted, a replaced one is read only by versioned reads, which flush every time, and a name created again
/// holds the content it held (<see cref="IObjectStore"/>). This process does not see another's delete, so after another
/// process deletes and re-creates such a name, the read may answer before the re-creation is durable. Concurrent operations share flushes: one runs at a time, and the
/// operations which observed while it ran share the next, which starts after their observations. A replace's version
/// check hashes the object's file in pooled chunks.
/// </para>
/// <para>
/// An appendable object's file is <c>!</c> while it is open and <c>=</c> once sealed, in place of the <c>+</c>, and the
/// object is the first of its <c>+</c>, <c>!</c>, and <c>=</c> files which exists. Every appendable call holds the lock. A
/// create publishes an empty <c>!</c> file when the name has no file. An append writes the open file's bytes and its own
/// to a temporary file, flushes it, and renames it over the open file, so a published file never changes and an append
/// lands whole or not at all. A seal renames the open file to its sealed name. A tombstone renames its file over the
/// <c>+</c> name, flushes the directory, then removes the <c>!</c> and <c>=</c> files, so a crash between them leaves the
/// tombstone, which hides them; a delete which meets them removes them and flushes before it removes the <c>+</c> file.
/// A read looks in that order, and once more for the <c>+</c> file when it finds none, which a concurrent tombstone
/// published before it removed the file the read missed. A read which finds an appendable file always flushes.
/// </para>
/// <para>
/// The lock is the operating system's exclusive file lock, so replaces, deletes, and appendable calls exclude one
/// another across every process on the host: without it, a delete between a replace's check and its rename would leave the replaced object
/// in place, and two replaces of one version could both apply. A create takes no lock: its no-replace publication is
/// atomic against every other change of the name. An object's version is the SHA-256 of its content, so its file holds
/// exactly its bytes, and an object which holds earlier content again holds its earlier version again (archive §3).
/// Every file-system call goes through the WAL's <see cref="WalFileSystem"/>, which reports a failed operation to the
/// fatal-error handler, stopping the process (conventions.md §14).
/// </para>
/// <para>
/// A crash or a stop between a write's temporary file and its publication or removal leaves the temporary file, which
/// after a hard-link publication keeps a deleted object's bytes on disk. Listings skip temporary files, so the store
/// reclaims its own (<see cref="ReclaimTemporaryFiles"/>): as it opens, and whenever its host calls it, it deletes each
/// temporary file whose last write is older than <see cref="FileSystemObjectStoreOptions.TemporaryFileAge"/>. Each
/// deletion holds the file against every other handle from the check of its last write to its deletion
/// (<see cref="WalFileSystem.TryDeleteFileWrittenBefore"/>), so it never removes a name a concurrent publication gave the
/// file. A write whose temporary file a reclamation deletes throws <see cref="FileNotFoundException"/> from its
/// publication, its outcome unknown, having published nothing, so every answer stands and the age is a liveness bound
/// only. A reclamation flushes nothing: a deletion a crash loses, a later pass makes again.
/// </para>
/// </remarks>
public sealed partial class FileSystemObjectStore : IObjectStore, IAppendableObjectStore
{
    /// <summary>
    /// The name of the lock file replaces and deletes hold.
    /// </summary>
    internal const string LockFileName = "store.lock";

    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The length of each read a replace's version check hashes.
    /// </summary>
    private const int HashChunkLength = 64 * 1024;

    /// <summary>
    /// The character which starts every object's file name and separates its name's components.
    /// </summary>
    private const char ObjectFilePrefix = '+';

    /// <summary>
    /// The character which starts an open appendable object's file name in place of <see cref="ObjectFilePrefix"/>.
    /// </summary>
    private const char OpenAppendablePrefix = '!';

    /// <summary>
    /// The character which starts a sealed appendable object's file name in place of <see cref="ObjectFilePrefix"/>.
    /// </summary>
    private const char SealedAppendablePrefix = '=';

    /// <summary>
    /// The order in which a read looks for an object's file: the object is the first of its plain, open, and sealed files
    /// which exists, and a read which finds none looks for the plain file again, which a tombstone publishes before it
    /// removes the others.
    /// </summary>
    private static readonly char[] LookupOrder = [ObjectFilePrefix, OpenAppendablePrefix, SealedAppendablePrefix, ObjectFilePrefix];

    /// <summary>
    /// The longest file name the store writes, in bytes: the limit of NTFS, ext4, XFS, and APFS.
    /// </summary>
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// The extension which ends a temporary file's name, <c>{file}.{guid:N}.tmp</c>.
    /// </summary>
    private const string TemporaryExtension = ".tmp";

    /// <summary>
    /// The length of a temporary file's suffix, <c>.{guid:N}.tmp</c>.
    /// </summary>
    private const int TemporarySuffixLength = 1 + 32 + 4;

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly WalFileSystem _fileSystem;
    private readonly TimeSpan _temporaryFileAge;
    private readonly ILogger _logger;
    private readonly Lock _flushSync = new();

    /// <summary>
    /// The names whose plain objects this process has seen durable: a ranged or properties read which finds one answers
    /// without a flush, since by the callers' demand such an object keeps its content until it is deleted.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _durable = new(StringComparer.Ordinal);

    /// <summary>
    /// The names this process replaced, which by the callers' demand it reads only with versioned reads; tracked in Debug
    /// builds, whose assertions check the demand the durable cache rests on.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _replaced = new(StringComparer.Ordinal);
    private bool _flushing;
    private TaskCompletionSource? _nextFlush;

    /// <summary>
    /// Gets a value indicating whether <see cref="ReplaceAsync"/> skips <c>store.lock</c>, so two concurrent replaces of one
    /// version can both check the version before either renames and both apply: a race mutant, for tests.
    /// </summary>
    internal bool SkipsReplaceLockMutant { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemObjectStore"/> class, which opens by reclaiming the temporary
    /// files the directory holds past the default age.
    /// </summary>
    /// <param name="directory">The directory holding the objects. It and every directory above it exist durably before
    /// the store is used.</param>
    /// <param name="timeProvider">Paces a replace or delete waiting for the lock, and gives the wall-clock time against
    /// which a temporary file's age is measured.</param>
    public FileSystemObjectStore(string directory, TimeProvider timeProvider)
        : this(directory, timeProvider, new FileSystemObjectStoreOptions(), NullLogger<FileSystemObjectStore>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemObjectStore"/> class, which opens by reclaiming the temporary
    /// files the directory holds past <paramref name="options"/>' age.
    /// </summary>
    /// <param name="directory">The directory holding the objects. It and every directory above it exist durably before
    /// the store is used.</param>
    /// <param name="timeProvider">Paces a replace or delete waiting for the lock, and gives the wall-clock time against
    /// which a temporary file's age is measured.</param>
    /// <param name="options">The age past which a temporary file is reclaimed.</param>
    /// <param name="logger">Logs each temporary file whose deletion another handle refused.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public FileSystemObjectStore(string directory, TimeProvider timeProvider, FileSystemObjectStoreOptions options, ILogger<FileSystemObjectStore> logger)
        : this(directory, timeProvider, WalFileSystem.Os, options, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemObjectStore"/> class over <paramref name="fileSystem"/>, with
    /// the default options.
    /// </summary>
    internal FileSystemObjectStore(string directory, TimeProvider timeProvider, WalFileSystem fileSystem)
        : this(directory, timeProvider, fileSystem, new FileSystemObjectStoreOptions(), NullLogger<FileSystemObjectStore>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemObjectStore"/> class over <paramref name="fileSystem"/>.
    /// </summary>
    internal FileSystemObjectStore(string directory, TimeProvider timeProvider, WalFileSystem fileSystem, FileSystemObjectStoreOptions options, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _directory = WalFileSystem.Normalize(directory);
        _timeProvider = timeProvider;
        _fileSystem = fileSystem;
        _temporaryFileAge = options.TemporaryFileAge;
        _logger = logger;
        _ = ReclaimTemporaryFiles();
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathOf(name);
        var temporary = await WriteTemporaryAsync(name, path, content, crc32c).ConfigureAwait(false);
        ObjectCreateOutcome outcome = TryPublish(temporary, path) ? ObjectCreateOutcome.Created.Instance : ObjectCreateOutcome.Exists.Instance;

        // conventions.md §13: the publication, this create's or another process's, is durable before the answer leaves.
        await FlushDirectoryAsync().ConfigureAwait(false);
        MarkDurable(name);
        return outcome;
    }

    /// <inheritdoc/>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotRange(offset, length);
        AssertNotReplaced(name);
        if (OpenObject(name) is not var (file, kind))
        {
            // conventions.md §13: the object's absence is durable before the answer leaves.
            _ = _durable.TryRemove(name.Value, out _);
            await FlushDirectoryAsync().ConfigureAwait(false);
            return null;
        }

        using var writer = new ArcBufferWriter(length);
        long objectLength;
        using (file)
        {
            objectLength = file.Length;
            if (offset <= objectLength - length)
            {
                await ReadRangeAsync(file, offset, length, writer, cancellationToken).ConfigureAwait(false);
            }
        }

        // conventions.md §13: the object's publication is durable before the answer leaves.
        await FlushAfterFindingAsync(name, kind).ConfigureAwait(false);
        ObjectStoreArguments.ThrowIfPastEnd(name, offset, length, objectLength);
        return writer.ConsumeSlice(length);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The file system keeps no checksum, so the store computes a plain object's CRC-32C from its file, in pooled chunks,
    /// as it answers, where the cloud stores return the CRC-32C the write recorded: an object created once keeps its
    /// content (the callers' demand), so the two agree. An appendable object has none.
    /// </remarks>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        AssertNotReplaced(name);
        if (OpenObject(name) is not var (file, kind))
        {
            // conventions.md §13: the object's absence is durable before the answer leaves.
            _ = _durable.TryRemove(name.Value, out _);
            await FlushDirectoryAsync().ConfigureAwait(false);
            return null;
        }

        long length;
        uint? crc32c;
        using (file)
        {
            length = file.Length;
            crc32c = length == 0 || kind != ObjectFilePrefix ? null : await Crc32COfAsync(file, length, cancellationToken).ConfigureAwait(false);
        }

        // conventions.md §13: the object's publication is durable before the answer leaves.
        await FlushAfterFindingAsync(name, kind).ConfigureAwait(false);
        return new ObjectProperties(length, crc32c);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return ListDirectoryAsync(prefix, startAfter, cancellationToken);
    }

    /// <summary>
    /// Lists the directory once the listing is first read, with each object's length, and streams the objects once the
    /// flush after the listing makes them durable; the directory's one enumeration is its only page.
    /// </summary>
    /// <remarks>
    /// The seam's listing takes each length without opening a file on Windows, where a file whose deletion is pending
    /// keeps its name and answers an open with access denied. A file a concurrent delete or replace removes during the
    /// listing is listed or left out: its name is one a concurrent call changes, a hint (archive §3).
    /// </remarks>
    private async IAsyncEnumerable<ListedObject> ListDirectoryAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A file the store did not write, whose name maps to no object name, is not an object. The prefix's file-name form
        // is checked on the name before any name is built.
        // An object's file is the first of its plain, open, and sealed files, so a name with several lists the first.
        var filePrefix = prefix.Replace('/', ObjectFilePrefix);
        var files = _fileSystem.EnumerateFilesWithLengths(
            _directory,
            fileName => fileName.Length > 0 && fileName[1..].StartsWith(filePrefix, StringComparison.Ordinal) && ObjectNameOf(fileName.ToString()) is not null);
        var found = new Dictionary<string, (int Rank, long Length)>(files.Count, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.Path);
            var name = ObjectNameOf(fileName)!.Value;
            var rank = Array.IndexOf(LookupOrder, fileName[0]);
            if (ObjectStoreArguments.IsAfter(name, startAfter) && (!found.TryGetValue(name, out var first) || rank < first.Rank))
            {
                found[name] = (rank, file.Length);
            }
        }

        var listed = found.Select(static entry => new ListedObject(new ObjectName(entry.Key), entry.Value.Length)).ToList();

        // conventions.md §13: every publication and unlink the listing saw is durable before the objects leave.
        await FlushDirectoryAsync().ConfigureAwait(false);
        listed.Sort(static (left, right) => left.Name.CompareTo(right.Name));
        foreach (var entry in listed)
        {
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        using (await AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = PathOf(name);
            var open = PathOf(name, OpenAppendablePrefix);
            var sealedPath = PathOf(name, SealedAppendablePrefix);
            if (_fileSystem.FileExists(open) || _fileSystem.FileExists(sealedPath))
            {
                _fileSystem.DeleteFile(open);
                _fileSystem.DeleteFile(sealedPath);

                // A plain file hides an appendable one, which a tombstone left; its removal is durable before the plain
                // file's, so a crash between them never uncovers it.
                await FlushDirectoryAsync().ConfigureAwait(false);
            }

            _fileSystem.DeleteFile(path);
        }

        _ = _durable.TryRemove(name.Value, out _);

        // conventions.md §13: the object's absence, by this unlink or another process's, is durable before the delete
        // completes. The lock orders only the unlink against a replace's check and rename.
        await FlushDirectoryAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        byte[]? content = null;
        foreach (var kind in LookupOrder)
        {
            if ((content = await _fileSystem.ReadFileAsync(PathOf(name, kind), cancellationToken).ConfigureAwait(false)) is not null)
            {
                break;
            }
        }

        if (content is null)
        {
            // conventions.md §13: the object's absence is durable before the answer leaves.
            _ = _durable.TryRemove(name.Value, out _);
            await FlushDirectoryAsync().ConfigureAwait(false);
            return null;
        }

        // conventions.md §13: the object's publication is durable before the answer leaves. A replace changes the object,
        // so a versioned read flushes every time.
        await FlushDirectoryAsync().ConfigureAwait(false);
        return new VersionedObject(ArcBuffer.Wrap(content), VersionOf(content));
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfEmpty(content);
        RecordReplaced(name);
        var path = PathOf(name);
        using var held = SkipsReplaceLockMutant ? null : await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var current = await VersionOfFileAsync(path).ConfigureAwait(false);

        // conventions.md §13: the version the replace compares is durable before either answer leaves.
        await FlushDirectoryAsync().ConfigureAwait(false);
        if (current != expectedVersion)
        {
            return ObjectReplaceOutcome.Mismatch.Instance;
        }

        var temporary = await WriteTemporaryAsync(name, path, content, crc32c).ConfigureAwait(false);
        if (current is null)
        {
            if (!TryPublish(temporary, path))
            {
                // A create, which takes no lock, published the name after the check; its publication is durable before
                // the mismatch leaves.
                await FlushDirectoryAsync().ConfigureAwait(false);
                return ObjectReplaceOutcome.Mismatch.Instance;
            }
        }
        else
        {
            _fileSystem.MoveFile(temporary, path, replace: true);
        }

        // conventions.md §13: the replace is durable before Applied leaves.
        await FlushDirectoryAsync().ConfigureAwait(false);
        return new ObjectReplaceOutcome.Applied(VersionOf(content));
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var open = PathOf(name, OpenAppendablePrefix);
        using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var created = !Exists(name) && TryPublish(await WriteTemporaryAsync(name, open, default, null).ConfigureAwait(false), open);

        // conventions.md §13: the new object, or the one the create met, is durable before the answer leaves.
        await FlushDirectoryAsync().ConfigureAwait(false);
        return created ? ObjectCreateOutcome.Created.Instance : ObjectCreateOutcome.Exists.Instance;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotAppend(expectedLength, content);
        using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var outcome = await AppendHeldAsync(name, expectedLength, content, crc32c).ConfigureAwait(false);

        // conventions.md §13: the append, or the state it met, is durable before the answer leaves.
        await FlushDirectoryAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var open = PathOf(name, OpenAppendablePrefix);
        using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        ObjectSealOutcome outcome;
        if (_fileSystem.FileExists(PathOf(name)))
        {
            outcome = ObjectSealOutcome.NotAppendable.Instance;
        }
        else if (_fileSystem.FileExists(open))
        {
            _fileSystem.MoveFile(open, PathOf(name, SealedAppendablePrefix), replace: false);
            outcome = ObjectSealOutcome.Sealed.Instance;
        }
        else
        {
            outcome = _fileSystem.FileExists(PathOf(name, SealedAppendablePrefix)) ? ObjectSealOutcome.Sealed.Instance : ObjectSealOutcome.Absent.Instance;
        }

        // conventions.md §13: the seal, or the state it met, is durable before the answer leaves.
        await FlushDirectoryAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <inheritdoc/>
    public async ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var path = PathOf(name);
        using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var temporary = await WriteTemporaryAsync(name, path, content, crc32c).ConfigureAwait(false);
        _fileSystem.MoveFile(temporary, path, replace: true);

        // The plain file, which hides the appendable one, is durable before that one's removal, so a crash between them
        // never leaves neither.
        await FlushDirectoryAsync().ConfigureAwait(false);
        _fileSystem.DeleteFile(PathOf(name, OpenAppendablePrefix));
        _fileSystem.DeleteFile(PathOf(name, SealedAppendablePrefix));
        _ = _durable.TryRemove(name.Value, out _);
        await FlushDirectoryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Appends under the store's lock, which every appendable transition holds: the object's plain file answers sealed,
    /// its open file takes the append at its length by a copy renamed over it, and its sealed file answers sealed.
    /// </summary>
    private async ValueTask<ObjectAppendOutcome> AppendHeldAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c)
    {
        if (_fileSystem.FileExists(PathOf(name)))
        {
            return ObjectAppendOutcome.Sealed.Instance;
        }

        var open = PathOf(name, OpenAppendablePrefix);
        if (await _fileSystem.ReadFileAsync(open, CancellationToken.None).ConfigureAwait(false) is not { } existing)
        {
            if (await _fileSystem.ReadFileAsync(PathOf(name, SealedAppendablePrefix), CancellationToken.None).ConfigureAwait(false) is not { } sealedBytes)
            {
                return ObjectAppendOutcome.Absent.Instance;
            }

            // A sealed object which holds the append's bytes at its expected length took this call's earlier attempt.
            var bytes = content.ToArray();
            return sealedBytes.Length - bytes.Length >= expectedLength && sealedBytes.AsSpan((int)expectedLength, bytes.Length).SequenceEqual(bytes)
                ? new ObjectAppendOutcome.Appended(expectedLength + bytes.Length)
                : ObjectAppendOutcome.Sealed.Instance;
        }

        if (existing.Length != expectedLength)
        {
            return new ObjectAppendOutcome.LengthMismatch(existing.Length);
        }

        // A published file never changes: the append writes the object's bytes and its own to a new file, flushed, and
        // renames it over the open file, so it lands whole or not at all.
        var temporary = await WriteTemporaryAsync(name, open, content, crc32c, existing).ConfigureAwait(false);
        _fileSystem.MoveFile(temporary, open, replace: true);
        return new ObjectAppendOutcome.Appended(existing.Length + content.Length);
    }

    /// <summary>
    /// Returns whether any file holds <paramref name="name"/>'s object; under the store's lock, which every appendable
    /// transition holds.
    /// </summary>
    private bool Exists(ObjectName name) =>
        _fileSystem.FileExists(PathOf(name))
        || _fileSystem.FileExists(PathOf(name, OpenAppendablePrefix))
        || _fileSystem.FileExists(PathOf(name, SealedAppendablePrefix));

    /// <summary>
    /// Opens the file holding <paramref name="name"/>'s object, in <see cref="LookupOrder"/>, with the character its file
    /// name starts with; or returns <see langword="null"/> when no file holds it.
    /// </summary>
    private (IWalFileSystemFile File, char Kind)? OpenObject(ObjectName name)
    {
        foreach (var kind in LookupOrder)
        {
            if (OpenToRead(PathOf(name, kind)) is { } file)
            {
                return (file, kind);
            }
        }

        return null;
    }

    /// <summary>
    /// Flushes the directory after a read which found <paramref name="name"/>'s <paramref name="kind"/> of file: unless
    /// this process has seen a plain one durable, and always for an appendable one, which appends replace.
    /// </summary>
    private Task FlushAfterFindingAsync(ObjectName name, char kind) =>
        kind == ObjectFilePrefix ? FlushUnlessDurableAsync(name) : FlushDirectoryAsync();

    /// <summary>
    /// Returns the version of an object holding <paramref name="content"/>: the lowercase hexadecimal SHA-256 of it.
    /// </summary>
    internal static ObjectVersion VersionOf(ReadOnlySpan<byte> content) => new(Convert.ToHexStringLower(SHA256.HashData(content)));

    private static ObjectVersion VersionOf(ArcBuffer content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var segment in content)
        {
            hash.AppendData(segment);
        }

        return new ObjectVersion(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Returns the version of the object <paramref name="path"/> holds, hashing its file in pooled chunks, or
    /// <see langword="null"/> when no file holds it.
    /// </summary>
    private async ValueTask<ObjectVersion?> VersionOfFileAsync(string path)
    {
        using var file = OpenToRead(path);
        if (file is null)
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = ArrayPool<byte>.Shared.Rent(HashChunkLength);
        try
        {
            for (long offset = 0; ;)
            {
                var read = await file.ReadAsync(chunk.AsMemory(0, HashChunkLength), offset, CancellationToken.None).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(chunk, 0, read);
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return new ObjectVersion(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Flushes the directory after a ranged or properties read which found <paramref name="name"/>, unless this process has
    /// already seen it durable: by the callers' demand, an object such reads serve keeps its content until it is deleted.
    /// </summary>
    private async Task FlushUnlessDurableAsync(ObjectName name)
    {
        if (_durable.ContainsKey(name.Value))
        {
            return;
        }

        await FlushDirectoryAsync().ConfigureAwait(false);
        MarkDurable(name);
    }

    [Conditional("DEBUG")]
    private void RecordReplaced(ObjectName name) => _ = _replaced.TryAdd(name.Value, 0);

    /// <summary>
    /// Asserts the callers' demand that a replaced object is read only with versioned reads, on which a ranged or
    /// properties read's durable cache rests.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertNotReplaced(ObjectName name) =>
        Debug.Assert(!_replaced.ContainsKey(name.Value), $"'{name}' was replaced, so the callers' demand reads it only with versioned reads.");

    /// <summary>
    /// Records that a flush after this process found <paramref name="name"/>, or created it, made it durable.
    /// </summary>
    private void MarkDurable(ObjectName name) => _ = _durable.TryAdd(name.Value, 0);

    /// <summary>Returns the CRC-32C of a file's first <paramref name="length"/> bytes, read in chunks of 1 MiB.</summary>
    private static async ValueTask<uint> Crc32COfAsync(IWalFileSystemFile file, long length, CancellationToken cancellationToken)
    {
        const int Chunk = 1 << 20;
        var crc = new System.IO.Hashing.Crc32(System.IO.Hashing.Crc32ParameterSet.Crc32C);
        for (var offset = 0L; offset < length; offset += Chunk)
        {
            using var writer = new ArcBufferWriter();
            var count = (int)Math.Min(Chunk, length - offset);
            await ReadRangeAsync(file, offset, count, writer, cancellationToken).ConfigureAwait(false);
            using var chunk = writer.ConsumeSlice(count);
            foreach (var segment in chunk.MemorySegments)
            {
                crc.Append(segment.Span);
            }
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Returns the object name a file of the store's directory holds, or <see langword="null"/> when it holds none.
    /// </summary>
    internal static ObjectName? ObjectNameOf(string fileName) =>
        fileName.Length > 0
        && fileName[0] is ObjectFilePrefix or OpenAppendablePrefix or SealedAppendablePrefix
        && fileName[1..].Replace(ObjectFilePrefix, '/') is var value
        && ObjectName.IsValid(value)
            ? new ObjectName(value)
            : null;

    /// <summary>
    /// Deletes each temporary file whose last write is older than the configured age (archive §9). The store runs it as
    /// it opens; its host calls it on the host's cadence, as it runs the archive's sweeper.
    /// </summary>
    /// <remarks>
    /// Each deletion is one attempt which holds the file from the check of its last write to its deletion
    /// (<see cref="WalFileSystem.TryDeleteFileWrittenBefore"/>), so it never removes the name a concurrent publication gave
    /// the file. One which another handle refuses, as a writer's handle does on Windows, leaves the file for a later pass,
    /// and is logged and counted. The age is measured from the file system's last write to the store's
    /// <see cref="TimeProvider"/>'s wall-clock time; it bounds only how long a leftover file stays, since a write whose
    /// temporary file goes early answers no outcome. A failure is reported by the seam, which stops the process
    /// (conventions.md §14); only a test which intercepts the report sees it thrown here.
    /// </remarks>
    /// <returns>The temporary files deleted, or gone or written again since the listing, and those whose deletion
    /// another handle refused.</returns>
    public TemporaryFileReclamation ReclaimTemporaryFiles()
    {
        // The cutoff lies in the file system's time domain, which the store's clock gives.
        var cutoff = _timeProvider.GetUtcNow() - _temporaryFileAge;
        int reclaimed = 0, refused = 0;
        foreach (var file in _fileSystem.EnumerateFilesWithLastWriteTimes(_directory, IsTemporaryFileName))
        {
            if (file.LastWriteTime >= cutoff)
            {
                continue;
            }

            if (_fileSystem.TryDeleteFileWrittenBefore(file.Path, cutoff))
            {
                reclaimed++;
            }
            else
            {
                LogRefused(_logger, file.Path);
                refused++;
            }
        }

        FileSystemObjectStoreMetrics.RecordReclamation(reclaimed, refused);
        return new TemporaryFileReclamation(reclaimed, refused);
    }

    /// <summary>
    /// Returns whether a file of the store's directory is a write's temporary file, <c>{file}.{guid:N}.tmp</c> beside an
    /// object's file, judging the suffix before it allocates.
    /// </summary>
    private static bool IsTemporaryFileName(ReadOnlySpan<char> fileName) =>
        fileName.Length > TemporarySuffixLength
        && fileName.EndsWith(TemporaryExtension, StringComparison.Ordinal)
        && fileName[^TemporarySuffixLength] == '.'
        && Guid.TryParseExact(fileName.Slice(fileName.Length - TemporarySuffixLength + 1, 32), "N", out _)
        && ObjectNameOf(fileName[..^TemporarySuffixLength].ToString()) is not null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another handle refused the reclamation of the temporary file {Path}; a later pass deletes it.")]
    private static partial void LogRefused(ILogger logger, string path);

    /// <summary>
    /// Publishes <paramref name="temporary"/> under <paramref name="path"/> without replacing an existing file (a hard
    /// link on Linux and macOS, a no-replace move on Windows), then removes the temporary name, which a link keeps and a
    /// publication which met the name leaves.
    /// </summary>
    /// <returns>Whether the publication made the file; <see langword="false"/> when the name exists.</returns>
    private bool TryPublish(string temporary, string path)
    {
        bool published;
        try
        {
            _fileSystem.LinkFile(temporary, path);
            published = true;
        }
        catch (WalNameExistsException)
        {
            published = false;
        }

        _fileSystem.DeleteFile(temporary);
        return published;
    }

    /// <summary>
    /// Returns the path of <paramref name="name"/>'s file.
    /// </summary>
    /// <exception cref="ArgumentException">The name's file, with a temporary suffix, would pass the longest file name a
    /// file system holds; no name of at most <see cref="ObjectName.MaxLength"/> characters does.</exception>
    private string PathOf(ObjectName name, char kind = ObjectFilePrefix)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (1 + name.Value.Length + TemporarySuffixLength > MaxFileNameLength)
        {
            throw new ArgumentException($"The object name '{name}' is too long for the file-system store's {MaxFileNameLength}-byte file names.", nameof(name));
        }

        return Path.Combine(_directory, kind + name.Value.Replace('/', ObjectFilePrefix));
    }

    /// <summary>
    /// Opens an object's file to read it, or returns <see langword="null"/> when no file holds the object.
    /// </summary>
    private IWalFileSystemFile? OpenToRead(string path)
    {
        try
        {
            return _fileSystem.OpenFile(path, direct: false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of <paramref name="file"/>, which holds them, into
    /// <paramref name="writer"/>'s pages, each read filling what remains of the page it lands in.
    /// </summary>
    private static async ValueTask ReadRangeAsync(IWalFileSystemFile file, long offset, int length, ArcBufferWriter writer, CancellationToken cancellationToken)
    {
        for (var read = 0; read < length;)
        {
            var page = writer.GetMemory();
            var count = await file.ReadAsync(page[..Math.Min(page.Length, length - read)], offset + read, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                // An object's file never changes once published, so its length holds while a reader has it open.
                throw new EndOfStreamException("An object's file ended before the length it gave.");
            }

            writer.AdvanceWriter(count);
            read += count;
        }
    }

    /// <summary>
    /// Flushes the directory after the caller's observation: the flush in flight may have started before it, so the
    /// caller runs a flush when none is in flight, and otherwise shares the next, which starts once that one completes.
    /// </summary>
    private Task FlushDirectoryAsync()
    {
        TaskCompletionSource flush;
        lock (_flushSync)
        {
            if (_flushing)
            {
                return (_nextFlush ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            _flushing = true;
            flush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RunFlushes(flush);
        return flush.Task;
    }

    /// <summary>
    /// Runs the flush <paramref name="flush"/> stands for, then hands any flush callers queued meanwhile to the thread
    /// pool, so the caller which ran this one waits for no other's.
    /// </summary>
    private void RunFlushes(TaskCompletionSource flush)
    {
        try
        {
            _fileSystem.FlushDirectory(_directory, DirectoryEntries.Files);
            flush.SetResult();
        }
#pragma warning disable CA1031 // The flush's failure, which the seam has reported, is its callers' answer.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            flush.SetException(failure);
        }

        lock (_flushSync)
        {
            if (_nextFlush is null)
            {
                _flushing = false;
                return;
            }

            (flush, _nextFlush) = (_nextFlush, null);
        }

        _ = Task.Run(() => RunFlushes(flush), CancellationToken.None);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a new temporary file beside <paramref name="path"/> and flushes it, then checks
    /// the content against its writer's <paramref name="crc32c"/>, so nothing it publishes differs from the bytes the
    /// writer verified: the write reads the content in place, and a page changed before or during it fails the check.
    /// An append's file begins with <paramref name="existing"/>, the object's bytes before it.
    /// </summary>
    /// <exception cref="ObjectContentChangedException">The content does not match <paramref name="crc32c"/>; the temporary
    /// file is deleted.</exception>
    private async ValueTask<string> WriteTemporaryAsync(ObjectName name, string path, ArcBuffer content, uint? crc32c, ReadOnlyMemory<byte> existing = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + TemporaryExtension;
        using (var file = _fileSystem.CreateFile(temporary, replace: false, writeThrough: false, preallocationSize: existing.Length + content.Length))
        {
            await file.WriteAsync([existing, .. content.MemorySegments], 0, CancellationToken.None).ConfigureAwait(false);
            file.Flush();
        }

        if (crc32c is { } expected && ObjectStoreChecksums.Crc32C(content) != expected)
        {
            _fileSystem.DeleteFile(temporary);
            throw ObjectContentChangedException.For(name);
        }

        return temporary;
    }

    /// <summary>
    /// Takes the exclusive share of the store's lock file, waiting while another holder has it.
    /// </summary>
    private async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_directory, LockFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return _fileSystem.OpenLock(lockPath);
            }
            catch (WalLockHeldException)
            {
                // Another replace or delete holds the lock; it releases it once it completes.
                await Task.Delay(LockRetry, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
