using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// Controlled rewrite targets for <see cref="SynchronizationContext"/>.
/// </summary>
/// <remarks>
/// The ambient context is logical rather than physical-thread state. Posting is always routed through
/// the simulation coordinator, including for a custom context: invoking a custom context's base
/// implementation could otherwise put work on the physical thread pool.
/// </remarks>
public static class ControlledSynchronizationContext
{
    private const string CurrentApi = "System.Threading.SynchronizationContext.get_Current";
    private const string SetApi = "System.Threading.SynchronizationContext.SetSynchronizationContext";
    private const string CreateCopyApi = "System.Threading.SynchronizationContext.CreateCopy";
    private const string IsWaitNotificationRequiredApi = "System.Threading.SynchronizationContext.IsWaitNotificationRequired";
    private const string OperationStartedApi = "System.Threading.SynchronizationContext.OperationStarted";
    private const string OperationCompletedApi = "System.Threading.SynchronizationContext.OperationCompleted";
    private const string PostApi = "System.Threading.SynchronizationContext.Post";
    private const string SendApi = "System.Threading.SynchronizationContext.Send";
    private const string WaitApi = "System.Threading.SynchronizationContext.Wait";

    private sealed class RuntimeContexts
    {
        public Dictionary<long, SynchronizationContext> ByStrand { get; } = [];
    }

    // SimulationRuntimeIdentity instances are lifecycle objects. An ephemeron registry lets a completed
    // runtime and every context installed beneath it be collected without retaining a Guid-keyed global map.
    private static readonly ConditionalWeakTable<SimulationRuntimeIdentity, RuntimeContexts> Contexts = new();

    /// <summary>Gets the synchronization context installed for the current logical execution.</summary>
    public static SynchronizationContext? Current()
    {
        var snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(CurrentApi);
        return Contexts.GetValue(snapshot.Runtime, static _ => new RuntimeContexts()).ByStrand
            .TryGetValue(SimulationSynchronizationFlow.CurrentId, out var context)
            ? context
            : null;
    }

    /// <summary>Installs a synchronization context for the current logical execution.</summary>
    public static void SetSynchronizationContext(SynchronizationContext? syncContext)
    {
        var snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(SetApi);
        var contexts = Contexts.GetValue(snapshot.Runtime, static _ => new RuntimeContexts()).ByStrand;
        long strandId = SimulationSynchronizationFlow.CurrentId;
        if (syncContext is null)
        {
            contexts.Remove(strandId);
        }
        else
        {
            contexts[strandId] = syncContext;
        }
    }

    /// <summary>Calls <see cref="SynchronizationContext.CreateCopy"/> on the supplied context.</summary>
    public static SynchronizationContext CreateCopy(SynchronizationContext instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(CreateCopyApi);
        ArgumentNullException.ThrowIfNull(instance);
        return instance.CreateCopy();
    }

    /// <summary>Calls <see cref="SynchronizationContext.IsWaitNotificationRequired"/> on the supplied context.</summary>
    public static bool IsWaitNotificationRequired(SynchronizationContext instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(IsWaitNotificationRequiredApi);
        ArgumentNullException.ThrowIfNull(instance);
        return instance.IsWaitNotificationRequired();
    }

    /// <summary>Calls <see cref="SynchronizationContext.OperationStarted"/> on the supplied context.</summary>
    public static void OperationStarted(SynchronizationContext instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OperationStartedApi);
        ArgumentNullException.ThrowIfNull(instance);
        instance.OperationStarted();
    }

    /// <summary>Calls <see cref="SynchronizationContext.OperationCompleted"/> on the supplied context.</summary>
    public static void OperationCompleted(SynchronizationContext instance)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(OperationCompletedApi);
        ArgumentNullException.ThrowIfNull(instance);
        instance.OperationCompleted();
    }

    /// <summary>
    /// Queues a callback on the controlled scheduler. Custom context overrides are deliberately not
    /// invoked because their base implementation can dispatch work to the physical thread pool.
    /// </summary>
    public static void Post(SynchronizationContext instance, SendOrPostCallback callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(PostApi);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(callback);
        SimulationTaskRuntime.QueueWork(() => callback(state), PostApi, flowExecutionContext: true);
    }

    /// <summary>
    /// Runs a callback synchronously on the current logical thread. Custom context dispatch is not
    /// invoked, so <c>Send</c> can never introduce an OS wait or dispatch.
    /// </summary>
    public static void Send(SynchronizationContext instance, SendOrPostCallback callback, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(SendApi);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(callback);
        callback(state);
    }

    /// <summary>
    /// Rejects raw-handle waits before a physical thread can block.
    /// </summary>
    /// <returns>Never returns.</returns>
    public static int Wait(SynchronizationContext instance, IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(waitHandles);
        throw new SimulationApiException(
            SimulationApiCategory.SynchronizationContext,
            WaitApi,
            "raw OS wait handles cannot be waited through a synchronization context inside the deterministic scheduler.");
    }
}
