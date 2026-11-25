using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Journaling.Json;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableJobs.Tests;

/// <summary>
/// Unit, boundary, concurrency, serialization, and CsCheck model-based tests for <see cref="IDurableJobQueue"/>
/// (v6 §7.3, §8.5).
/// </summary>
[TestCategory("BVT"), TestCategory("DurableJobs")]
public sealed class DurableJobQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BoundariesIdempotencyAndConflictsAreEnforced()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        await using var harness = await QueueHarness.CreateAsync(
            time,
            options: new DurableJobQueueOptions
            {
                MaxPendingJobs = 2,
                MaxMetadataBytesPerJob = 16,
            });

        var due1 = time.GetUtcNow() + TimeSpan.FromSeconds(10);

        // Validation: invalid JobName, reserved WakeupJobName, invalid JobId, oversized UTF-8 metadata.
        Assert.Throws<ArgumentException>(() => harness.Queue.Schedule(new GrainJobRequest { JobName = " ", DueTime = due1 }));
        Assert.Throws<ArgumentException>(() => harness.Queue.Schedule(new GrainJobRequest { JobName = DurableJobQueue.WakeupJobName, DueTime = due1 }));
        Assert.Throws<ArgumentException>(() => harness.Queue.Schedule(new GrainJobRequest { JobId = "   ", JobName = "work", DueTime = due1 }));
        Assert.Throws<ArgumentException>(() => harness.Queue.Schedule(new GrainJobRequest { JobId = new string('a', GrainJobRequest.MaxJobIdLength + 1), JobName = "work", DueTime = due1 }));
        Assert.Throws<ArgumentException>(() => harness.Queue.Schedule(new GrainJobRequest { JobName = new string('n', GrainJobRequest.MaxJobNameLength + 1), DueTime = due1 }));

        // Multi-byte UTF-8 metadata check: "é" is 2 bytes, 9 × 2 = 18 > 16 bytes!
        Assert.Throws<ArgumentException>(() => harness.Queue.Schedule(new GrainJobRequest
        {
            JobName = "work",
            DueTime = due1,
            Metadata = new Dictionary<string, string> { ["k"] = new string('é', 8) }, // 1 + 16 = 17 > 16
        }));

        // Valid schedule with explicit JobId and metadata at exact 16-byte limit.
        var meta = new Dictionary<string, string> { ["k"] = new string('a', 15) }; // 1 + 15 = 16 bytes
        var id1 = harness.Queue.Schedule(new GrainJobRequest
        {
            JobId = "job-1",
            JobName = "send-email",
            DueTime = due1,
            Metadata = meta,
        });
        Assert.Equal("job-1", id1);
        await harness.StateManager.WriteStateAsync(Token);
        var writesAfterFirst = harness.Storage.Writes.Count;
        Assert.Equal(1, writesAfterFirst);

        // Equivalent explicit JobId is idempotent and stages ZERO new journal writes!
        var id1Dup = harness.Queue.Schedule(new GrainJobRequest
        {
            JobId = "job-1",
            JobName = "send-email",
            DueTime = due1,
            Metadata = new Dictionary<string, string> { ["k"] = new string('a', 15) },
            TraceParent = "00-different-telemetry-ignored",
        });
        Assert.Equal("job-1", id1Dup);
        await harness.StateManager.WriteStateAsync(Token);
        Assert.Equal(writesAfterFirst, harness.Storage.Writes.Count);

        // Conflicting Name, DueTime, or Metadata with same explicit JobId throws InvalidOperationException!
        Assert.Throws<InvalidOperationException>(() => harness.Queue.Schedule(new GrainJobRequest
        {
            JobId = "job-1",
            JobName = "other-name",
            DueTime = due1,
            Metadata = meta,
        }));
        Assert.Throws<InvalidOperationException>(() => harness.Queue.Schedule(new GrainJobRequest
        {
            JobId = "job-1",
            JobName = "send-email",
            DueTime = due1 + TimeSpan.FromSeconds(1),
            Metadata = meta,
        }));
        Assert.Throws<InvalidOperationException>(() => harness.Queue.Schedule(new GrainJobRequest
        {
            JobId = "job-1",
            JobName = "send-email",
            DueTime = due1,
            Metadata = new Dictionary<string, string> { ["k"] = "different" },
        }));

        // MaxPendingJobs = 2 enforced strictly; cancelling frees capacity.
        _ = harness.Queue.Schedule(new GrainJobRequest { JobId = "job-2", JobName = "work", DueTime = due1 });
        Assert.Throws<InvalidOperationException>(() => harness.Queue.Schedule(new GrainJobRequest { JobId = "job-3", JobName = "work", DueTime = due1 }));

        Assert.False(harness.Queue.Cancel("nonexistent"));
        Assert.True(harness.Queue.Cancel("job-2"));
        Assert.Equal(1, harness.Queue.Count);
        _ = harness.Queue.Schedule(new GrainJobRequest { JobId = "job-3", JobName = "work", DueTime = due1 });
        Assert.Equal(2, harness.Queue.Count);
        await harness.StateManager.WriteStateAsync(Token);
    }

    [Fact]
    public async Task PostAwaitExecutionGenerationGuardProtectsCancelAndRescheduleAgainstStaleHandler()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        await using var harness = await QueueHarness.CreateAsync(time);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DurableJobRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Queue.RegisterHandler(new DelegateQueueHandler(async (_, _) =>
        {
            entered.TrySetResult();
            return await release.Task;
        }));

        var t0 = time.GetUtcNow();
        _ = harness.Queue.Schedule(new GrainJobRequest { JobId = "job-a", JobName = "work", DueTime = t0 });
        await harness.StateManager.WriteStateAsync(Token);
        Assert.True(harness.Queue.TryGetJob("job-a", out var original));
        var gen1 = original.ExecutionGeneration;

        // Start executing job-a (generation 1) and pause at the await boundary.
        var passTask = harness.Queue.ProcessDueJobsAsync(Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // While handler awaits: cancel job-a and re-schedule a brand-new job-a (generation 2, due later)!
        Assert.True(harness.Queue.Cancel("job-a"));
        var futureDue = t0 + TimeSpan.FromMinutes(5);
        _ = harness.Queue.Schedule(new GrainJobRequest { JobId = "job-a", JobName = "work-v2", DueTime = futureDue });
        await harness.StateManager.WriteStateAsync(Token);

        Assert.True(harness.Queue.TryGetJob("job-a", out var replaced));
        Assert.True(replaced.ExecutionGeneration > gen1, "Re-scheduling the same JobId must advance ExecutionGeneration (ABA-safe).");

        // Stale handler finishes with Completed (or RescheduleAt): must NOT remove or overwrite generation 2!
        release.SetResult(DurableJobRunResult.Completed);
        _ = await passTask.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.True(harness.Queue.TryGetJob("job-a", out var surviving));
        Assert.Equal("work-v2", surviving.Name);
        Assert.Equal(futureDue, surviving.DueTime);
        Assert.Equal(replaced.ExecutionGeneration, surviving.ExecutionGeneration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandlerThrowingWithDirtyStateFencesAndDeactivatesWithoutPartialCommit(bool mutateLazyDurableValue)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        await using var harness = await QueueHarness.CreateAsync(time);

        harness.Counter.Value = 10;
        harness.Log.Add("clean");
        _ = harness.Queue.Schedule(new GrainJobRequest { JobId = "dirty-job", JobName = "work", DueTime = time.GetUtcNow() });
        await harness.StateManager.WriteStateAsync(Token);
        var committedWritesBefore = harness.Storage.Writes.Count;

        harness.Queue.RegisterHandler(new DelegateQueueHandler((_, _) =>
        {
            if (mutateLazyDurableValue)
            {
                harness.Counter.Value = 999; // Lazy IStateMachineDirtyProbe dirty state!
            }
            else
            {
                harness.Log.Add("dirty-partial"); // Eager _journalWriter dirty state!
            }

            throw new InvalidOperationException("Handler failed after mutating state.");
        }));

        _ = await harness.Queue.ProcessDueJobsAsync(Token);

        // Must have fenced, deactivated, and written NOTHING to storage!
        Assert.True(harness.Queue.IsFailed);
        Assert.True(harness.DeactivationCount >= 1);
        Assert.Equal(committedWritesBefore, harness.Storage.Writes.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.StateManager.WriteStateAsync(Token).AsTask());

        // Reactivating from the persisted storage recovers the exact clean pre-throw state (Counter = 10, Log = ["clean"], dirty-job still pending)!
        await using var recovered = await QueueHarness.ReactivateAsync(harness, time);
        Assert.Equal(10, recovered.Counter.Value);
        Assert.Equal(["clean"], recovered.Log.ToArray());
        Assert.True(recovered.Queue.TryGetJob("dirty-job", out var recoveredJob));
        Assert.Equal(0, recoveredJob.DequeueCount);
    }

    [Fact]
    public void SerializerAndAotJsonContextRoundTripAllCommandsWithoutReflection()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();

        var job = new PendingGrainJob
        {
            Id = "j-42",
            Name = "reconcile",
            DueTime = new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero),
            Metadata = new Dictionary<string, string> { ["tenant"] = "contoso", ["step"] = "2" },
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TraceState = "rojo=00f067aa0ba902b7",
            DequeueCount = 3,
            ExecutionGeneration = 17,
        };

        // 1. Orleans [GenerateSerializer] round-trip for PendingGrainJob & all 4 union cases.
        var commands = new DurableJobQueueCommand[]
        {
            new DurableJobQueueCommand.Schedule(job),
            new DurableJobQueueCommand.Cancel("j-42"),
            new DurableJobQueueCommand.Update("j-42", job.DueTime.AddMinutes(5), 4, 18),
            new DurableJobQueueCommand.Snapshot([job], 18),
        };

        var jobRoundTrip = serializer.Deserialize<PendingGrainJob>(serializer.SerializeToArray(job));
        Assert.NotNull(jobRoundTrip);
        Assert.Equal(job.Id, jobRoundTrip.Id);
        Assert.Equal(job.Name, jobRoundTrip.Name);
        Assert.Equal(job.DueTime, jobRoundTrip.DueTime);
        Assert.Equal(job.Metadata!["tenant"], jobRoundTrip.Metadata!["tenant"]);
        Assert.Equal(job.DequeueCount, jobRoundTrip.DequeueCount);
        Assert.Equal(job.ExecutionGeneration, jobRoundTrip.ExecutionGeneration);

        foreach (var cmd in commands)
        {
            var binaryRoundTrip = serializer.Deserialize<DurableJobQueueCommand>(serializer.SerializeToArray(cmd));
            Assert.Equal(cmd.GetType(), binaryRoundTrip.GetType());
        }

        // 2. Strict AOT-only JsonSerializerOptions (TypeInfoResolver = DurableJobQueueJsonContext.Default ONLY, zero reflection!).
        var aotOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = DurableJobQueueJsonContext.Default,
        };

        var jsonJob = JsonSerializer.Serialize(job, aotOptions);
        var decodedJob = JsonSerializer.Deserialize<PendingGrainJob>(jsonJob, aotOptions);
        Assert.NotNull(decodedJob);
        Assert.Equal(job.Id, decodedJob.Id);
        Assert.Equal(job.Metadata!["step"], decodedJob.Metadata!["step"]);

        foreach (var cmd in commands)
        {
            var json = JsonSerializer.Serialize(cmd, aotOptions);
            var decoded = JsonSerializer.Deserialize<DurableJobQueueCommand>(json, aotOptions);
            Assert.Equal(cmd.Value.GetType(), decoded.Value.GetType());
        }
    }

    [Fact]
    public void DurableJobQueueModelCsCheckOraclePreservesPendingJobsDueOrderAndWakeups()
    {
        var opGen = Gen.Select(
            Gen.Int[0, 5],   // op kind: 0=Schedule, 1=Cancel, 2=AdvanceTimeAndRun, 3=Reactivate, 4=ScheduleIdempotentOrConflict, 5=SnapshotAndReactivate
            Gen.Int[0, 5],   // job slot index (job-0 .. job-5)
            Gen.Int[0, 12],  // due offset seconds (0..12)
            Gen.Int[0, 3]);  // handler outcome: 0=Completed, 1=RescheduleAt(+3s), 2=CleanTransientThrow, 3=DirtyStateThrow

        opGen.Array[1, 35].Sample(ops =>
        {
            RunModelTraceAsync(ops).GetAwaiter().GetResult();
        }, iter: 80, seed: "0123456789AB");
    }

    private static async Task RunModelTraceAsync((int Op, int Slot, int OffsetSec, int Outcome)[] ops)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var queueOptions = new DurableJobQueueOptions
        {
            MaxPendingJobs = 4,
            MaxMetadataBytesPerJob = 128,
            RetryInitialInterval = TimeSpan.FromSeconds(2),
            RetryMaxInterval = TimeSpan.FromSeconds(10),
            MaxRetryCount = 2,
        };

        var harness = await QueueHarness.CreateAsync(time, queueOptions);
        try
        {
            // Reference model state (durable truth).
            var model = new Dictionary<string, ModelJob>(StringComparer.Ordinal);
            var modelCounter = 0;
            var outcomeMode = 0;
            var executedDueOrder = new List<long>();

            void AttachHandler(QueueHarness h)
            {
                h.Queue.RegisterHandler(new DelegateQueueHandler((ctx, _) =>
                {
                    executedDueOrder.Add(ctx.Job.DueTime.UtcTicks);
                    return outcomeMode switch
                    {
                        0 => ValueTask.FromResult(DurableJobRunResult.Completed),
                        1 => ValueTask.FromResult(DurableJobRunResult.RescheduleAt(time.GetUtcNow() + TimeSpan.FromSeconds(3))),
                        2 => ValueTask.FromException<DurableJobRunResult>(new InvalidOperationException("clean-transient")),
                        _ => DirtyThrow(h),
                    };

                    static ValueTask<DurableJobRunResult> DirtyThrow(QueueHarness target)
                    {
                        target.Counter.Value += 1;
                        return ValueTask.FromException<DurableJobRunResult>(new InvalidOperationException("dirty-throw"));
                    }
                }));
            }

            AttachHandler(harness);

            foreach (var (op, slot, offsetSec, outcome) in ops)
            {
                var jobId = $"job-{slot}";
                var now = time.GetUtcNow();

                switch (op)
                {
                    case 0: // Schedule + atomic grain state write
                    {
                        var due = now + TimeSpan.FromSeconds(offsetSec);
                        var req = new GrainJobRequest
                        {
                            JobId = jobId,
                            JobName = $"task-{slot}",
                            DueTime = due,
                            Metadata = new Dictionary<string, string> { ["slot"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        };

                        if (model.TryGetValue(jobId, out var existing))
                        {
                            if (existing.DueTime.UtcTicks == due.UtcTicks)
                            {
                                var returned = harness.Queue.Schedule(req);
                                Assert.Equal(jobId, returned);
                            }
                            else
                            {
                                Assert.Throws<InvalidOperationException>(() => harness.Queue.Schedule(req));
                            }
                        }
                        else if (model.Count >= queueOptions.MaxPendingJobs)
                        {
                            Assert.Throws<InvalidOperationException>(() => harness.Queue.Schedule(req));
                        }
                        else
                        {
                            var returned = harness.Queue.Schedule(req);
                            Assert.Equal(jobId, returned);
                            harness.Counter.Value = ++modelCounter;
                            await harness.StateManager.WriteStateAsync(Token);
                            model[jobId] = new ModelJob(jobId, req.JobName, due, DequeueCount: 0);
                            AssertWakeupInvariants(harness, model, now);
                        }

                        break;
                    }

                    case 1: // Cancel
                    {
                        var cancelled = harness.Queue.Cancel(jobId);
                        Assert.Equal(model.ContainsKey(jobId), cancelled);
                        if (cancelled)
                        {
                            _ = model.Remove(jobId);
                            await harness.StateManager.WriteStateAsync(Token);
                            AssertWakeupInvariants(harness, model, now);
                        }

                        break;
                    }

                    case 2: // Advance time and run due jobs
                    {
                        time.Advance(TimeSpan.FromSeconds(Math.Max(1, offsetSec % 6)));
                        outcomeMode = outcome;
                        executedDueOrder.Clear();

                        // Snapshot due jobs in model before executing.
                        var runNow = time.GetUtcNow();
                        var hadAnyDue = model.Values.Any(j => j.DueTime <= runNow);

                        _ = await harness.Queue.ProcessDueJobsAsync(Token);

                        if (hadAnyDue && outcomeMode == 3)
                        {
                            // Dirty-state throw: must fence, deactivate, leave durable model UNCHANGED, then reactivate cleanly!
                            Assert.True(harness.Queue.IsFailed);
                            Assert.True(harness.DeactivationCount >= 1);
                            var reactivated = await QueueHarness.ReactivateAsync(harness, time, queueOptions);
                            await harness.DisposeAsync();
                            harness = reactivated;
                            AttachHandler(harness);
                        }
                        else
                        {
                            // Verify due-time order was non-decreasing across every executed job in this pass.
                            for (var i = 1; i < executedDueOrder.Count; i++)
                            {
                                Assert.True(executedDueOrder[i] >= executedDueOrder[i - 1], "Jobs must execute in due-time order.");
                            }

                            // Advance reference model identically for every due job at runNow.
                            var dueJobs = model.Values
                                .Where(j => j.DueTime <= runNow)
                                .OrderBy(j => j.DueTime.UtcTicks)
                                .ThenBy(j => j.Id, StringComparer.Ordinal)
                                .ToList();

                            foreach (var dj in dueJobs)
                            {
                                switch (outcomeMode)
                                {
                                    case 0:
                                        _ = model.Remove(dj.Id);
                                        break;
                                    case 1:
                                        model[dj.Id] = dj with { DueTime = runNow + TimeSpan.FromSeconds(3), DequeueCount = 0 };
                                        break;
                                    case 2:
                                    {
                                        var nextDequeue = dj.DequeueCount + 1;
                                        if (nextDequeue > queueOptions.MaxRetryCount)
                                        {
                                            _ = model.Remove(dj.Id);
                                        }
                                        else
                                        {
                                            var delay = TimeSpan.FromTicks(Math.Min(
                                                queueOptions.RetryInitialInterval.Ticks * (1L << (nextDequeue - 1)),
                                                queueOptions.RetryMaxInterval.Ticks));
                                            model[dj.Id] = dj with { DueTime = runNow + delay, DequeueCount = nextDequeue };
                                        }

                                        break;
                                    }
                                }
                            }

                            if (dueJobs.Count > 0)
                            {
                                AssertWakeupInvariants(harness, model, runNow);
                            }
                        }

                        break;
                    }

                    case 3: // Deactivate + reactivate (replay journal)
                    {
                        var reactivated = await QueueHarness.ReactivateAsync(harness, time, queueOptions);
                        await harness.DisposeAsync();
                        harness = reactivated;
                        AttachHandler(harness);
                        break;
                    }

                    case 4: // Explicit JobId idempotency check
                    {
                        if (model.TryGetValue(jobId, out var existing))
                        {
                            var returned = harness.Queue.Schedule(new GrainJobRequest
                            {
                                JobId = existing.Id,
                                JobName = existing.Name,
                                DueTime = existing.DueTime,
                                Metadata = new Dictionary<string, string> { ["slot"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            });
                            Assert.Equal(existing.Id, returned);
                        }

                        break;
                    }

                    case 5: // Force compaction snapshot + reactivate
                    {
                        harness.Storage.CompactionRequested = true;
                        harness.Counter.Value = ++modelCounter;
                        await harness.StateManager.WriteStateAsync(Token);
                        harness.Storage.CompactionRequested = false;

                        var reactivated = await QueueHarness.ReactivateAsync(harness, time, queueOptions);
                        await harness.DisposeAsync();
                        harness = reactivated;
                        AttachHandler(harness);
                        break;
                    }
                }

                // Core state invariant after EVERY step: PendingJobs matches reference model exactly!
                Assert.Equal(modelCounter, harness.Counter.Value);
                Assert.Equal(model.Count, harness.Queue.Count);
                Assert.Equal(model.Count, harness.Queue.PendingJobs.Count);
                foreach (var (id, expected) in model)
                {
                    Assert.True(harness.Queue.TryGetJob(id, out var actual));
                    Assert.Equal(expected.Name, actual.Name);
                    Assert.Equal(expected.DueTime.UtcTicks, actual.DueTime.UtcTicks);
                    Assert.Equal(expected.DequeueCount, actual.DequeueCount);
                }
            }
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private static void AssertWakeupInvariants(QueueHarness harness, Dictionary<string, ModelJob> model, DateTimeOffset now)
    {
        // Zero backing ScheduleJobAsync calls whenever companion writes are supported!
        Assert.Empty(harness.ScheduledBackingJobs);

        // Decode last emitted wakeup companion record (if any) and verify its invariant against the model!
        var lastWakeup = harness.Storage.TryGetLastWakeup();
        if (model.Count == 0)
        {
            // Whenever all pending jobs clear, either no wakeup was ever emitted or the latest wakeup cleared it (DueTimeUtcTicks == null).
            if (lastWakeup is { } w)
            {
                Assert.Null(w.DueTimeUtcTicks);
            }
        }
        else
        {
            Assert.NotNull(lastWakeup);
            Assert.Equal(harness.GrainId, lastWakeup.Value.Target);
            Assert.Equal(DurableJobQueue.WakeupJobName, lastWakeup.Value.JobName);
            Assert.NotNull(lastWakeup.Value.DueTimeUtcTicks);

            // Clamped wakeup must cover the minimum due time in the queue (<= max(minDue, now + RecheckDelay)).
            var minDue = model.Values.Min(j => j.DueTime);
            var minClamped = minDue < now + DurableJobQueue.RecheckDelay ? now + DurableJobQueue.RecheckDelay : minDue;
            Assert.True(
                lastWakeup.Value.DueTimeUtcTicks!.Value <= minClamped.UtcTicks,
                $"Emitted wakeup ({lastWakeup.Value.DueTimeUtcTicks}) must cover earliest due job ({minClamped.UtcTicks}).");
        }
    }

    private readonly record struct ModelJob(string Id, string Name, DateTimeOffset DueTime, int DequeueCount);

    private sealed class DelegateQueueHandler(Func<IJobRunContext, CancellationToken, ValueTask<DurableJobRunResult>> callback) : IDurableJobQueueHandler
    {
        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken) =>
            callback(context, attemptCancellationToken);
    }

    private sealed class QueueHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private QueueHarness(
            ServiceProvider services,
            GrainId grainId,
            InMemoryCompanionStorage storage,
            JournaledStateManager stateManager,
            DurableJobQueue queue,
            IDurableValue<int> counter,
            IDurableList<string> log,
            List<ScheduleJobRequest> scheduledBackingJobs,
            Func<int> getDeactivationCount)
        {
            _services = services;
            GrainId = grainId;
            Storage = storage;
            StateManager = stateManager;
            Queue = queue;
            Counter = counter;
            Log = log;
            ScheduledBackingJobs = scheduledBackingJobs;
            _getDeactivationCount = getDeactivationCount;
        }

        private readonly Func<int> _getDeactivationCount;

        public GrainId GrainId { get; }
        public InMemoryCompanionStorage Storage { get; }
        public JournaledStateManager StateManager { get; }
        public DurableJobQueue Queue { get; }
        public IDurableValue<int> Counter { get; }
        public IDurableList<string> Log { get; }
        public List<ScheduleJobRequest> ScheduledBackingJobs { get; }
        public int DeactivationCount => _getDeactivationCount();

        public static Task<QueueHarness> CreateAsync(
            FakeTimeProvider time,
            DurableJobQueueOptions? options = null,
            InMemoryCompanionStorage? existingStorage = null)
            => CreateCoreAsync(time, options ?? new DurableJobQueueOptions(), existingStorage ?? new InMemoryCompanionStorage());

        public static Task<QueueHarness> ReactivateAsync(
            QueueHarness previous,
            FakeTimeProvider time,
            DurableJobQueueOptions? options = null)
            => CreateCoreAsync(time, options ?? new DurableJobQueueOptions(), previous.Storage);

        private static async Task<QueueHarness> CreateCoreAsync(
            FakeTimeProvider time,
            DurableJobQueueOptions options,
            InMemoryCompanionStorage storage)
        {
            var grainId = GrainId.Create("jobqueue-test", "1");
            var lifecycle = new GrainLifecycle(NullLogger<GrainLifecycle>.Instance);
            var deactivationCount = 0;

            var grainContext = Substitute.For<IGrainContext>();
            grainContext.GrainId.Returns(grainId);
            grainContext.ObservableLifecycle.Returns(lifecycle);
            grainContext.When(c => c.Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>()))
                .Do(_ => Interlocked.Increment(ref deactivationCount));

            var scheduledBackingJobs = new List<ScheduleJobRequest>();
            var jobManager = Substitute.For<ILocalDurableJobManager>();
            jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var req = ci.Arg<ScheduleJobRequest>();
                    scheduledBackingJobs.Add(req);
                    return Task.FromResult(new DurableJob
                    {
                        Id = $"backing-{scheduledBackingJobs.Count}",
                        Name = req.JobName,
                        DueTime = req.DueTime,
                        TargetGrainId = req.Target,
                        ShardId = "shard-1",
                    });
                });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSerializer();
            services.AddJsonJournalFormat(tryAdd: true);
            services.Configure<JsonJournalOptions>(o =>
            {
                o.AddTypeInfoResolver(DurableJobQueueJsonContext.Default);
                o.AddTypeInfoResolver(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
            });
            services.AddOptions<JournaledStateManagerOptions>();
            services.AddOptions<DurableJobQueueOptions>().Configure(o =>
            {
                o.MaxPendingJobs = options.MaxPendingJobs;
                o.MaxMetadataBytesPerJob = options.MaxMetadataBytesPerJob;
                o.RetryInitialInterval = options.RetryInitialInterval;
                o.RetryMaxInterval = options.RetryMaxInterval;
                o.MaxRetryCount = options.MaxRetryCount;
                o.ShouldRetry = options.ShouldRetry;
            });
            services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, time);
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<DurableJobQueueShared>();

            var provider = services.BuildServiceProvider();
            grainContext.ActivationServices.Returns(provider);
            var shared = new JournaledStateManagerShared(
                NullLogger<JournaledStateManager>.Instance,
                Microsoft.Extensions.Options.Options.Create(new JournaledStateManagerOptions()),
                time,
                provider);
            var stateManager = new JournaledStateManager(shared, storage, grainContext);
            var coordinator = new GrainWakeupCoordinator(grainContext, stateManager, jobManager, time);
            var registry = new DurableJobHandlerRegistry();
            var queueShared = provider.GetRequiredService<DurableJobQueueShared>();

            var queue = new DurableJobQueue(grainContext, stateManager, coordinator, registry, queueShared);
            var counterCodec = provider.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey);
            var logCodec = provider.GetRequiredKeyedService<IDurableListCommandCodec<string>>(JsonLinesJournalFormat.JournalFormatKey);
            var counter = new DurableValue<int>("counter", stateManager, counterCodec);
            var log = new DurableList<string>("log", stateManager, logCodec);

            await stateManager.InitializeAsync(Token);
            return new QueueHarness(provider, grainId, storage, stateManager, queue, counter, log, scheduledBackingJobs, () => Volatile.Read(ref deactivationCount));
        }

        public async ValueTask DisposeAsync()
        {
            Queue.Dispose();
            ((IDisposable)StateManager).Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class InMemoryCompanionStorage : IMessageJournalStorage
    {
        private byte[] _content = [];

        public List<(string Kind, WakeupRecord[] Wakeups)> Writes { get; } = [];
        public bool CompactionRequested { get; set; }
        public bool IsCompactionRequested => CompactionRequested;

        public WakeupRecord? TryGetLastWakeup()
        {
            for (var i = Writes.Count - 1; i >= 0; i--)
            {
                var wakeups = Writes[i].Wakeups;
                if (wakeups.Length > 0)
                {
                    return wakeups[^1];
                }
            }

            return null;
        }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            using var writer = new ArcBufferWriter();
            if (_content.Length > 0)
            {
                writer.Write(_content);
            }

            consumer.Read(new JournalBufferReader(writer.Reader, isCompleted: true), JournalMetadata.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            AppendWithMessagesAsync(value, ReadOnlyMemory<JournalMessageRecord>.Empty, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            ReplaceWithMessagesAsync(value, ReadOnlyMemory<JournalMessageRecord>.Empty, cancellationToken);

        public ValueTask AppendWithMessagesAsync(ReadOnlySequence<byte> value, ReadOnlyMemory<JournalMessageRecord> messages, CancellationToken cancellationToken)
        {
            var delta = value.ToArray();
            var combined = new byte[_content.Length + delta.Length];
            _content.CopyTo(combined, 0);
            delta.CopyTo(combined, _content.Length);
            _content = combined;
            RecordWrite("Append", messages);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReplaceWithMessagesAsync(ReadOnlySequence<byte> value, ReadOnlyMemory<JournalMessageRecord> messages, CancellationToken cancellationToken)
        {
            _content = value.ToArray();
            RecordWrite("Replace", messages);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            _content = [];
            return ValueTask.CompletedTask;
        }

        private void RecordWrite(string kind, ReadOnlyMemory<JournalMessageRecord> messages)
        {
            var wakeups = new List<WakeupRecord>(messages.Length);
            foreach (var c in messages.Span)
            {
                if (string.Equals(c.Channel, WakeupMessageCodec.ChannelName, StringComparison.Ordinal))
                {
                    var err = WakeupMessageCodec.Decode(c.Payload.Span, out var decoded);
                    Assert.Null(err);
                    wakeups.Add(decoded);
                }
            }

            Writes.Add((kind, wakeups.ToArray()));
        }
    }
}
