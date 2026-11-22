using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

public sealed class WalWriterTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task ConcurrentAppendsAreEachDurableOnceAndReadBack()
    {
        var storage = new SimulatedWalStorage(seed: 3);
        var payloads = Enumerable.Range(0, 400).Select(static index => Bytes(index % 37 * 311, index)).ToArray();
        WalPosition[] positions;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(pageSize: 512, fileSize: 64 * 512, window: 8), TestContext.Current.CancellationToken))
        {
            positions = await Task.WhenAll(payloads.Select(payload => Task.Run(() => AppendAsync(wal, payload).AsTask())));
            Assert.Equal(positions.Length, positions.Distinct().Count());
            for (var index = 0; index < payloads.Length; index += 37)
            {
                Assert.Equal(payloads[index], (await wal.ReadAsync(positions[index], TestContext.Current.CancellationToken)).ToBytes());
            }
        }

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, Options(pageSize: 512), TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(reopened);
        Assert.All(entries, static entry => Assert.True(entry.IsRecord));
        Assert.Equal(positions.Order(), entries.Select(static entry => entry.Position));
        var byPosition = positions.Zip(payloads).ToDictionary(static pair => pair.First, static pair => pair.Second);
        Assert.All(entries, entry => Assert.Equal(byPosition[entry.Position], entry.Payload));
    }

    [Fact]
    public async Task ALoneRecordSpanningPagesIsOneWrite()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        var issued = storage.IssuedWriteCount;

        // 9,000 bytes span three pages, the last of them open when the record is placed.
        var payload = Bytes(9000, 7);
        var position = await AppendAsync(wal, payload);

        Assert.Equal(issued + 1, storage.IssuedWriteCount);
        Assert.Equal(payload, (await wal.ReadAsync(position, TestContext.Current.CancellationToken)).ToBytes());
    }

    /// <summary>
    /// A record just over the largest large page reads back, by position and by scan, as a chain of pages each of the
    /// largest size (conventions.md §1).
    /// </summary>
    [Fact]
    public async Task ARecordOverTheLargestPageReadsBackInPagesOfTheLargestSize()
    {
        const int Largest = ArcBufferPagePool.LargestPageSize;
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(fileSize: 4L << 20), TestContext.Current.CancellationToken);
        var payload = Bytes(Largest + 1, 11);
        var position = await AppendAsync(wal, payload);
        await WaitForFrontierAsync(wal, position);

        Assert.True((await wal.ReadAsync(position, TestContext.Current.CancellationToken)).TryGetValue(out ArcBuffer read));
        using (read)
        {
            Assert.Equal(payload, read.ToArray());
            Assert.Equal([Largest, Largest], read.Pages.Select(static page => page.Capacity));
        }

        var scanned = 0;
        await foreach (var entry in wal.ReadAllAsync(default, TestContext.Current.CancellationToken))
        {
            Assert.True(entry.TryGetValue(out WalRecord record));
            Assert.Equal(payload, record.Payload.ToArray());
            Assert.Equal([Largest, Largest], record.Payload.Pages.Select(static page => page.Capacity));
            scanned++;
        }

        Assert.Equal(1, scanned);
    }

    [Fact]
    public async Task PositionsFollowAdmissionOrder()
    {
        var storage = new SimulatedWalStorage();
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(fileSize: 3 * 4096), TestContext.Current.CancellationToken);
        var positions = new List<WalPosition>();
        for (var index = 0; index < 40; index++)
        {
            positions.Add(await AppendAsync(wal, Bytes(index * 97, index)));
        }

        Assert.Equal(positions.Order(), positions);
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }

    [Fact]
    public async Task ARecordLargerThanTheWindowAndTheFileIsWrittenInWaves()
    {
        var storage = new SimulatedWalStorage();
        var payload = Bytes(100_000, 5);
        var options = Options(pageSize: 512, fileSize: 8 * 512, window: 2) with { PreallocateFiles = false };
        WalPosition position;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            _ = await AppendAsync(wal, [1]);
            position = await AppendAsync(wal, payload);
            var after = await AppendAsync(wal, [2]);
            Assert.Equal(payload, (await wal.ReadAsync(position, TestContext.Current.CancellationToken)).ToBytes());
            Assert.Equal(new WalPosition(2, 512 + 4), position);
            Assert.Equal(new WalPosition(3, 512 + 4), after);
            Assert.Equal(100_000, wal.ReadCopyBytes);
        }

        Assert.True(storage.ReadDurable(LogPath, 2).Length > 8 * 512);
        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        Assert.Equal([[1], payload, [2]], (await ReadAllAsync(reopened)).Select(static entry => entry.Payload!));
    }

    [Fact]
    public async Task OutOfOrderCompletionHoldsTheFrontier()
    {
        for (var seed = 0; seed < 16; seed++)
        {
            var storage = new SimulatedWalStorage(seed) { HoldWrites = true };
            await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
            using var first = new RecordingRequest(default, Buffer([1]));
            using var second = new RecordingRequest(default, Buffer([2]));
            await wal.WriteAsync(first, TestContext.Current.CancellationToken);
            await WalFormatTests.WaitForPendingAsync(storage, 1);
            await wal.WriteAsync(second, TestContext.Current.CancellationToken);
            await WalFormatTests.WaitForPendingAsync(storage, 2);

            Assert.True(storage.CompleteWrite());
            var completed = await Task.WhenAny(first.Completion, second.Completion);
            if (completed == second.Completion)
            {
                Assert.False(first.Completion.IsCompleted);
                Assert.Equal(new WalPosition(1, 4096), wal.DurableFrontier);
                Assert.True(storage.CompleteWrite());
                _ = await first.Completion;
                await WaitForAsync(() => wal.DurableFrontier == new WalPosition(1, 2 * 4096 + 4 + 4 + 1));
                return;
            }

            Assert.True(storage.CompleteWrite());
            _ = await second.Completion;
        }

        Assert.Fail("No seed completed the later group first.");
    }

    /// <summary>
    /// A failed write is reported once, by the storage, and stops the log as it stops the process (conventions.md §14):
    /// its records and every later request fail, and so does the record of a write already in flight once it completes,
    /// since a stopped log answers nothing; each accepted request gets exactly one outcome (WAL-I8), and disposal
    /// completes. The restarted process's open replays the record whose write completed and nothing of the failed write.
    /// </summary>
    [Fact]
    public async Task AFailedWriteStopsTheLogAndTheRestartedProcessRecoversTheAcknowledgedRecords()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        using var durable = new RecordingRequest(default, Buffer([1]));
        await wal.WriteAsync(durable, TestContext.Current.CancellationToken);
        await WalFormatTests.WaitForPendingAsync(storage, 1);
        var injected = new IOException("injected");
        storage.WriteFault = (_, _, _, _) => injected;
        var since = FatalErrors.ReportedCount;
        using var failed = new RecordingRequest(default, Buffer([2]));
        await wal.WriteAsync(failed, TestContext.Current.CancellationToken);

        Assert.Same(injected, (await Assert.ThrowsAsync<IOException>(() => failed.Completion)).InnerException);
        Assert.Same(injected, AssertReported(since, FatalErrorSite.HandleWriteGather, LogFile(1)).Failure.Exception);
        Assert.Same(injected, (await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(wal, [3]))).InnerException);
        Assert.True(storage.CompleteWrite());
        Assert.Same(injected, (await Assert.ThrowsAsync<IOException>(() => durable.Completion)).InnerException);
        await AssertAnswersNothingAsync(wal);
        await wal.DisposeAsync();
        Assert.Equal(1, durable.Outcomes);
        Assert.Equal(1, failed.Outcomes);

        await using var reopened = await WriteAheadLog.OpenAsync(storage.Crash(operatingSystem: false), LogPath, Options(), TestContext.Current.CancellationToken);
        Assert.Equal([[1]], (await ReadAllAsync(reopened)).Select(static entry => entry.Payload));
    }

    /// <summary>
    /// An outcome callback which throws leaves its caller's state behind the log, so the log reports it once, as the
    /// defect <c>WalOutcomeCallback</c>, and stops (conventions.md §14): it answers nothing more,
    /// and disposal completes.
    /// </summary>
    [Fact]
    public async Task AThrowingOutcomeCallbackIsReportedAndStopsTheLog()
    {
        var storage = new SimulatedWalStorage();
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        var since = FatalErrors.ReportedCount;
        using var throwing = new ThrowingRequest(Buffer([1]));
        await wal.WriteAsync(throwing, TestContext.Current.CancellationToken);

        _ = await throwing.Delivered;
        await WaitForStopAsync(wal);
        Assert.Same(throwing.Failure, AssertDefect(since, "WalOutcomeCallback").Failure.Exception);
        Assert.Same(throwing.Failure, (await Assert.ThrowsAsync<IOException>(async () => await AppendAsync(wal, [2]))).InnerException);
        await AssertAnswersNothingAsync(wal);
        await wal.DisposeAsync();
        Assert.Equal(1, throwing.Outcomes);
    }

    [Fact]
    public async Task EmptyRecordsConsumeThePageBufferBudget()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options() with { PageBufferBudgetBytes = 4096 }, TestContext.Current.CancellationToken);
        var requests = new List<RecordingRequest>();
        var admissions = new List<Task>();
        while (admissions.Count < 1000 && admissions.TrueForAll(static admission => admission.IsCompleted))
        {
            var request = new RecordingRequest(default, default);
            requests.Add(request);
            admissions.Add(Admit(wal, request));
        }

        Assert.False(admissions[^1].IsCompleted);
        Assert.True(requests.Count <= 4096 / 16 + 1);
        while (!requests.TrueForAll(static request => request.Completion.IsCompleted))
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        await Task.WhenAll(admissions);
        Assert.All(requests, static request => Assert.Equal(1, request.Outcomes));
    }

    [Fact]
    public async Task CancellationAppliesOnlyBeforeAcceptance()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        await using var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options() with { PageBufferBudgetBytes = 4096 }, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var accepted = new RecordingRequest(default, Buffer(new byte[4096]));
        await wal.WriteAsync(accepted, cancellation.Token);
        using var waiting = new RecordingRequest(default, Buffer([1]));
        var admission = wal.WriteAsync(waiting, cancellation.Token).AsTask();
        await PauseAsync(10);
        Assert.False(admission.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
        while (!accepted.Completion.IsCompleted)
        {
            _ = storage.CompleteWrite();
            await PauseAsync();
        }

        _ = await accepted.Completion;
        Assert.Equal(0, waiting.Outcomes);
    }

    [Fact]
    public async Task DirectIoWritesOnlyWholeAlignedPages()
    {
        var storage = new SimulatedWalStorage(minimumPageSize: 4096, requiresAlignedBuffers: true);
        var payloads = Enumerable.Range(0, 30).Select(static index => Bytes(index * 500, index)).ToArray();
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(fileSize: 8 * 4096), TestContext.Current.CancellationToken))
        {
            _ = await Task.WhenAll(payloads.Select(payload => AppendAsync(wal, payload).AsTask()));
        }

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken);
        Assert.Equal(
            payloads.Select(Convert.ToHexString).Order(),
            (await ReadAllAsync(reopened)).Select(static entry => Convert.ToHexString(entry.Payload!)).Order());
    }

    private static Task Admit(WriteAheadLog wal, WalWriteRequest request) => wal.WriteAsync(request, TestContext.Current.CancellationToken).AsTask();

    private static ArcBuffer Buffer(byte[] bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await PauseAsync();
        }
    }

    /// <summary>
    /// A request whose durable callback records its position and then throws.
    /// </summary>
    private sealed class ThrowingRequest : WalWriteRequest, IDisposable
    {
        private readonly TaskCompletionSource<WalPosition> _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ThrowingRequest(ArcBuffer payload) => Payload = payload;

        public Exception Failure { get; } = new InvalidOperationException("The outcome callback failed.");

        public Task<WalPosition> Delivered => _delivered.Task;

        public int Outcomes { get; private set; }

        public void Dispose() => Payload.Dispose();

        protected internal override void OnDurable(WalDurableRecord record)
        {
            Outcomes++;
            _ = _delivered.TrySetResult(record.Position);
            throw Failure;
        }

        protected internal override void OnFailed(Exception error)
        {
            Outcomes++;
            _ = _delivered.TrySetException(error);
        }
    }
}
