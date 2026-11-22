using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Orleans.Durable.Storage.Engine.Wal.FileSystem;

/// <summary>
/// Makes a directory's entries durable with the current platform's directory flush.
/// </summary>
/// <remarks>
/// A failed open or flush throws <see cref="IOException"/> whose inner <see cref="Win32Exception"/> carries the operating
/// system's error. On Windows, the flush is NTFS's: "This is only implemented by the NTFS file system. Other file systems
/// return STATUS_SUCCESS and perform no other action" (MS-FSA §2.1.5.7, note 80), so a directory on any other file system,
/// or on a remote volume, throws <see cref="PlatformNotSupportedException"/> (wal.md §3).
/// </remarks>
internal static partial class WalDirectorySync
{
    // FILE_ADD_FILE (FILE_WRITE_DATA) and FILE_ADD_SUBDIRECTORY (FILE_APPEND_DATA): a flush fails with
    // STATUS_ACCESS_DENIED on a handle with "neither write or append access" (NtFlushBuffersFileEx).
    internal const uint WindowsAddFile = 0x2;
    internal const uint WindowsAddSubdirectory = 0x4;

    // FILE_FLAG_BACKUP_SEMANTICS: CreateFileW opens a directory only with this flag; it confers no privilege.
    private const uint WindowsBackupSemantics = 0x02000000;

    // FileFsDeviceInformation and its FILE_REMOTE_DEVICE characteristic (wdm.h).
    private const int FileFsDeviceInformation = 4;
    private const uint RemoteDevice = 0x10;

    // The file system name buffer GetVolumeInformationByHandleW takes at most: MAX_PATH + 1 characters.
    private const int FileSystemNameLength = 261;

    // Linux open flags differ by architecture: O_DIRECTORY is 0x10000 on x86-64 (arch/x86 uses
    // include/uapi/asm-generic/fcntl.h) and 0x4000 on arm64 (arch/arm64/include/uapi/asm/fcntl.h), where 0x10000 is
    // O_DIRECT. O_CLOEXEC is 0x80000 on both.
    private const int LinuxX64Directory = 0x10000;
    private const int LinuxArm64Directory = 0x4000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int MacOsDirectory = 0x100000;
    private const int MacOsCloseOnExec = 0x1000000;
    private const int MacOsFullFsync = 51;

    // macOS errno values (sys/errno.h).
    private const int MacOsInvalidArgument = 22;
    private const int MacOsNotTerminal = 25;
    private const int MacOsNotSupported = 45;

    /// <summary>
    /// Makes the entries of <paramref name="entries"/>'s kind in the directory at <paramref name="path"/> durable.
    /// </summary>
    public static void Flush(string path, DirectoryEntries entries)
    {
        if (OperatingSystem.IsWindows())
        {
            // The flush takes the handle, so no open happens without its flush.
            FlushWindows(OpenQualified(path, entries is DirectoryEntries.Files ? WindowsAddFile : WindowsAddSubdirectory), path);
            return;
        }

        int flags;
        if (OperatingSystem.IsLinux())
        {
            flags = LinuxOpenFlags(RuntimeInformation.ProcessArchitecture);
        }
        else if (OperatingSystem.IsMacOS())
        {
            flags = MacOsDirectory | MacOsCloseOnExec;
        }
        else
        {
            throw new PlatformNotSupportedException("Durable write-ahead log directories require a directory fsync primitive.");
        }

        var descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            throw Failure($"Opening the directory '{path}' for fsync failed.", Marshal.GetLastPInvokeError());
        }

