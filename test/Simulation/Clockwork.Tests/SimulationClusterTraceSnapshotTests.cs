namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Characterizes the cluster's execution results as a structured, replayable trace.
/// </summary>
public sealed class SimulationClusterTraceSnapshotTests
{
    [Fact]
    public async Task IdenticalSeedsAndScriptsProduceIdenticalTraceSnapshots()
    {
        var firstTrace = await RunScriptAndCaptureTraceAsync(seed: 2024);
        var secondTrace = await RunScriptAndCaptureTraceAsync(seed: 2024);

        Assert.Equal(firstTrace, secondTrace);

        // Sanity check that the trace actually captured a meaningful, non-trivial timeline
        // rather than trivially matching because nothing happened.
        Assert.Equal(2, firstTrace.Count);
        Assert.Contains(firstTrace, e => e.Reason == SimulationExecutionReason.Idle);
        Assert.Contains(firstTrace, e => e.Reason == SimulationExecutionReason.ConditionMet);
    }

    private static async Task<IReadOnlyList<TraceEvent>> RunScriptAndCaptureTraceAsync(int seed)
    {
        await using var cluster = new SimulationCluster(seed, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var trace = new List<TraceEvent>();

        // A small, deterministic script mixing node suspension with delayed work, so the
        // resulting trace has multiple distinguishable event kinds.
        var messagesDelivered = 0;
        node.Context.SchedulerLane.EnqueueAfter(() => messagesDelivered++, TimeSpan.FromSeconds(2));
        node.SuspendFor(TimeSpan.FromSeconds(1));

        // RunFor advances time (emitting TimeAdvancing) and drains work until idle
        // (emitting ReachedIdleState), covering the suspend/resume + delayed work interaction.
        Record(cluster.RunFor(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        // The condition is already satisfied at this point, so this only exercises the
        // ConditionMet result without doing any further work.
        Record(cluster.RunUntil(() => messagesDelivered == 1, TestContext.Current.CancellationToken));

        return trace;

        void Record(SimulationExecutionResult result) =>
            trace.Add(new TraceEvent(
                result.Reason,
                result.ElapsedSimulatedTime,
                result.StepsExecuted,
                result.TimeAdvanceCount));
    }

    public readonly record struct TraceEvent(
        SimulationExecutionReason Reason,
        TimeSpan SimulatedTime,
        int StepsExecuted,
        int TimeAdvanceCount);
}
