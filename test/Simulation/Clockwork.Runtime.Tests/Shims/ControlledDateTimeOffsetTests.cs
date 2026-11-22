using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

public sealed class ControlledDateTimeOffsetTests
{
    [Fact]
    public void UtcNowHasZeroOffset()
    {
        var clock = ShimTestHarness.CreateClock(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var environment = ShimTestHarness.CreateEnvironment(clock);

        var now = ShimTestHarness.RunInSimulation(environment, ControlledDateTimeOffset.GetUtcNow);

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.Equal(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero), now);
    }

    [Fact]
    public void NowCarriesTheLocalZoneOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-minus3",
            TimeSpan.FromHours(-3),
            "test-3",
            "test-3");
        var clock = ShimTestHarness.CreateClock(new DateTimeOffset(2030, 5, 6, 7, 0, 0, TimeSpan.Zero));
        var environment = ShimTestHarness.CreateEnvironment(clock, localTimeZone: zone);

        var now = ShimTestHarness.RunInSimulation(environment, ControlledDateTimeOffset.GetNow);

        Assert.Equal(TimeSpan.FromHours(-3), now.Offset);
        Assert.Equal(new DateTimeOffset(2030, 5, 6, 4, 0, 0, TimeSpan.FromHours(-3)), now);
    }
}
