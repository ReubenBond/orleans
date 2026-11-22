namespace Orleans.Durable.Storage.Engine;

/// <summary>
/// The bounded, jittered, exponential backoff of every repetition in Durable Storage (conventions.md §6): the wait before repetition
/// <c>attempt</c> lies between its step, the first wait doubling with each attempt up to the longest, and twice the step,
/// and never exceeds the longest, so no wait is shorter than the first. A repeater which counts attempts from 0 passes
/// its own count.
/// </summary>
/// <remarks>
/// <para>
/// Polly's own jittered exponential backoff may draw a wait of zero, about once in 840 first repetitions for a first
/// wait of 20 ms, after which the operation repeats at once, without arming a timer (#335); each repeater takes its
/// waits from <see cref="Delay"/> instead.
/// </para>
/// <para>
/// The caller draws the jitter, as <c>Random.Shared.NextDouble()</c>, which a simulation's rewrite of the caller makes
/// deterministic; the wait itself is arithmetic over the draw.
/// </para>
/// </remarks>
public static class Backoff
{
    /// <summary>Returns the wait before repetition <paramref name="attempt"/>.</summary>
    /// <param name="attempt">The repetition's number, from 0.</param>
    /// <param name="first">The first step, which is the shortest wait; positive.</param>
    /// <param name="longest">The longest wait; at least <paramref name="first"/>.</param>
    /// <param name="jitter">The caller's draw, from 0 to below 1, which places the wait between the step and twice it.</param>
    /// <returns>A wait from the step to twice the step, at most <paramref name="longest"/>.</returns>
    public static TimeSpan Delay(int attempt, TimeSpan first, TimeSpan longest, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(first, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(longest, first);
        if (jitter is not (>= 0 and < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), jitter, "A jitter lies from 0 to below 1.");
        }

        var step = first.Ticks;
        for (var doubling = 0; doubling < attempt && step < longest.Ticks; doubling++)
        {
            step = step > longest.Ticks / 2 ? longest.Ticks : step * 2;
        }

        step = Math.Min(step, longest.Ticks);
        return TimeSpan.FromTicks(Math.Min(step + (long)(jitter * step), longest.Ticks));
    }
}
