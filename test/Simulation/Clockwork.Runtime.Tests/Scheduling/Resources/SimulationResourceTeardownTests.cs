using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling.Resources;

/// <summary>
/// Teardown/disposal coverage for the resource/wait states: an operation parked on an infinite
/// resource wait, one parked with a pending virtual-time timeout, and one parked with a live
/// cancellation registration must all be reclaimed by <see cref="SimulationScheduler.Dispose"/>
/// without leaking a physical thread, a pending timeout item, or a cancellation callback.
/// </summary>
public sealed class SimulationResourceTeardownTests
{
    private static SimulationPauseReason Reason(string tag) => SimulationPauseReason.ResourceWait(tag);

    [Fact]
    public void DisposeReclaimsAnOperationParkedOnAnInfiniteResourceWait()
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Monitor, "m");
        SimulationOperation op;
        try
        {
            op = scheduler.Schedule("waiter", () => scheduler.WaitOnResource(resource, Reason("m")));
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
            Assert.Equal(SimulationOperationState.Paused, op.State);
            Assert.Equal(1, resource.WaiterCount);
        }
        finally
        {
            scheduler.Dispose();
        }

        Assert.Equal(SimulationOperationState.Canceled, op.State);
        Assert.True(op.Thread is null or { IsAlive: false }, "Parked-on-resource operation leaked a live thread.");
    }

    [Fact]
    public void DisposeClearsAPendingVirtualTimeoutForAParkedOperation()
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        var resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "sem");
        SimulationOperation op;
        try
        {
            op = scheduler.Schedule(
                "timed",
                () => scheduler.WaitOnResource(resource, TimeSpan.FromSeconds(5), Reason("sem")));
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
            Assert.Equal(SimulationOperationState.Paused, op.State);

            // The finite wait registered exactly one pending virtual-time timeout.
            Assert.Single(scheduler.CapturePendingTimeouts());
        }
        finally
        {
            scheduler.Dispose();
        }

        Assert.Equal(SimulationOperationState.Canceled, op.State);
        Assert.True(op.Thread is null or { IsAlive: false }, "Parked-on-timeout operation leaked a live thread.");
    }

    [Fact]
    public void DisposeUnregistersCancellationSoLaterTokenCancellationIsInert()
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var resource = scheduler.CreateResource(SimulationResourceKind.WaitHandle, "wh");
        SimulationOperation op;
        try
        {
            op = scheduler.Schedule(
                "cancelable",
                () => scheduler.WaitOnResource(resource, Timeout.InfiniteTimeSpan, Reason("wh"), cts.Token));
            Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
            Assert.Equal(SimulationOperationState.Paused, op.State);
        }
        finally
        {
            scheduler.Dispose();
        }

        Assert.Equal(SimulationOperationState.Canceled, op.State);

        // The registration was disposed during teardown, so cancelling afterwards must not throw or
        // re-enter the disposed scheduler.
        cts.Cancel();
    }

    [Fact]
    public void DisposeWithEveryPausedStateReclaimsAllThreads()
    {
        var scheduler = SchedulerTestHarness.NewScheduler();
        using var cts = new CancellationTokenSource();
        var infinite = scheduler.CreateResource(SimulationResourceKind.Monitor, "inf");
        var timed = scheduler.CreateResource(SimulationResourceKind.Semaphore, "timed");
        var cancelable = scheduler.CreateResource(SimulationResourceKind.WaitHandle, "cancel");

        var ops = new List<SimulationOperation>
        {
            scheduler.Schedule("yield", () => scheduler.Pause(SimulationPauseReason.Yield)),
            scheduler.Schedule("infinite", () => scheduler.WaitOnResource(infinite, Reason("inf"))),
            scheduler.Schedule("timed", () => scheduler.WaitOnResource(timed, TimeSpan.FromSeconds(3), Reason("timed"))),
            scheduler.Schedule("cancelable", () => scheduler.WaitOnResource(cancelable, Timeout.InfiniteTimeSpan, Reason("cancel"), cts.Token)),
        };

        // Park every operation in its distinct paused state.
        while (scheduler.RunStep(TestContext.Current.CancellationToken))
        {
        }

        Assert.All(ops, op => Assert.Equal(SimulationOperationState.Paused, op.State));

        scheduler.Dispose();

        Assert.All(ops, op =>
        {
            Assert.Equal(SimulationOperationState.Canceled, op.State);
            Assert.True(op.Thread is null or { IsAlive: false }, $"Operation {op.Id} leaked a live thread through teardown.");
        });
    }
}
