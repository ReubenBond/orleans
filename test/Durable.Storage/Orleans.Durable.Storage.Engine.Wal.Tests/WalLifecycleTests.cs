using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

public sealed class WalLifecycleTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task CreateFailsWhenFilesExist()
    {
        var storage = new SimulatedWalStorage();
        await (await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken)).DisposeAsync();

        await Assert.ThrowsAsync<WalExistsException>(
            async () => await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenFailsOnAMissingOrEmptyLog()
    {
        var storage = new SimulatedWalStorage();
        await Assert.ThrowsAsync<WalNotFoundException>(
            async () => await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken));

        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WalNotFoundException>(
            async () => await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APageSizeBelowTheStorageMinimumIsUnsupported()
    {
        var storage = new SimulatedWalStorage(minimumPageSize: 4096);

        await Assert.ThrowsAsync<WalUnsupportedFormatException>(
            async () => await WriteAheadLog.CreateAsync(storage, LogPath, Options(pageSize: 512), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EachOpenAppendsToAFreshFileAndPositionsKeepIncreasing()
    {
        var storage = new SimulatedWalStorage();
        var positions = new List<WalPosition>();
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options() with { PreallocateFiles = false }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(new WalPosition(1, 4096), wal.DurableFrontier);
            positions.Add(await AppendAsync(wal, [1]));
        }

        for (var open = 2; open <= 3; open++)
        {
            await using var wal = await WriteAheadLog.OpenAsync(storage, LogPath, Options() with { PreallocateFiles = false }, TestContext.Current.CancellationToken);
            Assert.Equal(new WalPosition((ulong)open, 4096), wal.DurableFrontier);
            positions.Add(await AppendAsync(wal, [(byte)open]));
            Assert.Equal(new WalFileHeader(4096, 256, FirstOfOpen: true), WalTestHelpers.ReadHeader(storage.ReadDurable(LogPath, (ulong)open).AsSpan(0, 4096)));
        }

        Assert.Equal([new(1, 4100), new(2, 4100), new(3, 4100)], positions);
        await using var last = await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        Assert.Equal([[1], [2], [3]], (await ReadAllAsync(last)).Select(static entry => entry.Payload!));
    }

    /// <summary>
    /// Opening flushes every file before reading, so a record a process crash left only in the cache, which a
    /// scan may report, stays durable through a later operating-system crash.
    /// </summary>
    [Fact]
    public async Task OpenFlushesCachedBytesBeforeAnyRead()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        var append = AppendAsync(wal, [1, 2, 3]).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var restarted = storage.Crash(operatingSystem: false);
        _ = await Assert.ThrowsAsync<IOException>(() => append);
        await wal.DisposeAsync();

        await using (var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, Options(), TestContext.Current.CancellationToken))
        {
            Assert.Equal([[1, 2, 3]], (await ReadAllAsync(reopened)).Select(static entry => entry.Payload));
        }

        var afterOsCrash = restarted.Crash(operatingSystem: true);
        await using var final = await WriteAheadLog.OpenAsync(afterOsCrash, LogPath, Options(), TestContext.Current.CancellationToken);
        Assert.Equal([[1, 2, 3]], (await ReadAllAsync(final)).Select(static entry => entry.Payload));
    }

    /// <summary>
    /// Disposing the log closes its directory and every file it opened, including the next file it was preparing
    /// (docs/spec/verification.md §11.1).
    /// </summary>
    [Fact]
    public async Task DisposalClosesTheDirectoryAndEveryFile()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(pageSize: 512, fileSize: 8 * 512) with { PreallocateFiles = true };
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(index => AppendAsync(wal, Bytes(300, index)).AsTask()));
            Assert.True(storage.ListFiles(LogPath).Count > 1);
            Assert.True(storage.OpenHandleCount > 1);
        }

        Assert.Equal(0, storage.OpenHandleCount);
        await using (var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            Assert.Equal(20, (await ReadAllAsync(reopened)).Count(static entry => entry.IsRecord));
        }

        Assert.Equal(0, storage.OpenHandleCount);
    }

    [Fact]
    public async Task ASecondOwnerIsRejected()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(
            async () => await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeWaitsForAcceptedRequests()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        var append = AppendAsync(wal, [1, 2, 3]).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var disposal = wal.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await AppendAsync(wal, [4]));
        Assert.False(disposal.IsCompleted);

        Assert.True(storage.CompleteWrite());
        await disposal;
        Assert.Equal(new WalPosition(1, 4100), await append);
    }

    private static string FilePath(string directory, ulong number) =>
        System.IO.Path.Combine(directory, WalFormat.GetFileName(number));

    /// <summary>
    /// The portable storage creates a log's directory only under an existing parent, the caller's root: it creates no
    /// ancestor, so nothing it creates or flushes lies above that root (wal.md §3).
    /// </summary>
    [Fact]
    public async Task PortableStorageCreatesNoAncestor()
    {
        var root = Directory.CreateTempSubdirectory("clog-wal-");
        try
        {
            var missing = System.IO.Path.Combine(root.FullName, "missing");
            _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(
                async () => await WriteAheadLog.CreateAsync(new PortableWalStorage(), System.IO.Path.Combine(missing, "wal"), Options(fileSize: 4 * 4096), TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PortableStorageRoundTripsAcrossReopen()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clog-wal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new PortableWalStorage();
            var options = Options(fileSize: 4 * 4096) with { PreallocateFiles = true };
            var payloads = Enumerable.Range(0, 20).Select(static index => Bytes(index * 700, index)).ToArray();
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                await Task.WhenAll(payloads.Select(payload => AppendAsync(wal, payload).AsTask()));
            }

            FileHandleLeakCheck.AssertClosed(directory);
            await using var reopened = await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken);
            var entries = await ReadAllAsync(reopened);
            Assert.All(entries, static entry => Assert.True(entry.IsRecord));
            Assert.Equal(
                payloads.Select(Convert.ToHexString).Order(),
                entries.Select(static entry => Convert.ToHexString(entry.Payload!)).Order());
            Assert.Contains("00000000000000000002.clfl", Directory.EnumerateFiles(directory).Select(System.IO.Path.GetFileName));
            await Assert.ThrowsAsync<WalLockHeldException>(
                async () => await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreallocatedFilesAreZeroFilledPreparedAheadAndDoubleInSize()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clog-wal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new PortableWalStorage();
            var options = Options(fileSize: 16 << 20) with { PreallocateFiles = true };
            var payloads = Enumerable.Range(0, 1600).Select(static index => Bytes(4000, index)).ToArray();
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                foreach (var batch in payloads.Chunk(64))
                {
                    await Task.WhenAll(batch.Select(payload => AppendAsync(wal, payload).AsTask()));
                }
            }

            FileHandleLeakCheck.AssertClosed(directory);

            // The first file starts at 4 MiB; a roll uses the prepared 8 MiB file, and 16 MiB is prepared once that one is a
            // quarter full.
            Assert.Equal(
                [4L << 20, 8L << 20, 16L << 20],
                Enumerable.Range(1, 3).Select(number => new FileInfo(FilePath(directory, (ulong)number)).Length));
            var prepared = await File.ReadAllBytesAsync(FilePath(directory, 3), TestContext.Current.CancellationToken);
            Assert.False(prepared.AsSpan(4096).ContainsAnyExcept((byte)0));

            await using var reopened = await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken);
            var entries = await ReadAllAsync(reopened);
            Assert.Equal(
                payloads.Select(Convert.ToHexString).Order(),
                entries.Select(static entry => Convert.ToHexString(entry.Payload!)).Order());

            // Reopening truncates the last open's files after their data and the prepared file to its header.
            Assert.InRange(new FileInfo(FilePath(directory, 2)).Length, 2 * 4096, (8L << 20) - 4096);
            Assert.Equal(4096, new FileInfo(FilePath(directory, 3)).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordLargerThanThePreparedFileLeavesItEmpty()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clog-wal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new PortableWalStorage();
            var options = Options(fileSize: 4 * 4096) with { PreallocateFiles = true };
            var large = Bytes(5 * 4096, 1);
            var small = Bytes(10, 2);
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                Assert.Equal(3UL, (await AppendAsync(wal, large)).File);
                Assert.Equal(4UL, (await AppendAsync(wal, small)).File);
            }

            Assert.Equal(4 * 4096, new FileInfo(FilePath(directory, 2)).Length);
            await using var reopened = await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken);
            var entries = await ReadAllAsync(reopened);
            Assert.Equal([large, small], entries.Select(static entry => entry.Payload!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HeaderAndPayloadFormOneRecord()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        using var headerWriter = new ArcBufferWriter();
        headerWriter.Write([1, 2]);
        using var header = headerWriter.PeekSlice(2);
        using var payloadWriter = new ArcBufferWriter();
        payloadWriter.Write([3, 4, 5]);
        using var payload = payloadWriter.PeekSlice(3);
        using var withPayload = new RecordingRequest(header, payload);
        using var headerOnly = new RecordingRequest(header, default);
        using var empty = new RecordingRequest(default, default);

        await wal.WriteAsync(withPayload, TestContext.Current.CancellationToken);
        await wal.WriteAsync(headerOnly, TestContext.Current.CancellationToken);
        await wal.WriteAsync(empty, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4, 5], (await wal.ReadAsync(await withPayload.Completion, TestContext.Current.CancellationToken)).ToBytes());
        Assert.Equal([1, 2], (await wal.ReadAsync(await headerOnly.Completion, TestContext.Current.CancellationToken)).ToBytes());
        Assert.Empty((await wal.ReadAsync(await empty.Completion, TestContext.Current.CancellationToken)).ToBytes());
        Assert.All([withPayload, headerOnly, empty], static request => Assert.Equal(1, request.Outcomes));
    }
}
