using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;
using Xunit.Sdk;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Shows that the quiescence check runs the work due at the current instant and fails on any work left for later
/// (test/Durable.Storage/docs/spec/verification.md §11.1).
/// </summary>
public sealed class SimulationLeakCheckTests
{
    /// <summary>
    /// Work due at the current instant runs, and the simulation is then quiescent.
    /// </summary>
    [Fact]
    public async Task WorkDueNowRunsAndLeavesTheSimulationQuiescent()
    {
#pragma warning disable RS0030 // The test passes its token to the cluster's replica methods alone, which only observe it.
        var cancellationToken = TestContext.Current.CancellationToken;
#pragma warning restore RS0030
        await using var cluster = new SimulationCluster(1, DateTimeOffset.UnixEpoch);
        var ran = false;
        cluster.RunToCompletion(
            () =>
            {
                cluster.SchedulerLane.Enqueue(() => ran = true);
                return Task.CompletedTask;
            },
            cancellationToken);

        Assert.False(ran);
        SimulationLeakCheck.AssertQuiescent(cluster, cancellationToken);
        Assert.True(ran);
    }

    /// <summary>
    /// A one-shot timer armed after its owner was disposed outlives the owner, though it would end on its own, and fails
    /// the check; the check does not advance the clock to let it fire.
    /// </summary>
    [Fact]
    public Task AOneShotTimerArmedAfterDisposalFailsTheCheck() =>
        AssertTimerFailsTheCheckAsync(period: Timeout.InfiniteTimeSpan);

    /// <summary>
    /// A periodic timer which outlives its scenario fails the check.
    /// </summary>
    [Fact]
    public Task APeriodicTimerWhichOutlivesItsScenarioFailsTheCheck() =>
        AssertTimerFailsTheCheckAsync(period: TimeSpan.FromMilliseconds(5));

    private static async Task AssertTimerFailsTheCheckAsync(TimeSpan period)
    {
#pragma warning disable RS0030 // The test passes its token to the cluster's replica methods alone, which only observe it.
        var cancellationToken = TestContext.Current.CancellationToken;
#pragma warning restore RS0030
        await using var cluster = new SimulationCluster(1, DateTimeOffset.UnixEpoch);
        ITimer? timer = null;
        var fired = false;
        cluster.RunToCompletion(
            () =>
            {
                timer = cluster.TimeProvider.CreateTimer(_ => fired = true, null, TimeSpan.FromMilliseconds(5), period);
                return Task.CompletedTask;
            },
            cancellationToken);

        var start = cluster.TimeProvider.GetUtcNow();
        var failure = Assert.Throws<FailException>(() => SimulationLeakCheck.AssertQuiescent(cluster, cancellationToken));
        Assert.Contains("Work outlived the simulation's owners", failure.Message, StringComparison.Ordinal);
        Assert.False(fired);
        Assert.Equal(start, cluster.TimeProvider.GetUtcNow());

        cluster.RunToCompletion(
            () =>
            {
                timer!.Dispose();
                return Task.CompletedTask;
            },
            cancellationToken);
        SimulationLeakCheck.AssertQuiescent(cluster, cancellationToken);
    }
}
