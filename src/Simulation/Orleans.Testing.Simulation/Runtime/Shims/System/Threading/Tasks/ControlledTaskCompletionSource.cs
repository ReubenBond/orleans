using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// <para>
/// Controlled completion members of <see cref="TaskCompletionSource"/> and <see cref="TaskCompletionSource{TResult}"/>.
/// Rewritten code keeps the real BCL completion sources, with their real <see cref="TaskCreationOptions"/>: the
/// rewriter redirects calls to the completion members, each to a static shim whose first parameter is the
/// receiving source. Each shim completes the source through the BCL member itself, so validation, exceptions, and the
/// resulting task state are the BCL's, and the completion members are not virtual, so a source derived from the BCL
/// type completes exactly as it would without the redirect.
/// </para>
/// <para>
/// A successful completion publishes a race-detection release edge on the source's task, which an await, a
/// synchronous wait, or a controlled combinator or continuation acquires when it observes the task complete. A
/// completion from another task (<c>SetFromTask</c>) first acquires that task's edges, since it reads that task's
/// outcome.
/// </para>
/// <para>
/// Each shim completes the source through <see cref="SimulationCompletion"/>, so a continuation which uninstrumented code
/// registered and which the BCL runs inline runs on the simulation's logical thread.
/// </para>
/// <para>
/// Completion is the one controlled surface which runs outside a simulation: a finalizer, or a callback from
/// uninstrumented code, may complete a source with no simulation's ambient context, and the shim then passes through to
/// the BCL member. With a simulation's ambient context, a completion publishes into that simulation, so it requires the
/// simulation's logical thread like every other controlled API and otherwise throws
/// <see cref="SimulationThreadAffinityException"/>. Each shim publishes or acquires an edge only inside a controlled
/// operation, as the race detector records edges only there.
/// </para>
/// <para>
/// The constructors which take <see cref="TaskCreationOptions"/> also redirect, so that inside a simulation
/// <see cref="TaskCreationOptions.AttachedToParent"/> is rejected, as it is for <c>new Task</c>, <c>StartNew</c>, and
/// <c>ContinueWith</c>: Clockwork's controlled tasks do not model parent and child tasks, so a promise could not attach
/// to the task running it as it does under the BCL, where a parent faults when its attached promise faults.
/// </para>
/// </summary>
public static class ControlledTaskCompletionSource
{
    private const string TaskCompletionSourceApi = "System.Threading.Tasks.TaskCompletionSource";
    private const string GenericTaskCompletionSourceApi = "System.Threading.Tasks.TaskCompletionSource`1";

    /// <summary>Controlled <c>new TaskCompletionSource(TaskCreationOptions)</c>.</summary>
    /// <param name="creationOptions">The options of the source's task.</param>
    /// <returns>The source.</returns>
    public static TaskCompletionSource Create(TaskCreationOptions creationOptions)
    {
        RequireUnattached(creationOptions, TaskCompletionSourceApi);
        return new TaskCompletionSource(creationOptions);
    }

    /// <summary>Controlled <c>new TaskCompletionSource(object?, TaskCreationOptions)</c>.</summary>
    /// <param name="state">The state of the source's task, its <see cref="Task.AsyncState"/>.</param>
    /// <param name="creationOptions">The options of the source's task.</param>
    /// <returns>The source.</returns>
    public static TaskCompletionSource Create(object? state, TaskCreationOptions creationOptions)
    {
        RequireUnattached(creationOptions, TaskCompletionSourceApi);
        return new TaskCompletionSource(state, creationOptions);
    }

    /// <summary>Controlled <c>new TaskCompletionSource&lt;TResult&gt;(TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The result type of the source's task.</typeparam>
    /// <param name="creationOptions">The options of the source's task.</param>
    /// <returns>The source.</returns>
    public static TaskCompletionSource<TResult> CreateGeneric<TResult>(TaskCreationOptions creationOptions)
    {
        RequireUnattached(creationOptions, GenericTaskCompletionSourceApi);
        return new TaskCompletionSource<TResult>(creationOptions);
    }

    /// <summary>Controlled <c>new TaskCompletionSource&lt;TResult&gt;(object?, TaskCreationOptions)</c>.</summary>
    /// <typeparam name="TResult">The result type of the source's task.</typeparam>
    /// <param name="state">The state of the source's task, its <see cref="Task.AsyncState"/>.</param>
    /// <param name="creationOptions">The options of the source's task.</param>
    /// <returns>The source.</returns>
    public static TaskCompletionSource<TResult> CreateGeneric<TResult>(object? state, TaskCreationOptions creationOptions)
    {
        RequireUnattached(creationOptions, GenericTaskCompletionSourceApi);
        return new TaskCompletionSource<TResult>(state, creationOptions);
    }

    /// <summary>Controlled <c>TaskCompletionSource.SetResult()</c>.</summary>
    public static void SetResult(TaskCompletionSource source) =>
        Complete(Require(TaskCompletionSourceApi), source.Task, source, static source => source.SetResult());

