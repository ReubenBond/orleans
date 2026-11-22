using System.ComponentModel;
using System.Diagnostics;

namespace Orleans.Testing.Simulation.Shims.System.Diagnostics;

/// <summary>Controlled rewrite targets for <see cref="Stopwatch"/> timestamp APIs.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ControlledStopwatch
{
    /// <summary>Controlled replacement for <see cref="Stopwatch.GetTimestamp"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static long GetTimestamp()
    {
        var (_, environment, node) =
            SimulationRuntimeDispatch.RequireEnvironment("System.Diagnostics.Stopwatch.GetTimestamp");
        return ToStopwatchTimestamp(environment.GetTimestamp(node));
    }

    /// <summary>Controlled replacement for <see cref="Stopwatch.GetElapsedTime(long)"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TimeSpan GetElapsedTime(long startingTimestamp)
    {
        var (_, environment, node) =
            SimulationRuntimeDispatch.RequireEnvironment("System.Diagnostics.Stopwatch.GetElapsedTime");
        return Stopwatch.GetElapsedTime(
            startingTimestamp,
            ToStopwatchTimestamp(environment.GetTimestamp(node)));
    }

    private static long ToStopwatchTimestamp(long virtualTicks) =>
        checked((long)((decimal)virtualTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond));
}
