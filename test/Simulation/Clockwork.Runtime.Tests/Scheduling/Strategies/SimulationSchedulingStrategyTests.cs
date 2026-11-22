using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

namespace Orleans.Testing.Simulation.Runtime.Tests.Scheduling.Strategies;

/// <summary>
/// Exercises the pluggable scheduling strategies and their decision recording/replay wiring. Each
/// test drives several operations that yield a fixed number of times, appending their id every time
/// they are granted the baton, so the resulting list is the exact interleaving the strategy produced.
/// </summary>
public sealed class SimulationSchedulingStrategyTests
{
    private const int YieldsPerOperation = 2;

    [Fact]
    public void RoundRobinIsTheDefaultAndRotatesAcrossRunnableOperations()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        Assert.Equal("round-robin", scheduler.SchedulingStrategy.Name);

        var order = DriveThreeYieldingOperations(scheduler);

        // last=None->1, then 2, then 3, wrapping: a fair rotation.
        Assert.Equal([1L, 2L, 3L, 1L, 2L, 3L, 1L, 2L, 3L], order);
    }

    [Fact]
    public void RoundRobinFastPathMatchesInstrumentedSelection()
    {
        using var fast = SchedulerTestHarness.NewScheduler();
        using var instrumented = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();
        instrumented.DecisionLog = log;

        var fastOrder = DriveThreeYieldingOperations(fast);
        var instrumentedOrder = DriveThreeYieldingOperations(instrumented);

        Assert.Equal(fastOrder, instrumentedOrder);
        Assert.NotEmpty(log.Records);
        Assert.All(log.Records, record => Assert.Equal("round-robin", record.SourceId));
    }

    [Fact]
    public void FifoAlwaysRunsTheSmallestRunnableId()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Fifo();

        var order = DriveThreeYieldingOperations(scheduler);

        // The smallest runnable id is picked every step, so each operation runs to completion in turn.
        Assert.Equal([1L, 1L, 1L, 2L, 2L, 2L, 3L, 3L, 3L], order);
    }

    [Fact]
    public void PriorityRunsHighestPriorityBandFirstThenRoundRobinWithinIt()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Priority();

        var order = new List<long>();
        // Ids 1,2,3 with priorities 0,10,5 -> op2 (highest) drains first, then op3, then op1.
        ScheduleYielding(scheduler, order, priority: 0);
        ScheduleYielding(scheduler, order, priority: 10);
        ScheduleYielding(scheduler, order, priority: 5);

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal([2L, 2L, 2L, 3L, 3L, 3L, 1L, 1L, 1L], order);
    }

    [Fact]
    public void PriorityRotatesFairlyWithinAnEqualPriorityBand()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Priority();

        var order = new List<long>();
        ScheduleYielding(scheduler, order, priority: 5);
        ScheduleYielding(scheduler, order, priority: 5);
        ScheduleYielding(scheduler, order, priority: 5);

        scheduler.Drain(TestContext.Current.CancellationToken);

        // All equal priority collapses to plain round-robin.
        Assert.Equal([1L, 2L, 3L, 1L, 2L, 3L, 1L, 2L, 3L], order);
    }

    [Fact]
    public void PriorityRotatesAcrossANoncontiguousHighestPriorityBand()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Priority();

        var order = new List<long>();
        ScheduleYielding(scheduler, order, priority: 10);
        ScheduleYielding(scheduler, order, priority: 0);
        ScheduleYielding(scheduler, order, priority: 10);

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal([1L, 3L, 1L, 3L, 1L, 3L, 2L, 2L, 2L], order);
    }

    [Fact]
    public void PriorityRotatesWhenAllPrioritiesAreMinimumValue()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Priority();

        var order = new List<long>();
        ScheduleYielding(scheduler, order, priority: int.MinValue);
        ScheduleYielding(scheduler, order, priority: int.MinValue);
        ScheduleYielding(scheduler, order, priority: int.MinValue);

        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L, 1L, 2L, 3L, 1L, 2L, 3L], order);
    }

    [Fact]
    public void SameSeedProducesTheSameSchedule()
    {
        var first = RunSeeded(seed: 12345);
        var second = RunSeeded(seed: 12345);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeedsMeaningfullyExploreDifferentSchedules()
    {
        var schedules = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 1; seed <= 12; seed++)
        {
            schedules.Add(string.Join(",", RunSeeded(seed)));
        }

        // Distinct seeds must not collapse to a single interleaving; the search actually explores.
        Assert.True(schedules.Count > 1, "Seeded-random scheduling did not explore multiple interleavings.");
    }

    [Fact]
    public void SeededRandomRecordsEverySchedulingChoiceButNotSingleCandidateSteps()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();
        scheduler.DecisionLog = log;
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(scheduler.Runtime);

        DriveThreeYieldingOperations(scheduler);

        Assert.NotEmpty(log.Records);
        Assert.All(log.Records, r =>
        {
            Assert.Equal(SimulationDecisionKind.SchedulingOrder, r.Kind);
            Assert.Equal(SimulationSeedDomain.Scheduler, r.Domain);
            Assert.Equal("seeded-random", r.SourceId);
        });

        // Every recorded selected id must have been among that decision's candidate set.
        Assert.All(log.Records, r =>
        {
            var candidates = r.InputMetadata!.Split(',');
            Assert.Contains(r.SelectedResult, candidates);
        });
    }

    [Fact]
    public void ReplayReproducesARecordedSeededScheduleExactly()
    {
        // Record a seeded run.
        using var recorder = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();
        recorder.DecisionLog = log;
        recorder.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(recorder.Runtime);
        var recordedOrder = DriveThreeYieldingOperations(recorder);

        // Replay from the recorded decisions, with a different (default) seed source.
        using var replay = SchedulerTestHarness.NewScheduler(seed: 999);
        replay.SchedulingStrategy = SimulationSchedulingStrategies.Replay(log.Records);
        var replayedOrder = DriveThreeYieldingOperations(replay);
        replay.ValidateReplayComplete();

        Assert.Equal(recordedOrder, replayedOrder);
    }

    [Fact]
    public void ReplayValidatorFailsAtTheFirstDivergentSchedulingChoice()
    {
        // Record with FIFO so the schedule is [1,1,1,2,2,2,3,3,3].
        using var recorder = SchedulerTestHarness.NewScheduler();
        var log = new SimulationDecisionLog();
        recorder.DecisionLog = log;
        recorder.SchedulingStrategy = SimulationSchedulingStrategies.Fifo();
        DriveThreeYieldingOperations(recorder);

        // Re-run with round-robin (diverges immediately) while validating against the FIFO recording.
        using var replay = SchedulerTestHarness.NewScheduler();
        replay.SchedulingStrategy = SimulationSchedulingStrategies.RoundRobin();
        replay.ReplayValidator = new SimulationDecisionReplayValidator(
            new SimulationInMemoryDecisionReplayReader(log.Records));

        Assert.Throws<SimulationDecisionReplayMismatchException>(() => DriveThreeYieldingOperations(replay));
    }

    [Fact]
    public void ReplayStrategyThrowsWhenTheRecordedSelectionIsNotRunnable()
    {
        // A hand-built log whose single scheduling decision names an operation id that will never be
        // runnable, so replay must fail deterministically rather than silently picking something else.
        var bogus = new List<SimulationDecisionRecord>
        {
            new(
                new SimulationDecisionId(0),
                SimulationSeedDomain.Scheduler,
                SimulationDecisionKind.SchedulingOrder,
                "seeded-random",
                "1,2",
                "999",
                Guid.NewGuid(),
                NodeId: null,
                Orleans.Testing.Simulation.Runtime.Execution.SimulationLogicalExecutionId.None),
        };

        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Replay(bogus);
        var order = new List<long>();
        ScheduleYielding(scheduler, order);
        ScheduleYielding(scheduler, order);

        Assert.Throws<SimulationDecisionReplayMismatchException>(() => scheduler.Drain(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExplicitReplayCompletionRejectsUnconsumedSchedulingRecords()
    {
        var records = new[]
        {
            SchedulingRecord(0, "1"),
            SchedulingRecord(1, "2"),
        };
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Replay(records);
        scheduler.Schedule("first", () => { });
        scheduler.Schedule("second", () => { });

        scheduler.Drain(TestContext.Current.CancellationToken);
        var exception = Assert.Throws<SimulationDecisionReplayMismatchException>(scheduler.ValidateReplayComplete);

        Assert.Contains("unconsumed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitReplayCompletionRejectsUnconsumedDecisionValidationRecords()
    {
        var extra = SchedulingRecord(0, "1");
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.ReplayValidator = new SimulationDecisionReplayValidator(
            new SimulationInMemoryDecisionReplayReader([extra]));
        scheduler.Schedule("only", () => { });

        scheduler.Drain(TestContext.Current.CancellationToken);
        var exception = Assert.Throws<SimulationDecisionReplayMismatchException>(scheduler.ValidateReplayComplete);

        Assert.Equal(extra, exception.Expected);
        Assert.Null(exception.Actual);
    }

    [Fact]
    public void PartialRunDoesNotRequireReplayCompletion()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Replay(
            [
                SchedulingRecord(0, "1"),
                SchedulingRecord(1, "2"),
            ]);
        scheduler.Schedule("first", () => { });
        scheduler.Schedule("second", () => { });

        Assert.True(scheduler.RunStep(TestContext.Current.CancellationToken));
        Assert.Throws<SimulationSchedulerException>(scheduler.ValidateReplayComplete);
    }

    [Fact]
    public void ReusableDrainDoesNotPrematurelyValidateLaterReplayBatches()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.Replay(
            [
                SchedulingRecord(0, "1"),
                SchedulingRecord(1, "3"),
            ]);

        scheduler.Schedule("batch-one-a", () => { });
        scheduler.Schedule("batch-one-b", () => { });
        scheduler.Drain(TestContext.Current.CancellationToken);

        scheduler.Schedule("batch-two-a", () => { });
        scheduler.Schedule("batch-two-b", () => { });
        scheduler.Drain(TestContext.Current.CancellationToken);
        scheduler.ValidateReplayComplete();
    }

    [Fact]
    public void ParallelSimulationsWithTheSameSeedAreIsolatedAndReproducible()
    {
        // Two independent schedulers sharing a simulation seed must produce identical schedules and keep
        // their decision logs separate - no shared static scheduling state leaks between simulations.
        var (orderA, logA) = RunSeededWithLog(seed: 4242);
        var (orderB, logB) = RunSeededWithLog(seed: 4242);

        Assert.Equal(orderA, orderB);
        Assert.Equal(logA.Records.Count, logB.Records.Count);
        Assert.NotSame(logA, logB);
    }

    [Fact]
    public void SettingANullStrategyThrows()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        Assert.Throws<ArgumentNullException>(() => scheduler.SchedulingStrategy = null!);
    }

    private static List<long> RunSeeded(int seed)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler(seed: seed);
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(scheduler.Runtime);
        return DriveThreeYieldingOperations(scheduler);
    }

    private static (List<long> Order, SimulationDecisionLog Log) RunSeededWithLog(int seed)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler(seed: seed);
        var log = new SimulationDecisionLog();
        scheduler.DecisionLog = log;
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(scheduler.Runtime);
        var order = DriveThreeYieldingOperations(scheduler);
        return (order, log);
    }

    private static List<long> DriveThreeYieldingOperations(SimulationScheduler scheduler)
    {
        var order = new List<long>();
        ScheduleYielding(scheduler, order);
        ScheduleYielding(scheduler, order);
        ScheduleYielding(scheduler, order);
        scheduler.Drain(TestContext.Current.CancellationToken);
        return order;
    }

    private static void ScheduleYielding(SimulationScheduler scheduler, List<long> order, int priority = 0)
    {
        SimulationOperation? self = null;
        self = scheduler.Schedule(
            "yielder",
            () =>
            {
                order.Add(self!.Id.Value);
                for (var i = 0; i < YieldsPerOperation; i++)
                {
                    scheduler.Yield();
                    order.Add(self!.Id.Value);
                }
            },
            node: null,
            priority: priority);
    }

    private static SimulationDecisionRecord SchedulingRecord(long sequence, string selectedResult) =>
        new(
            new SimulationDecisionId(sequence),
            SimulationSeedDomain.Scheduler,
            SimulationDecisionKind.SchedulingOrder,
            "seeded-random",
            "1,2",
            selectedResult,
            Guid.NewGuid(),
            NodeId: null,
            Orleans.Testing.Simulation.Runtime.Execution.SimulationLogicalExecutionId.None);
}
