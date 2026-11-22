using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationProgressReporterTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("5s", 5_000)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("00:00:05", 5_000)]
    public void ParsesSupportedProgressIntervals(string value, double expectedMilliseconds)
    {
        Assert.True(SimulationProgressEnvironment.TryParseInterval(value, out TimeSpan interval));
        Assert.Equal(expectedMilliseconds, interval.TotalMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0s")]
    [InlineData("-1s")]
    [InlineData("5")]
    [InlineData("soon")]
    public void RejectsInvalidProgressIntervals(string value) =>
        Assert.False(SimulationProgressEnvironment.TryParseInterval(value, out _));

    [Fact]
    public void ReportsExactLiveCountersAfterTheConfiguredWallClockInterval()
    {
        TimeSpan wallTime = TimeSpan.Zero;
        var output = new StringWriter();
        var runtime = new SimulationRuntimeIdentity(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Seed: 17,
            Description: "test");
        var reporter = new SimulationProgressReporter(
            TimeSpan.FromSeconds(5),
            runtime,
            output,
            () => wallTime,
            () => new SimulationPendingWorkSummary(1, 2, 3, []),
            () => 4);
        var snapshot = new SimulationProgressSnapshot(
            Iterations: 9,
            StepsExecuted: 7,
            TimeAdvanceCount: 2,
            ConsecutiveTimeAdvanceCount: 1,
            StartTime: DateTimeOffset.UnixEpoch,
            CurrentTime: DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(10));

        wallTime = TimeSpan.FromSeconds(4);
        reporter.Report(snapshot);
        Assert.Equal(string.Empty, output.ToString());

        wallTime = TimeSpan.FromSeconds(5);
        reporter.Report(snapshot);

        string line = output.ToString();
        Assert.Contains(
            "[Orleans simulation] runtime=00112233445566778899aabbccddeeff seed=17",
            line,
            StringComparison.Ordinal);
        Assert.Contains("wall=00:00:05", line, StringComparison.Ordinal);
        Assert.Contains("iterations=9 steps=7 timeAdvances=2 consecutiveTimeAdvances=1", line, StringComparison.Ordinal);
        Assert.Contains("simulated=00:10:00", line, StringComparison.Ordinal);
        Assert.Contains("operations=4 runnable=1 waiting=2 blocked=3", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CarriesCountersAndSimulatedTimeAcrossAdaptiveBatches()
    {
        TimeSpan wallTime = TimeSpan.Zero;
        var output = new StringWriter();
        var reporter = new SimulationProgressReporter(
            TimeSpan.FromSeconds(5),
            new SimulationRuntimeIdentity(Guid.Empty, Seed: 1),
            output,
            () => wallTime,
            () => SimulationPendingWorkSummary.Empty,
            () => 0);
        reporter.CompleteBatch(new SimulationExecutionResult(
            SimulationExecutionReason.MaxIterationsReached,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(1),
            iterations: 10,
            stepsExecuted: 8,
            timeAdvanceCount: 2,
            consecutiveTimeAdvanceCount: 0,
            SimulationPendingWorkSummary.Empty,
            new SimulationExecutionLimits(10, TimeSpan.FromMinutes(10), 10_000),
            attemptedTimeAdvance: null));

        wallTime = TimeSpan.FromSeconds(5);
        reporter.Report(new SimulationProgressSnapshot(
            Iterations: 3,
            StepsExecuted: 2,
            TimeAdvanceCount: 1,
            ConsecutiveTimeAdvanceCount: 1,
            StartTime: DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(1),
            CurrentTime: DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(2)));

        string line = output.ToString();
        Assert.Contains("iterations=13 steps=10 timeAdvances=3", line, StringComparison.Ordinal);
        Assert.Contains("simulated=00:02:00", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DriveLoopPublishesProgressAfterACompletedIteration()
    {
        var ran = false;
        var snapshots = new List<SimulationProgressSnapshot>();
        var loop = new SimulationDriveLoop(
            () => DateTimeOffset.UnixEpoch,
            _ =>
            {
                ran = true;
                return true;
            },
            () => null,
            _ => { },
            () => SimulationPendingWorkSummary.Empty,
            () => false,
            () => false);

        SimulationExecutionResult result = loop.Execute(new SimulationDriveLoopOptions(
            Condition: () => ran,
            MaxSimulatedTimeAdvance: TimeSpan.FromMinutes(1),
            MaxIterations: 10,
            MaxConsecutiveTimeAdvances: 10,
            ObserveTeardownCancellation: false,
            InitialConsecutiveTimeAdvances: 0,
            EndTime: null,
            CancellationToken: TestContext.Current.CancellationToken,
            Progress: snapshots.Add));

        SimulationProgressSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(1, snapshot.Iterations);
        Assert.Equal(1, snapshot.StepsExecuted);
        Assert.Equal(0, snapshot.TimeAdvanceCount);
        Assert.Equal(SimulationExecutionReason.ConditionMet, result.Reason);
    }
}
