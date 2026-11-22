using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// <para>
/// Static shims for the <see cref="Task"/> and <see cref="Task{TResult}"/> surface that ordinary
/// application code calls directly (as opposed to the compiler-generated builders/awaiters). The
/// rewriter redirects each supported call site here; instance members are exposed as static methods
/// whose first parameter is the receiver, matching Clockwork's <c>RedirectCall</c> convention.
/// </para>
/// <para>
/// No shim registers a continuation on a task through the BCL. A BCL continuation inherits the antecedent's dispatch,
/// so an antecedent completed with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> (or a continuation
/// the BCL queues) would run it on the real thread pool, outside the coordinator. Combinators
/// (<see cref="WhenAll(Task[])"/>, <see cref="WhenAny(Task[])"/>, <see cref="WhenEach(Task[])"/>, <see cref="Unwrap(Task{Task})"/>) and
/// <see cref="ContinueWith(Task, Action{Task})"/> instead observe their antecedents' completion through readiness
/// waits on the coordinator, as the controlled awaiters do, and complete their results on controlled operations.
/// Synchronous waits (<see cref="Wait(Task)"/>, <see cref="Result{TResult}"/>, <see cref="WaitAll(Task[])"/>,
/// <see cref="WaitAny(Task[])"/>, and their timed and cancellable overloads, whose timeouts elapse in virtual time) pump
/// the coordinator instead of blocking a physical thread, then delegate to
/// the real API to reproduce its exact completion/exception semantics. <see cref="Run(Action)"/> and
/// its overloads queue their body as a fresh controlled operation on the coordinator, so
/// the work runs on the single logical thread interleaved with everything else instead of on an
/// uncontrolled physical thread-pool thread. Delay and timeout APIs register deterministic virtual
/// deadlines on the same coordinator and never consume wall-clock time.
/// </para>
/// </summary>
public static partial class ControlledTask
{
    private const string TaskRunApi = "System.Threading.Tasks.Task.Run";
    private const string WaitAsyncApi = "System.Threading.Tasks.Task.WaitAsync";

    private const string StartApi = "System.Threading.Tasks.Task.Start";
    private const string RunSynchronouslyApi = "System.Threading.Tasks.Task.RunSynchronously";

    /// <summary>Controlled <c>Task.Yield()</c>: returns an awaitable that always suspends and resumes via the coordinator.</summary>
    /// <returns>A <see cref="ControlledYieldAwaitable"/>.</returns>
    public static ControlledYieldAwaitable Yield() =>
        (SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Yield"), default(ControlledYieldAwaitable)).Item2;

    /// <summary>
    /// Controlled instance <c>task.Wait()</c>. Pumps the coordinator until the task completes (never
    /// blocking a physical thread) then delegates to the real <c>Task.Wait()</c> so the exact
    /// <see cref="AggregateException"/> semantics are preserved.
    /// </summary>
    /// <param name="task">The task to wait for.</param>
    public static void Wait(Task task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        WaitCore(task, Timeout.Infinite, CancellationToken.None);
    }

    /// <summary>
    /// Controlled instance <c>task.Result</c> getter. Pumps the coordinator until the task completes then
    /// returns the real <see cref="Task{TResult}.Result"/> (preserving <see cref="AggregateException"/>
    /// semantics on fault/cancel).
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="task">The task to read the result of.</param>
    /// <returns>The task's result.</returns>
    public static TResult Result<TResult>(Task<TResult> task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task`1.get_Result");
        ArgumentNullException.ThrowIfNull(task);
        SimulationTaskRuntime.DrainUntilCompleted(
            task,
            "System.Threading.Tasks.Task`1.get_Result",
            CancellationToken.None);
        RaceSynchronization.Wait(task);
        return task.Result;
    }

