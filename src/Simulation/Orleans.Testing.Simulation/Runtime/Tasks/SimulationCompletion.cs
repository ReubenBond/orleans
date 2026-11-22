using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>
/// <para>
/// The one path by which Clockwork completes a task, or cancels a token, for simulation code. Inside a simulation, every
/// such completion runs with no <see cref="SynchronizationContext"/> current and with <see cref="TaskScheduler.Current"/>
/// the default scheduler, as on a thread-pool thread; outside one it is the BCL's call, unchanged.
/// </para>
/// <para>
/// The BCL runs a continuation registered as a bare state machine (an await with <c>ConfigureAwait(false)</c>, or one
/// registered where no context or scheduler was current, as in the <c>System.Linq.AsyncEnumerable</c> operators) inline
/// in the call which completes its task only where no context other than the base one is current, the current scheduler
/// is the default one (<c>AwaitTaskContinuation.IsValidLocationForInlining</c>), and the stack has room; anywhere else it
/// queues the continuation to the real thread pool. Simulation code runs in such other places: a scheduler lane's work
/// runs with the lane's <see cref="SimulationSynchronizationContext"/> current, the root task of
/// <c>SimulationCluster.RunToCompletion</c> and any other task on a <see cref="SimulationTaskScheduler"/> run visibly on
/// it, and a task which uninstrumented code constructs and rewritten code starts runs visibly on the coordinator's
/// scheduler. Those stay current for the rest of that work, so uninstrumented code there continues and schedules work on
/// the simulation, as cooperative mode relies on; only the completion runs without them.
/// </para>
/// <para>
/// Code inside the completion therefore sees no context and the default scheduler, and a continuation which captured the
/// context is posted to it rather than run inline. Where a scheduler is current, the completion runs inside a task which
/// hides it, which the coordinator runs inline (<see cref="ControlledTaskScheduler.RunInline"/>), so
/// <see cref="Task.CurrentId"/> there is that task's. Either way it runs in the caller's <see cref="ExecutionContext"/>,
/// so its <see cref="AsyncLocal{T}"/> writes persist, as in the BCL.
/// </para>
/// <para>
/// The BCL checks the stack (<see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/>) several frames inside the
/// completion, and queues the continuations it would inline to the thread pool where too little remains. A completion
/// therefore requires <see cref="CompletionStackHeadroom"/> bytes beyond the BCL's own margin before it does anything,
/// and otherwise fails the run with <see cref="SimulationApiException"/>, through the scheduler's failure path. That
/// covers the frames down to the BCL's check for the continuations this completion runs; a continuation which completes
/// further tasks inline goes deeper, and the BCL checks those completions only against its own margin. A caller which
/// has other side effects makes them inside the completion delegate, so a failed check leaves them undone.
/// </para>
/// <para>
/// Clockwork's code calls the BCL's completion members (<see cref="TaskCompletionSource"/>'s <c>Set*</c> and
/// <c>TrySet*</c>, <see cref="System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore{TResult}"/>'s <c>Set*</c>, and
/// <see cref="CancellationTokenSource"/>'s <c>Cancel</c>, <c>CancelAsync</c>, and <c>CancelAfter</c>) only in the delegates
/// its callers pass here (see
/// <see cref="CompletionCallbackAttribute"/>): an analyzer rejects any other call in the Clockwork assembly.
/// </para>
/// </summary>
internal static class SimulationCompletion
{
    /// <summary>A completion which acts on a variable, such as a <see cref="System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore{TResult}"/> field.</summary>
    /// <typeparam name="TTarget">The variable's type.</typeparam>
    /// <typeparam name="TArgument">The completion's argument.</typeparam>
    /// <param name="target">The variable.</param>
    /// <param name="argument">The argument.</param>
    public delegate void ReferenceAction<TTarget, TArgument>(ref TTarget target, TArgument argument);

    /// <summary>
    /// The stack, in bytes, which a completion requires beyond the BCL's margin: room for the frames between the
    /// completion and the BCL's own check, through a task which hides the scheduler, with a wide allowance.
    /// </summary>
    internal const int CompletionStackHeadroom = 32 * 1024;

    /// <summary>Runs <paramref name="complete"/> as a completion for simulation code.</summary>
    /// <typeparam name="TState">The state <paramref name="complete"/> takes.</typeparam>
    /// <param name="state">The state passed to <paramref name="complete"/>.</param>
    /// <param name="complete">The completion.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    public static void Run<TState>(TState state, [CompletionCallback] Action<TState> complete, string apiName) =>
        _ = Run(
            (state, complete),
            static invocation =>
            {
                invocation.complete(invocation.state);
                return true;
            },
            apiName);

