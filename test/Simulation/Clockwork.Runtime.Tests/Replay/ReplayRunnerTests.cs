using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Replay;

public sealed class ReplayRunnerTests
{
    [Fact]
    public void RecordAndReplaySuccessfulSeededSchedule()
    {
        var recordedOrder = new List<string>();
        ReplayRecordingOptions configuration = SeededConfiguration(scheduleSeed: 91);

        ReplayExecutionResult recorded = Record(
            configuration,
            scheduler => ScheduleYieldingPair(scheduler, recordedOrder));
        var replayedOrder = new List<string>();
        ReplayExecutionResult replayed = Replay(
            recorded.Artifact,
            scheduler => ScheduleYieldingPair(scheduler, replayedOrder));

        Assert.Equal(ReplayTerminationKind.Completed, recorded.Artifact.Outcome.Kind);
        Assert.NotEmpty(recorded.Artifact.Decisions);
        Assert.True(replayed.Reproduced);
        Assert.Equal(recordedOrder, replayedOrder);
    }

    [Fact]
    public void RecordAndReplayFaultByStableExceptionIdentity()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(3),
            static scheduler => scheduler.Schedule("fault", static () => throw new KnownReplayException()));

        ReplayExecutionResult replayed = Replay(
            recorded.Artifact,
            static scheduler => scheduler.Schedule("fault", static () => throw new KnownReplayException()));

        Assert.Equal(ReplayTerminationKind.Faulted, recorded.Artifact.Outcome.Kind);
        Assert.Equal(typeof(KnownReplayException).FullName, recorded.Artifact.Outcome.FailureIdentity);
        Assert.Null(recorded.Artifact.Outcome.Diagnostic);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void CanceledOperationIsADiagnosticAndLeavesRunCompleted()
    {
        static void Scenario(SimulationScheduler scheduler)
        {
            SimulationOperation operation = scheduler.Schedule("cancel", static () => { });
            scheduler.Cancel(operation);
        }

        ReplayExecutionResult recorded = Record(SeededConfiguration(4), Scenario);
        ReplayExecutionResult replayed = Replay(
            recorded.Artifact,
            Scenario);

        Assert.Equal(ReplayTerminationKind.Completed, recorded.Artifact.Outcome.Kind);
        Assert.Null(recorded.Artifact.Outcome.FailureIdentity);
        Assert.Equal(1, Assert.Single(recorded.Artifact.Diagnostics.CanceledOperations).Id);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void RecordAndReplayVirtualTimeout()
    {
        static void Scenario(SimulationScheduler scheduler)
        {
            SimulationResource resource = scheduler.CreateResource(SimulationResourceKind.Timer, "timeout");
            scheduler.Schedule(
                "waiter",
                () =>
                {
                    SimulationWaitOutcome outcome = scheduler.WaitOnResource(
                        resource,
                        TimeSpan.FromSeconds(5),
                        SimulationPauseReason.ResourceWait("timeout"));
                    Assert.Equal(SimulationWaitOutcome.TimedOut, outcome);
                });
        }

        ReplayExecutionResult recorded = Record(SeededConfiguration(5), Scenario);
        ReplayExecutionResult replayed = Replay(
            recorded.Artifact,
            Scenario);

        Assert.Contains(
            recorded.Artifact.Decisions,
            static decision => decision.SourceId == "resource-wait-resolution" &&
                decision.SelectedResult == nameof(SimulationWaitOutcome.TimedOut));
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void RecordAndReplayDeadlockAtTerminalBoundary()
    {
        static void Scenario(SimulationScheduler scheduler)
        {
            SimulationResource first = scheduler.CreateResource(SimulationResourceKind.Monitor, "first");
            SimulationResource second = scheduler.CreateResource(SimulationResourceKind.Monitor, "second");
            scheduler.Schedule(
                "one",
                () =>
                {
                    scheduler.MarkResourceOwner(first, scheduler.CurrentOperation);
                    scheduler.Yield();
                    scheduler.WaitOnResource(second, SimulationPauseReason.ResourceWait("second"));
                });
            scheduler.Schedule(
                "two",
                () =>
                {
                    scheduler.MarkResourceOwner(second, scheduler.CurrentOperation);
                    scheduler.Yield();
                    scheduler.WaitOnResource(first, SimulationPauseReason.ResourceWait("first"));
                });
        }

        ReplayRecordingOptions configuration = new()
        {
            SimulationSeed = 7,
            SchedulingPolicy = ReplaySchedulingPolicy.RoundRobin,
        };
        ReplayExecutionResult recorded = Record(configuration, Scenario);
        ReplayExecutionResult replayed = Replay(
            recorded.Artifact,
            Scenario);

        Assert.Equal(ReplayTerminationKind.Deadlocked, recorded.Artifact.Outcome.Kind);
        Assert.StartsWith("deadlock:", recorded.Artifact.Outcome.FailureIdentity, StringComparison.Ordinal);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void SeededSchedulingRandomizesResourceWinnersAndReplaysThem()
    {
        var recordedWinners = new List<int>();
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(29),
            scheduler => ScheduleResourceContest(scheduler, recordedWinners));
        var replayedWinners = new List<int>();
        ReplayExecutionResult replayed = Replay(
            recorded.Artifact,
            scheduler => ScheduleResourceContest(scheduler, replayedWinners));

        Assert.Contains(
            recorded.Artifact.Decisions,
            static decision => decision.Kind == SimulationDecisionKind.ResourceWinner);
        Assert.Equal(recordedWinners, replayedWinners);
        Assert.True(replayed.Reproduced);
    }

    [Fact]
    public void TruncatedDecisionStreamFailsAtFirstMissingChoice()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(31),
            static scheduler => ScheduleYieldingPair(scheduler, []));
        ReplayDecision[] truncated = recorded.Artifact.Decisions.SkipLast(1).ToArray();
        ReplayArtifact artifact = recorded.Artifact with { Decisions = truncated };

        SimulationDecisionReplayMismatchException exception = Assert.Throws<SimulationDecisionReplayMismatchException>(
            () => Replay(
                artifact,
                static scheduler => ScheduleYieldingPair(scheduler, [])));

        Assert.Contains("exhausted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SurplusDecisionStreamFailsAtTerminalBoundary()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(37),
            static scheduler => ScheduleYieldingPair(scheduler, []));
        ReplayDecision extra = recorded.Artifact.Decisions[^1] with
        {
            Sequence = recorded.Artifact.Decisions.Count,
        };
        ReplayArtifact artifact = recorded.Artifact with
        {
            Decisions = [.. recorded.Artifact.Decisions, extra],
        };

        SimulationDecisionReplayMismatchException exception = Assert.Throws<SimulationDecisionReplayMismatchException>(
            () => Replay(
                artifact,
                static scheduler => ScheduleYieldingPair(scheduler, [])));

        Assert.Contains("remain unconsumed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentScenarioFailsWithExpectedAndActualDecisionContext()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(41),
            static scheduler => ScheduleYieldingPair(scheduler, []));

        SimulationDecisionReplayMismatchException exception = Assert.Throws<SimulationDecisionReplayMismatchException>(
            () => Replay(
                recorded.Artifact,
                static scheduler =>
                {
                    scheduler.Schedule("different-a", scheduler.Yield);
                    scheduler.Schedule("different-b", static () => { });
                    scheduler.Schedule("different-c", static () => { });
                }));

        Assert.True(exception.Expected is not null || exception.Message.Contains("recorded", StringComparison.Ordinal));
        Assert.Contains("Replay diverged", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalEntryFailureProducesAbortedOutcome()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(43),
            static _ => throw new InvalidOperationException("external entry rejected"));

        Assert.Equal(ReplayTerminationKind.Aborted, recorded.Artifact.Outcome.Kind);
        Assert.Equal(typeof(InvalidOperationException).FullName, recorded.Artifact.Outcome.FailureIdentity);
        Assert.Throws<ReplayCompatibilityException>(
            () => Replay(
                recorded.Artifact,
                static _ => { }));
    }

    [Fact]
    public void ExternalFailureAfterObservedControlledFaultRemainsAborted()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(47),
            static scheduler =>
            {
                scheduler.Schedule(
                    "controlled-fault",
                    static () => throw new KnownReplayException());
                Assert.True(scheduler.RunStep(CancellationToken.None));
                throw new ExternalReplayException();
            });

        Assert.Equal(ReplayTerminationKind.Aborted, recorded.Artifact.Outcome.Kind);
        Assert.Equal(typeof(ExternalReplayException).FullName, recorded.Artifact.Outcome.FailureIdentity);
        Assert.IsType<ExternalReplayException>(recorded.ExecutionException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ControlledApiOffTheSimulationThreadAbortsTheRun(bool fromOperation)
    {
        // Without an operation, nothing is runnable when the runner first steps.
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(53),
            scheduler =>
            {
                if (fromOperation)
                {
                    scheduler.Schedule("escape", () => CallControlledApiOnAnotherThread(scheduler.Runtime));
                }
                else
                {
                    CallControlledApiOnAnotherThread(scheduler.Runtime);
                }
            });

        Assert.Equal(ReplayTerminationKind.Aborted, recorded.Artifact.Outcome.Kind);
        Assert.Equal(typeof(SimulationThreadAffinityException).FullName, recorded.Artifact.Outcome.FailureIdentity);
        Assert.Equal(
            "System.DateTime.UtcNow",
            Assert.IsType<SimulationThreadAffinityException>(recorded.ExecutionException).ApiName);
    }

    // The thread carries the simulation's ambient context off its logical thread, and swallows the rejection.
    private static void CallControlledApiOnAnotherThread(SimulationRuntimeIdentity runtime)
    {
        var escaped = new Thread(() =>
        {
            using (SimulationExecutionContext.EnterRuntime(runtime))
            {
                _ = Xunit.Record.Exception(() => ControlledDateTime.GetUtcNow());
            }
        });
        escaped.Start();
        Assert.True(escaped.Join(TimeSpan.FromSeconds(30)), "The escaped thread did not finish.");
    }

    private static ReplayRecordingOptions SeededConfiguration(int scheduleSeed) => new()
    {
        SimulationSeed = 1234,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = scheduleSeed,
        MaxSteps = 10_000,
    };

    [Fact]
    public void ReplayPropagatesOrchestrationCancellationInsteadOfOutcomeMismatch()
    {
        ReplayExecutionResult recorded = Record(
            SeededConfiguration(scheduleSeed: 1),
            scheduler => scheduler.Schedule("complete", static () => { }));
        using var cancellation = new CancellationTokenSource();

        var exception = Assert.Throws<OperationCanceledException>(
            () => ReplayRunner.Replay(
                recorded.Artifact,
                ReplayCompatibilityRequirements.Current(),
                scheduler => scheduler.Schedule("complete", cancellation.Cancel),
                maxSteps: 1_000_000,
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void RecordPropagatesOrchestrationCancellationAtQuiescence()
    {
        using var cancellation = new CancellationTokenSource();

        var exception = Assert.Throws<OperationCanceledException>(
            () => ReplayRunner.Record(
                SeededConfiguration(scheduleSeed: 1),
                scheduler => scheduler.Schedule("complete", cancellation.Cancel),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static ReplayExecutionResult Record(
        ReplayRecordingOptions configuration,
        Action<SimulationScheduler> scenario) =>
        ReplayRunner.Record(configuration, scenario, TestContext.Current.CancellationToken);

    private static ReplayExecutionResult Replay(
        ReplayArtifact artifact,
        Action<SimulationScheduler> scenario) =>
        ReplayRunner.Replay(
            artifact,
            ReplayCompatibilityRequirements.Current(),
            scenario,
            maxSteps: 1_000_000,
            cancellationToken: TestContext.Current.CancellationToken);

    private static void ScheduleYieldingPair(
        SimulationScheduler scheduler,
        List<string> order)
    {
        scheduler.Schedule(
            "first",
            () =>
            {
                order.Add("first:1");
                scheduler.Yield();
                order.Add("first:2");
            });
        scheduler.Schedule(
            "second",
            () =>
            {
                order.Add("second:1");
                scheduler.Yield();
                order.Add("second:2");
            });
    }

    private static void ScheduleResourceContest(
        SimulationScheduler scheduler,
        List<int> winners)
    {
        SimulationResource resource = scheduler.CreateResource(SimulationResourceKind.Semaphore, "contest");
        for (var id = 1; id <= 3; id++)
        {
            int captured = id;
            scheduler.Schedule(
                $"waiter-{captured}",
                () =>
                {
                    scheduler.WaitOnResource(
                        resource,
                        SimulationPauseReason.ResourceWait("contest"));
                    winners.Add(captured);
                });
        }

        scheduler.Schedule(
            "signaler",
            () =>
            {
                while (resource.WaiterCount < 3)
                {
                    scheduler.Yield();
                }

                scheduler.SignalOne(resource);
                scheduler.SignalOne(resource);
                scheduler.SignalOne(resource);
            });
    }

    private sealed class KnownReplayException : Exception;

    private sealed class ExternalReplayException : Exception;
}
