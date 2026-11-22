using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;
using Orleans.Testing.Simulation.Shims.System;

namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationClusterTests
{
    [Fact]
    public async Task ClusterRunsScheduledWorkDeterministically()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var executed = false;

        node.Context.SchedulerLane.EnqueueAfter(() => executed = true, TimeSpan.FromSeconds(5));

        Assert.Equal(SimulationExecutionReason.ConditionMet, cluster.RunUntil(() => executed, TestContext.Current.CancellationToken).Reason);
        Assert.Equal(cluster.StartDateTime + TimeSpan.FromSeconds(5), cluster.TimeProvider.GetUtcNow());
    }

    [Fact]
    public async Task SuspendedNodeResumesAfterSimulatedDuration()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");

        node.SuspendFor(TimeSpan.FromSeconds(5));

        Assert.True(node.IsSuspended);
        Assert.Equal(SimulationExecutionReason.ConditionMet, cluster.RunUntil(() => !node.IsSuspended, TestContext.Current.CancellationToken).Reason);
    }

    [Fact]
    public async Task NetworkPartitionsAreDirectionalAndHealable()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");
        _ = cluster.AddNode("node-2");

        cluster.Network.CreatePartition("node-1", "node-2");

        Assert.Equal(DeliveryStatus.Partitioned, cluster.Network.CheckDelivery("node-1", "node-2"));
        Assert.Equal(DeliveryStatus.Success, cluster.Network.CheckDelivery("node-2", "node-1"));

        cluster.Network.HealPartition("node-1", "node-2");
        Assert.True(cluster.Network.CanDeliver("node-1", "node-2"));
    }

    [Fact]
    public async Task RunUntilReportsIdleWhenThereIsNoPendingWork()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        _ = cluster.AddNode("node-1");

        Assert.Equal(SimulationExecutionReason.Idle, cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 100).Reason);
    }

    [Fact]
    public async Task RunUntilReportsWhenTheNextDueTimeExceedsMaxSimulatedTimeAdvance()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch)
        {
            MaxSimulatedTimeAdvance = TimeSpan.FromSeconds(1),
        };
        var node = cluster.AddNode("node-1");

        // Schedule work far beyond the stuck-detection threshold; the condition never becomes true.
        node.Context.SchedulerLane.EnqueueAfter(() => { }, TimeSpan.FromSeconds(10));

        Assert.Equal(
            SimulationExecutionReason.MaxSimulatedTimeAdvanceExceeded,
            cluster.RunUntil(() => false, TestContext.Current.CancellationToken, maxIterations: 100).Reason);
        cluster.MaxSimulatedTimeAdvance = TimeSpan.FromMinutes(10);
    }

    [Fact]
    public async Task RunUntilIdleReportsTheNumberOfTasksExecutedBeforeGoingIdle()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        var node = cluster.AddNode("node-1");
        var executedCount = 0;

        for (var i = 0; i < 3; i++)
        {
            node.Context.SchedulerLane.Enqueue(() => executedCount++);
        }

        Assert.Equal(3, cluster.RunUntilIdle(TestContext.Current.CancellationToken).StepsExecuted);
        Assert.Equal(3, executedCount);
    }

    [Fact]
    public async Task RunToCompletionOverloadsExecuteTaskFactoriesWithinActiveSimulation()
    {
        await using var cluster = new SimulationCluster(seed: 12345, DateTimeOffset.UnixEpoch);
        var budget = new AdaptiveExecutionBudget(maxTotalIterations: 1_000);

        cluster.RunToCompletion(() =>
        {
            _ = new ControlledLock();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        cluster.RunToCompletion(
            () =>
            {
                _ = new ControlledLock();
                return Task.CompletedTask;
            },
            budget, TestContext.Current.CancellationToken);
        Assert.True(cluster.RunToCompletion(() =>
        {
            _ = new ControlledLock();
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken));
        Assert.True(cluster.RunToCompletion(
            () =>
            {
                _ = new ControlledLock();
                return Task.FromResult(true);
            },
            budget, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForkRandomIsReproducibleAcrossClustersWithTheSameSeed()
    {
        await using var first = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch);
        await using var second = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch);

        var firstDerived = first.ForkRandom();
        var secondDerived = second.ForkRandom();

        Assert.Equal(firstDerived.Next(), secondDerived.Next());
        Assert.Equal(firstDerived.NextGuid(), secondDerived.NextGuid());

        // The parent stream must also stay in sync after deriving a child stream.
        Assert.Equal(first.Random.Next(), second.Random.Next());
    }

    [Fact]
    public async Task DisposeFailureStillReleasesInfrastructureAndSecondDisposeIsANoOp()
    {
        var cluster = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch);
        var node = cluster.AddCustomNode(
            "node",
            context => new FailingDisposeNode("node", context));
        var runtime = cluster.RuntimeIdentity;
        var teardownToken = cluster.TeardownCancellationToken;

        var exception = await Assert.ThrowsAsync<AggregateException>(() => cluster.DisposeAsync().AsTask());

        var failure = Assert.Single(exception.InnerExceptions);
        Assert.Equal("cluster cleanup failed", failure.Message);
        Assert.Equal(1, node.DisposeCallCount);
        Assert.True(teardownToken.IsCancellationRequested);
        Assert.False(SimulationExecutionContext.IsActive);

        await cluster.DisposeAsync();
        Assert.Equal(1, node.DisposeCallCount);
    }

    [Fact]
    public async Task TeardownCancellationRunsRegisteredCallbacksOnTheSimulationThread()
    {
        var cluster = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch);
        DateTime? observed = null;
        cluster.RunToCompletion(
            () =>
            {
                // Registered from simulation work, the callback captures the simulation's ambient context, so the
                // controlled clock it reads requires the simulation's logical thread when disposal cancels the token.
                _ = cluster.TeardownCancellationToken.Register(() => observed = ControlledDateTime.GetUtcNow());
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await cluster.DisposeAsync();

        Assert.Equal(DateTime.UnixEpoch, observed);
    }

    [Fact]
    public async Task CallerCancellationCancelsTheTeardownTokenOnTheSimulationThread()
    {
        using var caller = new CancellationTokenSource();
        var cluster = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch, cancellationToken: caller.Token);
        DateTime? observed = null;
        cluster.RunToCompletion(
            () =>
            {
                _ = cluster.TeardownCancellationToken.Register(() => observed = ControlledDateTime.GetUtcNow());
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        // Cancelled on another thread, as a test framework's timeout token is: the next drive step cancels the
        // teardown token, on the simulation's logical thread.
        await Task.Run(caller.Cancel, TestContext.Current.CancellationToken);
        Assert.False(cluster.TeardownCancellationToken.IsCancellationRequested);

        Assert.Equal(
            SimulationExecutionReason.TeardownCancellationRequested,
            cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
        Assert.Equal(DateTime.UnixEpoch, observed);
        await cluster.DisposeAsync();
    }

    [Fact]
    public async Task TeardownCallbackFailureFromCallerCancellationPropagatesFromTheDrive()
    {
        using var caller = new CancellationTokenSource();
        var cluster = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch, cancellationToken: caller.Token);
        cluster.RunToCompletion(
            () =>
            {
                _ = cluster.TeardownCancellationToken.Register(() => throw new InvalidOperationException("teardown failed"));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await caller.CancelAsync();

        var failure = Assert.Throws<AggregateException>(() => cluster.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.Equal("teardown failed", Assert.Single(failure.InnerExceptions).Message);
        await cluster.DisposeAsync();
    }

    [Fact]
    public async Task DisposalSurfacesAThreadAffinityViolationNoStepRethrew()
    {
        var cluster = new SimulationCluster(seed: 42, DateTimeOffset.UnixEpoch);
        var escaped = new Thread(() =>
        {
            using (SimulationExecutionContext.EnterRuntime(cluster.RuntimeIdentity))
            {
                _ = Record.Exception(() => ControlledDateTime.GetUtcNow());
            }
        });
        escaped.Start();
        Assert.True(escaped.Join(TimeSpan.FromSeconds(30)), "The escaped thread did not finish.");

        var exception = await Assert.ThrowsAsync<AggregateException>(() => cluster.DisposeAsync().AsTask());

        Assert.Equal(
            "System.DateTime.UtcNow",
            Assert.IsType<SimulationThreadAffinityException>(Assert.Single(exception.InnerExceptions)).ApiName);
    }

    private sealed class FailingDisposeNode(
        string address,
        SimulationNodeContext context) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public int DisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            throw new InvalidOperationException("cluster cleanup failed");
        }
    }
}
