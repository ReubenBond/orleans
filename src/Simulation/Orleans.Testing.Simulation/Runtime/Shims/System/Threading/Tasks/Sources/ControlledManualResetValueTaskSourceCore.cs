using System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Sources;

/// <summary>
/// Controlled completion members of <see cref="ManualResetValueTaskSourceCore{TResult}"/>. Each completes the core
/// through the BCL member, as <see cref="SimulationCompletion"/> runs a completion, so a continuation which the core runs
/// inline, or a task which the BCL completes over it (<c>AsTask</c>), runs on the simulation's logical thread. As the
/// <c>TaskCompletionSource</c> completion shims do, they pass through to the BCL member outside a simulation, and inside
/// one require its logical thread.
/// </summary>
public static class ControlledManualResetValueTaskSourceCore
{
    private const string Api = "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1";

    /// <summary>Controlled <c>ManualResetValueTaskSourceCore&lt;TResult&gt;.SetResult(TResult)</c>.</summary>
    /// <typeparam name="TResult">The core's result type.</typeparam>
    /// <param name="core">The receiving core.</param>
    /// <param name="result">The result.</param>
    public static void SetResult<TResult>(ref ManualResetValueTaskSourceCore<TResult> core, TResult result)
    {
        SimulationRuntimeDispatch.RequireSimulationThreadIfActive(Api, nameof(SetResult));
        SimulationCompletion.Run(
            ref core,
            result,
            static (ref ManualResetValueTaskSourceCore<TResult> core, TResult result) => core.SetResult(result),
            Api);
    }

    /// <summary>Controlled <c>ManualResetValueTaskSourceCore&lt;TResult&gt;.SetException(Exception)</c>.</summary>
    /// <typeparam name="TResult">The core's result type.</typeparam>
    /// <param name="core">The receiving core.</param>
    /// <param name="error">The exception.</param>
    public static void SetException<TResult>(ref ManualResetValueTaskSourceCore<TResult> core, Exception error)
    {
        SimulationRuntimeDispatch.RequireSimulationThreadIfActive(Api, nameof(SetException));
        SimulationCompletion.Run(
            ref core,
            error,
            static (ref ManualResetValueTaskSourceCore<TResult> core, Exception error) => core.SetException(error),
            Api);
    }
}
