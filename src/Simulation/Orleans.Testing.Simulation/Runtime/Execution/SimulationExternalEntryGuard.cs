namespace Orleans.Testing.Simulation.Runtime.Execution;

/// <summary>
/// Thrown by <see cref="SimulationExternalEntryGuard"/> when a callback is about to execute inside
/// what should be one simulation runtime's ambient scope, but the calling thread already carries
/// ambient context for a <em>different</em> simulation runtime. This always indicates a bug -
/// either two simulations sharing a thread without properly restoring/disposing their scopes, or a
/// callback that escaped one simulation and was invoked directly on a thread that is mid-step for
/// another - so this is thrown eagerly with an actionable message rather than being silently
/// repaired (which would just paper over the contamination) or caught broadly.
/// </summary>
public sealed class SimulationExternalEntryException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationExternalEntryException"/> class.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    public SimulationExternalEntryException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationExternalEntryException"/> class.
    /// </summary>
    public SimulationExternalEntryException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationExternalEntryException"/> class.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SimulationExternalEntryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <para>
/// Detects a callback entering an active simulation's boundary (a scheduler lane about to run an
/// item, a synchronization-context callback, a timer firing) while the calling thread's ambient
/// simulation execution context - if any - belongs to a <em>different</em> simulation runtime than
/// the one about to be entered. This is the "external entry" case: a callback that legitimately
/// belongs to one simulation is somehow executing on a thread that is simultaneously, ambiently,
/// inside another simulation's scope.
/// </para>
/// <para>
/// Deliberately narrow: a calling thread with <em>no</em> ambient context at all (the normal case
/// for e.g. the test thread calling into <c>RunUntil</c> for the first time, or a queue whose
/// owner never opted into ambient-context integration) is not flagged - that is completely normal
/// current behavior and must not be falsely rejected. Only a concrete collision between two
/// different <see cref="SimulationRuntimeIdentity.Id"/> values is treated as an error.
/// </para>
/// </summary>
public static class SimulationExternalEntryGuard
{
    /// <summary>
    /// Validates that the calling thread's ambient simulation context, if any, either matches
    /// <paramref name="expectedRuntime"/> or is entirely absent, before <paramref name="boundaryName"/>
    /// installs its own scope for <paramref name="expectedRuntime"/>.
    /// </summary>
    /// <param name="expectedRuntime">The runtime about to be entered.</param>
    /// <param name="boundaryName">
    /// A short, human-readable name for the boundary performing this check (e.g. the queue or
    /// callback kind), included in the exception message for diagnosability.
    /// </param>
    /// <exception cref="SimulationExternalEntryException">
    /// Thrown if the calling thread already carries ambient context for a different simulation
    /// runtime.
    /// </exception>
    public static void ValidateEntry(SimulationRuntimeIdentity expectedRuntime, string boundaryName)
    {
        ArgumentNullException.ThrowIfNull(expectedRuntime);
        ArgumentException.ThrowIfNullOrEmpty(boundaryName);

        if (!SimulationExecutionContext.TryGetCurrentRuntime(out var ambientRuntime) || ambientRuntime is null)
        {
            // No ambient context at all - the normal, expected case for the very first entry, or
            // for queues/callbacks that never opted into ambient-context integration.
            return;
        }

        if (ambientRuntime.Id == expectedRuntime.Id)
        {
            // Same runtime - this is ordinary re-entrancy (e.g. a nested Send/RunOnce pump), not
            // external entry.
            return;
        }

        throw new SimulationExternalEntryException(BuildDiagnosticMessage(expectedRuntime, ambientRuntime, boundaryName));
    }

    private static string BuildDiagnosticMessage(
        SimulationRuntimeIdentity expectedRuntime,
        SimulationRuntimeIdentity ambientRuntime,
        string boundaryName)
    {
        var suppressionNote = FindRecentSuppressionNote(ambientRuntime);

        return $"External entry detected at '{boundaryName}': the calling thread carries ambient simulation " +
            $"context for runtime '{ambientRuntime.Id}' (seed {ambientRuntime.Seed}), but this boundary belongs " +
            $"to a different runtime '{expectedRuntime.Id}' (seed {expectedRuntime.Seed}). This usually means " +
            "two simulations shared a thread without one properly disposing its ambient scope, or a callback " +
            "captured by one simulation was invoked directly on a thread mid-step for another." + suppressionNote;
    }

    private static string FindRecentSuppressionNote(SimulationRuntimeIdentity ambientRuntime)
    {
        foreach (var suppressionEvent in SimulationFlowSuppressionDiagnostics.GetRecentEvents())
        {
            if (suppressionEvent.CapturedContext?.Runtime.Id == ambientRuntime.Id)
            {
                return $" A flow suppression was recorded for this runtime (reason: '{suppressionEvent.Reason}', " +
                    $"at {suppressionEvent.TimestampUtc:O}); if this callback is expected to re-enter a " +
                    "simulation, install the ambient context explicitly instead of relying on flow.";
            }
        }

        return string.Empty;
    }
}
