using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tests.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

public sealed class SimulationRuntimeDispatchTests
{
    [Fact]
    public void ControlledApiOffTheSimulationThreadIsRejectedAndSurfacedOnTheDriver()
    {
        using var coordinator = new SimulationSchedulerTestHost(seed: 7);
        SimulationRuntimeIdentity runtime = coordinator.Scheduler.Runtime;
        (Exception? First, Exception? Second, int ThreadId) escaped = default;

        // A thread started from simulation work carries the simulation's ambient context with it.
        TaskTestHarness.RunInSimulation(coordinator, () => escaped = RunOnNewThread(() => (
            Record.Exception(() => SimulationRuntimeDispatch.RequireActiveSimulation("test.first")),
            Record.Exception(() => SimulationRuntimeDispatch.RequireActiveSimulation("test.second")),
            Environment.CurrentManagedThreadId)));

        var rejected = Assert.IsType<SimulationThreadAffinityException>(escaped.First);
        Assert.Equal("test.first", rejected.ApiName);
        Assert.Same(runtime, rejected.Runtime);
        Assert.Equal(escaped.ThreadId, rejected.ManagedThreadId);
        Assert.StartsWith($"test.first ran on managed thread {escaped.ThreadId}, ", rejected.Message, StringComparison.Ordinal);
        Assert.Contains($"'{runtime.Id}' (seed 7)", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("test.second", Assert.IsType<SimulationThreadAffinityException>(escaped.Second).ApiName);

        // Nothing is runnable, yet the driver surfaces the first violation, once, as its own instance carrying the escaped
        // thread's stack.
        var surfaced = Assert.Throws<SimulationThreadAffinityException>(
            () => coordinator.Scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.NotSame(rejected, surfaced);
        Assert.Equal("test.first", surfaced.ApiName);
        Assert.Contains(nameof(SimulationRuntimeDispatch.RequireActiveSimulation), surfaced.StackTrace, StringComparison.Ordinal);
        Assert.False(coordinator.Scheduler.RunStep(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ControlledApiUnderAnotherSimulationsOperationIsRejectedAndSurfacedByItsOwnDriver()
    {
        using var ambient = new SimulationSchedulerTestHost(seed: 3);
        using var host = new SimulationSchedulerTestHost(seed: 4);
        Exception? rejection = null;
        host.Scheduler.Schedule(
            "foreign",
            () =>
            {
                using (SimulationExecutionContext.EnterRuntime(ambient.Scheduler.Runtime))
                {
                    rejection = Record.Exception(() => SimulationRuntimeDispatch.RequireActiveSimulation("test.api"));
                }
            });

        host.Scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Same(ambient.Scheduler.Runtime, Assert.IsType<SimulationThreadAffinityException>(rejection).Runtime);
        Assert.Equal(
            "test.api",
            Assert.Throws<SimulationThreadAffinityException>(
                () => ambient.Scheduler.RunStep(TestContext.Current.CancellationToken)).ApiName);
    }

    [Fact]
    public void OneThreadAtATimeHoldsTheControlScope()
    {
        using var coordinator = new SimulationSchedulerTestHost();
        SimulationScheduler scheduler = coordinator.Scheduler;
        SimulationRuntimeIdentity runtime = scheduler.Runtime;

        using (scheduler.EnterControlScope())
        {
            using (scheduler.EnterControlScope())
            {
                Assert.True(scheduler.IsSimulationThread);
            }

            // The nested scope leaves control with the outermost one.
            Assert.True(scheduler.IsSimulationThread);
            Exception? contender = RunOnNewThread(() => Record.Exception(scheduler.EnterControlScope));
            Assert.Contains(
                $"managed thread {Environment.CurrentManagedThreadId} controls it",
                Assert.IsType<SimulationSchedulerException>(contender).Message,
                StringComparison.Ordinal);
        }

        Assert.False(scheduler.IsSimulationThread);
        (bool Inside, Exception? After) handedOff = RunOnNewThread(() =>
        {
            using (SimulationExecutionContext.EnterRuntime(runtime))
            {
                bool inside;
                using (scheduler.EnterControlScope())
                {
                    inside = SimulationRuntimeDispatch.RequireActiveSimulation("test.api").Runtime == runtime;
                }

                return (inside, Record.Exception(() => SimulationRuntimeDispatch.RequireActiveSimulation("test.api")));
            }
        });
        Assert.True(handedOff.Inside);
        Assert.IsType<SimulationThreadAffinityException>(handedOff.After);
    }

    private static T RunOnNewThread<T>(Func<T> body)
    {
        T result = default!;
        var thread = new Thread(() => result = body()) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The new thread did not finish.");
        return result;
    }

    [Fact]
    public void RequireEnvironmentOutsideSimulationRequiresActiveSimulation()
    {
        Assert.False(SimulationExecutionContext.IsActive);

        Exception? exception = Record.Exception(
            () => SimulationRuntimeDispatch.RequireEnvironment("test.api"));

        SimulationNotActiveExceptionAssert.Equal(exception, "test.api");
    }

    [Fact]
    public void RequireEnvironmentReturnsAmbientEnvironmentAndNode()
    {
        var clock = ShimTestHarness.CreateClock();
        var environment = ShimTestHarness.CreateEnvironment(clock);

        ShimTestHarness.RunInSimulation(environment, () =>
        {
            var (_, resolved, node) = SimulationRuntimeDispatch.RequireEnvironment("test.api");
            Assert.IsType<SimulationRuntimeEnvironment>(resolved);
            Assert.Equal(environment.GetUtcNow(node), resolved.GetUtcNow(node));
            Assert.Equal(ShimTestHarness.DefaultNodeAddress, node?.Address);
        });
    }

    [Fact]
    public void ParallelRuntimesCarryIndependentEnvironments()
    {
        var clockA = ShimTestHarness.CreateClock();
        var clockB = ShimTestHarness.CreateClock();
        var environmentA = ShimTestHarness.CreateEnvironment(clockA);
        var environmentB = ShimTestHarness.CreateEnvironment(clockB);
        var runtimeA = ShimTestHarness.NewRuntime(description: "A");
        var runtimeB = ShimTestHarness.NewRuntime(description: "B");
        clockA.Advance(TimeSpan.FromHours(1));
        clockB.Advance(TimeSpan.FromHours(2));

        var utcA = ShimTestHarness.RunInSimulation(environmentA, ControlledDateTime.GetUtcNow, runtime: runtimeA);
        var utcB = ShimTestHarness.RunInSimulation(environmentB, ControlledDateTime.GetUtcNow, runtime: runtimeB);

        Assert.Equal(new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc), utcA);
        Assert.Equal(new DateTime(2024, 1, 1, 2, 0, 0, DateTimeKind.Utc), utcB);
    }
}