    /// <summary>Controlled <c>Task.WaitAll(Task[])</c>: pumps until all complete, then delegates.</summary>
    /// <param name="tasks">The tasks to wait for.</param>
    public static void WaitAll(params Task[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        WaitAllCore(tasks, Timeout.Infinite, WaitAllApi, CancellationToken.None);
    }

    /// <summary>Controlled <c>Task.WaitAny(Task[])</c>: pumps until at least one completes, then delegates.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>The index of the first completed task, or -1 if <paramref name="tasks"/> is empty.</returns>
    public static int WaitAny(params Task[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyCore(tasks, Timeout.Infinite, CancellationToken.None);
    }

    /// <summary>
    /// Controlled <c>task.RunSynchronously()</c>: runs the task on the calling logical thread through
    /// <see cref="ControlledTaskScheduler"/>, with the BCL's validation and status transitions.
    /// </summary>
    /// <param name="task">The task to run.</param>
    public static void RunSynchronously(Task task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(RunSynchronouslyApi);
        ArgumentNullException.ThrowIfNull(task);
        ControlledTaskScheduler.RunInline(task);
    }

    /// <summary>Controlled <c>task.RunSynchronously(TaskScheduler)</c>; see <see cref="RunSynchronously(Task)"/>.</summary>
    /// <param name="task">The task to run.</param>
    /// <param name="scheduler">
    /// The scheduler (see <see cref="ControlledTaskScheduler.ResolveTarget"/>). A <see cref="SimulationTaskScheduler"/>
    /// never runs a task inline, so, as in the BCL, the task is queued to its lane and the caller waits for it, pumping
    /// the coordinator.
    /// </param>
    public static void RunSynchronously(Task task, TaskScheduler scheduler)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(RunSynchronouslyApi);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(scheduler);
        _ = ControlledTaskScheduler.ResolveTarget(scheduler, caller, SimulationApiCategory.Task, RunSynchronouslyApi);
        if (scheduler is SimulationTaskScheduler lane)
        {
            task.Start(lane);
            SimulationTaskRuntime.DrainUntilCompleted(task, RunSynchronouslyApi, CancellationToken.None);
            return;
        }

        ControlledTaskScheduler.RunInline(task);
    }

    /// <summary>
    /// Controlled <c>task.Start()</c>: queues the task to <see cref="ControlledTaskScheduler"/>, which runs it as a fresh
    /// controlled operation, as <c>Task.Run</c> runs its body, with the BCL's validation and status transitions.
    /// </summary>
    /// <param name="task">The task to start.</param>
    public static void Start(Task task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(StartApi);
        ArgumentNullException.ThrowIfNull(task);
        task.Start(ControlledTaskScheduler.Instance);
    }

    /// <summary>Controlled <c>task.Start(TaskScheduler)</c>; see <see cref="Start(Task)"/>.</summary>
    /// <param name="task">The task to start.</param>
    /// <param name="scheduler">
    /// The scheduler (see <see cref="ControlledTaskScheduler.ResolveTarget"/>): a <see cref="SimulationTaskScheduler"/>
    /// runs the task on its lane, and <see cref="TaskScheduler.Default"/> on the coordinator.
    /// </param>
    public static void Start(Task task, TaskScheduler scheduler)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(StartApi);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(scheduler);
        _ = ControlledTaskScheduler.ResolveTarget(scheduler, caller, SimulationApiCategory.Task, StartApi);
        task.Start(scheduler is SimulationTaskScheduler lane ? lane : ControlledTaskScheduler.Instance);
    }


