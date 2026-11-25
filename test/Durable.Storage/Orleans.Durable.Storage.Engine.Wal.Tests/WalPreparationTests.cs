using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Staged preparation of the next preallocated file: when it starts (including earlier when doubling), how its
/// zero-fill is paced and woken at three-quarters full, zero-stall rolling to an allocated file while staging is in
/// flight, and instant cancellation on disposal (wal.md §8, "Files and preallocation").
/// </summary>
public sealed class WalPreparationTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    // 512 data pages: the next file's preparation starts at 128 and is paced to be ready at 384.
    private const long DataPages = 512;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void TheFillSpreadsItsBytesOverTheTimeTheCurrentFileTakesToBecomeThreeQuartersFull()
    {
        // 128 pages in 10 s: 256 more pages, to three quarters, take 20 s, so 64 KiB of the remaining 2 MiB waits
        // 20 s / 32.
        Assert.Equal(
            TimeSpan.FromSeconds(0.625),
            WriteAheadLog.FillDelay(DataPages, 128, TimeSpan.FromSeconds(10), 2L << 20, 64 << 10));

        // 256 pages in 20 s: 128 more take 10 s, so 64 KiB of the remaining 2 MiB waits 10 s / 32.
        Assert.Equal(
            TimeSpan.FromSeconds(0.3125),
            WriteAheadLog.FillDelay(DataPages, 256, TimeSpan.FromSeconds(20), 2L << 20, 64 << 10));
    }

    [Fact]
    public void TheFillGoesAtFullSpeedOnceTheCurrentFileIsThreeQuartersFullAndWaitsAtMostOneSecond()
    {
        Assert.Equal(TimeSpan.Zero, WriteAheadLog.FillDelay(DataPages, 384, TimeSpan.FromSeconds(10), 2L << 20, 64 << 10));
        Assert.Equal(TimeSpan.Zero, WriteAheadLog.FillDelay(DataPages, 500, TimeSpan.FromSeconds(10), 2L << 20, 64 << 10));
        Assert.Equal(TimeSpan.FromSeconds(1), WriteAheadLog.FillDelay(DataPages, 128, TimeSpan.FromHours(1), 2L << 20, 64 << 10));
        Assert.Equal(TimeSpan.FromSeconds(1), WriteAheadLog.FillDelay(DataPages, 0, TimeSpan.FromSeconds(10), 2L << 20, 64 << 10));
    }

    [Fact]
    public async Task TheNextFileIsPreparedOnceTheCurrentFileIsAQuarterFull()
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken));
        await using var wal = await WriteAheadLog.CreateAsync(directory, PacedOptions(new FakeTimeProvider()), TestContext.Current.CancellationToken);

        // Each record waits for durability alone, so it closes its own page.
        for (var page = 1; page < DataPages / 4; page++)
        {
            _ = await AppendAsync(wal, Bytes(100, page));
        }

        Assert.Equal(0, directory.PrepareCount);
        _ = await AppendAsync(wal, Bytes(100, 0));
        await directory.PrepareStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DoublingStartsPreparingAtOneEighthFull()
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken));

        // 8 MiB target size: first file is 4 MiB (1023 data pages), so doubling to 8 MiB starts at 12.5% (127 pages).
        var options = Options(fileSize: 8L << 20) with { PreallocateFiles = true, TimeProvider = new FakeTimeProvider() };
        await using var wal = await WriteAheadLog.CreateAsync(directory, options, TestContext.Current.CancellationToken);
        const int DoublingThreshold = (int)(1023 * 0.125);
        for (var page = 1; page < DoublingThreshold; page++)
        {
            _ = await AppendAsync(wal, Bytes(100, page));
        }

        Assert.Equal(0, directory.PrepareCount);
        _ = await AppendAsync(wal, Bytes(100, 0));
        await directory.PrepareStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CrossingThreeQuartersWakesAPacedFillImmediately()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken));
        await using var wal = await WriteAheadLog.CreateAsync(directory, PacedOptions(time), TestContext.Current.CancellationToken);
        await StartSlowPreparationAsync(wal, directory, time);

        // The clock no longer moves, so the paced fill would wait forever; reaching 75% wakes it immediately before roll.
        for (var page = DataPages / 4; page < DataPages * 3 / 4; page++)
        {
            _ = await AppendAsync(wal, Bytes(100, (int)page)).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        await directory.PrepareCompleted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(1UL, wal.CurrentFile);
        Assert.Equal([1UL], storage.ListFiles(LogPath));
    }

    [Fact]
    public async Task ARollHurriesAPacedFillAndPublishesWhenReady()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken));
        await using var wal = await WriteAheadLog.CreateAsync(directory, PacedOptions(time), TestContext.Current.CancellationToken);
        await StartSlowPreparationAsync(wal, directory, time);

        // Reaching 75% wakes the paced fill; wait for it to finish, then fill the rest of file 1 and roll.
        for (var page = DataPages / 4; page < DataPages * 3 / 4; page++)
        {
            _ = await AppendAsync(wal, Bytes(100, (int)page)).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        await directory.PrepareCompleted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        WalPosition last = default;
        for (var page = DataPages * 3 / 4; page <= DataPages; page++)
        {
            last = await AppendAsync(wal, Bytes(100, (int)page)).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2UL, last.File);
        Assert.False(wal.CurrentFileAllocated);
        Assert.Contains(2UL, directory.Published.Keys);
        Assert.Empty(directory.Allocated);
        Assert.Equal([1UL, 2UL], storage.ListFiles(LogPath));
    }

    /// <summary>
    /// Rolling while background staging is still in flight does not block on zero-fill: it hurries the in-flight fill,
    /// rolls immediately via <see cref="WalDirectory.CreateAllocatedFileAsync"/>, and rolls early to the staged
    /// zero-filled file as soon as it completes, with zero skipped file numbers and full recovery across reopen.
    /// </summary>
    [Fact]
    public async Task ARollWhilePreparationIsInFlightRollsImmediatelyToAnAllocatedFileThenSwitchesWhenReady()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage();
        var options = PacedOptions(time);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloads = new List<byte[]>();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using (var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken)) { PrepareGate = gate.Task })
        {
            await using var wal = await WriteAheadLog.CreateAsync(directory, options, TestContext.Current.CancellationToken);
            for (var page = 0; page < DataPages; page++)
            {
                var payload = Bytes(100, page);
                payloads.Add(payload);
                _ = await AppendAsync(wal, payload).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            await directory.PrepareStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.False(directory.PrepareCompleted.Task.IsCompleted);

            // Exceed file 1 while preparation is still gated: roll MUST complete immediately via CreateAllocatedFileAsync.
            var burstPayload = Bytes(100, payloads.Count);
            payloads.Add(burstPayload);
            var rolled = await AppendAsync(wal, burstPayload).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(2UL, rolled.File);
            Assert.True(wal.CurrentFileAllocated);
            Assert.Contains(2UL, directory.Allocated.Keys);
            Assert.False(directory.PrepareCompleted.Task.IsCompleted);

            // Release the gated preparation; once ready, the next append rolls early to it as file 3 (zero-filled).
            gate.SetResult();
            await directory.PrepareCompleted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var afterReady = Bytes(100, payloads.Count);
            payloads.Add(afterReady);
            var switched = await AppendAsync(wal, afterReady).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(3UL, switched.File);
            Assert.False(wal.CurrentFileAllocated);
            Assert.Contains(3UL, directory.Published.Keys);
            Assert.Equal([1UL, 2UL, 3UL], storage.ListFiles(LogPath));
        }

        Assert.Equal(0, storage.OpenHandleCount);
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(reopened);
        Assert.All(entries, static entry => Assert.True(entry.IsRecord));
        Assert.Equal(payloads, entries.Select(static entry => entry.Payload));
    }

    [Fact]
    public async Task DisposalCancelsAPacedFillImmediatelyWithoutPublishingOrLeakingTemporaries()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage();
        var options = PacedOptions(time);
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using (var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken)))
        {
            var wal = await WriteAheadLog.CreateAsync(directory, options, TestContext.Current.CancellationToken);
            await StartSlowPreparationAsync(wal, directory, time);
            var paceCallsBeforeDispose = directory.PaceCalls;

            await wal.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.True(directory.PrepareCancelled.Task.IsCompletedSuccessfully);
            Assert.False(directory.PrepareCompleted.Task.IsCompleted);
            Assert.Equal(paceCallsBeforeDispose, directory.PaceCalls);
        }

        // Disposal cancelled staging before publication: only file 1 exists, and no handle leaked.
        Assert.Equal([1UL], storage.ListFiles(LogPath));
        Assert.Equal(0, storage.OpenHandleCount);
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        Assert.Equal(DataPages / 4, (await ReadAllAsync(reopened)).Count);
    }

    [Fact]
    public async Task DisposalOnPortableStorageRemovesUnpublishedTemporaryFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "clog-wal-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new FakeTimeProvider();
            var storage = new PortableWalStorage();
            var options = PacedOptions(time);
            await storage.CreateDirectoryAsync(path, TestContext.Current.CancellationToken);
            await using (var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(path, TestContext.Current.CancellationToken)))
            {
                var wal = await WriteAheadLog.CreateAsync(directory, options, TestContext.Current.CancellationToken);
                await StartSlowPreparationAsync(wal, directory, time);
                await wal.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
                Assert.True(directory.PrepareCancelled.Task.IsCompletedSuccessfully);
            }

            FileHandleLeakCheck.AssertClosed(path);
            Assert.Empty(Directory.EnumerateFiles(path, "*" + WalFormat.TemporaryExtension));
            Assert.Equal(["00000000000000000001.clfl"], Directory.EnumerateFiles(path, "*" + WalFormat.FileExtension).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static WalOptions PacedOptions(TimeProvider time) =>
        Options(fileSize: 4096 * (DataPages + 1)) with { PreallocateFiles = true, TimeProvider = time };

    /// <summary>
    /// Large durable groups put the log in bulk mode, where a roll creates an allocated file without a zero-fill; small
    /// groups take it out, and it prepares a zero-filled file at once and rolls to it early. Every record recovers
    /// across a reopen, the allocated file's unused tail included (wal.md §8, "Files and preallocation").
    /// </summary>
    [Fact]
    public async Task LargeGroupsAllocateFilesAndSmallGroupsReturnToZeroFilledOnes()
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        var options = Options(fileSize: 16L << 20, window: 2048) with { PreallocateFiles = true, TimeProvider = new FakeTimeProvider() };
        var payloads = new List<byte[]>();
        await using (var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken)))
        {
            await using var wal = await WriteAheadLog.CreateAsync(directory, options, TestContext.Current.CancellationToken);
            async Task AppendRecordAsync(int length)
            {
                var payload = Bytes(length, payloads.Count);
                payloads.Add(payload);
                _ = await AppendAsync(wal, payload).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            // Each 4 MiB record is one group; the group average passes 3 MiB after eleven.
            while (!wal.BulkMode)
            {
                Assert.True(payloads.Count < 20, "Large groups never entered bulk mode.");
                await AppendRecordAsync(4 << 20);
            }

            while (!wal.CurrentFileAllocated)
            {
                Assert.True(payloads.Count < 40, "No roll created an allocated file in bulk mode.");
                await AppendRecordAsync(4 << 20);
            }

            Assert.NotEmpty(directory.Allocated);

            // Small records are one-page groups; the average falls below 1 MiB, the log prepares a zero-filled file, and
            // the next record after it is ready starts it.
            while (wal.CurrentFileAllocated)
            {
                Assert.True(payloads.Count < 10_000, "Small groups never left the allocated file.");
                await AppendRecordAsync(100);
            }

            Assert.False(wal.BulkMode);
            Assert.NotEmpty(directory.Published);
            await AppendRecordAsync(100);
        }

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(reopened);
        Assert.All(entries, static entry => Assert.True(entry.IsRecord));
        Assert.Equal(payloads, entries.Select(static entry => entry.Payload));
    }

    /// <summary>
    /// Fills a quarter of the first file over an hour of <paramref name="time"/>, so the next file's fill waits a
    /// second before each write, and waits until it does.
    /// </summary>
    private static async Task StartSlowPreparationAsync(WriteAheadLog wal, RecordingDirectory directory, FakeTimeProvider time)
    {
        for (var page = 1; page < DataPages / 4; page++)
        {
            _ = await AppendAsync(wal, Bytes(100, page));
        }

        time.Advance(TimeSpan.FromHours(1));
        _ = await AppendAsync(wal, Bytes(100, 0));
        await directory.Paced.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A directory which records staged preparations, allocations, publications, and paced waits.
    /// </summary>
    private sealed class RecordingDirectory(WalDirectory inner) : WalDirectory
    {
        private int _prepareCount;
        private int _paceCalls;

        public int PrepareCount => Volatile.Read(ref _prepareCount);

        public int PaceCalls => Volatile.Read(ref _paceCalls);

        public Task? PrepareGate { get; init; }

        public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PrepareCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PrepareCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Paced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentDictionary<ulong, bool> Allocated { get; } = new();

        public ConcurrentDictionary<ulong, bool> Published { get; } = new();

        public override int MinimumPageSize => inner.MinimumPageSize;

        public override string Path => inner.Path;

        public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken) =>
            inner.ListFilesAsync(cancellationToken);

        public override ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken)
        {
            _ = Allocated.TryAdd(file, true);
            return inner.CreateAllocatedFileAsync(file, header, length, cancellationToken);
        }

        public override async ValueTask<PreparedWalFile> PrepareFileAsync(
            long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _prepareCount);
            _ = PrepareStarted.TrySetResult();
            try
            {
                if (PrepareGate is { } gate)
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var result = await inner.PrepareFileAsync(preallocatedLength, pace is null ? null : Pace, cancellationToken).ConfigureAwait(false);
                _ = PrepareCompleted.TrySetResult();
                return result;
            }
            catch (OperationCanceledException)
            {
                _ = PrepareCancelled.TrySetResult();
                throw;
            }

            ValueTask Pace(long written, int count, CancellationToken token)
            {
                _ = Interlocked.Increment(ref _paceCalls);
                var waiting = pace(written, count, token);
                if (!waiting.IsCompleted)
                {
                    _ = Paced.TrySetResult();
                }

                return waiting;
            }
        }

        public override ValueTask<WalFile> PublishPreparedFileAsync(
            PreparedWalFile prepared, ulong file, ArcBuffer header, CancellationToken cancellationToken)
        {
            _ = Published.TryAdd(file, true);
            return inner.PublishPreparedFileAsync(prepared, file, header, cancellationToken);
        }

        public override ValueTask<WalFile> CreateFileAsync(
            ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken) =>
            inner.CreateFileAsync(file, header, preallocatedLength, pace, cancellationToken);

        public override ValueTask<WalFile> OpenFileAsync(ulong file, CancellationToken cancellationToken) =>
            inner.OpenFileAsync(file, cancellationToken);

        public override ValueTask DeleteFileAsync(ulong file, CancellationToken cancellationToken) =>
            inner.DeleteFileAsync(file, cancellationToken);

        public override ValueTask<WalFile> CreateNamedFileAsync(string name, ArcBuffer contents, CancellationToken cancellationToken) =>
            inner.CreateNamedFileAsync(name, contents, cancellationToken);

        public override ValueTask<WalFile> OpenNamedFileAsync(string name, CancellationToken cancellationToken) =>
            inner.OpenNamedFileAsync(name, cancellationToken);

        public override ValueTask<IReadOnlyList<string>> ListNamedFilesAsync(string extension, CancellationToken cancellationToken) =>
            inner.ListNamedFilesAsync(extension, cancellationToken);

        public override ValueTask DeleteNamedFileAsync(string name, CancellationToken cancellationToken) =>
            inner.DeleteNamedFileAsync(name, cancellationToken);

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
