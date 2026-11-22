using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Tests.Exploration;

public sealed class ReplayTraceMinimizerTests
{
    [Fact]
    public void MinimizerShrinksKnownTraceAndPreservesFailureIdentity()
    {
        ReplayExecutionResult recorded = RecordLongFaultTrace();
        Func<ReplayArtifact, ReplayFailureObservation> predicate = ReplayFailurePredicates.ForScenario(
            ReplayCompatibilityRequirements.Current(),
            FaultScenario,
            maxSteps: 10_000,
            TestContext.Current.CancellationToken);

        ReplayMinimizationResult result = ReplayTraceMinimizer.Minimize(
            recorded.Artifact,
            new ReplayMinimizationOptions { MaxAttempts = 200 },
            predicate,
            TestContext.Current.CancellationToken);

        Assert.True(result.Verified);
        Assert.True(result.MinimizedDecisionCount < result.OriginalDecisionCount);
        Assert.Equal(1, result.MinimizedDecisionCount);
        Assert.Equal(
            recorded.Artifact.Outcome.FailureIdentity,
            result.MinimizedArtifact.Outcome.FailureIdentity);
        Assert.Contains(result.Progress, static progress => progress.Accepted);
    }

    [Fact]
    public void DivergentCandidatesAreRejectedWithoutLosingFirstDivergenceSemantics()
    {
        ReplayExecutionResult recorded = RecordLongFaultTrace();
        Func<ReplayArtifact, ReplayFailureObservation> predicate = ReplayFailurePredicates.ForScenario(
            ReplayCompatibilityRequirements.Current(),
            FaultScenario,
            maxSteps: 10_000,
            TestContext.Current.CancellationToken);

        ReplayArtifact divergent = recorded.Artifact with
        {
            Decisions =
            [
                recorded.Artifact.Decisions[0] with { SelectedResult = "999" },
            ],
        };
        ReplayFailureObservation observation = predicate(divergent);

        Assert.False(observation.Reproduced);
        Assert.StartsWith("divergence:", observation.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AttemptBoundIsDeterministic()
    {
        ReplayExecutionResult recorded = RecordLongFaultTrace();
        Func<ReplayArtifact, ReplayFailureObservation> predicate = ReplayFailurePredicates.ForScenario(
            ReplayCompatibilityRequirements.Current(),
            FaultScenario,
            maxSteps: 10_000,
            TestContext.Current.CancellationToken);

        ReplayMinimizationResult first = ReplayTraceMinimizer.Minimize(
            recorded.Artifact,
            new ReplayMinimizationOptions { MaxAttempts = 3 },
            predicate,
            TestContext.Current.CancellationToken);
        ReplayMinimizationResult second = ReplayTraceMinimizer.Minimize(
            recorded.Artifact,
            new ReplayMinimizationOptions { MaxAttempts = 3 },
            predicate,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, first.Attempts);
        Assert.Equal(
            first.Progress.Select(static progress => (progress.Action, progress.Accepted)),
            second.Progress.Select(static progress => (progress.Action, progress.Accepted)));
    }

    [Fact]
    public void MinimizerHonorsPreCanceledTokenBeforeInvokingPredicate()
    {
        ReplayExecutionResult recorded = RecordLongFaultTrace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var predicateInvoked = false;

        var exception = Assert.Throws<OperationCanceledException>(
            () => ReplayTraceMinimizer.Minimize(
                recorded.Artifact,
                new ReplayMinimizationOptions(),
                _ =>
                {
                    predicateInvoked = true;
                    return new ReplayFailureObservation { Reproduced = true };
                },
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(predicateInvoked);
    }

    private static ReplayExecutionResult RecordLongFaultTrace()
    {
        for (var scheduleSeed = 1; scheduleSeed <= 100; scheduleSeed++)
        {
            ReplayExecutionResult execution = ReplayRunner.Record(
                new ReplayRecordingOptions
                {
                    SimulationSeed = 55,
                    SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
                    ScheduleSeed = scheduleSeed,
                    MaxSteps = 10_000,
                },
                FaultScenario,
                TestContext.Current.CancellationToken);
            if (execution.Artifact.Decisions.Count >= 4)
            {
                return execution;
            }
        }

        throw new InvalidOperationException("The deterministic seed corpus did not produce a long fault trace.");
    }

    private static void FaultScenario(SimulationScheduler scheduler)
    {
        scheduler.Schedule(
            "background-one",
            () =>
            {
                for (var index = 0; index < 5; index++)
                {
                    scheduler.Yield();
                }
            });
        scheduler.Schedule(
            "background-two",
            () =>
            {
                for (var index = 0; index < 5; index++)
                {
                    scheduler.Yield();
                }
            });
        scheduler.Schedule("fault", static () => throw new KnownFailureException());
    }

    private sealed class KnownFailureException : Exception;
}
