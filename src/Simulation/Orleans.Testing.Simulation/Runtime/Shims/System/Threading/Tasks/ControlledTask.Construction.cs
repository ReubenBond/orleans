using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// Controlled <see cref="Task"/> and <see cref="Task{TResult}"/> constructors. A task constructed with <c>new Task(...)</c>
/// runs when the controlled <c>Start</c> or <c>RunSynchronously</c> executes it, and the BCL then runs its delegate in the
/// <see cref="ExecutionContext"/> captured here, which carries this caller's strand and node. The constructed task's
/// delegate re-applies the identity of the strand which executes it (see
/// <see cref="ControlledTaskScheduler.ExecuteAsCurrentStrand"/>), so a started task owns its own locks, as a task on its own
/// thread does, and a task run inline shares its caller's. <see cref="TaskCreationOptions.AttachedToParent"/> is rejected:
/// Clockwork's controlled tasks do not model parent and child tasks, and a parent waiting for an attached child would
/// block the simulation's logical thread.
/// <para>
/// The constructed task hides its scheduler (<see cref="TaskCreationOptions.HideScheduler"/>), so inside it
/// <see cref="TaskScheduler.Current"/> is <see cref="TaskScheduler.Default"/>, as inside a task the BCL starts on the
/// default scheduler. The BCL runs a framework continuation inline only where the current scheduler is the default
/// one, and queues it to the thread pool anywhere else, so a task running visibly on the coordinator's scheduler would
/// send the framework continuations it completes off the simulation. It also denies child attachment
/// (<see cref="TaskCreationOptions.DenyChildAttach"/>), as <c>Task.Run</c>'s tasks do, so no child - a
/// <see cref="TaskCompletionSource"/> constructed with <see cref="TaskCreationOptions.AttachedToParent"/>, say - attaches
/// to it and makes <c>RunSynchronously</c> wait for that child on the logical thread. <see cref="CreationOptions"/> reports
/// the options as the caller passed them.
/// </para>
/// </summary>
// The factories mirror the BCL constructors, which take the CancellationToken before the TaskCreationOptions.
#pragma warning disable CA1068
public static partial class ControlledTask
{
    private const string TaskConstructorApi = "System.Threading.Tasks.Task..ctor";
    private const string GenericTaskConstructorApi = "System.Threading.Tasks.Task`1..ctor";

    // The options which the controlled constructors add to every task they construct, and with which Clockwork's own
    // tasks run code which must see the default scheduler (see SimulationCompletion).
    internal const TaskCreationOptions ConstructorOptions = TaskCreationOptions.HideScheduler | TaskCreationOptions.DenyChildAttach;

    // The constructor options each constructed task's creator did not pass itself.
    private static readonly ConditionalWeakTable<Task, StrongBox<TaskCreationOptions>> AddedByConstructor = [];

    /// <summary>Controlled <c>new Task(Action)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action action) => Create(action, CancellationToken.None, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task(Action, CancellationToken)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action action, CancellationToken cancellationToken) =>
        Create(action, cancellationToken, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task(Action, TaskCreationOptions)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action action, TaskCreationOptions creationOptions) =>
        Create(action, CancellationToken.None, creationOptions);

