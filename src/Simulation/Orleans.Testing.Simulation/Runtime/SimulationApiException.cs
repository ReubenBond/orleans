namespace Orleans.Testing.Simulation.Runtime;

/// <summary>Identifies the controlled API surface that rejected an unsupported operation.</summary>
public enum SimulationApiCategory
{
    /// <summary>Controlled task and async APIs.</summary>
    Task,

    /// <summary>Controlled thread APIs.</summary>
    Thread,

    /// <summary>Controlled thread-pool APIs.</summary>
    ThreadPool,

    /// <summary>Controlled synchronization-context APIs.</summary>
    SynchronizationContext,

    /// <summary>Controlled execution-context APIs.</summary>
    ExecutionContext,

    /// <summary>Controlled parallel-loop APIs.</summary>
    Parallel,

    /// <summary>Controlled wait-handle, event, mutex, and semaphore APIs.</summary>
    WaitHandle,

    /// <summary>Controlled lightweight semaphore APIs.</summary>
    SemaphoreSlim,

    /// <summary>Controlled lightweight manual-reset-event APIs.</summary>
    ManualResetEventSlim,

    /// <summary>Controlled timer and time-provider APIs.</summary>
    Timer,
}

/// <summary>
/// Thrown when Clockwork cannot model an API faithfully inside a simulation. The structured category,
/// API name, and reason replace API-specific exception classes while preserving actionable diagnostics.
/// </summary>
public sealed class SimulationApiException : InvalidOperationException
{
    /// <summary>Initializes a new unsupported controlled-API failure.</summary>
    /// <param name="category">The controlled API surface.</param>
    /// <param name="apiName">The unsupported API.</param>
    /// <param name="reason">Why Clockwork cannot model the API faithfully.</param>
    public SimulationApiException(SimulationApiCategory category, string apiName, string reason)
        : base($"The {FormatCategory(category)} API '{apiName}' is not supported inside a Clockwork simulation: {reason}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Category = category;
        ApiName = apiName;
        Reason = reason;
    }

    /// <summary>Gets the controlled API surface.</summary>
    public SimulationApiCategory Category { get; }

    /// <summary>Gets the unsupported API.</summary>
    public string ApiName { get; }

    /// <summary>Gets why Clockwork cannot model the API faithfully.</summary>
    public string Reason { get; }

    private static string FormatCategory(SimulationApiCategory category) =>
        category switch
        {
            SimulationApiCategory.ThreadPool => "thread-pool",
            SimulationApiCategory.SynchronizationContext => "synchronization-context",
            SimulationApiCategory.ExecutionContext => "execution-context",
            SimulationApiCategory.WaitHandle => "wait-handle",
            SimulationApiCategory.SemaphoreSlim => "semaphore",
            SimulationApiCategory.ManualResetEventSlim => "ManualResetEventSlim",
            _ => category.ToString().ToLowerInvariant(),
        };
}
