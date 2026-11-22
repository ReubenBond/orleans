using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.ComponentModel;

/// <summary>
/// Rejection of <see cref="global::System.ComponentModel.BackgroundWorker.RunWorkerAsync()"/> and its overload with an
/// argument. The BCL raises <c>DoWork</c>, the application's own work, on a task it starts with
/// <c>Task.Factory.StartNew(..., TaskScheduler.Default)</c>, so the work runs on the thread pool outside the simulation's
/// coordinator. A private method raises <c>DoWork</c> and holds the operation's state, so a shim cannot run the work
/// elsewhere with the BCL's semantics; the rewriter injects this rejection before each call site instead.
/// </summary>
public static class ControlledBackgroundWorker
{
    /// <summary>Rejection injected before a <c>RunWorkerAsync</c> call.</summary>
    /// <param name="apiName">The rejected API, supplied by the rewriter.</param>
    public static void RejectUnsupported(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Task,
            apiName,
            "BackgroundWorker raises DoWork on a task of TaskScheduler.Default, outside the coordinator; run the work with " +
            "Task.Run, which runs it as a controlled operation, and raise the progress and completion events from it.");
    }
}
