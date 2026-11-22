using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Flushes directories through the operating system's file system, <see cref="WalFileSystem.Os"/>, with each platform's
/// primitive: <c>fsync</c> of a descriptor opened with each architecture's <c>open</c> flags on Linux and macOS, and on
/// Windows <c>FlushFileBuffers</c> of a handle opened with backup semantics and the right to add the flushed kind of
/// entry, on local NTFS volumes only. A flush which cannot be made throws an <see cref="IOException"/> carrying the
/// operating system's error.
/// </summary>
public sealed partial class WalDirectorySyncTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int NotADirectory = 20;

    // ENOENT on Linux and macOS; ERROR_FILE_NOT_FOUND on Windows.
    private const int NotFound = 2;
    private const int AccessDenied = 5;
    private const int MaxPath = 260;
    private const uint Delete = 0x10000;

    [Fact]
    public void LinuxOpenFlagsUseEachArchitecturesDirectoryValue()
    {
        Assert.Equal(0x10000 | 0x80000, WalDirectorySync.LinuxOpenFlags(Architecture.X64));

        // Regression for #61: on arm64, O_DIRECTORY is 0x4000 and 0x10000 is O_DIRECT.
        Assert.Equal(0x4000 | 0x80000, WalDirectorySync.LinuxOpenFlags(Architecture.Arm64));
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void LinuxOpenFlagsRejectUnsupportedArchitectures(Architecture architecture) =>
        Assert.Throws<PlatformNotSupportedException>(() => WalDirectorySync.LinuxOpenFlags(architecture));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlushSyncsADirectory(bool directories) => WithDirectory(path =>
        Assert.Null(Record.Exception(() => WalFileSystem.Os.FlushDirectory(path, directories ? DirectoryEntries.Directories : DirectoryEntries.Files))));

    /// <summary>
    /// A flush which opens nothing fails rather than skipping the directory.
    /// </summary>
    [Fact]
    public void FlushOfAMissingDirectoryFails() => WithDirectory(path =>
        AssertFails(() => WalFileSystem.Os.FlushDirectory(Path.Combine(path, "missing"), DirectoryEntries.Files), NotFound, "Opening the directory", Path.Combine(path, "missing")));

    /// <summary>
    /// The operating system's file system creates one directory level: a directory whose parent is missing is refused,
    /// and no ancestor is created, since only the parent's flush makes the name durable (wal.md §3).
    /// </summary>
    [Fact]
    public void CreatingADirectoryWhoseParentIsMissingCreatesNothing() => WithDirectory(path =>
    {
        _ = Assert.Throws<DirectoryNotFoundException>(() => WalFileSystem.Os.CreateDirectory(Path.Combine(path, "missing", "child")));
        Assert.False(Directory.Exists(Path.Combine(path, "missing")));
        WalFileSystem.Os.CreateDirectory(Path.Combine(path, "child"));
        Assert.True(Directory.Exists(Path.Combine(path, "child")));
    });

    /// <summary>
    /// A directory whose path is longer than <c>MAX_PATH</c> flushes: on Windows, by its extended path, since this
    /// process does not opt in to long paths.
    /// </summary>
    [Fact]
    public void FlushSyncsADirectoryWhosePathIsLongerThanMaxPath()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() && RtlAreLongPathsEnabled(),
            "The process opts in to long paths, so a flush by a plain path would pass too and the extended path would go untested.");
        WithDirectory(root =>
        {
            var path = root;
            while (path.Length <= MaxPath)
            {
                path = Path.Combine(path, new string('d', 40));
            }

            _ = Directory.CreateDirectory(path);
            Assert.Null(Record.Exception(() => WalFileSystem.Os.FlushDirectory(path, DirectoryEntries.Files)));
        });
    }

    [Fact]
    public void FlushRejectsARegularFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows opens a file with backup semantics as it opens a directory.");
        WithDirectory(directory =>
        {
            var file = Path.Combine(directory, "file");
            File.WriteAllBytes(file, [1]);

            // O_DIRECTORY makes the open fail with ENOTDIR; the x86-64 value on arm64 (O_DIRECT) would open the file.
            AssertFails(() => WalFileSystem.Os.FlushDirectory(file, DirectoryEntries.Files), NotADirectory, "Opening the directory", file);
        });
    }

    /// <summary>
    /// Only a local NTFS volume qualifies on Windows: every other file system's directory flush makes nothing durable
    /// (MS-FSA §2.1.5.7, note 80), and a remote volume reports the server's file system.
    /// </summary>
    [Theory]
    [InlineData("NTFS", false, true)]
    [InlineData("ReFS", false, false)]
    [InlineData("FAT32", false, false)]
    [InlineData("FAT", false, false)]
    [InlineData("exFAT", false, false)]
    [InlineData("NTFS", true, false)]
    public void OnlyALocalNtfsVolumeQualifies(string fileSystem, bool remote, bool qualifies)
    {
        var exception = Record.Exception(() => WalDirectorySync.ThrowIfUnqualified("directory", fileSystem, remote));
        if (qualifies)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.IsType<PlatformNotSupportedException>(exception);
        }
    }

    /// <summary>
    /// The Windows tests flush on a local NTFS volume, so every one which flushes successfully qualifies the volume check
    /// directly.
    /// </summary>
    [Fact]
    public void OnWindowsTheTestsFlushOnALocalNtfsVolume()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows volumes.");
        var volume = new DriveInfo(Path.GetTempPath());
        Assert.Equal(("NTFS", DriveType.Fixed), (volume.DriveFormat, volume.DriveType));
    }

    /// <summary>
    /// On Windows, a handle on any file system but NTFS is refused before its flush: a named pipe's is NPFS's.
    /// </summary>
    [Fact]
    public void OnWindowsAFlushOnAnotherFileSystemIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named pipes.");
        var name = $"clog-wal-dirsync-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name);
        var exception = Assert.Throws<PlatformNotSupportedException>(() => WalFileSystem.Os.FlushDirectory($@"\\.\pipe\{name}", DirectoryEntries.Directories));
        Assert.Contains("NPFS", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// On Windows, a directory on a remote volume is refused: by its device path, the volume reports the server's NTFS
    /// and the remote device flag, and by its UNC path, before it is opened.
    /// </summary>
    [Fact]
    public void OnWindowsAFlushOnARemoteVolumeIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows shares.");
        WithDirectory(path =>
        {
            var share = @"\\localhost\" + path[0] + "$" + path[2..];
            Assert.SkipUnless(Directory.Exists(share), "The administrative share of the temporary directory's drive is unreachable.");
            _ = Assert.Throws<PlatformNotSupportedException>(() => WalFileSystem.Os.FlushDirectory(@"\\?\UNC\" + share[2..], DirectoryEntries.Files));
            _ = Assert.Throws<PlatformNotSupportedException>(() => WalFileSystem.Os.FlushDirectory(share, DirectoryEntries.Files));
        });
    }

    /// <summary>
    /// On Windows, a flush opens the directory with the right to add the flushed kind of entry; a directory which denies
    /// it fails the flush with <c>ERROR_ACCESS_DENIED</c>.
    /// </summary>
    [Theory]
    [InlineData(FileSystemRights.CreateFiles)]
    [InlineData(FileSystemRights.CreateDirectories)]
    [SupportedOSPlatform("windows")]
    public void OnWindowsAFlushNeedsTheRightToAddItsKindOfEntry(FileSystemRights right) =>
        WithAccess(path => AssertFails(() => WalFileSystem.Os.FlushDirectory(path, Entries(right)), AccessDenied, "Opening the directory", path), right, AccessControlType.Deny);

    /// <summary>
    /// On Windows, the right to add the flushed kind of entry is the only one a flush needs: a directory whose protected
    /// access list grants nothing else, beyond the synchronization every handle takes, flushes.
    /// </summary>
    [Theory]
    [InlineData(FileSystemRights.CreateFiles)]
    [InlineData(FileSystemRights.CreateDirectories)]
    [SupportedOSPlatform("windows")]
    public void OnWindowsAFlushNeedsNoOtherRight(FileSystemRights right) =>
        WithAccess(path => Assert.Null(Record.Exception(() => WalFileSystem.Os.FlushDirectory(path, Entries(right)))), right | FileSystemRights.Synchronize, AccessControlType.Allow);

    /// <summary>
    /// On Windows, a flush shares the directory fully: it succeeds while another handle holds the directory with delete
    /// access, and while other flushes of it run.
    /// </summary>
    [Fact]
    public void OnWindowsAFlushSharesTheDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing modes.");
        WithDirectory(path =>
        {
            using (WalDirectorySync.OpenWindows(path, Delete))
            {
                Assert.Null(Record.Exception(() => WalFileSystem.Os.FlushDirectory(path, DirectoryEntries.Files)));
            }

            Assert.Null(Record.Exception(() => Parallel.For(0, 64, _ => WalFileSystem.Os.FlushDirectory(path, DirectoryEntries.Files))));
        });
    }

    /// <summary>
    /// On Windows, a flush which the opened handle refuses fails with its error. A named pipe's handle opens as a
    /// directory's does and refuses the flush: <c>NtFlushBuffersFile</c> counts <c>FILE_APPEND_DATA</c>, which a pipe
    /// reads as <c>FILE_CREATE_PIPE_INSTANCE</c>, only for other files.
    /// </summary>
    [Fact]
    public void OnWindowsAFlushTheHandleRefusesFails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named pipes.");
        var name = $"clog-wal-dirsync-{Guid.NewGuid():N}";
        var path = $@"\\.\pipe\{name}";
        using var server = new NamedPipeServerStream(name);
        AssertFails(() => WalDirectorySync.FlushWindows(WalDirectorySync.OpenWindows(path, WalDirectorySync.WindowsAddSubdirectory), path), AccessDenied, "Flushing the directory");
    }

    /// <summary>
    /// On Windows, creating a directory which exists flushes its parent with the right to add a subdirectory, and a
    /// parent which denies it fails the creation with the flush's <see cref="IOException"/>.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindowsCreatingADirectoryWhoseParentDeniesItsFlushFails() => WithDirectory(parent =>
    {
        var path = Path.Combine(parent, "wal");
        _ = Directory.CreateDirectory(path);
        WithAccess(
            parent,
            () => AssertFails(() => new PortableWalStorage().CreateDirectoryAsync(path, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult(), AccessDenied, "Opening the directory", parent),
            FileSystemRights.CreateDirectories,
            AccessControlType.Deny);
    });

    /// <summary>
    /// On Windows, opening a directory flushes it with the right to add a file, and a directory which denies it fails the
    /// open with the flush's <see cref="IOException"/>.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void OnWindowsOpeningADirectoryWhichDeniesItsFlushFails() => WithDirectory(path =>
    {
        File.WriteAllBytes(Path.Combine(path, "lock"), []);
        WithAccess(
            path,
            () => AssertFails(() => new PortableWalStorage().OpenDirectoryAsync(path, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult(), AccessDenied, "Opening the directory", path),
            FileSystemRights.CreateFiles,
            AccessControlType.Deny);
    });

    /// <summary>
    /// On Windows, the flush opens a full, normalized local path in its extended form, keeps a device path's form, and
    /// refuses a UNC path, which names a remote volume.
    /// </summary>
    [Theory]
    [InlineData(@"C:/clog/../data/wal", @"\\?\C:\data\wal")]
    [InlineData(@"\\?\C:\data\wal", @"\\?\C:\data\wal")]
    [InlineData(@"\\.\C:\data\wal", @"\\.\C:\data\wal")]
    [InlineData(@"\??\C:\data\wal", @"\??\C:\data\wal")]
    [InlineData(@"\\server\share\wal", null)]
    public void OnWindowsTheFlushOpensTheExtendedFullPath(string path, string? expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");
        if (expected is null)
        {
            _ = Assert.Throws<PlatformNotSupportedException>(() => WalDirectorySync.WindowsExtendedPath(path));
        }
        else
        {
            Assert.Equal(expected, WalDirectorySync.WindowsExtendedPath(path));
        }
    }

    /// <summary>
    /// Returns the kind of entry whose creation <paramref name="right"/> grants.
    /// </summary>
    private static DirectoryEntries Entries(FileSystemRights right) =>
        right == FileSystemRights.CreateDirectories ? DirectoryEntries.Directories : DirectoryEntries.Files;

    /// <summary>
    /// Asserts that <paramref name="flush"/> fails with the operating system's <paramref name="error"/>, and that a flush
    /// through the file-system seam reported the failure once, as a failed flush of <paramref name="reportedPath"/>
    /// (conventions.md §14), while a flush beneath it reported nothing.
    /// </summary>
    private static void AssertFails(Action flush, int error, string message, string? reportedPath = null)
    {
        var since = FatalErrors.ReportedCount;
        var exception = Assert.Throws<IOException>(flush);
        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
        Assert.Equal(error, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
        var reported = FatalErrors.TakeMine(since);
        if (reportedPath is null)
        {
            Assert.Empty(reported);
        }
        else
        {
            var report = Assert.Single(reported);
            Assert.Equal((FatalErrorSite.FlushDirectory, reportedPath), (report.Site, report.Path));
            Assert.Same(exception, report.Failure.Exception);
        }
    }

    private static void WithDirectory(Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("clog-wal-dirsync-");
        try
        {
            test(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs <paramref name="test"/> on a new directory whose access list holds one rule for the current user: a denial of
    /// <paramref name="rights"/> beside the inherited rules, or, protected from inheritance, a grant of them alone.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WithAccess(Action<string> test, FileSystemRights rights, AccessControlType type)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows access rights.");
        WithDirectory(path => WithAccess(path, () => test(path), rights, type));
    }

    [SupportedOSPlatform("windows")]
    private static void WithAccess(string path, Action test, FileSystemRights rights, AccessControlType type)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows access rights.");
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, type);
        security.SetAccessRuleProtection(isProtected: type == AccessControlType.Allow, preserveInheritance: false);
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        try
        {
            test();
        }
        finally
        {
            // The owner may always rewrite the access list; unprotecting it inherits the parent's rules again.
            _ = security.RemoveAccessRule(rule);
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            directory.SetAccessControl(security);
        }
    }

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool RtlAreLongPathsEnabled();
}
