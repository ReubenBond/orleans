using System.Diagnostics.CodeAnalysis;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Simulation;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Creates and opens write-ahead log directories with explicit durability semantics.
/// </summary>
/// <remarks>
/// One storage instance may be shared by every log in a process. Implementations supply io_uring, portable, and
/// simulated storage.
/// </remarks>
public abstract class WalStorage
{
    /// <summary>
    /// Creates the directory under its existing parent, or accepts an existing one, and completes once its name is
    /// durable. Repeating it makes durable a name a creator which crashed left unsynced.
    /// </summary>
    /// <remarks>
    /// The parent is the caller's root or lies beneath it, and it is the only directory this flushes (wal.md §3).
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">The parent does not exist.</exception>
    public abstract ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Takes exclusive ownership of an existing directory and makes its entries durable.
    /// </summary>
    /// <remarks>
    /// The directory's name is durable once a <see cref="CreateDirectoryAsync"/> for it has returned. Callers never open
    /// a directory while it is being deleted (wal.md §3).
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist, and its absence is durable.</exception>
    public abstract ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a directory which no one owns, with everything in it, if it exists, and completes when the deletion is
    /// durable.
    /// </summary>
    /// <remarks>
    /// Callers never open a directory while it is being deleted (wal.md §3). An owner which arrives anyway can lock only
    /// the emptied directory, which it then keeps (WAL-I11).
    /// </remarks>
    /// <exception cref="WalLockHeldException">Another owner holds the directory's lock, and nothing is deleted; or an
    /// owner arrived during the deletion, and the directory remains holding only the entries that owner creates, each
    /// durable once its open returns. <see cref="SimulatedWalStorage"/> answers only the first.</exception>
    public abstract ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the names of the directories directly within <paramref name="path"/> in ordinal order, or none when it
    /// does not exist, once every name it reports, and the absence of every other, is durable.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Measures a directory its caller holds: the bytes of the files beneath it, and the space of the volume holding it,
    /// queried for the directory itself, so a directory which is its own mount point reports its own volume.
    /// </summary>
    /// <remarks>
    /// A reparse point (a symbolic link, a junction, or a mounted volume) is neither counted nor entered. Below the
    /// directory, an entry which a concurrent change removes, which is being deleted, or which the process cannot read is
    /// an answer, left out or counted with the length it had. A failure of the directory's own listing, its absence
    /// among them, or of its volume's query stops the process (wal.md §9).
    /// </remarks>
    public abstract ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// A directory's measurement (<see cref="WalStorage.MeasureDirectoryAsync"/>): the bytes of the files beneath it, and the
/// space of the volume holding it.
/// </summary>
/// <param name="UsedBytes">The bytes of the files in the directory and in every directory below it.</param>
/// <param name="FreeBytes">The free space of the directory's volume available to the process.</param>
/// <param name="TotalBytes">The size of the directory's volume.</param>
public readonly record struct WalDirectoryUsage(long UsedBytes, long FreeBytes, long TotalBytes);

/// <summary>
/// An exclusively owned write-ahead log directory holding numbered log files and named files.
/// </summary>
/// <remarks>
/// Every answer from the directory's names (a listing, an open, or a refusal of an existing name) reports only durable
/// names, including those a creation or deletion in flight changed: a failed creation or deletion
/// asserts nothing about its name.
/// </remarks>
public abstract class WalDirectory : IAsyncDisposable
{
    /// <summary>
    /// Gets the directory's path, by which a failure of its storage is reported.
    /// </summary>
    public abstract string Path { get; }

    /// <summary>
    /// Gets the largest of the filesystem block, physical sector, and direct-I/O alignment sizes: the smallest page
    /// which a write never shares with another page.
    /// </summary>
    public abstract int MinimumPageSize { get; }

