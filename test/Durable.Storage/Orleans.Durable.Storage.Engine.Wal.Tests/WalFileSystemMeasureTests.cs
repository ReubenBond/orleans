using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// The operating system's measurement of a directory (<see cref="WalFileSystem.MeasureDirectory"/>,
/// <see cref="WalFileSystem.GetVolumeSpace"/>; wal.md §9): every file beneath the directory counts, hidden ones too; a
/// reparse point is neither counted nor entered; and below the directory, an entry the process cannot read, or which a
/// concurrent deletion removes, is an answer, never a failure which would stop the process.
/// </summary>
[NoPageLeakCheck("The measurement rents no pages.")]
public sealed class WalFileSystemMeasureTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("clog-wal-measure-");
    private readonly DirectoryInfo _outside = Directory.CreateTempSubdirectory("clog-wal-outside-");

    public void Dispose()
    {
        _root.Delete(recursive: true);
        _outside.Delete(recursive: true);
    }

    /// <summary>
    /// The measurement counts the bytes of every file in the directory and in every directory below it, a hidden file
    /// among them, and reads a volume with free space no larger than its size.
    /// </summary>
    [Fact]
    public void EveryFileBeneathTheDirectoryCountsHiddenOnesToo()
    {
        Write(_root.FullName, "a", 3);
        var nested = Directory.CreateDirectory(Path.Combine(_root.FullName, "one", "two"));
        Write(nested.FullName, "b", 5);
        var hidden = Write(nested.FullName, ".hidden", 7);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        }

        Assert.Equal(15, WalFileSystem.Os.MeasureDirectory(_root.FullName));
        var volume = WalFileSystem.Os.GetVolumeSpace(_root.FullName);
        Assert.InRange(volume.FreeBytes, 1, volume.TotalBytes);
    }

    /// <summary>
    /// A symbolic link, or on Windows a junction, to a directory elsewhere, and a symbolic link to a file, are neither
    /// counted nor entered: a mounted volume, or a link out of the node root, is not the directory's.
    /// </summary>
    [Fact]
    public void AReparsePointIsNeitherCountedNorEntered()
    {
        Write(_root.FullName, "a", 3);
        Write(_outside.FullName, "elsewhere", 1_000);
        var link = Path.Combine(_root.FullName, "link");
        if (!TryLinkDirectory(link, _outside.FullName))
        {
            Assert.Skip("This process can create neither a directory link nor a junction.");
        }

        TryLinkFile(Path.Combine(_root.FullName, "file-link"), Path.Combine(_outside.FullName, "elsewhere"));

        Assert.Equal(3, WalFileSystem.Os.MeasureDirectory(_root.FullName));
    }

    /// <summary>
    /// A directory below the measured one which the process cannot list, as <c>System Volume Information</c> at an NTFS
    /// volume's root or <c>lost+found</c> at an ext4 volume's, is left out, and no failure is reported.
    /// </summary>
    [Fact]
    public void AnUnreadableDirectoryBelowTheDirectoryIsLeftOutUnreported()
    {
        Write(_root.FullName, "a", 3);
        var denied = Directory.CreateDirectory(Path.Combine(_root.FullName, "denied"));
        Write(denied.FullName, "b", 5);
        using var restore = Deny(denied);
        if (restore is null)
        {
            Assert.Skip("This process reads every directory, as root does.");
        }

        var since = FatalErrors.ReportedCount;
        Assert.Equal(3, WalFileSystem.Os.MeasureDirectory(_root.FullName));
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>
    /// Directories which a concurrent deletion removes during the measurement, as the LedgerStore deletes a dropped
    /// replica's directory, are answers: no measurement fails or reports, and each counts at least the bytes no deletion
    /// touches.
    /// </summary>
    [Fact]
    public async Task DirectoriesDeletedDuringTheMeasurementAreAnswers()
    {
        Write(_root.FullName, "kept", 11);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var churn = Task.Run(
            () =>
            {
                for (var round = 0; !stop.IsCancellationRequested; round++)
                {
                    try
                    {
                        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, $"replica-{round % 8}", "slots"));
                        Write(directory.FullName, "root", 13);
                        Directory.Delete(Path.Combine(_root.FullName, $"replica-{(round + 4) % 8}"), recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // The directory is missing, or the measurement holds it open: the churn goes on.
                    }
                }
            },
            stop.Token);

        var since = FatalErrors.ReportedCount;
        var watch = Stopwatch.StartNew();
        for (var measurement = 0; measurement < 500 || watch.Elapsed < TimeSpan.FromSeconds(1); measurement++)
        {
            Assert.InRange(WalFileSystem.Os.MeasureDirectory(_root.FullName), 11, long.MaxValue);
        }

        await stop.CancelAsync();
        await Task.WhenAny(churn);
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    private static string Write(string directory, string name, int length)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            // Without the privilege a symbolic link needs, a junction is a reparse point to a directory too.
            using var junction = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true })!;
            junction.WaitForExit();
            return junction.ExitCode == 0;
        }
    }

    private static void TryLinkFile(string link, string target)
    {
        try
        {
            _ = File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            // Windows grants file links only with the privilege; the directory link covers reparse points then.
        }
    }

    /// <summary>
    /// Denies the process the listing of <paramref name="directory"/>, returning what restores it, or
    /// <see langword="null"/> when the process lists it anyway.
    /// </summary>
    private static IDisposable? Deny(DirectoryInfo directory) => OperatingSystem.IsWindows() ? DenyOnWindows(directory) : DenyOnUnix(directory);

    [SupportedOSPlatform("windows")]
    private static Restore DenyOnWindows(DirectoryInfo directory)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        return new Restore(() => Allow(directory, rule));
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        var security = directory.GetAccessControl();
        _ = security.RemoveAccessRule(rule);
        directory.SetAccessControl(security);
    }

    [UnsupportedOSPlatform("windows")]
    private static Restore? DenyOnUnix(DirectoryInfo directory)
    {
        var mode = directory.UnixFileMode;
        directory.UnixFileMode = UnixFileMode.None;
        var restore = new Restore(() => directory.UnixFileMode = mode);
        try
        {
            _ = directory.EnumerateFileSystemInfos().Any();
        }
        catch (UnauthorizedAccessException)
        {
            return restore;
        }

        restore.Dispose();
        return null;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