    /// <summary>Runs <paramref name="complete"/> as a completion for simulation code.</summary>
    /// <typeparam name="TState">The state <paramref name="complete"/> takes.</typeparam>
    /// <typeparam name="TResult">The completion's result.</typeparam>
    /// <param name="state">The state passed to <paramref name="complete"/>.</param>
    /// <param name="complete">The completion.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>What <paramref name="complete"/> returns.</returns>
    public static TResult Run<TState, TResult>(TState state, [CompletionCallback] Func<TState, TResult> complete, string apiName)
    {
        if (!SimulationExecutionContext.TryGetCurrentRuntime(out SimulationRuntimeIdentity? runtime) || runtime is null)
        {
            return complete(state);
        }

        if (!HasStackForCompletion())
        {
            throw FailRun(runtime, apiName);
        }

        SynchronizationContext? context = SynchronizationContext.Current;
        bool schedulerAllowsInlining = TaskScheduler.Current == TaskScheduler.Default;
        if (context is null && schedulerAllowsInlining)
        {
            return Complete(state, complete);
        }

        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return schedulerAllowsInlining
                ? Complete(state, complete)
                : CompleteWithTheSchedulerHidden(state, complete, runtime, apiName);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    /// <summary>Runs <paramref name="complete"/> on <paramref name="target"/> as a completion for simulation code.</summary>
    /// <typeparam name="TTarget">The variable's type.</typeparam>
    /// <typeparam name="TArgument">The completion's argument.</typeparam>
    /// <param name="target">The variable the completion acts on.</param>
    /// <param name="argument">The argument passed to <paramref name="complete"/>.</param>
    /// <param name="complete">The completion.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    public static unsafe void Run<TTarget, TArgument>(
        ref TTarget target,
        TArgument argument,
        [CompletionCallback] ReferenceAction<TTarget, TArgument> complete,
        string apiName)
    {
        // The variable is pinned while the completion runs, which it does synchronously, so its address reaches the
        // completion even where a task which hides the scheduler runs it.
        fixed (byte* pinned = &Unsafe.As<TTarget, byte>(ref target))
        {
            Run(
                (target: (nint)pinned, argument, complete),
                static invocation => invocation.complete(
                    ref Unsafe.AsRef<TTarget>((void*)invocation.target),
                    invocation.argument),
                apiName);
        }
    }