    /// <summary>Controlled <c>new Task(Action, CancellationToken, TaskCreationOptions)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action action, CancellationToken cancellationToken, TaskCreationOptions creationOptions)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TaskConstructorApi);
        ArgumentNullException.ThrowIfNull(action);
        RejectAttachedToParent(creationOptions, TaskConstructorApi);
        return Isolate(creationOptions, options => new Task(() => ControlledTaskScheduler.RunAsExecutingTask(action), cancellationToken, options));
    }

    /// <summary>Controlled <c>new Task(Action&lt;object?&gt;, object?)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action<object?> action, object? state) =>
        Create(action, state, CancellationToken.None, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task(Action&lt;object?&gt;, object?, CancellationToken)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action<object?> action, object? state, CancellationToken cancellationToken) =>
        Create(action, state, cancellationToken, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task(Action&lt;object?&gt;, object?, TaskCreationOptions)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task Create(Action<object?> action, object? state, TaskCreationOptions creationOptions) =>
        Create(action, state, CancellationToken.None, creationOptions);

    /// <summary>Controlled <c>new Task(Action&lt;object?&gt;, object?, CancellationToken, TaskCreationOptions)</c>.</summary>
    /// <param name="action">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task Create(
        Action<object?> action,
        object? state,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(TaskConstructorApi);
        ArgumentNullException.ThrowIfNull(action);
        RejectAttachedToParent(creationOptions, TaskConstructorApi);
        return Isolate(
            creationOptions,
            options => new Task(
                taskState => ControlledTaskScheduler.RunAsExecutingTask(() => action(taskState)),
                state,
                cancellationToken,
                options));
    }

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;TResult&gt;)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(Func<TResult> function) =>
        Create(function, CancellationToken.None, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;TResult&gt;, CancellationToken)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(Func<TResult> function, CancellationToken cancellationToken) =>
        Create(function, cancellationToken, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;TResult&gt;, TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(Func<TResult> function, TaskCreationOptions creationOptions) =>
        Create(function, CancellationToken.None, creationOptions);

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;TResult&gt;, CancellationToken, TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(
        Func<TResult> function,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GenericTaskConstructorApi);
        // The BCL names the delegate 'action' in Task<TResult>'s argument validation too.
        ArgumentNullException.ThrowIfNull(function, "action");
        RejectAttachedToParent(creationOptions, GenericTaskConstructorApi);
        return Isolate(
            creationOptions,
            options => new Task<TResult>(
                () =>
                {
                    TResult result = default!;
                    ControlledTaskScheduler.RunAsExecutingTask(() => result = function());
                    return result;
                },
                cancellationToken,
                options));
    }

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;object?, TResult&gt;, object?)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(Func<object?, TResult> function, object? state) =>
        Create(function, state, CancellationToken.None, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;object?, TResult&gt;, object?, CancellationToken)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(
        Func<object?, TResult> function,
        object? state,
        CancellationToken cancellationToken) =>
        Create(function, state, cancellationToken, TaskCreationOptions.None);

    /// <summary>Controlled <c>new Task&lt;TResult&gt;(Func&lt;object?, TResult&gt;, object?, TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(
        Func<object?, TResult> function,
        object? state,
        TaskCreationOptions creationOptions) =>
        Create(function, state, CancellationToken.None, creationOptions);

    /// <summary>
    /// Controlled <c>new Task&lt;TResult&gt;(Func&lt;object?, TResult&gt;, object?, CancellationToken, TaskCreationOptions)</c>.
    /// </summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="function">The task's delegate.</param>
    /// <param name="state">The state passed to the delegate, which is the task's <see cref="Task.AsyncState"/>.</param>
    /// <param name="cancellationToken">The token that cancels the task before it runs.</param>
    /// <param name="creationOptions">The task's creation options.</param>
    /// <returns>The created task.</returns>
    public static Task<TResult> Create<TResult>(
        Func<object?, TResult> function,
        object? state,
        CancellationToken cancellationToken,
        TaskCreationOptions creationOptions)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GenericTaskConstructorApi);
        // The BCL names the delegate 'action' in Task<TResult>'s argument validation too.
        ArgumentNullException.ThrowIfNull(function, "action");
        RejectAttachedToParent(creationOptions, GenericTaskConstructorApi);
        return Isolate(
            creationOptions,
            options => new Task<TResult>(
                taskState =>
                {
                    TResult result = default!;
                    ControlledTaskScheduler.RunAsExecutingTask(() => result = function(taskState));
                    return result;
                },
                state,
                cancellationToken,
                options));
    }

    /// <summary>
    /// Controlled <c>task.CreationOptions</c>: the options a task's creator passed, without the
    /// <see cref="TaskCreationOptions.HideScheduler"/> and <see cref="TaskCreationOptions.DenyChildAttach"/> which the
    /// controlled constructors add.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>The task's creation options.</returns>
    public static TaskCreationOptions CreationOptions(Task task)
    {
        SimulationRuntimeDispatch.RequireSimulationThreadIfActive("System.Threading.Tasks.Task", "get_CreationOptions");

        // An instance getter: a null receiver fails with the runtime's NullReferenceException.
        TaskCreationOptions options = task.CreationOptions;
        return AddedByConstructor.TryGetValue(task, out StrongBox<TaskCreationOptions>? added) ? options & ~added.Value : options;
    }

    // Constructs the task with its scheduler hidden and child attachment denied, recording the options its creator did
    // not pass itself.
    private static TTask Isolate<TTask>(TaskCreationOptions creationOptions, Func<TaskCreationOptions, TTask> construct)
        where TTask : Task
    {
        TTask task = construct(creationOptions | ConstructorOptions);
        TaskCreationOptions added = ConstructorOptions & ~creationOptions;
        if (added != 0)
        {
            AddedByConstructor.Add(task, new StrongBox<TaskCreationOptions>(added));
        }

        return task;
    }

    private static void RejectAttachedToParent(TaskCreationOptions creationOptions, string apiName)
    {
        if ((creationOptions & TaskCreationOptions.AttachedToParent) != 0)
        {
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                apiName,
                "TaskCreationOptions.AttachedToParent attaches the task to a parent task, and Clockwork's controlled " +
                "tasks do not model parent and child tasks: a parent waiting for an attached child would block the " +
                "simulation's logical thread.");
        }
    }
}
#pragma warning restore CA1068
