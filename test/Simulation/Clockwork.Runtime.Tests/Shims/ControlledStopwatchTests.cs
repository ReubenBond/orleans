using System.Diagnostics;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

public sealed class ControlledStopwatchTests
{
    [Fact]
    public void TimestampAndElapsedTimeMeasureVirtualDuration()
    {
        var clock = ShimTestHarness.CreateClock();
        var environment = ShimTestHarness.CreateEnvironment(clock);

        var elapsed = ShimTestHarness.RunInSimulation(environment, () =>
        {
            var start = ControlledStopwatch.GetTimestamp();
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            return ControlledStopwatch.GetElapsedTime(start);
        });

        Assert.Equal(TimeSpan.FromMilliseconds(1500), elapsed);
    }

    [Fact]
    public void TimestampAdvancesInStopwatchFrequencyUnits()
    {
        var clock = ShimTestHarness.CreateClock();
        var environment = ShimTestHarness.CreateEnvironment(clock);

        var (origin, afterOneSecond) = ShimTestHarness.RunInSimulation(environment, () =>
        {
            long start = ControlledStopwatch.GetTimestamp();
            clock.Advance(TimeSpan.FromSeconds(1));
            return (start, ControlledStopwatch.GetTimestamp());
        });

        Assert.Equal(0L, origin);
        Assert.Equal(Stopwatch.Frequency, afterOneSecond - origin);
    }

    [Fact]
    public void ElapsedTimeConvertsStopwatchFrequencyDelta()
    {
        var clock = ShimTestHarness.CreateClock();
        var environment = ShimTestHarness.CreateEnvironment(clock);
        var duration = TimeSpan.FromSeconds(3);

        var (start, end, elapsed) = ShimTestHarness.RunInSimulation(environment, () =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            long startingTimestamp = ControlledStopwatch.GetTimestamp();
            clock.Advance(duration);
            long endingTimestamp = ControlledStopwatch.GetTimestamp();
            return (
                startingTimestamp,
                endingTimestamp,
                ControlledStopwatch.GetElapsedTime(startingTimestamp));
        });

        Assert.Equal(checked(2L * Stopwatch.Frequency), start);
        Assert.Equal(checked(3L * Stopwatch.Frequency), end - start);
        Assert.Equal(duration, elapsed);
    }

}
