using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

#pragma warning disable CA1068 // Signatures intentionally mirror the BCL TaskFactory overload order.

/// <summary>
/// <para>
/// Static shims for the <see cref="TaskFactory"/> and <see cref="TaskFactory{TResult}"/> surface. The
/// rewriter redirects the supported call sites here; instance members are exposed as static methods
/// whose first parameter is the receiver, matching Clockwork's <c>RedirectCall</c> convention.
/// </para>
/// <para>
/// <see cref="TaskFactory.StartNew(global::System.Action)"/> and its counterparts schedule work onto a
/// <see cref="TaskScheduler"/> - by default the thread pool. The controlled runtime handles that scheduling by
/// queuing the delegate body as a fresh controlled operation on the simulation coordinator (exactly as
/// <see cref="ControlledTask.Run(global::System.Action)"/> does), so the work runs deterministically on the
/// single logical thread with the factory's (or the call's) cancellation token honoured. The
/// options that change scheduling or parent/continuation semantics and non-default task schedulers are
/// rejected under simulation rather than silently ignored.
/// </para>
/// <para>
/// <c>ContinueWhenAll</c> and <c>ContinueWhenAny</c> return a genuine BCL continuation of a private gate, which the
/// coordinator completes once it observes the antecedents complete, as <see cref="ControlledTask"/>'s
/// <c>ContinueWith</c> does, so the BCL registers nothing on the antecedents. <c>FromAsync</c> is rejected at the call
/// site, since an <see cref="IAsyncResult"/> completes outside Clockwork's control.
/// </para>
/// </summary>
public static partial class ControlledTaskFactory
{
    /// <summary>Controlled <c>TaskFactory.StartNew(Action)</c>.</summary>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="action">The work to schedule.</param>
    /// <returns>A task that completes when the work does.</returns>
    public static Task StartNew(TaskFactory factory, Action action)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            action,
            factory.CancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled <c>TaskFactory.StartNew(Action, CancellationToken)</c>.</summary>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="action">The work to schedule.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task that completes when the work does.</returns>
    public static Task StartNew(TaskFactory factory, Action action, CancellationToken cancellationToken)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            action,
            cancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled <c>TaskFactory.StartNew(Action, TaskCreationOptions)</c>.</summary>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="action">The work to schedule.</param>
    /// <param name="creationOptions">The creation options (<see cref="TaskCreationOptions.AttachedToParent"/> is rejected inside a simulation).</param>
    /// <returns>A task that completes when the work does.</returns>
    public static Task StartNew(TaskFactory factory, Action action, TaskCreationOptions creationOptions)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            action,
            factory.CancellationToken,
            creationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled full-scheduler <c>TaskFactory.StartNew(Action, ...)</c> overload.</summary>
    public static Task StartNew(
        TaskFactory factory,
        Action action,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(action, cancellationToken, creationOptions, scheduler);
    }

