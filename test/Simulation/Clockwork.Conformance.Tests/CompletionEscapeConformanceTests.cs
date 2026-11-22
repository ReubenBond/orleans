using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Pins the completions which still send a framework continuation to the real thread pool, and checks that
/// <see cref="ThreadPoolEscapeGuard"/> reports such an escape. Clockwork runs every completion it makes for simulation
/// code where the BCL inlines continuations, but a completion which the BCL makes itself follows the BCL's dispatch:
/// a task which uninstrumented code completes in a lane's work, and a task which runs visibly on a Clockwork scheduler,
/// whose continuations the BCL runs while that scheduler is current. Each escaped continuation reports whether it ran on
/// the simulation's logical thread, and is joined with a bound before the host is disposed.
/// </summary>
public sealed class CompletionEscapeConformanceTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ATaskWhichUninstrumentedCodeCompletesInALanesWorkContinuesOnTheThreadPool()
    {
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);

        Task<bool> continued = CompleteUninstrumentedInALanesWork(host);

        Assert.False(await Joined(continued));
    }

    [Fact]
    public async Task AContinuationOfATaskWhichRanOnALaneSchedulerContinuesOnTheThreadPool()
    {
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        SimulationTaskScheduler lane = host.NodeTaskScheduler("node");
        Task<bool>? continued = null;

        host.InvokeAction(() =>
        {
            Task onTheLane = Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, lane);
            continued = ResumedOnTheLogicalThread(onTheLane, host.Scheduler);
        });

        Assert.False(await Joined(continued!));
    }

    // The guard's negative control: the same escape, with the guard on, fails the test when the host is disposed.
    [Fact]
    public async Task TheGuardReportsWorkWhichTheLogicalThreadQueuesToTheThreadPool()
    {
        var host = new SimulationHost(Start);
        Task<bool> continued = CompleteUninstrumentedInALanesWork(host);
        Assert.False(await Joined(continued));

        Exception? failure = Record.Exception(host.Dispose);

        Assert.Contains("queued 1 work item(s) to the real thread pool", failure?.Message, StringComparison.Ordinal);
    }

    private static Task<bool> CompleteUninstrumentedInALanesWork(SimulationHost host)
    {
        var source = new TaskCompletionSource();
        Task<bool>? continued = null;
        host.InvokeAction(() =>
        {
            continued = ResumedOnTheLogicalThread(source.Task, host.Scheduler);
            source.SetResult();
        });
        return continued!;
    }

    private static async Task<bool> ResumedOnTheLogicalThread(Task task, SimulationScheduler scheduler)
    {
        await task.ConfigureAwait(false);
        return scheduler.IsSimulationThread;
    }

    private static Task<bool> Joined(Task<bool> continued) =>
        continued.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}
