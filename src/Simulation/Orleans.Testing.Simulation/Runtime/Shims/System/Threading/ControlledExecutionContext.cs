using System.Runtime.Serialization;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>Controlled rewrite targets for the public <see cref="ExecutionContext"/> surface.</summary>
public static class ControlledExecutionContext
{
    private const string CaptureApi = "System.Threading.ExecutionContext.Capture";
    private const string RunApi = "System.Threading.ExecutionContext.Run";
    private const string SuppressFlowApi = "System.Threading.ExecutionContext.SuppressFlow";
    private const string RestoreFlowApi = "System.Threading.ExecutionContext.RestoreFlow";
    private const string IsFlowSuppressedApi = "System.Threading.ExecutionContext.IsFlowSuppressed";
    private const string RestoreApi = "System.Threading.ExecutionContext.Restore";
    private const string CreateCopyApi = "System.Threading.ExecutionContext.CreateCopy";
    private const string DisposeApi = "System.Threading.ExecutionContext.Dispose";
    private const string GetObjectDataApi = "System.Threading.ExecutionContext.GetObjectData";

    /// <summary>Captures the caller's BCL execution context.</summary>
    public static ExecutionContext? Capture()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CaptureApi);
        return ExecutionContext.Capture();
    }

    /// <summary>
    /// Runs a callback under a captured BCL context while preserving the current controlled strand.
    /// </summary>
    public static void Run(ExecutionContext executionContext, ContextCallback callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(RunApi);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(callback);
        SimulationTaskRuntime.RunWithCapturedExecutionContext(executionContext, () => callback(state));
    }

    /// <summary>Suppresses BCL execution-context flow.</summary>
    public static AsyncFlowControl SuppressFlow()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SuppressFlowApi);
        return ExecutionContext.SuppressFlow();
    }

    /// <summary>Restores BCL execution-context flow.</summary>
    public static void RestoreFlow()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(RestoreFlowApi);
        ExecutionContext.RestoreFlow();
    }

    /// <summary>Gets whether BCL execution-context flow is currently suppressed.</summary>
    public static bool IsFlowSuppressed()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(IsFlowSuppressedApi);
        return ExecutionContext.IsFlowSuppressed();
    }

    /// <summary>
    /// Restores a BCL execution context and re-applies the current controlled strand identity, which is
    /// intentionally not part of user-visible execution-context flow.
    /// </summary>
    public static void Restore(ExecutionContext executionContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(RestoreApi);
        ArgumentNullException.ThrowIfNull(executionContext);
        SimulationExecutionSnapshot snapshot = SimulationExecutionContext.Current
            ?? throw new InvalidOperationException("A controlled execution context requires an active simulation.");
        long strandId = SimulationSynchronizationFlow.CurrentId;
        ExecutionContext.Restore(executionContext);
        SimulationExecutionContext.RestoreSnapshot(snapshot);
        SimulationSynchronizationFlow.RestoreCurrentId(strandId);
    }

    /// <summary>Creates a BCL copy of an execution context.</summary>
    public static ExecutionContext CreateCopy(ExecutionContext instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CreateCopyApi);
        ArgumentNullException.ThrowIfNull(instance);
        return instance.CreateCopy();
    }

    /// <summary>Disposes an execution context using the BCL implementation.</summary>
    public static void Dispose(ExecutionContext instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(DisposeApi);
        ArgumentNullException.ThrowIfNull(instance);
        instance.Dispose();
    }

    /// <summary>Rejects the legacy serialization member before it can invoke BCL serialization behavior.</summary>
    [Obsolete("ExecutionContext serialization is legacy. This member exists only for rewritten legacy callers.")]
    public static void GetObjectData(ExecutionContext instance, SerializationInfo info, StreamingContext context)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GetObjectDataApi);
        throw new SimulationApiException(
            SimulationApiCategory.ExecutionContext,
            GetObjectDataApi,
            "legacy ExecutionContext serialization can invoke uncontrolled BCL serialization behavior.");
    }
}
