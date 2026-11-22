using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable"/> - the value produced by
/// <c>valueTask.ConfigureAwait(continueOnCapturedContext)</c>. Like
/// <see cref="ControlledConfiguredTaskAwaitable"/> it makes "<c>ConfigureAwait(false)</c> stays
/// controlled" true for <see cref="ValueTask"/>: the flag is ignored because every
/// continuation is routed through the coordinator onto the one logical thread.
/// </summary>
public readonly struct ControlledConfiguredValueTaskAwaitable
{
    private readonly ValueTask _valueTask;
    private readonly bool _continueOnCapturedContext;

    /// <summary>Initializes a new controlled configured value-task awaitable.</summary>
    /// <param name="valueTask">The value task being awaited.</param>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour (ignored by controlled scheduling).</param>
    [StackTraceHidden]
    public ControlledConfiguredValueTaskAwaitable(in ValueTask valueTask, bool continueOnCapturedContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.ValueTask.ConfigureAwait");
        _valueTask = valueTask;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    /// <summary>Gets the awaiter for this configured awaitable.</summary>
    /// <returns>A <see cref="ControlledConfiguredValueTaskAwaiter"/>.</returns>
    public ControlledConfiguredValueTaskAwaiter GetAwaiter()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.ValueTask.ConfigureAwait");
        return new(_valueTask, _continueOnCapturedContext);
    }
}

/// <summary>The awaiter for <see cref="ControlledConfiguredValueTaskAwaitable"/>.</summary>
public readonly struct ControlledConfiguredValueTaskAwaiter : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.ValueTask.ConfigureAwait";
    private readonly ValueTask _valueTask;
    private readonly bool _continueOnCapturedContext;

    internal ControlledConfiguredValueTaskAwaiter(ValueTask valueTask, bool continueOnCapturedContext)
    {
        _valueTask = valueTask;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    /// <summary>Gets a value indicating whether the awaited value task has already completed.</summary>
    public bool IsCompleted => (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName), _valueTask.IsCompleted).Item2;

    /// <summary>Completes the await, throwing the value task's fault or cancellation.</summary>
    /// <remarks>
    /// Called synchronously before the value task completes, it pumps the simulation until the value task completes,
    /// as a controlled synchronous wait does, instead of blocking the simulation's logical thread.
    /// </remarks>
    [StackTraceHidden]
    public void GetResult()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
        if (!_valueTask.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(_valueTask, ApiName);
        }

        ControlledValueTask.WaitCompletion(_valueTask);
        _valueTask.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => Register(continuation, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => Register(continuation, flowExecutionContext: false);

    private void Register(Action continuation, bool flowExecutionContext)
    {
        var (scheduler, node) = SimulationTaskRuntime.RequireScheduler(ApiName);
        ArgumentNullException.ThrowIfNull(continuation);
        var valueTask = _valueTask;
        scheduler.ScheduleWhenReady(node, () => valueTask.IsCompleted, continuation);
    }
}

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable{TResult}"/>. Behaves exactly
/// like <see cref="ControlledConfiguredValueTaskAwaitable"/> but yields the awaited value task's result.
/// </summary>
/// <typeparam name="TResult">The result type of the awaited value task.</typeparam>
public readonly struct ControlledConfiguredValueTaskAwaitable<TResult>
{
    private readonly ValueTask<TResult> _valueTask;
    private readonly bool _continueOnCapturedContext;

    /// <summary>Initializes a new controlled configured value-task awaitable.</summary>
    /// <param name="valueTask">The value task being awaited.</param>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour (ignored by controlled scheduling).</param>
    [StackTraceHidden]
    public ControlledConfiguredValueTaskAwaitable(in ValueTask<TResult> valueTask, bool continueOnCapturedContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.ValueTask`1.ConfigureAwait");
        _valueTask = valueTask;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    /// <summary>Gets the awaiter for this configured awaitable.</summary>
    /// <returns>A <see cref="ControlledConfiguredValueTaskAwaiter{TResult}"/>.</returns>
    public ControlledConfiguredValueTaskAwaiter<TResult> GetAwaiter()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.ValueTask`1.ConfigureAwait");
        return new(_valueTask, _continueOnCapturedContext);
    }
}

/// <summary>The awaiter for <see cref="ControlledConfiguredValueTaskAwaitable{TResult}"/>.</summary>
/// <typeparam name="TResult">The result type of the awaited value task.</typeparam>
public readonly struct ControlledConfiguredValueTaskAwaiter<TResult> : ICriticalNotifyCompletion, INotifyCompletion
{
    private const string ApiName = "System.Threading.Tasks.ValueTask`1.ConfigureAwait";
    private readonly ValueTask<TResult> _valueTask;
    private readonly bool _continueOnCapturedContext;

    internal ControlledConfiguredValueTaskAwaiter(ValueTask<TResult> valueTask, bool continueOnCapturedContext)
    {
        _valueTask = valueTask;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    /// <summary>Gets a value indicating whether the awaited value task has already completed.</summary>
    public bool IsCompleted => (SimulationRuntimeDispatch.RequireActiveSimulation(ApiName), _valueTask.IsCompleted).Item2;

    /// <summary>Completes the await, returning the result or throwing the value task's fault/cancellation.</summary>
    /// <returns>The awaited value task's result.</returns>
    /// <remarks>
    /// Called synchronously before the value task completes, it pumps the simulation until the value task completes,
    /// as a controlled synchronous wait does, instead of blocking the simulation's logical thread.
    /// </remarks>
    [StackTraceHidden]
    public TResult GetResult()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);
        if (!_valueTask.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(_valueTask, ApiName);
        }

        ControlledValueTask.WaitCompletion(_valueTask);
        return _valueTask.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => Register(continuation, flowExecutionContext: true);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => Register(continuation, flowExecutionContext: false);

    private void Register(Action continuation, bool flowExecutionContext)
    {
        var (scheduler, node) = SimulationTaskRuntime.RequireScheduler(ApiName);
        ArgumentNullException.ThrowIfNull(continuation);
        var valueTask = _valueTask;
        scheduler.ScheduleWhenReady(node, () => valueTask.IsCompleted, continuation);
    }
}
