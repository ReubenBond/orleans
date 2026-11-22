using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

public sealed class ControlledDateTimeTests
{
    [Fact]
    public void UtcNowReturnsVirtualUtcWithUtcKind()
    {
        var clock = ShimTestHarness.CreateClock(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var environment = ShimTestHarness.CreateEnvironment(clock);

        var now = ShimTestHarness.RunInSimulation(environment, ControlledDateTime.GetUtcNow);

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.Equal(new DateTime(2030, 5, 6, 7, 8, 9, DateTimeKind.Utc), now);
    }

    [Fact]
    public void NowReturnsVirtualLocalWithLocalKind()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus5",
            TimeSpan.FromHours(5),
            "test+5",
            "test+5");
        var clock = ShimTestHarness.CreateClock(new DateTimeOffset(2030, 5, 6, 7, 0, 0, TimeSpan.Zero));
        var environment = ShimTestHarness.CreateEnvironment(clock, localTimeZone: zone);

        var now = ShimTestHarness.RunInSimulation(environment, ControlledDateTime.GetNow);

        Assert.Equal(DateTimeKind.Local, now.Kind);
        Assert.Equal(new DateTime(2030, 5, 6, 12, 0, 0), now);
    }

    [Fact]
    public void TodayReturnsVirtualLocalDateAtMidnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-plus5",
            TimeSpan.FromHours(5),
            "test+5",
            "test+5");
        var clock = ShimTestHarness.CreateClock(new DateTimeOffset(2030, 5, 6, 21, 0, 0, TimeSpan.Zero));
        var environment = ShimTestHarness.CreateEnvironment(clock, localTimeZone: zone);

        var today = ShimTestHarness.RunInSimulation(environment, ControlledDateTime.GetToday);

        Assert.Equal(new DateTime(2030, 5, 7), today);
        Assert.Equal(TimeSpan.Zero, today.TimeOfDay);
        Assert.Equal(DateTimeKind.Local, today.Kind);
    }

    [Fact]
    public void OutsideSimulationFailsBeforeReadingTheRealClock()
    {
        Assert.False(Orleans.Testing.Simulation.Runtime.Execution.SimulationExecutionContext.IsActive);
        DateTime result = DateTime.MinValue;

        Exception? exception = Record.Exception(() => result = ControlledDateTime.GetUtcNow());

        Assert.Equal(DateTime.MinValue, result);
        SimulationNotActiveExceptionAssert.Equal(exception, "System.DateTime.UtcNow");
    }

    [Fact]
    public void ReplayWithSameClockProducesIdenticalReadings()
    {
        DateTime Read()
        {
            var clock = ShimTestHarness.CreateClock();
            var environment = ShimTestHarness.CreateEnvironment(clock);
            return ShimTestHarness.RunInSimulation(environment, () =>
            {
                clock.Advance(TimeSpan.FromMinutes(90));
                return ControlledDateTime.GetUtcNow();
            });
        }

        Assert.Equal(Read(), Read());
    }
}
