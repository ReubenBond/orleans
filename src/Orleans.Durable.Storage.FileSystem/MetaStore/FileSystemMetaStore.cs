using System.Buffers;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;

namespace Orleans.Durable.Storage.FileSystem.MetaStore;

/// <summary>
/// Keeps every register as one file below a root directory, for clusters whose processes share one host's file
/// system, such as a soak or a lab on one machine.
/// </summary>
/// <remarks>
/// <para>
/// A register keyed <c>{cluster:N}/{name}</c> is the file <c>{root}/{cluster:N}/{stem}~clfm</c>. Its stem is the name
/// with each <c>/</c> written as <c>+</c> and each <c>.</c> as <c>=</c>; its lock file is <c>{stem}~lock</c>, and a
/// write's temporary file <c>{stem}~{guid:N}~tmp</c>. No register name holds <c>+</c>, <c>=</c>, or <c>~</c>, so distinct
/// registers have distinct files, every file lies in its cluster's directory, and no file name holds a dot (MS-I4).
/// Every process which opens the same root addresses the same registers. A name whose temporary file's name would exceed
/// 255 characters is refused.
/// </para>
/// <para>
/// The root is an operator's: it and every directory above it exist durably before the store is used. The store
/// creates only the cluster directories in it, and flushes only the root and those directories.
/// </para>
/// <para>
/// A write makes its cluster directory's name durable, holds the register's lock file exclusively, checks the version,
/// writes the new value to a temporary file, flushes it, and renames it over the register, so a read, which opens the
/// file without the lock, returns one write's value and version whole. A write flushes the cluster directory before it
/// answers <see cref="RegisterWriteOutcome.Applied"/>, and a read which finds the file, or a write which answers
/// <see cref="RegisterWriteOutcome.Mismatch"/> against it, flushes the directory before it answers, so every answer
/// survives an operating-system crash (conventions.md §13, MS-I1). Each applied write stamps a fresh random version
/// (MS-I5). A write copies its value into the file's bytes before it writes them, and a read wraps the array it reads
/// the file into without a copy, which the store never changes. The lock is the seam's lock
/// file: the handle's exclusive sharing mode on Windows, and <c>flock(2)</c>, which the seam takes itself, on Linux and
/// macOS, so it holds across processes and containers sharing the root on one kernel. The root is on a local file
/// system (wal.md §3).
/// </para>
/// <para>
/// Every file-system call goes through the WAL's <see cref="WalFileSystem"/>. Its directory flush is qualified on Linux
/// and on Windows on local NTFS volumes; on Windows it refuses every other volume, and on macOS it issues
/// <c>fsync</c> unqualified (wal.md §9).
/// </para>
/// </remarks>
public sealed class FileSystemMetaStore : IMetaStore
{
    /// <summary>
    /// The longest stem a register's files may have: its temporary file's name, the longest, adds 37 characters
    /// (<c>~</c>, 32 hexadecimal digits, and <c>~tmp</c>) and stays within 255.
    /// </summary>
    internal const int MaximumStemLength = 255 - 37;

    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private readonly WalFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemMetaStore"/> class.
    /// </summary>
    /// <param name="root">The directory holding the registers. It and every directory above it exist durably before the
    /// store is used.</param>
    /// <param name="timeProvider">Paces the retries of a write waiting for its register's lock.</param>
    public FileSystemMetaStore(string root, TimeProvider timeProvider)
        : this(root, timeProvider, WalFileSystem.Os)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemMetaStore"/> class over <paramref name="fileSystem"/>.
    /// </summary>
    internal FileSystemMetaStore(string root, TimeProvider timeProvider, WalFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _root = WalFileSystem.Normalize(root);
        _timeProvider = timeProvider;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The register's temporary file name would exceed 255 characters.</exception>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        // Forming the key validates the cluster and the name.
        _ = MetaStoreRegisterName.ToKey(cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Stem(name);
        if (stem.Length > MaximumStemLength)
        {
            throw new ArgumentException(
                $"The register name '{name}' is too long for the file-system MetaStore, whose file names hold at most 255 characters.",
                nameof(name));
        }

        return ValueTask.FromResult<IConditionalRegister>(new FileSystemConditionalRegister(
            _root, Path.Combine(_root, cluster.ToString("N")), stem, _fileSystem, _timeProvider));
    }

    /// <summary>
    /// Returns the stem of a register's files: the name with each <c>/</c> written as <c>+</c> and each <c>.</c> as
    /// <c>=</c>.
    /// </summary>
    internal static string Stem(string name) => name.Replace('/', '+').Replace('.', '=');
}

/// <summary>
/// What decoding a register file gives (conventions.md §15): the entry, or the <see cref="InvalidDataException"/> naming
/// the first rule the file breaks, with the rule's reason code (metastore.md §7).
/// </summary>
internal readonly union RegisterFileDecoding(MetaStoreEntry, InvalidDataException);

/// <summary>
/// One register file: <c>CLFM</c>, format version 1, a 16-byte version, then the value.
/// </summary>
/// <remarks>
/// A rename can be visible before it is durable: its writer, in this or another process, may not have flushed the
/// directory yet, or may have crashed before it could. So every answer flushes the directory before it leaves: a read
/// of a present file and a mismatch against one after reading the file, and an applied write after its rename.
/// </remarks>
internal sealed class FileSystemConditionalRegister : IConditionalRegister
{
    internal const int FormatVersionOffset = 4;
    internal const int VersionOffset = 5;
    internal const int VersionLength = 16;
    internal const int HeaderLength = VersionOffset + VersionLength;
    internal const byte FormatVersion = 1;

