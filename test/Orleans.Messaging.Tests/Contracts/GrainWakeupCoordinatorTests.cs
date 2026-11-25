using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Durable.Messaging.Tests.Contracts;

/// <summary>
/// Unit tests for Phase J3: <see cref="JournaledStateManager"/> internal double-buffered companion staging and
/// <see cref="GrainWakeupCoordinator"/> over <see cref="FakeTimeProvider"/> (orleans.md §5.15).
/// </summary>
[TestCategory("BVT")]
public sealed class GrainWakeupCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task JournaledStateManagerStagingCoversDoubleBufferingEmptyAppendEmptyReplaceDefaultAndReset()
    {
        var storage = new RecordingMessageStorage();
        await using var harness = CreateManager(storage);
        var manager = harness.Manager;

        Assert.True(manager.SupportsMessageWrites);

        // default(JournalMessageRecord) rejected immediately.
        Assert.Throws<ArgumentException>(() => manager.StageMessageWrite(default));

        // First write persists the initial stream directory entry; second write with no state change has an empty delta,
        // so staging a message on it must throw InvalidOperationException (and fence).
        await manager.WriteStateAsync(Token);
        manager.StageMessageWrite(new JournalMessageRecord(WakeupMessageCodec.ChannelName, new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteStateAsync(Token).AsTask());

        // Fresh manager: empty Replace snapshot with staged message succeeds via ReplaceWithMessagesAsync.
        var storage2 = new RecordingMessageStorage();
        await using var harness2 = CreateManager(storage2);
        var m2 = harness2.Manager;
        var v2 = harness2.Value;
        v2.Value = "seed";
        await m2.WriteStateAsync(Token);
        Assert.Single(storage2.Writes);
        Assert.Equal("Append", storage2.Writes[0].Kind);
        Assert.Empty(storage2.Writes[0].Messages);

        // Double-buffer reentrancy: hold write #1 inside AppendWithMessagesAsync, stage message #2 while suspended,
        // and verify write #1 carries only message #1 while write #2 carries only message #2!
        var enteredWrite1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage2.HoldNextWrite = async () =>
        {
            enteredWrite1.TrySetResult();
            await releaseWrite1.Task.ConfigureAwait(false);
        };

        v2.Value = "w1";
        m2.StageMessageWrite(new JournalMessageRecord("sim.stream", new byte[] { 0x11 }));
        var write1Task = m2.WriteStateAsync(Token).AsTask();
        await enteredWrite1.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // While write #1 is in flight, stage message #2 and a second change.
        v2.Value = "w2";
        m2.StageMessageWrite(new JournalMessageRecord("sim.stream", new byte[] { 0x22 }));
        var write2Task = m2.WriteStateAsync(Token).AsTask();

        releaseWrite1.SetResult();
        await Task.WhenAll(write1Task, write2Task);

        Assert.Equal(3, storage2.Writes.Count);
        Assert.Equal("AppendWithMessages", storage2.Writes[1].Kind);
        Assert.Equal([0x11], Assert.Single(storage2.Writes[1].Messages).Payload.ToArray());
        Assert.Equal("AppendWithMessages", storage2.Writes[2].Kind);
        Assert.Equal([0x22], Assert.Single(storage2.Writes[2].Messages).Payload.ToArray());

        // Empty Replace snapshot with messages is allowed (triggered via storage.CompactionRequested).
        storage2.CompactionRequested = true;
        m2.StageMessageWrite(new JournalMessageRecord("sim.stream", new byte[] { 0x33 }));
        await m2.WriteStateAsync(Token);
        storage2.CompactionRequested = false;
        Assert.Equal("ReplaceWithMessages", storage2.Writes[^1].Kind);
        Assert.Equal([0x33], Assert.Single(storage2.Writes[^1].Messages).Payload.ToArray());

        // DeleteStateAsync clears staged messages so a subsequent write carries none.
        m2.StageMessageWrite(new JournalMessageRecord("sim.stream", new byte[] { 0x44 }));
        await m2.DeleteStateAsync(Token);
        v2.Value = "after-delete";
        await m2.WriteStateAsync(Token);
        Assert.Equal("Append", storage2.Writes[^1].Kind);
        Assert.Empty(storage2.Writes[^1].Messages);
    }

    [Fact]
    public async Task CoordinatorEmitsClampedCoalescedAndClearedWakeupsAndSeedsOnRecovery()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var storage = new RecordingMessageStorage();
        var jobManager = new FakeJobManager();
        var grainId = GrainId.Create("order", "42");
        var source = new FakeWakeupSource("orleans.messaging.pump");

        await using var harness = CreateManager(storage, grainId, initialize: false);
        var coordinator = new GrainWakeupCoordinator(harness.GrainContext, harness.Manager, jobManager, time);
        coordinator.RegisterSource(source, TimeSpan.FromSeconds(1));

        // Seed pending work BEFORE recovery so OnRecoveryCompleted sets _hadPendingWork = true!
        source.NextDue = now + TimeSpan.FromSeconds(5);
        await harness.Manager.InitializeAsync(Token);

        // Immediately clearing pending work on first write MUST emit SetWakeup(null) because _hadPendingWork was seeded true!
        source.NextDue = null;
        harness.Value.Value = "cleared-after-recovery";
        await harness.Manager.WriteStateAsync(Token);
        Assert.Equal(0, jobManager.ScheduleCalls);
        var clearRec = DecodeSingleWakeup(storage.Writes[^1].Messages);
        Assert.Equal(grainId, clearRec.Target);
        Assert.Equal("orleans.messaging.pump", clearRec.JobName);
        Assert.Null(clearRec.DueTimeUtcTicks);

        // Another write with still no pending work emits ZERO messages!
        harness.Value.Value = "still-idle";
        await harness.Manager.WriteStateAsync(Token);
        Assert.Empty(storage.Writes[^1].Messages);

        // Immediate due work (due < now + RecheckDelay) clamps to now + 1s.
        source.NextDue = now;
        harness.Value.Value = "work-1";
        await harness.Manager.WriteStateAsync(Token);
        var clampedRec = DecodeSingleWakeup(storage.Writes[^1].Messages);
        Assert.Equal((now + TimeSpan.FromSeconds(1)).UtcTicks, clampedRec.DueTimeUtcTicks);

        // Redundant later re-emission while now < prev and clamped >= prev is coalesced (0 messages)!
        time.Advance(TimeSpan.FromMilliseconds(200));
        source.NextDue = time.GetUtcNow() + TimeSpan.FromSeconds(5);
        harness.Value.Value = "work-2-coalesced";
        await harness.Manager.WriteStateAsync(Token);
        Assert.Empty(storage.Writes[^1].Messages);

        // Once clock passes prev (now >= prev), next write re-emits SetWakeup!
        time.Advance(TimeSpan.FromSeconds(1));
        var tNow = time.GetUtcNow();
        source.NextDue = tNow + TimeSpan.FromSeconds(10);
        harness.Value.Value = "work-3-after-prev";
        await harness.Manager.WriteStateAsync(Token);
        var afterPrevRec = DecodeSingleWakeup(storage.Writes[^1].Messages);
        Assert.Equal((tNow + TimeSpan.FromSeconds(10)).UtcTicks, afterPrevRec.DueTimeUtcTicks);

        // Earlier due time than prev re-emits immediately!
        source.NextDue = tNow + TimeSpan.FromSeconds(3);
        harness.Value.Value = "work-4-earlier";
        await harness.Manager.WriteStateAsync(Token);
        var earlierRec = DecodeSingleWakeup(storage.Writes[^1].Messages);
        Assert.Equal((tNow + TimeSpan.FromSeconds(3)).UtcTicks, earlierRec.DueTimeUtcTicks);

        Assert.Equal(0, jobManager.ScheduleCalls);
    }

    [Fact]
    public async Task CoordinatorExecuteJobHandlesEphemeralVsBackingAdoptionAndLaterDedup()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var storage = new RecordingMessageStorage();
        var jobManager = new FakeJobManager();
        var grainId = GrainId.Create("order", "99");
        var source = new FakeWakeupSource("orleans.messaging.pump");

        await using var harness = CreateManager(storage, grainId);
        var coordinator = new GrainWakeupCoordinator(harness.GrainContext, harness.Manager, jobManager, time);
        coordinator.RegisterSource(source, TimeSpan.FromSeconds(1));

        // 1. Ephemeral wakeup runs due work, reschedules itself, and NEVER sets WakeupJobId!
        source.NextDue = now + TimeSpan.FromSeconds(10);
        var ephemeralCtx = MakeContext(grainId, "ephemeral-1", source.JobName, now, isEphemeral: true);
        var ephemeralRes = await coordinator.ExecuteJobAsync(source, ephemeralCtx, _ => ValueTask.CompletedTask, Token);
        Assert.True(ephemeralRes.IsRescheduleRequested);
        Assert.Equal(now + TimeSpan.FromSeconds(10), ephemeralRes.RescheduleTime);
        Assert.Null(coordinator.GetTrackedBackingJobId(source));

        // 2. Backing job B1 arrives -> tracked at now + 10s.
        var b1Ctx = MakeContext(grainId, "backing-1", source.JobName, now, isEphemeral: false);
        var b1Res = await coordinator.ExecuteJobAsync(source, b1Ctx, _ => ValueTask.CompletedTask, Token);
        Assert.True(b1Res.IsRescheduleRequested);
        Assert.Equal("backing-1", coordinator.GetTrackedBackingJobId(source));

        // 3. Redundant later backing job B2 arrives (next = now + 20s >= tracked now + 10s) -> completes without replacing B1!
        source.NextDue = now + TimeSpan.FromSeconds(20);
        var b2Ctx = MakeContext(grainId, "backing-2", source.JobName, now, isEphemeral: false);
        var b2Res = await coordinator.ExecuteJobAsync(source, b2Ctx, _ => ValueTask.CompletedTask, Token);
        Assert.Equal(DurableJobRunStatus.Completed, b2Res.Status);
        Assert.Equal("backing-1", coordinator.GetTrackedBackingJobId(source));

        // 4. Earlier post-roll backing job B3 arrives (next = now + 3s < tracked now + 10s) -> ADOPTED!
        source.NextDue = now + TimeSpan.FromSeconds(3);
        var b3Ctx = MakeContext(grainId, "backing-3", source.JobName, now, isEphemeral: false);
        var b3Res = await coordinator.ExecuteJobAsync(source, b3Ctx, _ => ValueTask.CompletedTask, Token);
        Assert.True(b3Res.IsRescheduleRequested);
        Assert.Equal(now + TimeSpan.FromSeconds(3), b3Res.RescheduleTime);
        Assert.Equal("backing-3", coordinator.GetTrackedBackingJobId(source));

        // 5. Ephemeral wakeup arriving while B3 is tracked leaves B3 intact even when source has no more work!
        source.NextDue = null;
        var ephemeralClearCtx = MakeContext(grainId, "ephemeral-2", source.JobName, now, isEphemeral: true);
        var ephemeralClearRes = await coordinator.ExecuteJobAsync(source, ephemeralClearCtx, _ => ValueTask.CompletedTask, Token);
        Assert.Equal(DurableJobRunStatus.Completed, ephemeralClearRes.Status);
        Assert.Equal("backing-3", coordinator.GetTrackedBackingJobId(source));
    }

    [Fact]
    public async Task CoordinatorFallsBackToScheduleJobAsyncWhenCompanionWritesUnsupported()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var plainStorage = new PlainNonCompanionStorage();
        var jobManager = new FakeJobManager();
        var grainId = GrainId.Create("fallback", "1");
        var source = new FakeWakeupSource("orleans.messaging.pump");

        await using var harness = CreateManager(plainStorage, grainId);
        var coordinator = new GrainWakeupCoordinator(harness.GrainContext, harness.Manager, jobManager, time);
        coordinator.RegisterSource(source, TimeSpan.FromSeconds(1));

        Assert.False(coordinator.SupportsMessageWrites);
        await coordinator.EnsureFallbackWakeupAsync(source, now, CancellationToken.None, Token);
        Assert.Equal(1, jobManager.ScheduleCalls);
        Assert.Equal(now + TimeSpan.FromSeconds(1), Assert.Single(jobManager.Requests).DueTime);
    }

    private static WakeupRecord DecodeSingleWakeup(JournalMessageRecord[] messages)
    {
        var rec = Assert.Single(messages);
        Assert.Equal(WakeupMessageCodec.ChannelName, rec.Channel);
        Assert.Null(WakeupMessageCodec.Decode(rec.Payload.Span, out var decoded));
        return decoded;
    }

    private static IJobRunContext MakeContext(GrainId target, string id, string jobName, DateTimeOffset due, bool isEphemeral) =>
        new JobRunContext(
            new DurableJob { Id = id, Name = jobName, DueTime = due, TargetGrainId = target, ShardId = "s0" },
            Guid.NewGuid().ToString("N"),
            retryCount: 1,
            isEphemeralWakeup: isEphemeral);

    private static TestHarness CreateManager(IJournalStorage storage, GrainId? grainId = null, bool initialize = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddJsonJournalFormat(tryAdd: true);
        services.Configure<JsonJournalOptions>(static o => o.AddTypeInfoResolver(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()));
        var sp = services.BuildServiceProvider();
        var shared = new JournaledStateManagerShared(
            NullLogger<JournaledStateManager>.Instance,
            Microsoft.Extensions.Options.Options.Create(new JournaledStateManagerOptions()),
            TimeProvider.System,
            sp);
        var ctx = new StubGrainContext(grainId ?? GrainId.Create("test", "1"), sp);
        var manager = new JournaledStateManager(shared, storage, ctx);
        var valueCodec = sp.GetRequiredKeyedService<IDurableValueCommandCodec<string>>(JsonLinesJournalFormat.JournalFormatKey);
        var value = new DurableValue<string>("v", manager, valueCodec);
        if (initialize)
        {
            manager.InitializeAsync(Token).AsTask().GetAwaiter().GetResult();
        }

        return new TestHarness(sp, ctx, manager, value);
    }

    private sealed class TestHarness(ServiceProvider sp, IGrainContext grainContext, JournaledStateManager manager, DurableValue<string> value) : IAsyncDisposable
    {
        public IGrainContext GrainContext { get; } = grainContext;
        public JournaledStateManager Manager { get; } = manager;
        public DurableValue<string> Value { get; } = value;

        public async ValueTask DisposeAsync()
        {
            ((IDisposable)Manager).Dispose();
            await sp.DisposeAsync();
        }
    }

    private sealed class FakeWakeupSource(string jobName) : IGrainWakeupSource
    {
        public string JobName { get; } = jobName;
        public DateTimeOffset? NextDue { get; set; }
        public DateTimeOffset? GetNextDueTime(DateTimeOffset now) => NextDue;
    }

    private sealed class FakeJobManager : ILocalDurableJobManager
    {
        public List<ScheduleJobRequest> Requests { get; } = [];
        public int ScheduleCalls => Requests.Count;

        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new DurableJob
            {
                Id = $"job-{Requests.Count}",
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "shard-0",
            });
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken) => Task.FromResult(false);
    }

    private sealed class PlainNonCompanionStorage : IJournalStorage
    {
        public bool IsCompactionRequested => false;
        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            using var writer = new Orleans.Serialization.Buffers.ArcBufferWriter();
            consumer.Read(new JournalBufferReader(writer.Reader, isCompleted: true), JournalMetadata.Empty);
            return ValueTask.CompletedTask;
        }
        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingMessageStorage : IMessageJournalStorage
    {
        public List<(string Kind, byte[] Body, JournalMessageRecord[] Messages)> Writes { get; } = [];
        public bool CompactionRequested { get; set; }
        public Func<Task>? HoldNextWrite { get; set; }
        public bool IsCompactionRequested => CompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            using var writer = new Orleans.Serialization.Buffers.ArcBufferWriter();
            consumer.Read(new JournalBufferReader(writer.Reader, isCompleted: true), JournalMetadata.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Writes.Add(("Append", value.ToArray(), []));
            return ValueTask.CompletedTask;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Writes.Add(("Replace", value.ToArray(), []));
            return ValueTask.CompletedTask;
        }

        public async ValueTask AppendWithMessagesAsync(ReadOnlySequence<byte> value, ReadOnlyMemory<JournalMessageRecord> messages, CancellationToken cancellationToken)
        {
            var hold = HoldNextWrite;
            HoldNextWrite = null;
            var body = value.ToArray();
            var copied = messages.ToArray();
            if (hold is not null)
            {
                await hold().ConfigureAwait(false);
            }

            Writes.Add(("AppendWithMessages", body, copied));
        }

        public ValueTask ReplaceWithMessagesAsync(ReadOnlySequence<byte> value, ReadOnlyMemory<JournalMessageRecord> messages, CancellationToken cancellationToken)
        {
            Writes.Add(("ReplaceWithMessages", value.ToArray(), messages.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class StubGrainContext(GrainId grainId, IServiceProvider services) : IGrainContext
    {
        public GrainReference GrainReference => throw new NotSupportedException();
        public GrainId GrainId { get; } = grainId;
        public object? GrainInstance => null;
        public ActivationId ActivationId { get; } = ActivationId.NewId();
        public GrainAddress Address { get; } = new();
        public IServiceProvider ActivationServices { get; } = services;
        public IGrainLifecycle ObservableLifecycle => throw new NotSupportedException();
        public IWorkItemScheduler Scheduler => throw new NotSupportedException();
        public Task Deactivated => Task.CompletedTask;
        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
        public TComponent? GetComponent<TComponent>() where TComponent : class => null;
        public object? GetComponent(Type componentType) => null;
        public object GetTarget() => throw new NotSupportedException();
        public TTarget GetTarget<TTarget>() where TTarget : class => throw new NotSupportedException();
        public void SetComponent<TComponent>(TComponent? value) where TComponent : class { }
        public void ReceiveMessage(object message) => throw new NotSupportedException();
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }
        public void Rehydrate(IRehydrationContext context) { }
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
    }
}
