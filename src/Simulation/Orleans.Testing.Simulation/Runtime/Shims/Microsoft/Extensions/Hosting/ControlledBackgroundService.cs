using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.Microsoft.Extensions.Hosting;

/// <summary>
/// Rejection of <see cref="global::Microsoft.Extensions.Hosting.BackgroundService.StartAsync"/> where rewritten code calls
/// it, directly or as an override's base call. The BCL starts <c>ExecuteAsync</c>, the application's own work, with the
/// real <c>Task.Run</c>, so it runs on the thread pool outside the simulation's coordinator; the task it returns is kept
/// in a private field that <c>ExecuteTask</c> and <c>StopAsync</c> read, so a shim cannot start the work elsewhere with
/// the BCL's semantics. A call through <c>IHostedService</c>, as a host makes, names the interface rather than this
/// member, so it stays a BCL call, whose work the thread-affinity check catches at its first controlled call.
/// </summary>
public static class ControlledBackgroundService
{
    /// <summary>Rejection injected before a <c>BackgroundService.StartAsync</c> call.</summary>
    /// <param name="apiName">The rejected API, supplied by the rewriter.</param>
    public static void RejectUnsupported(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Task,
            apiName,
            "BackgroundService.StartAsync starts ExecuteAsync with the real Task.Run, outside the coordinator; implement " +
            "IHostedService in the system under test and start the work with Task.Run, which runs it as a controlled " +
            "operation.");
    }
}
