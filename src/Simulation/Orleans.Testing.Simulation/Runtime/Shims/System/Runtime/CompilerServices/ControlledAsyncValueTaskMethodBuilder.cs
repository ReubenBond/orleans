using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"/>, used as the builder field
/// of a rewritten <c>async ValueTask</c> state machine. Like
/// <see cref="ControlledAsyncTaskMethodBuilder"/> it is a transparent value-type wrapper over the real
/// builder; continuation control lives in the substituted awaiters.
/// </summary>
public struct ControlledAsyncValueTaskMethodBuilder
{
    private AsyncValueTaskMethodBuilder _inner;

    private ControlledAsyncValueTaskMethodBuilder(AsyncValueTaskMethodBuilder inner) => _inner = inner;

    /// <summary>Creates a new controlled builder wrapping a fresh real builder.</summary>
    /// <returns>The new builder.</returns>
    public static ControlledAsyncValueTaskMethodBuilder Create() =>
        (RequireActive(), new ControlledAsyncValueTaskMethodBuilder(AsyncValueTaskMethodBuilder.Create())).Item2;

    /// <summary>Gets the value task for the async method, with unchanged semantics.</summary>
    public readonly ValueTask Task => (RequireActive(), _inner.Task).Item2;

    /// <summary>Begins running the state machine, up to its first suspension point.</summary>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="stateMachine">The state machine.</param>
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        RequireActive();
        _inner.Start(ref stateMachine);
    }

    /// <summary>Associates the boxed state machine with the builder.</summary>
    /// <param name="stateMachine">The boxed state machine.</param>
    public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
    {
        RequireActive();
        _inner.SetStateMachine(stateMachine);
    }

    /// <summary>Completes the value task successfully.</summary>
    public void SetResult()
    {
        RequireActive();
        _inner.SetResult();
        ControlledValueTask.SignalCompletion(_inner.Task);
    }

    /// <summary>Completes the value task in a faulted or cancelled state.</summary>
    /// <param name="exception">The exception to fault (or cancel) the value task with.</param>
    public void SetException(Exception exception)
    {
        RequireActive();
        _inner.SetException(exception);
        ControlledValueTask.SignalCompletion(_inner.Task);
    }

    /// <summary>Schedules the state machine to resume when the awaiter completes (context-flowing).</summary>
    /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stateMachine">The state machine.</param>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        RequireActive();
        _inner.AwaitOnCompleted(ref awaiter, ref stateMachine);
    }

    /// <summary>Schedules the state machine to resume when the awaiter completes (non context-flowing).</summary>
    /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stateMachine">The state machine.</param>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        RequireActive();
        _inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
    }

    private static SimulationExecutionSnapshot RequireActive() =>
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder");
}

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder{TResult}"/>. See
/// <see cref="ControlledAsyncValueTaskMethodBuilder"/> for the design; this variant produces a
/// <see cref="ValueTask{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">The result type of the async method.</typeparam>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "The C# compiler's async builder pattern requires a static Create() method on the builder type; this mirrors the BCL AsyncValueTaskMethodBuilder<TResult>.")]
public struct ControlledAsyncValueTaskMethodBuilder<TResult>
{
    private AsyncValueTaskMethodBuilder<TResult> _inner;

    private ControlledAsyncValueTaskMethodBuilder(AsyncValueTaskMethodBuilder<TResult> inner) => _inner = inner;

    /// <summary>Creates a new controlled builder wrapping a fresh real builder.</summary>
    /// <returns>The new builder.</returns>
    public static ControlledAsyncValueTaskMethodBuilder<TResult> Create() =>
        (RequireActive(), new ControlledAsyncValueTaskMethodBuilder<TResult>(AsyncValueTaskMethodBuilder<TResult>.Create())).Item2;

    /// <summary>Gets the value task for the async method, with unchanged semantics.</summary>
    public readonly ValueTask<TResult> Task => (RequireActive(), _inner.Task).Item2;

    /// <summary>Begins running the state machine, up to its first suspension point.</summary>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="stateMachine">The state machine.</param>
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        RequireActive();
        _inner.Start(ref stateMachine);
    }

    /// <summary>Associates the boxed state machine with the builder.</summary>
    /// <param name="stateMachine">The boxed state machine.</param>
    public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
    {
        RequireActive();
        _inner.SetStateMachine(stateMachine);
    }

    /// <summary>Completes the value task successfully with the given result.</summary>
    /// <param name="result">The result value.</param>
    public void SetResult(TResult result)
    {
        RequireActive();
        _inner.SetResult(result);
        ControlledValueTask.SignalCompletion(_inner.Task);
    }

    /// <summary>Completes the value task in a faulted or cancelled state.</summary>
    /// <param name="exception">The exception to fault (or cancel) the value task with.</param>
    public void SetException(Exception exception)
    {
        RequireActive();
        _inner.SetException(exception);
        ControlledValueTask.SignalCompletion(_inner.Task);
    }

    /// <summary>Schedules the state machine to resume when the awaiter completes (context-flowing).</summary>
    /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stateMachine">The state machine.</param>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        RequireActive();
        _inner.AwaitOnCompleted(ref awaiter, ref stateMachine);
    }

    /// <summary>Schedules the state machine to resume when the awaiter completes (non context-flowing).</summary>
    /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
    /// <typeparam name="TStateMachine">The state machine type.</typeparam>
    /// <param name="awaiter">The awaiter.</param>
    /// <param name="stateMachine">The state machine.</param>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        RequireActive();
        _inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
    }

    private static SimulationExecutionSnapshot RequireActive() =>
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1");
}
