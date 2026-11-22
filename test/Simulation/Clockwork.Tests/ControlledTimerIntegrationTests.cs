using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Tests;

public sealed class ControlledTimerIntegrationTests
{
    [Fact]
    public async Task PendingTimerIsReportedAndTeardownDrainsIt()
    {
        var simulation = new SimulationCluster(
            seed: 1,
            startDateTime: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        SimulationNode node = simulation.AddNode("node");
        ControlledTimer? timer = null;
        var timerFired = false;

        node.Context.SchedulerLane.EnqueueAfter(
            () => timer = new ControlledTimer(
                _ => timerFired = true,
                null,
                TimeSpan.FromMinutes(1),
                Timeout.InfiniteTimeSpan),
            TimeSpan.Zero);

        SimulationExecutionResult result = simulation.RunUntil(() => timer is not null, TestContext.Current.CancellationToken);

        SimulationScheduledItemDiagnostic deadline = Assert.Single(
            result.PendingWork.Items,
            static item => item.QueueIdentity == "simulation-scheduler");
        Assert.Equal("CallbackTimer", deadline.Kind);
        Assert.Equal("Simulation scheduler timer", deadline.Description);
        Assert.Equal(1, result.PendingWork.WaitingCount);

        await simulation.DisposeAsync();
        Assert.True(timerFired);
    }
}
