using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

public sealed class ControlledEnvironmentTests
{
    [Fact]
    public void CurrentManagedThreadIdUsesTheSimulationLogicalThread()
    {
        var environment = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var id = ShimTestHarness.RunInSimulation(
            environment,
            ControlledEnvironment.GetCurrentManagedThreadId);

        Assert.Equal(SimulationScheduler.SimulationLogicalThreadOwnerId, id);
    }

    [Fact]
    public void TickCount64IsVirtualMillisecondsSinceOrigin()
    {
        var clock = ShimTestHarness.CreateClock();
        var environment = ShimTestHarness.CreateEnvironment(clock);

        var ticks = ShimTestHarness.RunInSimulation(environment, () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(4242));
            return ControlledEnvironment.GetTickCount64();
        });

        Assert.Equal(4242L, ticks);
    }

    [Fact]
    public void TickCountWrapsLikeTheLow32BitsOfTickCount64()
    {
        var clock = ShimTestHarness.CreateClock();
        var environment = ShimTestHarness.CreateEnvironment(clock);
        var wrapMilliseconds = (long)int.MaxValue + 5;

        var (tick32, tick64) = ShimTestHarness.RunInSimulation(environment, () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(wrapMilliseconds));
            return (ControlledEnvironment.GetTickCount(), ControlledEnvironment.GetTickCount64());
        });

        Assert.Equal(wrapMilliseconds, tick64);
        Assert.Equal(unchecked((int)wrapMilliseconds), tick32);
        Assert.True(tick32 < 0);
    }
}
