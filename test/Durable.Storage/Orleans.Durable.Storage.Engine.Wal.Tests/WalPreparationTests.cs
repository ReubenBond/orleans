using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Preparation of the next preallocated file: when it starts, how its zero-fill is paced, and how a roll or disposal
/// hurries it (wal.md §8, "Files and preallocation").
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

        Assert.DoesNotContain(2UL, directory.Started.Keys);
        _ = await AppendAsync(wal, Bytes(100, 0));
        await directory.Started.GetOrAdd(2, static _ => new TaskCompletionSource()).Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARollHurriesAPacedFill()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken));
        await using var wal = await WriteAheadLog.CreateAsync(directory, PacedOptions(time), TestContext.Current.CancellationToken);
        await StartSlowPreparationAsync(wal, directory, time);

        // The clock no longer moves, so the fill would wait forever; filling the file rolls to it regardless.
        WalPosition last = default;
        for (var page = DataPages / 4; page <= DataPages; page++)
        {
            last = await AppendAsync(wal, Bytes(100, (int)page)).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2UL, last.File);
        Assert.Contains(2UL, storage.ListFiles(LogPath));
    }

    [Fact]
    public async Task DisposalHurriesAPacedFill()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage();
        var options = PacedOptions(time);
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using (var directory = new RecordingDirectory(await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken)))
        {
            var wal = await WriteAheadLog.CreateAsync(directory, options, TestContext.Current.CancellationToken);
            await StartSlowPreparationAsync(wal, directory, time);
            await wal.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        // The prepared file holds no data page; the next open truncates it to its header.
        Assert.Equal([1UL, 2UL], storage.ListFiles(LogPath));
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        Assert.Equal(DataPages / 4, (await ReadAllAsync(reopened)).Count);
        Assert.Equal(4096, storage.ReadDurable(LogPath, 2).Length);
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
    /// A directory which records the log files whose creation started and signals the first paced write.
    /// </summary>
    private sealed class RecordingDirectory(WalDirectory inner) : WalDirectory
    {
        public ConcurrentDictionary<ulong, TaskCompletionSource> Started { get; } = new();

        public TaskCompletionSource Paced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override int MinimumPageSize => inner.MinimumPageSize;

        public override string Path => inner.Path;

        public override ValueTask<IReadOnlyList<ulong>> ListFilesAsync(CancellationToken cancellationToken) =>
            inner.ListFilesAsync(cancellationToken);

        public ConcurrentDictionary<ulong, bool> Allocated { get; } = new();

        public override ValueTask<WalFile> CreateAllocatedFileAsync(ulong file, ArcBuffer header, long length, CancellationToken cancellationToken)
        {
            _ = Allocated.TryAdd(file, true);
            _ = Started.GetOrAdd(file, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return inner.CreateAllocatedFileAsync(file, header, length, cancellationToken);
        }

        public override ValueTask<WalFile> CreateFileAsync(
            ulong file, ArcBuffer header, long preallocatedLength, WalFillPace? pace, CancellationToken cancellationToken)
        {
            _ = Started.GetOrAdd(file, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return inner.CreateFileAsync(file, header, preallocatedLength, pace is null ? null : Pace, cancellationToken);

            ValueTask Pace(long written, int count, CancellationToken token)
            {
                var waiting = pace(written, count, token);
                if (!waiting.IsCompleted)
                {
                    _ = Paced.TrySetResult();
                }

                return waiting;
            }
        }

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