    private static readonly byte[] Magic = "CLFM"u8.ToArray();
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(1);

    private readonly string _root;
    private readonly string _directory;
    private readonly string _stem;
    private readonly string _path;
    private readonly WalFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemConditionalRegister"/> class for the register whose files
    /// have <paramref name="stem"/> in the cluster directory <paramref name="directory"/> of <paramref name="root"/>.
    /// </summary>
    internal FileSystemConditionalRegister(string root, string directory, string stem, WalFileSystem fileSystem, TimeProvider timeProvider)
    {
        _root = root;
        _directory = directory;
        _stem = stem;
        _path = Path.Combine(directory, stem + "~clfm");
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfRootMissing();

        // The cluster's directory is created by its first write and never removed, so its absence from the root answers
        // that no register of the cluster was written. The file system answers a missing path only within an existing
        // directory: reading a register in a missing directory would be a failure.
        if (!_fileSystem.DirectoryExists(_directory))
        {
            return null;
        }

        var encoded = await _fileSystem.ReadFileAsync(_path, cancellationToken).ConfigureAwait(false);
        if (encoded is not null)
        {
            // MS-I1: the rename which published the value is durable before the value leaves. The flush follows the
            // read, so it covers every rename the read saw; a flush before the read would miss one landing between them.
            FlushDirectory();
        }

        return encoded is null ? null : Entry(Decode(encoded));
    }

    /// <inheritdoc/>
    public async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfRootMissing();
        if (expectedVersion is not null && !Guid.TryParseExact(expectedVersion.Value, "N", out _))
        {
            return RegisterWriteOutcome.Mismatch.Instance;
        }

        MakeDirectoryDurable();
        using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);

        // "The MetaStore component has a simple API: it is a single versioned register supporting a conditional
        // write. Reading the MetaStore returns a value with an attached version. Writing to it requires supplying a
        // new value and an expected existing version." See research/papers/delos/paper.md, section 3.2.
        var encoded = await _fileSystem.ReadFileAsync(_path, cancellationToken).ConfigureAwait(false);
        var currentVersion = encoded is null ? null : DecodeVersion(encoded);
        if (currentVersion != expectedVersion)
        {
            if (encoded is not null)
            {
                // MS-I1: the version the mismatch is decided against is durable before the mismatch leaves.
                FlushDirectory();
            }

            // A mismatch is reported only while the caller's token is not cancelled (metastore.md §6).
            cancellationToken.ThrowIfCancellationRequested();
            return RegisterWriteOutcome.Mismatch.Instance;
        }

        // The file's bytes are the value's copy: the caller's sequence is read here, before the write's task completes,
        // and never again (conventions.md §1).
        var version = Guid.NewGuid();
        var contents = Encode(version, value);

        // A write which fails before its rename leaves its temporary file, which no read opens; a failure stops the
        // process (conventions.md §14).
        var temporary = Path.Combine(_directory, _stem + "~" + Guid.NewGuid().ToString("N") + "~tmp");
        using (var handle = _fileSystem.CreateFile(temporary, replace: false, writeThrough: false, preallocationSize: 0))
        {
            await handle.WriteAsync(contents, 0, CancellationToken.None).ConfigureAwait(false);
            handle.Flush();
        }

