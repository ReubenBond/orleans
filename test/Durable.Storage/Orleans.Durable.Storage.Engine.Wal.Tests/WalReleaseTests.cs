using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks releasing a prefix of files: only closed, durable files go, the log keeps working, and replay starts
/// at the first retained file.
/// </summary>
public sealed class WalReleaseTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    // Each 3,000-byte record fills most of a 4 KiB page, so a 4-page file holds three records.
    private static WalOptions SmallFiles => Options(fileSize: 4 * 4096) with { PreallocateFiles = false };

    /// <summary>
    /// Once a file's successor submits its first page, the predecessor is closed and durable and the log forgets it:
    /// an open which rolls through many files keeps at most two writers reachable, not one per file it ever wrote.
    /// </summary>
    [Fact]
    public async Task AnOpenKeepsNoWriterOfAFileItHasMovedPast()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        for (var index = 0; index < 60; index++)
        {
            _ = await AppendAsync(wal, Bytes(3_000, index));
        }

        Assert.True(wal.CurrentFile >= 20, $"The log reached file {wal.CurrentFile}.");
        Assert.InRange(wal.WriterChainLength, 1, 2);
    }

    [Fact]
    public async Task ReleasedFilesAreGoneAndTheRetainedSuffixRecoversAfterReopen()
    {
        var storage = new SimulatedWalStorage();
        var payloads = Enumerable.Range(0, 10).Select(static index => Bytes(3_000, index)).ToArray();
        var positions = new List<WalPosition>();
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken))
        {
            foreach (var payload in payloads)
            {
                positions.Add(await AppendAsync(wal, payload));
            }

            Assert.Equal(4ul, wal.CurrentFile);
            await WaitForFrontierAsync(wal, positions[^1]);
            await wal.ReleaseAsync(3, TestContext.Current.CancellationToken);
            Assert.Equal([3ul, 4ul], storage.ListFiles(LogPath));

            // Records in retained files still read, and appends continue.
            Assert.Equal(payloads[6], (await wal.ReadAsync(positions[6], TestContext.Current.CancellationToken)).ToBytes());
            Assert.Equal(payloads[6..], (await ReadAllAsync(wal)).Select(static entry => entry.Payload!));
            await wal.ReleaseAsync(3, TestContext.Current.CancellationToken);
            _ = await AppendAsync(wal, [42]);
        }

        // The first retained file is the start, though it did not begin an open.
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        Assert.Equal(5ul, reopened.CurrentFile);
        var entries = await ReadAllAsync(reopened);
        Assert.All(entries, static entry => Assert.True(entry.IsRecord));
        Assert.Equal([.. payloads[6..], [42]], entries.Select(static entry => entry.Payload));
        Assert.Equal([.. payloads[6..], [42]], (await ReadAllAsync(reopened, new WalPosition(3, 0))).Select(static entry => entry.Payload));
    }

    [Fact]
    public async Task TheCurrentFileAndFilesWithPagesInFlightAreNeverReleased()
    {
        var storage = new SimulatedWalStorage();
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        try
        {
            await AssertReleasesOnlyDurableClosedFilesAsync(storage, wal);
        }
        finally
        {
            await wal.DisposeAsync();
        }
    }

    private static async Task AssertReleasesOnlyDurableClosedFilesAsync(SimulatedWalStorage storage, WriteAheadLog wal)
    {
        var last = default(WalPosition);
        for (var index = 0; index < 4; index++)
        {
            last = await AppendAsync(wal, Bytes(3_000, index));
        }

        Assert.Equal(2ul, wal.CurrentFile);
        await WaitForFrontierAsync(wal, last);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wal.ReleaseAsync(3, TestContext.Current.CancellationToken));
        await wal.ReleaseAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal([2ul], storage.ListFiles(LogPath));

        // A closed file whose last group is still in flight holds pages which are not durable, so the frontier lies in it.
        storage.HoldWrites = true;
        var appends = Enumerable.Range(0, 3).Select(index => AppendAsync(wal, Bytes(3_000, 10 + index)).AsTask()).ToList();
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        appends.Add(AppendAsync(wal, Bytes(3_000, 13)).AsTask());
        while (wal.CurrentFile != 3)
        {
            await PauseAsync();
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wal.ReleaseAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal([2ul, 3ul], storage.ListFiles(LogPath));
        storage.HoldWrites = false;
        while (!appends.TrueForAll(static append => append.IsCompleted))
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        await WaitForFrontierAsync(wal, (await Task.WhenAll(appends)).Max());
        await wal.ReleaseAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal([3ul], storage.ListFiles(LogPath));
    }

    /// <summary>
    /// A file is released only once the frontier lies in a later file: until then, a record in it may still be completing
    /// <see cref="WalWriteRequest.OnDurable"/>, which delivers a position and page in that file to its caller, though
    /// every page of the file is durable (WAL-I5, conventions §13).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AFileIsReleasedOnlyOnceTheFrontierPassesEveryRecordInIt()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        using var first = Request(Bytes(3_000, 0));
        using var second = Request(Bytes(3_000, 1));
        using var gated = new GatedRequest(Bytes(3_000, 2));
        using var next = Request(Bytes(3_000, 3));
        await wal.WriteAsync(first, TestContext.Current.CancellationToken);
        await wal.WriteAsync(second, TestContext.Current.CancellationToken);
        await wal.WriteAsync(gated, TestContext.Current.CancellationToken);
        while (!gated.Entered.IsSet)
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        // File 1's pages are written, each once (WAL-I9), so the next record rolls to file 2.
        await wal.WriteAsync(next, TestContext.Current.CancellationToken);
        while (!next.Completion.IsCompleted)
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        // Every page of file 1 is durable and file 2 holds a durable record, but the gated record's callback is running.
        Assert.Equal(2ul, (await next.Completion).File);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await wal.ReleaseAsync(2, TestContext.Current.CancellationToken));
        Assert.Equal([1ul, 2ul], storage.ListFiles(LogPath));

        gated.Gate.Set();
        await WaitForFrontierAsync(wal, await next.Completion);
        await wal.ReleaseAsync(2, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(1ul, storage.ListFiles(LogPath));
    }

    /// <summary>
    /// Regression test: a release disposed and deleted its files one at a time, so a deletion which failed, as one does
    /// once the system crashes, left every later released file's handle open, outside the log, which no disposal closed.
    /// A release now closes every file it took from the log, whichever deletion fails.
    /// </summary>
    [Fact]
    public async Task AReleaseWhoseDeletionFailsClosesEveryReleasedFile()
    {
        var storage = new SimulatedWalStorage();
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        var last = default(WalPosition);
        for (var index = 0; index < 10; index++)
        {
            last = await AppendAsync(wal, Bytes(3_000, index));
        }

        Assert.Equal(4ul, wal.CurrentFile);
        await WaitForFrontierAsync(wal, last);
        _ = storage.Crash(operatingSystem: false);
        _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await wal.ReleaseAsync(4, TestContext.Current.CancellationToken));
        await wal.DisposeAsync();
        Assert.Equal(0, storage.OpenHandleCount);
    }

    /// <summary>
    /// Regression test: a release whose second deletion failed left that file and every later released one on disk but
    /// outside the log. A failed deletion is reported and stops the process (conventions.md §14) once the release has
    /// closed every file it took; the files not yet deleted stay a contiguous prefix on disk (WAL-I5), which the restarted
    /// process's open finds as the log's own, and a release then deletes them.
    /// </summary>
    [Fact]
    public async Task AReleaseWhoseSecondDeletionFailsLeavesAPrefixTheRestartedProcessFinds()
    {
        var storage = new SimulatedWalStorage();
        var payloads = Enumerable.Range(0, 10).Select(static index => Bytes(3_000, index)).ToArray();
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        var last = default(WalPosition);
        foreach (var payload in payloads)
        {
            last = await AppendAsync(wal, payload);
        }

        Assert.Equal(4ul, wal.CurrentFile);
        await WaitForFrontierAsync(wal, last);
        var handles = storage.OpenHandleCount;
        var failed = new IOException("The device failed the deletion.");
        storage.DeleteFault = (_, file) => file == 2 ? failed : null;
        var since = FatalErrors.ReportedCount;
        Assert.Same(failed, await Assert.ThrowsAsync<IOException>(async () => await wal.ReleaseAsync(4, TestContext.Current.CancellationToken)));
        Assert.Same(failed, AssertReported(since, FatalErrorSite.DeleteFile, LogFile(2)).Failure.Exception);
        Assert.Equal([2ul, 3ul, 4ul], storage.ListFiles(LogPath));
        Assert.Equal(handles - 3, storage.OpenHandleCount);
        await wal.DisposeAsync();
        Assert.Equal(0, storage.OpenHandleCount);

        var restarted = storage.Crash(operatingSystem: false);
        await using (var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, SmallFiles, TestContext.Current.CancellationToken))
        {
            var entries = await ReadAllAsync(reopened);
            Assert.DoesNotContain(entries, static entry => entry.IsCorruption);
            Assert.Equal(payloads[3..], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload));
            await reopened.ReleaseAsync(reopened.CurrentFile, TestContext.Current.CancellationToken);
            Assert.Equal([reopened.CurrentFile], restarted.ListFiles(LogPath));
        }

        Assert.Equal(0, restarted.OpenHandleCount);
    }

    /// <summary>
    /// A record larger than the staged prepared file rolls directly to its own allocated file without publishing an
    /// empty intermediate file, keeping the staged file for the next normal roll and keeping the directory's files a
    /// contiguous suffix across releases (WAL-I5).
    /// </summary>
    [Fact]
    public async Task APreparedFileARollPassesIsReusedWithoutPublishingAnEmptyFile()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(pageSize: 512, fileSize: 4 * 512) with { PreallocateFiles = true };

        // The large record rolls directly to file 2 (no skipped empty file!), and the small record rolls to the staged
        // prepared file as file 3.
        var large = Bytes(5 * 512, 1);
        var small = Bytes(500, 2);
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            Assert.Equal(2ul, (await AppendAsync(wal, large)).File);
            var last = await AppendAsync(wal, small);
            Assert.Equal(3ul, last.File);
            await WaitForFrontierAsync(wal, last);
            var scanned = await ReadAllAsync(wal);
            Assert.DoesNotContain(scanned, static entry => entry.IsCorruption);
            Assert.Equal([large, small], scanned.Select(static entry => entry.Payload));

            for (var bound = 2ul; bound <= 3; bound++)
            {
                await wal.ReleaseAsync(bound, TestContext.Current.CancellationToken);
                Assert.Equal(
                    Enumerable.Range((int)bound, 4 - (int)bound).Select(static file => (ulong)file),
                    storage.ListFiles(LogPath).Where(static file => file <= 3));
            }

            Assert.Equal([small], (await ReadAllAsync(wal)).Select(static entry => entry.Payload));
        }

        await using (var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            var entries = await ReadAllAsync(reopened);
            Assert.DoesNotContain(entries, static entry => entry.IsCorruption);
            Assert.Equal([small], entries.Select(static entry => entry.Payload));
        }

        Assert.Equal(0, storage.OpenHandleCount);
    }

    /// <summary>
    /// Regression test: a roll which encounters a staged prepared file whose preparation failed stops the log with that
    /// reported failure (conventions.md §14), creates no file past it, and the restarted process's open finds a
    /// contiguous log (§6).
    /// </summary>
    [Fact]
    public async Task ARollWhichPassesAFailedPreparedFileStopsTheLog()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(pageSize: 512, fileSize: 4 * 512) with { PreallocateFiles = true };

        var failed = new IOException("The staged preparation failed.");
        storage.PrepareFault = (_, _) => failed;
        var since = FatalErrors.ReportedCount;
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        Assert.Same(failed, (await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(wal, Bytes(5 * 512, 1)))).InnerException);
        await wal.DisposeAsync();
        Assert.Same(failed, AssertReported(since, FatalErrorSite.CreateFile, Path.Combine(LogPath, "prealloc.tmp")).Failure.Exception);
        Assert.DoesNotContain(2ul, storage.ListFiles(LogPath));
        Assert.Equal(0, storage.OpenHandleCount);

        var restarted = storage.Crash(operatingSystem: false);
        await using var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, options, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await ReadAllAsync(reopened), static entry => entry.IsCorruption);
        Assert.Equal(2ul, (await AppendAsync(reopened, [1])).File);
    }

    /// <summary>
    /// Regression test: a release cancelled after it took its files from the log left them on disk, outside the log,
    /// where no later release deleted them, so the directory's files stopped being a contiguous suffix (WAL-I5).
    /// Cancellation applies only before the release takes its files: a cancelled release leaves them the log's, readable,
    /// and a later release deletes them.
    /// </summary>
    [Fact]
    public async Task ACancelledReleaseLeavesItsFilesToALaterRelease()
    {
        var root = Directory.CreateTempSubdirectory("clog-wal-release-");
        var path = Path.Combine(root.FullName, LogPath);
        try
        {
            var payloads = Enumerable.Range(0, 10).Select(static index => Bytes(3_000, index)).ToArray();
            await using var wal = await WriteAheadLog.CreateAsync(new PortableWalStorage(), path, SmallFiles, TestContext.Current.CancellationToken);
            var positions = new List<WalPosition>();
            foreach (var payload in payloads)
            {
                positions.Add(await AppendAsync(wal, payload));
            }

            await WaitForFrontierAsync(wal, positions[^1]);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wal.ReleaseAsync(3, cancelled.Token));

            Assert.Equal(payloads[0], (await wal.ReadAsync(positions[0], TestContext.Current.CancellationToken)).ToBytes());
            Assert.Equal([1ul, 2ul, 3ul, 4ul], LogFiles(path));
            await wal.ReleaseAsync(3, TestContext.Current.CancellationToken);
            Assert.Equal([3ul, 4ul], LogFiles(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }

        static IEnumerable<ulong> LogFiles(string path) =>
            Directory.EnumerateFiles(path, "*" + WalFormat.FileExtension)
                .Select(static file => WalFormat.TryParseFileName(Path.GetFileName(file), out var number) ? number : 0)
                .Order();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACrashAfterAReleaseRecoversTheRetainedFiles(bool operatingSystem)
    {
        var storage = new SimulatedWalStorage();
        var payloads = Enumerable.Range(0, 8).Select(static index => Bytes(3_000, index)).ToArray();
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        var last = default(WalPosition);
        foreach (var payload in payloads)
        {
            last = await AppendAsync(wal, payload);
        }

        await WaitForFrontierAsync(wal, last);
        await wal.ReleaseAsync(2, TestContext.Current.CancellationToken);
        var restarted = storage.Crash(operatingSystem);
        await wal.DisposeAsync();

        Assert.Equal([2ul, 3ul], restarted.ListFiles(LogPath));
        await using (var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, SmallFiles, TestContext.Current.CancellationToken))
        {
            Assert.Equal(4ul, reopened.CurrentFile);
            Assert.Equal(payloads[3..], (await ReadAllAsync(reopened)).Select(static entry => entry.Payload));

            // The open fills files 4 and 5 and starts 6.
            for (var index = 0; index < 7; index++)
            {
                _ = await AppendAsync(reopened, Bytes(3_000, 100 + index));
            }

            Assert.Equal(6ul, reopened.CurrentFile);
        }

        // A gap within the retained files, which a later file of its open follows, is still damage.
        restarted.RemoveFile(LogPath, 5);
        await using var damaged = await WriteAheadLog.OpenAsync(restarted, LogPath, SmallFiles, TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(damaged);
        var corruption = Assert.Single(entries, static entry => entry.IsCorruption);
        Assert.Equal(new WalPosition(5, 0), corruption.Position);
        Assert.Equal(payloads[3..].Length + 4, entries.Count(static entry => entry.IsRecord));
    }

    /// <summary>
    /// A release whose deletion's directory flush fails is reported and stops the process (conventions.md §14), leaving
    /// the removal unsynced: an operating-system crash keeps or loses it, and either way the next open finds a contiguous
    /// suffix of files and every acknowledged record in it (WAL-I5, WAL-I12).
    /// </summary>
    [Fact]
    public async Task AReleaseWhoseDirectoryFlushFailsLeavesAContiguousSuffixAcrossAnOperatingSystemCrash()
    {
        var payloads = Enumerable.Range(0, 10).Select(static index => Bytes(3_000, index)).ToArray();
        var outcomes = new HashSet<bool>();
        for (var seed = 0; seed < 16; seed++)
        {
            var storage = new SimulatedWalStorage(seed);
            var wal = await WriteAheadLog.CreateAsync(storage, LogPath, SmallFiles, TestContext.Current.CancellationToken);
            var last = default(WalPosition);
            foreach (var payload in payloads)
            {
                last = await AppendAsync(wal, payload);
            }

            await WaitForFrontierAsync(wal, last);
            storage.DirectoryFlushFault = static _ => new IOException("The directory flush failed.");
            var since = FatalErrors.ReportedCount;
            _ = await Assert.ThrowsAsync<IOException>(async () => await wal.ReleaseAsync(4, TestContext.Current.CancellationToken));
            _ = AssertReported(since, FatalErrorSite.FlushDirectory, LogPath);
            var restarted = storage.Crash(operatingSystem: true);
            await wal.DisposeAsync();

            await using var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, SmallFiles, TestContext.Current.CancellationToken);
            var entries = await ReadAllAsync(reopened);
            Assert.DoesNotContain(entries, static entry => entry.IsCorruption);
            var kept = restarted.ListFiles(LogPath).Contains(1ul);
            _ = outcomes.Add(kept);
            Assert.Equal(kept ? payloads : payloads[3..], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload));
        }

        Assert.Equal([false, true], outcomes.Order());
    }

    /// <summary>
    /// A request whose durable callback signals <see cref="Entered"/> and runs until <see cref="Gate"/> is set.
    /// </summary>
    private sealed class GatedRequest : WalWriteRequest, IDisposable
    {
        public GatedRequest(byte[] payload) => Payload = Buffer(payload);

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Gate { get; } = new();

        public void Dispose()
        {
            Payload.Dispose();
            Entered.Dispose();
            Gate.Dispose();
        }

        protected internal override void OnDurable(WalDurableRecord record)
        {
            record.EnsureDurable();
            Entered.Set();
            _ = Gate.Wait(TimeSpan.FromSeconds(20));
        }

        protected internal override void OnFailed(Exception error) => Entered.Set();
    }
}