        try
        {
            if ((OperatingSystem.IsMacOS() ? FullFsync(descriptor) : Fsync(descriptor)) != 0)
            {
                throw Failure($"fsync of the directory '{path}' failed.", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    /// <summary>
    /// Flushes a descriptor on macOS with <c>F_FULLFSYNC</c>, which asks the drive to write its cache, as macOS's
    /// <c>fsync</c> does not; where the file system lacks it (<c>ENOTSUP</c>, <c>ENOTTY</c>, or <c>EINVAL</c>), <c>fsync</c>
    /// is the flush it has, and any other failure is the flush's.
    /// </summary>
    private static int FullFsync(int descriptor)
    {
        if (Fcntl(descriptor, MacOsFullFsync) == 0)
        {
            return 0;
        }

        return Marshal.GetLastPInvokeError() is MacOsNotSupported or MacOsNotTerminal or MacOsInvalidArgument ? Fsync(descriptor) : -1;
    }

    /// <summary>
    /// Returns <c>O_DIRECTORY | O_CLOEXEC</c> for a Linux process of the given architecture.
    /// </summary>
    internal static int LinuxOpenFlags(Architecture architecture)
    {
        if (architecture == Architecture.X64)
        {
            return LinuxX64Directory | LinuxCloseOnExec;
        }

        if (architecture == Architecture.Arm64)
        {
            return LinuxArm64Directory | LinuxCloseOnExec;
        }

        throw new PlatformNotSupportedException($"Durable write-ahead log directories support Linux on x64 and arm64, not {architecture}.");
    }

    /// <summary>
    /// Throws <see cref="PlatformNotSupportedException"/> unless a directory's volume is local and its file system is
    /// NTFS, the only file system whose directory flush makes the directory's entries durable.
    /// </summary>
    internal static void ThrowIfUnqualified(string path, string fileSystem, bool remote)
    {
        if (remote)
        {
            throw RemoteVolume(path);
        }

        if (!string.Equals(fileSystem, "NTFS", StringComparison.Ordinal))
        {
            throw new PlatformNotSupportedException(
                $"The directory '{path}' lies on a {fileSystem} volume, whose directory flush makes nothing durable; WriteAheadLog's directories lie on local NTFS volumes.");
        }
    }

    /// <summary>
    /// Returns the extended-length (<c>\\?\</c>) form of a local path, which <c>CreateFileW</c> opens beyond
    /// <c>MAX_PATH</c> characters, as .NET's file APIs form it; a device path (<c>\\?\</c>, <c>\\.\</c>, or <c>\??\</c>)
    /// is returned unchanged.
    /// </summary>
    /// <remarks>
    /// The extended form is taken literally, so the path is made full first.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The path is a UNC path, which names a remote volume.</exception>
    internal static string WindowsExtendedPath(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.Length >= 4 && IsSeparator(path[0]) && IsSeparator(path[1]) && path[2] is '?' or '.' && IsSeparator(path[3]))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? throw RemoteVolume(path) : @"\\?\" + full;

        static bool IsSeparator(char value) => value is '\\' or '/';
    }

    /// <summary>
    /// Opens a directory, sharing it fully, with backup semantics and <paramref name="access"/> alone.
    /// </summary>
    internal static SafeFileHandle OpenWindows(string path, uint access)
    {
        var handle = CreateFile(
            WindowsExtendedPath(path),
            access,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: 0,
            FileMode.Open,
            WindowsBackupSemantics,
            template: 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure($"Opening the directory '{path}' for a flush failed.", error);
        }

        return handle;
    }

    /// <summary>
    /// Opens a directory as <see cref="OpenWindows"/> does, and throws <see cref="PlatformNotSupportedException"/>
    /// unless its volume is local NTFS.
    /// </summary>
    internal static SafeFileHandle OpenQualified(string path, uint access)
    {
        var handle = OpenWindows(path, access);
        try
        {
            ThrowIfUnqualified(path, FileSystemName(handle, path), IsRemote(handle, path));
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Flushes a handle <see cref="OpenWindows"/> opened, and closes it.
    /// </summary>
    internal static void FlushWindows(SafeFileHandle handle, string path)
    {
        using (handle)
        {
            if (!FlushFileBuffers(handle))
            {
                throw Failure($"Flushing the directory '{path}' failed.", Marshal.GetLastPInvokeError());
            }
        }
    }

    private static string FileSystemName(SafeFileHandle handle, string path)
    {
        Span<char> name = stackalloc char[FileSystemNameLength];
        if (GetVolumeInformationByHandle(handle, 0, 0, 0, 0, 0, name, FileSystemNameLength) == 0)
        {
            throw Failure($"Reading the file system of the directory '{path}' failed.", Marshal.GetLastPInvokeError());
        }

        return new string(name[..name.IndexOf('\0')]);
    }

    private static bool IsRemote(SafeFileHandle handle, string path)
    {
        // IO_STATUS_BLOCK is two pointer-sized words; FILE_FS_DEVICE_INFORMATION is DeviceType, then Characteristics.
        Span<nint> status = stackalloc nint[2];
        Span<uint> device = stackalloc uint[2];
        var result = NtQueryVolumeInformationFile(handle, status, device, 2 * sizeof(uint), FileFsDeviceInformation);
        if (result < 0)
        {
            throw Failure($"Reading the volume of the directory '{path}' failed.", (int)RtlNtStatusToDosError(result));
        }

        return (device[1] & RemoteDevice) != 0;
    }

    private static PlatformNotSupportedException RemoteVolume(string path) =>
        new($"The directory '{path}' lies on a remote volume; WriteAheadLog's directories lie on local NTFS volumes.");

    private static IOException Failure(string message, int error) => new(message, new Win32Exception(error));

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetVolumeInformationByHandle(
        SafeFileHandle handle,
        nint volumeName,
        uint volumeNameLength,
        nint serialNumber,
        nint maximumComponentLength,
        nint fileSystemFlags,
        Span<char> fileSystemName,
        uint fileSystemNameLength);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQueryVolumeInformationFile(SafeFileHandle handle, Span<nint> status, Span<uint> information, uint length, int informationClass);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fsync(int descriptor);

    // fcntl is variadic; F_FULLFSYNC takes no argument, so only the named parameters are passed.
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fcntl(int descriptor, int command);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Close(int descriptor);
}