    /// <summary>Controlled <c>TaskCompletionSource.TrySetResult()</c>.</summary>
    public static bool TrySetResult(TaskCompletionSource source) =>
        TryComplete(Require(TaskCompletionSourceApi), source.Task, source, static source => source.TrySetResult());

    /// <summary>Controlled <c>TaskCompletionSource.SetException(Exception)</c>.</summary>
    public static void SetException(TaskCompletionSource source, Exception exception) =>
        Complete(Require(TaskCompletionSourceApi), source.Task, (source, exception), static completion => completion.source.SetException(completion.exception));

    /// <summary>Controlled <c>TaskCompletionSource.SetException(IEnumerable&lt;Exception&gt;)</c>.</summary>
    public static void SetException(TaskCompletionSource source, IEnumerable<Exception> exceptions) =>
        Complete(Require(TaskCompletionSourceApi), source.Task, (source, exceptions), static completion => completion.source.SetException(completion.exceptions));

    /// <summary>Controlled <c>TaskCompletionSource.TrySetException(Exception)</c>.</summary>
    public static bool TrySetException(TaskCompletionSource source, Exception exception) =>
        TryComplete(Require(TaskCompletionSourceApi), source.Task, (source, exception), static completion => completion.source.TrySetException(completion.exception));

    /// <summary>Controlled <c>TaskCompletionSource.TrySetException(IEnumerable&lt;Exception&gt;)</c>.</summary>
    public static bool TrySetException(TaskCompletionSource source, IEnumerable<Exception> exceptions) =>
        TryComplete(Require(TaskCompletionSourceApi), source.Task, (source, exceptions), static completion => completion.source.TrySetException(completion.exceptions));

    /// <summary>Controlled <c>TaskCompletionSource.SetCanceled()</c>.</summary>
    public static void SetCanceled(TaskCompletionSource source) =>
        Complete(Require(TaskCompletionSourceApi), source.Task, source, static source => source.SetCanceled());

    /// <summary>Controlled <c>TaskCompletionSource.SetCanceled(CancellationToken)</c>.</summary>
    public static void SetCanceled(TaskCompletionSource source, CancellationToken cancellationToken) =>
        Complete(Require(TaskCompletionSourceApi), source.Task, (source, cancellationToken), static completion => completion.source.SetCanceled(completion.cancellationToken));

    /// <summary>Controlled <c>TaskCompletionSource.TrySetCanceled()</c>.</summary>
    public static bool TrySetCanceled(TaskCompletionSource source) =>
        TryComplete(Require(TaskCompletionSourceApi), source.Task, source, static source => source.TrySetCanceled());

    /// <summary>Controlled <c>TaskCompletionSource.TrySetCanceled(CancellationToken)</c>.</summary>
    public static bool TrySetCanceled(TaskCompletionSource source, CancellationToken cancellationToken) =>
        TryComplete(Require(TaskCompletionSourceApi), source.Task, (source, cancellationToken), static completion => completion.source.TrySetCanceled(completion.cancellationToken));

    /// <summary>Controlled <c>TaskCompletionSource.SetFromTask(Task)</c>.</summary>
    public static void SetFromTask(TaskCompletionSource source, Task completedTask) =>
        Complete(Require(TaskCompletionSourceApi), source.Task, (source, completedTask), static completion => completion.source.SetFromTask(completion.completedTask), completedTask);

