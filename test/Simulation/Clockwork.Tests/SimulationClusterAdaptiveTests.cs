namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers the adaptive execution-budget overloads: escalation across successive
/// batches, immediate termination without escalation for every non-<see cref="SimulationExecutionReason.MaxIterationsReached"/>
/// reason, the hard <see cref="AdaptiveExecutionBudget.MaxTotalIterations"/> ceiling, and folding of
/// combined counters across batches.
/// </summary>
public sealed class SimulationClusterAdaptiveTests
{
    [Fact]
    public async Task AdaptiveRunUntilEscalatesAcrossBatchesUntilConditionIsMet()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var counter = 0;
        const int stepsNeeded = 20;
        for (var i = 0; i < stepsNeeded; i++)
        {
            node.Context.SchedulerLane.Enqueue(() => counter++);
        }

        // A single batch of 2 iterations cannot possibly satisfy a condition that needs 20 steps -
        // reaching ConditionMet here is only possible if adaptive RunUntil escalated across several
        // batches on its own.
        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 2, growthFactor: 2.0, maxTotalIterations: 1000);
        var result = cluster.RunUntil(() => counter >= stepsNeeded, budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.ConditionMet, result.Reason);
        Assert.True(result.ConditionMet);
        Assert.Equal(stepsNeeded, result.StepsExecuted);
        Assert.Equal(stepsNeeded, counter);
        Assert.True(result.Iterations > budget.InitialMaxIterations, "Escalation must have occurred across more than one batch.");
    }

    [Fact]
    public async Task AdaptiveRunUntilStopsImmediatelyOnIdleWithoutEscalating()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        // The initial batch is large enough that, if escalation happened needlessly, the result
        // would report far more than a handful of iterations. An empty, condition-never-met cluster
        // must stop at Idle well before that.
        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 1000, growthFactor: 2.0, maxTotalIterations: 1_000_000);
        var result = cluster.RunUntil(() => false, budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.False(result.ConditionMet);
        Assert.True(result.Iterations <= 2, "An idle cluster should stop within the first batch's first couple of iterations.");
    }

    [Theory]
    [InlineData(SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded)]
    [InlineData(SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded)]
    public async Task AdaptiveRunUntilStopsOnGenuinelyStuckReasonsWithoutEscalating(SimulationExecutionReason expectedReason)
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        if (expectedReason == SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded)
        {
            cluster.MaxSimulatedTimeAdvance = TimeSpan.FromSeconds(1);
            var node = cluster.AddNode("node-1");
            node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(10));
        }
        else
        {
            cluster.MaxConsecutiveTimeAdvances = 2;
            var node = cluster.AddNode("node-1");
            node.Suspend();
            for (var i = 1; i <= 5; i++)
            {
                node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(i));
            }
        }

        // A single batch's worth of iterations (well under the initial budget) is all it should take
        // to detect the stuck condition - escalating further would not help and must not happen.
        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 1000, growthFactor: 2.0, maxTotalIterations: 1_000_000);
        var result = cluster.RunUntil(() => false, budget, TestContext.Current.CancellationToken);

        Assert.Equal(expectedReason, result.Reason);
        Assert.True(result.Iterations < budget.InitialMaxIterations);
        cluster.MaxSimulatedTimeAdvance = TimeSpan.FromMinutes(10);
    }

    [Fact]
    public async Task AdaptiveRunUntilHonorsTheHardTotalIterationsCapWhenConditionNeverIsMet()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var keepRunning = true;

        // Self-perpetuating work: every step re-enqueues itself immediately, so the cluster is never
        // idle and every iteration is a real step. Escalation would run forever without a hard cap.
        void RunForever()
        {
            if (keepRunning)
            {
                node.Context.SchedulerLane.Enqueue(RunForever);
            }
        }

        RunForever();

        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 3, growthFactor: 2.0, maxTotalIterations: 20);
        var result = cluster.RunUntil(() => false, budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.MaxIterationsReached, result.Reason);
        Assert.Equal(budget.MaxTotalIterations, result.Iterations);
        Assert.Equal(budget.MaxTotalIterations, result.StepsExecuted);
        keepRunning = false;
    }

    [Fact]
    public async Task AdaptiveRunUntilUsesTheExplicitDefaultBudget()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        var result = cluster.RunUntil(() => true, AdaptiveExecutionBudget.Default, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.ConditionMet, result.Reason);
        Assert.Equal(0, result.Iterations);
    }

    [Fact]
    public async Task AdaptiveRunsHonorCallerCancellation()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new AdaptiveExecutionBudget(maxTotalIterations: 1_000);

        var untilException = Assert.Throws<OperationCanceledException>(
            () => cluster.RunUntil(
                () => false,
                budget,
                cancellation.Token));
        var idleException = Assert.Throws<OperationCanceledException>(
            () => cluster.RunUntilIdle(
                budget,
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, untilException.CancellationToken);
        Assert.Equal(cancellation.Token, idleException.CancellationToken);
    }

    [Fact]
    public async Task AdaptiveCompletionWinsCancellationAtBatchBoundary()
    {
        var budget = new AdaptiveExecutionBudget(
            initialMaxIterations: 1,
            growthFactor: 2,
            maxTotalIterations: 1_000);

        await using (var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch))
        using (var cancellation = new CancellationTokenSource())
        {
            var node = cluster.AddNode("node-1");
            var completed = false;
            node.Context.SchedulerLane.Enqueue(() =>
            {
                completed = true;
                cancellation.Cancel();
            });

            SimulationExecutionResult result = cluster.RunUntil(
                () => completed,
                budget,
                cancellation.Token);

            Assert.Equal(SimulationExecutionReason.ConditionMet, result.Reason);
        }

        await using (var cluster = new SimulationCluster(seed: 2, DateTimeOffset.UnixEpoch))
        using (var cancellation = new CancellationTokenSource())
        {
            var node = cluster.AddNode("node-1");
            node.Context.SchedulerLane.Enqueue(cancellation.Cancel);

            SimulationExecutionResult result = cluster.RunUntilIdle(
                budget,
                cancellation.Token);

            Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        }
    }

    [Fact]
    public async Task AdaptiveRunUntilRejectsNullCondition()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        Assert.Throws<ArgumentNullException>(() => cluster.RunUntil(null!, AdaptiveExecutionBudget.Default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdaptiveRunUntilIdleEscalatesAcrossBatchesUntilIdle()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        const int stepsToDrain = 20;
        for (var i = 0; i < stepsToDrain; i++)
        {
            node.Context.SchedulerLane.Enqueue(() => { });
        }

        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 2, growthFactor: 2.0, maxTotalIterations: 1000);
        var result = cluster.RunUntilIdle(budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(stepsToDrain, result.StepsExecuted);
        Assert.True(result.Iterations > budget.InitialMaxIterations, "Escalation must have occurred across more than one batch.");
    }

    [Fact]
    public async Task AdaptiveRunUntilIdleStopsImmediatelyOnTeardownCancellationWithoutEscalating()
    {
        using var cts = new CancellationTokenSource();
        await using var cluster = new SimulationCluster(
            seed: 1,
            DateTimeOffset.UnixEpoch,
            cancellationToken: cts.Token);
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.Enqueue(() => { });
        await cts.CancelAsync();

        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 1000, growthFactor: 2.0, maxTotalIterations: 1_000_000);
        var result = cluster.RunUntilIdle(budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.TeardownCancellationRequested, result.Reason);
        Assert.Equal(0, result.Iterations);
    }

    [Fact]
    public async Task AdaptiveRunUntilIdleFoldsCountersAcrossEscalatedBatches()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        const int steps = 6;
        for (var i = 0; i < steps; i++)
        {
            node.Context.SchedulerLane.Enqueue(() => { });
        }

        // Two timers spaced one second apart force two separate time advances after the immediate
        // steps drain, split across escalating batches.
        node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(1));
        node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(2));

        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 1, growthFactor: 2.0, maxTotalIterations: 1000);
        var result = cluster.RunUntilIdle(budget, TestContext.Current.CancellationToken);

        // Each timer contributes one time advance (to reach its due time) plus one step (to execute
        // its callback once ready), on top of the immediate steps.
        const int timers = 2;
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(steps + timers, result.StepsExecuted);
        Assert.Equal(timers, result.TimeAdvanceCount);
        Assert.Equal(cluster.StartDateTime + TimeSpan.FromSeconds(2), result.EndTime);

        // Every iteration across every folded batch is either a step, a time advance, or the final
        // "check and stop" iteration that discovered idleness - so the sum of the two counters can
        // never exceed the total iteration count.
        Assert.True(result.Iterations >= result.StepsExecuted + result.TimeAdvanceCount);
    }

    [Fact]
    public async Task AdaptiveRunUntilCarriesConsecutiveTimeAdvancesAcrossBatchBoundaries()
    {
        await using var cluster = CreateClusterWithBlockedTimers();
        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 1, growthFactor: 2.0, maxTotalIterations: 100);

        var result = cluster.RunUntil(() => false, budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded, result.Reason);
        Assert.Equal(3, result.TimeAdvanceCount);
        Assert.Equal(3, result.ConsecutiveTimeAdvanceCount);
        Assert.Equal(budget.MaxTotalIterations, result.Limits.MaxIterations);
    }

    [Fact]
    public async Task AdaptiveRunUntilIdleCarriesConsecutiveTimeAdvancesAcrossBatchBoundaries()
    {
        await using var cluster = CreateClusterWithBlockedTimers();
        var budget = new AdaptiveExecutionBudget(initialMaxIterations: 1, growthFactor: 2.0, maxTotalIterations: 100);

        var result = cluster.RunUntilIdle(budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded, result.Reason);
        Assert.Equal(3, result.TimeAdvanceCount);
        Assert.Equal(3, result.ConsecutiveTimeAdvanceCount);
        Assert.Equal(budget.MaxTotalIterations, result.Limits.MaxIterations);
    }

    private static SimulationCluster CreateClusterWithBlockedTimers()
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch)
        {
            MaxConsecutiveTimeAdvances = 2,
        };
        var node = cluster.AddNode("node-1");
        node.Suspend();
        for (var seconds = 1; seconds <= 3; seconds++)
        {
            node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(seconds));
        }

        return cluster;
    }

}