    /// <summary>Returns a task completed after the requested virtual delay.</summary>
    public static Task Delay(int millisecondsDelay)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Delay");
        return DelayCore(ValidateMillisecondsDelay(millisecondsDelay), CancellationToken.None);
    }

    /// <summary>Returns a task completed after the requested virtual delay.</summary>
    public static Task Delay(TimeSpan delay)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Delay");
        return DelayCore(ValidateTimerTimeSpan(delay, nameof(delay)), CancellationToken.None);
    }

    /// <summary>Returns a cancellable task completed after the requested virtual delay.</summary>
    public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Delay");
        return DelayCore(ValidateMillisecondsDelay(millisecondsDelay), cancellationToken);
    }

    /// <summary>Returns a cancellable task completed after the requested virtual delay.</summary>
    public static Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Delay");
        return DelayCore(ValidateTimerTimeSpan(delay, nameof(delay)), cancellationToken);
    }

    /// <summary>Returns a task completed after a virtual delay interpreted by a controlled provider.</summary>
    public static Task Delay(TimeSpan delay, TimeProvider timeProvider)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Delay");
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimerTimeSpan(delay, nameof(delay));
        ControlledTimeProvider.ValidateProvider(timeProvider, "System.Threading.Tasks.Task.Delay");
        return DelayCore(validated, CancellationToken.None);
    }

    /// <summary>Returns a cancellable task completed after a virtual delay interpreted by a controlled provider.</summary>
    public static Task Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.Delay");
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimerTimeSpan(delay, nameof(delay));
        ControlledTimeProvider.ValidateProvider(timeProvider, "System.Threading.Tasks.Task.Delay");
        return DelayCore(validated, cancellationToken);
    }

    /// <summary>Waits for a task or cancellation without escaping the controlled scheduler.</summary>
    public static Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        return WaitAsyncCore(task, Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>Waits for a task or virtual timeout.</summary>
    public static Task WaitAsync(Task task, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        return WaitAsyncCore(task, ValidateTimerTimeSpan(timeout, nameof(timeout)), CancellationToken.None);
    }

    /// <summary>Waits for a task or virtual timeout interpreted by a controlled provider.</summary>
    public static Task WaitAsync(Task task, TimeSpan timeout, TimeProvider timeProvider)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimerTimeSpan(timeout, nameof(timeout));
        ControlledTimeProvider.ValidateProvider(timeProvider, "System.Threading.Tasks.Task.WaitAsync");
        return WaitAsyncCore(task, validated, CancellationToken.None);
    }

    /// <summary>Waits for a task, virtual timeout, or cancellation.</summary>
    public static Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        return WaitAsyncCore(task, ValidateTimerTimeSpan(timeout, nameof(timeout)), cancellationToken);
    }

    /// <summary>Waits for a task, controlled-provider timeout, or cancellation.</summary>
    public static Task WaitAsync(
        Task task,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimerTimeSpan(timeout, nameof(timeout));
        ControlledTimeProvider.ValidateProvider(timeProvider, "System.Threading.Tasks.Task.WaitAsync");
        return WaitAsyncCore(task, validated, cancellationToken);
    }

    /// <summary>Waits for a generic task or cancellation.</summary>
    public static Task<TResult> WaitAsync<TResult>(Task<TResult> task, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task`1.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        return WaitAsyncCore(task, Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>Waits for a generic task or virtual timeout.</summary>
    public static Task<TResult> WaitAsync<TResult>(Task<TResult> task, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task`1.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        return WaitAsyncCore(task, ValidateTimerTimeSpan(timeout, nameof(timeout)), CancellationToken.None);
    }

    /// <summary>Waits for a generic task or controlled-provider timeout.</summary>
    public static Task<TResult> WaitAsync<TResult>(
        Task<TResult> task,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task`1.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimerTimeSpan(timeout, nameof(timeout));
        ControlledTimeProvider.ValidateProvider(timeProvider, "System.Threading.Tasks.Task`1.WaitAsync");
        return WaitAsyncCore(task, validated, CancellationToken.None);
    }

    /// <summary>Waits for a generic task, virtual timeout, or cancellation.</summary>
    public static Task<TResult> WaitAsync<TResult>(
        Task<TResult> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task`1.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        return WaitAsyncCore(task, ValidateTimerTimeSpan(timeout, nameof(timeout)), cancellationToken);
    }

    /// <summary>Waits for a generic task, controlled-provider timeout, or cancellation.</summary>
    public static Task<TResult> WaitAsync<TResult>(
        Task<TResult> task,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.Tasks.Task`1.WaitAsync");
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeSpan validated = ValidateTimerTimeSpan(timeout, nameof(timeout));
        ControlledTimeProvider.ValidateProvider(timeProvider, "System.Threading.Tasks.Task`1.WaitAsync");
        return WaitAsyncCore(task, validated, cancellationToken);
    }

    /// <summary>
    /// Controlled <c>Task.Run(Action)</c>. Under simulation the work is queued as a fresh controlled
    /// operation on the coordinator - it runs on the single logical thread interleaved with all other
    /// controlled work, never on an uncontrolled physical thread-pool thread - and the returned task
    /// completes (or faults/cancels) exactly as the real <c>Task.Run</c> would.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <returns>A task that completes when the work does.</returns>
    public static Task Run(Action action) => Run(action, CancellationToken.None);

    /// <summary>Controlled <c>Task.Run(Action, CancellationToken)</c>.</summary>
    /// <param name="action">The work to run.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task that completes when the work does.</returns>
    public static Task Run(Action action, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TaskRunApi);
        ArgumentNullException.ThrowIfNull(action);
        ExecutionContext? context = ExecutionContext.Capture();
        var tcs = new TaskCompletionSource();
        SimulationTaskRuntime.QueueWork(
            () => SimulationTaskRuntime.RunWithCapturedExecutionContext(
                context,
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, cancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    try
                    {
                        action();
                        SimulationCompletion.TrySetResult(tcs, TaskRunApi);
                        PublishCompletion(tcs.Task);
                    }
                    catch (OperationCanceledException oce)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, oce.CancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                    }
                    catch (Exception ex)
                    {
                        SimulationCompletion.TrySetException(tcs, ex, TaskRunApi);
                        PublishCompletion(tcs.Task);
                    }
                }),
            TaskRunApi);
        return tcs.Task;
    }

    /// <summary>Controlled <c>Task.Run&lt;TResult&gt;(Func&lt;TResult&gt;)</c>.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="function">The work to run.</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> Run<TResult>(Func<TResult> function) => Run(function, CancellationToken.None);

    /// <summary>Controlled <c>Task.Run&lt;TResult&gt;(Func&lt;TResult&gt;, CancellationToken)</c>.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="function">The work to run.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task with the function's result.</returns>
    public static Task<TResult> Run<TResult>(Func<TResult> function, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TaskRunApi);
        ArgumentNullException.ThrowIfNull(function);
        ExecutionContext? context = ExecutionContext.Capture();
        var tcs = new TaskCompletionSource<TResult>();
        SimulationTaskRuntime.QueueWork(
            () => SimulationTaskRuntime.RunWithCapturedExecutionContext(
                context,
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, cancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    try
                    {
                        TResult result = function();
                        SimulationCompletion.TrySetResult(tcs, result, TaskRunApi);
                        PublishCompletion(tcs.Task);
                    }
                    catch (OperationCanceledException oce)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, oce.CancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                    }
                    catch (Exception ex)
                    {
                        SimulationCompletion.TrySetException(tcs, ex, TaskRunApi);
                        PublishCompletion(tcs.Task);
                    }
                }),
            TaskRunApi);
        return tcs.Task;
    }

    /// <summary>
    /// Controlled <c>Task.Run(Func&lt;Task&gt;)</c>: the returned task completes only when the inner task
    /// the delegate produces completes (the same "unwrap" semantics as the real overload), with the inner
    /// task's own completion driven deterministically through the coordinator.
    /// </summary>
    /// <param name="function">The asynchronous work to run.</param>
    /// <returns>A task representing the unwrapped inner task.</returns>
    public static Task Run(Func<Task> function) => Run(function, CancellationToken.None);

    /// <summary>Controlled <c>Task.Run(Func&lt;Task&gt;, CancellationToken)</c> with unwrap semantics.</summary>
    /// <param name="function">The asynchronous work to run.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task representing the unwrapped inner task.</returns>
    public static Task Run(Func<Task> function, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TaskRunApi);
        ArgumentNullException.ThrowIfNull(function);
        ExecutionContext? context = ExecutionContext.Capture();
        var tcs = new TaskCompletionSource();
        SimulationTaskRuntime.QueueWork(
            () => SimulationTaskRuntime.RunWithCapturedExecutionContext(
                context,
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, cancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    Task inner;
                    try
                    {
                        inner = function();
                    }
                    catch (OperationCanceledException oce)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, oce.CancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }
                    catch (Exception ex)
                    {
                        SimulationCompletion.TrySetException(tcs, ex, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    if (inner is null)
                    {
                        SimulationCompletion.TrySetException(tcs, new InvalidOperationException(
                            "Task.Run(Func<Task>) delegate returned a null task."), TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    SimulationTaskRuntime.ScheduleContinuation(
                        inner,
                        () => PropagateTo(inner, tcs, TaskRunApi),
                        TaskRunApi,
                        flowExecutionContext: false);
                }),
            TaskRunApi);
        return tcs.Task;
    }

    /// <summary>Controlled <c>Task.Run&lt;TResult&gt;(Func&lt;Task&lt;TResult&gt;&gt;)</c> with unwrap semantics.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="function">The asynchronous work to run.</param>
    /// <returns>A task representing the unwrapped inner task's result.</returns>
    public static Task<TResult> Run<TResult>(Func<Task<TResult>> function) => Run(function, CancellationToken.None);

    /// <summary>Controlled <c>Task.Run&lt;TResult&gt;(Func&lt;Task&lt;TResult&gt;&gt;, CancellationToken)</c> with unwrap semantics.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="function">The asynchronous work to run.</param>
    /// <param name="cancellationToken">A token that cancels the work before it starts.</param>
    /// <returns>A task representing the unwrapped inner task's result.</returns>
    public static Task<TResult> Run<TResult>(Func<Task<TResult>> function, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TaskRunApi);
        ArgumentNullException.ThrowIfNull(function);
        ExecutionContext? context = ExecutionContext.Capture();
        var tcs = new TaskCompletionSource<TResult>();
        SimulationTaskRuntime.QueueWork(
            () => SimulationTaskRuntime.RunWithCapturedExecutionContext(
                context,
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, cancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    Task<TResult> inner;
                    try
                    {
                        inner = function();
                    }
                    catch (OperationCanceledException oce)
                    {
                        SimulationCompletion.TrySetCanceled(tcs, oce.CancellationToken, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }
                    catch (Exception ex)
                    {
                        SimulationCompletion.TrySetException(tcs, ex, TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    if (inner is null)
                    {
                        SimulationCompletion.TrySetException(tcs, new InvalidOperationException(
                            "Task.Run(Func<Task<TResult>>) delegate returned a null task."), TaskRunApi);
                        PublishCompletion(tcs.Task);
                        return;
                    }

                    SimulationTaskRuntime.ScheduleContinuation(
                        inner,
                        () => PropagateTo(inner, tcs, TaskRunApi),
                        TaskRunApi,
                        flowExecutionContext: false);
                }),
            TaskRunApi);
        return tcs.Task;
    }

    private static Task DelayCore(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (delay == TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        return new SimulationDelayPromise(delay, cancellationToken).Task;
    }

    private static Task WaitAsyncCore(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled))
        {
            return task;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (timeout == TimeSpan.Zero)
        {
            return Task.FromException(new TimeoutException());
        }

        var completion = new TaskCompletionSource();
        var race = new SimulationTaskCompletionRace(
            task,
            timeout,
            () => PropagateTo(task, completion, WaitAsyncApi),
            () => SimulationCompletion.TrySetException(completion, new TimeoutException(), WaitAsyncApi),
            () => SimulationCompletion.TrySetCanceled(completion, cancellationToken, WaitAsyncApi),
            cancellationToken);
        race.Start();
        return completion.Task;
    }

    private static Task<TResult> WaitAsyncCore<TResult>(
        Task<TResult> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted || (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled))
        {
            return task;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResult>(cancellationToken);
        }

        if (timeout == TimeSpan.Zero)
        {
            return Task.FromException<TResult>(new TimeoutException());
        }

        var completion = new TaskCompletionSource<TResult>();
        var race = new SimulationTaskCompletionRace(
            task,
            timeout,
            () => PropagateTo(task, completion, WaitAsyncApi),
            () => SimulationCompletion.TrySetException(completion, new TimeoutException(), WaitAsyncApi),
            () => SimulationCompletion.TrySetCanceled(completion, cancellationToken, WaitAsyncApi),
            cancellationToken);
        race.Start();
        return completion.Task;
    }

    private static TimeSpan ValidateMillisecondsDelay(int millisecondsDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsDelay, -1);
        return millisecondsDelay == Timeout.Infinite
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(millisecondsDelay);
    }

    private static TimeSpan ValidateTimerTimeSpan(TimeSpan timeout, string parameterName)
    {
        const uint maxSupportedTimeout = 0xfffffffe;
        long milliseconds = (long)timeout.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, -1, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, maxSupportedTimeout, parameterName);
        return milliseconds == Timeout.Infinite
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private static bool AnyCompleted(Task[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task.IsCompleted)
            {
                return true;
            }
        }

        return tasks.Length == 0;
    }

    /// <summary>Copies a completed antecedent's terminal status (success/fault/cancel) onto a non-generic source.</summary>
    private static void PropagateTo(Task inner, TaskCompletionSource tcs, string apiName)
    {
        if (inner.IsCanceled)
        {
            SimulationCompletion.TrySetCanceled(tcs, GetCancellationToken(inner), apiName);
        }
        else if (inner.IsFaulted)
        {
            SimulationCompletion.TrySetException(tcs, inner.Exception!.InnerExceptions, apiName);
        }
        else
        {
            SimulationCompletion.TrySetResult(tcs, apiName);
        }

        PublishCompletion(tcs.Task);
    }

    /// <summary>Copies a completed antecedent's terminal status (success/fault/cancel) onto a generic source.</summary>
    private static void PropagateTo<TResult>(Task<TResult> inner, TaskCompletionSource<TResult> tcs, string apiName)
    {
        if (inner.IsCanceled)
        {
            SimulationCompletion.TrySetCanceled(tcs, GetCancellationToken(inner), apiName);
        }
        else if (inner.IsFaulted)
        {
            SimulationCompletion.TrySetException(tcs, inner.Exception!.InnerExceptions, apiName);
        }
        else
        {
            SimulationCompletion.TrySetResult(tcs, inner.Result, apiName);
        }

        PublishCompletion(tcs.Task);
    }

    private static void PublishCompletion(Task task)
    {
        if (task.IsCompleted)
        {
            RaceSynchronization.Signal(task);
        }
    }

    private static void ValidateNoNullTasks(Task[] tasks)
    {
        foreach (Task? task in tasks)
        {
            if (task is null)
            {
                throw new ArgumentException("The tasks array included at least one null element.", nameof(tasks));
            }
        }
    }

    private static CancellationToken GetCancellationToken(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            return exception.CancellationToken;
        }

        return CancellationToken.None;
    }
}