        // On Windows the rename waits while a reader, which holds the file only for one read, has it open (wal.md §9).
        _fileSystem.MoveFile(temporary, _path, replace: true);

        // MS-I1, conventions.md §13: the rename which applied the write is durable before the version leaves.
        FlushDirectory();
        return new RegisterWriteOutcome.Applied(ToVersion(version));
    }

    /// <summary>
    /// Refuses a root which does not exist: an operator creates it, durably, before the store is used.
    /// </summary>
    private void ThrowIfRootMissing()
    {
        if (!_fileSystem.DirectoryExists(_root))
        {
            throw new DirectoryNotFoundException(
                $"The MetaStore root '{_root}' does not exist. An operator creates it, durably, before the store is used; the store creates only what lies in it.");
        }
    }

    /// <summary>
    /// Creates the cluster directory when it is absent, and makes its name durable whether or not this write created it.
    /// </summary>
    /// <remarks>
    /// A writer which crashed after creating the directory may have left its name unsynced, so every write flushes the
    /// root before it renames into the directory, and a read need flush only the directory.
    /// </remarks>
    private void MakeDirectoryDurable() => _fileSystem.CreateDirectoryDurably(_directory);

    private void FlushDirectory() => _fileSystem.FlushDirectory(_directory, DirectoryEntries.Files);

    /// <summary>
    /// Takes the exclusive share of the register's lock file, waiting while another holder has it.
    /// </summary>
    private async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_directory, _stem + "~lock");
        while (true)
        {
            try
            {
                return _fileSystem.OpenLock(lockPath);
            }
            catch (WalLockHeldException)
            {
                // Another writer holds the register; it releases the lock once its write completes.
                await Task.Delay(LockRetry, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the register file holding <paramref name="value"/> at <paramref name="version"/>.
    /// </summary>
    internal static byte[] Encode(Guid version, in ReadOnlySequence<byte> value)
    {
        var encoded = new byte[checked(HeaderLength + (int)value.Length)];
        Magic.CopyTo(encoded, 0);
        encoded[FormatVersionOffset] = FormatVersion;
        _ = version.TryWriteBytes(encoded.AsSpan(VersionOffset, VersionLength));
        value.CopyTo(encoded.AsSpan(HeaderLength));
        return encoded;
    }

    /// <summary>
    /// Decodes a register file (metastore.md §7): the entry it holds, which wraps <paramref name="encoded"/> without a
    /// copy, or the rejection naming the first rule it breaks, with the reason code <c>truncated</c> (shorter than its
    /// 21-byte header), <c>bad-magic</c>, or <c>bad-version</c>, in that order (conventions.md §15).
    /// </summary>
    internal static RegisterFileDecoding Decode(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length < HeaderLength)
        {
            return DecodeRejection.Create("truncated", "A MetaStore register file is shorter than its 21-byte header.");
        }

        if (!encoded.AsSpan(0, FormatVersionOffset).SequenceEqual(Magic))
        {
            return DecodeRejection.Create("bad-magic", "A MetaStore register file does not carry the CLFM magic.");
        }

        if (encoded[FormatVersionOffset] != FormatVersion)
        {
            return DecodeRejection.Create("bad-version", "A MetaStore register file has a format version other than 1.");
        }

        return new MetaStoreEntry(ToVersion(new Guid(encoded.AsSpan(VersionOffset, VersionLength))), ArcBuffer.Wrap(encoded.AsMemory(HeaderLength)));
    }

    /// <summary>
    /// Returns the version a register file holds, or throws the rejection naming the first rule it breaks.
    /// </summary>
    private static MetaStoreVersion DecodeVersion(byte[] encoded)
    {
        using var entry = Entry(Decode(encoded));
        return entry.Version;
    }

    /// <summary>
    /// Returns the entry a decoding holds, or throws its rejection: the MetaStore has no corruption exception, so a
    /// malformed register file fails the read or write with the rejection itself (conventions.md §15).
    /// </summary>
    private static MetaStoreEntry Entry(RegisterFileDecoding decoding)
    {
        Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
        return decoding switch
        {
            MetaStoreEntry entry => entry,
            InvalidDataException rejection => throw rejection,
            null => throw new UnreachableException(),
        };
    }

    private static MetaStoreVersion ToVersion(Guid version) => new(version.ToString("N"));
}
