using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for <see cref="global::System.Runtime.CompilerServices.YieldAwaitable"/> - the
/// value produced by <c>Task.Yield()</c>. Awaiting it always suspends (its awaiter reports
/// <see cref="ControlledYieldAwaiter.IsCompleted"/> as <see langword="false"/>) and then resumes as
/// immediately-runnable work on the simulation's logical thread, giving other logical operations a
/// deterministic chance to run first. This is the controlled analogue of a real yield's "post the
/// continuation back asynchronously", but ordered by the coordinator instead of a
/// <see cref="global::System.Threading.SynchronizationContext"/> or the thread pool.
/// </summary>
public readonly struct ControlledYieldAwaitable
{
    /// <summary>Gets the awaiter for this yield awaitable.</summary>
    /// <returns>A <see cref="ControlledYieldAwaiter"/>.</returns>
    public ControlledYieldAwaiter GetAwaiter() =>
        (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Yield"), default(ControlledYieldAwaiter)).Item2;
}

/// <summary>The awaiter for <see cref="ControlledYieldAwaitable"/>.</summary>
public readonly struct ControlledYieldAwaiter : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.Task.Yield";

    /// <summary>
    /// Always <see langword="false"/> so that awaiting a yield always suspends and reschedules the
    /// continuation, exactly like the real yield awaiter.
    /// </summary>
    public bool IsCompleted => (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName), false).Item2;

    /// <summary>Completes the yield. A no-op, as a yield produces no result and cannot fault.</summary>
    public void GetResult()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
    }

    /// <inheritdoc />
    public void OnCompleted(Action continuation) =>
        SimulationTaskRuntime.ScheduleYield(continuation, ApiName, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) =>
        SimulationTaskRuntime.ScheduleYield(continuation, ApiName, flowExecutionContext: false);
}
