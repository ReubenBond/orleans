using System.Collections.Concurrent;

namespace Orleans.Testing.Simulation.Runtime.Execution;

/// <summary>
/// A single recorded call to <see cref="SimulationExecutionContext.SuppressFlow(string)"/>.
/// </summary>
/// <param name="Reason">The reason supplied by the caller.</param>
/// <param name="CapturedContext">
/// The ambient simulation context that was active immediately before flow was suppressed, or
/// <see langword="null"/> if no simulation was active at that point.
/// </param>
/// <param name="TimestampUtc">The wall-clock time the suppression was recorded. This is a
/// diagnostics timestamp only - it is deliberately wall-clock (not simulated) time, since flow
/// suppression is itself an escape from the simulation's controlled execution.</param>
public sealed record SimulationFlowSuppressionEvent(
    string Reason,
    SimulationExecutionSnapshot? CapturedContext,
    DateTimeOffset TimestampUtc);

/// <summary>
/// <para>
/// A small, thread-safe, bounded ring buffer of the most recent
/// <see cref="SimulationFlowSuppressionEvent"/>s recorded via
/// <see cref="SimulationExecutionContext.SuppressFlow(string)"/>, process-wide. This exists purely
/// for diagnostics: when <see cref="SimulationExternalEntryGuard"/> (or a developer) needs to
/// explain why ambient simulation context unexpectedly went missing on some callback, this buffer
/// can be consulted to see whether a deliberate suppression happened recently, rather than
/// concluding the loss was necessarily a bug.
/// </para>
/// <para>
/// This is intentionally not keyed per-runtime or per-thread: flow suppression, by definition,
/// happens exactly at the point where per-thread/per-context tracking would otherwise stop
/// working, so a single small global buffer (bounded at <see cref="Capacity"/> entries) is the
/// simplest correct design. It is not a substitute for real logging/tracing.
/// </para>
/// </summary>
public static class SimulationFlowSuppressionDiagnostics
{
    /// <summary>
    /// The maximum number of recent suppression events retained. Older events are evicted in
    /// insertion order once this many are stored.
    /// </summary>
    public const int Capacity = 64;

    private static readonly ConcurrentQueue<SimulationFlowSuppressionEvent> Events = new();

    /// <summary>
    /// Records a suppression event, evicting the oldest entry if <see cref="Capacity"/> would
    /// otherwise be exceeded.
    /// </summary>
    /// <param name="flowSuppressionEvent">The event to record.</param>
    public static void Record(SimulationFlowSuppressionEvent flowSuppressionEvent)
    {
        ArgumentNullException.ThrowIfNull(flowSuppressionEvent);
        Events.Enqueue(flowSuppressionEvent);
        while (Events.Count > Capacity && Events.TryDequeue(out _))
        {
            // Keep trimming until back within capacity; a concurrent Record() may race this loop,
            // which is fine - the bound is "approximately Capacity", not exact.
        }
    }

    /// <summary>
    /// Returns a snapshot of the currently-recorded suppression events, oldest first.
    /// </summary>
    public static IReadOnlyList<SimulationFlowSuppressionEvent> GetRecentEvents() => [.. Events];

    /// <summary>
    /// Clears all recorded events. Intended for test isolation between test cases that assert on
    /// this diagnostic buffer's contents.
    /// </summary>
    public static void Clear()
    {
        while (Events.TryDequeue(out _))
        {
        }
    }
}
