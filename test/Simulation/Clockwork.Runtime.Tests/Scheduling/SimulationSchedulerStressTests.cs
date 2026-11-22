using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

/// <summary>
/// Stress and teardown coverage for the unified <see cref="SimulationScheduler"/>: the
/// exactly-one-running invariant across many physical threads, deterministic interleaving under
/// yielding, no physical thread leaks, and teardown with paused/parked operations.
/// </summary>
public sealed class SimulationSchedulerStressTests
{
    [Theory]
    [InlineData(8, 50)]
    [InlineData(32, 20)]
    public void ExactlyOneOperationExecutesSutCodeAtATimeUnderYieldingStress(int operationCount, int iterations)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var concurrent = 0;
        var maxObserved = 0;
        var violations = 0;
        var threadIds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        for (var i = 0; i < operationCount; i++)
        {
            scheduler.Schedule($"op-{i}", () =>
            {
                threadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
                for (var iter = 0; iter < iterations; iter++)
                {
                    var now = Interlocked.Increment(ref concurrent);
                    if (now != 1)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    UpdateMax(ref maxObserved, now);
                    Thread.SpinWait(200);
                    Interlocked.Decrement(ref concurrent);
                    scheduler.Yield();
                }
            });
        }

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(0, violations);
        Assert.Equal(1, maxObserved);
        // Every yielded stack retains its worker until the operation completes.
        Assert.Equal(operationCount, threadIds.Count);
    }

    [Fact]
    public void YieldingOperationsInterleaveInDeterministicRoundRobinOrder()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var trace = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var name = $"op{i}";
            scheduler.Schedule(name, () =>
            {
                for (var round = 0; round < 3; round++)
                {
                    trace.Add($"{name}:{round}");
                    scheduler.Yield();
                }
            });
        }

        scheduler.Drain(TestContext.Current.CancellationToken);

        // Lowest-id selection means all round-0 slices run (in registration order) before any
        // round-1 slice, and so on: a stable, deterministic round-robin.
        Assert.Equal(
            [
                "op0:0", "op1:0", "op2:0",
                "op0:1", "op1:1", "op2:1",
                "op0:2", "op1:2", "op2:2",
            ],
            trace);
    }

    [Fact]
    public void CompletedOperationsLeaveNoLivePhysicalThreads()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var ops = new List<SimulationOperation>();
        for (var i = 0; i < 16; i++)
        {
            ops.Add(scheduler.Schedule($"op-{i}", () => Thread.SpinWait(100)));
        }

        scheduler.Drain(TestContext.Current.CancellationToken);

        foreach (var op in ops)
        {
            Assert.Equal(SimulationOperationState.Completed, op.State);
            Assert.True(op.Thread is null or { IsAlive: false }, $"Operation {op.Id} leaked a live physical thread.");
        }

        Assert.Equal(1, scheduler.CreatedWorkerThreadCount);
        Assert.Equal(1, scheduler.IdleWorkerThreadCount);
    }

    [Fact]
    public void FiftyThousandShortCallbacksReuseOneWorker()
    {
        const int callbackCount = 50_000;
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var threadIds = new HashSet<int>();
        var completed = 0;
        Action callback = () =>
        {
            threadIds.Add(Environment.CurrentManagedThreadId);
            completed++;
        };

        for (var index = 0; index < callbackCount; index++)
        {
            scheduler.Schedule("short-callback", callback);
        }

        Assert.Equal(callbackCount, scheduler.Drain(TestContext.Current.CancellationToken));
        Assert.Equal(callbackCount, completed);
        Assert.Single(threadIds);
        Assert.Equal(1, scheduler.CreatedWorkerThreadCount);
        Assert.Equal(1, scheduler.IdleWorkerThreadCount);
    }

    [Fact]
    public void CompletedOperationsLeaveActiveSetButRemainInStatusHistory()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        for (var i = 0; i < 100; i++)
        {
            scheduler.Schedule($"op-{i}", () => { });
        }

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(0, scheduler.ActiveOperationCount);
        Assert.Equal(100, scheduler.CaptureStatus().Count);
        Assert.All(
            scheduler.CaptureStatus(),
            status => Assert.Equal(SimulationOperationState.Completed, status.State));
    }

    [Fact]
    public void ManyPausedOperationsKeepDistinctThreadsParkedThenTeardownReclaimsThemAll()
    {
        const int count = 12;
        var scheduler = SchedulerTestHarness.NewScheduler();
        var ops = new List<SimulationOperation>();
        for (var i = 0; i < count; i++)
        {
            ops.Add(scheduler.Schedule($"op-{i}", () =>
                scheduler.Pause(SimulationPauseReason.ResourceWait("never-signaled"))));
        }

        // Drive each operation once so all of them park in the paused state.
        for (var i = 0; i < count; i++)
        {
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        }

        Assert.False(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(count, scheduler.PendingOperationCount);

        var threads = new HashSet<Thread>();
        foreach (var op in ops)
        {
            Assert.Equal(SimulationOperationState.Paused, op.State);
            Assert.NotNull(op.Thread);
            Assert.True(op.Thread!.IsAlive, $"Paused operation {op.Id} should keep its thread parked.");
            threads.Add(op.Thread);
        }

        Assert.Equal(count, threads.Count);

        // Teardown must cooperatively unwind and reclaim every parked worker.
        scheduler.Dispose();

        foreach (Thread thread in threads)
        {
            Assert.True(SpinUntilDead(thread), "A parked worker remained alive after teardown.");
        }

        Assert.All(ops, static op =>
        {
            Assert.Equal(SimulationOperationState.Canceled, op.State);
            Assert.Null(op.Thread);
        });
    }

    [Fact]
    public void DisposeIsIdempotentAndSafeWithNoOperations()
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Dispose();
        scheduler.Dispose();
    }

    [Fact]
    public void OperationsCanReuseAnEarlierWorkersThread()
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        Thread? firstThread = null;
        var first = scheduler.Schedule("first", () => firstThread = Thread.CurrentThread);
        scheduler.Drain(TestContext.Current.CancellationToken);

        Thread? secondThread = null;
        var second = scheduler.Schedule("second", () => secondThread = Thread.CurrentThread);
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationOperationState.Completed, first.State);
        Assert.Equal(SimulationOperationState.Completed, second.State);
        Assert.Same(firstThread, secondThread);
        Assert.Equal(1, scheduler.CreatedWorkerThreadCount);

        scheduler.Dispose();
        Assert.True(SpinUntilDead(secondThread!));
    }

    [Fact]
    public void ParallelSchedulersOwnIndependentWorkerPools()
    {
        using var first = SchedulerTestHarness.NewScheduler(description: "first");
        using var second = SchedulerTestHarness.NewScheduler(description: "second");
        Thread? firstThread = null;
        Thread? secondThread = null;
        first.Schedule("first", () => firstThread = Thread.CurrentThread);
        second.Schedule("second", () => secondThread = Thread.CurrentThread);

        using var start = new Barrier(2);
        var firstDriver = new Thread(() =>
        {
            start.SignalAndWait();
            first.Drain(TestContext.Current.CancellationToken);
        });
        var secondDriver = new Thread(() =>
        {
            start.SignalAndWait();
            second.Drain(TestContext.Current.CancellationToken);
        });
        firstDriver.Start();
        secondDriver.Start();

        Assert.True(firstDriver.Join(TimeSpan.FromSeconds(5)));
        Assert.True(secondDriver.Join(TimeSpan.FromSeconds(5)));
        Assert.NotSame(firstThread, secondThread);
        Assert.Equal(1, first.CreatedWorkerThreadCount);
        Assert.Equal(1, second.CreatedWorkerThreadCount);
    }

    [Fact]
    public void DecisionReplayPreservesShortCallbackWorkerReuse()
    {
        var log = new SimulationDecisionLog();
        using (var recorder = SchedulerTestHarness.NewScheduler())
        {
            recorder.DecisionLog = log;
            for (var index = 0; index < 100; index++)
            {
                recorder.Schedule($"callback-{index}", static () => { });
            }

            recorder.Drain(TestContext.Current.CancellationToken);
            Assert.Equal(1, recorder.CreatedWorkerThreadCount);
        }

        using var replay = SchedulerTestHarness.NewScheduler();
        replay.ReplayValidator = new SimulationDecisionReplayValidator(
            new SimulationInMemoryDecisionReplayReader(log.Records));
        for (var index = 0; index < 100; index++)
        {
            replay.Schedule($"callback-{index}", static () => { });
        }

        replay.Drain(TestContext.Current.CancellationToken);
        replay.ValidateReplayComplete();
        Assert.Equal(1, replay.CreatedWorkerThreadCount);
    }

    [Theory]
    [InlineData(CancellationRace.Signal)]
    [InlineData(CancellationRace.Timeout)]
    [InlineData(CancellationRace.Dispose)]
    public void ExternalCancellationDoesNotDeadlockWaiterCleanup(CancellationRace race)
    {
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var scheduler = SchedulerTestHarness.NewScheduler();
            var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "race");
            using var cancellation = new CancellationTokenSource();
            var timeout = race == CancellationRace.Timeout
                ? TimeSpan.FromTicks(1)
                : Timeout.InfiniteTimeSpan;

            var operation = scheduler.Schedule(
                "waiter",
                () => scheduler.WaitOnResource(
                    resource,
                    timeout,
                    SimulationPauseReason.ResourceWait("race"),
                    cancellation.Token));

            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
            Assert.Equal(SimulationOperationState.Paused, operation.State);
            Thread workerThread = operation.Thread!;

            using var start = new Barrier(2);
            Exception? contenderError = null;
            Exception? cancelError = null;
            var contender = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    switch (race)
                    {
                        case CancellationRace.Signal:
                            scheduler.SignalOne(resource);
                            break;
                        case CancellationRace.Timeout:
                            scheduler.TryAdvanceVirtualTime();
                            break;
                        case CancellationRace.Dispose:
                            scheduler.Dispose();
                            break;
                    }
                }
                catch (Exception exception)
                {
                    contenderError = exception;
                }
            })
            {
                IsBackground = true,
            };
            var canceler = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    cancellation.Cancel();
                }
                catch (Exception exception)
                {
                    cancelError = exception;
                }
            })
            {
                IsBackground = true,
            };

            contender.Start();
            canceler.Start();

            var completed = contender.Join(TimeSpan.FromSeconds(2))
                && canceler.Join(TimeSpan.FromSeconds(2));
            Assert.True(completed, $"{race} deadlocked against external cancellation on iteration {iteration}.");
            Assert.Null(contenderError);
            Assert.Null(cancelError);

            if (race != CancellationRace.Dispose)
            {
                scheduler.Drain(TestContext.Current.CancellationToken);
                Assert.Empty(scheduler.CapturePendingTimeouts());
                scheduler.Dispose();
            }

            Assert.True(SpinUntilDead(workerThread), $"Operation {operation.Id} leaked after {race} race.");
            Assert.Null(operation.Thread);
        }
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (candidate <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, seen) != seen);
    }

    private static bool SpinUntilDead(Thread thread) =>
        SpinWait.SpinUntil(() => !thread.IsAlive, TimeSpan.FromSeconds(10));

    public enum CancellationRace
    {
        Signal,
        Timeout,
        Dispose,
    }
}