    /// <summary>Runs <see cref="TaskCompletionSource.TrySetResult()"/> as a completion for simulation code.</summary>
    /// <param name="source">The source.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetResult(TaskCompletionSource source, string apiName) =>
        Run(source, static source => source.TrySetResult(), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource{TResult}.TrySetResult(TResult)"/> as a completion for simulation code.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="source">The source.</param>
    /// <param name="result">The result.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetResult<TResult>(TaskCompletionSource<TResult> source, TResult result, string apiName) =>
        Run((source, result), static completion => completion.source.TrySetResult(completion.result), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource.TrySetCanceled(CancellationToken)"/> as a completion for simulation code.</summary>
    /// <param name="source">The source.</param>
    /// <param name="cancellationToken">The token which canceled.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Every completion here takes the API name last.")]
    public static bool TrySetCanceled(TaskCompletionSource source, CancellationToken cancellationToken, string apiName) =>
        Run((source, cancellationToken), static completion => completion.source.TrySetCanceled(completion.cancellationToken), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource{TResult}.TrySetCanceled(CancellationToken)"/> as a completion for simulation code.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="source">The source.</param>
    /// <param name="cancellationToken">The token which canceled.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Every completion here takes the API name last.")]
    public static bool TrySetCanceled<TResult>(TaskCompletionSource<TResult> source, CancellationToken cancellationToken, string apiName) =>
        Run((source, cancellationToken), static completion => completion.source.TrySetCanceled(completion.cancellationToken), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource.TrySetException(Exception)"/> as a completion for simulation code.</summary>
    /// <param name="source">The source.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetException(TaskCompletionSource source, Exception exception, string apiName) =>
        Run((source, exception), static completion => completion.source.TrySetException(completion.exception), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource.TrySetException(IEnumerable{Exception})"/> as a completion for simulation code.</summary>
    /// <param name="source">The source.</param>
    /// <param name="exceptions">The exceptions.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetException(TaskCompletionSource source, IEnumerable<Exception> exceptions, string apiName) =>
        Run((source, exceptions), static completion => completion.source.TrySetException(completion.exceptions), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource{TResult}.TrySetException(Exception)"/> as a completion for simulation code.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="source">The source.</param>
    /// <param name="exception">The exception.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetException<TResult>(TaskCompletionSource<TResult> source, Exception exception, string apiName) =>
        Run((source, exception), static completion => completion.source.TrySetException(completion.exception), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource{TResult}.TrySetException(IEnumerable{Exception})"/> as a completion for simulation code.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="source">The source.</param>
    /// <param name="exceptions">The exceptions.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetException<TResult>(TaskCompletionSource<TResult> source, IEnumerable<Exception> exceptions, string apiName) =>
        Run((source, exceptions), static completion => completion.source.TrySetException(completion.exceptions), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource.TrySetFromTask(Task)"/> as a completion for simulation code.</summary>
    /// <param name="source">The source.</param>
    /// <param name="completedTask">The completed task whose outcome the source takes.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetFromTask(TaskCompletionSource source, Task completedTask, string apiName) =>
        Run((source, completedTask), static completion => completion.source.TrySetFromTask(completion.completedTask), apiName);

    /// <summary>Runs <see cref="TaskCompletionSource{TResult}.TrySetFromTask(Task{TResult})"/> as a completion for simulation code.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="source">The source.</param>
    /// <param name="completedTask">The completed task whose outcome the source takes.</param>
    /// <param name="apiName">The API which completes, for diagnostics.</param>
    /// <returns>Whether the source completed.</returns>
    public static bool TrySetFromTask<TResult>(TaskCompletionSource<TResult> source, Task<TResult> completedTask, string apiName) =>
        Run((source, completedTask), static completion => completion.source.TrySetFromTask(completion.completedTask), apiName);

    /// <summary>Runs <see cref="CancellationTokenSource.Cancel()"/> as a completion for simulation code.</summary>
    /// <param name="source">The source.</param>
    /// <param name="apiName">The API which cancels, for diagnostics.</param>
    public static void Cancel(CancellationTokenSource source, string apiName) =>
        Run(source, static source => source.Cancel(), apiName);

    /// <summary>
    /// Gets whether <see cref="CompletionStackHeadroom"/> bytes remain beyond the BCL's margin. The BCL's check passes first,
    /// so the headroom reserved on the stack to check again past it cannot exhaust the stack.
    /// </summary>
    /// <returns>Whether a completion has room to run the continuations it completes inline.</returns>
    internal static bool HasStackForCompletion() =>
        RuntimeHelpers.TryEnsureSufficientExecutionStack() && HasStackPastHeadroom();

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static unsafe bool HasStackPastHeadroom()
    {
        byte* headroom = stackalloc byte[CompletionStackHeadroom];
        headroom[0] = 0;
        return RuntimeHelpers.TryEnsureSufficientExecutionStack();
    }

    private static TResult Complete<TState, TResult>(TState state, Func<TState, TResult> complete)
    {
        Debug.Assert(SynchronizationContext.Current is null, "A completion runs with no synchronization context.");
        Debug.Assert(TaskScheduler.Current == TaskScheduler.Default, "A completion runs with the default scheduler current.");
        return complete(state);
    }

    // Inside a task which hides its scheduler, TaskScheduler.Current is the default scheduler. The task is constructed
    // without capturing the ExecutionContext, so it runs in the caller's.
    private static TResult CompleteWithTheSchedulerHidden<TState, TResult>(
        TState state,
        Func<TState, TResult> complete,
        SimulationRuntimeIdentity runtime,
        string apiName)
    {
        Task<TResult> hidden;
        using (ExecutionContext.SuppressFlow())
        {
            hidden = new Task<TResult>(
                static boxed =>
                {
                    var (state, complete) = ((TState, Func<TState, TResult>))boxed!;
                    return Complete(state, complete);
                },
                (state, complete),
                CancellationToken.None,
                ControlledTask.ConstructorOptions);
        }

        try
        {
            ControlledTaskScheduler.RunInline(hidden);
        }
        catch (TaskSchedulerException)
        {
            // The BCL declined to run the task inline (the stack ran out after the check above), and RunSynchronously wraps
            // the coordinator's refusal to queue it in TaskSchedulerException; the completion has not run.
            throw FailRun(runtime, apiName);
        }

        return hidden.GetAwaiter().GetResult();
    }

    // The run fails from the driving thread's next step, so simulation code which catches the exception thrown here still
    // fails it. Each thread gets an instance of its own.
    private static SimulationApiException FailRun(SimulationRuntimeIdentity runtime, string apiName)
    {
        runtime.Scheduler.ReportUnhandledCallbackException(StackExhausted(apiName));
        return StackExhausted(apiName);
    }

    private static SimulationApiException StackExhausted(string apiName) =>
        new(
            SimulationApiCategory.Task,
            apiName,
            "the calling thread's stack is nearly exhausted, so the BCL would queue the continuations which this completion " +
            "runs to the thread pool rather than run them inline on the simulation's logical thread.");
}
