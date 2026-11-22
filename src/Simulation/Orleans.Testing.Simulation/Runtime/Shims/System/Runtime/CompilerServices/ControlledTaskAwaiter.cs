using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for <see cref="global::System.Runtime.CompilerServices.TaskAwaiter"/>. A rewritten
/// state machine stores this in place of the real awaiter and the compiler-generated code drives it
/// through the identical surface (<see cref="IsCompleted"/>, <see cref="OnCompleted"/> /
/// <see cref="UnsafeOnCompleted"/>, <see cref="GetResult"/>). The only behavioural change is where the
/// continuation goes: instead of the awaited task's real completion callback (which may run inline or on
/// the thread pool, and honours a captured <see cref="global::System.Threading.SynchronizationContext"/>), the
/// continuation is handed to the ambient simulation scheduler so it always resumes
/// on the simulation's single logical thread.
/// </summary>
public readonly struct ControlledTaskAwaiter : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.Task.GetAwaiter";
    private readonly Task _task;

    /// <summary>Initializes a new controlled awaiter over <paramref name="task"/>.</summary>
    /// <param name="task">The task being awaited.</param>
    [StackTraceHidden]
    public ControlledTaskAwaiter(Task task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);

        // The BCL call dereferences the task, so a null task throws the BCL's own NullReferenceException.
        _ = task.GetAwaiter();
        _task = task;
    }

    /// <summary>Gets a value indicating whether the awaited task has already completed.</summary>
    public bool IsCompleted => (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName), _task.IsCompleted).Item2;

    /// <summary>
    /// Completes the await, throwing the task's fault or cancellation exactly as the real awaiter would
    /// (the first exception unwrapped for a fault, <see cref="TaskCanceledException"/> for cancellation).
    /// </summary>
    /// <remarks>
    /// Called synchronously before the task completes, it pumps the simulation until the task completes,
    /// as a controlled synchronous wait does, instead of blocking the simulation's logical thread.
    /// </remarks>
    [StackTraceHidden]
    public void GetResult()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
        if (!_task.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(_task, ApiName, CancellationToken.None);
        }

        RaceSynchronization.Wait(_task);
        _task.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void OnCompleted(Action continuation) =>
        SimulationTaskRuntime.ScheduleContinuation(_task, continuation, ApiName, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) =>
        SimulationTaskRuntime.ScheduleContinuation(_task, continuation, ApiName, flowExecutionContext: false);
}

/// <summary>
/// The controlled substitute for <see cref="global::System.Runtime.CompilerServices.TaskAwaiter{TResult}"/>.
/// Behaves exactly like <see cref="ControlledTaskAwaiter"/> but yields the awaited task's result.
/// </summary>
/// <typeparam name="TResult">The result type of the awaited task.</typeparam>
public readonly struct ControlledTaskAwaiter<TResult> : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.Task`1.GetAwaiter";
    private readonly Task<TResult> _task;

    /// <summary>Initializes a new controlled awaiter over <paramref name="task"/>.</summary>
    /// <param name="task">The task being awaited.</param>
    [StackTraceHidden]
    public ControlledTaskAwaiter(Task<TResult> task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);

        // The BCL call dereferences the task, so a null task throws the BCL's own NullReferenceException.
        _ = task.GetAwaiter();
        _task = task;
    }

    /// <summary>Gets a value indicating whether the awaited task has already completed.</summary>
    public bool IsCompleted => (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName), _task.IsCompleted).Item2;

    /// <summary>Completes the await, returning the result or throwing the task's fault/cancellation.</summary>
    /// <returns>The awaited task's result.</returns>
    /// <remarks>
    /// Called synchronously before the task completes, it pumps the simulation until the task completes,
    /// as a controlled synchronous wait does, instead of blocking the simulation's logical thread.
    /// </remarks>
    [StackTraceHidden]
    public TResult GetResult()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
        if (!_task.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(_task, ApiName, CancellationToken.None);
        }

        RaceSynchronization.Wait(_task);
        return _task.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void OnCompleted(Action continuation) =>
        SimulationTaskRuntime.ScheduleContinuation(_task, continuation, ApiName, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) =>
        SimulationTaskRuntime.ScheduleContinuation(_task, continuation, ApiName, flowExecutionContext: false);
}
