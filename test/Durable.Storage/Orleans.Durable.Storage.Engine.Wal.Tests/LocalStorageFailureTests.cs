using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks how the WAL's storage reports a failed local storage operation (conventions.md §14): the file-system seam reports
/// each failure once, with its own operation and path, and rethrows it; the file system's answers pass through unreported,
/// and a path is answered missing only within an existing directory; on Windows, another handle's contention is waited
/// out, up to its bound; and each of the simulated storage's fault hooks reports the failure it injects and stops the
/// storage until its crash.
/// </summary>
public sealed class LocalStorageFailureTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    // Each operation of the file-system seam and of a file it opened, invoked on the paths SeamOperations names.
    private static readonly Dictionary<FatalErrorSite, Func<WalFileSystem, Task>> Invocations = new()
    {
        [FatalErrorSite.DirectoryExists] = static fileSystem => Sync(() => _ = fileSystem.DirectoryExists("d")),
        [FatalErrorSite.CreateDirectory] = static fileSystem => Sync(() => fileSystem.CreateDirectory("d")),
        [FatalErrorSite.DeleteDirectory] = static fileSystem => Sync(() => fileSystem.DeleteDirectory("d")),
        [FatalErrorSite.ListFiles] = static fileSystem => Sync(() => _ = fileSystem.EnumerateFiles("d", "*")),
        [FatalErrorSite.ListDirectories] = static fileSystem => Sync(() => _ = fileSystem.EnumerateDirectories("d")),
        [FatalErrorSite.FileExists] = static fileSystem => Sync(() => _ = fileSystem.FileExists("f")),
        [FatalErrorSite.OpenLock] = static fileSystem => Sync(() => fileSystem.OpenLock("f").Dispose()),
        [FatalErrorSite.CreateFile] = static fileSystem =>
            Sync(() => fileSystem.CreateFile("f", replace: false, writeThrough: false, preallocationSize: 0).Dispose()),
        [FatalErrorSite.OpenFile] = static fileSystem => Sync(() => fileSystem.OpenFile("f", direct: false).Dispose()),
        [FatalErrorSite.MoveFile] = static fileSystem => Sync(() => fileSystem.MoveFile("f", "g", replace: true)),
        [FatalErrorSite.LinkFile] = static fileSystem => Sync(() => fileSystem.LinkFile("f", "g")),
        [FatalErrorSite.ReadFile] = static async fileSystem => _ = await fileSystem.ReadFileAsync("f", Token),
        [FatalErrorSite.DeleteFile] = static fileSystem => Sync(() => fileSystem.DeleteFile("f")),
        [FatalErrorSite.FlushDirectory] = static fileSystem => Sync(() => fileSystem.FlushDirectory("d", DirectoryEntries.Files)),
        [FatalErrorSite.DeleteEmptyDirectory] = static fileSystem => Sync(() => _ = fileSystem.TryDeleteEmptyDirectory("d")),
        [FatalErrorSite.DeleteLockFile] = static fileSystem => Sync(() => _ = fileSystem.TryDeleteLockFile("f")),
        [FatalErrorSite.HandleLength] = static fileSystem => OnFileAsync(fileSystem, static file => Sync(() => _ = file.Length)),
        [FatalErrorSite.HandleRead] = static fileSystem => OnFileAsync(fileSystem, static async file => _ = await file.ReadAsync(new byte[1], 0, Token)),
        [FatalErrorSite.HandleReadScatter] = static fileSystem =>
            OnFileAsync(fileSystem, static async file => _ = await file.ReadAsync([new byte[1]], 0, Token)),
        [FatalErrorSite.HandleWrite] = static fileSystem => OnFileAsync(fileSystem, static async file => await file.WriteAsync(new byte[1], 0, Token)),
        [FatalErrorSite.HandleWriteGather] = static fileSystem =>
            OnFileAsync(fileSystem, static async file => await file.WriteAsync([new byte[1]], 0, Token)),
        [FatalErrorSite.HandleSetLength] = static fileSystem => OnFileAsync(fileSystem, static file => Sync(() => file.SetLength(1))),
        [FatalErrorSite.HandleFlush] = static fileSystem => OnFileAsync(fileSystem, static file => Sync(file.Flush)),
        [FatalErrorSite.MeasureDirectory] = static fileSystem => Sync(() => _ = fileSystem.MeasureDirectory("d")),
        [FatalErrorSite.VolumeSpace] = static fileSystem => Sync(() => _ = fileSystem.GetVolumeSpace("d")),
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    public static TheoryData<FatalErrorSite, string> SeamOperations => new()
    {
        { FatalErrorSite.DirectoryExists, "d" },
        { FatalErrorSite.CreateDirectory, "d" },
        { FatalErrorSite.DeleteDirectory, "d" },
        { FatalErrorSite.ListFiles, "d" },
        { FatalErrorSite.ListDirectories, "d" },
        { FatalErrorSite.FileExists, "f" },
        { FatalErrorSite.OpenLock, "f" },
        { FatalErrorSite.CreateFile, "f" },
        { FatalErrorSite.OpenFile, "f" },
        { FatalErrorSite.MoveFile, "g" },
        { FatalErrorSite.LinkFile, "g" },
        { FatalErrorSite.ReadFile, "f" },
        { FatalErrorSite.DeleteFile, "f" },
        { FatalErrorSite.FlushDirectory, "d" },
        { FatalErrorSite.DeleteEmptyDirectory, "d" },
        { FatalErrorSite.DeleteLockFile, "f" },
        { FatalErrorSite.HandleLength, "f" },
        { FatalErrorSite.HandleRead, "f" },
        { FatalErrorSite.HandleReadScatter, "f" },
        { FatalErrorSite.HandleWrite, "f" },
        { FatalErrorSite.HandleWriteGather, "f" },
        { FatalErrorSite.HandleSetLength, "f" },
        { FatalErrorSite.HandleFlush, "f" },
        { FatalErrorSite.MeasureDirectory, "d" },
        { FatalErrorSite.VolumeSpace, "d" },
    };

    /// <summary>
    /// Gets the seam's operations which report under another operation's site, with the site and the path they name.
    /// </summary>
    public static TheoryData<string, FatalErrorSite, string> SharedSiteOperations => new()
    {
        { nameof(WalFileSystem.EnumerateFilesWithLengths), FatalErrorSite.ListFiles, "d" },
        { nameof(WalFileSystem.EnumerateFilesWithLastWriteTimes), FatalErrorSite.ListFiles, "d" },
        { nameof(WalFileSystem.TryDeleteFileWrittenBefore), FatalErrorSite.DeleteFile, "f" },
    };

    public static TheoryData<Type> Answers =>
    [
        typeof(FileNotFoundException),
        typeof(WalNameExistsException),
        typeof(WalLockHeldException),
        typeof(PlatformNotSupportedException),
        typeof(OperationCanceledException),
        typeof(ObjectDisposedException),
        typeof(ArgumentException),
        typeof(NotSupportedException),
        typeof(InvalidOperationException),
    ];

    public static TheoryData<string> Hooks =>
    [
        nameof(SimulatedWalStorage.WriteFault),
        nameof(SimulatedWalStorage.TornWriteFault),
        nameof(SimulatedWalStorage.ReadFault),
        nameof(SimulatedWalStorage.NamedReadFault),
        nameof(SimulatedWalStorage.NamedWriteFault),
        nameof(SimulatedWalStorage.DirectoryFlushFault),
        nameof(SimulatedWalStorage.NamedDeleteFault),
        nameof(SimulatedWalStorage.DeleteFault),
        nameof(SimulatedWalStorage.NamedPublishedFault),
    ];

    /// <summary>
    /// Every operation of the file-system seam, and of a file it opened, reports its failure once, as itself and with the
    /// path it names (a rename's or a publication's new name), and rethrows the file system's own exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeamOperations))]
    public async Task AFailedOperationIsReportedOnceAsItselfAndRethrown(FatalErrorSite operation, string path)
    {
        var failure = new IOException("The device failed.");
        var fileSystem = new FailingFileSystem(failure, openFiles: operation >= FatalErrorSite.HandleLength);
        var since = FatalErrors.ReportedCount;

        Assert.Same(failure, await Record.ExceptionAsync(() => Invocations[operation](fileSystem)));
        Assert.Same(failure, AssertReported(since, operation, path).Failure.Exception);

        // The seam reports the first failure it meets: it repeats no failed call.
        Assert.Equal(1, fileSystem.Calls);
    }

    /// <summary>
    /// A listing with lengths or last-write times reports a failure, a missing directory among them, as a listing of
    /// files, and a deletion of a file written before a cutoff reports one as a deletion, once, and rethrows it.
    /// </summary>
    [Theory]
    [MemberData(nameof(SharedSiteOperations))]
    public void AnOperationWhichSharesASiteReportsItsFailureThere(string operation, FatalErrorSite site, string path)
    {
        Action<WalFileSystem> invoke = operation switch
        {
            nameof(WalFileSystem.EnumerateFilesWithLengths) => static fileSystem => _ = fileSystem.EnumerateFilesWithLengths("d", static _ => true),
            nameof(WalFileSystem.EnumerateFilesWithLastWriteTimes) => static fileSystem => _ = fileSystem.EnumerateFilesWithLastWriteTimes("d", static _ => true),
            _ => static fileSystem => _ = fileSystem.TryDeleteFileWrittenBefore("f", DateTimeOffset.MaxValue),
        };
        Exception[] failures = site == FatalErrorSite.ListFiles
            ? [new IOException("The device failed."), new DirectoryNotFoundException("The directory does not exist.")]
            : [new IOException("The device failed.")];
        foreach (var failure in failures)
        {
            var fileSystem = new FailingFileSystem(failure);
            var since = FatalErrors.ReportedCount;

            Assert.Same(failure, Record.Exception(() => invoke(fileSystem)));
            Assert.Same(failure, AssertReported(since, site, path).Failure.Exception);
            Assert.Equal(1, fileSystem.Calls);
        }
    }

    /// <summary>
    /// An answer the file system gives, and a refusal of a request the caller cancelled, the volume does not serve, or
    /// the caller made wrongly, passes through the seam unreported.
    /// </summary>
    [Theory]
    [MemberData(nameof(Answers))]
    public void AnAnswerIsRethrownUnreported(Type answer)
    {
        var exception = (Exception)Activator.CreateInstance(answer, "The file system answered.")!;
        var fileSystem = new FailingFileSystem(exception);
        var since = FatalErrors.ReportedCount;

        Assert.Same(exception, Record.Exception(() => fileSystem.DeleteFile("f")));
        Assert.Empty(FatalErrors.TakeMine(since));
        Assert.Equal(1, fileSystem.Calls);
    }

    /// <summary>
    /// A missing directory answers only the directory operations whose contract names it: whether a directory exists,
    /// a creation whose parent is missing, a deletion and its last steps, and a listing of directories. Every other
    /// operation names a directory its caller created or was given, so a missing one means it has gone, as when its
    /// volume vanished, and the seam reports it as the operation's failure (conventions.md §14).
    /// </summary>
    [Theory]
    [MemberData(nameof(SeamOperations))]
    public async Task AMissingDirectoryAnswersOnlyTheDirectoryOperationsWhichNameIt(FatalErrorSite operation, string path)
    {
        var missing = new DirectoryNotFoundException("The directory does not exist.");
        var fileSystem = new FailingFileSystem(missing, openFiles: operation >= FatalErrorSite.HandleLength);
        var since = FatalErrors.ReportedCount;

        Assert.Same(missing, await Record.ExceptionAsync(() => Invocations[operation](fileSystem)));
        if (operation is FatalErrorSite.DirectoryExists or FatalErrorSite.CreateDirectory or FatalErrorSite.DeleteDirectory
            or FatalErrorSite.DeleteEmptyDirectory or FatalErrorSite.DeleteLockFile or FatalErrorSite.ListDirectories)
        {
            Assert.Empty(FatalErrors.TakeMine(since));
        }
        else
        {
            Assert.Same(missing, AssertReported(since, operation, path).Failure.Exception);
        }
    }

    /// <summary>
    /// The operating system's file system gives a missing file or directory, an existing name, and a held lock as
    /// answers, which change nothing and are not reported.
    /// </summary>
    [Fact]
    public async Task TheOperatingSystemsAnswersAreNotReported()
    {
        var directory = Directory.CreateTempSubdirectory("clog-wal-answers-");
        try
        {
            var fileSystem = WalFileSystem.Os;
            var a = Path.Combine(directory.FullName, "a");
            var b = Path.Combine(directory.FullName, "b");
            var missing = Path.Combine(directory.FullName, "missing");
            var lockPath = Path.Combine(directory.FullName, "lock");
            await File.WriteAllBytesAsync(a, [1], Token);
            await File.WriteAllBytesAsync(b, [2], Token);
            var since = FatalErrors.ReportedCount;

            Assert.False(fileSystem.FileExists(missing));
            Assert.False(fileSystem.DirectoryExists(missing));
            Assert.False(fileSystem.DirectoryExists(Path.Combine(missing, "child")));
            Assert.Null(await fileSystem.ReadFileAsync(missing, Token));
            fileSystem.DeleteFile(missing);
            _ = Assert.Throws<FileNotFoundException>(() => fileSystem.OpenFile(missing, direct: false));
            _ = Assert.Throws<DirectoryNotFoundException>(() => fileSystem.EnumerateDirectories(missing));
            _ = Assert.Throws<DirectoryNotFoundException>(() => fileSystem.CreateDirectory(Path.Combine(missing, "child")));
            _ = Assert.Throws<DirectoryNotFoundException>(() => fileSystem.DeleteDirectory(missing));
            _ = Assert.Throws<WalNameExistsException>(() => fileSystem.CreateFile(b, replace: false, writeThrough: false, preallocationSize: 0));
            _ = Assert.Throws<WalNameExistsException>(() => fileSystem.MoveFile(a, b, replace: false));
            _ = Assert.Throws<WalNameExistsException>(() => fileSystem.LinkFile(a, b));
            using (fileSystem.OpenLock(lockPath))
            {
                _ = Assert.Throws<WalLockHeldException>(() => fileSystem.OpenLock(lockPath));
            }

            Assert.Empty(FatalErrors.TakeMine(since));
            Assert.Equal([1], await File.ReadAllBytesAsync(a, Token));
            Assert.Equal([2], await File.ReadAllBytesAsync(b, Token));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The operating system's file system answers a path missing only within an existing directory: a file operation,
    /// or a directory flush, in a directory which has gone, as when its volume vanished, is reported as that operation's
    /// failure, while whether a directory exists answers <see langword="false"/> though its parent is missing too.
    /// </summary>
    [Fact]
    public async Task TheOperatingSystemReportsAFileOperationInAMissingDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("clog-wal-gone-");
        try
        {
            var fileSystem = WalFileSystem.Os;
            var gone = Path.Combine(directory.FullName, "gone");
            var file = Path.Combine(gone, "f");
            var since = FatalErrors.ReportedCount;

            Assert.False(fileSystem.DirectoryExists(Path.Combine(gone, "child")));
            Assert.Empty(FatalErrors.TakeMine(since));
            AssertFailureReported(since, FatalErrorSite.FileExists, file, Record.Exception(() => fileSystem.FileExists(file)));
            AssertFailureReported(since, FatalErrorSite.ReadFile, file, await Record.ExceptionAsync(async () => await fileSystem.ReadFileAsync(file, Token)));
            AssertFailureReported(since, FatalErrorSite.OpenFile, file, Record.Exception(() => fileSystem.OpenFile(file, direct: false).Dispose()));
            AssertFailureReported(since, FatalErrorSite.ListFiles, gone, Record.Exception(() => fileSystem.EnumerateFiles(gone, "*")));
            AssertFailureReported(since, FatalErrorSite.MeasureDirectory, gone, Record.Exception(() => fileSystem.MeasureDirectory(gone)));
            AssertFailureReported(since, FatalErrorSite.VolumeSpace, gone, Record.Exception(() => fileSystem.GetVolumeSpace(gone)));
            AssertFailureReported(since, FatalErrorSite.FlushDirectory, gone, Record.Exception(() => fileSystem.FlushDirectory(gone, DirectoryEntries.Files)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// On Windows, a refusal which outlasts the contention bound is a failure: a deletion which a read-only file refuses
    /// for good, with the access denial another process's handle also causes, is repeated for 30 s on the seam's clock
    /// and then reported once, and the file stays.
    /// </summary>
    [Fact]
    public async Task OnWindowsContentionPastItsBoundIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Contention is Windows'.");
        var directory = Directory.CreateTempSubdirectory("clog-wal-bound-");
        var file = Path.Combine(directory.FullName, "refused");
        await File.WriteAllBytesAsync(file, [1], Token);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var time = new FakeTimeProvider();
            var fileSystem = WalFileSystem.CreateOs(time);
            var since = FatalErrors.ReportedCount;
            var deletion = Task.Run(() => fileSystem.DeleteFile(file), Token);

            Assert.InRange(await AdvanceUntilCompletedAsync(time, deletion), FileSystemAnswers.ContentionBound, FileSystemAnswers.ContentionBound * 2);
            var refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => deletion);
            Assert.Same(refusal, AssertReported(since, FatalErrorSite.DeleteFile, file).Failure.Exception);
            Assert.True(File.Exists(file));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// On Windows an open's access denial of a file is contention, as a file whose deletion is pending answers it, and a
    /// denial which outlasts the bound is the file's permissions: an open of a read-only file for writing is repeated for
    /// 30 s on the seam's clock and then reported once.
    /// </summary>
    [Fact]
    public async Task OnWindowsAnOpensAccessDenialPastItsBoundIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Contention is Windows'.");
        var directory = Directory.CreateTempSubdirectory("clog-wal-bound-");
        var file = Path.Combine(directory.FullName, "refused");
        await File.WriteAllBytesAsync(file, [1], Token);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var time = new FakeTimeProvider();
            var fileSystem = WalFileSystem.CreateOs(time);
            var since = FatalErrors.ReportedCount;
            var open = Task.Run(() => fileSystem.OpenFile(file, direct: false).Dispose(), Token);

            Assert.InRange(await AdvanceUntilCompletedAsync(time, open), FileSystemAnswers.ContentionBound, FileSystemAnswers.ContentionBound * 2);
            var refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => open);
            Assert.Same(refusal, AssertReported(since, FatalErrorSite.OpenFile, file).Failure.Exception);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// On Windows, a directory's deletion which meets contention waits it out outside the directory's lock, so the
    /// directory's answers proceed meanwhile, and a refusal which outlasts the bound on the seam's clock is reported once.
    /// </summary>
    [Fact]
    public async Task OnWindowsADirectoryWaitsOutContentionOutsideItsLock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Contention is Windows'.");
        var root = Directory.CreateTempSubdirectory("clog-wal-bound-");
        var path = Path.Combine(root.FullName, "wal");
        var file = Path.Combine(WalFileSystem.Normalize(path), "a.chk");
        try
        {
            var time = new FakeTimeProvider();
            var fileSystem = WalFileSystem.CreateOs(time);
            FileWalDirectory.Create(path, fileSystem, Token);
            await using var directory = FileWalDirectory.Open(path, fileSystem, (name, flush) => PortableWalFile.Open(fileSystem, name, flush, direct: false), Token);
            using (var contents = Buffer([1]))
            {
                await (await directory.CreateNamedFileAsync("a.chk", contents, Token)).DisposeAsync();
            }

            File.SetAttributes(file, FileAttributes.ReadOnly);
            var since = FatalErrors.ReportedCount;
            var deletion = directory.DeleteNamedFileAsync("a.chk", Token).AsTask();
            Assert.False(deletion.IsCompleted);
            Assert.Equal(["a.chk"], await directory.ListNamedFilesAsync(".chk", Token));

            Assert.InRange(await AdvanceUntilCompletedAsync(time, deletion), FileSystemAnswers.ContentionBound, FileSystemAnswers.ContentionBound * 2);
            var refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => deletion);
            Assert.Same(refusal, AssertReported(since, FatalErrorSite.DeleteFile, file).Failure.Exception);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// On Windows, a deletion of a file, a rename over it, or an open of it, which another handle holds open sharing
    /// only reads, as antivirus and indexing services briefly do, waits until the handle closes and then succeeds
    /// unreported: the refusal is the other handle's answer, not a failure of this process's storage.
    /// </summary>
    /// <param name="operation">The operation the other handle refuses: <c>delete</c>, <c>rename</c>, or <c>open</c>.</param>
    [Theory]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("open")]
    public async Task OnWindowsAnotherHandlesContentionIsWaitedOut(string operation)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing modes.");
        var directory = Directory.CreateTempSubdirectory("clog-wal-contention-");
        try
        {
            var held = Path.Combine(directory.FullName, "held");
            var source = Path.Combine(directory.FullName, "source");
            await File.WriteAllBytesAsync(held, [1], Token);
            await File.WriteAllBytesAsync(source, [2], Token);
            var since = FatalErrors.ReportedCount;
            Task running;
            using (File.OpenHandle(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                running = Task.Run(
                    () =>
                    {
                        switch (operation)
                        {
                            case "rename":
                                WalFileSystem.Os.MoveFile(source, held, replace: true);
                                break;
                            case "delete":
                                WalFileSystem.Os.DeleteFile(held);
                                break;
                            default:
                                WalFileSystem.Os.OpenFile(held, direct: false).Dispose();
                                break;
                        }
                    },
                    Token);
                await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Token);
                Assert.False(running.IsCompleted);
            }

            await running.WaitAsync(TimeSpan.FromSeconds(20), Token);
            Assert.Empty(FatalErrors.TakeMine(since));
            switch (operation)
            {
                case "rename":
                    Assert.False(File.Exists(source));
                    Assert.Equal([2], await File.ReadAllBytesAsync(held, Token));
                    break;
                case "delete":
                    Assert.False(File.Exists(held));
                    break;
                default:
                    Assert.Equal([1], await File.ReadAllBytesAsync(held, Token));
                    break;
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Each fault hook of the simulated storage reports the failure it injects, as the operation the file-system seam
    /// would report and with the path of the file or directory it names, and stops the storage as the report stops the
    /// process: every later call fails unreported, until a crash returns the restarted process's storage, which serves
    /// the files the failure left.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hooks))]
    public async Task EachSimulatedFaultIsReportedAndStopsTheStorageUntilItsCrash(string hook)
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, Token);
        var directory = await storage.OpenDirectoryAsync(LogPath, Token);
        using var page = Buffer(new byte[4096]);
        var log = await directory.CreateFileAsync(1, page, 0, null, Token);
        var named = await directory.CreateNamedFileAsync("a.chk", page, Token);
        var failure = new IOException("The simulated device failed.");
        (FatalErrorSite Operation, string Path, Func<Task> Invoke) fault = hook switch
        {
            nameof(SimulatedWalStorage.WriteFault) => Inject(
                () => storage.WriteFault = (_, _, _, _) => failure,
                FatalErrorSite.HandleWriteGather,
                LogFile(1),
                async () => await log.WriteAsync(4096, new[] { page })),
            nameof(SimulatedWalStorage.TornWriteFault) => Inject(
                () => storage.TornWriteFault = (_, _, _, _) => failure,
                FatalErrorSite.HandleWriteGather,
                LogFile(1),
                async () => await log.WriteAsync(4096, new[] { page })),
            nameof(SimulatedWalStorage.ReadFault) => Inject(
                () => storage.ReadFault = (_, _, _, _) => failure,
                FatalErrorSite.HandleReadScatter,
                LogFile(1),
                async () => (await log.ReadAsync(0, 4096, Token)).Dispose()),
            nameof(SimulatedWalStorage.NamedReadFault) => Inject(
                () => storage.NamedReadFault = (_, _, _, _) => failure,
                FatalErrorSite.HandleReadScatter,
                Path.Combine(LogPath, "a.chk"),
                async () => (await named.ReadAsync(0, 4096, Token)).Dispose()),
            nameof(SimulatedWalStorage.NamedWriteFault) => Inject(
                () => storage.NamedWriteFault = (_, _, _, _) => failure,
                FatalErrorSite.CreateFile,
                Path.Combine(LogPath, "b.chk"),
                async () => await (await directory.CreateNamedFileAsync("b.chk", page, Token)).DisposeAsync()),
            nameof(SimulatedWalStorage.DirectoryFlushFault) => Inject(
                () => storage.DirectoryFlushFault = _ => failure,
                FatalErrorSite.FlushDirectory,
                LogPath,
                async () => await (await directory.CreateFileAsync(2, page, 0, null, Token)).DisposeAsync()),
            nameof(SimulatedWalStorage.NamedDeleteFault) => Inject(
                () => storage.NamedDeleteFault = (_, _) => failure,
                FatalErrorSite.DeleteFile,
                Path.Combine(LogPath, "a.chk"),
                async () => await directory.DeleteNamedFileAsync("a.chk", Token)),
            nameof(SimulatedWalStorage.DeleteFault) => Inject(
                () => storage.DeleteFault = (_, _) => failure,
                FatalErrorSite.DeleteFile,
                LogFile(1),
                async () => await directory.DeleteFileAsync(1, Token)),
            nameof(SimulatedWalStorage.NamedPublishedFault) => Inject(
                () => storage.NamedPublishedFault = (_, _) => failure,
                FatalErrorSite.FlushDirectory,
                LogPath,
                async () => await (await directory.CreateNamedFileAsync("b.chk", page, Token)).DisposeAsync()),
            _ => throw new ArgumentOutOfRangeException(nameof(hook), hook, "An unknown hook."),
        };
        var since = FatalErrors.ReportedCount;

        Assert.Same(failure, await Record.ExceptionAsync(fault.Invoke));
        Assert.Same(failure, AssertReported(since, fault.Operation, fault.Path).Failure.Exception);
        _ = await Assert.ThrowsAsync<IOException>(async () => await directory.ListFilesAsync(Token));
        _ = await Assert.ThrowsAsync<IOException>(async () => await storage.OpenDirectoryAsync(LogPath, Token));
        Assert.Empty(FatalErrors.TakeMine(since));
        await log.DisposeAsync();
        await named.DisposeAsync();
        await directory.DisposeAsync();

        var restarted = storage.Crash(operatingSystem: false);
        await using var reopened = await restarted.OpenDirectoryAsync(LogPath, Token);
        Assert.Contains(1ul, await reopened.ListFilesAsync(Token));
        Assert.Contains("a.chk", await reopened.ListNamedFilesAsync(".chk", Token));
    }

    /// <summary>
    /// A call through a crashed process's simulated storage, and a write the crash left in flight, fails with
    /// <see cref="CrashedProcessException"/>, which no report keeps: a crashed process reports nothing. A call through
    /// storage which an injected fault stopped carries the reported fault, so no component reports it again.
    /// </summary>
    [Fact]
    public async Task ACrashedStorageFailsAsACrashAndAStoppedOneCarriesItsReportedFault()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await storage.CreateDirectoryAsync(LogPath, Token);
        var directory = await storage.OpenDirectoryAsync(LogPath, Token);
        using var page = Buffer(new byte[4096]);
        var log = await directory.CreateFileAsync(1, page, 0, null, Token);
        var inFlight = log.WriteAsync(4096, new[] { page }).AsTask();
        var restarted = storage.Crash(operatingSystem: false);

        _ = await Assert.ThrowsAsync<CrashedProcessException>(() => inFlight);
        _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await directory.ListFilesAsync(Token));
        await log.DisposeAsync();
        await directory.DisposeAsync();

        var failure = new IOException("The simulated device failed.");
        restarted.DeleteFault = (_, _) => failure;
        await using var reopened = await restarted.OpenDirectoryAsync(LogPath, Token);
        var since = FatalErrors.ReportedCount;
        Assert.Same(failure, await Record.ExceptionAsync(async () => await reopened.DeleteFileAsync(1, Token)));
        _ = AssertReported(since, FatalErrorSite.DeleteFile, LogFile(1));
        var stopped = await Assert.ThrowsAsync<IOException>(async () => await reopened.ListFilesAsync(Token));
        Assert.Same(failure, stopped.InnerException);
        Assert.True(FatalErrorHandler.IsReported(stopped));
    }

    private static void AssertFailureReported(long since, FatalErrorSite operation, string path, Exception? failure)
    {
        Assert.NotNull(failure);
        Assert.Same(failure, AssertReported(since, operation, path).Failure.Exception);
    }

    private static (FatalErrorSite Operation, string Path, Func<Task> Invoke) Inject(
        Action install, FatalErrorSite operation, string path, Func<Task> invoke)
    {
        install();
        return (operation, path, invoke);
    }

    private static Task Sync(Action operation)
    {
        operation();
        return Task.CompletedTask;
    }

    private static async Task OnFileAsync(WalFileSystem fileSystem, Func<IWalFileSystemFile, Task> operation)
    {
        using var file = fileSystem.OpenFile("f", direct: false);
        await operation(file);
    }

    /// <summary>
    /// A file system each of whose operations throws <paramref name="failure"/>; with <paramref name="openFiles"/>, it
    /// creates and opens files each of whose operations throws it. It counts the calls which throw.
    /// </summary>
    private sealed class FailingFileSystem(Exception failure, bool openFiles = false) : WalFileSystem
    {
        private int _calls;

        /// <summary>
        /// Gets how many calls threw the failure.
        /// </summary>
        public int Calls => Volatile.Read(ref _calls);

        internal override bool DirectoryExistsCore(string path) => throw Fail();

        internal override void CreateDirectoryCore(string path) => throw Fail();

        internal override void DeleteDirectoryCore(string path) => throw Fail();

        internal override IEnumerable<string> EnumerateFilesCore(string path, string searchPattern) => throw Fail();

        internal override IEnumerable<WalListedFileWriteTime> EnumerateFilesWithLastWriteTimesCore(string path, WalFileNameFilter includeName) => throw Fail();

        internal override IEnumerable<string> EnumerateDirectoriesCore(string path) => throw Fail();

        internal override long MeasureDirectoryCore(string path) => throw Fail();

        internal override WalVolumeSpace GetVolumeSpaceCore(string path) => throw Fail();

        internal override bool FileExistsCore(string path) => throw Fail();

        internal override IDisposable OpenLockCore(string path) => throw Fail();

        internal override IDisposable OpenLockForDeletionCore(string path) => throw Fail();

        internal override IWalFileSystemFile CreateFileCore(string path, bool replace, bool writeThrough, long preallocationSize) =>
            openFiles ? new FailingFile(Fail) : throw Fail();

        internal override IWalFileSystemFile OpenFileCore(string path, bool direct) =>
            openFiles ? new FailingFile(Fail) : throw Fail();

        internal override void MoveFileCore(string source, string destination, bool replace) => throw Fail();

        internal override void LinkFileCore(string existing, string link) => throw Fail();

        internal override ValueTask<byte[]?> ReadFileCoreAsync(string path, CancellationToken cancellationToken) =>
            ValueTask.FromException<byte[]?>(Fail());

        internal override void DeleteFileCore(string path) => throw Fail();

        internal override bool TryDeleteEmptyDirectoryCore(string path) => throw Fail();

        internal override bool TryDeleteLockFileCore(string path) => throw Fail();

        internal override void FlushDirectoryCore(string path, DirectoryEntries entries) => throw Fail();

        private Exception Fail()
        {
            _ = Interlocked.Increment(ref _calls);
            return failure;
        }
    }

    private sealed class FailingFile(Func<Exception> fail) : IWalFileSystemFile
    {
        public long Length => throw fail();

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromException<int>(fail());

        public ValueTask<long> ReadAsync(IReadOnlyList<Memory<byte>> buffers, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromException<long>(fail());

        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromException(fail());

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromException(fail());

        public void SetLength(long length) => throw fail();

        public void Flush() => throw fail();

        public void Dispose()
        {
        }
    }
}
