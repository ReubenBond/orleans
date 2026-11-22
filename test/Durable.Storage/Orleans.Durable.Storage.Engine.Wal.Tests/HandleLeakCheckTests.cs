using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.IoUring;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using Xunit.Sdk;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Shows that the handle checks see an open handle and pass once it is closed, so a test which passes them proves its
/// owner closed every handle (docs/spec/verification.md §11.1).
/// </summary>
public sealed class HandleLeakCheckTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public Task AnOpenPortableWalFileFailsTheCheck() => AssertOpenFilesFailTheCheckAsync(new PortableWalStorage());

    [Fact]
    public async Task AnOpenIoUringWalFileFailsTheCheck()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "io_uring requires Linux.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var storage = new IoUringWalStorage();
        await AssertOpenFilesFailTheCheckAsync(storage);
    }

    /// <summary>
    /// A simulated storage counts every directory and file handle until it is disposed, once.
    /// </summary>
    [Fact]
    public async Task AnUndisposedSimulatedHandleStaysOpen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, cancellationToken);
        var directory = await storage.OpenDirectoryAsync(LogPath, cancellationToken);
        WalFile file;
        using (var header = Header())
        {
            file = await directory.CreateFileAsync(1, header, preallocatedLength: 0, pace: null, cancellationToken);
        }

        Assert.Equal(2, storage.OpenHandleCount);
        await directory.DisposeAsync();
        await directory.DisposeAsync();
        Assert.Equal(1, storage.OpenHandleCount);
        await file.DisposeAsync();
        await file.DisposeAsync();
        Assert.Equal(0, storage.OpenHandleCount);
    }

    /// <summary>
    /// The check fails while a log file is open, names each open file, and passes once the file and the directory,
    /// which holds the lock file, are closed.
    /// </summary>
    private static async Task AssertOpenFilesFailTheCheckAsync(WalStorage storage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "clog-wal-handles-" + Guid.NewGuid().ToString("N"));
        try
        {
            await storage.CreateDirectoryAsync(path, cancellationToken);
            var directory = await storage.OpenDirectoryAsync(path, cancellationToken);
            WalFile file;
            using (var header = Header())
            {
                file = await directory.CreateFileAsync(1, header, preallocatedLength: 0, pace: null, cancellationToken);
            }

            var open = Assert.Throws<FailException>(() => FileHandleLeakCheck.AssertClosed(path)).Message;
            Assert.Contains(WalFormat.GetFileName(1), open, StringComparison.Ordinal);
            Assert.Contains(WalFormat.LockFileName, open, StringComparison.Ordinal);

            await file.DisposeAsync();
            var locked = Assert.Throws<FailException>(() => FileHandleLeakCheck.AssertClosed(path)).Message;
            Assert.DoesNotContain(WalFormat.GetFileName(1), locked, StringComparison.Ordinal);
            Assert.Contains(WalFormat.LockFileName, locked, StringComparison.Ordinal);

            await directory.DisposeAsync();
            FileHandleLeakCheck.AssertClosed(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static ArcBuffer Header()
    {
        var page = new byte[4096];
        WalFormat.WriteHeader(page, writeWindowPages: 256, firstOfOpen: true);
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(page);
        return writer.ConsumeSlice(page.Length);
    }
}
