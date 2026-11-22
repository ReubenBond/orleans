using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;

namespace Orleans.Testing.Simulation.Runtime.Tests.Replay;

public sealed class ClusterReplayRunnerTests
{
    [Fact]
    public void RecordAndReplayClusterScenarioExactly()
    {
        var recordedTrace = new List<string>();
        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 91),
            cluster => ConfigureScenario(cluster, recordedTrace),
            TestContext.Current.CancellationToken);
        var replayedTrace = new List<string>();

        ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
            recorded.Artifact,
            ReplayCompatibilityRequirements.Current(),
            cluster => ConfigureScenario(cluster, replayedTrace),
            maxSteps: 10_000,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cluster", recorded.Artifact.Scheduler.Options["executionHost"]);
        Assert.Equal(ReplayTerminationKind.Completed, recorded.Artifact.Outcome.Kind);
        Assert.NotEmpty(recorded.Artifact.Decisions);
        Assert.Equal(recordedTrace, replayedTrace);
        Assert.Equal(DateTimeOffset.UnixEpoch.ToString("O"), recordedTrace[0]);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void ClusterOperationFailureIsCapturedAndReplayed()
    {
        static void Scenario(SimulationCluster cluster)
        {
            SimulationNode node = cluster.AddNode("faulting-node");
            node.Context.SchedulerLane.Enqueue(
                static () => throw new KnownClusterReplayException());
        }

        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 3),
            Scenario,
            TestContext.Current.CancellationToken);
        ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
            recorded.Artifact,
            ReplayCompatibilityRequirements.Current(),
            Scenario,
            maxSteps: 10_000,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReplayTerminationKind.Faulted, recorded.Artifact.Outcome.Kind);
        Assert.Equal(
            typeof(KnownClusterReplayException).FullName,
            recorded.Artifact.Outcome.FailureIdentity);
        Assert.Equal(1, recorded.Steps);
        Assert.Equal(1, replayed.Steps);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void ReplayRejectsMismatchedExecutionHost()
    {
        ReplayExecutionResult schedulerArtifact = ReplayRunner.Record(
            SeededConfiguration(scheduleSeed: 5),
            static scheduler => scheduler.Schedule("complete", static () => { }),
            TestContext.Current.CancellationToken);
        ReplayExecutionResult clusterArtifact = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 5),
            static cluster => cluster.SchedulerLane.Enqueue(static () => { }),
            TestContext.Current.CancellationToken);

        ReplayCompatibilityException schedulerAsCluster = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.ReplayCluster(
                schedulerArtifact.Artifact,
                ReplayCompatibilityRequirements.Current(),
                static _ => { },
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken));
        ReplayCompatibilityException clusterAsScheduler = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.Replay(
                clusterArtifact.Artifact,
                ReplayCompatibilityRequirements.Current(),
                static _ => { },
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("execution host mismatch", schedulerAsCluster.Message, StringComparison.Ordinal);
        Assert.Contains("execution host mismatch", clusterAsScheduler.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayRejectsUnknownExecutionHost()
    {
        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 7),
            static _ => { },
            TestContext.Current.CancellationToken);
        ReplayArtifact artifact = recorded.Artifact with
        {
            Scheduler = recorded.Artifact.Scheduler with
            {
                Options = new SortedDictionary<string, string>(
                    recorded.Artifact.Scheduler.Options.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal))
                {
                    ["executionHost"] = "unknown",
                },
            },
        };

        ReplayCompatibilityException exception = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.Replay(
                artifact,
                ReplayCompatibilityRequirements.Current(),
                static _ => { },
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported replay execution host", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactRecordsTheDefaultProcessorCount()
    {
        int observed = 0;
        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 11),
            cluster => observed = cluster.RuntimeEnvironment.GetProcessorCount(node: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, observed);
        Assert.Equal("4", recorded.Artifact.Scheduler.Options["processorCount"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplayRunsWithTheRecordedProcessorCount(bool cluster)
    {
        ReplayRecordingOptions options = SeededConfiguration(scheduleSeed: 13) with { ProcessorCount = 6 };
        int recordedCount = 0;
        int replayedCount = 0;
        ReplayExecutionResult recorded = cluster
            ? ReplayRunner.RecordCluster(options, c => recordedCount = c.ProcessorCount, TestContext.Current.CancellationToken)
            : ReplayRunner.Record(options, s => recordedCount = s.Runtime.Environment.GetProcessorCount(null), TestContext.Current.CancellationToken);

        ReplayExecutionResult replayed = cluster
            ? ReplayRunner.ReplayCluster(
                recorded.Artifact,
                ReplayCompatibilityRequirements.Current() with { ProcessorCount = 6 },
                c => replayedCount = c.RuntimeEnvironment.GetProcessorCount(null),
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken)
            : ReplayRunner.Replay(
                recorded.Artifact,
                ReplayCompatibilityRequirements.Current(),
                s => replayedCount = s.Runtime.Environment.GetProcessorCount(null),
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("6", recorded.Artifact.Scheduler.Options["processorCount"]);
        Assert.Equal(6, recordedCount);
        Assert.Equal(6, replayedCount);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void ReplayRejectsAProcessorCountOtherThanTheRequiredOne()
    {
        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 17) with { ProcessorCount = 6 },
            static _ => { },
            TestContext.Current.CancellationToken);
        bool ran = false;

        ReplayCompatibilityException exception = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.ReplayCluster(
                recorded.Artifact,
                ReplayCompatibilityRequirements.Current() with { ProcessorCount = 4 },
                _ => ran = true,
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(ran);
        Assert.Contains("simulated processor count mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("four")]
    public void ReplayRejectsAnArtifactWithoutAValidProcessorCount(string? processorCount)
    {
        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            SeededConfiguration(scheduleSeed: 19),
            static _ => { },
            TestContext.Current.CancellationToken);
        var options = new SortedDictionary<string, string>(
            recorded.Artifact.Scheduler.Options.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        options.Remove("processorCount");
        if (processorCount is not null)
        {
            options["processorCount"] = processorCount;
        }

        ReplayArtifact artifact = recorded.Artifact with
        {
            Scheduler = recorded.Artifact.Scheduler with { Options = options },
        };
        bool ran = false;

        ReplayCompatibilityException exception = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.ReplayCluster(
                artifact,
                ReplayCompatibilityRequirements.Current(),
                _ => ran = true,
                maxSteps: 10_000,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(ran);
        Assert.Contains("processor count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploreClusterCreatesAndDisposesFreshClusterPerIteration()
    {
        var runtimeIds = new List<Guid>();
        var disposalCount = new DisposalCount();

        ScheduleExplorationResult result = ScheduleExplorer.ExploreCluster(
            new ScheduleExplorationOptions
            {
                SimulationSeed = 777,
                FirstScheduleSeed = 100,
                MaxIterations = 4,
                MaxStepsPerIteration = 10_000,
                MaxFailures = 1,
            },
            cluster =>
            {
                runtimeIds.Add(cluster.RuntimeIdentity.Id);
                _ = cluster.AddCustomNode(
                    "node",
                    context => new TrackingDisposalNode("node", context, disposalCount));
                cluster.SchedulerLane.Enqueue(static () => { });
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ExplorationTerminationReason.IterationLimit, result.TerminationReason);
        Assert.Equal(4, result.Iterations.Count);
        Assert.Equal(4, runtimeIds.Distinct().Count());
        Assert.Equal(4, disposalCount.Value);
        Assert.All(
            result.Iterations,
            static iteration => Assert.Equal(
                "cluster",
                iteration.Execution.Artifact.Scheduler.Options["executionHost"]));
    }

    private static ReplayRecordingOptions SeededConfiguration(int scheduleSeed) => new()
    {
        SimulationSeed = 1234,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = scheduleSeed,
        MaxSteps = 10_000,
    };

    private static void ConfigureScenario(SimulationCluster cluster, List<string> trace)
    {
        trace.Add(cluster.StartDateTime.ToString("O"));
        SimulationNode first = cluster.AddNode("first");
        SimulationNode second = cluster.AddNode("second");
        _ = cluster.AddCustomNode(
            "disposal",
            context => new AwaitingDisposalNode("disposal", context, trace));
        first.Context.SchedulerLane.Enqueue(
            () =>
            {
                trace.Add("first:1");
                cluster.Scheduler.Yield();
                trace.Add("first:2");
            });
        second.Context.SchedulerLane.Enqueue(
            () =>
            {
                trace.Add("second:1");
                cluster.Scheduler.Yield();
                trace.Add("second:2");
            });
        cluster.SchedulerLane.EnqueueAfter(
            () => trace.Add(cluster.TimeProvider.GetUtcNow().ToString("O")),
            TimeSpan.FromSeconds(5));
    }

    private sealed class KnownClusterReplayException : Exception;

    private sealed class DisposalCount
    {
        public int Value { get; set; }
    }

    private sealed class TrackingDisposalNode(
        string address,
        SimulationNodeContext context,
        DisposalCount count) : SimulationNode, IDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public void Dispose() => count.Value++;
    }

    private sealed class AwaitingDisposalNode(
        string address,
        SimulationNodeContext context,
        List<string> trace) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public async ValueTask DisposeAsync()
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Context.SchedulerLane.Enqueue(
                () =>
                {
                    trace.Add("dispose:first");
                    first.SetResult();
                });
            Context.SchedulerLane.Enqueue(
                () =>
                {
                    trace.Add("dispose:second");
                    second.SetResult();
                });
            await first.Task;
            await second.Task;
        }
    }
}
