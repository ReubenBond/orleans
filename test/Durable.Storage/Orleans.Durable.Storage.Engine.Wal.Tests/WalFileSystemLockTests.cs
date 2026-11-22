using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// The operating system's lock file, <see cref="WalFileSystem.OpenLock"/>, excludes every other owner on the host: a
/// second handle in the same process, and another process, whichever of the two has .NET's own file locking disabled,
/// since on Linux and macOS the seam takes <c>flock(2)</c> itself (WAL-I11; wal.md §3). A directory whose lock another
/// owner holds is not deleted.
/// </summary>
public sealed class WalFileSystemLockTests : IDisposable
{
    private const string LockHostTry = "CLOG_LOCK_HOST_TRY";
    private const string LockHostHold = "CLOG_LOCK_HOST_HOLD";
    private const string Holding = "clog-lock-host=holding";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clog-wal-lock-" + Guid.NewGuid().ToString("N"));

    public WalFileSystemLockTests() => _ = Directory.CreateDirectory(_directory);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        FileHandleLeakCheck.AssertClosed(_directory);
        Directory.Delete(_directory, recursive: true);
        _leaks.Dispose();
    }

    [Fact]
    public void ASecondHandleCannotTakeAHeldLock()
    {
        var path = Path.Combine(_directory, "lock");
        var held = WalFileSystem.Os.OpenLock(path);
        _ = Assert.Throws<WalLockHeldException>(() => WalFileSystem.Os.OpenLock(path));
        held.Dispose();
        WalFileSystem.Os.OpenLock(path).Dispose();
    }

    /// <summary>
    /// A process whose .NET file locking is disabled, so that its runtime takes no advisory lock of its own, still finds
    /// the lock this process holds, and takes it once this process releases it.
    /// </summary>
    [Fact]
    public async Task AnotherProcessCannotTakeAHeldLockEvenWithDotNetFileLockingDisabled()
    {
        var path = Path.Combine(_directory, "lock");
        using (WalFileSystem.Os.OpenLock(path))
        {
            Assert.Equal("held", await TryLockInAnotherProcessAsync(path));
        }

        Assert.Equal("taken", await TryLockInAnotherProcessAsync(path));
    }

    /// <summary>
    /// The lock a process takes with .NET's file locking disabled, which only the seam's own <c>flock</c> holds,
    /// excludes this process until that process releases it.
    /// </summary>
    [Fact]
    public async Task ALockHeldWithDotNetFileLockingDisabledExcludesAnotherProcess()
    {
        var path = Path.Combine(_directory, "lock");
        var release = path + ".release";
        using var process = StartLockHost(LockHostHold, path);
        var error = process.StandardError.ReadToEndAsync(Token);
        var output = new StringBuilder();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(Token)) is not null && line != Holding)
        {
            _ = output.AppendLine(line);
        }

        if (line is null)
        {
            await process.WaitForExitAsync(Token);
            var diagnostics = await error;
            Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"The lock host exited with code {process.ExitCode} without holding the lock.\n{output}\n{diagnostics}"));
        }

        _ = Assert.Throws<WalLockHeldException>(() => WalFileSystem.Os.OpenLock(path));
        await File.WriteAllTextAsync(release, string.Empty, Token);
        _ = await process.StandardOutput.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        var errors = await error;
        Assert.True(process.ExitCode == 0, string.Create(CultureInfo.InvariantCulture, $"The lock host exited with code {process.ExitCode}.\n{errors}"));
        WalFileSystem.Os.OpenLock(path).Dispose();
        File.Delete(release);
    }

    /// <summary>
    /// Only a held lock answers <see cref="WalLockHeldException"/>; a file system which takes no <c>flock</c> is not one
    /// Clog qualifies, and every other error fails the lock.
    /// </summary>
    [Theory]
    [InlineData(WalFlock.LinuxWouldBlock, false, typeof(WalLockHeldException))]
    [InlineData(WalFlock.MacOsWouldBlock, true, typeof(WalLockHeldException))]
    [InlineData(WalFlock.MacOsWouldBlock, false, typeof(IOException))]
    [InlineData(WalFlock.LinuxWouldBlock, true, typeof(IOException))]
    [InlineData(WalFlock.LinuxNoLocks, false, typeof(IOException))]
    [InlineData(WalFlock.MacOsNoLocks, true, typeof(IOException))]
    [InlineData(WalFlock.Interrupted, false, typeof(IOException))]
    [InlineData(WalFlock.LinuxNotSupported, false, typeof(PlatformNotSupportedException))]
    [InlineData(WalFlock.LinuxNoSystemCall, false, typeof(PlatformNotSupportedException))]
    [InlineData(WalFlock.MacOsNotSupported, true, typeof(PlatformNotSupportedException))]
    [InlineData(WalFlock.MacOsOperationNotSupported, true, typeof(PlatformNotSupportedException))]
    [InlineData(WalFlock.MacOsNoSystemCall, true, typeof(PlatformNotSupportedException))]
    public void AFailedExclusiveLockAnswersHeldOnlyWhenItWouldBlock(int error, bool macOs, Type expected) =>
        Assert.IsType(expected, WalFlock.ExclusiveFailure(error, "lock", macOs), exactMatch: true);

    /// <summary>
    /// A lock which <c>flock(2)</c> refuses with any error but a held lock or an unqualified volume is a local storage
    /// failure, which the open's seam reports once, as <see cref="FatalErrorSite.OpenLock"/>, after which the refused
    /// handle is closed; a held lock and a volume which takes no <c>flock</c> are answers, which it does
    /// not report (conventions.md §14).
    /// </summary>
    /// <param name="refusal">How <c>flock</c> refuses the lock.</param>
    [Theory]
    [InlineData("failure")]
    [InlineData("held")]
    [InlineData("unsupported")]
    public void OnUnixOnlyAFailedFlockIsReportedAsTheOpensFailure(string refusal)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The seam takes flock(2) on Linux and macOS.");
        var macOs = OperatingSystem.IsMacOS();
        var error = refusal switch
        {
            "failure" => macOs ? WalFlock.MacOsNoLocks : WalFlock.LinuxNoLocks,
            "held" => WalFlock.WouldBlock,
            _ => macOs ? WalFlock.MacOsNotSupported : WalFlock.LinuxNotSupported,
        };
        var fileSystem = WalFileSystem.CreateOs(TimeProvider.System, _ => error);
        var path = Path.Combine(_directory, "lock");
        var since = FatalErrors.ReportedCount;

        var failure = Record.Exception(() => fileSystem.OpenLock(path));

        var reported = FatalErrors.TakeMine(since);
        if (refusal == "failure")
        {
            var report = Assert.Single(reported);
            Assert.Equal((FatalErrorSite.OpenLock, path), (report.Site, report.Path));
            Assert.Same(failure, report.Failure.Exception);
        }
        else
        {
            Assert.Empty(reported);
            Assert.IsType(refusal == "held" ? typeof(WalLockHeldException) : typeof(PlatformNotSupportedException), failure, exactMatch: true);
        }

        // The refused handle is closed, so the lock is free.
        WalFileSystem.Os.OpenLock(path).Dispose();
    }

    /// <summary>
    /// On Windows a handle which shares reads but not deletion, as a scanner's does, is no owner: the deletion waits it
    /// out as contention and then deletes the directory.
    /// </summary>
    [Fact]
    public async Task OnWindowsADeletionWaitsOutAHandleWhichIsNoOwner()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Contention is Windows'.");
        var directory = Path.Combine(_directory, "wal");
        _ = Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, WalFormat.LockFileName);
        await File.WriteAllBytesAsync(lockPath, [], Token);
        await using var scanned = File.Open(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Token);
                await scanned.DisposeAsync();
            },
            Token);

        WalFileSystem.Os.DeleteDirectory(directory);

        await release;
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// A deletion which another deletion of the same directory completes first, between this one's last steps, finds the
    /// directory gone: the deletion's answer, which nothing reports.
    /// </summary>
    [Fact]
    public void ADirectoryWhichAnotherDeletionCompletesFirstIsDeleted()
    {
        var directory = Path.Combine(_directory, "deleted");
        FileWalDirectory.Create(directory, WalFileSystem.Os, Token);
        File.WriteAllText(Path.Combine(directory, "file"), "file");
        var fileSystem = new CompletingDeletionFileSystem(Token);
        var since = FatalErrors.ReportedCount;

        FileWalDirectory.Delete(directory, fileSystem, Token);

        Assert.True(fileSystem.Completed);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>
    /// A directory whose lock another owner holds is not deleted; the held lock is the deletion's answer, which on
    /// Windows, where an owner's handle and a scanner's look alike, it gives once the handle outlasts the contention
    /// bound. Once the lock is released, the deletion removes the directory.
    /// </summary>
    [Fact]
    public async Task ADirectoryWhoseLockAnotherOwnerHoldsIsNotDeleted()
    {
        var directory = Path.Combine(_directory, "wal");
        _ = Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "file");
        await File.WriteAllTextAsync(file, "content", Token);
        var time = new FakeTimeProvider();
        var fileSystem = WalFileSystem.CreateOs(time);
        using (WalFileSystem.Os.OpenLock(Path.Combine(directory, WalFormat.LockFileName)))
        {
            var deletion = Task.Run(() => FileWalDirectory.Delete(directory, fileSystem, Token), Token);
            var waited = await AdvanceUntilCompletedAsync(time, deletion);
            _ = await Assert.ThrowsAsync<WalLockHeldException>(() => deletion);
            AssertWaitedOutOnWindows(waited);
            Assert.True(File.Exists(file));
        }

        FileWalDirectory.Delete(directory, WalFileSystem.Os, Token);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// The step of a deletion just before which another owner tries to take the directory.
    /// </summary>
    public enum DeletionStep
    {
        /// <summary>
        /// The lock file's deletion, after every other entry is gone.
        /// </summary>
        LockFile,

        /// <summary>
        /// The empty directory's deletion, after the lock file is gone.
        /// </summary>
        Directory,
    }

    /// <summary>
    /// An owner which arrives while a directory is being deleted, against the callers' rule (wal.md §3), can lock only
    /// the emptied directory, and keeps it: the deletion removes every other entry under its lock, then the lock file and
    /// the directory, and its last step meets the owner's lock or its new lock file and answers
    /// <see cref="WalLockHeldException"/>. On Linux and macOS the deletion unlinks the lock file still holding its lock, so
    /// an owner arriving before that is refused and the deletion completes.
    /// </summary>
    [Theory]
    [InlineData(DeletionStep.LockFile)]
    [InlineData(DeletionStep.Directory)]
    public async Task AnOwnerWhichArrivesDuringADeletionKeepsTheEmptiedDirectory(DeletionStep step)
    {
        var directory = Path.Combine(_directory, "wal");
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "file"), "content", Token);
        var lockPath = Path.Combine(directory, WalFormat.LockFileName);
        var time = new FakeTimeProvider();
        var arriving = new ArrivingOwnerFileSystem(directory, step, WalFileSystem.CreateOs(time));
        var deletion = Task.Run(() => FileWalDirectory.Delete(directory, arriving, Token), Token);
        var waited = await AdvanceUntilCompletedAsync(time, deletion);
        if (!OperatingSystem.IsWindows() && step == DeletionStep.LockFile)
        {
            await deletion;
            _ = Assert.IsType<WalLockHeldException>(arriving.Refusal);
            Assert.Null(arriving.Lock);
            Assert.False(Directory.Exists(directory));
            return;
        }

        // On Windows the owner's lock refuses the lock file's deletion, as a scanner's handle would, until the bound.
        _ = await Assert.ThrowsAsync<WalLockHeldException>(() => deletion);
        if (step == DeletionStep.LockFile)
        {
            AssertWaitedOutOnWindows(waited);
        }

        Assert.Null(arriving.Refusal);
        var owner = Assert.IsAssignableFrom<IDisposable>(arriving.Lock);
        Assert.Equal([WalFormat.LockFileName, ArrivingOwnerFileSystem.OwnerFile], Directory.EnumerateFiles(directory).Select(static file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(directory));
        _ = Assert.Throws<WalLockHeldException>(() => WalFileSystem.Os.OpenLock(lockPath));
        owner.Dispose();
        FileWalDirectory.Delete(directory, WalFileSystem.Os, Token);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// On Windows a scanner's handle on a directory's lock file, which shares reads but not deletion, is no owner: the
    /// deletion waits it out as contention, whether it meets it at the lock's open or at the lock file's deletion, and
    /// then deletes the directory, reporting nothing.
    /// </summary>
    /// <param name="atLockFileDeletion">Whether the scanner opens the lock file just before its deletion rather than
    /// just before the lock's open.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnWindowsADeletionWaitsOutAScannerOnItsLockFile(bool atLockFileDeletion)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Contention is Windows'.");
        var directory = Path.Combine(_directory, "wal");
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "file"), "content", Token);
        var scanning = new ScanningFileSystem(Path.Combine(directory, WalFormat.LockFileName), atLockFileDeletion);
        var since = FatalErrors.ReportedCount;

        FileWalDirectory.Delete(directory, scanning, Token);

        await scanning.Closed;
        Assert.False(Directory.Exists(directory));
        Assert.Empty(FatalErrors.TakeMine(since));
    }

    /// <summary>
    /// A lock taken on a lock file which a deletion unlinked is not ownership, since another owner may hold a new lock
    /// file at the same path: <see cref="WalFlock.LockIfNamed"/> answers whether the path still names the locked file,
    /// and the seam opens the path again when it does not (WAL-I11).
    /// </summary>
    [Fact]
    public void ALockOnAnUnlinkedLockFileIsNotOwnership()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Windows refuses to delete a file which a handle holds without sharing deletion.");
        var path = Path.Combine(_directory, WalFormat.LockFileName);
        using var stale = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        File.Delete(path);
        using (WalFileSystem.Os.OpenLock(path))
        {
            Assert.False(WalFlock.LockIfNamed(stale, path));
        }

        File.Delete(path);
        Assert.False(WalFlock.LockIfNamed(stale, path));
        using var current = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(WalFlock.LockIfNamed(current, path));
    }

    /// <summary>
    /// A directory which a concurrent deletion removes after the check that it exists is already gone: a second deletion
    /// completes, and an open reports it missing, each once the parent's flush makes its absence durable (WAL-I12), and
    /// neither reports a failure. A lock's open in a missing directory is otherwise a failure, which the seam reports
    /// (conventions.md §14).
    /// </summary>
    [Fact]
    public void ADirectoryRemovedDuringADeletionOrAnOpenIsReportedOnceItsAbsenceIsDurable()
    {
        var directory = Path.Combine(_directory, "wal");
        var removed = new RemovedDirectoryFileSystem(directory);
        var since = FatalErrors.ReportedCount;
        FileWalDirectory.Delete(directory, removed, Token);
        Assert.Equal([_directory], removed.Flushed);
        removed.Flushed.Clear();
        _ = Assert.Throws<DirectoryNotFoundException>(() => FileWalDirectory.Open(directory, removed, static (_, _) => throw new InvalidOperationException("No file is opened."), Token));
        Assert.Equal([_directory], removed.Flushed);
        Assert.Empty(FatalErrors.TakeMine(since));

        var lockPath = Path.Combine(directory, WalFormat.LockFileName);
        _ = Assert.Throws<DirectoryNotFoundException>(() => WalFileSystem.Os.OpenLock(lockPath));
        var reported = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((FatalErrorSite.OpenLock, lockPath), (reported.Site, reported.Path));
    }

    /// <summary>
    /// The deletion's last steps answer as values: a directory holding an entry is not deleted, and on Windows a lock file
    /// another owner holds is not deleted, once its handle outlasts the contention bound, while Linux and macOS unlink it.
    /// </summary>
    [Fact]
    public async Task TheLastStepsOfADeletionAnswerNotEmptyAndHeld()
    {
        var directory = Path.Combine(_directory, "wal");
        _ = Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, WalFormat.LockFileName);
        await File.WriteAllTextAsync(Path.Combine(directory, "file"), "content", Token);
        Assert.False(WalFileSystem.Os.TryDeleteEmptyDirectory(directory));
        var time = new FakeTimeProvider();
        var fileSystem = WalFileSystem.CreateOs(time);
        using (WalFileSystem.Os.OpenLock(lockPath))
        {
            var deletion = Task.Run(() => fileSystem.TryDeleteLockFile(lockPath), Token);
            var waited = await AdvanceUntilCompletedAsync(time, deletion);
            Assert.Equal(!OperatingSystem.IsWindows(), await deletion);
            AssertWaitedOutOnWindows(waited);
        }

        Assert.True(WalFileSystem.Os.TryDeleteLockFile(lockPath));
        File.Delete(Path.Combine(directory, "file"));
        Assert.True(WalFileSystem.Os.TryDeleteEmptyDirectory(directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task LockHost_TriesTheLock()
    {
        var path = Environment.GetEnvironmentVariable(LockHostTry);
        Assert.SkipWhen(path is null, "Runs as a child of the cross-process lock tests.");
        await File.WriteAllTextAsync(path + ".result", TryLock(path), Token);
    }

    [Fact]
    public async Task LockHost_HoldsTheLock()
    {
        var path = Environment.GetEnvironmentVariable(LockHostHold);
        Assert.SkipWhen(path is null, "Runs as a child of the cross-process lock tests.");
        using (WalFileSystem.Os.OpenLock(path))
        {
            await using var report = Console.OpenStandardOutput();
            await report.WriteAsync(Encoding.ASCII.GetBytes($"\n{Holding}\n"), Token);
            await report.FlushAsync(Token);
            while (!File.Exists(path + ".release"))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Token);
            }
        }
    }

    /// <summary>
    /// Tries the lock at <paramref name="path"/> in a lock host, whose .NET file locking is disabled.
    /// </summary>
    private static string TryLock(string path)
    {
        // With .NET's own lock off, a plain exclusive open succeeds whoever holds the lock, so only the seam's flock can
        // exclude this process. Windows has no such switch: there the lock is the handle's sharing mode.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
            }
            catch (IOException exception)
            {
                return ".NET's file locking is on: " + exception.Message;
            }
        }

        try
        {
            WalFileSystem.Os.OpenLock(path).Dispose();
            return "taken";
        }
        catch (WalLockHeldException)
        {
            // The refused lock closed its descriptor.
            return OperatingSystem.IsLinux() && OpenDescriptorsOf(path) != 0 ? "held, with its descriptor left open" : "held";
        }
    }

    /// <summary>
    /// Returns how many of this process's descriptors <c>/proc/self/fd</c> lists open on <paramref name="path"/>, on
    /// Linux.
    /// </summary>
    private static int OpenDescriptorsOf(string path)
    {
        var target = Path.GetFullPath(path);
        return Directory.EnumerateFileSystemEntries("/proc/self/fd")
            .Count(entry => string.Equals(new FileInfo(entry).LinkTarget, target, StringComparison.Ordinal));
    }

    private static async Task<string> TryLockInAnotherProcessAsync(string path)
    {
        var result = path + ".result";
        File.Delete(result);
        using var process = StartLockHost(LockHostTry, path);
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        var diagnostics = await output + "\n" + await error;
        Assert.True(
            process.ExitCode == 0 && File.Exists(result),
            string.Create(CultureInfo.InvariantCulture, $"The lock host exited with code {process.ExitCode}.\n{diagnostics}"));
        var answer = await File.ReadAllTextAsync(result, Token);
        File.Delete(result);
        return answer;
    }

    /// <summary>
    /// Starts this test assembly as a lock host which runs the host test <paramref name="variable"/> selects on
    /// <paramref name="path"/>, with .NET's file locking disabled, so .NET opens files without its best-effort
    /// <c>flock</c> and only the seam's own lock is taken there.
    /// </summary>
    private static Process StartLockHost(string variable, string path)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(WalFileSystemLockTests).Assembly.Location);
        }

        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add(variable == LockHostHold ? $"*{nameof(LockHost_HoldsTheLock)}" : $"*{nameof(LockHost_TriesTheLock)}");
        start.Environment[variable] = path;
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        return Process.Start(start) ?? throw new InvalidOperationException("The lock host did not start.");
    }

    /// <summary>
    /// Asserts that, on Windows, a refusal by another handle on a lock file was waited out for the contention bound before
    /// it answered that the lock is held; elsewhere a held lock answers at once.
    /// </summary>
    private static void AssertWaitedOutOnWindows(TimeSpan waited)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.InRange(waited, FileSystemAnswers.ContentionBound, FileSystemAnswers.ContentionBound * 2);
        }
    }

    /// <summary>
    /// The operating system's file system <paramref name="inner"/>, whose members a test double overrides.
    /// </summary>
    private class OsFileSystem(WalFileSystem inner) : WalFileSystem(inner.TimeProvider)
    {
        internal override bool DirectoryExistsCore(string path) => inner.DirectoryExistsCore(path);

        internal override void CreateDirectoryCore(string path) => inner.CreateDirectoryCore(path);

        internal override void DeleteDirectoryCore(string path) => inner.DeleteDirectoryCore(path);

        internal override bool TryDeleteEmptyDirectoryCore(string path) => inner.TryDeleteEmptyDirectoryCore(path);

        internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern) => inner.EnumerateFilesCore(path, searchPattern);

        internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) =>
            inner.EnumerateFilesWithLastWriteTimesCore(path, includeName);

        internal override bool TryDeleteFileWrittenBeforeCore(string path, DateTimeOffset cutoff) => inner.TryDeleteFileWrittenBeforeCore(path, cutoff);

        internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => inner.EnumerateDirectoriesCore(path);

        internal override bool FileExistsCore(string path) => inner.FileExistsCore(path);

        internal override IDisposable OpenLockCore(string path) => inner.OpenLockCore(path);

        internal override IDisposable OpenLockForDeletionCore(string path) => inner.OpenLockForDeletionCore(path);

        internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize) =>
            inner.CreateFileCore(path, replace, writeThrough, preallocationSize);

        internal override IWalFileSystemFile OpenFileCore(string path, bool direct) => inner.OpenFileCore(path, direct);

        internal override void MoveFileCore(string source, string destination, bool replace) => inner.MoveFileCore(source, destination, replace);

        internal override void LinkFileCore(string existing, string link) => inner.LinkFileCore(existing, link);

        internal override ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken) => inner.ReadFileCoreAsync(path, cancellationToken);

        internal override void DeleteFileCore(string path) => inner.DeleteFileCore(path);

        internal override bool TryDeleteLockFileCore(string path) => inner.TryDeleteLockFileCore(path);

        internal override void FlushDirectoryCore(string path, DirectoryEntries entries) => inner.FlushDirectoryCore(path, entries);
    }

    /// <summary>
    /// The operating system's file system, in which another owner tries to take the lock of <paramref name="directory"/>,
    /// and creates a file there once it holds it, just before a deletion's <paramref name="step"/>.
    /// </summary>
    private sealed class ArrivingOwnerFileSystem(string directory, DeletionStep step, WalFileSystem inner) : OsFileSystem(inner)
    {
        public const string OwnerFile = "owner";

        public IDisposable? Lock { get; private set; }

        public WalLockHeldException? Refusal { get; private set; }

        internal override bool TryDeleteEmptyDirectoryCore(string path)
        {
            Arrive(DeletionStep.Directory);
            return base.TryDeleteEmptyDirectoryCore(path);
        }

        internal override bool TryDeleteLockFileCore(string path)
        {
            Arrive(DeletionStep.LockFile);
            return base.TryDeleteLockFileCore(path);
        }

        private void Arrive(DeletionStep reached)
        {
            if (reached != step)
            {
                return;
            }

            try
            {
                Lock = Os.OpenLock(Path.Combine(directory, WalFormat.LockFileName));
            }
            catch (WalLockHeldException exception)
            {
                Refusal = exception;
                return;
            }

            File.WriteAllText(Path.Combine(directory, OwnerFile), "owner");
        }
    }

    /// <summary>
    /// The operating system's file system, in which <paramref name="directory"/>, which does not exist, existed when it
    /// was checked: a concurrent deletion removed it. It records the directories flushed.
    /// </summary>
    private sealed class RemovedDirectoryFileSystem(string directory) : OsFileSystem(Os)
    {
        public List<string> Flushed { get; } = [];

        internal override bool DirectoryExistsCore(string path) =>
            string.Equals(path, directory, StringComparison.Ordinal) || base.DirectoryExistsCore(path);

        internal override void FlushDirectoryCore(string path, DirectoryEntries entries)
        {
            Flushed.Add(path);
            base.FlushDirectoryCore(path, entries);
        }
    }

    /// <summary>
    /// The operating system's file system, in which another deletion of the same directory runs to completion just before
    /// a deletion's last step.
    /// </summary>
    private sealed class CompletingDeletionFileSystem(CancellationToken cancellationToken) : OsFileSystem(Os)
    {
        public bool Completed { get; private set; }

        internal override bool TryDeleteEmptyDirectoryCore(string path)
        {
            if (!Completed)
            {
                Completed = true;
                FileWalDirectory.Delete(path, Os, cancellationToken);
            }

            return base.TryDeleteEmptyDirectoryCore(path);
        }
    }

    /// <summary>
    /// The operating system's file system, in which a scanner opens the lock file at <paramref name="lockPath"/>, sharing
    /// reads but not deletion, as antivirus and indexing services do, just before the deletion's lock open or, with
    /// <paramref name="atLockFileDeletion"/>, the lock file's deletion, and closes it 100 ms later.
    /// </summary>
    private sealed class ScanningFileSystem(string lockPath, bool atLockFileDeletion) : OsFileSystem(Os)
    {
        public Task Closed { get; private set; } = Task.CompletedTask;

        internal override IDisposable OpenLockForDeletionCore(string path)
        {
            if (!atLockFileDeletion)
            {
                Scan();
            }

            return base.OpenLockForDeletionCore(path);
        }

        internal override bool TryDeleteLockFileCore(string path)
        {
            if (atLockFileDeletion)
            {
                Scan();
            }

            return base.TryDeleteLockFileCore(path);
        }

        /// <summary>
        /// Opens the scanner's handle and returns once it is open; the handle closes 100 ms later.
        /// </summary>
        private void Scan()
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Closed = Task.Run(async () =>
            {
                await using var scanner = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                opened.SetResult();
                await PauseAsync(100);
            });
            _ = Task.WaitAny(opened.Task, Closed);
            if (Closed.IsFaulted)
            {
                // The scanner's open failed; its exception fails the test.
                Closed.GetAwaiter().GetResult();
            }
        }
    }
}
