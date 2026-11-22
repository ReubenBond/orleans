using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orleans.Durable.Storage.Engine.Wal.FileSystem;

/// <summary>
/// <c>flock(2)</c> on Linux and macOS: the exclusive lock through which a lock file excludes every other owner in every
/// process on the host (WAL-I11; wal.md §3), and the shared lock a direct open takes, as .NET takes for a shared handle.
/// </summary>
/// <remarks>
/// .NET takes <c>flock</c> for a handle it opens only best-effort: <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c> skips it,
/// and every error but a held lock is ignored, so a lock file's open could succeed while excluding nobody. The seam
/// therefore takes the exclusive lock itself, and every error but a held lock fails it.
/// </remarks>
internal static partial class WalFlock
{
    // flock(2)'s operations, the same on Linux (include/uapi/asm-generic/fcntl.h) and macOS (sys/fcntl.h).
    public const int Shared = 1;
    public const int Exclusive = 2;
    public const int NonBlocking = 4;

    // errno values: Linux's (include/uapi/asm-generic/errno-base.h and errno.h) beside macOS's (sys/errno.h). EINTR is 4
    // on both; Linux's EOPNOTSUPP and ENOTSUP are one value, and macOS's are two.
    public const int Interrupted = 4;
    public const int LinuxWouldBlock = 11;
    public const int MacOsWouldBlock = 35;
    public const int LinuxNoLocks = 37;
    public const int MacOsNoLocks = 77;
    public const int LinuxNoSystemCall = 38;
    public const int MacOsNoSystemCall = 78;
    public const int LinuxNotSupported = 95;
    public const int MacOsNotSupported = 45;
    public const int MacOsOperationNotSupported = 102;

    // statfs(2) on Linux: struct statfs is 120 bytes on x86-64 and arm64, and begins with f_type, a long; NFS_SUPER_MAGIC
    // (include/uapi/linux/magic.h) names NFS.
    private const int StatfsLength = 120;
    private const long NfsMagic = 0x6969;

    // statx(2) on Linux (include/uapi/linux/stat.h): STATX_INO, and stx_mask at 0x00, stx_ino at 0x20, stx_dev_major at
    // 0x88, and stx_dev_minor at 0x8c; AT_FDCWD names the working directory, against which a full path resolves.
    private const uint StatxInode = 0x100;
    private const int StatxInodeOffset = 0x20;
    private const int StatxDeviceMajorOffset = 0x88;
    private const int StatxDeviceMinorOffset = 0x8c;
    private const long CurrentDirectory = -100;

    // stat(2) on macOS with 64-bit inodes (sys/stat.h): struct stat is 144 bytes, with st_dev, an int, at 0 and st_ino at
    // 8. x86-64 names that variant fstat$INODE64 and stat$INODE64; arm64 has no other.
    private const int MacOsStatLength = 144;
    private const int MacOsInodeOffset = 8;

    // ENOENT, the same on Linux and macOS.
    private const int NoEntry = 2;

    /// <summary>
    /// Gets the <c>errno</c> with which a held lock refuses a non-blocking <c>flock</c> on this platform.
    /// </summary>
    public static int WouldBlock => OperatingSystem.IsMacOS() ? MacOsWouldBlock : LinuxWouldBlock;

    /// <summary>
    /// Takes <c>flock(</c><paramref name="operation"/><c>)</c> on <paramref name="descriptor"/>, again whenever a signal
    /// interrupts it, and returns 0 or the <c>errno</c> it failed with.
    /// </summary>
    public static int Lock(int descriptor, int operation)
    {
        while (Flock(descriptor, operation) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
            {
                return error;
            }
        }

        return 0;
    }

    /// <summary>
    /// Returns the exception for an exclusive, non-blocking <c>flock</c> of <paramref name="path"/> which failed with
    /// <paramref name="error"/>: <see cref="WalLockHeldException"/> when another owner holds the lock;
    /// <see cref="PlatformNotSupportedException"/> when the file system takes no <c>flock</c>, so it is not a local
    /// file system Durable Storage qualifies; and <see cref="IOException"/> otherwise.
    /// </summary>
    public static Exception ExclusiveFailure(int error, string path, bool macOs)
    {
        if (error == (macOs ? MacOsWouldBlock : LinuxWouldBlock))
        {
            return new WalLockHeldException($"Another owner holds the lock '{path}'.", new Win32Exception(error));
        }

        var unsupported = macOs
            ? error is MacOsNotSupported or MacOsOperationNotSupported or MacOsNoSystemCall
            : error is LinuxNotSupported or LinuxNoSystemCall;
        return unsupported
            ? new PlatformNotSupportedException(
                $"The file system of '{path}' takes no flock(2) lock, which a lock file requires; durable storage's lock files need a local file system.",
                new Win32Exception(error))
            : new IOException($"Locking '{path}' failed.", new Win32Exception(error));
    }

