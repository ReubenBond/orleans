using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

/// <summary>
/// Verifies that the controlled-operation scheduler installs a logical execution identity into the
/// ambient <see cref="SimulationExecutionContext"/> such that decisions recorded from inside an
/// operation body automatically carry that operation's runtime, node, and logical identity - the
/// controlled-operation kernel "logical identity wiring" requirement without changing the decision API.
/// </summary>
public sealed class SimulationOperationDecisionWiringTests
{
    [Fact]
    public void DecisionRecordedInsideOperationCarriesItsLogicalAndRuntimeIdentity()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();
        SimulationDecisionRecord? recorded = null;

        var op = scheduler.Schedule("decide", () =>
        {
            var request = SimulationDecisionRequest.FromAmbientContext(
                SimulationSeedDomain.Scheduler,
                SimulationDecisionKind.Choice,
                selectedResult: "picked",
                sourceId: "unit-test");
            recorded = log.Record(request);
        });

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.NotNull(recorded);
        Assert.Equal(op.LogicalExecutionId, recorded!.LogicalExecutionId);
        Assert.Equal(scheduler.Runtime.Id, recorded.RuntimeId);
        Assert.Null(recorded.NodeId);
        Assert.Equal("picked", recorded.SelectedResult);
    }

    [Fact]
    public void DecisionRecordedInsideNodeScopedOperationCarriesTheNodeAddress()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();
        SimulationDecisionRecord? recorded = null;

        scheduler.Schedule(
            "decide",
            () => recorded = log.Record(SimulationDecisionRequest.FromAmbientContext(
                SimulationSeedDomain.Network,
                SimulationDecisionKind.RandomDraw,
                selectedResult: "7")),
            new SimulationNodeIdentity("node-7"));

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.NotNull(recorded);
        Assert.Equal("node-7", recorded!.NodeId);
    }

    [Fact]
    public void DistinctOperationsProduceDistinctLogicalIdentityOnTheirDecisions()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();

        var opA = scheduler.Schedule("a", () => log.Record(SimulationDecisionRequest.FromAmbientContext(
            SimulationSeedDomain.Scheduler, SimulationDecisionKind.Choice, "a")));
        var opB = scheduler.Schedule("b", () => log.Record(SimulationDecisionRequest.FromAmbientContext(
            SimulationSeedDomain.Scheduler, SimulationDecisionKind.Choice, "b")));

        scheduler.Drain(TestContext.Current.CancellationToken);

        var records = log.Records;
        Assert.Equal(2, records.Count);
        Assert.NotEqual(opA.LogicalExecutionId, opB.LogicalExecutionId);
        Assert.Equal(opA.LogicalExecutionId, records[0].LogicalExecutionId);
        Assert.Equal(opB.LogicalExecutionId, records[1].LogicalExecutionId);
    }

    [Fact]
    public void FromAmbientContextThrowsWhenNoRuntimeScopeIsActive()
    {
        Assert.Throws<InvalidOperationException>(() => SimulationDecisionRequest.FromAmbientContext(
            SimulationSeedDomain.Scheduler,
            SimulationDecisionKind.Choice,
            selectedResult: "x"));
    }

    [Fact]
    public void ExternalCancellationVersusSignalWinnerIsRecordedAndReplayValidated()
    {
        var log = new SimulationDecisionLog();
        SimulationWaitOutcome recordedOutcome;
        using (var recorder = SchedulerTestHarness.NewScheduler())
        {
            recorder.DecisionLog = log;
            var resource = recorder.CreateResource(SimulationResourceKind.Semaphore, "race");
            using var cancellation = new CancellationTokenSource();
            SimulationWaitOutcome? outcome = null;
            recorder.Schedule(
                "waiter",
                () => outcome = recorder.WaitOnResource(
                    resource,
                    Timeout.InfiniteTimeSpan,
                    SimulationPauseReason.ResourceWait("race"),
                    cancellation.Token));
            Assert.True(recorder.RunStep(TestContext.Current.CancellationToken));

            using var start = new Barrier(2);
            var testCancellation = TestContext.Current.CancellationToken;
            var signaler = new Thread(() =>
            {
                start.SignalAndWait(testCancellation);
                recorder.SignalOne(resource);
            })
            {
                IsBackground = true,
            };
            var canceler = new Thread(() =>
            {
                start.SignalAndWait(testCancellation);
                cancellation.Cancel();
            })
            {
                IsBackground = true,
            };
            signaler.Start();
            canceler.Start();
            Assert.True(signaler.Join(TimeSpan.FromSeconds(5)));
            Assert.True(canceler.Join(TimeSpan.FromSeconds(5)));
            recorder.Drain(TestContext.Current.CancellationToken);
            recordedOutcome = Assert.IsType<SimulationWaitOutcome>(outcome);
        }

        var record = Assert.Single(log.Records);
        Assert.Equal(SimulationDecisionKind.Choice, record.Kind);
        Assert.Equal("resource-wait-resolution", record.SourceId);
        Assert.Equal(recordedOutcome.ToString(), record.SelectedResult);

        using var replay = SchedulerTestHarness.NewScheduler();
        replay.ReplayValidator = new SimulationDecisionReplayValidator(
            new SimulationInMemoryDecisionReplayReader(log.Records));
        var replayResource = replay.CreateResource(SimulationResourceKind.Semaphore, "race");
        using var replayCancellation = new CancellationTokenSource();
        SimulationWaitOutcome? replayedOutcome = null;
        replay.Schedule(
            "waiter",
            () => replayedOutcome = replay.WaitOnResource(
                replayResource,
                Timeout.InfiniteTimeSpan,
                SimulationPauseReason.ResourceWait("race"),
                replayCancellation.Token));
        Assert.True(replay.RunStep(TestContext.Current.CancellationToken));

        if (recordedOutcome == SimulationWaitOutcome.Signaled)
        {
            replay.SignalOne(replayResource);
            replayCancellation.Cancel();
        }
        else
        {
            replayCancellation.Cancel();
            replay.SignalOne(replayResource);
        }

        replay.Drain(TestContext.Current.CancellationToken);
        replay.ValidateReplayComplete();
        Assert.Equal(recordedOutcome, replayedOutcome);
    }
}
