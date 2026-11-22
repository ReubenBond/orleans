using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

/// <summary>
/// Covers coordinator access through a complete ambient runtime plus continuation and synchronous-wait
/// routing through that coordinator.
/// </summary>
public sealed class ControlledTaskRuntimeTests
{
    [Fact]
    public void RequireSchedulerOutsideSimulationRequiresActiveSimulation()
    {
        Assert.False(SimulationTaskRuntime.IsSimulationActive);

        Exception? exception = Record.Exception(
            () => SimulationTaskRuntime.RequireScheduler("test.api"));

        SimulationNotActiveExceptionAssert.Equal(exception, "test.api");
    }

    [Fact]
    public void RequireSchedulerReturnsNodeWhenActiveAndRegistered()
    {
        var coordinator = new SimulationSchedulerTestHost();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var (resolved, node) = SimulationTaskRuntime.RequireScheduler("test.api");
            Assert.Same(coordinator.Scheduler, resolved);
            Assert.NotNull(node);
            Assert.Equal(TaskTestHarness.DefaultNodeAddress, node!.Address);
        });
    }

    [Fact]
    public void ScheduleContinuationRoutesThroughCoordinatorWhenAntecedentReady()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var ran = false;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var completed = System.Threading.Tasks.Task.CompletedTask;
            SimulationTaskRuntime.ScheduleContinuation(completed, () => ran = true, "test.await", flowExecutionContext: false);

            // The continuation must not run inline - it is queued on the coordinator's loop.
            Assert.False(ran);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(ran);
        });
    }

    [Fact]
    public void ScheduleYieldQueuesContinuationWithoutRunningInline()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var ran = false;

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            SimulationTaskRuntime.ScheduleYield(() => ran = true, "test.yield", flowExecutionContext: false);
            Assert.False(ran);
            coordinator.Scheduler.RunUntilIdle(TestContext.Current.CancellationToken);
            Assert.True(ran);
        });
    }

    [Fact]
    public void DrainUntilCompletedPumpsUntilTaskCompletes()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var tcs = new System.Threading.Tasks.TaskCompletionSource();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            // Completing the task is itself scheduled work; the drain must run it.
            coordinator.Scheduler.Schedule(() => tcs.SetResult());
            SimulationTaskRuntime.DrainUntilCompleted(
                tcs.Task,
                "test.wait",
                TestContext.Current.CancellationToken);
            Assert.True(tcs.Task.IsCompleted);
        });
    }

    [Fact]
    public void DrainUntilCompletedThrowsDeadlockWhenTaskNeverCompletes()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var tcs = new System.Threading.Tasks.TaskCompletionSource();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            Assert.Throws<SimulationSynchronousWaitDeadlockException>(
                () => SimulationTaskRuntime.DrainUntilCompleted(
                    tcs.Task,
                    "test.wait",
                    TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public void DrainUntilCompletedHonorsCallerCancellation()
    {
        var coordinator = new SimulationSchedulerTestHost();
        var tcs = new System.Threading.Tasks.TaskCompletionSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        TaskTestHarness.RunInSimulation(coordinator, () =>
        {
            var exception = Assert.Throws<OperationCanceledException>(
                () => SimulationTaskRuntime.DrainUntilCompleted(
                    tcs.Task,
                    "test.wait",
                    cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
        });
    }

    [Fact]
    public void ParallelRuntimesResolveTheirOwnSchedulers()
    {
        var coordinatorA = new SimulationSchedulerTestHost(description: "A");
        var coordinatorB = new SimulationSchedulerTestHost(description: "B");

        var resolvedA = TaskTestHarness.RunInSimulation(
            coordinatorA,
            () =>
            {
                return SimulationTaskRuntime.RequireScheduler("api").Scheduler;
            });
        var resolvedB = TaskTestHarness.RunInSimulation(
            coordinatorB,
            () =>
            {
                return SimulationTaskRuntime.RequireScheduler("api").Scheduler;
            });

        Assert.Same(coordinatorA.Scheduler, resolvedA);
        Assert.Same(coordinatorB.Scheduler, resolvedB);
    }
}
