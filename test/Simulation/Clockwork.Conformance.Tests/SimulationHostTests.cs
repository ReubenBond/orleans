using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>The conformance host's watchdog: a probe which blocks the logical thread fails its test instead of hanging.</summary>
public sealed class SimulationHostTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADriveWhichBlocksTheLogicalThreadFailsWithAMessage()
    {
        using var gate = new ManualResetEventSlim();
        using var host = new SimulationHost(Start, hangBound: TimeSpan.FromSeconds(1));
        try
        {
            var exception = Assert.Throws<TimeoutException>(() => host.Invoke(BlockMethod, gate));

            Assert.Contains("blocked the simulation's logical thread", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            // Releases the abandoned drive, so its thread finishes rather than outliving the test.
            gate.Set();
        }
    }

    private static MethodInfo BlockMethod { get; } =
        typeof(SimulationHostTests).GetMethod(nameof(Block), BindingFlags.NonPublic | BindingFlags.Static)!;

    // Uninstrumented, so the wait is the BCL's: it blocks the logical thread without yielding to the scheduler.
    private static void Block(ManualResetEventSlim gate) => gate.Wait();
}
