namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>
/// The base type for every exception the controlled async/task machinery raises. Distinguishes a
/// deterministic, diagnosable failure originating in Clockwork's controlled task layer from an
/// ordinary application <see cref="System.Threading.Tasks.Task"/> fault.
/// </summary>
public class SimulationTaskException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SimulationTaskException"/> class.</summary>
    public SimulationTaskException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SimulationTaskException"/> class.</summary>
    /// <param name="message">The diagnostic message.</param>
    public SimulationTaskException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SimulationTaskException"/> class.</summary>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public SimulationTaskException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown by a controlled synchronous wait (<c>task.Wait()</c>, <c>task.Result</c>,
/// <c>Task.WaitAll</c>/<c>WaitAny</c>) when the simulation's ready work is exhausted yet the awaited
/// task has still not completed. Because the simulation runs on a single logical thread that the wait
/// itself is pumping, this is the deterministic, immediately-diagnosable signature of a deadlock rather
/// than a real-time hang.
/// </summary>
public sealed class SimulationSynchronousWaitDeadlockException : SimulationTaskException
{
    /// <summary>Initializes a new instance of the <see cref="SimulationSynchronousWaitDeadlockException"/> class.</summary>
    /// <param name="apiName">The synchronous-wait API that deadlocked.</param>
    public SimulationSynchronousWaitDeadlockException(string apiName)
        : base(
            $"The controlled synchronous wait '{apiName}' cannot complete: the simulation has no more ready " +
            "work to run yet the awaited task is still not complete. In the single-threaded simulation this " +
            "is a deadlock - the work that would complete the task can never run because this blocking wait " +
            "is occupying the only logical thread. Prefer awaiting the task instead of blocking on it.")
    {
        ApiName = apiName;
    }

    /// <summary>Gets the synchronous-wait API that deadlocked.</summary>
    public string? ApiName { get; }
}
