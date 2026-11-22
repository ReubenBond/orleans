using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Tests;

internal static class SimulationTestHarness
{
    public static SimulationScheduler NewScheduler(
        DateTimeOffset? startDateTime = null,
        int seed = 1)
    {
        var runtime = new SimulationRuntimeIdentity(Guid.NewGuid(), seed, "test");
        return new SimulationScheduler(
            runtime,
            new SimulationSeedAuthority(seed),
            startDateTime ?? DateTimeOffset.UnixEpoch,
            TimeZoneInfo.Utc);
    }

    public static SingleThreadedGuard NewGuard(SimulationScheduler scheduler) =>
        new(() => scheduler.IsSimulationThread
            ? SimulationScheduler.SimulationLogicalThreadOwnerId
            : Environment.CurrentManagedThreadId);

    public static SimulationSchedulerLane NewLane(
        DateTimeOffset? startDateTime = null,
        SimulationNodeIdentity? node = null)
    {
        var scheduler = NewScheduler(startDateTime);
        return new SimulationSchedulerLane(scheduler, NewGuard(scheduler), node);
    }

    public static (SimulationScheduler Scheduler, SingleThreadedGuard Guard, SimulationNodeContext Context)
        NewNodeComponents(
            string address = "node-1",
            SimulationSchedulerLane? externalLane = null)
    {
        var scheduler = externalLane?.Scheduler ?? NewScheduler();
        var guard = NewGuard(scheduler);
        var node = new SimulationNodeIdentity(address);
        var context = new SimulationNodeContext(
            scheduler,
            guard,
            new SimulationRandom(1),
            externalLane,
            logger: null,
            node);
        return (scheduler, guard, context);
    }
}