    /// <summary>Controlled <c>TaskCompletionSource.TrySetFromTask(Task)</c>.</summary>
    public static bool TrySetFromTask(TaskCompletionSource source, Task completedTask) =>
        TryComplete(Require(TaskCompletionSourceApi), source.Task, (source, completedTask), static completion => completion.source.TrySetFromTask(completion.completedTask), completedTask);

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.SetResult(TResult)</c>.</summary>
    public static void SetResult<TResult>(TaskCompletionSource<TResult> source, TResult result) =>
        Complete(Require(GenericTaskCompletionSourceApi), source.Task, (source, result), static completion => completion.source.SetResult(completion.result));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.TrySetResult(TResult)</c>.</summary>
    public static bool TrySetResult<TResult>(TaskCompletionSource<TResult> source, TResult result) =>
        TryComplete(Require(GenericTaskCompletionSourceApi), source.Task, (source, result), static completion => completion.source.TrySetResult(completion.result));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.SetException(Exception)</c>.</summary>
    public static void SetException<TResult>(TaskCompletionSource<TResult> source, Exception exception) =>
        Complete(Require(GenericTaskCompletionSourceApi), source.Task, (source, exception), static completion => completion.source.SetException(completion.exception));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.SetException(IEnumerable&lt;Exception&gt;)</c>.</summary>
    public static void SetException<TResult>(TaskCompletionSource<TResult> source, IEnumerable<Exception> exceptions) =>
        Complete(Require(GenericTaskCompletionSourceApi), source.Task, (source, exceptions), static completion => completion.source.SetException(completion.exceptions));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.TrySetException(Exception)</c>.</summary>
    public static bool TrySetException<TResult>(TaskCompletionSource<TResult> source, Exception exception) =>
        TryComplete(Require(GenericTaskCompletionSourceApi), source.Task, (source, exception), static completion => completion.source.TrySetException(completion.exception));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.TrySetException(IEnumerable&lt;Exception&gt;)</c>.</summary>
    public static bool TrySetException<TResult>(TaskCompletionSource<TResult> source, IEnumerable<Exception> exceptions) =>
        TryComplete(Require(GenericTaskCompletionSourceApi), source.Task, (source, exceptions), static completion => completion.source.TrySetException(completion.exceptions));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.SetCanceled()</c>.</summary>
    public static void SetCanceled<TResult>(TaskCompletionSource<TResult> source) =>
        Complete(Require(GenericTaskCompletionSourceApi), source.Task, source, static source => source.SetCanceled());

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.SetCanceled(CancellationToken)</c>.</summary>
    public static void SetCanceled<TResult>(TaskCompletionSource<TResult> source, CancellationToken cancellationToken) =>
        Complete(Require(GenericTaskCompletionSourceApi), source.Task, (source, cancellationToken), static completion => completion.source.SetCanceled(completion.cancellationToken));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.TrySetCanceled()</c>.</summary>
    public static bool TrySetCanceled<TResult>(TaskCompletionSource<TResult> source) =>
        TryComplete(Require(GenericTaskCompletionSourceApi), source.Task, source, static source => source.TrySetCanceled());

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.TrySetCanceled(CancellationToken)</c>.</summary>
    public static bool TrySetCanceled<TResult>(TaskCompletionSource<TResult> source, CancellationToken cancellationToken) =>
        TryComplete(Require(GenericTaskCompletionSourceApi), source.Task, (source, cancellationToken), static completion => completion.source.TrySetCanceled(completion.cancellationToken));

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.SetFromTask(Task&lt;TResult&gt;)</c>.</summary>
    public static void SetFromTask<TResult>(TaskCompletionSource<TResult> source, Task<TResult> completedTask) =>
        Complete(Require(GenericTaskCompletionSourceApi), source.Task, (source, completedTask), static completion => completion.source.SetFromTask(completion.completedTask), completedTask);

    /// <summary>Controlled <c>TaskCompletionSource&lt;TResult&gt;.TrySetFromTask(Task&lt;TResult&gt;)</c>.</summary>
    public static bool TrySetFromTask<TResult>(TaskCompletionSource<TResult> source, Task<TResult> completedTask) =>
        TryComplete(Require(GenericTaskCompletionSourceApi), source.Task, (source, completedTask), static completion => completion.source.TrySetFromTask(completion.completedTask), completedTask);

    // Outside a simulation the constructor is the BCL's. Rejecting before constructing matters: the BCL attaches the
    // promise to the running task as it constructs it, and that parent would then wait for a promise nobody completes.
    private static void RequireUnattached(TaskCreationOptions creationOptions, string api)
    {
        SimulationRuntimeDispatch.RequireSimulationThreadIfActive(api, ".ctor");
        if ((creationOptions & TaskCreationOptions.AttachedToParent) != 0
            && SimulationExecutionContext.TryGetCurrentRuntime(out _))
        {
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                api + "..ctor",
                "TaskCreationOptions.AttachedToParent attaches the promise to the task running it, and Clockwork's " +
                "controlled tasks do not model parent and child tasks: a parent waiting for an attached child would block " +
                "the simulation's logical thread.");
        }
    }

    // Requires the logical thread inside a simulation. Each shim passes it as its first argument, so it runs before the
    // shim reads the source's task.
    private static string Require(string api, [CallerMemberName] string member = "")
    {
        SimulationRuntimeDispatch.RequireSimulationThreadIfActive(api, member);
        return api;
    }

    // Completes the source through the BCL member (see SimulationCompletion), then publishes the release edge on its task.
    // A completion from another task first acquires that task's edges, since it reads that task's outcome.
    private static void Complete<TState>(
        string api,
        Task task,
        TState state,
        [CompletionCallback] Action<TState> complete,
        Task? antecedent = null)
    {
        Acquire(antecedent);
        SimulationCompletion.Run(state, complete, api);
        RaceSynchronization.Signal(task);
    }

    private static bool TryComplete<TState>(
        string api,
        Task task,
        TState state,
        [CompletionCallback] Func<TState, bool> complete,
        Task? antecedent = null)
    {
        Acquire(antecedent);
        if (!SimulationCompletion.Run(state, complete, api))
        {
            return false;
        }

        RaceSynchronization.Signal(task);
        return true;
    }

    private static void Acquire(Task? antecedent)
    {
        if (antecedent is not null)
        {
            RaceSynchronization.Wait(antecedent);
        }
    }
}
