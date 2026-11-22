using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// WAL-I3 (a file's name is published only with its durable header) at the crash cut between a file's creation under
/// its temporary name and its rename, and WAL-I9 (pages are written once) over every write the storage receives.
/// </summary>
public sealed class WalPublicationTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int PageSize = 4096;

    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 60)];

    /// <summary>
    /// A crash after a file was created under its temporary name, with any prefix of its header or with its whole
    /// header and preallocated zeros, but before its rename, leaves no published file: the directory lists only the
    /// earlier files, the next open deletes the temporary and publishes the same file number with its own header, and
    /// every record survives.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PageSize / 2)]
    [InlineData(PageSize - 1)]
    [InlineData(PageSize)]
    [InlineData(4 * PageSize)]
    public async Task ACrashBeforeTheRenameLeavesNoPublishedFileAndTheNextOpenRemovesTheTemporary(int temporaryLength)
    {
        var directory = Path.Combine(Path.GetTempPath(), "clog-wal-publish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options(pageSize: PageSize) with { PreallocateFiles = false };
            var storage = new PortableWalStorage();
            var payloads = new[] { Bytes(100, 1), Bytes(5000, 2), Bytes(7, 3) };
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                foreach (var payload in payloads)
                {
                    _ = await AppendAsync(wal, payload);
                }
            }

            var temporary = Path.Combine(directory, WalFormat.GetFileName(2) + WalFormat.TemporaryExtension);
            await File.WriteAllBytesAsync(temporary, TemporaryContents(temporaryLength), TestContext.Current.CancellationToken);
            Assert.Equal([WalFormat.GetFileName(1)], PublishedFiles(directory));

            await using (var reopened = await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                Assert.False(File.Exists(temporary));
                var entries = await ReadAllAsync(reopened);
                Assert.All(entries, static entry => Assert.True(entry.IsRecord));
                Assert.Equal(payloads, entries.Select(static entry => entry.Payload));
                Assert.Equal(2UL, (await AppendAsync(reopened, [9])).File);
            }

            Assert.Equal([WalFormat.GetFileName(1), WalFormat.GetFileName(2)], PublishedFiles(directory));
            Assert.Empty(Directory.GetFiles(directory, "*" + WalFormat.TemporaryExtension));
            var header = WalTestHelpers.ReadHeader((await File.ReadAllBytesAsync(Path.Combine(directory, WalFormat.GetFileName(2)), TestContext.Current.CancellationToken)).AsSpan(0, PageSize));
            Assert.Equal(new WalFileHeader(PageSize, options.WriteWindowPages, FirstOfOpen: true), header);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A crash while the first file of a new log existed only under its temporary name leaves a directory with no log
    /// files: opening it finds no log, and creating it starts the log afresh.
    /// </summary>
    [Fact]
    public async Task ACrashBeforeTheFirstFileIsRenamedLeavesNoLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clog-wal-publish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options(pageSize: PageSize) with { PreallocateFiles = false };
            var storage = new PortableWalStorage();
            _ = Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, WalFormat.GetFileName(1) + WalFormat.TemporaryExtension);
            await File.WriteAllBytesAsync(temporary, TemporaryContents(PageSize), TestContext.Current.CancellationToken);

            _ = await Assert.ThrowsAsync<WalNotFoundException>(
                async () => await WriteAheadLog.OpenAsync(storage, directory, options, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(temporary));

            await File.WriteAllBytesAsync(temporary, TemporaryContents(PageSize / 2), TestContext.Current.CancellationToken);
            await using (var wal = await WriteAheadLog.CreateAsync(storage, directory, options, TestContext.Current.CancellationToken))
            {
                Assert.Equal(new WalPosition(1, PageSize + WalFormat.FrameWordLength), await AppendAsync(wal, [1]));
            }

            Assert.Equal([WalFormat.GetFileName(1)], PublishedFiles(directory));
            Assert.Empty(Directory.GetFiles(directory, "*" + WalFormat.TemporaryExtension));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Across rounds of appending and then crashing the process or the operating system, or closing, every write the
    /// storage receives covers whole data pages: an open writes only pages of its own files, and each of them once; a
    /// replay writes only hole pages, only into files of earlier opens, and only over pages which were not valid when
    /// the crash happened; and a replay after a close writes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task EachDataPageIsWrittenOnceAndReplayWritesOnlyHolePagesOverInvalidPages(int seed)
    {
        var random = new Random(seed);
        var pageSize = random.Next(2) == 0 ? 512 : 1024;
        var groups = random.Next(1, 4);
        var options = new WalOptions
        {
            PageSize = pageSize,
            FileSize = pageSize * random.Next(3, 12),
            WriteWindowPages = random.Next(1, 6),
            MinWriteGroupsInFlight = groups,
            MaxWriteGroupsInFlight = groups,
            PreallocateFiles = random.Next(2) == 0,
        };
        var writes = new List<(ulong File, long Offset, int Length)>();
        var written = new HashSet<(ulong File, long Offset)>();
        var storage = Record(new SimulatedWalStorage(seed, sectorSize: 256) { HoldWrites = true }, writes);
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var openFirst = 1UL;
        try
        {
            var rounds = random.Next(2, 6);
            for (var round = 0; round < rounds; round++)
            {
                var appends = new List<Task<WalPosition>>();
                var steps = random.Next(5, 40);
                for (var step = 0; step < steps; step++)
                {
                    if (random.Next(3) == 0)
                    {
                        _ = storage.CompleteWrite();
                    }
                    else
                    {
                        var payload = Bytes(Math.Max(random.Next(0, 3 * pageSize), sizeof(int)), (round << 16) | step);
                        appends.Add(AppendAsync(wal, payload).AsTask());
                    }

                    await Task.Yield();
                }

                var crash = random.Next(3);
                if (crash == 0)
                {
                    storage.HoldWrites = false;
                    while (storage.CompleteWrite())
                    {
                        await Task.Yield();
                    }

                    _ = await Task.WhenAll(appends);
                    await wal.DisposeAsync();
                }
                else
                {
                    // Completing the latest write while earlier ones are in flight leaves invalid pages before a valid
                    // one, which an operating-system crash makes replay replace with hole pages.
                    (ulong File, long Offset, int Length)? latest;
                    lock (writes)
                    {
                        latest = writes.Count > 0 ? writes[^1] : null;
                    }

                    if (latest is { } last && random.Next(2) == 0)
                    {
                        _ = storage.CompleteWrite((name, file, offset) => name is null && file == last.File && offset == last.Offset);
                    }

                    storage = storage.Crash(operatingSystem: crash == 2);
                    await wal.DisposeAsync();

                    await SettleAsync(appends);
                }

                CheckOpenWrites(TakeWrites(writes), openFirst, pageSize, written);

                // The durable bytes the crash left, before replay flushes and repairs them.
                var files = storage.ListFiles(LogPath);
                var before = files.ToDictionary(static file => file, file => storage.ReadDurable(LogPath, file));
                storage = Record(storage, writes);
                storage.HoldWrites = false;
                wal = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
                openFirst = files.Max() + 1;

                var replay = TakeWrites(writes);
                if (crash == 0)
                {
                    Assert.Empty(replay);
                }

                foreach (var (file, offset, length) in replay)
                {
                    Assert.True(file < openFirst, Describe($"Replay wrote file {file}, which is not a file of an earlier open."));
                    AssertWholeDataPages(offset, length, pageSize);
                    var after = storage.ReadDurable(LogPath, file);
                    for (var page = offset; page < offset + length; page += pageSize)
                    {
                        Assert.False(
                            before[file].Length >= page + pageSize && WalFormat.IsValidPage(before[file].AsSpan((int)page, pageSize)),
                            Describe($"Replay overwrote the valid page at {page} of file {file}."));
                        Assert.True(WalFormat.IsHolePage(after.AsSpan((int)page, pageSize)), Describe($"Replay wrote a page other than a hole page at {page} of file {file}."));
                        _ = written.Add((file, page));
                    }
                }

                storage.HoldWrites = true;
            }
        }
        finally
        {
            storage.HoldWrites = false;
            while (storage.CompleteWrite())
            {
                await Task.Yield();
            }

            await wal.DisposeAsync();
        }

        CheckOpenWrites(TakeWrites(writes), openFirst, pageSize, written);
    }

    /// <summary>
    /// When an operating-system crash leaves earlier pages in flight before a later durable page, replay writes a hole
    /// page over exactly the invalid pages within the write window before the last valid page, and nothing else.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task ReplayWritesHolePagesOverExactlyTheInvalidPagesBeforeTheLastValidPage(int seed)
    {
        const int pageSize = 512;
        var options = Options(pageSize: pageSize, fileSize: 64 * pageSize, window: 4, groups: 3);
        var writes = new List<(ulong File, long Offset, int Length)>();
        var storage = Record(new SimulatedWalStorage(seed, sectorSize: 256) { HoldWrites = true }, writes);
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);

        // Each record fills one page, which is its own write group; the latest completes while the others are in flight.
        var appends = new List<Task<WalPosition>>();
        for (var index = 1; index <= 3; index++)
        {
            appends.Add(AppendAsync(wal, Bytes(pageSize - WalFormat.CrcLength - WalFormat.FrameWordLength, index)).AsTask());
            await WalFormatTests.WaitForPendingAsync(storage, index);
        }

        var latest = TakeWrites(writes)[^1];
        Assert.True(storage.CompleteWrite((name, file, offset) => name is null && file == latest.File && offset == latest.Offset));
        var restarted = storage.Crash(operatingSystem: true);
        await wal.DisposeAsync();

        foreach (var append in appends)
        {
            try
            {
                _ = await append;
            }
            catch (IOException)
            {
                // Only the latest record's page became durable, and it is not yet acknowledged.
            }
        }

        var before = restarted.ReadDurable(LogPath, 1);
        var last = Enumerable.Range(0, before.Length / pageSize - 1)
            .Last(page => WalFormat.IsValidPage(before.AsSpan(pageSize * (1 + page), pageSize)));
        Assert.Equal(latest.Offset, pageSize * (1L + last));
        var expected = Enumerable.Range(Math.Max(0, last - options.WriteWindowPages + 1), last - Math.Max(0, last - options.WriteWindowPages + 1))
            .Select(static page => pageSize * (1L + page))
            .Where(offset => !WalFormat.IsValidPage(before.AsSpan((int)offset, pageSize)))
            .ToArray();

        await using var reopened = await WriteAheadLog.OpenAsync(Record(restarted, writes), LogPath, options, TestContext.Current.CancellationToken);
        var replay = TakeWrites(writes);

        Assert.All(replay, static write => Assert.Equal(1UL, write.File));
        var holes = replay.SelectMany(static write => Enumerable.Range(0, write.Length / pageSize).Select(page => write.Offset + (long)page * pageSize)).Order().ToArray();
        Assert.Equal(expected, holes);
        var after = restarted.ReadDurable(LogPath, 1);
        Assert.All(holes, offset => Assert.True(WalFormat.IsHolePage(after.AsSpan((int)offset, pageSize))));
    }

    /// <summary>
    /// Checks one open's writes: whole data pages of its own files, none written before.
    /// </summary>
    private static void CheckOpenWrites(
        List<(ulong File, long Offset, int Length)> writes,
        ulong openFirst,
        int pageSize,
        HashSet<(ulong File, long Offset)> written)
    {
        foreach (var (file, offset, length) in writes)
        {
            Assert.True(file >= openFirst, Describe($"An open wrote file {file}, which an earlier open created."));
            AssertWholeDataPages(offset, length, pageSize);
            for (var page = offset; page < offset + length; page += pageSize)
            {
                Assert.True(written.Add((file, page)), Describe($"The page at {page} of file {file} was written twice."));
            }
        }
    }

    private static void AssertWholeDataPages(long offset, int length, int pageSize)
    {
        Assert.True(offset >= pageSize, Describe($"A write at {offset} reached the header page."));
        Assert.True(offset % pageSize == 0 && length > 0 && length % pageSize == 0, Describe($"The write of {length} bytes at {offset} is not whole pages."));
    }

    private static string Describe(FormattableString message) => message.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static SimulatedWalStorage Record(SimulatedWalStorage storage, List<(ulong File, long Offset, int Length)> writes)
    {
        storage.WriteFault = (_, file, offset, length) =>
        {
            lock (writes)
            {
                writes.Add((file, offset, length));
            }

            return null;
        };
        return storage;
    }

    private static List<(ulong File, long Offset, int Length)> TakeWrites(List<(ulong File, long Offset, int Length)> writes)
    {
        lock (writes)
        {
            var taken = writes.ToList();
            writes.Clear();
            return taken;
        }
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes of a file being created: a valid first-of-open header page, then zeros.
    /// </summary>
    private static byte[] TemporaryContents(int length)
    {
        var file = new byte[Math.Max(length, PageSize)];
        WalFormat.WriteHeader(file.AsSpan(0, PageSize), 256, firstOfOpen: true);
        return file[..length];
    }

    private static string[] PublishedFiles(string directory) =>
        [
            .. Directory.GetFiles(directory)
                .Select(static path => Path.GetFileName(path))
                .Where(static name => name.EndsWith(WalFormat.FileExtension, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];
}