    /// <summary>Controlled <c>TaskFactory.StartNew(Action&lt;object&gt;, object)</c>.</summary>
    public static Task StartNew(TaskFactory factory, Action<object?> action, object? state)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);
        return StartNewCore(
            () => action(state),
            factory.CancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory.StartNew</c> overload.</summary>
    public static Task StartNew(
        TaskFactory factory,
        Action<object?> action,
        object? state,
        CancellationToken cancellationToken)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);
        return StartNewCore(
            () => action(state),
            cancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory.StartNew</c> overload.</summary>
    public static Task StartNew(
        TaskFactory factory,
        Action<object?> action,
        object? state,
        TaskCreationOptions creationOptions)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);
        return StartNewCore(
            () => action(state),
            factory.CancellationToken,
            creationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled full-scheduler state-carrying <c>TaskFactory.StartNew</c> overload.</summary>
    public static Task StartNew(
        TaskFactory factory,
        Action<object?> action,
        object? state,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);
        return StartNewCore(
            () => action(state),
            cancellationToken,
            creationOptions,
            scheduler,
            state);
    }

    private static Task StartNewCore(
        Action action,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler,
        object? asyncState = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(scheduler);
        RejectUnsupportedOptions(creationOptions);
        SimulationExecutionSnapshot target = ResolveTarget(scheduler);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource(asyncState);
        SimulationTaskRuntime.QueueCapturedWork(
            target,
            ExecutionContext.Capture(),
            () => RunAction(action, tcs, cancellationToken),
            StartNewApi);
        return tcs.Task;
    }

    /// <summary>Controlled <c>TaskFactory.StartNew&lt;TResult&gt;(Func&lt;TResult&gt;)</c> - the common <c>Task.Factory.StartNew(() =&gt; ...)</c> form.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="function">The work to schedule.</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> StartNew<TResult>(TaskFactory factory, Func<TResult> function)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            function,
            factory.CancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled <c>TaskFactory.StartNew&lt;TResult&gt;(Func&lt;TResult&gt;, CancellationToken)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="function">The work to schedule.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> StartNew<TResult>(TaskFactory factory, Func<TResult> function, CancellationToken cancellationToken)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            function,
            cancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled <c>TaskFactory.StartNew&lt;TResult&gt;(Func&lt;TResult&gt;, TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="function">The work to schedule.</param>
    /// <param name="creationOptions">The creation options (<see cref="TaskCreationOptions.AttachedToParent"/> is rejected inside a simulation).</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> StartNew<TResult>(TaskFactory factory, Func<TResult> function, TaskCreationOptions creationOptions)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            function,
            factory.CancellationToken,
            creationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled full-scheduler <c>TaskFactory.StartNew&lt;TResult&gt;</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory factory,
        Func<TResult> function,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(function, cancellationToken, creationOptions, scheduler);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory.StartNew&lt;TResult&gt;</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory factory,
        Func<object?, TResult> function,
        object? state)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(
            () => function(state),
            factory.CancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory.StartNew&lt;TResult&gt;</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory factory,
        Func<object?, TResult> function,
        object? state,
        CancellationToken cancellationToken)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(
            () => function(state),
            cancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory.StartNew&lt;TResult&gt;</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory factory,
        Func<object?, TResult> function,
        object? state,
        TaskCreationOptions creationOptions)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(
            () => function(state),
            factory.CancellationToken,
            creationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled full-scheduler state-carrying <c>TaskFactory.StartNew&lt;TResult&gt;</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory factory,
        Func<object?, TResult> function,
        object? state,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(() => function(state), cancellationToken, creationOptions, scheduler, state);
    }

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.StartNew(Func&lt;TResult&gt;)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="function">The work to schedule.</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> StartNew<TResult>(TaskFactory<TResult> factory, Func<TResult> function)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            function,
            factory.CancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.StartNew(Func&lt;TResult&gt;, CancellationToken)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="function">The work to schedule.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> StartNew<TResult>(TaskFactory<TResult> factory, Func<TResult> function, CancellationToken cancellationToken)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            function,
            cancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.StartNew(Func&lt;TResult&gt;, TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="factory">The receiving factory.</param>
    /// <param name="function">The work to schedule.</param>
    /// <param name="creationOptions">The creation options (<see cref="TaskCreationOptions.AttachedToParent"/> is rejected inside a simulation).</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> StartNew<TResult>(TaskFactory<TResult> factory, Func<TResult> function, TaskCreationOptions creationOptions)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(
            function,
            factory.CancellationToken,
            creationOptions,
            factory.Scheduler ?? TaskScheduler.Default);
    }

    /// <summary>Controlled full-scheduler <c>TaskFactory&lt;TResult&gt;.StartNew</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory<TResult> factory,
        Func<TResult> function,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        return StartNewCore(function, cancellationToken, creationOptions, scheduler);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory&lt;TResult&gt;.StartNew</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory<TResult> factory,
        Func<object?, TResult> function,
        object? state)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(
            () => function(state),
            factory.CancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory&lt;TResult&gt;.StartNew</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory<TResult> factory,
        Func<object?, TResult> function,
        object? state,
        CancellationToken cancellationToken)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(
            () => function(state),
            cancellationToken,
            factory.CreationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled state-carrying <c>TaskFactory&lt;TResult&gt;.StartNew</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory<TResult> factory,
        Func<object?, TResult> function,
        object? state,
        TaskCreationOptions creationOptions)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(
            () => function(state),
            factory.CancellationToken,
            creationOptions,
            factory.Scheduler ?? TaskScheduler.Default,
            state);
    }

    /// <summary>Controlled full-scheduler state-carrying <c>TaskFactory&lt;TResult&gt;.StartNew</c> overload.</summary>
    public static Task<TResult> StartNew<TResult>(
        TaskFactory<TResult> factory,
        Func<object?, TResult> function,
        object? state,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(function);
        return StartNewCore(() => function(state), cancellationToken, creationOptions, scheduler, state);
    }

    private static Task<TResult> StartNewCore<TResult>(
        Func<TResult> function,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions,
        TaskScheduler scheduler,
        object? asyncState = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(scheduler);
        RejectUnsupportedOptions(creationOptions);
        SimulationExecutionSnapshot target = ResolveTarget(scheduler);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResult>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<TResult>(asyncState);
        SimulationTaskRuntime.QueueCapturedWork(
            target,
            ExecutionContext.Capture(),
            () => RunFunc(function, tcs, cancellationToken),
            StartNewApi);
        return tcs.Task;
    }

    private const string StartNewApi = "System.Threading.Tasks.TaskFactory.StartNew";

    private static void RequireActive() => SimulationRuntimeDispatch.RequireActiveSimulation(StartNewApi);

    private static void RunAction(Action action, TaskCompletionSource tcs, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                SimulationCompletion.TrySetCanceled(tcs, cancellationToken, StartNewApi);
                return;
            }

            try
            {
                action();
                SimulationCompletion.TrySetResult(tcs, StartNewApi);
            }
            catch (OperationCanceledException oce)
                when (cancellationToken.IsCancellationRequested && oce.CancellationToken == cancellationToken)
            {
                SimulationCompletion.TrySetCanceled(tcs, cancellationToken, StartNewApi);
            }
            catch (Exception ex)
            {
                SimulationCompletion.TrySetException(tcs, ex, StartNewApi);
            }
        }
        finally
        {
            if (tcs.Task.IsCompleted)
            {
                RaceSynchronization.Signal(tcs.Task);
            }
        }
    }

    private static void RunFunc<TResult>(Func<TResult> function, TaskCompletionSource<TResult> tcs, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                SimulationCompletion.TrySetCanceled(tcs, cancellationToken, StartNewApi);
                return;
            }

            try
            {
                TResult result = function();
                SimulationCompletion.TrySetResult(tcs, result, StartNewApi);
            }
            catch (OperationCanceledException oce)
                when (cancellationToken.IsCancellationRequested && oce.CancellationToken == cancellationToken)
            {
                SimulationCompletion.TrySetCanceled(tcs, cancellationToken, StartNewApi);
            }
            catch (Exception ex)
            {
                SimulationCompletion.TrySetException(tcs, ex, StartNewApi);
            }
        }
        finally
        {
            if (tcs.Task.IsCompleted)
            {
                RaceSynchronization.Signal(tcs.Task);
            }
        }
    }

    private static void RejectUnsupportedOptions(TaskCreationOptions creationOptions)
    {
        if (creationOptions != TaskCreationOptions.None)
        {
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                "System.Threading.Tasks.TaskFactory.StartNew",
                $"the creation option combination '{creationOptions}' is not supported inside a simulation: " +
                "the cooperative scheduler cannot faithfully preserve the returned task's observable creation " +
                "options or their scheduling, parent, and continuation semantics.");
        }
    }

    private static SimulationExecutionSnapshot ResolveTarget(TaskScheduler scheduler) =>
        ControlledTaskScheduler.ResolveTarget(
            scheduler,
            SimulationRuntimeDispatch.RequireActiveSimulation(StartNewApi),
            SimulationApiCategory.Task,
            StartNewApi);

#pragma warning restore CA1068
}
