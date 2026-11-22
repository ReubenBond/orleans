using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.ConfiguredTaskAwaitable"/> - the value produced by
/// <c>task.ConfigureAwait(continueOnCapturedContext)</c> and <c>task.ConfigureAwait(options)</c>. This is
/// the type that makes "<c>ConfigureAwait(false)</c> stays controlled" true: inside a simulation
/// <see cref="ConfigureAwaitOptions.ContinueOnCapturedContext"/> is deliberately ignored because there is no
/// captured <see cref="global::System.Threading.SynchronizationContext"/> to honour or discard - every
/// continuation is routed through the coordinator onto the one logical thread regardless.
/// <see cref="ConfigureAwaitOptions.SuppressThrowing"/> and <see cref="ConfigureAwaitOptions.ForceYielding"/>
/// behave as they do on the BCL awaiter.
/// </summary>
public readonly struct ControlledConfiguredTaskAwaitable
{
    private const string ApiName = "System.Threading.Tasks.Task.ConfigureAwait";
    private readonly Task _task;
    private readonly ConfigureAwaitOptions _options;

    /// <summary>Initializes a new controlled configured awaitable.</summary>
    /// <param name="task">The task being awaited.</param>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour (ignored by controlled scheduling).</param>
    [StackTraceHidden]
    public ControlledConfiguredTaskAwaitable(Task task, bool continueOnCapturedContext)
        : this(task, continueOnCapturedContext ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None)
    {
    }

    /// <summary>Initializes a new controlled configured awaitable.</summary>
    /// <param name="task">The task being awaited.</param>
    /// <param name="options">
    /// The await options. <see cref="ConfigureAwaitOptions.ContinueOnCapturedContext"/> is ignored by controlled scheduling.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is invalid for <see cref="Task"/>.</exception>
    [StackTraceHidden]
    public ControlledConfiguredTaskAwaitable(Task task, ConfigureAwaitOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);

        // The BCL call dereferences the task and validates the options, so a null task and invalid options
        // throw the BCL's own exceptions.
        _ = task.ConfigureAwait(options);
        _task = task;
        _options = options;
    }

    /// <summary>Gets the awaiter for this configured awaitable.</summary>
    /// <returns>A <see cref="ControlledConfiguredTaskAwaiter"/>.</returns>
    public ControlledConfiguredTaskAwaiter GetAwaiter()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
        return new(_task, _options);
    }
}

/// <summary>The awaiter for <see cref="ControlledConfiguredTaskAwaitable"/>.</summary>
public readonly struct ControlledConfiguredTaskAwaiter : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.Task.ConfigureAwait";
    private readonly Task _task;
    private readonly ConfigureAwaitOptions _options;

    internal ControlledConfiguredTaskAwaiter(Task task, ConfigureAwaitOptions options)
    {
        _task = task;
        _options = options;
    }

    /// <summary>
    /// Gets a value indicating whether the await completes synchronously: the awaited task has completed and
    /// <see cref="ConfigureAwaitOptions.ForceYielding"/> was not requested.
    /// </summary>
    public bool IsCompleted =>
        (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName),
            (_options & ConfigureAwaitOptions.ForceYielding) == 0 && _task.IsCompleted).Item2;

    /// <summary>
    /// Completes the await, throwing the task's fault or cancellation unless
    /// <see cref="ConfigureAwaitOptions.SuppressThrowing"/> was requested.
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

        // The BCL awaiter applies the end-of-await semantics, including marking a suppressed fault as observed.
        _task.ConfigureAwait(_options).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => Register(continuation, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => Register(continuation, flowExecutionContext: false);

    private void Register(Action continuation, bool flowExecutionContext)
    {
        SimulationTaskRuntime.ScheduleContinuation(_task, continuation, ApiName, flowExecutionContext);
    }
}

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.ConfiguredTaskAwaitable{TResult}"/>. Behaves exactly like
/// <see cref="ControlledConfiguredTaskAwaitable"/> but yields the awaited task's result; as in the BCL,
/// <see cref="ConfigureAwaitOptions.SuppressThrowing"/> is rejected because there would be no result to return.
/// </summary>
/// <typeparam name="TResult">The result type of the awaited task.</typeparam>
public readonly struct ControlledConfiguredTaskAwaitable<TResult>
{
    private const string ApiName = "System.Threading.Tasks.Task`1.ConfigureAwait";
    private readonly Task<TResult> _task;
    private readonly ConfigureAwaitOptions _options;

    /// <summary>Initializes a new controlled configured awaitable.</summary>
    /// <param name="task">The task being awaited.</param>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour (ignored by controlled scheduling).</param>
    [StackTraceHidden]
    public ControlledConfiguredTaskAwaitable(Task<TResult> task, bool continueOnCapturedContext)
        : this(task, continueOnCapturedContext ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None)
    {
    }

    /// <summary>Initializes a new controlled configured awaitable.</summary>
    /// <param name="task">The task being awaited.</param>
    /// <param name="options">
    /// The await options. <see cref="ConfigureAwaitOptions.ContinueOnCapturedContext"/> is ignored by controlled scheduling.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/> is invalid for <see cref="Task{TResult}"/>, including <see cref="ConfigureAwaitOptions.SuppressThrowing"/>.
    /// </exception>
    [StackTraceHidden]
    public ControlledConfiguredTaskAwaitable(Task<TResult> task, ConfigureAwaitOptions options)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);

        // The BCL call dereferences the task and validates the options, so a null task and invalid options
        // throw the BCL's own exceptions.
        _ = task.ConfigureAwait(options);
        _task = task;
        _options = options;
    }

    /// <summary>Gets the awaiter for this configured awaitable.</summary>
    /// <returns>A <see cref="ControlledConfiguredTaskAwaiter{TResult}"/>.</returns>
    public ControlledConfiguredTaskAwaiter<TResult> GetAwaiter()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
        return new(_task, _options);
    }
}

/// <summary>The awaiter for <see cref="ControlledConfiguredTaskAwaitable{TResult}"/>.</summary>
/// <typeparam name="TResult">The result type of the awaited task.</typeparam>
public readonly struct ControlledConfiguredTaskAwaiter<TResult> : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.Task`1.ConfigureAwait";
    private readonly Task<TResult> _task;
    private readonly ConfigureAwaitOptions _options;

    internal ControlledConfiguredTaskAwaiter(Task<TResult> task, ConfigureAwaitOptions options)
    {
        _task = task;
        _options = options;
    }

    /// <summary>
    /// Gets a value indicating whether the await completes synchronously: the awaited task has completed and
    /// <see cref="ConfigureAwaitOptions.ForceYielding"/> was not requested.
    /// </summary>
    public bool IsCompleted =>
        (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName),
            (_options & ConfigureAwaitOptions.ForceYielding) == 0 && _task.IsCompleted).Item2;

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
    public void OnCompleted(Action continuation) => Register(continuation, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => Register(continuation, flowExecutionContext: false);

    private void Register(Action continuation, bool flowExecutionContext)
    {
        SimulationTaskRuntime.ScheduleContinuation(_task, continuation, ApiName, flowExecutionContext);
    }
}