    /// <summary>
    /// Returns whether the file system which holds <paramref name="descriptor"/> is NFS, on Linux.
    /// </summary>
    /// <exception cref="IOException"><c>fstatfs</c> failed.</exception>
    public static bool IsLinuxNfs(int descriptor, string path)
    {
        Span<byte> statfs = stackalloc byte[StatfsLength];
        if (Fstatfs(descriptor, statfs) != 0)
        {
            throw new IOException($"Reading the file system of '{path}' failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return BinaryPrimitives.ReadInt64LittleEndian(statfs) == NfsMagic;
    }

    /// <summary>
    /// Takes the exclusive, non-blocking <c>flock</c> on <paramref name="handle"/>'s file and returns whether
    /// <paramref name="path"/> still names that file once it holds it, on Linux and macOS.
    /// </summary>
    /// <remarks>
    /// The open and the lock are two steps: a deletion can unlink the file the open found, and another owner then
    /// creates and locks a new file at the path, so a lock on the unlinked file would make a second owner (WAL-I11). A
    /// caller which meets <see langword="false"/> disposes the handle and opens the path again.
    /// </remarks>
    /// <exception cref="WalLockHeldException">Another owner holds the lock.</exception>
    /// <exception cref="PlatformNotSupportedException">The file system takes no <c>flock</c>, or is NFS on Linux.</exception>
    public static bool LockIfNamed(SafeFileHandle handle, string path)
    {
        var descriptor = (int)handle.DangerousGetHandle();
        if (OperatingSystem.IsLinux() && IsLinuxNfs(descriptor, path))
        {
            throw new PlatformNotSupportedException($"The lock file '{path}' is on NFS; durable storage's lock files need a local file system.");
        }

        var error = Lock(descriptor, Exclusive | NonBlocking);
        if (error != 0)
        {
            throw ExclusiveFailure(error, path, OperatingSystem.IsMacOS());
        }

        return IdentityOf(path) == IdentityOf(descriptor, path);
    }

    /// <summary>
    /// Returns the device and inode of the file <paramref name="descriptor"/> holds open.
    /// </summary>
    /// <exception cref="IOException">Reading them failed.</exception>
    private static (ulong Device, ulong Inode) IdentityOf(int descriptor, string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            Span<byte> stat = stackalloc byte[MacOsStatLength];
            var result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Fstat(descriptor, stat) : FstatInode64(descriptor, stat);
            return result == 0
                ? MacOsIdentity(stat)
                : throw new IOException($"Reading the identity of '{path}' failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        Span<byte> statx = stackalloc byte[WalDirectIo.StatxLength];
        ReadOnlySpan<byte> emptyPath = [0];
        return LinuxStatx(descriptor, emptyPath, WalDirectIo.EmptyPath, statx) == 0
            ? LinuxIdentity(statx, path)
            : throw new IOException($"Reading the identity of '{path}' failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    /// <summary>
    /// Returns the device and inode of the file at <paramref name="path"/>, or <see langword="null"/> when nothing exists
    /// there.
    /// </summary>
    /// <exception cref="IOException">Reading them failed.</exception>
    private static (ulong Device, ulong Inode)? IdentityOf(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        long result;
        Span<byte> buffer = stackalloc byte[Math.Max(MacOsStatLength, WalDirectIo.StatxLength)];
        if (OperatingSystem.IsMacOS())
        {
            result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Stat(bytes, buffer) : StatInode64(bytes, buffer);
        }
        else
        {
            result = LinuxStatx(CurrentDirectory, bytes, 0, buffer);
        }

        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NoEntry ? null : throw new IOException($"Reading the identity of '{path}' failed.", new Win32Exception(error));
        }

        return OperatingSystem.IsMacOS() ? MacOsIdentity(buffer) : LinuxIdentity(buffer, path);
    }

    private static long LinuxStatx(long directory, ReadOnlySpan<byte> path, long flags, Span<byte> buffer)
    {
        buffer.Clear();
        var number = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? WalDirectIo.LinuxArm64Statx : WalDirectIo.LinuxX64Statx;
        return WalDirectIo.Statx(number, directory, path, flags, StatxInode, buffer);
    }

    private static (ulong Device, ulong Inode) LinuxIdentity(ReadOnlySpan<byte> statx, string path)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(statx) & StatxInode) == 0)
        {
            throw new IOException($"The file system of '{path}' reports no inode.");
        }

        var device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(statx[StatxDeviceMajorOffset..]) << 32)
            | BinaryPrimitives.ReadUInt32LittleEndian(statx[StatxDeviceMinorOffset..]);
        return (device, BinaryPrimitives.ReadUInt64LittleEndian(statx[StatxInodeOffset..]));
    }

    private static (ulong Device, ulong Inode) MacOsIdentity(ReadOnlySpan<byte> stat) =>
        ((uint)BinaryPrimitives.ReadInt32LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat[MacOsInodeOffset..]));

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fstat(int descriptor, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int FstatInode64(int descriptor, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Stat(ReadOnlySpan<byte> path, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int StatInode64(ReadOnlySpan<byte> path, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Flock(int descriptor, int operation);

    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fstatfs(int descriptor, Span<byte> buffer);
}
