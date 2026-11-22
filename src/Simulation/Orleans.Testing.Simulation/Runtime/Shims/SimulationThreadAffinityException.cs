using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Shims;

/// <summary>
/// Thrown when a controlled rewrite target runs with a simulation's ambient context on a thread other than that
/// simulation's logical thread: one of its scheduler's operation threads, or the thread controlling the scheduler
/// (see <see cref="Scheduling.SimulationScheduler.IsSimulationThread"/>). The ambient context flows with the
/// <see cref="ExecutionContext"/>, so work which code Clockwork did not rewrite moves onto a real thread carries it
/// along: <c>BackgroundService.StartAsync</c> in the .NET shared framework starting <c>ExecuteAsync</c> through the real
/// <c>Task.Run</c>, or a timer, thread-pool item, or continuation queued by an uninstrumented library. Such work races the
/// simulation, so the controlled API rejects it before doing anything, and the scheduler also surfaces the violation
/// on the thread driving it, in case the escaped work swallows this exception.
/// </summary>
public sealed class SimulationThreadAffinityException : InvalidOperationException
{
    private const string Remedy =
        "The thread carries the simulation's ambient context, so work escaped onto a real thread through code Clockwork " +
        "did not rewrite - for example the real Task.Run with which BackgroundService.StartAsync starts ExecuteAsync, or a " +
        "timer, thread-pool item, or continuation queued by an uninstrumented library - and races the simulation. " +
        "Start that work from instrumented code so it runs as a controlled operation: BackgroundService loads " +
        "uninstrumented from the shared framework, so implement IHostedService in the system under test instead. Host " +
        "code which runs simulation callbacks itself, such as cancelling a token the system under test registered on, " +
        "runs them inside SimulationScheduler.EnterControlScope().";

    /// <summary>Initializes a new instance for the controlled API that ran off the simulation's logical thread.</summary>
    /// <param name="apiName">The rewritten BCL API name.</param>
    /// <param name="runtime">The simulation runtime whose ambient context the calling thread carries.</param>
    internal SimulationThreadAffinityException(string apiName, SimulationRuntimeIdentity runtime)
        : this(apiName, runtime, Environment.CurrentManagedThreadId)
    {
    }

    private SimulationThreadAffinityException(string apiName, SimulationRuntimeIdentity runtime, int managedThreadId)
        : base(FormatMessage(apiName, runtime, managedThreadId))
    {
        ApiName = apiName;
        Runtime = runtime;
        ManagedThreadId = managedThreadId;
    }

    /// <summary>Gets the rewritten BCL API name.</summary>
    public string ApiName { get; }

    /// <summary>Gets the simulation runtime whose ambient context the calling thread carried.</summary>
    public SimulationRuntimeIdentity Runtime { get; }

    /// <summary>Gets the managed id of the thread which called the controlled API.</summary>
    public int ManagedThreadId { get; }

    // The message depends on the arguments, so they are validated before the base constructor formats it.
    private static string FormatMessage(string apiName, SimulationRuntimeIdentity runtime, int managedThreadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        ArgumentNullException.ThrowIfNull(runtime);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{apiName} ran on managed thread {managedThreadId}, off the logical thread of simulation runtime '{runtime.Id}' (seed {runtime.Seed}). {Remedy}");
    }
}
