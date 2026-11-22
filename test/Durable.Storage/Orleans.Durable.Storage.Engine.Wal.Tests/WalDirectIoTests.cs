using System.Globalization;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.IoUring;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// The storages open each log and named file for direct I/O, so each write reports its own transfer errors and each read
/// returns the device's bytes; durability comes from data-synchronous handles or a data sync per write (wal.md §3, §9).
/// </summary>
public sealed class WalDirectIoTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public static TheoryData<string> Storages => ["portable", "io-uring"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void LinuxOpenFlagsUseEachArchitecturesDirectValue()
    {
        // O_RDWR | O_DSYNC | O_CLOEXEC, and O_DIRECT, which is 0x4000 on x86-64 and 0x10000 on arm64.
        Assert.Equal(0x2 | 0x1000 | 0x80000 | 0x4000, WalDirectIo.LinuxOpenFlags(Architecture.X64));
        Assert.Equal(0x2 | 0x1000 | 0x80000 | 0x10000, WalDirectIo.LinuxOpenFlags(Architecture.Arm64));
        _ = Assert.Throws<PlatformNotSupportedException>(() => WalDirectIo.LinuxOpenFlags(Architecture.X86));
    }

    /// <summary>
    /// A Linux file qualifies for direct I/O only when <c>statx</c> reports its direct-I/O alignment
    /// (<c>STATX_DIOALIGN</c>, 0x2000) and both its memory and offset alignments divide 4,096: a zero alignment is a
    /// file the file system serves through the page cache, and an unreported one a kernel before 6.1 (wal.md §9).
    /// </summary>
    [Theory]
    [InlineData(0x2000u, 512u, 512u, true)]
    [InlineData(0x2000u, 4u, 4096u, true)]
    [InlineData(0x2000u | 0x7FFu, 512u, 4096u, true)]
    [InlineData(0x7FFu, 512u, 512u, false)]
    [InlineData(0x2000u, 0u, 0u, false)]
    [InlineData(0x2000u, 512u, 0u, false)]
    [InlineData(0x2000u, 512u, 8192u, false)]
    [InlineData(0x2000u, 8192u, 512u, false)]
    [InlineData(0x2000u, 512u, 1536u, false)]
    public void AFileQualifiesForDirectIoOnlyWhenStatxReportsAnAlignmentDividingTheDirectAlignment(uint mask, uint memory, uint offset, bool qualifies) =>
        Assert.Equal(qualifies, WalDirectIo.ServesDirectIo(mask, memory, offset));

    /// <summary>
    /// On Linux and Windows, the portable storage opens every named file it creates or opens for direct, write-through
    /// I/O (wal.md §9); macOS, which serves development and tests, buffers.
    /// </summary>
    [Fact]
    public async Task ThePortableStorageOpensEveryFileForDirectIo()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS serves development and tests with buffered I/O.");
        var path = Path.Combine(Path.GetTempPath(), "clog-wal-direct-files-" + Guid.NewGuid().ToString("N"));
        var storage = new PortableWalStorage();
        try
        {
            await storage.CreateDirectoryAsync(path, Token);
            await using var directory = await storage.OpenDirectoryAsync(path, Token);
            using var writer = ArcBufferWriter.CreateAligned();
            writer.Write(WalTestHelpers.Bytes(4096, 3));
            using var contents = writer.ConsumeSlice(4096);
            await using var created = await directory.CreateNamedFileAsync("file.bin", contents, Token);
            await using var opened = await directory.OpenNamedFileAsync("file.bin", Token);
            Assert.True(Assert.IsType<PortableWalFile>(created).Direct);
            Assert.True(Assert.IsType<PortableWalFile>(opened).Direct);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// On Linux, a direct open of a file another handle holds locked is the file system's answer that another owner
    /// holds it (<see cref="WalLockHeldException"/>), which the seam passes through unreported (conventions.md §14).
    /// </summary>
    [Fact]
    public void OnLinuxADirectOpenOfALockedFileAnswersThatTheLockIsHeld()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The direct open's flock is Linux's.");
        var path = Path.Combine(Path.GetTempPath(), "clog-wal-locked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[4096]);
        try
        {
            // .NET locks a handle which shares nothing with an exclusive flock.
            using (File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var since = FatalErrors.ReportedCount;
                _ = Assert.Throws<WalLockHeldException>(() => WalFileSystem.Os.OpenFile(path, direct: true));
                Assert.Empty(FatalErrors.TakeMine(since));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// On Windows a direct handle is opened with <c>FILE_FLAG_NO_BUFFERING</c>, which refuses an unaligned read that a
    /// buffered handle serves (wal.md §9). The WAL never issues one, so the seam reports the refusal as a failed read
    /// (conventions.md §14).
    /// </summary>
    [Fact]
    public async Task OnWindowsADirectHandleRefusesAnUnalignedRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FILE_FLAG_NO_BUFFERING is Windows'.");
        var path = Path.Combine(Path.GetTempPath(), "clog-wal-unaligned-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, new byte[8192], Token);
        try
        {
            using (var direct = WalFileSystem.Os.OpenFile(path, direct: true))
            {
                var since = FatalErrors.ReportedCount;
                var refusal = await Assert.ThrowsAnyAsync<IOException>(async () => await direct.ReadAsync(new byte[100], 1, Token));
                Assert.Same(refusal, WalTestHelpers.AssertReported(since, FatalErrorSite.HandleRead, path).Failure.Exception);
            }

            using var buffered = WalFileSystem.Os.OpenFile(path, direct: false);
            Assert.Equal(100, await buffered.ReadAsync(new byte[100], 1, Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// On Linux, every descriptor the log holds on its log files is direct. The portable storage's are data-synchronous;
    /// the io_uring storage's are not, because each io_uring write is followed by its own data sync (wal.md §9).
    /// </summary>
    [Theory]
    [MemberData(nameof(Storages))]
    public async Task OnLinuxEveryLogFileDescriptorIsDirectWithItsStoragesSyncMode(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "File descriptor flags are read from /proc on Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "clog-wal-direct-" + Guid.NewGuid().ToString("N"));
        using var uring = kind == "io-uring" ? new IoUringWalStorage() : null;
        WalStorage storage = uring is null ? new PortableWalStorage() : uring;
        try
        {
            var options = WalTestHelpers.Options() with { PreallocateFiles = false };
            await using (var log = await WriteAheadLog.CreateAsync(storage, path, options, Token))
            {
                _ = await WalTestHelpers.AppendAsync(log, [1, 2, 3]);
            }

            await using (var log = await WriteAheadLog.OpenAsync(storage, path, options, Token))
            {
                var expected = WalDirectIo.LinuxOpenFlags(RuntimeInformation.ProcessArchitecture, dataSync: uring is null);
                var flags = OpenFlags(path, WalFormat.FileExtension);
                Assert.Equal(2, flags.Count);
                Assert.All(flags, flag => Assert.Equal(expected, flag & WalDirectIo.LinuxOpenFlags(RuntimeInformation.ProcessArchitecture)));
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// A read of any range returns exactly the bytes the file holds there, shorter only at the end of the file, though
    /// the storage reads whole aligned pages.
    /// </summary>
    [Theory]
    [MemberData(nameof(Storages))]
    public async Task AReadOfAnyRangeReturnsExactlyItsBytes(string kind)
    {
        Assert.SkipWhen(kind == "io-uring" && !OperatingSystem.IsLinux(), "io_uring requires Linux.");
        var path = Path.Combine(Path.GetTempPath(), "clog-wal-reads-" + Guid.NewGuid().ToString("N"));
        using var uring = kind == "io-uring" && OperatingSystem.IsLinux() ? new IoUringWalStorage() : null;
        WalStorage storage = uring is null ? new PortableWalStorage() : uring;
        var bytes = WalTestHelpers.Bytes(10_000, 7);
        try
        {
            await storage.CreateDirectoryAsync(path, Token);
            await using var directory = await storage.OpenDirectoryAsync(path, Token);
            using (var writer = ArcBufferWriter.CreateAligned())
            {
                writer.Write(bytes);
                using var contents = writer.ConsumeSlice(bytes.Length);
                await (await directory.CreateNamedFileAsync("file.bin", contents, Token)).DisposeAsync();
            }

            await using var file = await directory.OpenNamedFileAsync("file.bin", Token);
            foreach (var (offset, length) in new[] { (0, 1), (1, 4095), (4095, 2), (4096, 4096), (5000, 9000), (8191, 1810), (10_000, 10), (12_288, 5) })
            {
                using var read = await file.ReadAsync(offset, length, Token);
                var expected = bytes.AsSpan(Math.Min(offset, bytes.Length), Math.Clamp(bytes.Length - offset, 0, length)).ToArray();
                Assert.True(expected.AsSpan().SequenceEqual(read.ToArray()), string.Create(CultureInfo.InvariantCulture, $"[{offset}, {offset + length})"));
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// Returns the open-file flags of each of this process's descriptors on a file in <paramref name="directory"/> whose
    /// name ends with <paramref name="extension"/>, from <c>/proc/self/fdinfo</c>.
    /// </summary>
    private static List<int> OpenFlags(string directory, string extension)
    {
        var flags = new List<int>();
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(descriptor).LinkTarget;
            }
            catch (IOException)
            {
                continue;
            }

            if (target is null
                || !string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(directory), StringComparison.Ordinal)
                || !target.EndsWith(extension, StringComparison.Ordinal))
            {
                continue;
            }

            var entry = File.ReadLines(Path.Combine("/proc/self/fdinfo", Path.GetFileName(descriptor)))
                .Single(static line => line.StartsWith("flags:", StringComparison.Ordinal));
            flags.Add(Convert.ToInt32(entry["flags:".Length..].Trim(), 8));
        }

        return flags;
    }
}
