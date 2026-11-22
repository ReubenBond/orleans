using System.Globalization;

namespace Orleans.Testing.Simulation.Tests;

/// <summary>
/// Covers the structured execution outcome API (<see cref="SimulationExecutionResult"/> and
/// friends) introduced alongside the consolidated drive-loop engine: every distinguishable
/// termination reason, pending-work diagnostics, deterministic formatting, and boundary values.
/// </summary>
public sealed class SimulationExecutionResultTests
{
    [Fact]
    public async Task RunUntilReportsConditionMetWithNoPendingWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var executed = false;
        node.Context.SchedulerLane.EnqueueAfter(() => executed = true, TimeSpan.FromSeconds(5));

        var result = cluster.RunUntil(() => executed, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.ConditionMet, result.Reason);
        Assert.True(result.ConditionMet);
        Assert.Equal(cluster.StartDateTime, result.StartTime);
        Assert.Equal(cluster.StartDateTime + TimeSpan.FromSeconds(5), result.EndTime);
        Assert.Equal(TimeSpan.FromSeconds(5), result.ElapsedSimulatedTime);
        Assert.Equal(1, result.StepsExecuted);
        Assert.True(result.TimeAdvanceCount >= 1);
        Assert.Equal(0, result.PendingWork.PendingCount);
    }

    [Fact]
    public async Task RunUntilReportsIdleWhenConditionNeverMetAndNoPendingWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        var result = cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 100);

        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.False(result.ConditionMet);
        Assert.Equal(0, result.PendingWork.PendingCount);
    }

    [Fact]
    public async Task RunUntilHonorsCallerCancellationBeforeEvaluatingTheCondition()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var conditionEvaluated = false;

        var exception = Assert.Throws<OperationCanceledException>(
            () => cluster.RunUntil(
                () =>
                {
                    conditionEvaluated = true;
                    return false;
                },
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(conditionEvaluated);
    }

    [Fact]
    public async Task RunUntilIdleReportsIdleOnAnEmptyCluster()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);

        var result = cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.False(result.ConditionMet);
        Assert.Equal(0, result.Iterations);
        Assert.Equal(0, result.PendingWork.PendingCount);
        Assert.Empty(result.PendingWork.Items);
    }

    [Fact]
    public async Task IdleWithPendingWorkIsReportedWhenASuspendedNodeHoldsAReadyItem()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        node.Suspend();
        node.Context.SchedulerLane.Enqueue(() => { });

        var result = cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.IdleWithPendingWork, result.Reason);
        Assert.Equal(0, result.PendingWork.RunnableCount);
        Assert.Equal(0, result.PendingWork.WaitingCount);
        Assert.Equal(1, result.PendingWork.BlockedCount);
        Assert.Equal(1, result.PendingWork.PendingCount);

        var item = Assert.Single(result.PendingWork.Items);
        Assert.Equal("node-1", item.QueueIdentity);
        Assert.Equal("action", item.Kind);
        Assert.Equal("Scheduled action", item.Description);
        Assert.True(item.IsReady);
        Assert.True(item.IsBlocked);

        // Idle-with-pending-work is additive diagnostic information, not a behavioral change.
    }

    [Fact]
    public async Task RunUntilIdleCanCancelSelfPerpetuatingSimulationWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        using var cancellation = new CancellationTokenSource();
        var executions = 0;
        Action? runAgain = null;
        runAgain = () =>
        {
            if (++executions == 3)
            {
                cancellation.Cancel();
                node.Context.SchedulerLane.Enqueue(() => { });
                return;
            }

            node.Context.SchedulerLane.Enqueue(runAgain!);
        };
        node.Context.SchedulerLane.Enqueue(runAgain);

        var exception = Assert.Throws<OperationCanceledException>(
            () => cluster.RunUntilIdle(cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(3, executions);
    }

    [Fact]
    public async Task RunUntilCompletionWinsCancellationRequestedByTheCompletingDispatch()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        using var cancellation = new CancellationTokenSource();
        var completed = false;
        node.Context.SchedulerLane.Enqueue(() =>
        {
            completed = true;
            cancellation.Cancel();
        });

        SimulationExecutionResult result = cluster.RunUntil(
            () => completed,
            cancellation.Token);

        Assert.Equal(SimulationExecutionReason.ConditionMet, result.Reason);
    }

    [Fact]
    public async Task PendingWorkItemsAreOrderedDeterministicallyByDueTimeThenSequenceThenQueue()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var nodeB = cluster.AddNode("node-2");
        var nodeA = cluster.AddNode("node-1");
        nodeA.Suspend();
        nodeB.Suspend();

        // Both items become due at the same simulated instant with independent per-queue sequence
        // numbers (0 each) - the queue identity is the only remaining, deterministic tiebreaker.
        nodeB.Context.SchedulerLane.Enqueue(() => { });
        nodeA.Context.SchedulerLane.Enqueue(() => { });

        var result = cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.PendingWork.Items.Count);
        Assert.Equal("node-1", result.PendingWork.Items[0].QueueIdentity);
        Assert.Equal("node-2", result.PendingWork.Items[1].QueueIdentity);
    }

    [Fact]
    public async Task MaxSimulatedTimeAdvanceExceededReportsTheAttemptedAdvance()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch) { MaxSimulatedTimeAdvance = TimeSpan.FromSeconds(1) };
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(10));

        var result = cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 100);

        Assert.Equal(SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded, result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(10), result.AttemptedTimeAdvance);
        cluster.MaxSimulatedTimeAdvance = TimeSpan.FromMinutes(10);
    }

    [Fact]
    public async Task MaxConsecutiveTimeAdvancesExceededIsReportedWhenManyAdvancesNeverExecuteWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch) { MaxConsecutiveTimeAdvances = 2 };
        var node = cluster.AddNode("node-1");
        node.Suspend();

        // None of these will ever execute (the node is suspended), so every step advances time
        // without doing any work. Five distinct due times comfortably exceed the threshold of 2.
        for (var i = 1; i <= 5; i++)
        {
            node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(i));
        }

        var result = cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 100);

        Assert.Equal(SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded, result.Reason);
        Assert.Equal(3, result.ConsecutiveTimeAdvanceCount);
        Assert.Equal(3, result.TimeAdvanceCount);
    }

    [Fact]
    public async Task MaxIterationsReachedStopsBeforeAnyOtherReason()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.Enqueue(() => { });

        var result = cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 1);

        Assert.Equal(SimulationExecutionReason.MaxIterationsReached, result.Reason);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(1, result.StepsExecuted);
    }

    [Fact]
    public async Task TeardownCancellationRequestedStopsRunUntilIdleImmediately()
    {
        using var cts = new CancellationTokenSource();
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch, cancellationToken: cts.Token);
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.Enqueue(() => { });
        await cts.CancelAsync();

        var result = cluster.RunUntilIdle(TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.TeardownCancellationRequested, result.Reason);
        Assert.Equal(0, result.Iterations);
    }

    [Fact]
    public async Task RunUntilDoesNotObserveTeardownCancellation()
    {
        // RunUntil never checked teardown cancellation in the original
        // implementation; only the RunUntilIdle family does. This must stay true.
        using var cts = new CancellationTokenSource();
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch, cancellationToken: cts.Token);
        await cts.CancelAsync();

        var result = cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 5);

        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
    }

    [Fact]
    public async Task RunForReportsIdleAfterProcessingTimerWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var fired = false;
        node.Context.SchedulerLane.EnqueueAfter(() => fired = true, TimeSpan.FromSeconds(2));

        var result = cluster.RunFor(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(fired);
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.False(result.ConditionMet);
        Assert.Equal(cluster.StartDateTime, result.StartTime);
        Assert.Equal(cluster.StartDateTime + TimeSpan.FromSeconds(5), result.EndTime);
        Assert.Equal(1, result.StepsExecuted);
    }

    [Fact]
    public async Task RunForReachesTheExactTargetThenDrainsWorkDueAtThatInstant()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var events = new List<string>();
        node.Context.SchedulerLane.EnqueueAfter(
            () =>
            {
                events.Add("timer");
                node.Context.SchedulerLane.Enqueue(() => events.Add("continuation"));
            },
            TimeSpan.FromSeconds(5));

        SimulationExecutionResult result = cluster.RunFor(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["timer", "continuation"], events);
        Assert.Equal(cluster.StartDateTime + TimeSpan.FromSeconds(5), result.EndTime);
        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
    }

    [Fact]
    public async Task RunForDoesNotExecuteWorkBeyondTheTarget()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var events = new List<string>();
        node.Context.SchedulerLane.EnqueueAfter(() => events.Add("within"), TimeSpan.FromSeconds(2));
        node.Context.SchedulerLane.EnqueueAfter(() => events.Add("beyond"), TimeSpan.FromSeconds(6));

        SimulationExecutionResult result = cluster.RunFor(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["within"], events);
        Assert.Equal(cluster.StartDateTime + TimeSpan.FromSeconds(5), result.EndTime);
        Assert.Equal(1, result.PendingWork.WaitingCount);
    }

    [Fact]
    public async Task RunForWithZeroDurationIsANoOp()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        var result = cluster.RunFor(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.Idle, result.Reason);
        Assert.Equal(0, result.Iterations);
        Assert.Equal(0, result.TimeAdvanceCount);
        Assert.Equal(result.StartTime, result.EndTime);
    }

    [Fact]
    public async Task RunForHonorsCallerCancellationEvenForZeroDuration()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(
            () => cluster.RunFor(TimeSpan.Zero, cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RunForRejectsNegativeDuration()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        Assert.Throws<ArgumentOutOfRangeException>(() => cluster.RunFor(TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunUntilRejectsNullCondition()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        Assert.Throws<ArgumentNullException>(() => cluster.RunUntil(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MaxIterationsBoundaryOfZeroNeverEvaluatesTheCondition()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var conditionEvaluated = false;

        var result = cluster.RunUntil(() => { conditionEvaluated = true; return true; }, TestContext.Current.CancellationToken, maxIterations: 0);

        Assert.Equal(SimulationExecutionReason.MaxIterationsReached, result.Reason);
        Assert.Equal(0, result.Iterations);
        Assert.False(conditionEvaluated);
    }

    [Fact]
    public async Task CancellationOnFinalIterationWinsUnmetCondition()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        using var cancellation = new CancellationTokenSource();
        node.Context.SchedulerLane.Enqueue(cancellation.Cancel);

        var exception = Assert.Throws<OperationCanceledException>(
            () => cluster.RunUntil(
                () => false,
                cancellation.Token,
                maxIterations: 1));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task YieldedOperationIsNotMisclassifiedAsIdleAtIterationBoundary()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.Enqueue(cluster.Scheduler.Yield);

        SimulationExecutionResult result = cluster.RunUntilIdle(
            TestContext.Current.CancellationToken,
            maxIterations: 1);

        Assert.Equal(SimulationExecutionReason.MaxIterationsReached, result.Reason);
        _ = cluster.RunUntilIdle(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TeardownCancellationWinsAtFinalIterationBoundary()
    {
        using var teardown = new CancellationTokenSource();
        await using var cluster = new SimulationCluster(
            seed: 1,
            DateTimeOffset.UnixEpoch,
            cancellationToken: teardown.Token);
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.Enqueue(() =>
        {
            teardown.Cancel();
        });

        SimulationExecutionResult result = cluster.RunUntilIdle(
            TestContext.Current.CancellationToken,
            maxIterations: 1);

        Assert.Equal(SimulationExecutionReason.TeardownCancellationRequested, result.Reason);
    }

    [Fact]
    public async Task MaxConsecutiveTimeAdvancesBoundaryOfZeroTripsOnTheFirstAdvance()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch) { MaxConsecutiveTimeAdvances = 0 };
        var node = cluster.AddNode("node-1");
        node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(1));

        var result = cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 100);

        Assert.Equal(SimulationExecutionReason.MaxConsecutiveTimeAdvancesExceeded, result.Reason);
        Assert.Equal(1, result.ConsecutiveTimeAdvanceCount);
    }

    [Fact]
    public async Task ToStringAndToDetailedStringAreDeterministicAcrossIdenticalRuns()
    {
        var first = await RunScriptAsync();
        var second = await RunScriptAsync();

        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(first.ToDetailedString(), second.ToDetailedString());
        Assert.Contains("Idle", first.ToString(), StringComparison.Ordinal);
        Assert.Contains("PendingWork:", first.ToDetailedString(), StringComparison.Ordinal);

        static async Task<SimulationExecutionResult> RunScriptAsync()
        {
            await using var cluster = new SimulationCluster(seed: 99, DateTimeOffset.UnixEpoch);
            var node = cluster.AddNode("node-1");
            node.Suspend();
            node.Context.SchedulerLane.Enqueue(() => { });
            return cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FormattingUsesInvariantCultureRegardlessOfCurrentThreadCulture()
    {
        // German formatting uses a comma as the decimal separator and different date ordering;
        // the invariant-culture formatting used by ToString/ToDetailedString must not be affected
        // by the current thread culture, so an identical script run under each culture must
        // produce byte-for-byte identical output.
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariantResult = await RunScriptAsync();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var germanResult = await RunScriptAsync();

            Assert.Equal(invariantResult.ToString(), germanResult.ToString());
            Assert.Equal(invariantResult.ToDetailedString(), germanResult.ToDetailedString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        static async Task<SimulationExecutionResult> RunScriptAsync()
        {
            await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
            var node = cluster.AddNode("node-1");
            node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(1.5));
            return cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        }
    }
}
