using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Exploration;

public sealed class ScheduleExplorerTests
{
    [Fact]
    public void IterationIdsAndSeedsAreDeterministic()
    {
        ScheduleExplorationResult first = Explore(ExploreOptions(maxIterations: 4, maxFailures: 1), NoOpScenario);
        ScheduleExplorationResult second = Explore(ExploreOptions(maxIterations: 4, maxFailures: 1), NoOpScenario);

        Assert.Equal(
            first.Iterations.Select(static iteration => (iteration.IterationId, iteration.ScheduleSeed)),
            second.Iterations.Select(static iteration => (iteration.IterationId, iteration.ScheduleSeed)));
        Assert.Equal([100, 101, 102, 103], first.Iterations.Select(static iteration => iteration.ScheduleSeed));
        Assert.All(first.Iterations, static iteration => Assert.Equal(777, iteration.Execution.Artifact.SimulationSeed));
    }

    [Fact]
    public void ExplorationFindsRaceWithinFixedBoundAndReplaysExactly()
    {
        ScheduleExplorationResult result = Explore(
            ExploreOptions(maxIterations: 8, maxFailures: 1),
            RaceScenario);

        ScheduleExplorationIteration failure = Assert.Single(
            result.Iterations,
            static iteration => iteration.IsFailure);
        Assert.Equal(ExplorationTerminationReason.FailureLimit, result.TerminationReason);
        Assert.Equal(ReplayTerminationKind.RaceDetected, failure.Execution.Artifact.Outcome.Kind);
        ReplayExecutionResult replay = ReplayRunner.Replay(
            failure.Execution.Artifact,
            ReplayCompatibilityRequirements.Current(),
            RaceScenario,
            maxSteps: 1_000_000,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replay.Reproduced);
    }

    [Fact]
    public void ExplorationFindsDeadlockWithinFixedBoundAndReplaysExactly()
    {
        ScheduleExplorationResult result = Explore(
            ExploreOptions(maxIterations: 32, maxFailures: 1),
            ConditionalDeadlockScenario);

        ScheduleExplorationIteration failure = Assert.Single(
            result.Iterations,
            static iteration => iteration.IsFailure);
        Assert.Equal(ReplayTerminationKind.Deadlocked, failure.Execution.Artifact.Outcome.Kind);
        ReplayExecutionResult replay = ReplayRunner.Replay(
            failure.Execution.Artifact,
            ReplayCompatibilityRequirements.Current(),
            ConditionalDeadlockScenario,
            maxSteps: 1_000_000,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replay.Reproduced);
    }

    [Fact]
    public void FailureLimitAndAggregateCountsAreStable()
    {
        ScheduleExplorationOptions configuration = ExploreOptions(maxIterations: 20, maxFailures: 3);

        ScheduleExplorationResult result = Explore(configuration, RaceScenario);

        Assert.Equal(ExplorationTerminationReason.FailureLimit, result.TerminationReason);
        Assert.Equal(3, result.FailureCount);
        Assert.Equal(3, result.OutcomeCounts[ReplayTerminationKind.RaceDetected]);
        Assert.Single(result.RetainedFailures);
    }

    [Fact]
    public void MidIterationCancellationTerminatesWithoutRecordingFailure()
    {
        using var cancellation = new CancellationTokenSource();

        ScheduleExplorationResult result = ScheduleExplorer.Explore(
            ExploreOptions(maxIterations: 20, maxFailures: 1),
            scheduler => scheduler.Schedule("cancel", cancellation.Cancel),
            cancellation.Token);

        Assert.Equal(ExplorationTerminationReason.Canceled, result.TerminationReason);
        Assert.Empty(result.Iterations);
        Assert.Equal(0, result.FailureCount);
        Assert.Empty(result.RetainedFailures);
    }

    private static ScheduleExplorationResult Explore(
        ScheduleExplorationOptions options,
        Action<SimulationScheduler> scenario) =>
        ScheduleExplorer.Explore(options, scenario, TestContext.Current.CancellationToken);

    private static ScheduleExplorationOptions ExploreOptions(
        int maxIterations,
        int maxFailures) => new()
        {
            SimulationSeed = 777,
            FirstScheduleSeed = 100,
            MaxIterations = maxIterations,
            MaxStepsPerIteration = 10_000,
            MaxFailures = maxFailures,
        };

    private static void NoOpScenario(SimulationScheduler scheduler) =>
        scheduler.Schedule("complete", static () => { });

    private static void RaceScenario(SimulationScheduler scheduler)
    {
        var target = new object();
        scheduler.Schedule(
            "writer-one",
            () => RaceInstrumentation.WriteInstance(
                target,
                "Counter.Value",
                "Scenario::WriterOne",
                1,
                sourceFile: null,
                sourceLine: -1));
        scheduler.Schedule(
            "writer-two",
            () => RaceInstrumentation.WriteInstance(
                target,
                "Counter.Value",
                "Scenario::WriterTwo",
                2,
                sourceFile: null,
                sourceLine: -1));
    }

    private static void ConditionalDeadlockScenario(SimulationScheduler scheduler)
    {
        SimulationResource first = scheduler.CreateResource(SimulationResourceKind.Monitor, "first");
        SimulationResource second = scheduler.CreateResource(SimulationResourceKind.Monitor, "second");
        scheduler.Schedule(
            "one",
            () =>
            {
                scheduler.MarkResourceOwner(first, scheduler.CurrentOperation);
                scheduler.Yield();
                if (second.Owner is not null)
                {
                    scheduler.WaitOnResource(second, SimulationPauseReason.ResourceWait("second"));
                }
            });
        scheduler.Schedule(
            "two",
            () =>
            {
                scheduler.MarkResourceOwner(second, scheduler.CurrentOperation);
                scheduler.Yield();
                if (first.Owner is not null)
                {
                    scheduler.WaitOnResource(first, SimulationPauseReason.ResourceWait("first"));
                }
            });
    }
}
