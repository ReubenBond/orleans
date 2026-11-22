using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>Verifies injected scheduling points use the controlled scheduler and replayable strategy.</summary>
public sealed class RaceSchedulingPointTests
{
    [Fact]
    public void SchedulingPointEmitsExactMetadataAndYieldsThroughScheduler()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.Schedule(
            "writer",
            () => RaceInstrumentation.WriteStatic(
                "Fx.Subject::Value",
                "System.Void Fx.Subject::Run()",
                42,
                "Subject.cs",
                17));

        Assert.Equal(2, scheduler.Drain(TestContext.Current.CancellationToken));

        RaceSchedulingPoint point = Assert.Single(scheduler.CaptureRaceSchedulingPoints());
        Assert.Equal(RaceAccessKind.Write, point.Kind);
        Assert.Equal("Fx.Subject::Value", point.Location);
        Assert.Equal("System.Void Fx.Subject::Run()", point.Source.Method);
        Assert.Equal(42, point.Source.ILOffset);
        Assert.Equal("Subject.cs", point.Source.SourceFile);
        Assert.Equal(17, point.Source.SourceLine);
        Assert.Equal(1, scheduler.CreatedWorkerThreadCount);
    }

    [Fact]
    public void SameSeedReproducesTraceAndAlternateSeedsExploreDifferentInterleavings()
    {
        string first = Run(seed: 7);
        string replay = Run(seed: 7);
        Assert.Equal(first, replay);

        var traces = Enumerable.Range(1, 24).Select(Run).ToHashSet(StringComparer.Ordinal);
        Assert.True(traces.Count > 1, "Expected alternate scheduler seeds to explore more than one trace.");
    }

    [Fact]
    public void TraceRetainsTheMostRecentPointsUpToItsCapacity()
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();
        scheduler.RaceTraceCapacity = 3;
        scheduler.Schedule("first", () => AccessTwice("A"));
        scheduler.Schedule("second", () => AccessTwice("B"));
        scheduler.Schedule("racer", () => RaceInstrumentation.WriteStatic("Fx.Subject::Value", "C", 3, null, -1));
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.Equal([3L, 4L, 5L], scheduler.CaptureRaceSchedulingPoints().Select(static point => point.Sequence));
        RaceReport race = Assert.IsType<RaceReport>(scheduler.FirstRace);
        Assert.InRange(race.ScheduleTrace.Length, 1, 3);
        Assert.Equal(race.SecondAccess.Source, race.ScheduleTrace[^1].Source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ReplayArtifactLimits.MaxRaceSchedulingPoints + 1)]
    public void TraceCapacityOutsideWhatAnArtifactHoldsIsRejected(int capacity)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler();

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.RaceTraceCapacity = capacity);
        scheduler.RaceTraceCapacity = ReplayArtifactLimits.MaxRaceSchedulingPoints;
        Assert.Equal(ReplayArtifactLimits.MaxRaceSchedulingPoints, scheduler.RaceTraceCapacity);
    }

    [Fact]
    public void LoweringTheTraceCapacityKeepsTheMostRecentPoints()
    {
        var trace = new RaceSchedulingTrace(capacity: 4);
        for (int sequence = 1; sequence <= 6; sequence++)
        {
            trace.Add(new RaceSchedulingPoint(sequence, new SimulationOperationId(1), RaceAccessKind.Read, "x", default));
        }

        trace.Capacity = 2;
        trace.Add(new RaceSchedulingPoint(7, new SimulationOperationId(1), RaceAccessKind.Read, "x", default));

        Assert.Equal([6L, 7L], trace.Select(static point => point.Sequence));
    }

    [Fact]
    public void SchedulingPointOutsideControlledOperationIsNoOp()
    {
        RaceInstrumentation.ReadStatic("Fx.Subject::Value", "Fx.Subject::Run", 1, null, -1);
    }

    private static string Run(int seed)
    {
        using var scheduler = SchedulerTestHarness.NewScheduler(seed: seed);
        scheduler.SchedulingStrategy = SimulationSchedulingStrategies.SeededRandom(seed);
        scheduler.DecisionLog = new SimulationDecisionLog();
        scheduler.Schedule("a", () => AccessTwice("A"));
        scheduler.Schedule("b", () => AccessTwice("B"));
        scheduler.Drain(TestContext.Current.CancellationToken);

        Assert.NotEmpty(scheduler.DecisionLog.Records);
        Assert.InRange(scheduler.CreatedWorkerThreadCount, 1, 2);
        return string.Join(
            ",",
            scheduler.CaptureRaceSchedulingPoints().Select(point => $"{point.OperationId.Value}:{point.Source.Method}"));
    }

    private static void AccessTwice(string method)
    {
        RaceInstrumentation.WriteStatic("Fx.Subject::Value", method, 1, "Subject.cs", 10);
        RaceInstrumentation.ReadStatic("Fx.Subject::Value", method, 2, "Subject.cs", 11);
    }
}
