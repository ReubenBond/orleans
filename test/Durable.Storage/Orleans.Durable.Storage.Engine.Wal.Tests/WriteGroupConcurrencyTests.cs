using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

public sealed class WriteGroupConcurrencyTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private static readonly TimeSpan Write = TimeSpan.FromMilliseconds(8);

    [Fact]
    public void TheLimitStartsAtTheMinimumAndHoldsWithOnlyLoneTimings()
    {
        var concurrency = new WriteGroupConcurrency(1, 4);
        for (var group = 0; group < 10; group++)
        {
            Assert.Equal(1, concurrency.Slots(0));
            concurrency.Submitted(1);
            concurrency.Completed(1, Write);
        }

        Assert.Equal(1, concurrency.Limit);
    }

    [Theory]
    [InlineData(1.0, 4)]
    [InlineData(1.25, 4)]
    [InlineData(1.3, 1)]
    [InlineData(1.5, 1)]
    [InlineData(2.0, 1)]
    public void TheLimitIsTheMaximumOnlyWhileConcurrentGroupsAreAboutAsFastAsLoneOnes(double ratio, int limit)
    {
        var concurrency = new WriteGroupConcurrency(1, 4);
        concurrency.Completed(1, Write);
        concurrency.Completed(2, Write * ratio);

        Assert.Equal(limit, concurrency.Limit);
    }

    [Fact]
    public void BelowTheMaximumEverySixtyFifthGroupMayPassTheLimit()
    {
        var concurrency = new WriteGroupConcurrency(1, 4);
        for (var group = 0; group < WriteGroupConcurrency.ProbeInterval; group++)
        {
            Assert.Equal(0, concurrency.Slots(1));
            concurrency.Submitted(1);
        }

        Assert.Equal(1, concurrency.Slots(1));
        Assert.Equal(0, concurrency.Slots(4));
        concurrency.Submitted(2);
        Assert.Equal(0, concurrency.Slots(1));
    }

    [Fact]
    public void AFixedConcurrencyNeverPassesItsLimit()
    {
        var concurrency = new WriteGroupConcurrency(2, 2);
        for (var group = 0; group < 3 * WriteGroupConcurrency.ProbeInterval; group++)
        {
            concurrency.Submitted(2);
            concurrency.Completed(group % 2 + 1, Write);
        }

        Assert.Equal(2, concurrency.Slots(0));
        Assert.Equal(0, concurrency.Slots(2));
        Assert.Equal(2, concurrency.Limit);
    }

    [Fact]
    public void TheLimitFollowsTheDeviceWhenItStopsRunningWritesInParallel()
    {
        var concurrency = new WriteGroupConcurrency(1, 4);
        concurrency.Completed(1, Write);
        concurrency.Completed(2, Write);
        Assert.Equal(4, concurrency.Limit);

        var completions = 0;
        while (concurrency.Limit == 4)
        {
            concurrency.Completed(2, Write * 2);
            completions++;
        }

        // An eighth of each difference moves the concurrent average from 1× past 1.25× within three completions.
        Assert.Equal(3, completions);
    }

    /// <summary>
    /// On a device which runs durable writes one at a time, the log keeps one group in flight: records accepted during
    /// a write share the next, and a probe which waits behind the write ahead of it keeps the limit at one.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task OnASerialDeviceRecordsAcceptedDuringAWriteShareTheNext()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, AdaptiveOptions(time), TestContext.Current.CancellationToken);
        await WriteLoneGroupsAsync(wal, storage, time);

        // The probe: a second group submitted while the first is midway, which finishes a whole write after it.
        var first = AppendAsync(wal, Bytes(100, 1)).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var probe = AppendAsync(wal, Bytes(100, 2)).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 2);
        time.Advance(Write / 2);
        Assert.True(storage.CompleteWrite(static (_, _, _) => true));
        time.Advance(Write);
        Assert.True(storage.CompleteWrite(static (_, _, _) => true));
        _ = await Task.WhenAll(first, probe);

        var blocker = AppendAsync(wal, Bytes(100, 3)).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var waiting = Enumerable.Range(4, 3).Select(seed => AppendAsync(wal, Bytes(100, seed)).AsTask()).ToArray();
        await PauseAsync(20);
        Assert.Equal(1, storage.PendingWriteCount);

        var issued = storage.IssuedWriteCount;
        Assert.True(storage.CompleteWrite(static (_, _, _) => true));
        _ = await blocker;
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        Assert.True(storage.CompleteWrite(static (_, _, _) => true));
        _ = await Task.WhenAll(waiting);
        Assert.Equal(issued + 1, storage.IssuedWriteCount);
    }

    /// <summary>
    /// On a device which runs durable writes in parallel, a probe as fast as a lone group raises the limit to the
    /// maximum, and records accepted while groups are in flight are submitted at once.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task OnAParallelDeviceGroupsAreSubmittedAtOnceUpToTheMaximum()
    {
        var time = new FakeTimeProvider();
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, AdaptiveOptions(time), TestContext.Current.CancellationToken);
        await WriteLoneGroupsAsync(wal, storage, time);

        var first = AppendAsync(wal, Bytes(100, 1)).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var probe = AppendAsync(wal, Bytes(100, 2)).AsTask();
        await WalFormatTests.WaitForPendingAsync(storage, 2);
        time.Advance(Write);
        Assert.True(storage.CompleteWrite(static (_, _, _) => true));
        Assert.True(storage.CompleteWrite(static (_, _, _) => true));
        _ = await Task.WhenAll(first, probe);

        var appends = new List<Task<WalPosition>>();
        for (var index = 1; index <= 4; index++)
        {
            appends.Add(AppendAsync(wal, Bytes(100, 10 + index)).AsTask());
            await WalFormatTests.WaitForPendingAsync(storage, index);
        }

        storage.HoldWrites = false;
        while (storage.CompleteWrite())
        {
            await Task.Yield();
        }

        _ = await Task.WhenAll(appends);
    }

    /// <summary>
    /// A lone record spanning pages is one write even with one group in flight: the open page holding its end continues
    /// the run of its earlier pages, so the pump closes it into the group it submits.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WithOneGroupInFlightALoneRecordSpanningPagesIsOneWrite()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(groups: 1), TestContext.Current.CancellationToken);
        var issued = storage.IssuedWriteCount;

        var payload = Bytes(9000, 7);
        var position = await AppendAsync(wal, payload);

        Assert.Equal(issued + 1, storage.IssuedWriteCount);
        Assert.Equal(payload, (await wal.ReadAsync(position, TestContext.Current.CancellationToken)).ToBytes());
    }

    private static WalOptions AdaptiveOptions(FakeTimeProvider time) =>
        Options(pageSize: 512) with { MinWriteGroupsInFlight = 1, MaxWriteGroupsInFlight = 4, TimeProvider = time, PreallocateFiles = false };

    /// <summary>
    /// Writes enough lone groups, each taking one write, for the next group past the limit to be a probe.
    /// </summary>
    private static async Task WriteLoneGroupsAsync(WriteAheadLog wal, SimulatedWalStorage storage, FakeTimeProvider time)
    {
        for (var group = 0; group < WriteGroupConcurrency.ProbeInterval; group++)
        {
            var append = AppendAsync(wal, Bytes(100, group)).AsTask();
            await WalFormatTests.WaitForPendingAsync(storage, 1);
            time.Advance(Write);
            Assert.True(storage.CompleteWrite());
            _ = await append;
        }
    }
}
