using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

public sealed class SimulationSchedulerNodeDiscardTests
{
    [Fact]
    public void DiscardPendingWorkCancelsControlledDelayAndWaitAsyncDeadlines()
    {
        using var host = new SimulationSchedulerTestHost();
        var node = new SimulationNodeIdentity(TaskTestHarness.DefaultNodeAddress);
        SimulationNodeContext context = CreateContext(host.Scheduler, node);
        var source = new TaskCompletionSource();

        TaskTestHarness.RunInSimulation(host, () =>
        {
            Task delay = ControlledTask.Delay(
                TimeSpan.FromMinutes(9),
                TestContext.Current.CancellationToken);
            Task wait = ControlledTask.WaitAsync(
                source.Task,
                TimeSpan.FromMinutes(11),
                TestContext.Current.CancellationToken);
            Assert.Equal(2, host.Scheduler.PendingTimerRegistrationCount);
            Assert.Equal(1, host.Scheduler.WaitingOperationCount);

            context.DiscardPendingWork();
            source.SetResult();
            int steps = host.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

            Assert.Equal(SimulationNodeState.Discarded, context.State);
            Assert.False(delay.IsCompleted);
            Assert.False(wait.IsCompleted);
            Assert.Equal(0, steps);
            Assert.Null(host.Scheduler.NextTimerDue);
            Assert.Equal(0, host.Scheduler.PendingTimerRegistrationCount);
            Assert.Equal(0, host.Scheduler.WaitingOperationCount);
            Assert.Equal(0, host.Scheduler.PendingOperationCount);
            Assert.Equal(TimeSpan.Zero, host.Scheduler.VirtualTime);
        });
    }

    [Fact]
    public void DiscardPendingWorkCancelsNodeReadinessAndResourcePausedOperations()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var node = new SimulationNodeIdentity("node-1");
        SimulationNodeContext context = CreateContext(scheduler, node);
        var readinessReached = false;
        var readinessCallbackRan = false;
        var afterWaitRan = false;
        using var cancellation = new CancellationTokenSource();
        SimulationResource resource = scheduler.CreateResource(
            SimulationResourceKind.Semaphore,
            "node-resource");
        _ = scheduler.ScheduleWhenReady(
            node,
            () => readinessReached,
            () => readinessCallbackRan = true);
        SimulationOperation resourceOperation = scheduler.Schedule(
            "node-resource-wait",
            () =>
            {
                _ = scheduler.WaitOnResource(
                    resource,
                    TimeSpan.FromMinutes(5),
                    SimulationPauseReason.ResourceWait("node-resource"),
                    cancellation.Token);
                afterWaitRan = true;
            },
            node);

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Equal(SimulationOperationState.Paused, resourceOperation.State);
        Assert.Equal(1, resource.WaiterCount);
        Assert.Single(scheduler.CapturePendingTimeouts());
        Thread resourceWorker = resourceOperation.Thread!;

        context.DiscardPendingWork();
        readinessReached = true;
        cancellation.Cancel();

        Assert.Equal(SimulationNodeState.Discarded, context.State);
        Assert.Equal(SimulationOperationState.Canceled, resourceOperation.State);
        Assert.Null(resourceOperation.Thread);
        Assert.False(readinessCallbackRan);
        Assert.False(afterWaitRan);
        Assert.Equal(0, resource.WaiterCount);
        Assert.Empty(scheduler.CapturePendingTimeouts());
        Assert.Null(scheduler.SignalOne(resource));
        Assert.Equal(0, scheduler.PendingOperationCount);
        Assert.All(
            scheduler.CaptureStatus().Where(status => status.NodeAddress == node.Address),
            status => Assert.Equal(SimulationOperationState.Canceled, status.State));
        Assert.Equal(1, scheduler.IdleWorkerThreadCount);