    /// <summary>
    /// Lists the published log-file numbers in ascending order.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a log file holding <paramref name="header"/> at offset 0 and publishes its name.
    /// </summary>
    /// <remarks>
    /// The header, the reserved length, and the name are durable before this returns, and the name never refers to
    /// a file without its complete header. When <paramref name="preallocatedLength"/> is non-zero, the file holds that
    /// many bytes, every byte after the header written as zero, so later writes within it change no file metadata.
    /// When <paramref name="pace"/> is set, each zero-fill write waits for it first. A storage which cannot serve the
    /// file refuses it before its name is published. A failed or cancelled creation asserts nothing: its name may be
    /// published, with its complete header, and is durable once the directory's next flush completes. The caller keeps
    /// its lease on <paramref name="header"/> until the call completes; the directory keeps no reference to it.
    /// </remarks>
    /// <exception cref="IOException">A file with the number exists.</exception>
    public abstract ValueTask<WalFile> CreateFileAsync(
        ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a log file holding <paramref name="header"/> at offset 0, with <paramref name="length"/> bytes allocated
    /// but not written, and publishes its name.
    /// </summary>
    /// <remarks>
    /// As <see cref="CreateFileAsync"/>, except that the bytes after the header are allocated without being written:
    /// they read as zero, and a later write within them may change file metadata, which its own durability covers. It
    /// writes only the header, so it costs no zero-fill, while each small durable write into the file may cost a
    /// metadata commit.
    /// </remarks>
    /// <exception cref="IOException">A file with the number exists.</exception>
    public abstract ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an existing log file and makes every byte it holds durable before returning, so subsequent reads observe
    /// only durable bytes.
    /// </summary>
    /// <exception cref="FileNotFoundException">No file has the number.</exception>
    public abstract ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a log file which no handle holds open, if it exists, and completes when the deletion is durable.
    /// </summary>
    public abstract ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a named file holding <paramref name="contents"/> and publishes its name.
    /// </summary>
    /// <remarks>
    /// The contents and the name are durable before this returns, and the name never refers to a file without its
    /// complete contents. A failed creation asserts nothing, as for <see cref="CreateFileAsync"/>. Named files hold data
    /// a log's owner keeps beside its log files; a name is a simple file name which is neither a log-file name nor a
    /// temporary name. The caller keeps its lease on <paramref name="contents"/> until the call completes; the directory
    /// keeps no reference to it.
    /// </remarks>
    /// <exception cref="IOException">A file with the name exists.</exception>
    public abstract ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an existing named file.
    /// </summary>
    /// <exception cref="FileNotFoundException">No file has the name.</exception>
    public abstract ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the names of the published named files ending with <paramref name="extension"/>, in ordinal order.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a named file, if it exists, and completes when the deletion is durable.
    /// </summary>
    public abstract ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public abstract ValueTask DisposeAsync();
}

/// <summary>
/// One open log file or named file.
/// </summary>
public abstract class WalFile : IAsyncDisposable
{
    /// <summary>
    /// Gets the file length in bytes.
    /// </summary>
    public abstract long Length { get; }

    /// <summary>
    /// Writes <paramref name="buffers"/> back to back at <paramref name="offset"/> and completes when they are
    /// durable.
    /// </summary>
    /// <remarks>
    /// Every buffer is a run of whole pages from the aligned size class, written at an offset which is a multiple of
    /// their size, as direct I/O requires. Each write reports its own failure: one which completes is durable whatever
    /// becomes of the file's other writes. The caller keeps its leases and the bytes unchanged until completion. Calls
    /// for disjoint ranges may overlap and complete in any order. A call runs to completion once made, and short writes
    /// are finished internally.
    /// </remarks>
    public abstract ValueTask WriteAsync(long offset, ReadOnlyMemory<ArcBuffer> buffers);

    /// <summary>
    /// Returns an owned buffer holding up to <paramref name="length"/> bytes at <paramref name="offset"/>; it is
    /// shorter only at the end of the file.
    /// </summary>
    /// <remarks>
    /// The bytes are the device's: a failed write's bytes are read only as far as the device holds them.
    /// </remarks>
    public abstract ValueTask<ArcBuffer> ReadAsync(long offset, int length, CancellationToken cancellationToken);

    /// <summary>
    /// Shortens the file to <paramref name="length"/> bytes and completes when the new length is durable.
    /// </summary>
    /// <remarks>
    /// Replay calls this while no write to the file is in flight.
    /// </remarks>
    public abstract ValueTask TruncateAsync(long length, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public abstract ValueTask DisposeAsync();
}
