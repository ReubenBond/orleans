using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling.Resources;

public sealed class SimulationTimerQueueTests
{
    [Fact]
    public void AdvanceToOrdersDueTimersAndRetainsFutureTimers()
    {
        var queue = new SimulationTimerQueue();
        SimulationTimerRegistration late = queue.Schedule(TimeSpan.FromTicks(3), onElapsed: null);
        SimulationTimerRegistration first = queue.Schedule(TimeSpan.FromTicks(1), onElapsed: null);
        SimulationTimerRegistration second = queue.Schedule(TimeSpan.FromTicks(1), onElapsed: null);
        SimulationTimerRegistration future = queue.Schedule(TimeSpan.FromTicks(5), onElapsed: null);

        IReadOnlyList<ISimulationTimerEntry> due = queue.AdvanceTo(TimeSpan.FromTicks(3));

        Assert.Equal([first, second, late], due);
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(future.DueTime, queue.NextDueTime());
    }

    [Fact]
    public void AdvanceToNextDuePurgesCanceledTimers()
    {
        var queue = new SimulationTimerQueue();
        SimulationTimerRegistration canceled = queue.Schedule(TimeSpan.FromTicks(1), onElapsed: null);
        SimulationTimerRegistration live = queue.Schedule(TimeSpan.FromTicks(2), onElapsed: null);
        canceled.Cancel();

        IReadOnlyList<ISimulationTimerEntry> due = queue.AdvanceToNextDue();

        Assert.Equal([live], due);
        Assert.Equal(TimeSpan.FromTicks(2), queue.Now);
        Assert.False(queue.HasPending);
    }
}