        scheduler.Dispose();
        Assert.False(resourceWorker.IsAlive);
    }

    [Fact]
    public void DiscardedNodeRejectsSchedulerContinuationReadinessAndTimerPaths()
    {
        using var host = new SimulationSchedulerTestHost();
        var node = new SimulationNodeIdentity(TaskTestHarness.DefaultNodeAddress);
        SimulationNodeContext context = CreateContext(host.Scheduler, node);
        context.DiscardPendingWork();

        Assert.Throws<ObjectDisposedException>(
            () => host.Scheduler.Schedule("direct", static () => { }, node));
        Assert.Throws<ObjectDisposedException>(
            () => host.Scheduler.ScheduleWhenReady(node, static () => true, static () => { }));
        Assert.Throws<ObjectDisposedException>(
            () => host.Scheduler.RegisterTimer(node, TimeSpan.FromSeconds(1), static () => { }));
        Assert.Throws<ObjectDisposedException>(
            () => host.Scheduler.ScheduleAfter(
                "delayed",
                static () => { },
                TimeSpan.FromSeconds(1),
                node));

        TaskTestHarness.RunInSimulation(host, () =>
        {
            Assert.Throws<ObjectDisposedException>(
                () => SimulationTaskRuntime.QueueWork(
                    static () => { },
                    "test.queue",
                    flowExecutionContext: false));
            Assert.Throws<ObjectDisposedException>(
                () => SimulationTaskRuntime.ScheduleContinuation(
                    Task.CompletedTask,
                    static () => { },
                    "test.await",
                    flowExecutionContext: false));
            Assert.Throws<ObjectDisposedException>(
                () =>
                {
                    _ = ControlledTask.Delay(
                        TimeSpan.FromSeconds(1),
                        TestContext.Current.CancellationToken);
                });
            Assert.Throws<ObjectDisposedException>(
                () =>
                {
                    _ = ControlledTask.WaitAsync(
                        new TaskCompletionSource().Task,
                        TimeSpan.FromSeconds(1),
                        TestContext.Current.CancellationToken);
                });
        });

        Assert.Equal(0, host.Scheduler.PendingOperationCount);
        Assert.Equal(0, host.Scheduler.PendingTimerRegistrationCount);
        Assert.Equal(0, host.Scheduler.WaitingOperationCount);
    }

    [Fact]
    public void DiscardDuringCreatedNotificationRejectsAdmissionWithoutRunnableLeak()
    {
        var listener = new CallbackListener();
        using var scheduler = SchedulerTestHarness.NewScheduler(listener);
        var node = new SimulationNodeIdentity("node-1");
        SimulationNodeContext context = CreateContext(scheduler, node);
        listener.Callback = (operation, state) =>
        {
            if (state == SimulationOperationState.Created && operation.Node == node)
            {
                context.DiscardPendingWork();
            }
        };

        Assert.Throws<ObjectDisposedException>(
            () => scheduler.Schedule("reentrant-admission", static () => { }, node));

        SimulationOperationStatus status = Assert.Single(scheduler.CaptureStatus());
        Assert.Equal(SimulationOperationState.Canceled, status.State);
        Assert.Equal(SimulationNodeState.Discarded, context.State);
        Assert.Equal(0, scheduler.PendingOperationCount);
        Assert.Equal(0, scheduler.RunnableOperationCount);
        Assert.Equal(0, scheduler.WaitingOperationCount);
    }

    [Fact]
    public void DiscardDuringReadinessCreatedNotificationRejectsInsertionWithoutWaitLeak()
    {
        var listener = new CallbackListener();
        using var scheduler = SchedulerTestHarness.NewScheduler(listener);
        var node = new SimulationNodeIdentity("node-1");
        SimulationNodeContext context = CreateContext(scheduler, node);
        var readinessCallbackRan = false;
        listener.Callback = (operation, state) =>
        {
            if (state == SimulationOperationState.Created && operation.Node == node)
            {
                context.DiscardPendingWork();
            }
        };

        Assert.Throws<ObjectDisposedException>(
            () => scheduler.ScheduleWhenReady(
                node,
                static () => true,
                () => readinessCallbackRan = true));

        Assert.False(readinessCallbackRan);
        SimulationOperationStatus status = Assert.Single(scheduler.CaptureStatus());
        Assert.Equal(SimulationOperationState.Canceled, status.State);
        Assert.Equal(SimulationNodeState.Discarded, context.State);
        Assert.Equal(0, scheduler.PendingOperationCount);
        Assert.Equal(0, scheduler.RunnableOperationCount);
        Assert.Equal(0, scheduler.WaitingOperationCount);
        Assert.False(scheduler.RunStep(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SameDeadlineTimerSchedulingCallbackDiscardSkipsLaterNodeTimerAndPreservesFutureTimer()
    {
        var listener = new CallbackListener();
        using var scheduler = SchedulerTestHarness.NewScheduler(listener);
        var discardedNode = new SimulationNodeIdentity("discarded");
        var survivorNode = new SimulationNodeIdentity("survivor");
        SimulationNodeContext context = CreateContext(scheduler, discardedNode);
        var trace = new List<string>();
        var discardedDuringElapsedBatch = false;
        listener.Callback = (operation, state) =>
        {
            // An elapsed scheduler timer registers its callback operation synchronously. Discard
            // reentrantly while ElapseTimers is still walking the already-dequeued same-time batch.
            if (!discardedDuringElapsedBatch
                && state == SimulationOperationState.Created
                && operation.Node is null)
            {
                discardedDuringElapsedBatch = true;
                context.DiscardPendingWork();
            }
        };
        _ = scheduler.RegisterTimer(
            node: null,
            TimeSpan.FromMinutes(5),
            () => trace.Add("first"));
        _ = scheduler.RegisterTimer(
            discardedNode,
            TimeSpan.FromMinutes(5),
            () => trace.Add("discarded"));
        _ = scheduler.RegisterTimer(
            survivorNode,
            TimeSpan.FromMinutes(7),
            () => trace.Add("later"));

        Assert.True(scheduler.TryAdvanceVirtualTime());
        Assert.Equal(TimeSpan.FromMinutes(5), scheduler.VirtualTime);
        Assert.Equal(SimulationNodeState.Discarded, context.State);
        Assert.True(discardedDuringElapsedBatch);
        Assert.Equal(1, scheduler.PendingTimerRegistrationCount);

        scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(["first"], trace);
        Assert.Equal(TimeSpan.FromMinutes(5), scheduler.VirtualTime);
        Assert.True(scheduler.TryAdvanceVirtualTime());
        Assert.Equal(TimeSpan.FromMinutes(7), scheduler.VirtualTime);
        scheduler.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(["first", "later"], trace);
        Assert.Equal(0, scheduler.PendingTimerRegistrationCount);
        Assert.False(scheduler.TryAdvanceVirtualTime());
    }

    [Fact]
    public void DiscardPendingWorkRecordAndReplayConsumeTheSameDecisionStream()
    {
        var log = new SimulationDecisionLog();
        ScenarioResult recorded = RunScenario(log, replay: null);
        Assert.NotEmpty(log.Records);

        ScenarioResult replayed = RunScenario(record: null, log.Records);

        Assert.Equal(recorded.Trace, replayed.Trace);
        Assert.Equal(recorded.TargetStates, replayed.TargetStates);
        Assert.Equal(recorded.VirtualTime, replayed.VirtualTime);
        Assert.Equal(recorded.CreatedWorkerCount, replayed.CreatedWorkerCount);
        Assert.Equal(["survivor-a:0", "survivor-b:0", "survivor-a:1", "survivor-b:1"], recorded.Trace);
        Assert.All(recorded.TargetStates, state => Assert.Equal(SimulationOperationState.Canceled, state));
    }

    [Fact]
    public void DiscardPendingWorkCleansRegistrationsWorkersAndPreservesExactlyOneRunning()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var discardedNode = new SimulationNodeIdentity("discarded");
        var survivorNode = new SimulationNodeIdentity("survivor");
        SimulationNodeContext context = CreateContext(scheduler, discardedNode);
        SimulationResource resource = scheduler.CreateResource(
            SimulationResourceKind.Semaphore,
            "discarded-resource");
        SimulationOperation parked = scheduler.Schedule(
            "parked",
            () => scheduler.WaitOnResource(
                resource,
                TimeSpan.FromMinutes(5),
                SimulationPauseReason.ResourceWait("discarded-resource")),
            discardedNode);
        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Thread parkedWorker = parked.Thread!;

        _ = scheduler.ScheduleWhenReady(
            discardedNode,
            static () => false,
            static () => { });
        _ = scheduler.RegisterTimer(
            discardedNode,
            TimeSpan.FromMinutes(7),
            static () => { });
        SimulationOperation queuedA =
            scheduler.Schedule("discarded-a", static () => { }, discardedNode);
        SimulationOperation queuedB =
            scheduler.Schedule("discarded-b", static () => { }, discardedNode);

        var concurrent = 0;
        var maximumConcurrent = 0;
        var survivorExecutions = 0;
        var survivorWorkers = new HashSet<Thread>();
        var survivorOperations = new List<SimulationOperation>();
        for (var operationIndex = 0; operationIndex < 4; operationIndex++)
        {
            survivorOperations.Add(scheduler.Schedule(
                $"survivor-{operationIndex}",
                () =>
                {
                    survivorWorkers.Add(Thread.CurrentThread);
                    for (var iteration = 0; iteration < 5; iteration++)
                    {
                        int current = Interlocked.Increment(ref concurrent);
                        UpdateMaximum(ref maximumConcurrent, current);
                        survivorExecutions++;
                        Interlocked.Decrement(ref concurrent);
                        scheduler.Yield();
                    }
                },
                survivorNode));
        }

        context.DiscardPendingWork();
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal(1, maximumConcurrent);
        Assert.Equal(20, survivorExecutions);
        Assert.Equal(SimulationOperationState.Canceled, parked.State);
        Assert.Equal(SimulationOperationState.Canceled, queuedA.State);
        Assert.Equal(SimulationOperationState.Canceled, queuedB.State);
        Assert.Null(parked.Thread);
        Assert.Equal(0, resource.WaiterCount);
        Assert.Empty(scheduler.CapturePendingTimeouts());
        Assert.Equal(0, scheduler.PendingTimerRegistrationCount);
        Assert.Equal(0, scheduler.PendingOperationCount);
        Assert.Equal(4, survivorWorkers.Count);
        Assert.Equal(4, scheduler.CreatedWorkerThreadCount);
        Assert.Equal(1, scheduler.IdleWorkerThreadCount);
        Assert.All(survivorOperations, static operation => Assert.Null(operation.Thread));
        Assert.All(
            scheduler.CaptureStatus().Where(status => status.NodeAddress == discardedNode.Address),
            status => Assert.Equal(SimulationOperationState.Canceled, status.State));
        Assert.All(
            scheduler.CaptureStatus().Where(status => status.NodeAddress == survivorNode.Address),
            status => Assert.Equal(SimulationOperationState.Completed, status.State));

        scheduler.Dispose();
        Assert.All(survivorWorkers, static worker => Assert.False(worker.IsAlive));
        Assert.False(parkedWorker.IsAlive);
    }

    private static SimulationNodeContext CreateContext(
        SimulationScheduler scheduler,
        SimulationNodeIdentity node)
    {
        var guard = new SingleThreadedGuard(
            () => scheduler.IsSimulationThread
                ? SimulationScheduler.SimulationLogicalThreadOwnerId
                : Environment.CurrentManagedThreadId);
        return new SimulationNodeContext(
            scheduler,
            guard,
            new SimulationRandom(seed: 1),
            externalSchedulerLane: null,
            logger: null,
            node);
    }

    private static ScenarioResult RunScenario(
        SimulationDecisionLog? record,
        IReadOnlyList<SimulationDecisionRecord>? replay)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        if (record is not null)
        {
            scheduler.DecisionLog = record;
        }

        if (replay is not null)
        {
            scheduler.ReplayValidator = new SimulationDecisionReplayValidator(
                new SimulationInMemoryDecisionReplayReader(replay));
        }

        var discardedNode = new SimulationNodeIdentity("discarded");
        var survivorNode = new SimulationNodeIdentity("survivor");
        SimulationNodeContext context = CreateContext(scheduler, discardedNode);
        SimulationOperation targetA =
            scheduler.Schedule("target-a", static () => { }, discardedNode);
        SimulationOperation targetB =
            scheduler.Schedule("target-b", static () => { }, discardedNode);
        var trace = new List<string>();
        ScheduleSurvivor("survivor-a");
        ScheduleSurvivor("survivor-b");

        context.DiscardPendingWork();
        scheduler.Drain(TestContext.Current.CancellationToken);
        if (replay is not null)
        {
            scheduler.ValidateReplayComplete();
        }

        return new ScenarioResult(
            [.. trace],
            [targetA.State, targetB.State],
            scheduler.VirtualTime,
            scheduler.CreatedWorkerThreadCount);

        void ScheduleSurvivor(string name)
        {
            scheduler.Schedule(
                name,
                () =>
                {
                    for (var iteration = 0; iteration < 2; iteration++)
                    {
                        trace.Add($"{name}:{iteration}");
                        scheduler.Yield();
                    }
                },
                survivorNode);
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, observed) != observed);
    }

    private sealed record ScenarioResult(
        string[] Trace,
        SimulationOperationState[] TargetStates,
        TimeSpan VirtualTime,
        int CreatedWorkerCount);

    private sealed class CallbackListener : ISimulationOperationListener
    {
        public Action<SimulationOperation, SimulationOperationState>? Callback { get; set; }

        public void OnStateChanged(
            SimulationOperation operation,
            SimulationOperationState newState) =>
            Callback?.Invoke(operation, newState);
    }
}
