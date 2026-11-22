using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal.FileSystem;

/// <summary>
/// Direct file I/O: each write reports its own transfer errors, data-synchronous handles or a following data sync make
/// it durable, and each read returns the device's bytes (wal.md §3).
/// </summary>
/// <remarks>
/// <para>
/// Linux reports a buffered file's writeback error once per open file description: "After an error has been reported
/// on one request, subsequent requests on the same file descriptor should return 0, unless further writeback errors
/// have occurred since the previous file synchronization" (<c>Documentation/filesystems/vfs.rst</c>, "Handling errors
/// during writeback"). With buffered writes, one write group's <c>fsync</c> could take another group's error, and a
/// page whose writeback failed stays cached and readable, so a later open in the same boot could read bytes the device
/// never held. A direct write returns its own error, and a direct read bypasses the page cache.
/// </para>
/// <para>
/// Offsets, lengths, and memory of direct reads and writes are multiples of <see cref="Alignment"/>, which covers
/// 512-byte and 4 KiB logical blocks and sectors. Reads of any range are aligned here; writes are whole aligned pages.
/// </para>
/// <para>
/// A Linux file system may accept <c>O_DIRECT</c> and still serve a file through the page cache: ext4 does for
/// <c>data=journal</c>, inline-data, and verity files, and for encrypted files without inline encryption. On that path
/// <c>O_DSYNC</c> reports writeback errors through the open file description again. <c>statx</c>'s
/// <c>STATX_DIOALIGN</c> (Linux 6.1) reports a direct-I/O alignment of zero for such a file, so every open checks it.
/// </para>
/// </remarks>
internal static partial class WalDirectIo
{
    /// <summary>
    /// The alignment of every direct read and write: the size of an aligned <see cref="ArcBuffer"/> page.
    /// </summary>
    public const int Alignment = ArcBufferWriter.AlignedPageSize;

    // Linux open flags, from include/uapi/asm-generic/fcntl.h, which x86-64 uses, and arch/arm64/include/uapi/asm/fcntl.h:
    // O_DIRECT is 0x4000 on x86-64 and 0x10000 on arm64, where 0x4000 is O_DIRECTORY. O_RDWR, O_DSYNC, and O_CLOEXEC are
    // the same on both.
    private const int LinuxReadWrite = 0x2;
    private const int LinuxDataSync = 0x1000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxX64Direct = 0x4000;
    private const int LinuxArm64Direct = 0x10000;

    // statx(2), from include/uapi/linux/stat.h and the architectures' system call tables: struct statx is 256 bytes, with
    // stx_mask at 0x00, stx_dio_mem_align at 0x98, and stx_dio_offset_align at 0x9c.
    internal const long LinuxX64Statx = 332;
    internal const long LinuxArm64Statx = 291;
    internal const int EmptyPath = 0x1000;
    private const uint StatxDirectIoAlignment = 0x2000;
    internal const int StatxLength = 0x100;
    private const int StatxMemoryAlignment = 0x98;
    private const int StatxOffsetAlignment = 0x9c;

    private const int ErrorNoEntry = 2;
    private const int ErrorInvalidArgument = 22;

