using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Jobs;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Runtime;
using Polly;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Unit tests for Phase J2: <see cref="WakeupCompanionCodec"/> (golden vectors + Verify snapshots),
/// <see cref="WakeupStreamHandler"/> / <see cref="WakeupLedgerSession"/> (<see cref="FakeTimeProvider"/>),
/// <see cref="WakeupStreamProcessor"/>, <see cref="SiloArchive.Lsm"/> pre-gate <c>local-streams</c> check, and
/// <see cref="NodeRuntime"/> handler/processor parity (orleans.md §5.15, §6).
/// </summary>
public sealed class WakeupEngineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task WakeupCompanionGoldenVectorsDecodeAndRoundTrip()
    {
        var lines = new List<string>();
        foreach (var vector in Cases("orleans", "wakeup-companions.json"))
        {
            var bytes = Hex(vector);
            var spanErr = WakeupCompanionCodec.Decode(bytes, out var spanRec);
            Assert.Null(spanErr);
            var seqErr = WakeupCompanionCodec.Decode(MultiSegment(bytes, 3), out var seqRec);
            Assert.Null(seqErr);
            Assert.Equal(spanRec, seqRec);
            Assert.Equal(bytes, WakeupCompanionCodec.Encode(spanRec));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{vector.GetProperty("name").GetString()} => {spanRec.Target} | {spanRec.JobName} | {spanRec.DueTimeUtcTicks?.ToString(CultureInfo.InvariantCulture) ?? "clear"}"));
        }

        return Verify(string.Join('\n', lines));
    }

    [Fact]
    public Task WakeupCompanionRejectionVectorsMatchOnSpanAndSequence()
    {
        var lines = new List<string>();
        foreach (var vector in Cases("orleans", "wakeup-companion-rejections.json"))
        {
            var bytes = Hex(vector);
            var expectedReason = vector.GetProperty("reason").GetString()!;
            var spanErr = WakeupCompanionCodec.Decode(bytes, out _);
            Assert.NotNull(spanErr);
            Assert.Equal(expectedReason, DecodeRejection.ReasonOf(spanErr));

            var seqErr = WakeupCompanionCodec.Decode(MultiSegment(bytes, 2), out _);
            Assert.NotNull(seqErr);
            Assert.Equal(expectedReason, DecodeRejection.ReasonOf(seqErr));
            lines.Add($"{vector.GetProperty("name").GetString()} => {expectedReason}");
        }

        return Verify(string.Join('\n', lines));
    }

    [Fact]
    public async Task WakeupLedgerSessionHandlesHeapOrderingInFlightAbaClearRescheduleBackoffRollAndStop()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new WakeupOptions
        {
            MaxWakeupBytesPerLedger = 500,
            MaxConcurrentWakeupDispatches = 4,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(4),
        };

        var g1 = GrainId.Create("order", "1");
        var g2 = GrainId.Create("order", "2");
        var g3 = GrainId.Create("order", "3");
        var job = "orleans.messaging.pump";

        var g1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var g1Release = new TaskCompletionSource<DurableJobRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var g2Attempts = 0;
        var g2Completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var g3Dispatches = new List<DateTimeOffset>();
        var g3SecondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchedContexts = new ConcurrentQueue<IJobRunContext>();

        var dispatcher = new FakeDispatcher(async (target, context, ct) =>
        {
            dispatchedContexts.Enqueue(context);
            if (target == g1)
            {
                g1Entered.TrySetResult();
                return await g1Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            if (target == g2)
            {
                var attempt = Interlocked.Increment(ref g2Attempts);
                if (attempt <= 2)
                {
                    throw new InvalidOperationException("transient");
                }

                g2Completed.TrySetResult();
                return DurableJobRunResult.Completed;
            }

            if (target == g3)
            {
                g3Dispatches.Add(time.GetUtcNow());
                if (g3Dispatches.Count == 1)
                {
                    return DurableJobRunResult.RescheduleAt(time.GetUtcNow() + TimeSpan.FromSeconds(10));
                }

                g3SecondDone.TrySetResult();
                return DurableJobRunResult.Completed;
            }

            return DurableJobRunResult.Completed;
        });

        var handler = new WakeupStreamHandler(options, dispatcher, time);
        var ledger = new LedgerId(7);
        var session = (WakeupLedgerSession)handler.OpenLedger(ledger);
        var now = time.GetUtcNow();

        // Schedule g1 at +5s, g2 at +20s, g3 at +2s (earlier than g1), and then clear a 4th grain immediately.
        var g4 = GrainId.Create("order", "4");
        session.OnBatchDurable(Slot.Zero, [
            EncodeRecord(g1, job, now + TimeSpan.FromSeconds(5)),
            EncodeRecord(g2, job, now + TimeSpan.FromSeconds(20)),
            EncodeRecord(g3, job, now + TimeSpan.FromSeconds(2)),
            EncodeRecord(g4, job, now + TimeSpan.FromSeconds(1)),
            EncodeRecord(g4, job, null),
        ]);
        Assert.Equal(3, session.HeapCount);
        Assert.Equal(3, session.EntryCount);

        // Advance +2s: g3 fires first and requests RescheduleAt(+10s -> t=+12s).
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => g3Dispatches.Count == 1, Token);

        // Advance +3s (t=+5s): g1 fires and blocks in flight.
        time.Advance(TimeSpan.FromSeconds(3));
        await g1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // While g1 is InFlight, clear g1 (SetWakeup(null)) -> must NOT resurrect when g1 completes!
        session.OnBatchDurable(new Slot(1), [EncodeRecord(g1, job, null)]);
        g1Release.SetResult(DurableJobRunResult.RescheduleAt(time.GetUtcNow() + TimeSpan.FromSeconds(1)));
        await WaitUntilAsync(() => session.EntryCount == 2, Token);
        Assert.Equal(2, session.HeapCount);

        // Advance +7s (t=+12s): g3 fires its rescheduled run and completes.
        time.Advance(TimeSpan.FromSeconds(7));
        await g3SecondDone.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await WaitUntilAsync(() => session.EntryCount == 1, Token);

        // Advance +8s (t=+20s): g2 fires attempt 1 (throws -> +1s), then attempt 2 (throws -> +2s), then attempt 3 (succeeds).
        time.Advance(TimeSpan.FromSeconds(8));
        await WaitUntilAsync(() => Volatile.Read(ref g2Attempts) == 1, Token);
        await WaitUntilAsync(() => session.HeapCount == 1, Token);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Volatile.Read(ref g2Attempts) == 2, Token);
        await WaitUntilAsync(() => session.HeapCount == 1, Token);
        time.Advance(TimeSpan.FromSeconds(2));
        await g2Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await WaitUntilAsync(() => session.EntryCount == 0, Token);

        // Every dispatched context carries IsEphemeralWakeup == true.
        Assert.All(dispatchedContexts, static ctx => Assert.True(ctx.IsEphemeralWakeup));

        // Exceed MaxWakeupBytesPerLedger (500 bytes) -> CollectLedgersNeedingRoll flags ledger 7.
        var many = new LocalAppendRecord[10];
        for (var i = 0; i < many.Length; i++)
        {
            many[i] = EncodeRecord(GrainId.Create("bulk", i.ToString(CultureInfo.InvariantCulture)), job, time.GetUtcNow() + TimeSpan.FromHours(1));
        }

        session.OnBatchDurable(new Slot(2), many);
        Assert.Equal([ledger], handler.CollectLedgersNeedingRoll());
        Assert.Empty(handler.CollectLedgersNeedingRoll());

        // StopAsync drains cleanly and tolerates trailing OnBatchDurable & Dispose calls.
        await handler.StopAsync();
        session.OnBatchDurable(new Slot(3), [EncodeRecord(g1, job, time.GetUtcNow() + TimeSpan.FromMinutes(1))]);
        session.Dispose();
    }

    [Fact]
    public async Task WakeupLedgerSessionInFlightNewerSetWakeupReEnqueuesWithoutAba()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var g = GrainId.Create("aba", "1");
        var job = "orleans.messaging.pump";
        var count = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<DurableJobRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = new FakeDispatcher(async (_, _, ct) =>
        {
            var n = Interlocked.Increment(ref count);
            if (n == 1)
            {
                firstEntered.TrySetResult();
                return await firstRelease.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            secondDone.TrySetResult();
            return DurableJobRunResult.Completed;
        });

        var handler = new WakeupStreamHandler(new WakeupOptions(), dispatcher, time);
        var session = (WakeupLedgerSession)handler.OpenLedger(new LedgerId(9));
        var now = time.GetUtcNow();

        session.OnBatchDurable(Slot.Zero, [EncodeRecord(g, job, now + TimeSpan.FromSeconds(1))]);
        time.Advance(TimeSpan.FromSeconds(1));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // While dispatch #1 is in flight, a clear and a NEW SetWakeup(+5s) arrive!
        session.OnBatchDurable(new Slot(1), [EncodeRecord(g, job, null), EncodeRecord(g, job, time.GetUtcNow() + TimeSpan.FromSeconds(5))]);
        // Dispatch #1 returns Completed, but Generation changed -> entry MUST be re-enqueued for +5s!
        firstRelease.SetResult(DurableJobRunResult.Completed);
        await WaitUntilAsync(() => session.HeapCount == 1, Token);

        time.Advance(TimeSpan.FromSeconds(5));
        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await handler.StopAsync();
    }

    [Fact]
    public async Task WakeupStreamProcessorFoldsAppliesJitterRespectsCapAndSkipsSucceededOnRetry()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var options = new WakeupOptions
        {
            MaxConcurrentWakeupSchedules = 2,
            OverdueJitterWindow = TimeSpan.FromSeconds(5),
        };

        var gFuture = GrainId.Create("g", "future");
        var gOverdue = GrainId.Create("g", "overdue");
        var gCleared = GrainId.Create("g", "cleared");
        var gFailOnce = GrainId.Create("g", "fail-once");
        var job = "orleans.messaging.pump";

        var scheduled = new ConcurrentQueue<ScheduleJobRequest>();
        var inFlight = 0;
        var maxInFlight = 0;
        var failOnceRemaining = 1;

        var jobManager = new FakeDurableJobManager(async (req, ct) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, current);
            try
            {
                await Task.Yield();
                if (req.Target == gFailOnce && Interlocked.Exchange(ref failOnceRemaining, 0) == 1)
                {
                    throw new IOException("transient schedule failure");
                }

                scheduled.Enqueue(req);
                return new DurableJob { Id = Guid.NewGuid().ToString("N"), Name = req.JobName, DueTime = req.DueTime, TargetGrainId = req.Target, ShardId = "s0" };
            }
            finally
            {
                _ = Interlocked.Decrement(ref inFlight);
            }
        });

        using var processor = new WakeupStreamProcessor(jobManager, options, time);
        var ledgerMeta = new LedgerMetadata(new LedgerId(3), KeyRange.Root, new LedgerLocation.Nodes([NodeId.New()]), 4, new PrimaryClaim(NodeId.New(), SequencerInstanceId.New()), new LedgerStatus.Closed(new Slot(5)));

        // 0 surviving entries -> 0 ScheduleJobAsync calls.
        using (var emptyFold = processor.BeginClosedLedgerFold(ledgerMeta, new Slot(2)))
        {
            emptyFold.Add(Slot.Zero, 0, new ReadOnlySequence<byte>(WakeupCompanionCodec.Encode(new WakeupRecord(gCleared, job, now.UtcTicks + TimeSpan.TicksPerMinute))));
            Assert.True(emptyFold.MemoryBytes > 0);
            emptyFold.Add(new Slot(1), 0, new ReadOnlySequence<byte>(WakeupCompanionCodec.Encode(new WakeupRecord(gCleared, job, null))));
            Assert.Equal(0, emptyFold.MemoryBytes);
            await emptyFold.CommitAsync(Token);
            Assert.Empty(scheduled);
        }

        // Malformed payload throws ArchiveCorruptException.
        using (var badFold = processor.BeginClosedLedgerFold(ledgerMeta, new Slot(1)))
        {
            _ = Assert.Throws<ArchiveCorruptException>(() => badFold.Add(Slot.Zero, 0, new ReadOnlySequence<byte>([0xFF])));
        }

        // Surviving entries: future preserved, overdue jittered in [now, now + 5s), partial failure retries only failed entry.
        var futureDue = now + TimeSpan.FromMinutes(10);
        var overdueDue = now - TimeSpan.FromMinutes(2);
        using var fold = processor.BeginClosedLedgerFold(ledgerMeta, new Slot(5));
        fold.Add(Slot.Zero, 0, new ReadOnlySequence<byte>(WakeupCompanionCodec.Encode(new WakeupRecord(gFuture, job, futureDue.UtcTicks))));
        fold.Add(new Slot(1), 0, new ReadOnlySequence<byte>(WakeupCompanionCodec.Encode(new WakeupRecord(gOverdue, job, overdueDue.UtcTicks))));
        fold.Add(new Slot(2), 0, new ReadOnlySequence<byte>(WakeupCompanionCodec.Encode(new WakeupRecord(gFailOnce, job, futureDue.UtcTicks))));

        _ = await Assert.ThrowsAsync<IOException>(() => fold.CommitAsync(Token));
        // gFuture and gOverdue succeeded and were removed; only gFailOnce remains!
        Assert.Equal(2, scheduled.Count);
        Assert.True(fold.MemoryBytes > 0);

        // Retry commits ONLY gFailOnce (no duplicate scheduling of gFuture or gOverdue).
        await fold.CommitAsync(Token);
        Assert.Equal(0, fold.MemoryBytes);
        Assert.Equal(3, scheduled.Count);
        Assert.True(maxInFlight <= options.MaxConcurrentWakeupSchedules);

        var byTarget = scheduled.ToDictionary(static r => r.Target);
        Assert.Equal(futureDue, byTarget[gFuture].DueTime);
        Assert.InRange(byTarget[gOverdue].DueTime, now, now + options.OverdueJitterWindow);
        Assert.Equal(futureDue, byTarget[gFailOnce].DueTime);
    }

    [Fact]
    public async Task SiloArchiveLsmRefusesGateBeforePassOnLocalStreamsMismatchAndAllowsOtherParamMismatch()
    {
        var objects = new InMemoryObjectStore();
        var prefix = ArchiveStorePrefix.Of("testcluster");
        var siloArchive = new SiloArchive.Lsm(objects, prefix);
        var storedParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = "json",
            ["max-body-bytes"] = "4194304",
            [JournalStore.LocalStreamsParameter] = "orleans.wakeup",
        };
        var identity = await siloArchive.FirstRecordAsync(storedParams, Token);

        // Case 1: local-streams mismatch refuses BEFORE Gate.Pass!
        var mismatchedLocal = new SiloArchive.Lsm(objects, prefix);
        var callerParamsBadStreams = new Dictionary<string, string>(storedParams, StringComparer.Ordinal)
        {
            [JournalStore.LocalStreamsParameter] = "",
        };
        var ex1 = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(() =>
            mismatchedLocal.CheckAsync(identity, callerParamsBadStreams, ResiliencePipeline.Empty, Token));
        Assert.True(ex1.IsParameter);
        Assert.Equal(JournalStore.LocalStreamsParameter, ex1.Field);
        Assert.False(mismatchedLocal.Gate.IsPassed);

        // Case 2: local-streams matches, max-body-bytes mismatches -> Gate PASSES first (OR-I8), then throws parameter mismatch!
        var mismatchedBody = new SiloArchive.Lsm(objects, prefix);
        var callerParamsBadBody = new Dictionary<string, string>(storedParams, StringComparer.Ordinal)
        {
            ["max-body-bytes"] = "1048576",
        };
        var ex2 = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(() =>
            mismatchedBody.CheckAsync(identity, callerParamsBadBody, ResiliencePipeline.Empty, Token));
        Assert.True(ex2.IsParameter);
        Assert.Equal("max-body-bytes", ex2.Field);
        Assert.True(mismatchedBody.Gate.IsPassed);
    }

    [Fact]
    public void NodeRuntimeValidateLocalStreamParityRejectsMismatchedSets()
    {
        var time = new FakeTimeProvider();
        var handler = new WakeupStreamHandler(new WakeupOptions(), new FakeDispatcher((_, _, _) => ValueTask.FromResult(DurableJobRunResult.Completed)), time);
        using var processor = new WakeupStreamProcessor(new FakeDurableJobManager((_, _) => throw new NotSupportedException()), new WakeupOptions(), time);

        // Matching sets pass.
        NodeRuntime.ValidateLocalStreamParity([], []);
        NodeRuntime.ValidateLocalStreamParity([handler], [processor]);

        // Handler without processor throws.
        _ = Assert.Throws<ArgumentException>(() => NodeRuntime.ValidateLocalStreamParity([handler], []));

        // Processor without handler throws.
        _ = Assert.Throws<ArgumentException>(() => NodeRuntime.ValidateLocalStreamParity([], [processor]));
    }

    private static LocalAppendRecord EncodeRecord(GrainId target, string jobName, DateTimeOffset? dueTime) =>
        new(WakeupStreamHandler.WakeupStream, WakeupCompanionCodec.Encode(new WakeupRecord(target, jobName, dueTime?.UtcTicks)));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int current;
        while (candidate > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static ReadOnlySequence<byte> MultiSegment(byte[] bytes, int chunk)
    {
        if (bytes.Length <= chunk)
        {
            return new ReadOnlySequence<byte>(bytes);
        }

        var first = new Segment(bytes.AsMemory(0, chunk), 0);
        var last = first;
        for (var i = chunk; i < bytes.Length; i += chunk)
        {
            last = last.Append(bytes.AsMemory(i, Math.Min(chunk, bytes.Length - i)));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> nextMemory)
        {
            var next = new Segment(nextMemory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    private sealed class FakeDispatcher(Func<GrainId, IJobRunContext, CancellationToken, ValueTask<DurableJobRunResult>> dispatch) : IGrainWakeupDispatcher
    {
        public ValueTask<DurableJobRunResult> DispatchAsync(GrainId target, IJobRunContext context, CancellationToken cancellationToken) =>
            dispatch(target, context, cancellationToken);
    }

    private sealed class FakeDurableJobManager(Func<ScheduleJobRequest, CancellationToken, Task<DurableJob>> schedule) : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) =>
            schedule(request, cancellationToken);

        public Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken) =>
            Task.FromResult(false);
    }
}
