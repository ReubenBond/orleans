using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Exploration;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Replay;

/// <summary>
/// The run outcome comes from races, unhandled faults, deadlocks, and bounds, and otherwise from the
/// entry task's own terminal state. Canceled operations are diagnostics, never the outcome
/// (ReubenBond/Clockwork#44). Every case records a cluster run and replays its persisted bytes.
/// </summary>
public sealed class ReplayOutcomeClassificationTests
{
    private const string ContinuationDescription = "Simulation continuation";
    private const string LaneCallbackDescription = "Simulation lane callback";
    private const string EntryOption = "entry";
    private const string TaskEntry = "task";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimedOutWaitAsyncInEntryTaskLeavesRunCompleted(bool generic)
    {
        CancellationToken neverCanceled = TestContext.Current.CancellationToken;

        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            // The reproduction from ReubenBond/Clockwork#44.
            if (generic)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => ControlledTask.WaitAsync(new TaskCompletionSource<int>().Task, TimeSpan.FromMilliseconds(5), neverCanceled));
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => ControlledTask.WaitAsync(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(5), neverCanceled));
            }

            trace.Add(At("timed out", cluster));
        });

        Assert.Equal(["timed out@5"], trace);
        AssertCompleted(artifact);
        Assert.Equal(ContinuationDescription, Assert.Single(artifact.Diagnostics.CanceledOperations).Description);
    }

    [Fact]
    public void TimedOutWaitAsyncInSynchronousScenarioLeavesRunCompleted()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplaySynchronous((cluster, trace) =>
        {
            SimulationNode waiter = cluster.AddNode("waiter");
            waiter.Context.SchedulerLane.Enqueue(() =>
            {
                Task wait = ControlledTask.WaitAsync(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(5));
                SimulationTaskRuntime.DrainUntilCompleted(wait, "test.wait", CancellationToken.None);
                trace.Add(At(wait.Exception?.InnerException?.GetType().Name, cluster));
            });
        });

        Assert.Equal(["TimeoutException@5"], trace);
        AssertCompleted(artifact);
        Assert.Equal(ContinuationDescription, Assert.Single(artifact.Diagnostics.CanceledOperations).Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TokenCanceledWaitAsyncLeavesRunCompleted(bool generic)
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            using var cancellation = new CancellationTokenSource();
            cluster.SchedulerLane.EnqueueAfter(cancellation.Cancel, TimeSpan.FromMilliseconds(3));
            Task wait = generic
                ? ControlledTask.WaitAsync(new TaskCompletionSource<int>().Task, cancellation.Token)
                : ControlledTask.WaitAsync(new TaskCompletionSource().Task, cancellation.Token);
            await ObserveAsync(wait);
            trace.Add(At(wait.Status.ToString(), cluster));
        });

        Assert.Equal(["Canceled@3"], trace);
        AssertCompleted(artifact);
        Assert.Equal(ContinuationDescription, Assert.Single(artifact.Diagnostics.CanceledOperations).Description);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void TaskRunCanceledByItsTokenLeavesRunCompleted(bool bodyWaitsWithToken, int canceledOperations)
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            using var cancellation = new CancellationTokenSource();
            Task work;
            if (bodyWaitsWithToken)
            {
                work = ControlledTask.Run(
                    () => ControlledTask.WaitAsync(new TaskCompletionSource().Task, cancellation.Token),
                    cancellation.Token);
                cluster.SchedulerLane.EnqueueAfter(cancellation.Cancel, TimeSpan.FromMilliseconds(3));
            }
            else
            {
                work = ControlledTask.Run(
                    () =>
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    },
                    cancellation.Token);
            }

            await ObserveAsync(work);
            trace.Add(At(work.Status.ToString(), cluster));
        });

        Assert.Equal([bodyWaitsWithToken ? "Canceled@3" : "Canceled@0"], trace);
        AssertCompleted(artifact);

        // Task.Run is task-backed: its task observes the cancellation, so its operation completes and
        // nothing faults. A body which waits with its token cancels that wait's continuation, which the
        // diagnostics report; a body which throws OperationCanceledException itself cancels only its task.
        Assert.Equal(canceledOperations, artifact.Diagnostics.CanceledOperations.Count);
        Assert.DoesNotContain(
            artifact.Diagnostics.Operations,
            static operation => operation.State == nameof(SimulationOperationState.Faulted));
    }

    [Fact]
    public void EntryTaskThatEndsCanceledClassifiesRunCanceled()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            using var cancellation = new CancellationTokenSource();
            cluster.SchedulerLane.EnqueueAfter(cancellation.Cancel, TimeSpan.FromMilliseconds(2));
            trace.Add(At("waiting", cluster));
            await ControlledTask.Delay(TimeSpan.FromHours(1), cancellation.Token);
            trace.Add(At("resumed", cluster));
        });

        Assert.Equal(["waiting@0"], trace);
        Assert.Equal(ReplayTerminationKind.Canceled, artifact.Outcome.Kind);
        Assert.Equal("entry-task-canceled", artifact.Outcome.FailureIdentity);
        Assert.Equal(TimeSpan.FromMilliseconds(2).Ticks, artifact.Diagnostics.VirtualTimeTicks);
    }

    [Fact]
    public void EntryTaskThatFaultsClassifiesRunFaulted()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            await ControlledTask.Delay(TimeSpan.FromMilliseconds(1));
            trace.Add(At("throwing", cluster));
            throw new FormatException("entry failed");
        });

        Assert.Equal(["throwing@1"], trace);
        Assert.Equal(ReplayTerminationKind.Faulted, artifact.Outcome.Kind);
        Assert.Equal(typeof(FormatException).FullName, artifact.Outcome.FailureIdentity);
        Assert.Equal("entry failed", artifact.Outcome.Diagnostic);
    }

    [Fact]
    public void EntryTaskIncompleteAtQuiescenceClassifiesRunDeadlocked()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            trace.Add(At("waiting", cluster));
            await new TaskCompletionSource().Task;
        });

        Assert.Equal(["waiting@0"], trace);
        Assert.Equal(ReplayTerminationKind.Deadlocked, artifact.Outcome.Kind);
        Assert.Equal("entry-task-incomplete", artifact.Outcome.FailureIdentity);
    }

    [Fact]
    public void RunEndsWhenEntryTaskCompletesAndReportsLeftoverWork()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            _ = ControlledTask.Run(() => ControlledTask.Delay(TimeSpan.FromHours(1)));
            await ControlledTask.Delay(TimeSpan.FromMilliseconds(1));
            trace.Add(At("entry completed", cluster));
        });

        Assert.Equal(["entry completed@1"], trace);
        AssertCompleted(artifact);

        // The runner stops when the entry task completes; the background delay is left pending.
        Assert.Equal(TimeSpan.FromMilliseconds(1).Ticks, artifact.Diagnostics.VirtualTimeTicks);
        Assert.Equal("PausedUntilTime", artifact.Diagnostics.Liveness);
        Assert.Contains(
            artifact.Diagnostics.Operations,
            static operation => operation.State == nameof(SimulationOperationState.Created));
    }

    [Fact]
    public void ResettingDueTimerLeavesRunCompleted()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplaySynchronous((cluster, trace) =>
        {
            SimulationNode node = cluster.AddNode("timers");
            node.Context.SchedulerLane.Enqueue(() =>
            {
                ITimer? timer = null;
                timer = node.Context.TimeProvider.CreateTimer(
                    _ =>
                    {
                        trace.Add(At("fired", cluster));
                        timer!.Dispose();
                    },
                    state: null,
                    TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);

                // The zero-due callback is already admitted; resetting the timer cancels it before it runs.
                timer.Change(TimeSpan.FromMilliseconds(5), Timeout.InfiniteTimeSpan);
            });
        });

        Assert.Equal(["fired@5"], trace);
        AssertCompleted(artifact);
        Assert.Equal(LaneCallbackDescription, Assert.Single(artifact.Diagnostics.CanceledOperations).Description);
    }

    [Fact]
    public void OperationCanceledExceptionEscapingLaneCallbackFaultsRun()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplaySynchronous((cluster, trace) =>
        {
            cluster.SchedulerLane.Enqueue(() => trace.Add(At("before", cluster)));
            cluster.SchedulerLane.EnqueueAfter(
                static () => throw new OperationCanceledException(),
                TimeSpan.FromMilliseconds(1));
            cluster.SchedulerLane.EnqueueAfter(() => trace.Add(At("after", cluster)), TimeSpan.FromMilliseconds(2));
        });

        // The lane callback's escaping exception faults its operation, which stops the run at 1 ms.
        Assert.Equal(["before@0"], trace);
        AssertFaultedBy(artifact, typeof(OperationCanceledException), LaneCallbackDescription);
        Assert.Equal(TimeSpan.FromMilliseconds(1).Ticks, artifact.Diagnostics.VirtualTimeTicks);
    }

    [Fact]
    public void OperationCanceledExceptionEscapingThreadPoolItemFaultsRun()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            ControlledThreadPool.QueueUserWorkItem(static _ => throw new OperationCanceledException());
            await ControlledTask.Delay(TimeSpan.FromMilliseconds(5));
            trace.Add(At("entry completed", cluster));
        });

        Assert.Empty(trace);
        AssertFaultedBy(artifact, typeof(OperationCanceledException), ContinuationDescription);
        Assert.Equal(0, artifact.Diagnostics.VirtualTimeTicks);
    }

    [Fact]
    public void TaskCanceledExceptionEscapingTimerCallbackFaultsRunWithItsConcreteType()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            using var timer = new ControlledTimer(
                static _ => throw new TaskCanceledException(),
                state: null,
                dueTime: 1,
                period: Timeout.Infinite);
            await ControlledTask.Delay(TimeSpan.FromMilliseconds(5));
            trace.Add(At("entry completed", cluster));
        });

        Assert.Empty(trace);
        AssertFaultedBy(artifact, typeof(TaskCanceledException), ContinuationDescription);
        Assert.Equal(TimeSpan.FromMilliseconds(1).Ticks, artifact.Diagnostics.VirtualTimeTicks);
    }

    [Fact]
    public void OperationCanceledExceptionEscapingRawScheduleBodyFaultsRun()
    {
        static void Scenario(SimulationScheduler scheduler) =>
            scheduler.Schedule("raw body", static () => throw new OperationCanceledException());

        ReplayExecutionResult recorded = ReplayRunner.Record(Options, Scenario, TestContext.Current.CancellationToken);
        ReplayExecutionResult replayed = ReplayRunner.Replay(
            Persist(recorded.Artifact),
            ReplayCompatibilityRequirements.Current(),
            Scenario,
            maxSteps: Options.MaxSteps,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(replayed.Reproduced);
        AssertFaultedBy(recorded.Artifact, typeof(OperationCanceledException), "raw body");
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(FormatException))]
    public void ExceptionEscapingThreadBodyFaultsRun(Type exceptionType)
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask((cluster, trace) =>
        {
            Thread thread = ControlledThread.Create(() => throw (Exception)Activator.CreateInstance(exceptionType)!);
            ControlledThread.Start(thread);
            ControlledThread.Join(thread);
            trace.Add(At("joined", cluster));
            return Task.CompletedTask;
        });

        // As in .NET, where the thread's unhandled exception crashes the process, the run ends there.
        Assert.Empty(trace);
        AssertFaultedBy(artifact, exceptionType, ContinuationDescription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitCycleLeftWhenEntryTaskCompletesIsADiagnostic(bool unrelatedTimerPending)
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            ScheduleWaitCycle(cluster.Scheduler);
            if (unrelatedTimerPending)
            {
                _ = ControlledTask.Run(() => ControlledTask.Delay(TimeSpan.FromHours(1)));
            }

            await ControlledTask.Delay(TimeSpan.FromMilliseconds(1));
            trace.Add(At("entry completed", cluster));
        });

        // The entry task ended the run, as Main returning ends a process; whether unrelated work is
        // still pending does not change the outcome, and the cycle it left is a diagnostic.
        Assert.Equal(["entry completed@1"], trace);
        AssertCompleted(artifact);
        Assert.Single(artifact.Diagnostics.DeadlockCycles);
    }

    [Fact]
    public void WaitCycleWhileEntryTaskIsIncompleteClassifiesRunDeadlocked()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            ScheduleWaitCycle(cluster.Scheduler);
            trace.Add(At("waiting", cluster));
            await new TaskCompletionSource().Task;
        });

        Assert.Equal(["waiting@0"], trace);
        Assert.Equal(ReplayTerminationKind.Deadlocked, artifact.Outcome.Kind);
        Assert.StartsWith("deadlock:", artifact.Outcome.FailureIdentity, StringComparison.Ordinal);
        Assert.Single(artifact.Diagnostics.DeadlockCycles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaskReturningCallbackPassedToRecordClusterRunsAsSynchronousEntry(bool asyncLambda)
    {
        // An async lambda or a Task-returning call still binds to the synchronous Action overload: the
        // callback only registers work, so the runner drives that work and observes its failure.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ReplayExecutionResult recorded = asyncLambda
            ? ReplayRunner.RecordCluster(
                Options,
                async cluster =>
                {
                    RegisterFailingWork(cluster);
                    await Task.CompletedTask;
                },
                cancellationToken)
            : ReplayRunner.RecordCluster(Options, cluster => RegisterFailingWorkAsync(cluster), cancellationToken);
        ReplayExecutionResult replayed = asyncLambda
            ? ReplayRunner.ReplayCluster(
                Persist(recorded.Artifact),
                ReplayCompatibilityRequirements.Current(),
                async cluster =>
                {
                    RegisterFailingWork(cluster);
                    await Task.CompletedTask;
                },
                Options.MaxSteps,
                cancellationToken)
            : ReplayRunner.ReplayCluster(
                Persist(recorded.Artifact),
                ReplayCompatibilityRequirements.Current(),
                cluster => RegisterFailingWorkAsync(cluster),
                Options.MaxSteps,
                cancellationToken);

        Assert.True(replayed.Reproduced);
        Assert.False(recorded.Artifact.Scheduler.Options.ContainsKey(EntryOption));
        AssertFaultedBy(recorded.Artifact, typeof(FormatException), LaneCallbackDescription);
    }

    [Fact]
    public void ArtifactRecordsItsEntryFormAndReplayRejectsTheOtherForm()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ReplayArtifact entryTask = ReplayRunner.RecordClusterEntry(
            Options,
            static _ => Task.CompletedTask,
            cancellationToken).Artifact;
        ReplayArtifact callback = ReplayRunner.RecordCluster(Options, static _ => { }, cancellationToken).Artifact;
        ReplayArtifact unknown = callback with
        {
            Scheduler = callback.Scheduler with
            {
                Options = new SortedDictionary<string, string>(
                    callback.Scheduler.Options.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal)
                {
                    [EntryOption] = "unknown",
                },
            },
        };

        ReplayCompatibilityException entryTaskAsCallback = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.ReplayCluster(
                Persist(entryTask),
                ReplayCompatibilityRequirements.Current(),
                static _ => { },
                Options.MaxSteps,
                cancellationToken));
        ReplayCompatibilityException callbackAsEntryTask = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.ReplayClusterEntry(
                Persist(callback),
                ReplayCompatibilityRequirements.Current(),
                static _ => Task.CompletedTask,
                Options.MaxSteps,
                cancellationToken));
        ReplayCompatibilityException unknownForm = Assert.Throws<ReplayCompatibilityException>(
            () => ReplayRunner.ReplayCluster(
                unknown,
                ReplayCompatibilityRequirements.Current(),
                static _ => { },
                Options.MaxSteps,
                cancellationToken));

        Assert.Equal(TaskEntry, entryTask.Scheduler.Options[EntryOption]);
        Assert.False(callback.Scheduler.Options.ContainsKey(EntryOption));
        Assert.Contains("entry form mismatch: artifact='task', requested='callback'", entryTaskAsCallback.Message, StringComparison.Ordinal);
        Assert.Contains("entry form mismatch: artifact='callback', requested='task'", callbackAsEntryTask.Message, StringComparison.Ordinal);
        Assert.Contains("Unsupported replay entry form 'unknown'", unknownForm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploreClusterEntryRecordsEntryTaskRunsWhichReplayOnlyAsEntryTasks()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScheduleExplorationResult result = ScheduleExplorer.ExploreClusterEntry(
            new ScheduleExplorationOptions
            {
                SimulationSeed = 4404,
                FirstScheduleSeed = 1,
                MaxIterations = 3,
                MaxStepsPerIteration = 10_000,
                MaxFailures = 3,
            },
            static cluster => RegisterFailingWorkAsync(cluster),
            cancellationToken);

        // Each run ends when its entry task completes, before the registered work fails.
        Assert.Equal(3, result.Iterations.Count);
        Assert.All(result.Iterations, iteration =>
        {
            ReplayArtifact artifact = iteration.Execution.Artifact;
            Assert.Equal(TaskEntry, artifact.Scheduler.Options[EntryOption]);
            AssertCompleted(artifact);
            Assert.True(ReplayRunner.ReplayClusterEntry(
                Persist(artifact),
                ReplayCompatibilityRequirements.Current(),
                static cluster => RegisterFailingWorkAsync(cluster),
                Options.MaxSteps,
                cancellationToken).Reproduced);
            Assert.Throws<ReplayCompatibilityException>(() => ReplayRunner.ReplayCluster(
                artifact,
                ReplayCompatibilityRequirements.Current(),
                static cluster => RegisterFailingWorkAsync(cluster),
                Options.MaxSteps,
                cancellationToken));
        });
    }

    private static void RegisterFailingWork(SimulationCluster cluster) =>
        cluster.SchedulerLane.EnqueueAfter(
            static () => throw new FormatException("registered work"),
            TimeSpan.FromMilliseconds(5));

    private static Task RegisterFailingWorkAsync(SimulationCluster cluster)
    {
        RegisterFailingWork(cluster);
        return Task.CompletedTask;
    }

    // Two raw operations which each own one monitor resource and then wait indefinitely on the other's.
    private static void ScheduleWaitCycle(SimulationScheduler scheduler)
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

    [Fact]
    public void HandledTaskRunFaultIsNotAnOperationFault()
    {
        (ReplayArtifact artifact, List<string> trace) = RecordAndReplayEntryTask(async (cluster, trace) =>
        {
            Task work = ControlledTask.Run(static () => throw new FormatException("handled"), CancellationToken.None);
            await ObserveAsync(work);
            trace.Add(At(work.Exception?.InnerException?.Message, cluster));
        });

        Assert.Equal(["handled@0"], trace);
        AssertCompleted(artifact);
        Assert.DoesNotContain(
            artifact.Diagnostics.Operations,
            static operation => operation.State == nameof(SimulationOperationState.Faulted));
    }

    // Awaits on the entry's lane, so the continuation stays on the controlled scheduler, and observes
    // the task's fault or cancellation as an ordinary outcome.
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or FormatException)
        {
        }
    }

    private static void AssertCompleted(ReplayArtifact artifact)
    {
        Assert.Equal(ReplayTerminationKind.Completed, artifact.Outcome.Kind);
        Assert.Null(artifact.Outcome.FailureIdentity);
    }

    // An exception escaping an operation body is unhandled: the operation faults, not cancels, and
    // its concrete type is the run's failure identity.
    private static void AssertFaultedBy(ReplayArtifact artifact, Type exceptionType, string operationDescription)
    {
        Assert.Equal(ReplayTerminationKind.Faulted, artifact.Outcome.Kind);
        Assert.Equal(exceptionType.FullName, artifact.Outcome.FailureIdentity);
        ReplayOperationDiagnostic faulted = Assert.Single(
            artifact.Diagnostics.Operations,
            static operation => operation.State == nameof(SimulationOperationState.Faulted));
        Assert.Equal(operationDescription, faulted.Description);
        Assert.Empty(artifact.Diagnostics.CanceledOperations);
    }

    private static string At(string? observation, SimulationCluster cluster) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{observation}@{cluster.Scheduler.VirtualTime.TotalMilliseconds}");

    private static (ReplayArtifact Artifact, List<string> Trace) RecordAndReplayEntryTask(
        Func<SimulationCluster, List<string>, Task> scenario)
    {
        var recordedTrace = new List<string>();
        ReplayExecutionResult recorded = ReplayRunner.RecordClusterEntry(
            Options,
            cluster => scenario(cluster, recordedTrace),
            TestContext.Current.CancellationToken);

        var replayedTrace = new List<string>();
        ReplayExecutionResult replayed = ReplayRunner.ReplayClusterEntry(
            Persist(recorded.Artifact),
            ReplayCompatibilityRequirements.Current(),
            cluster => scenario(cluster, replayedTrace),
            maxSteps: Options.MaxSteps,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TaskEntry, recorded.Artifact.Scheduler.Options[EntryOption]);
        Assert.True(replayed.Reproduced);
        Assert.Equal(recordedTrace, replayedTrace);
        return (recorded.Artifact, recordedTrace);
    }

    private static (ReplayArtifact Artifact, List<string> Trace) RecordAndReplaySynchronous(
        Action<SimulationCluster, List<string>> scenario)
    {
        var recordedTrace = new List<string>();
        ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
            Options,
            cluster => scenario(cluster, recordedTrace),
            TestContext.Current.CancellationToken);

        var replayedTrace = new List<string>();
        ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
            Persist(recorded.Artifact),
            ReplayCompatibilityRequirements.Current(),
            cluster => scenario(cluster, replayedTrace),
            maxSteps: Options.MaxSteps,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(recorded.Artifact.Scheduler.Options.ContainsKey(EntryOption));
        Assert.True(replayed.Reproduced);
        Assert.Equal(recordedTrace, replayedTrace);
        return (recorded.Artifact, recordedTrace);
    }

    private static ReplayArtifact Persist(ReplayArtifact artifact)
    {
        ReplayArtifact persisted = ReplayArtifactSerializer.Deserialize(ReplayArtifactSerializer.Serialize(artifact));
        Assert.Equal(artifact.Outcome, persisted.Outcome);
        Assert.Equal(
            artifact.Diagnostics.CanceledOperations.Select(static operation => operation.Id),
            persisted.Diagnostics.CanceledOperations.Select(static operation => operation.Id));
        return persisted;
    }

    private static ReplayRecordingOptions Options { get; } = new()
    {
        SimulationSeed = 4404,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = 44,
        MaxSteps = 10_000,
        IncludeDiagnosticMessages = true,
    };
}
