using System.Buffers.Binary;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks that a scan replays every valid record and classifies each invalid page by WAL-I2.
/// </summary>
public sealed class WalReplayTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int Page = 4096;

    [Fact]
    public async Task TornPageInTheCrashRegionIsTornAndLaterRecordsRecover()
    {
        var storage = new SimulatedWalStorage();
        var payloads = await WritePageRecordsAsync(storage, Options(window: 4), count: 10);
        storage.Damage(LogPath, 1, DataPage(8), Garbage());

        var entries = await ReopenAndReadAsync(storage, Options(window: 4));

        Assert.Equal([.. payloads[..8], payloads[9]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        var fault = Assert.Single(entries, static entry => entry.Fault is not null);
        Assert.True(fault.IsTorn);
        Assert.Equal(new WalPosition(1, (ulong)DataPage(8)), fault.Position);
        Assert.Equal(new WalTorn(new WalPosition(1, (ulong)DataPage(8)), new WalPosition(1, (ulong)DataPage(9))), fault.Fault!.Value.Value);

        // Replay recorded the tear durably, so every later open reports it identically.
        Assert.True(WalFormat.IsHolePage(storage.ReadDurable(LogPath, 1).AsSpan((int)DataPage(8), Page)));
        Assert.Equal(Describe(entries), Describe(await ReopenAndReadAsync(storage, Options(window: 4))));
    }

    /// <summary>
    /// Once an open is final, damage anywhere in it is corruption, including the pages which bounded its crash
    /// region: losing the last page cannot turn earlier damage, or its own records, into a tear.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamageAfterReplayIsCorruptionEvenAtTheEndOfAnOpen(bool zeroes)
    {
        var storage = new SimulatedWalStorage();
        var payloads = await WritePageRecordsAsync(storage, Options(window: 4), count: 10);
        await (await WriteAheadLog.OpenAsync(storage, LogPath, Options(window: 4), TestContext.Current.CancellationToken)).DisposeAsync();
        foreach (var page in new[] { 7, 9 })
        {
            storage.Damage(LogPath, 1, DataPage(page), zeroes ? new byte[Page] : Garbage());
        }

        var entries = await ReopenAndReadAsync(storage, Options(window: 4));

        Assert.Equal([.. payloads[..7], payloads[8]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.Equal(
            [
                new WalEntry(new WalCorruption(new WalPosition(1, (ulong)DataPage(7)), new WalPosition(1, (ulong)DataPage(8)))),
                new WalEntry(new WalCorruption(new WalPosition(1, (ulong)DataPage(9)), new WalPosition(1, (ulong)DataPage(10)))),
            ],
            entries.Where(static entry => entry.Fault is not null).Select(static entry => entry.Fault!.Value));
    }

    /// <summary>
    /// A replay whose hole-page write fails is reported and stops the process (conventions.md §14); the restarted
    /// process's replay repeats it with the same result.
    /// </summary>
    [Fact]
    public async Task AnInterruptedReplayIsRepeatedWithTheSameResult()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(fileSize: 16 * Page, window: 4) with { PreallocateFiles = true };
        var payloads = await WritePageRecordsAsync(storage, options, count: 10);
        storage.Damage(LogPath, 1, DataPage(6), Garbage());
        storage.Damage(LogPath, 1, DataPage(8), new byte[Page]);
        storage.WriteFault = (_, file, offset, _) => file == 1 && offset == DataPage(8) ? new IOException("injected write failure") : null;
        var since = FatalErrors.ReportedCount;

        await Assert.ThrowsAsync<IOException>(
            async () => await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken));
        _ = AssertReported(since, FatalErrorSite.HandleWriteGather, LogFile(1));
        Assert.True(WalFormat.IsHolePage(storage.ReadDurable(LogPath, 1).AsSpan((int)DataPage(6), Page)));
        var restarted = storage.Crash(operatingSystem: false);
        var files = await ListFilesAsync(restarted);
        Assert.Equal([1UL], files);

        var entries = await ReopenAndReadAsync(restarted, options);

        Assert.Equal([.. payloads[..6], payloads[7], payloads[9]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.Equal(
            [
                new WalEntry(new WalTorn(new WalPosition(1, (ulong)DataPage(6)), new WalPosition(1, (ulong)DataPage(7)))),
                new WalEntry(new WalTorn(new WalPosition(1, (ulong)DataPage(8)), new WalPosition(1, (ulong)DataPage(9)))),
            ],
            entries.Where(static entry => entry.Fault is not null).Select(static entry => entry.Fault!.Value));
        Assert.Equal(DataPage(10), restarted.ReadDurable(LogPath, 1).Length);
    }

    [Fact]
    public async Task DamageBeforeTheWindowIsCorruption()
    {
        var storage = new SimulatedWalStorage();
        var payloads = await WritePageRecordsAsync(storage, Options(window: 4), count: 10);
        storage.Damage(LogPath, 1, DataPage(2), Garbage());
        storage.Damage(LogPath, 1, DataPage(3), new byte[Page]);

        var entries = await ReopenAndReadAsync(storage, Options(window: 4));

        Assert.Equal([.. payloads[..2], .. payloads[4..]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        var fault = Assert.Single(entries, static entry => entry.Fault is not null);
        Assert.Equal(new WalCorruption(new WalPosition(1, (ulong)DataPage(2)), new WalPosition(1, (ulong)DataPage(4))), fault.Fault!.Value.Value);
    }

    [Fact]
    public async Task ARunStraddlingTheWindowSplitsIntoCorruptionThenTorn()
    {
        var storage = new SimulatedWalStorage();
        _ = await WritePageRecordsAsync(storage, Options(window: 4), count: 10);
        foreach (var page in new[] { 4, 5, 6, 7, 8 })
        {
            storage.Damage(LogPath, 1, DataPage(page), Garbage());
        }

        var faults = (await ReopenAndReadAsync(storage, Options(window: 4))).Where(static entry => entry.Fault is not null).ToArray();

        Assert.Equal(2, faults.Length);
        Assert.Equal(new WalCorruption(new WalPosition(1, (ulong)DataPage(4)), new WalPosition(1, (ulong)DataPage(6))), faults[0].Fault!.Value.Value);
        Assert.Equal(new WalTorn(new WalPosition(1, (ulong)DataPage(6)), new WalPosition(1, (ulong)DataPage(9))), faults[1].Fault!.Value.Value);
    }

    [Fact]
    public async Task ReplayTruncatesTheOpenAfterItsLastValidPage()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(fileSize: 8 * Page, window: 4) with { PreallocateFiles = true };
        var payloads = await WritePageRecordsAsync(storage, options, count: 8);
        var files = await ListFilesAsync(storage);
        Assert.Equal([1UL, 2UL], files);
        storage.Damage(LogPath, 1, DataPage(4), Garbage());
        storage.Damage(LogPath, 1, DataPage(5), Garbage());
        storage.Damage(LogPath, 1, DataPage(6), Garbage());
        storage.Damage(LogPath, 2, DataPage(0), Garbage());

        var entries = await ReopenAndReadAsync(storage, options);

        // Pages after the last valid page (page 3 in file 1) were in flight at the crash, and file 2 holds no valid page.
        Assert.Equal(payloads[..4], entries.Select(static entry => entry.Payload!));
        Assert.Equal(DataPage(4), storage.ReadDurable(LogPath, 1).Length);
        Assert.Equal(Page, storage.ReadDurable(LogPath, 2).Length);
    }

    [Fact]
    public async Task DamageInAFileFollowedByValidDataOfTheSameOpenIsCorruption()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(fileSize: 4 * Page);
        var payloads = await WritePageRecordsAsync(storage, options, count: 5);
        Assert.Equal(3, storage.ReadDurable(LogPath, 1).Length / Page - 1);
        storage.Damage(LogPath, 1, DataPage(2), Garbage());

        var entries = await ReopenAndReadAsync(storage, options);

        Assert.Equal([.. payloads[..2], .. payloads[3..]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.True(Assert.Single(entries, static entry => entry.Fault is not null).IsCorruption);
    }

    [Fact]
    public async Task ATornMiddlePageLosesOnlyItsRecordAndTheOrphanFragmentIsSkipped()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var large = Bytes(9000, 1);
        var small = Bytes(100, 2);
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken))
        {
            var first = AppendAsync(wal, [1]).AsTask();
            await WalFormatTests.WaitForPendingAsync(storage, 1);
            var second = AppendAsync(wal, [2]).AsTask();
            await WalFormatTests.WaitForPendingAsync(storage, 2);
            var records = new[] { first, second, AppendAsync(wal, large).AsTask(), AppendAsync(wal, small).AsTask() };
            while (!Task.WhenAll(records).IsCompleted)
            {
                _ = storage.CompleteWrite();
                await PauseAsync();
            }

            _ = await first;
            _ = await second;
            Assert.Equal(new WalPosition(1, (ulong)DataPage(2) + 4), await records[2]);
            Assert.Equal(new WalPosition(1, (ulong)DataPage(4) + 8 + 832), await records[3]);
        }

        storage.HoldWrites = false;
        storage.Damage(LogPath, 1, DataPage(3), Garbage());

        var entries = await ReopenAndReadAsync(storage, Options());

        Assert.Equal([[1], [2], small], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.True(Assert.Single(entries, static entry => entry.Fault is not null).IsTorn);
    }

    [Fact]
    public async Task AnInvalidNeighbouringFrameMakesAPointReadCorrupt()
    {
        var storage = new SimulatedWalStorage();
        WalPosition position;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken))
        {
            position = await AppendAsync(wal, Bytes(10, 1));
        }

        var page = storage.ReadDurable(LogPath, 1).AsSpan(Page, Page).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4 + 4 + 10), WalFormat.EncodeFrame(FrameKind.Full, Page));
        WalFormat.SealPage(page);
        storage.Damage(LogPath, 1, Page, page);
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);

        Assert.True((await reopened.ReadAsync(position, TestContext.Current.CancellationToken)).TryGetValue(out WalCorruption _));
        Assert.True(Assert.Single(await ReadAllAsync(reopened)).IsCorruption);
    }

    /// <summary>
    /// A First frame may name a record of up to <c>int.MaxValue</c> bytes (wal.md §7), so a point read counts the
    /// record's continuation pages without overflow, and a record its file cannot hold is corruption.
    /// </summary>
    [Fact]
    public async Task APointReadOfAFirstFrameNamingTheLongestRecordIsCorruption()
    {
        var storage = new SimulatedWalStorage();
        _ = await WritePageRecordsAsync(storage, Options(), count: 2);
        var page = new byte[Page];
        BinaryPrimitives.WriteUInt32LittleEndian(
            page.AsSpan(WalFormat.CrcLength),
            WalFormat.EncodeFrame(FrameKind.First, Page - WalFormat.CrcLength - WalFormat.FirstFrameHeaderLength));
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(WalFormat.CrcLength + WalFormat.FrameWordLength), int.MaxValue);
        WalFormat.SealPage(page);
        storage.Damage(LogPath, 1, DataPage(1), page);
        await using var wal = await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);

        var read = await wal.ReadAsync(new WalPosition(1, (ulong)DataPage(1) + WalFormat.CrcLength), TestContext.Current.CancellationToken);

        Assert.True(read.TryGetValue(out WalCorruption _));
    }

    [Fact]
    public async Task AFirstFrameWithoutAPayloadByteIsCorruption()
    {
        var storage = new SimulatedWalStorage();
        _ = await WritePageRecordsAsync(storage, Options(), count: 2);
        var page = new byte[Page];
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), WalFormat.EncodeFrame(FrameKind.Full, Page - 20));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(Page - 12), WalFormat.EncodeFrame(FrameKind.First, 0));
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(Page - 8), 5);
        WalFormat.SealPage(page);
        storage.Damage(LogPath, 1, DataPage(0), page);

        Assert.False(WalFormat.IsWellFormed(page));
        var entries = await ReopenAndReadAsync(storage, Options());
        Assert.True(entries[0].IsCorruption);
        Assert.Single(entries, static entry => entry.IsRecord);
    }

    [Fact]
    public async Task AFaultInsideTheStartingPageStartsAtTheRequestedPosition()
    {
        var storage = new SimulatedWalStorage();
        _ = await WritePageRecordsAsync(storage, Options(window: 4), count: 10);
        storage.Damage(LogPath, 1, DataPage(8), Garbage());
        await using var wal = await WriteAheadLog.OpenAsync(storage, LogPath, Options(window: 4), TestContext.Current.CancellationToken);

        var from = new WalPosition(1, (ulong)DataPage(8) + 100);
        var entries = await ReadAllAsync(wal, from);

        Assert.Equal(new WalTorn(from, new WalPosition(1, (ulong)DataPage(9))), entries[0].Fault!.Value.Value);
        Assert.Single(entries, static entry => entry.IsRecord);
    }

    [Fact]
    public async Task AValidPageWhichBreaksTheFrameRulesIsCorruption()
    {
        var storage = new SimulatedWalStorage();
        var payloads = await WritePageRecordsAsync(storage, Options(), count: 3);
        var page = storage.ReadDurable(LogPath, 1).AsSpan((int)DataPage(1), Page).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), WalFormat.EncodeFrame(FrameKind.Full, Page));
        WalFormat.SealPage(page);
        storage.Damage(LogPath, 1, DataPage(1), page);

        var entries = await ReopenAndReadAsync(storage, Options());

        Assert.Equal([payloads[0], payloads[2]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.Equal(
            new WalCorruption(new WalPosition(1, (ulong)DataPage(1)), new WalPosition(1, (ulong)DataPage(2))),
            Assert.Single(entries, static entry => entry.Fault is not null).Fault!.Value.Value);
    }

    /// <summary>
    /// Adjacent invalid pages form one run, but each valid page which breaks the frame rules is an entry of its own, so
    /// the invalid pages after one start a run of their own (wal.md §6).
    /// </summary>
    [Fact]
    public async Task AValidPageWhichBreaksTheFrameRulesIsAnEntryOfItsOwn()
    {
        var storage = new SimulatedWalStorage();
        var payloads = await WritePageRecordsAsync(storage, Options(window: 4), count: 10);
        var page = storage.ReadDurable(LogPath, 1).AsSpan((int)DataPage(1), Page).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), WalFormat.EncodeFrame(FrameKind.Full, Page));
        WalFormat.SealPage(page);
        storage.Damage(LogPath, 1, DataPage(1), page);
        storage.Damage(LogPath, 1, DataPage(2), Garbage());
        storage.Damage(LogPath, 1, DataPage(3), Garbage());

        var entries = await ReopenAndReadAsync(storage, Options(window: 4));

        Assert.Equal([payloads[0], .. payloads[4..]], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.Equal(
            [
                new WalEntry(new WalCorruption(new WalPosition(1, (ulong)DataPage(1)), new WalPosition(1, (ulong)DataPage(2)))),
                new WalEntry(new WalCorruption(new WalPosition(1, (ulong)DataPage(2)), new WalPosition(1, (ulong)DataPage(4)))),
            ],
            entries.Where(static entry => entry.Fault is not null).Select(static entry => entry.Fault!.Value));
    }

    [Fact]
    public async Task ALeadingContinuationAfterASettledPageIsCorruption()
    {
        var storage = new SimulatedWalStorage();
        _ = await WritePageRecordsAsync(storage, Options(), count: 3);
        var page = storage.ReadDurable(LogPath, 1).AsSpan((int)DataPage(1), Page).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), WalFormat.EncodeFrame(FrameKind.Continuation, Page - 8));
        WalFormat.SealPage(page);
        storage.Damage(LogPath, 1, DataPage(1), page);

        var entries = await ReopenAndReadAsync(storage, Options());

        Assert.True(Assert.Single(entries, static entry => entry.Fault is not null).IsCorruption);
        Assert.Equal(2, entries.Count(static entry => entry.IsRecord));
    }

    [Fact]
    public async Task AFileWithAnInvalidHeaderIsCorruptionAndOtherFilesRecover()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(fileSize: 4 * Page);
        var payloads = await WritePageRecordsAsync(storage, options, count: 5);
        storage.Damage(LogPath, 1, 20, [0xFF]);

        var entries = await ReopenAndReadAsync(storage, options);

        Assert.Equal(payloads[3..], entries.Where(static entry => entry.IsRecord).Select(static entry => entry.Payload!));
        Assert.Equal(
            new WalCorruption(new WalPosition(1, 0), new WalPosition(1, 4 * Page)),
            Assert.Single(entries, static entry => entry.Fault is not null).Fault!.Value.Value);
    }

    [Fact]
    public async Task AnUnknownFileVersionFailsOpen()
    {
        var storage = new SimulatedWalStorage();
        _ = await WritePageRecordsAsync(storage, Options(), count: 1);
        var header = storage.ReadDurable(LogPath, 1)[..Page];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 2);
        WalFormat.SealPage(header);
        storage.Damage(LogPath, 1, 0, header);

        await Assert.ThrowsAsync<WalUnsupportedFormatException>(
            async () => await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadingFromAPositionYieldsOnlyLaterEntries()
    {
        var storage = new SimulatedWalStorage();
        var options = Options(fileSize: 4 * Page);
        var payloads = await WritePageRecordsAsync(storage, options, count: 6);
        await using var wal = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var all = await ReadAllAsync(wal);

        var from = all[3].Position;
        var later = await ReadAllAsync(wal, from);

        Assert.Equal(payloads[3..], later.Select(static entry => entry.Payload!));
        Assert.Equal(all.Skip(3).Select(static entry => entry.Position), later.Select(static entry => entry.Position));
    }

    [Fact]
    public async Task PointReadOfADamagedPageReportsCorruption()
    {
        var storage = new SimulatedWalStorage();
        WalPosition small;
        WalPosition large;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken))
        {
            small = await AppendAsync(wal, Bytes(10, 1));
            large = await AppendAsync(wal, Bytes(10_000, 2));
        }

        storage.Damage(LogPath, 1, DataPage(2), Garbage());
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(Bytes(10, 1), (await reopened.ReadAsync(small, TestContext.Current.CancellationToken)).ToBytes());
        var read = await reopened.ReadAsync(large, TestContext.Current.CancellationToken);
        Assert.True(read.TryGetValue(out WalCorruption corruption));
        Assert.Equal(new WalCorruption(new WalPosition(1, (ulong)DataPage(2)), new WalPosition(1, (ulong)DataPage(3))), corruption);
    }

    private static long DataPage(long page) => Page * (1 + page);

    private static byte[] Garbage() => Bytes(Page, 99);

    /// <summary>
    /// Writes records which each fill one data page, then closes the log.
    /// </summary>
    /// <summary>
    /// Writes one record per page. With 15 data pages per file, the next file is prepared once 3 hold records.
    /// </summary>
    private static async Task<byte[][]> WritePageRecordsAsync(SimulatedWalStorage storage, WalOptions options, int count)
    {
        var payloads = Enumerable.Range(0, count).Select(static index => Bytes(Page - 8, index)).ToArray();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        foreach (var payload in payloads)
        {
            _ = await AppendAsync(wal, payload);
        }

        return payloads;
    }

    private static async Task<List<Observed>> ReopenAndReadAsync(SimulatedWalStorage storage, WalOptions options)
    {
        await using var wal = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        return await ReadAllAsync(wal);
    }

    private static async Task<IReadOnlyList<ulong>> ListFilesAsync(SimulatedWalStorage storage)
    {
        await using var directory = await storage.OpenDirectoryAsync(LogPath, TestContext.Current.CancellationToken);
        return await directory.ListFilesAsync(TestContext.Current.CancellationToken);
    }

    private static string[] Describe(List<Observed> entries) =>
        [
            .. entries.Select(static entry => entry.Payload is { } payload
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Position} {Convert.ToHexString(payload)}")
                : entry.Fault.ToString()!),
        ];
}
