namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationClusterRunToCompletionTests
{
    [Fact]
    public async Task FixedRunToCompletionDrivesCapturedContinuations()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var resumed = false;

        cluster.RunToCompletion(async () =>
        {
            await Task.Yield();
            resumed = true;
        }, TestContext.Current.CancellationToken);

        Assert.True(resumed);
    }

    [Fact]
    public async Task AdaptiveGenericRunToCompletionReturnsTheTaskResult()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var budget = new AdaptiveExecutionBudget(
            initialMaxIterations: 1,
            growthFactor: 2,
            maxTotalIterations: 100);

        int result = cluster.RunToCompletion(
            async () =>
            {
                await Task.Yield();
                return 42;
            },
            budget,
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunToCompletionPropagatesTheOriginalTaskFault()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);

        var exception = Assert.Throws<InvalidOperationException>(
            () => cluster.RunToCompletion(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("expected failure");
            }, TestContext.Current.CancellationToken));

        Assert.Equal("expected failure", exception.Message);
    }

    [Fact]
    public async Task AllRunToCompletionOverloadsHonorCallerCancellationBeforeStartingWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new AdaptiveExecutionBudget(maxTotalIterations: 1_000);
        var invocations = 0;

        Task RunAsync()
        {
            invocations++;
            return Task.CompletedTask;
        }

        Task<int> RunWithResultAsync()
        {
            invocations++;
            return Task.FromResult(42);
        }

        Assert.Throws<OperationCanceledException>(
            () => cluster.RunToCompletion(
                RunAsync,
                cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => cluster.RunToCompletion(
                RunAsync,
                budget,
                cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => cluster.RunToCompletion(
                RunWithResultAsync,
                cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => cluster.RunToCompletion(
                RunWithResultAsync,
                budget,
                cancellation.Token));

        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task RunToCompletionReportsCancellationInsteadOfTimeoutForIncompleteTask()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        var neverCompletes = new TaskCompletionSource();

        var exception = Assert.Throws<OperationCanceledException>(
            () => cluster.RunToCompletion(
                async () =>
                {
                    cancellation.Cancel();
                    await neverCompletes.Task;
                },
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task AdaptiveRunToCompletionExhaustionIncludesTheDetailedResult()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var completion = new TaskCompletionSource();
        var budget = new AdaptiveExecutionBudget(
            initialMaxIterations: 1,
            growthFactor: 2,
            maxTotalIterations: 3);

        var exception = Assert.Throws<TimeoutException>(
            () => cluster.RunToCompletion(() => completion.Task, budget, TestContext.Current.CancellationToken));

        Assert.Contains("Reason: Idle", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxIterations=3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixedAndAdaptiveOverloadsResolveByArgumentType()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var budget = new AdaptiveExecutionBudget(maxTotalIterations: 1_000);

        SimulationExecutionResult fixedUntil = cluster.RunUntil(() => true, TestContext.Current.CancellationToken, 1);
        SimulationExecutionResult adaptiveUntil = cluster.RunUntil(() => true, budget, TestContext.Current.CancellationToken);
        SimulationExecutionResult fixedIdle = cluster.RunUntilIdle(TestContext.Current.CancellationToken, maxIterations: 1);
        SimulationExecutionResult adaptiveIdle = cluster.RunUntilIdle(budget, TestContext.Current.CancellationToken);
        cluster.RunToCompletion(() => Task.CompletedTask, TestContext.Current.CancellationToken, 1);
        cluster.RunToCompletion(() => Task.CompletedTask, budget, TestContext.Current.CancellationToken);
        int fixedValue = cluster.RunToCompletion(() => Task.FromResult(1), TestContext.Current.CancellationToken, 1);
        int adaptiveValue = cluster.RunToCompletion(() => Task.FromResult(2), budget, TestContext.Current.CancellationToken);

        Assert.Equal(SimulationExecutionReason.ConditionMet, fixedUntil.Reason);
        Assert.Equal(SimulationExecutionReason.ConditionMet, adaptiveUntil.Reason);
        Assert.Equal(SimulationExecutionReason.Idle, fixedIdle.Reason);
        Assert.Equal(SimulationExecutionReason.Idle, adaptiveIdle.Reason);
        Assert.Equal(1, fixedValue);
        Assert.Equal(2, adaptiveValue);
    }

}
