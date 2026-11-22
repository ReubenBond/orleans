namespace Orleans.Durable.Storage.Diagnostics;

/// <summary>
/// The site which stopped the process through <see cref="FatalErrorHandler"/>. Each value is the exit code with which
/// the process stops, and one production site reports it (conventions.md §14).
/// </summary>
/// <remarks>
/// <para>
/// The values lie in 79–125, clear of sysexits (64–78), the shell's 126 and 127, and the 128 + signal statuses.
/// <see cref="Defect"/> is the code every defect shares, which the component that detects it reports
/// (<see cref="FatalErrorHandler.StopOnDefect"/>), naming the defect in the report. Every other value is a failed local
/// storage operation, which only the storage seams report (<see cref="FatalErrorHandler.StopOnStorageFailure"/>); each
/// has its own code, so operators tell storage failures apart by it.
/// </para>
/// <para>
/// Orleans.Durable.Storage.Diagnostics lies beneath every layer, yet this registry names sites in the layers above it. That is deliberate: one
/// registry keeps every exit code unique, which separate enumerations could not (spec README §2.5).
/// </para>
/// </remarks>
public enum FatalErrorSite
{
    /// <summary>
    /// Defect, the code which every defect shares: the report names the defect, as a WAL outcome callback which threw,
    /// or an archive object's bytes which no longer match the CRC-32C their writer computed when it verified them.
    /// </summary>
    Defect = 79,

    /// <summary>The WAL's file system: whether a directory exists.</summary>
    DirectoryExists = 80,

    /// <summary>The WAL's file system: creating a directory.</summary>
    CreateDirectory = 81,

    /// <summary>The WAL's file system: deleting a directory and its contents.</summary>
    DeleteDirectory = 82,

    /// <summary>The WAL's file system: listing a directory's files, by name, with their lengths, or with their last writes.</summary>
    ListFiles = 83,

    /// <summary>The WAL's file system: listing a directory's subdirectories.</summary>
    ListDirectories = 84,

    /// <summary>The WAL's file system: whether a file exists.</summary>
    FileExists = 85,

    /// <summary>The WAL's file system: opening a lock file.</summary>
    OpenLock = 86,

    /// <summary>The WAL's file system: creating a file.</summary>
    CreateFile = 87,

    /// <summary>The WAL's file system: opening a file.</summary>
    OpenFile = 88,

    /// <summary>The WAL's file system: renaming a file.</summary>
    MoveFile = 89,

    /// <summary>The WAL's file system: publishing a file under a new name without replacing another.</summary>
    LinkFile = 90,

    /// <summary>The WAL's file system: reading a whole file.</summary>
    ReadFile = 91,

    /// <summary>The WAL's file system: deleting a file.</summary>
    DeleteFile = 92,

    /// <summary>The WAL's file system: flushing a directory, which makes its entries durable.</summary>
    FlushDirectory = 93,

    /// <summary>The WAL's file system: deleting a directory which holds no entries, the last step of a directory's deletion.</summary>
    DeleteEmptyDirectory = 94,

    /// <summary>The WAL's file system: deleting a directory's lock file, the step of a directory's deletion before the last.</summary>
    DeleteLockFile = 95,

    /// <summary>A handle the WAL's file system opened: reading its file's length.</summary>
    HandleLength = 96,

    /// <summary>A handle the WAL's file system opened: a read into one buffer.</summary>
    HandleRead = 97,

    /// <summary>A handle the WAL's file system opened: a read into several buffers.</summary>
    HandleReadScatter = 98,

    /// <summary>A handle the WAL's file system opened: a write of one buffer.</summary>
    HandleWrite = 99,

    /// <summary>A handle the WAL's file system opened: a write of several buffers.</summary>
    HandleWriteGather = 100,

    /// <summary>A handle the WAL's file system opened: setting its file's length.</summary>
    HandleSetLength = 101,

    /// <summary>A handle the WAL's file system opened: flushing its file, which makes its contents and length durable.</summary>
    HandleFlush = 102,

    /// <summary>The WAL's io_uring storage: opening a file, flushing it, and reading its length.</summary>
    IoUringOpen = 103,

    /// <summary>The WAL's io_uring storage: a write.</summary>
    IoUringWrite = 104,

    /// <summary>The WAL's io_uring storage: a read.</summary>
    IoUringRead = 105,

    /// <summary>The WAL's io_uring storage: truncating a file and flushing it.</summary>
    IoUringTruncate = 106,

    /// <summary>The WAL's io_uring storage: submitting to or reaping from its ring, which every operation in flight needs.</summary>
    IoUringRing = 107,

    /// <summary>The journal index's RocksDB storage: creating, opening, or reading the watermarks of an index.</summary>
    JournalIndexOpen = 108,

    /// <summary>The journal index's RocksDB storage: a <c>key_index</c> prefix lookup or existence probe.</summary>
    JournalIndexLookup = 109,

    /// <summary>The journal index's RocksDB storage: a <c>tombstones</c> ledger scan.</summary>
    JournalIndexScan = 110,

    /// <summary>The journal index's RocksDB storage: applying a batch or dropping ledgers.</summary>
    JournalIndexWrite = 111,

    /// <summary>The journal index's RocksDB storage: an atomic multi-column-family flush.</summary>
    JournalIndexFlush = 112,

    /// <summary>The journal index's RocksDB storage: deleting and reopening an index.</summary>
    JournalIndexReset = 113,

    /// <summary>The WAL's file system: summing the bytes of the files beneath a directory.</summary>
    MeasureDirectory = 114,

    /// <summary>The WAL's file system: reading the free and total space of the volume holding a directory.</summary>
    VolumeSpace = 115,

    /// <summary>The journal index's RocksDB storage: a background integrity check.</summary>
    JournalIndexCheck = 116,

    /// <summary>The native CASPaxos MetaStore's SQLite storage: flushing a database's directory, which makes its entries durable.</summary>
    ConsensusDirectoryFlush = 117,

    /// <summary>
    /// The native CASPaxos MetaStore's SQLite storage: a transaction of a voter's acceptor database, its identity binding
    /// and admissions, or a proposer's ballot reservations.
    /// </summary>
    ConsensusTransaction = 118,
}

