using System.Buffers.Binary;
using CsCheck;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Pins record pages (WAL-I8), page reads by key (WAL-I2), scan starts beyond the replayed WAL (WAL-I6),
/// caller-owned directories, and named files.
/// </summary>
public sealed class WalPageTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void PagesFromDurabilityScansAndKeyReadsAgreeForAnyRecordSizes()
    {
        Gen.Int[1, 1_400].Array[1, 40].Sample(
            lengths => _leaks.InScope(() => RoundTripAsync(lengths).GetAwaiter().GetResult()),
            iter: 25,
            threads: 1);

        static async Task RoundTripAsync(int[] lengths)
        {
            var storage = new SimulatedWalStorage();
            var options = Options(pageSize: 512, fileSize: 8 * 512, window: 8);
            var written = new List<(ulong Key, byte[] Bytes, WalPosition Position, WalPage Page)>();
            await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
            {
                var requests = new List<(ulong Key, byte[] Bytes, RecordingRequest Request)>();
                for (var index = 0; index < lengths.Length; index++)
                {
                    var bytes = Keyed((ulong)index, lengths[index]);
                    var request = Request(bytes);
                    await wal.WriteAsync(request, TestContext.Current.CancellationToken);
                    requests.Add(((ulong)index, bytes, request));
                }

                foreach (var (key, bytes, request) in requests)
                {
                    written.Add((key, bytes, await request.Completion, request.Page));
                    request.Dispose();
                }
            }

            await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
            var scanned = new List<WalRecord>();
            await foreach (var entry in reopened.ReadAllAsync(default, TestContext.Current.CancellationToken))
            {
                Assert.True(entry.TryGetValue(out WalRecord record));
                scanned.Add(record with { Payload = default });
            }

            Assert.Equal(written.Select(record => (record.Position, record.Page)), scanned.Select(record => (record.Position, record.Page)));
            foreach (var (key, bytes, position, page) in written)
            {
                Assert.Equal(bytes, (await reopened.ReadAsync(page, key, TestContext.Current.CancellationToken)).ToBytes());
                Assert.Equal(bytes, (await reopened.ReadAsync(position, TestContext.Current.CancellationToken)).ToBytes());
            }
        }
    }

    /// <summary>
    /// Records packed on one page are told apart by key, including a record whose First frame holds fewer than 8
    /// bytes, so its key continues on the next page; among equal keys the last record wins.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task KeyReadsDistinguishPackedRecordsAndKeysWhichSpanPages()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var options = Options(pageSize: 512, window: 64, groups: 1);
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);

        // The first record's group occupies the only group slot, so the next records share one open page.
        using var blocker = Request(Keyed(100, 20));
        await wal.WriteAsync(blocker, TestContext.Current.CancellationToken);
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        using var first = Request(Keyed(1, 100));
        using var duplicate = Request(Keyed(1, 50));

        // 4 (CRC) + 104 + 54 + 4 + length leaves 12 + 3 bytes: the next record's First frame holds 3 bytes.
        using var filler = Request(Keyed(2, 512 - 4 - 104 - 54 - 4 - 15));
        using var spanning = Request(Keyed(3, 900));
        foreach (var request in new[] { first, duplicate, filler, spanning })
        {
            await wal.WriteAsync(request, TestContext.Current.CancellationToken);
        }

        storage.HoldWrites = false;
        while (!spanning.Completion.IsCompleted)
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        var page = first.Page;
        Assert.All([duplicate, filler, spanning], request => Assert.Equal(page, request.Page));
        Assert.Equal(Keyed(1, 50), (await wal.ReadAsync(page, 1, TestContext.Current.CancellationToken)).ToBytes());
        Assert.Equal(Keyed(2, 512 - 4 - 104 - 54 - 4 - 15), (await wal.ReadAsync(page, 2, TestContext.Current.CancellationToken)).ToBytes());
        Assert.Equal(Keyed(3, 900), (await wal.ReadAsync(page, 3, TestContext.Current.CancellationToken)).ToBytes());
        Assert.True((await wal.ReadAsync(page, 4, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption _));

        // The spanning record may be the last match for any key, so a damaged next page makes the page unreadable.
        var next = new WalCorruption(
            new WalPosition(page.File, 512 * (2 + page.Index)),
            new WalPosition(page.File, 512 * (3 + page.Index)));
        storage.Damage(LogPath, page.File, 512 * (2 + page.Index) + 40, [1, 2, 3]);
        foreach (var key in new ulong[] { 1, 3, 4 })
        {
            Assert.True((await wal.ReadAsync(page, key, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption corruption));
            Assert.Equal(next, corruption);
        }
    }

    /// <summary>
    /// A record whose continuation a crash left beyond its file's end was torn and never acknowledged, so it is no
    /// match: the earlier records on its first page stay readable by key.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task KeyReadsSkipARecordTornAtTheFileEnd()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var options = Options(pageSize: 512, window: 64, groups: 1);
        WalPage page;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            using var blocker = Request(Keyed(100, 20));
            await wal.WriteAsync(blocker, TestContext.Current.CancellationToken);
            while (storage.PendingWriteCount == 0)
            {
                await PauseAsync();
            }

            // 4 (CRC) + 104 + 4 + length leaves 12 + 3 bytes: the spanning record's First frame holds 3 bytes.
            using var first = Request(Keyed(1, 100));
            using var filler = Request(Keyed(2, 512 - 4 - 104 - 4 - 15));
            using var spanning = Request(Keyed(3, 900));
            foreach (var request in new[] { first, filler, spanning })
            {
                await wal.WriteAsync(request, TestContext.Current.CancellationToken);
            }

            storage.HoldWrites = false;
            while (!spanning.Completion.IsCompleted)
            {
                _ = storage.CompleteWrite();
                await PauseAsync();
            }

            page = first.Page;
            Assert.Equal(page, spanning.Page);
        }

        storage.TruncateFile(LogPath, page.File, 512 * (2L + page.Index));
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        Assert.Equal(Keyed(1, 100), (await reopened.ReadAsync(page, 1, TestContext.Current.CancellationToken)).ToBytes());
        Assert.Equal(Keyed(2, 512 - 4 - 104 - 4 - 15), (await reopened.ReadAsync(page, 2, TestContext.Current.CancellationToken)).ToBytes());
        Assert.True((await reopened.ReadAsync(page, 3, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption _));
    }

    /// <summary>
    /// A keyed read names only a settled record: a record whose key lies wholly on the page but whose continuation is
    /// not yet durable in this open, on any of its pages, is no match, so the read names the earlier record with the
    /// key, and the later one once its continuation is durable (conventions §13).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task KeyReadsSkipARecordWhoseContinuationIsNotYetDurable()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };

        // With a window of one page, a page opens only once the page before it is durable.
        var options = Options(pageSize: 512, window: 1, groups: 1);
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        using var blocker = Request(Keyed(100, 20));
        await wal.WriteAsync(blocker, TestContext.Current.CancellationToken);
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        // 4 (CRC) + 104 leaves 404 bytes: the later record's First frame holds 392, its key among them.
        using var earlier = Request(Keyed(1, 100));
        using var later = Request(Keyed(1, 900));
        await wal.WriteAsync(earlier, TestContext.Current.CancellationToken);
        await wal.WriteAsync(later, TestContext.Current.CancellationToken);

        // The blocker's page, then the page holding the earlier record and the later record's First frame.
        Assert.True(storage.CompleteWrite());
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        Assert.True(storage.CompleteWrite());
        _ = await earlier.Completion;

        var page = earlier.Page;
        Assert.False(later.Completion.IsCompleted);
        Assert.Equal(Keyed(1, 100), (await wal.ReadAsync(page, 1, TestContext.Current.CancellationToken)).ToBytes());

        // The later record's first continuation page becomes durable, and its last is in flight: it is still no match.
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        Assert.True(storage.CompleteWrite());
        while (storage.PendingWriteCount == 0)
        {
            await PauseAsync();
        }

        Assert.False(later.Completion.IsCompleted);
        Assert.Equal(Keyed(1, 100), (await wal.ReadAsync(page, 1, TestContext.Current.CancellationToken)).ToBytes());

        while (!later.Completion.IsCompleted)
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        Assert.Equal(page, later.Page);
        Assert.Equal(Keyed(1, 900), (await wal.ReadAsync(page, 1, TestContext.Current.CancellationToken)).ToBytes());
    }

    /// <summary>
    /// In a final open, a record whose last continuation page is a hole page, which replay wrote over a page a crash
    /// left incomplete, or lies past its file's end, was torn and is no match, even when its key lies wholly on its first
    /// page: the read names the earlier record with the key.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyReadsSkipARecordTornOnItsLastPage(bool truncated)
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var options = Options(pageSize: 512, window: 64, groups: 1);
        WalPage page;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            using var blocker = Request(Keyed(100, 20));
            await wal.WriteAsync(blocker, TestContext.Current.CancellationToken);
            while (storage.PendingWriteCount == 0)
            {
                await PauseAsync();
            }

            // 4 (CRC) + 104 leaves 404 bytes: the torn record's First frame holds 392, and its two continuation pages 504
            // and 404; the last record starts on the second and continues on the pages after it.
            using var earlier = Request(Keyed(1, 100));
            using var torn = Request(Keyed(1, 1300));
            using var last = Request(Keyed(2, 600));
            foreach (var request in new[] { earlier, torn, last })
            {
                await wal.WriteAsync(request, TestContext.Current.CancellationToken);
            }

            storage.HoldWrites = false;
            while (!last.Completion.IsCompleted)
            {
                _ = storage.CompleteWrite();
                await PauseAsync();
            }

            page = earlier.Page;
            Assert.Equal(page, torn.Page);
        }

        // The torn record's second continuation page reads as a crash leaves an unfinished write, so replay writes a hole
        // page over it; or the file ends before it.
        if (truncated)
        {
            storage.TruncateFile(LogPath, page.File, 512 * (3L + page.Index));
        }
        else
        {
            storage.Damage(LogPath, page.File, 512 * (3L + page.Index) + 40, [1, 2, 3]);
        }

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        Assert.Equal(Keyed(1, 100), (await reopened.ReadAsync(page, 1, TestContext.Current.CancellationToken)).ToBytes());
    }

    [Fact]
    public async Task KeyReadsReportEveryUnreadablePageAsCorruption()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(pageSize: 512);
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        using var request = Request(Keyed(7, 100));
        await wal.WriteAsync(request, TestContext.Current.CancellationToken);
        _ = await request.Completion;
        var page = request.Page;

        Assert.True((await wal.ReadAsync(page with { File = 99 }, 7, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption _));
        Assert.True((await wal.ReadAsync(page with { Index = 1_000_000 }, 7, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption _));
        Assert.True((await wal.ReadAsync(page, 8, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption _));

        storage.Damage(LogPath, page.File, 512 * (1 + page.Index) + 40, [1, 2, 3]);
        Assert.True((await wal.ReadAsync(page, 7, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption corruption));
        Assert.Equal(new WalPosition(page.File, 512 * (1 + page.Index)), corruption.Start);
    }

    /// <summary>
    /// A scan which starts beyond what survives of the last file of an open, or in a file which no longer exists
    /// before a new open, starts in that open's lost tail and reports nothing; starting exactly at the replayed end
    /// reports nothing either.
    /// </summary>
    [Fact]
    public async Task ScansStartingInALostTailReportNothing()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(pageSize: 512);
        WalPosition end;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            await WaitForFrontierAsync(wal, await AppendAsync(wal, Bytes(100, 1)));
            end = wal.DurableFrontier;
        }

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var replayedEnd = new WalPosition(end.File, 2 * 512);
        Assert.Empty(await ReadAllAsync(reopened, replayedEnd));
        Assert.Empty(await ReadAllAsync(reopened, end));

        Assert.Empty(await ReadAllAsync(reopened, new WalPosition(end.File, 5 * 512)));
        Assert.Empty(await ReadAllAsync(reopened, new WalPosition(end.File + 100, 512)));
    }

    /// <summary>
    /// Files of one open are numbered consecutively, so a scan reports a file lost from the middle of an open
    /// as corruption between the records around it, and an absent start file as a gap up to the next file.
    /// </summary>
    [Fact]
    public async Task ScansReportAFileLostWithinAnOpen()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(pageSize: 512, fileSize: 4 * 512);
        var positions = new List<WalPosition>();
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            for (var index = 0; index < 12; index++)
            {
                positions.Add(await AppendAsync(wal, Bytes(400, index)));
            }
        }

        var files = positions.Select(static position => position.File).Distinct().ToArray();
        Assert.True(files.Length >= 3, $"The records span only {files.Length} files.");
        var lost = files[1];
        storage.RemoveFile(LogPath, lost);

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var observed = await ReadAllAsync(reopened);
        var gap = new WalEntry(new WalCorruption(new WalPosition(lost, 0), new WalPosition(lost + 1, 0)));
        Assert.Equal([gap], observed.Where(static entry => entry.Fault is not null).Select(static entry => entry.Fault));
        Assert.Equal(
            positions.Where(position => position.File != lost),
            observed.Where(static entry => entry.Payload is not null).Select(static entry => entry.Position));

        var fromLost = await ReadAllAsync(reopened, new WalPosition(lost, 512));
        Assert.Equal(new WalEntry(new WalCorruption(new WalPosition(lost, 512), new WalPosition(lost + 1, 0))), fromLost[0].Fault);
        Assert.Equal(positions.Where(position => position.File > lost), fromLost.Skip(1).Select(static entry => entry.Position));
    }

    /// <summary>
    /// A new open's first file takes at least the minimum number, so a lost file's number is never reused, and
    /// scans skip the numbers skipped as an earlier open's lost tail.
    /// </summary>
    [Fact]
    public async Task AnOpenStartsAtLeastAtTheMinimumFirstFile()
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        WalPosition first;
        await using (var wal = await WriteAheadLog.CreateAsync(directory, Options(), TestContext.Current.CancellationToken))
        {
            first = await AppendAsync(wal, Bytes(10, 1));
        }

        var reopened = await WriteAheadLog.OpenAsync(directory, Options(), minimumFirstFile: first.File + 50, TestContext.Current.CancellationToken);
        var second = await AppendAsync(reopened, Bytes(10, 2));
        Assert.True(second.File >= first.File + 50, $"The new open started at file {second.File}.");
        await WaitForFrontierAsync(reopened, second);
        Assert.Equal([first, second], (await ReadAllAsync(reopened)).Select(static entry => entry.Position));
        await reopened.DisposeAsync();

        // A scan starting in the lost tail itself reports nothing either.
        await using var again = await WriteAheadLog.OpenAsync(directory, Options(), minimumFirstFile: 0, TestContext.Current.CancellationToken);
        Assert.Equal([first, second], (await ReadAllAsync(again)).Select(static entry => entry.Position));
        Assert.Equal([second], (await ReadAllAsync(again, new WalPosition(first.File + 10, 4096))).Select(static entry => entry.Position));
    }

    /// <summary>
    /// <see cref="WriteAheadLog.DurableFrontier"/> passes a record only after its <see cref="WalWriteRequest.OnDurable"/>
    /// returns, which is the cut a slot-table checkpoint relies on.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheFrontierNeverPassesARecordWhoseDurableCallbackIsRunning()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var blocking = new BlockingRequest(entered, gate);
        await wal.WriteAsync(blocking, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var frontier = wal.DurableFrontier;
        Assert.True(frontier <= blocking.Position, $"The frontier {frontier} passed the record at {blocking.Position}.");

        gate.Set();
        await WaitForFrontierAsync(wal, blocking.Position);
    }

    /// <summary>
    /// An append completes from its record's <see cref="WalWriteRequest.OnDurable"/>, and
    /// <see cref="WriteAheadLog.DurableFrontier"/> passes the record once every outcome callback of its batch has returned
    /// (WAL-I8), so a caller which reads the frontier after an append waits for it to pass the record (issue #97).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAppendCompletesBeforeTheFrontierPassesItsRecord()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        var first = AppendAsync(wal, [1]).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var second = AppendAsync(wal, [2]).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 2);

        // Both write groups are in flight, so the append and the blocking request share data page 2, and one batch
        // delivers their outcomes, the append's first.
        var appended = AppendAsync(wal, [3]).AsTask();
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var blocking = new BlockingRequest(entered, gate);
        await wal.WriteAsync(blocking, TestContext.Current.CancellationToken);

        var shared = WalFormat.DataPageOffset(4096, 2);
        while (!(first.IsCompleted && second.IsCompleted))
        {
            _ = storage.CompleteWrite((name, _, offset) => name is null && offset < shared);
            await PauseAsync();
        }

        _ = await first;
        await WaitForFrontierAsync(wal, await second);
        var settled = wal.DurableFrontier;
        while (!storage.CompleteWrite((name, _, offset) => name is null && offset == shared))
        {
            await PauseAsync();
        }

        var position = await appended;
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(new WalPosition(1, (ulong)shared + 4), position);
        Assert.Equal(new WalPosition(1, (ulong)shared + 4 + 4 + 1), blocking.Position);
        Assert.Equal(settled, wal.DurableFrontier);

        gate.Set();
        await WaitForFrontierAsync(wal, blocking.Position);
        Assert.True(wal.DurableFrontier > position);
    }

    [Fact]
    public async Task ALogOverACallerOwnedDirectoryLeavesItOpenAfterDisposal()
    {
        var storage = new SimulatedWalStorage();
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using (var wal = await WriteAheadLog.CreateAsync(directory, Options(), TestContext.Current.CancellationToken))
        {
            _ = await AppendAsync(wal, Bytes(10, 1));
        }

        using (var contents = Buffer([1, 2, 3]))
        {
            await (await directory.CreateNamedFileAsync("after.bin", contents, TestContext.Current.CancellationToken)).DisposeAsync();
        }

        await using var reopened = await WriteAheadLog.OpenAsync(directory, Options(), minimumFirstFile: 0, TestContext.Current.CancellationToken);
        Assert.Equal(Bytes(10, 1), Assert.Single(await ReadAllAsync(reopened)).Payload);
        Assert.Equal(["after.bin"], await directory.ListNamedFilesAsync(".bin", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NamedFilesAreDurableExclusiveAndKeptApartFromLogFiles()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "clog-wal-files-" + Guid.NewGuid().ToString("N"));
        var storages = new (WalStorage Storage, string Path)[] { (new SimulatedWalStorage(), LogPath), (new PortableWalStorage(), temporary) };
        try
        {
            foreach (var (storage, path) in storages)
            {
                await AssertNamedFilesAsync(storage, path);
            }
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static async Task AssertNamedFilesAsync(WalStorage storage, string path)
    {
        await storage.CreateDirectoryAsync(path, TestContext.Current.CancellationToken);
        await using var directory = await storage.OpenDirectoryAsync(path, TestContext.Current.CancellationToken);
        using var b = Buffer([1, 2]);
        using var a = Buffer([3]);
        using var c = Buffer([4]);
        using var again = Buffer([5]);
        await (await directory.CreateNamedFileAsync("b.chk", b, TestContext.Current.CancellationToken)).DisposeAsync();
        await (await directory.CreateNamedFileAsync("a.chk", a, TestContext.Current.CancellationToken)).DisposeAsync();
        await (await directory.CreateNamedFileAsync("c.other", c, TestContext.Current.CancellationToken)).DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(async () => await directory.CreateNamedFileAsync("a.chk", again, TestContext.Current.CancellationToken));
        Assert.Equal(["a.chk", "b.chk"], await directory.ListNamedFilesAsync(".chk", TestContext.Current.CancellationToken));
        Assert.Empty(await directory.ListFilesAsync(TestContext.Current.CancellationToken));

        await using (var file = await directory.OpenNamedFileAsync("b.chk", TestContext.Current.CancellationToken))
        {
            using var bytes = await file.ReadAsync(0, 10, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2 }, bytes.ToArray());
        }

        await directory.DeleteNamedFileAsync("b.chk", TestContext.Current.CancellationToken);
        await directory.DeleteNamedFileAsync("missing.chk", TestContext.Current.CancellationToken);
        Assert.Equal(["a.chk"], await directory.ListNamedFilesAsync(".chk", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await directory.OpenNamedFileAsync("b.chk", TestContext.Current.CancellationToken));
        foreach (var invalid in new[] { "", "lock", "x/y", "x\\y", "00000000000000000001.clfl", "x.tmp" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await directory.CreateNamedFileAsync(invalid, a, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ACreatedNamedFileSurvivesAnOperatingSystemCrashAndAnUnfinishedWriteTears()
    {
        var storage = new SimulatedWalStorage(seed: 3);
        await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        await using var directory = await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        using var contents = Buffer(new byte[4096]);
        await using var file = await directory.CreateNamedFileAsync("root", contents, TestContext.Current.CancellationToken);
        storage.HoldWrites = true;
        using var bytes = Buffer(Bytes(4096, 9));
        var write = file.WriteAsync(0, new[] { bytes });
        var restarted = storage.Crash(operatingSystem: true);
        await Assert.ThrowsAsync<CrashedProcessException>(async () => await write);

        Assert.Equal(["root"], restarted.ListNamedFiles(LogPath));
        var durable = restarted.ReadDurableFile(LogPath, "root")!;
        Assert.Equal(4096, durable.Length);
    }

    /// <summary>
    /// The simulated storage models a name change whose directory flush failed: the failure is reported and stops the
    /// process (conventions §14), and an operating-system crash keeps or loses the change until a restarted process's
    /// open flushes the directory, as the file-system directories do.
    /// </summary>
    [Fact]
    public async Task ANameChangeWhoseFlushFailedIsUnsyncedUntilARestartedProcessOpensTheDirectory()
    {
        var outcomes = new HashSet<bool>();
        for (var seed = 0; seed < 16; seed++)
        {
            _ = outcomes.Add(await SurvivesAsync(seed, reopen: false));
            Assert.True(await SurvivesAsync(seed, reopen: true));
        }

        Assert.Equal([false, true], outcomes.Order());

        static async Task<bool> SurvivesAsync(int seed, bool reopen)
        {
            var storage = new SimulatedWalStorage(seed);
            await storage.CreateDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
            await using (var directory = await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken))
            {
                using var contents = Buffer([7]);
                storage.DirectoryFlushFault = static _ => new IOException("The directory flush failed.");
                var since = FatalErrors.ReportedCount;
                _ = await Assert.ThrowsAsync<IOException>(async () => await directory.CreateNamedFileAsync("a.chk", contents, TestContext.Current.CancellationToken));
                _ = AssertReported(since, FatalErrorSite.FlushDirectory, LogPath);
            }

            var restarted = storage.Crash(operatingSystem: false);
            if (reopen)
            {
                await (await restarted.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken)).DisposeAsync();
            }

            return restarted.Crash(operatingSystem: true).ListNamedFiles(LogPath).Contains("a.chk");
        }
    }

    [Fact]
    public void FilesLargerThan2To32DataPagesAreRejected()
    {
        var options = Options(pageSize: 512, fileSize: (1L << 32) * 512 + 1024);
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        (options with { FileSize = (1L << 32) * 512 }).Validate();
    }

    private static byte[] Keyed(ulong key, int length)
    {
        var bytes = Bytes(Math.Max(length, sizeof(ulong)), (int)key + length);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, key);
        return bytes;
    }

    private sealed class BlockingRequest : WalWriteRequest, IDisposable
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _gate;

        public BlockingRequest(ManualResetEventSlim entered, ManualResetEventSlim gate)
        {
            _entered = entered;
            _gate = gate;
            Payload = Buffer(Bytes(64, 5));
        }

        public WalPosition Position { get; private set; }

        protected internal override void OnDurable(WalDurableRecord record)
        {
            Position = record.Position;
            _entered.Set();
            _ = _gate.Wait(TimeSpan.FromSeconds(20));
        }

        public void Dispose() => Payload.Dispose();

        protected internal override void OnFailed(Exception error) => _entered.Set();
    }
}
