using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks the crash-state enumerator's persistence model on hand-made traces, and the directory implementation's
/// publication contracts (WAL-I3, WAL-I10, named files, deletion, directories) in every crash state its recorded trace
/// allows (verification.md §7.5).
/// </summary>
public sealed class CrashStateTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recording-root"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AnUnflushedWriteIsLostKeptTornOrZeroExtendedAndAFlushedOneSurvivesWhole()
    {
        var fileSystem = new RecordingFileSystem(Root);
        var bytes = Enumerable.Range(0, 1024).Select(static index => (byte)(index % 255 + 1)).ToArray();
        using (var handle = fileSystem.CreateFile(At("a"), replace: false, writeThrough: false, preallocationSize: 0))
        {
            fileSystem.FlushDirectory(Root, DirectoryEntries.Files);
            await handle.WriteAsync(bytes, 0, Token);

            // Lost, kept, zero-extended, or torn to its first or its last sector.
            byte[] zeros = new byte[512];
            Assert.Equal(
                new[] { string.Empty, Hex(bytes), Hex([.. zeros, .. zeros]), Hex(bytes[..512]), Hex([.. zeros, .. bytes[512..]]) }.Order(StringComparer.Ordinal),
                Contents(fileSystem, "a").Order(StringComparer.Ordinal));
            handle.Flush();
        }

        Assert.Equal([Hex(bytes)], Contents(fileSystem, "a"));
    }

    [Fact]
    public async Task AFileFlushLeavesItsNameUnsyncedAndADirectoryFlushLeavesItsOwnName()
    {
        var fileSystem = new RecordingFileSystem(Root);
        fileSystem.CreateDirectory(At("d"));
        using (var handle = fileSystem.CreateFile(At("d/f"), replace: false, writeThrough: false, preallocationSize: 0))
        {
            await handle.WriteAsync(new byte[] { 1, 2, 3 }, 0, Token);
            handle.Flush();
        }

        Assert.Equal([null, "010203"], Contents(fileSystem, "d/f").Distinct().Order());
        fileSystem.FlushDirectory(At("d"), DirectoryEntries.Files);
        Assert.Equal([null, "010203"], Contents(fileSystem, "d/f").Distinct().Order());
        fileSystem.FlushDirectory(Root, DirectoryEntries.Directories);
        Assert.Equal(["010203"], Contents(fileSystem, "d/f"));
    }

    /// <summary>
    /// A write-through write's bytes are durable once it completes, and the length it extends once a later flush of
    /// its file completes; a flush covers only writes which completed before it.
    /// </summary>
    [Fact]
    public async Task AWriteThroughWritesBytesAreDurableOnceItCompletesAndItsLengthOnceItsFileIsFlushed()
    {
        var fileSystem = new RecordingFileSystem(Root);
        using var handle = fileSystem.CreateFile(At("a"), replace: false, writeThrough: true, preallocationSize: 0);
        fileSystem.FlushDirectory(Root, DirectoryEntries.Files);

        await handle.WriteAsync(new byte[] { 1, 2, 3 }, 0, Token);
        Assert.Equal([string.Empty, "010203"], Contents(fileSystem, "a").Order(StringComparer.Ordinal));
        handle.Flush();
        Assert.Equal(["010203"], Contents(fileSystem, "a"));

        // Within the durable length, a completed write is durable alone.
        await handle.WriteAsync(new byte[] { 4 }, 1, Token);
        Assert.Equal(["010403"], Contents(fileSystem, "a"));

        fileSystem.HoldWrites = static _ => true;
        var write = handle.WriteAsync(new byte[] { 5 }, 2, Token);
        handle.Flush();
        Assert.Equal(["010403", "010405"], Contents(fileSystem, "a").Order(StringComparer.Ordinal));
        Assert.Single(fileSystem.HeldWrites).Complete();
        await write;
        Assert.Equal(["010405"], Contents(fileSystem, "a"));
    }

    /// <summary>
    /// A write-through write which fails, or whose process crashes, is direct, so the device holds what reads of it
    /// return: it is durable once a later flush of its file completes, and a flush while it is in flight covers none of
    /// it. The failure is reported and stops its process (conventions.md §14), so the flush which makes it durable is
    /// the restarted process's.
    /// </summary>
    [Fact]
    public async Task AWriteThroughWriteWhichFailsIsDurableOnceALaterFlushOfItsFileCompletes()
    {
        var fileSystem = new RecordingFileSystem(Root);
        using var handle = fileSystem.CreateFile(At("a"), replace: false, writeThrough: true, preallocationSize: 0);
        fileSystem.FlushDirectory(Root, DirectoryEntries.Files);
        await handle.WriteAsync(new byte[] { 1, 2, 3 }, 0, Token);
        handle.Flush();

        fileSystem.HoldWrites = static _ => true;
        var failed = handle.WriteAsync(new byte[] { 4 }, 1, Token);
        handle.Flush();
        var since = FatalErrors.ReportedCount;
        Assert.Single(fileSystem.HeldWrites).Fail();
        var failure = await Assert.ThrowsAsync<IOException>(async () => await failed);
        Assert.Same(failure, WalTestHelpers.AssertReported(since, FatalErrorSite.HandleWrite, At("a")).Failure.Exception);
        Assert.Equal(["010203", "010403"], Contents(fileSystem, "a").Order(StringComparer.Ordinal));

        // The restarted process's open flushes the file, as the WAL's opens do.
        var restarted = fileSystem.Restart();
        restarted.HoldWrites = static _ => true;
        using var reopened = restarted.OpenFile(At("a"), direct: true);
        reopened.Flush();
        Assert.Equal(["010403"], Contents(restarted, "a"));

        var crashed = reopened.WriteAsync(new byte[] { 5 }, 2, Token);
        var final = restarted.Restart();
        _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await crashed);
        Assert.Equal(["010403", "010405"], Contents(final, "a").Order(StringComparer.Ordinal));
        using (var flushed = final.OpenFile(At("a"), direct: true))
        {
            flushed.Flush();
        }

        Assert.Equal(["010405"], Contents(final, "a"));
    }

    /// <summary>
    /// A write group whose write fails leaves its pages on the device, where the restarted process's open later in the
    /// same boot reads them. The failure is reported and stops the log (conventions.md §14). The open flushes every file
    /// before reading, so every record its scan reports survives every crash state after it (WAL-I10, conventions §13).
    /// </summary>
    [Fact]
    public async Task AReopenAfterAFailedWriteReportsOnlyRecordsEveryLaterCrashKeeps()
    {
        var fileSystem = new RecordingFileSystem(Root, writeThrough: true);
        var options = WalTestHelpers.Options(fileSize: 16 * 4096, groups: 1) with { PreallocateFiles = false };
        var log = await WriteAheadLog.CreateAsync(fileSystem.Storage, At("wal"), options, Token);
        _ = await WalTestHelpers.AppendAsync(log, [1, 2, 3]);
        fileSystem.HoldWrites = static path => path.EndsWith(WalFormat.FileExtension, StringComparison.Ordinal);
        var failing = WalTestHelpers.AppendAsync(log, [4, 5, 6]).AsTask();
        while (fileSystem.HeldWrites.Count == 0)
        {
            await WalTestHelpers.PauseAsync();
        }

        fileSystem.HoldWrites = null;
        var since = FatalErrors.ReportedCount;
        fileSystem.HeldWrites[0].Fail();
        _ = await Assert.ThrowsAsync<IOException>(() => failing);
        await log.DisposeAsync();
        _ = WalTestHelpers.AssertReported(since, FatalErrorSite.HandleWriteGather, At("wal/" + WalFormat.GetFileName(1)));

        var restarted = fileSystem.Restart();
        List<WalTestHelpers.Observed> reported;
        await using (var reopened = await WriteAheadLog.OpenAsync(restarted.Storage, At("wal"), options, Token))
        {
            reported = await WalTestHelpers.ReadAllAsync(reopened);
        }

        Assert.Equal([[1, 2, 3], [4, 5, 6]], reported.Select(static entry => entry.Payload));
        var point = restarted.OperationCount;
        var enumerator = CrashStateEnumerator.Of(restarted, seed: 0);
        foreach (var state in enumerator.Enumerate(point, point, int.MaxValue).States)
        {
            var replayed = new RecordingFileSystem(Root, enumerator.Build(state), writeThrough: true);
            await using var reopened = await WriteAheadLog.OpenAsync(replayed.Storage, At("wal"), options, Token);
            var entries = await WalTestHelpers.ReadAllAsync(reopened);
            Assert.True(
                reported.Select(static entry => (entry.Position, Hex(entry.Payload!)))
                    .SequenceEqual(entries.Where(static entry => entry.IsRecord).Select(static entry => (entry.Position, Hex(entry.Payload!)))),
                enumerator.Describe(state));
        }
    }

    /// <summary>
    /// A device error at any operation of a log's life, from its directory's creation through appends which roll to new
    /// files and a release, is reported once and stops the process (conventions.md §14): the call which met it fails, and
    /// the restarted process's open finds no damage, every acknowledged record the release did not take, and no record no
    /// append wrote (WAL-I5, WAL-I8, WAL-I10, conventions §13).
    /// </summary>
    [Fact]
    public async Task AFailureAtAnyOperationIsReportedOnceAndTheRestartedProcessKeepsEveryAcknowledgedRecord()
    {
        var options = WalTestHelpers.Options(fileSize: 4 * 4096) with { PreallocateFiles = false };
        var clean = new RecordingFileSystem(Root, writeThrough: true);
        await LiveAsync(clean, options, new Life());
        var reported = new SortedSet<FatalErrorSite>();
        for (var failAt = 0; failAt < clean.OperationCount; failAt++)
        {
            var fileSystem = new RecordingFileSystem(Root, writeThrough: true) { FailOperation = failAt };
            var life = new Life();
            var since = FatalErrors.ReportedCount;
            _ = await Assert.ThrowsAnyAsync<IOException>(() => LiveAsync(fileSystem, options, life));
            _ = reported.Add(Assert.Single(FatalErrors.TakeMine(since)).Site);

            var restarted = fileSystem.Restart();
            List<WalTestHelpers.Observed> entries;
            try
            {
                await using var reopened = await WriteAheadLog.OpenAsync(restarted.Storage, At("wal"), options, Token);
                entries = await WalTestHelpers.ReadAllAsync(reopened);
            }
            catch (Exception exception) when (life.Acknowledged.Count == 0 && exception is WalNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            var failed = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The device error at operation {failAt}");
            Assert.False(entries.Exists(static entry => entry.IsCorruption), failed + " left damage.");
            foreach (var (position, payload) in life.Acknowledged.Where(record => !life.Releasing || record.Position.File >= Life.ReleaseBound))
            {
                Assert.True(entries.Exists(entry => entry.Position == position && entry.Payload.AsSpan().SequenceEqual(payload)), failed + " lost an acknowledged record.");
            }

            foreach (var entry in entries.Where(static entry => entry.IsRecord))
            {
                Assert.True(life.Appended.Exists(payload => payload.AsSpan().SequenceEqual(entry.Payload)), failed + " left a record no append wrote.");
            }
        }

        Assert.Equal(
            [
                FatalErrorSite.CreateDirectory,
                FatalErrorSite.OpenLock,
                FatalErrorSite.CreateFile,
                FatalErrorSite.MoveFile,
                FatalErrorSite.DeleteFile,
                FatalErrorSite.FlushDirectory,
                FatalErrorSite.HandleWriteGather,
                FatalErrorSite.HandleFlush,
            ],
            reported);
    }

    /// <summary>
    /// The portable storage's file makes each completed write durable with the length needed to read it: on macOS by a
    /// sync after every write, and on Linux and Windows by direct, write-through writes and a sync after a write which
    /// extends the file. Every crash state keeps every completed write.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APortableFilesCompletedWritesSurviveEveryCrash(bool writeThrough)
    {
        const int Page = WalDirectIo.Alignment;
        var fileSystem = new RecordingFileSystem(Root, writeThrough: writeThrough);
        var name = "wal/" + WalFormat.GetFileName(1);

        // The third write fills a gap within the length the first two made durable.
        var completed = new List<(int Point, long Offset, byte[] Bytes)>();
        using var header = Buffer(new byte[Page]);
        await fileSystem.Storage.CreateDirectoryAsync(At("wal"), Token);
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(At("wal"), Token))
        await using (var file = await directory.CreateFileAsync(1, header, 0, null, Token))
        {
            foreach (var (offset, value) in new[] { (Page, (byte)0xA1), (3L * Page, (byte)0xB2), (2L * Page, (byte)0xC3) })
            {
                byte[] bytes = [.. Enumerable.Repeat(value, Page)];
                using var buffer = AlignedBuffer(bytes);
                await file.WriteAsync(offset, new[] { buffer });
                completed.Add((fileSystem.OperationCount, offset, bytes));
            }
        }

        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        foreach (var state in enumerator.Enumerate(completed[0].Point, fileSystem.OperationCount, int.MaxValue).States)
        {
            var contents = enumerator.Build(state).ReadFile(name)!;
            foreach (var (_, offset, bytes) in completed.Where(write => write.Point <= state.Point))
            {
                Assert.True(contents.Length >= offset + bytes.Length && contents.AsSpan((int)offset, bytes.Length).SequenceEqual(bytes), enumerator.Describe(state));
            }
        }
    }

    /// <summary>
    /// A cap below two states per point still keeps, at every point, the image with every unsynced operation lost and
    /// the image with every one kept.
    /// </summary>
    [Fact]
    public async Task ACapBelowTwoStatesPerPointKeepsEveryPointsStrictestAndMostCompleteImages()
    {
        var fileSystem = new RecordingFileSystem(Root);
        using (var handle = fileSystem.CreateFile(At("a"), replace: false, writeThrough: false, preallocationSize: 0))
        {
            fileSystem.FlushDirectory(Root, DirectoryEntries.Files);
            for (var index = 0; index < 6; index++)
            {
                await handle.WriteAsync(new[] { (byte)(index + 1) }, index, Token);
            }
        }

        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        var last = fileSystem.OperationCount;
        var all = enumerator.Enumerate(0, last, int.MaxValue);
        var capped = enumerator.Enumerate(0, last, limit: 1);
        Assert.True(all.Distinct > 2 * all.Points);
        Assert.Equal(2 * capped.Points, capped.States.Count);
        for (var point = 0; point <= last; point++)
        {
            var unsynced = all.States.First(state => state.Point == point).Unsynced;
            var images = capped.States.Where(state => state.Point == point).Select(state => enumerator.Build(state).Fingerprint()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(enumerator.Build(new CrashState(point, unsynced, new int[unsynced.Count])).Fingerprint(), images);
            Assert.Contains(enumerator.Build(new CrashState(point, unsynced, [.. Enumerable.Repeat(1, unsynced.Count)])).Fingerprint(), images);
        }
    }

    [Fact]
    public void UnsyncedEntriesPersistInEverySubset()
    {
        var fileSystem = new RecordingFileSystem(Root);
        fileSystem.CreateFile(At("a"), replace: false, writeThrough: false, preallocationSize: 0).Dispose();
        fileSystem.CreateFile(At("b"), replace: false, writeThrough: false, preallocationSize: 0).Dispose();
        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        var states = enumerator.Enumerate(2, 2, int.MaxValue).States
            .Select(state => string.Join(',', new[] { "a", "b" }.Where(name => enumerator.Build(state).ReadFile(name) is not null)))
            .Order(StringComparer.Ordinal);
        Assert.Equal([string.Empty, "a", "a,b", "b"], states);
    }

    /// <summary>
    /// Every point keeps its own images: an image which also arose at an earlier point is judged again against what
    /// the later point acknowledged, so a write still unflushed is lost at the last point too.
    /// </summary>
    [Fact]
    public async Task EveryPointKeepsItsOwnImagesSoAnUnflushedWriteIsLostAtTheLastPoint()
    {
        var fileSystem = new RecordingFileSystem(Root);
        using (var handle = fileSystem.CreateFile(At("a"), replace: false, writeThrough: false, preallocationSize: 0))
        {
            fileSystem.FlushDirectory(Root, DirectoryEntries.Files);
            await handle.WriteAsync(new byte[] { 1 }, 0, Token);
        }

        fileSystem.FlushDirectory(Root, DirectoryEntries.Files);
        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        var last = fileSystem.OperationCount;
        Assert.Contains(
            enumerator.Enumerate(0, last, int.MaxValue).States,
            state => state.Point == last && enumerator.Build(state).ReadFile("a") is [] && state.Unsynced.Count == 1);
    }

    /// <summary>
    /// With more unsynced operations than one point enumerates in full, every operation is still kept in some state.
    /// </summary>
    [Fact]
    public void EveryUnsyncedOperationCanSurviveBeyondTenAtAPoint()
    {
        var fileSystem = new RecordingFileSystem(Root);
        var names = Enumerable.Range(0, 11).Select(static index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var name in names)
        {
            fileSystem.CreateFile(At(name), replace: false, writeThrough: false, preallocationSize: 0).Dispose();
        }

        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        var images = enumerator.Enumerate(names.Length, names.Length, int.MaxValue).States.Select(enumerator.Build).ToList();
        Assert.All(names, name => Assert.Contains(images, image => image.ReadFile(name) is not null));
    }

    [Fact]
    public void OperationsInADurablyRemovedDirectoryAreUnreachable()
    {
        var fileSystem = new RecordingFileSystem(Root);
        fileSystem.CreateDirectory(At("d"));
        fileSystem.CreateFile(At("d/x"), replace: false, writeThrough: false, preallocationSize: 0).Dispose();
        fileSystem.CreateFile(At("d/y"), replace: false, writeThrough: false, preallocationSize: 0).Dispose();
        fileSystem.DeleteDirectory(At("d"));
        fileSystem.FlushDirectory(Root, DirectoryEntries.Directories);
        var states = CrashStateEnumerator.Of(fileSystem, seed: 0).Enumerate(fileSystem.OperationCount, fileSystem.OperationCount, int.MaxValue);
        Assert.Equal(1, states.Enumerated);
    }

    /// <summary>
    /// Creating a directory makes its name durable before it returns, by flushing its parent, the root: a directory flush
    /// leaves the directory's own name unsynced. A trailing separator names the same directory.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("a/")]
    public async Task ACreatedDirectorySurvivesEveryCrash(string path)
    {
        var fileSystem = new RecordingFileSystem(Root);
        await fileSystem.Storage.CreateDirectoryAsync(At(path), Token);
        var point = fileSystem.OperationCount;
        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        foreach (var state in enumerator.Enumerate(point, point, int.MaxValue).States)
        {
            Assert.True(new RecordingFileSystem(Root, enumerator.Build(state)).DirectoryExists(At("a")), enumerator.Describe(state));
        }
    }

    /// <summary>
    /// A directory is created only under an existing parent, which is its caller's root or lies beneath it, so creating
    /// one creates and flushes nothing else (wal.md §3).
    /// </summary>
    [Fact]
    public async Task CreatingADirectoryWhoseParentIsMissingCreatesNothing()
    {
        var fileSystem = new RecordingFileSystem(Root);
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await fileSystem.Storage.CreateDirectoryAsync(At("a/b"), Token));
        Assert.Empty(fileSystem.Operations);
    }

    /// <summary>
    /// A process which crashed between creating a directory and flushing its parent left the name unsynced in the page
    /// cache. A restarted process which creates the directory again, accepting it, makes the name durable before it
    /// returns, so an operating-system crash afterwards keeps it (WAL-I10).
    /// </summary>
    [Theory]
    [InlineData("d")]
    [InlineData("d/")]
    public async Task ADirectoryACrashedProcessCreatedIsDurableOnceARestartedProcessCreatesItAgain(string path)
    {
        var crashed = new RecordingFileSystem(Root) { CrashAfter = 1 };
        _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await crashed.Storage.CreateDirectoryAsync(At("d"), Token));
        Assert.Equal(FileSystemOperationKind.MakeDirectory, Assert.Single(crashed.Operations).Kind);

        var restarted = crashed.Restart();
        await restarted.Storage.CreateDirectoryAsync(At(path), Token);
        AssertEveryState(restarted, static image => new RecordingFileSystem(Root, image).DirectoryExists(At("d")));
    }

    /// <summary>
    /// A deletion whose directory flush failed stopped its process (conventions §14) and left the removal unsynced. A
    /// restarted process which deletes the directory again, though it is gone, flushes its parent, so the removal is
    /// durable once the repeated deletion returns.
    /// </summary>
    [Fact]
    public async Task ADeletionARestartedProcessRepeatsIsDurableOnceItReturns()
    {
        var fileSystem = new RecordingFileSystem(Root);
        await fileSystem.Storage.CreateDirectoryAsync(At("d"), Token);
        fileSystem.FailFlushes = static path => path.Length == 0;
        var since = FatalErrors.ReportedCount;
        _ = await Assert.ThrowsAsync<IOException>(async () => await fileSystem.Storage.DeleteDirectoryAsync(At("d"), Token));
        _ = WalTestHelpers.AssertReported(since, FatalErrorSite.FlushDirectory, Root);
        var restarted = fileSystem.Restart();
        Assert.False(restarted.DirectoryExists(At("d")));

        await restarted.Storage.DeleteDirectoryAsync(At("d"), Token);
        AssertEveryState(restarted, static image => !new RecordingFileSystem(Root, image).DirectoryExists(At("d")));
    }

    /// <summary>
    /// A creation or deletion whose directory flush failed stopped its process (conventions §14) and left its name change
    /// unsynced. A restarted process's open flushes the directory, so its listing, an open which finds no file, and a
    /// refusal of an existing name each hold in every crash state, for log files and named files alike (WAL-I10, WAL-I12).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswersAfterAFailedFlushStoppedTheProcessHoldInEveryCrashState(bool named)
    {
        var fileSystem = new RecordingFileSystem(Root);
        await fileSystem.Storage.CreateDirectoryAsync(At("wal"), Token);
        using var contents = AlignedBuffer([.. Enumerable.Repeat((byte)7, 4096)]);

        string Name(ulong number) => named ? $"{number}.chk" : WalFormat.GetFileName(number);
        async Task CreateAsync(WalDirectory directory, ulong number) => await (named
            ? await directory.CreateNamedFileAsync(Name(number), contents, Token)
            : await directory.CreateFileAsync(number, contents, 0, null, Token)).DisposeAsync();
        async Task DeleteAsync(WalDirectory directory, ulong number)
        {
            if (named)
            {
                await directory.DeleteNamedFileAsync(Name(number), Token);
            }
            else
            {
                await directory.DeleteFileAsync(number, Token);
            }
        }

        async Task OpenAsync(WalDirectory directory, ulong number) => await (named
            ? await directory.OpenNamedFileAsync(Name(number), Token)
            : await directory.OpenFileAsync(number, Token)).DisposeAsync();
        async Task<string[]> ListAsync(WalDirectory directory) => named
            ? [.. await directory.ListNamedFilesAsync(".chk", Token)]
            : [.. (await directory.ListFilesAsync(Token)).Select(WalFormat.GetFileName)];
        bool Holds(FileSystemImage image, ulong number) => image.ReadFile("wal/" + Name(number)) is { Length: 4096 };

        // Runs a change whose directory flush fails, which is reported and stops the process, and returns the restarted
        // process's view.
        async Task<RecordingFileSystem> FailThenRestartAsync(RecordingFileSystem current, Func<WalDirectory, Task> change)
        {
            await using (var directory = await current.Storage.OpenDirectoryAsync(At("wal"), Token))
            {
                current.FailFlushes = static path => path == "wal";
                var since = FatalErrors.ReportedCount;
                _ = await Assert.ThrowsAsync<IOException>(() => change(directory));
                _ = WalTestHelpers.AssertReported(since, FatalErrorSite.FlushDirectory, At("wal"));
            }

            return current.Restart();
        }

        fileSystem = await FailThenRestartAsync(fileSystem, directory => CreateAsync(directory, 1));
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(At("wal"), Token))
        {
            Assert.Equal([Name(1)], await ListAsync(directory));
        }

        AssertEveryState(fileSystem, image => Holds(image, 1));

        fileSystem = await FailThenRestartAsync(fileSystem, directory => DeleteAsync(directory, 1));
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(At("wal"), Token))
        {
            _ = await Assert.ThrowsAsync<FileNotFoundException>(() => OpenAsync(directory, 1));
        }

        AssertEveryState(fileSystem, image => image.ReadFile("wal/" + Name(1)) is null);

        fileSystem = await FailThenRestartAsync(fileSystem, directory => CreateAsync(directory, 2));
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(At("wal"), Token))
        {
            _ = await Assert.ThrowsAsync<WalNameExistsException>(() => CreateAsync(directory, 2));
        }

        AssertEveryState(fileSystem, image => Holds(image, 2));
    }

    /// <summary>
    /// A storage which cannot serve a file refuses it while it is still a temporary: the refusal is reported and stops
    /// the process (conventions.md §14), the refused creation publishes no name in any crash state, and the restarted
    /// process's open deletes the temporary it left (wal.md §9).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACreationTheStorageRefusesPublishesNothing(bool named)
    {
        var fileSystem = new RecordingFileSystem(Root);
        await fileSystem.Storage.CreateDirectoryAsync(At("wal"), Token);
        var name = named ? "a.chk" : WalFormat.GetFileName(1);
        var temporary = At("wal/" + name + WalFormat.TemporaryExtension);
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(At("wal"), Token))
        {
            using var contents = AlignedBuffer([.. Enumerable.Repeat((byte)7, 4096)]);
            fileSystem.RefuseOpens = static path => path.EndsWith(WalFormat.TemporaryExtension, StringComparison.Ordinal);
            var since = FatalErrors.ReportedCount;
            _ = await Assert.ThrowsAsync<IOException>(async () => await (named
                ? await directory.CreateNamedFileAsync(name, contents, Token)
                : await directory.CreateFileAsync(1, contents, 0, null, Token)).DisposeAsync());
            _ = WalTestHelpers.AssertReported(since, FatalErrorSite.OpenFile, temporary);
        }

        AssertEveryState(fileSystem, image => image.ReadFile("wal/" + name) is null);
        var restarted = fileSystem.Restart();
        Assert.True(restarted.FileExists(temporary));
        await (await restarted.Storage.OpenDirectoryAsync(At("wal"), Token)).DisposeAsync();
        Assert.False(restarted.FileExists(temporary));
        Assert.False(restarted.FileExists(At("wal/" + name)));
    }

    /// <summary>
    /// A directory deletion whose parent flush failed stopped its process (conventions §14) and left the removal
    /// unsynced. A restarted process's listing of the missing directory, and an open which finds it missing, flush its
    /// parent before they report its absence, so every crash state after them lacks it (WAL-I12).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbsentDirectoryIsReportedOnlyOnceItsAbsenceIsDurable(bool open)
    {
        var fileSystem = new RecordingFileSystem(Root);
        await fileSystem.Storage.CreateDirectoryAsync(At("d"), Token);
        fileSystem.FailFlushes = static path => path.Length == 0;
        var since = FatalErrors.ReportedCount;
        _ = await Assert.ThrowsAsync<IOException>(async () => await fileSystem.Storage.DeleteDirectoryAsync(At("d"), Token));
        _ = WalTestHelpers.AssertReported(since, FatalErrorSite.FlushDirectory, Root);
        var restarted = fileSystem.Restart();
        if (open)
        {
            _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await restarted.Storage.OpenDirectoryAsync(At("d"), Token));
        }
        else
        {
            Assert.Empty(await restarted.Storage.ListDirectoriesAsync(At("d"), Token));
        }

        AssertEveryState(restarted, static image => !new RecordingFileSystem(Root, image).DirectoryExists(At("d")));
    }

    /// <summary>
    /// A process which crashed between creating a directory and flushing its parent left the name unsynced. A listing of
    /// the parent flushes it after it observes the name, so every crash state after the listing keeps it (WAL-I12).
    /// </summary>
    [Fact]
    public async Task ADirectoryListingHoldsInEveryCrashState()
    {
        var crashed = new RecordingFileSystem(Root) { CrashAfter = 1 };
        _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await crashed.Storage.CreateDirectoryAsync(At("d"), Token));
        var restarted = crashed.Restart();
        Assert.Equal(["d"], await restarted.Storage.ListDirectoriesAsync(Root, Token));
        AssertEveryState(restarted, static image => new RecordingFileSystem(Root, image).DirectoryExists(At("d")));
    }

    /// <summary>
    /// Creating a directory durably creates it when it is absent and accepts it when it is present, as a creator which
    /// crashed before its flush leaves it, and flushes its parent in both cases, so every crash state after it holds the
    /// directory (WAL-I10).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatingADirectoryDurablyFlushesItsParentWhetherOrNotItExisted(bool present)
    {
        var fileSystem = new RecordingFileSystem(Root);
        if (present)
        {
            fileSystem.CreateDirectory(At("d"));
        }

        var before = fileSystem.OperationCount;
        fileSystem.CreateDirectoryDurably(At("d"));

        (FileSystemOperationKind, string)[] created = present ? [] : [(FileSystemOperationKind.MakeDirectory, "d")];
        Assert.Equal(
            [.. created, (FileSystemOperationKind.FlushDirectory, string.Empty)],
            fileSystem.Operations.Skip(before).Select(static operation => (operation.Kind, operation.Path)));
        AssertEveryState(fileSystem, static image => new RecordingFileSystem(Root, image).DirectoryExists(At("d")));
    }

    /// <summary>
    /// A failed flush of the parent stops the process with the directory flush's own exit code, naming the parent
    /// (conventions §14), as the flush does on its own.
    /// </summary>
    [Fact]
    public void CreatingADirectoryDurablyStopsOnAFailedParentFlush()
    {
        var fileSystem = new RecordingFileSystem(Root) { FailFlushes = static path => path.Length == 0 };
        var since = FatalErrors.ReportedCount;

        _ = Assert.ThrowsAny<IOException>(() => fileSystem.CreateDirectoryDurably(At("d")));
        _ = WalTestHelpers.AssertReported(since, FatalErrorSite.FlushDirectory, Root);
    }

    /// <summary>
    /// Opening a directory flushes the directory and not its parent: its name was durable once its creation returned
    /// (WAL-I10).
    /// </summary>
    [Fact]
    public async Task OpeningADirectoryFlushesOnlyTheDirectory()
    {
        var fileSystem = new RecordingFileSystem(Root);
        await fileSystem.Storage.CreateDirectoryAsync(At("d"), Token);
        var created = fileSystem.OperationCount;
        await (await fileSystem.Storage.OpenDirectoryAsync(At("d"), Token)).DisposeAsync();
        Assert.Equal(
            ["d"],
            fileSystem.Operations.Skip(created).Where(static operation => operation.Kind == FileSystemOperationKind.FlushDirectory).Select(static operation => operation.Path));
    }

    /// <summary>
    /// A caller whose write-ahead log directory is its root opens the directory and creates the log in it. Creating the
    /// directory would flush its parent, above the root, which no flush may reach; opening it flushes only the root,
    /// and the log's first file is durable once the log's creation returns (wal.md §2, §3).
    /// </summary>
    [Fact]
    public async Task ALogWhoseDirectoryIsTheRootIsCreatedInTheOpenedRoot()
    {
        var fileSystem = new RecordingFileSystem(Root);
        var options = WalTestHelpers.Options(fileSize: 4 * 4096) with { PreallocateFiles = false };
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await WriteAheadLog.CreateAsync(fileSystem.Storage, Root, options, Token));
        Assert.Empty(fileSystem.Operations);

        int created;
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(Root, Token))
        await using (var log = await WriteAheadLog.CreateAsync(directory, options, Token))
        {
            created = fileSystem.OperationCount;
        }

        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        foreach (var state in enumerator.Enumerate(created, fileSystem.OperationCount, int.MaxValue).States)
        {
            Assert.True(enumerator.Build(state).ReadFile(WalFormat.GetFileName(1)) is { Length: > 0 }, enumerator.Describe(state));
        }
    }

    /// <summary>
    /// A process which crashed between publishing a named file and flushing its directory left the name unsynced. A
    /// restarted process which opens the directory flushes it before any read, so what it reads survives an
    /// operating-system crash (WAL-I10).
    /// </summary>
    [Fact]
    public async Task ANameACrashedProcessPublishedIsDurableOnceARestartedProcessOpensItsDirectory()
    {
        var crashed = new RecordingFileSystem(Root);
        await crashed.Storage.CreateDirectoryAsync(At("wal"), Token);
        await using (var directory = await crashed.Storage.OpenDirectoryAsync(At("wal"), Token))
        {
            // Creating a named file records its temporary's creation, write, and flush, then the rename; the process
            // crashes after the rename, before the directory flush.
            crashed.CrashAfter = crashed.OperationCount + 4;
            using var contents = Buffer([7, 7, 7]);
            _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await directory.CreateNamedFileAsync("a", contents, Token));
        }

        Assert.Equal(FileSystemOperationKind.Rename, crashed.Operations[^1].Kind);
        var restarted = crashed.Restart();
        await using (var directory = await restarted.Storage.OpenDirectoryAsync(At("wal"), Token))
        {
            Assert.Equal("070707", await ReadNamedAsync(directory, "a"));
        }

        var point = restarted.OperationCount;
        var enumerator = CrashStateEnumerator.Of(restarted, seed: 0);
        foreach (var state in enumerator.Enumerate(point, point, int.MaxValue).States)
        {
            Assert.True(enumerator.Build(state).ReadFile("wal/a") is [7, 7, 7], enumerator.Describe(state));
        }
    }

    /// <summary>
    /// Creating and deleting a named file, creating and deleting a log file, and creating and deleting a
    /// directory, named with a trailing separator, leave every crash state with each name absent or naming complete
    /// contents: until an operation returns, what it found or what it leaves, and after it, only what it leaves (WAL-I3,
    /// wal.md §2).
    /// </summary>
    [Fact]
    public Task PublishedNamesNameCompleteContentsInEveryCrashState() => SeededSimulation.RunAsync(0x5eed, async run =>
    {
        var fileSystem = new RecordingFileSystem(Root);
        var history = new Dictionary<string, List<Step>>(StringComparer.Ordinal) { ["a"] = [], ["log"] = [], ["gone"] = [] };
        byte[] first = [.. Enumerable.Repeat((byte)0xA1, 1500)];
        var header = Enumerable.Range(0, 4096).Select(static index => (byte)((index * 7 + 1) & 0xFF)).ToArray();
        var walPath = At("wal");
        var gonePath = At("gone");
        using var firstContents = Buffer(first);
        using var headerContents = Buffer(header);
        await fileSystem.Storage.CreateDirectoryAsync(walPath, Token);
        await using (var directory = await fileSystem.Storage.OpenDirectoryAsync(walPath, Token))
        {
            async Task StepAsync(string name, string? before, string? after, Func<Task> operation)
            {
                var started = fileSystem.OperationCount;
                await operation();
                history[name].Add(new Step(started, fileSystem.OperationCount, before, after));
            }

            await StepAsync("a", null, Hex(first), async () => await (await directory.CreateNamedFileAsync("a", firstContents, Token)).DisposeAsync());
            await StepAsync("log", null, Hex(header), async () => await (await directory.CreateFileAsync(1, headerContents, 0, null, Token)).DisposeAsync());
            await StepAsync("gone", null, "directory", async () => await fileSystem.Storage.CreateDirectoryAsync(gonePath, Token));
            await StepAsync("log", Hex(header), null, async () => await directory.DeleteFileAsync(1, Token));
            await StepAsync("a", Hex(first), null, async () => await directory.DeleteNamedFileAsync("a", Token));
            await StepAsync("gone", "directory", null, async () => await fileSystem.Storage.DeleteDirectoryAsync(gonePath + Path.DirectorySeparatorChar, Token));
        }

        var enumerator = CrashStateEnumerator.Of(fileSystem, run.Seed);
        var from = history.Values.Min(static steps => steps[0].Started);
        var states = enumerator.Enumerate(from, fileSystem.OperationCount, int.MaxValue);
        run.Record(states.ToString());
        TestDiagnostics.WriteLine(states.ToString());
        foreach (var state in states.States)
        {
            var replayed = new RecordingFileSystem(Root, enumerator.Build(state));
            await using var directory = await replayed.Storage.OpenDirectoryAsync(walPath, Token);
            var observed = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["a"] = await ReadNamedAsync(directory, "a"),
                ["log"] = (await directory.ListFilesAsync(Token)).Contains(1UL) ? await ReadLogAsync(directory, 1, header.Length) : null,
                ["gone"] = replayed.DirectoryExists(gonePath) ? "directory" : null,
            };
            foreach (var (name, steps) in history)
            {
                Assert.True(Allowed(steps, state.Point).Contains(observed[name]), name + ": " + enumerator.Describe(state));
            }
        }
    });

    private static string At(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static ArcBuffer Buffer(byte[] bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    private static ArcBuffer AlignedBuffer(byte[] bytes)
    {
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    /// <summary>
    /// Returns what a crash at <paramref name="point"/> may leave of one name: what it held before its first step,
    /// what the last step to start left once that step returned, and either side of a step still running.
    /// </summary>
    private static string?[] Allowed(List<Step> steps, int point)
    {
        var started = steps.FindLastIndex(step => step.Started < point);
        if (started < 0)
        {
            return [steps[0].Before];
        }

        var step = steps[started];
        return step.Returned <= point ? [step.After] : [step.Before, step.After];
    }

    /// <summary>
    /// Returns the contents of <paramref name="path"/> in every crash state after the last operation, as hexadecimal,
    /// or <see langword="null"/> where the file is absent.
    /// </summary>
    private static List<string?> Contents(RecordingFileSystem fileSystem, string path)
    {
        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        var point = fileSystem.OperationCount;
        return
        [
            .. enumerator.Enumerate(point, point, int.MaxValue).States
                .Select(state => enumerator.Build(state).ReadFile(path) is { } bytes ? Convert.ToHexString(bytes) : null),
        ];
    }

    /// <summary>
    /// Fails unless <paramref name="holds"/> holds in every crash state after the last operation.
    /// </summary>
    private static void AssertEveryState(RecordingFileSystem fileSystem, Func<FileSystemImage, bool> holds)
    {
        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        var point = fileSystem.OperationCount;
        foreach (var state in enumerator.Enumerate(point, point, int.MaxValue).States)
        {
            Assert.True(holds(enumerator.Build(state)), enumerator.Describe(state));
        }
    }

    private static async Task<string?> ReadNamedAsync(WalDirectory directory, string name)
    {
        try
        {
            await using var file = await directory.OpenNamedFileAsync(name, Token);
            using var bytes = await file.ReadAsync(0, (int)file.Length, Token);
            return Hex(bytes.ToArray());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static async Task<string> ReadLogAsync(WalDirectory directory, ulong number, int length)
    {
        await using var file = await directory.OpenFileAsync(number, Token);
        Assert.Equal(length, file.Length);
        using var bytes = await file.ReadAsync(0, length, Token);
        return Hex(bytes.ToArray());
    }

    /// <summary>
    /// Creates a log in a new directory, appends records which roll it to a third file, and releases the first two,
    /// recording in <paramref name="life"/> what it appended and which appends were acknowledged.
    /// </summary>
    private static async Task LiveAsync(RecordingFileSystem fileSystem, WalOptions options, Life life)
    {
        await fileSystem.Storage.CreateDirectoryAsync(At("wal"), Token);
        var log = await WriteAheadLog.CreateAsync(fileSystem.Storage, At("wal"), options, Token);
        try
        {
            // Each 3,000-byte record fills most of a 4 KiB page, so a 4-page file holds three.
            for (var index = 0; index < 8; index++)
            {
                var payload = WalTestHelpers.Bytes(3_000, index);
                life.Appended.Add(payload);
                life.Acknowledged.Add((await WalTestHelpers.AppendAsync(log, payload), payload));
            }

            await WalTestHelpers.WaitForFrontierAsync(log, life.Acknowledged[^1].Position);
            life.Releasing = true;
            await log.ReleaseAsync(Life.ReleaseBound, Token);
        }
        finally
        {
            await log.DisposeAsync();
        }
    }

    /// <summary>
    /// One operation on a name: the operations before it started and after it returned, and what the name held before
    /// and after it.
    /// </summary>
    private sealed record Step(int Started, int Returned, string? Before, string? After);

    /// <summary>
    /// What one run of <see cref="LiveAsync"/> appended, which appends were acknowledged, and whether it started its
    /// release.
    /// </summary>
    private sealed class Life
    {
        public const ulong ReleaseBound = 3;

        public List<byte[]> Appended { get; } = [];

        public List<(WalPosition Position, byte[] Payload)> Acknowledged { get; } = [];

        public bool Releasing { get; set; }
    }
}
