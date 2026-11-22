using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// The configured limits that were in effect for a single drive-loop execution, captured
/// alongside a <see cref="SimulationExecutionResult"/> so callers can tell how close (or how far
/// past) a limit the execution got without needing to remember the arguments they passed in.
/// </summary>
/// <param name="MaxIterations">The maximum number of loop iterations that were allowed.</param>
/// <param name="MaxSimulatedTimeAdvance">The maximum simulated-time gap that could be jumped in a single advance.</param>
/// <param name="MaxConsecutiveTimeAdvances">The maximum number of consecutive time advances allowed without executed work.</param>
public sealed record SimulationExecutionLimits(int MaxIterations, TimeSpan MaxSimulatedTimeAdvance, int MaxConsecutiveTimeAdvances)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"MaxIterations={MaxIterations}, MaxSimulatedTimeAdvance={MaxSimulatedTimeAdvance}, MaxConsecutiveTimeAdvances={MaxConsecutiveTimeAdvances}");
}