    /// <summary>
    /// Opens an existing file for direct reads and writes on Linux: <c>O_RDWR | O_DIRECT | O_CLOEXEC</c>, plus
    /// <c>O_DSYNC</c> when <paramref name="dataSync"/> is set, with a shared <c>flock</c> as .NET takes for a shared handle, once
    /// <c>statx</c> reports that the file system serves the file by direct I/O at <see cref="Alignment"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist in its existing directory.</exception>
    /// <exception cref="DirectoryNotFoundException">The file's directory does not exist.</exception>
    /// <exception cref="WalLockHeldException">Another owner holds the file's lock.</exception>
    /// <exception cref="PlatformNotSupportedException">The file system does not serve the file by direct I/O: the volume is
    /// not qualified (wal.md §9).</exception>
    /// <exception cref="IOException">The open fails.</exception>
    [SupportedOSPlatform("linux")]
    public static SafeFileHandle OpenLinux(string path, bool dataSync = true)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var descriptor = Open(path, LinuxOpenFlags(architecture, dataSync));
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                // A path is missing only when its own directory exists, as .NET answers a missing file.
                ErrorNoEntry when IsDirectory(Path.GetDirectoryName(path)) => new FileNotFoundException($"The file '{path}' does not exist.", path),
                ErrorNoEntry => new DirectoryNotFoundException($"The directory of '{path}' does not exist.", new Win32Exception(error)),
                ErrorInvalidArgument => new PlatformNotSupportedException(
                    $"The file system of '{path}' refuses direct I/O, which WriteAheadLog's files require; WriteAheadLog's volumes are qualified ones (wal.md §9).",
                    new Win32Exception(error)),
                _ => new IOException($"Opening '{path}' for direct I/O failed.", new Win32Exception(error)),
            };
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            // As .NET does for a shared handle, only a lock another handle holds refuses the open, an answer; a file system
            // without locks leaves the advisory lock untaken.
            if (WalFlock.Lock(descriptor, WalFlock.Shared | WalFlock.NonBlocking) is WalFlock.LinuxWouldBlock)
            {
                throw new WalLockHeldException($"The file '{path}' is locked by another owner.", new Win32Exception(WalFlock.LinuxWouldBlock));
            }

            ThrowUnlessDirect(descriptor, architecture, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns whether a file whose <c>statx</c> reports <paramref name="mask"/> and these direct-I/O alignments is
    /// served by direct I/O whose offsets, lengths, and memory are multiples of <see cref="Alignment"/>.
    /// </summary>
    internal static bool ServesDirectIo(uint mask, uint memoryAlignment, uint offsetAlignment) =>
        (mask & StatxDirectIoAlignment) != 0
        && offsetAlignment != 0 && Alignment % offsetAlignment == 0
        && memoryAlignment != 0 && Alignment % memoryAlignment == 0;

    private static void ThrowUnlessDirect(int descriptor, Architecture architecture, string path)
    {
        Span<byte> statx = stackalloc byte[StatxLength];
        statx.Clear();
        ReadOnlySpan<byte> emptyPath = [0];
        var number = architecture == Architecture.Arm64 ? LinuxArm64Statx : LinuxX64Statx;
        if (Statx(number, descriptor, emptyPath, EmptyPath, StatxDirectIoAlignment, statx) != 0)
        {
            throw new IOException($"Reading the direct-I/O alignment of '{path}' failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if (!ServesDirectIo(
            BinaryPrimitives.ReadUInt32LittleEndian(statx),
            BinaryPrimitives.ReadUInt32LittleEndian(statx[StatxMemoryAlignment..]),
            BinaryPrimitives.ReadUInt32LittleEndian(statx[StatxOffsetAlignment..])))
        {
            throw new PlatformNotSupportedException(
                $"The file system of '{path}' does not serve it by direct I/O at {Alignment}-byte alignment, as statx's STATX_DIOALIGN (Linux 6.1 or later) reports; WriteAheadLog's files require direct I/O, which only a qualified volume serves (wal.md §9).");
        }
    }

    /// <summary>
    /// Returns whether a directory exists at <paramref name="path"/>; an error reading it propagates, never read as absence.
    /// </summary>
    private static bool IsDirectory(string? path) =>
        path is not null && FileSystemAnswers.GetAttributes(path) is { } attributes && attributes.HasFlag(FileAttributes.Directory);

    /// <summary>
    /// Returns <c>O_RDWR | O_DIRECT | O_CLOEXEC</c>, with <c>O_DSYNC</c> when <paramref name="dataSync"/> is set, for a
    /// Linux process of the given architecture.
    /// </summary>
    internal static int LinuxOpenFlags(Architecture architecture, bool dataSync = true)
    {
        var sync = dataSync ? LinuxDataSync : 0;
        if (architecture == Architecture.X64)
        {
            return LinuxReadWrite | LinuxX64Direct | sync | LinuxCloseOnExec;
        }

        if (architecture == Architecture.Arm64)
        {
            return LinuxReadWrite | LinuxArm64Direct | sync | LinuxCloseOnExec;
        }

        throw new PlatformNotSupportedException($"Direct write-ahead log I/O supports Linux on x64 and arm64, not {architecture}.");
    }

    /// <summary>
    /// Rents the aligned pages which cover <paramref name="length"/> bytes at <paramref name="offset"/>, returning the
    /// aligned start of the range they cover.
    /// </summary>
    public static long RentAligned(ArcBufferWriter writer, long offset, int length, List<Memory<byte>> pages)
    {
        var start = offset & -(long)Alignment;
        var end = (offset + length + Alignment - 1) & -(long)Alignment;
        for (var position = start; position < end; position += Alignment)
        {
            var page = writer.GetMemory();
            pages.Add(page[..Alignment]);
            writer.AdvanceWriter(Alignment);
        }

        return start;
    }

    /// <summary>
    /// Returns, from the aligned pages <see cref="RentAligned"/> rented starting at <paramref name="start"/> and into
    /// which <paramref name="read"/> bytes were read, an owned buffer of the up to <paramref name="length"/> bytes at
    /// <paramref name="offset"/>.
    /// </summary>
    public static ArcBuffer Take(ArcBufferWriter writer, long start, long offset, int length, long read)
    {
        var skip = (int)(offset - start);
        writer.AdvanceReader(skip);
        return writer.ConsumeSlice((int)Math.Clamp(read - skip, 0, length));
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Open(string path, int flags);

    // statx(2) through libc's syscall, as the io_uring storage calls its system calls; every argument is passed at
    // register width.
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial long Statx(long number, long directory, ReadOnlySpan<byte> path, long flags, long mask, Span<byte> buffer);
}
