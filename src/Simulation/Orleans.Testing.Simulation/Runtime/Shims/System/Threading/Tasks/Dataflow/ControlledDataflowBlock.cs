using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Dataflow;

/// <summary>
/// Rejection of the TPL Dataflow entry points: the constructor of every block, and the
/// <see cref="global::System.Threading.Tasks.Dataflow.DataflowBlock"/> helpers which schedule work or wait (<c>Choose</c>,
/// <c>Receive</c>, <c>ReceiveAsync</c>, <c>ReceiveAllAsync</c>, <c>SendAsync</c>, and <c>OutputAvailableAsync</c>). A block
/// runs its delegates and message propagation on tasks of its options' <c>TaskScheduler</c>, but it also wires
/// cancellation and completion propagation through continuations on <c>TaskScheduler.Default</c> whatever the options
/// say, and the helpers time out on real timers. Replacing the options' scheduler would therefore leave work on the thread
/// pool, so the rewriter injects this rejection before each call site instead.
/// </summary>
public static class ControlledDataflowBlock
{
    /// <summary>Rejection injected before a Dataflow entry point.</summary>
    /// <param name="apiName">The rejected API, supplied by the rewriter.</param>
    public static void RejectUnsupported(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Task,
            apiName,
            "TPL Dataflow runs block work and completion propagation on tasks of TaskScheduler.Default and times out on " +
            "real timers, outside the coordinator; use System.Threading.Channels with Task.Run or Parallel.ForEachAsync, " +
            "which run on controlled operations.");
    }
}
