using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// <para>
/// The simulation coordinator as a <see cref="TaskScheduler"/>, on which the controlled <c>Task.Start</c> and
/// <c>Task.RunSynchronously</c> run a task constructed with <c>new Task(...)</c>. A queued task runs as a fresh controlled
/// operation on a fresh strand, as <c>Task.Run</c>'s body does, and a task run inline runs on the calling logical thread
/// as the caller's strand. The BCL keeps the task's state, so its status transitions and its validation of a second start
/// are the BCL's. A task constructed through the controlled constructors hides this scheduler, so inside it
/// <see cref="TaskScheduler.Current"/> is <see cref="TaskScheduler.Default"/> (see <see cref="ControlledTask.Create(Action)"/>).
/// </para>
/// <para>
/// Work reaches this scheduler only on the simulation's logical thread: it runs a task inline only there, and queuing
/// to it from another thread (uninstrumented code continuing on <see cref="TaskScheduler.Current"/> after a foreign
/// completion) is reported as a thread-affinity violation.
/// </para>
/// </summary>
internal sealed class ControlledTaskScheduler : TaskScheduler
{
    private const string QueueTaskApi = "System.Threading.Tasks.TaskScheduler.QueueTask";
    private const string RunSynchronouslyApi = "System.Threading.Tasks.Task.RunSynchronously";

    // The task RunInline is running on this thread, which the BCL queues instead only if it declines to inline it.
    [ThreadStatic]
    private static Task? t_inlineTask;

    // The identity the task a Clockwork scheduler is executing on this thread runs under; see ExecuteAsCurrentStrand.
    [ThreadStatic]
    private static (SimulationExecutionSnapshot Snapshot, long StrandId)? t_executingIdentity;

    private ControlledTaskScheduler()
    {
    }

    /// <summary>Gets the scheduler.</summary>
    public static ControlledTaskScheduler Instance { get; } = new();

    /// <summary>
    /// <para>
    /// The one scheduler policy of every controlled API which takes a <see cref="TaskScheduler"/>: resolves the scheduler
    /// to the simulation snapshot, and so the node, its work runs under.
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="TaskScheduler.Default"/>, which the controlled APIs map to the coordinator, and this scheduler run
    /// work under the caller's snapshot.</item>
    /// <item>A <see cref="SimulationTaskScheduler"/> of this simulation runs work on its lane's node, so the work suspends
    /// and crashes with that node, as work queued to the lane does.</item>
    /// <item>Any other scheduler is rejected: work queued to it would run outside the coordinator.</item>
    /// </list>
    /// </summary>
    /// <param name="scheduler">The scheduler the caller named.</param>
    /// <param name="caller">The caller's simulation snapshot.</param>
    /// <param name="category">The calling API's category.</param>
    /// <param name="apiName">The calling API.</param>
    /// <returns>The snapshot the work runs under.</returns>
    /// <exception cref="SimulationApiException">The scheduler does not run work on the coordinator.</exception>
    public static SimulationExecutionSnapshot ResolveTarget(
        TaskScheduler scheduler,
        SimulationExecutionSnapshot caller,
        SimulationApiCategory category,
        string apiName)
    {
        if (ReferenceEquals(scheduler, TaskScheduler.Default) || scheduler is ControlledTaskScheduler)
        {
            return caller;
        }

        if (scheduler is SimulationTaskScheduler { Lane: var lane } && ReferenceEquals(lane.Scheduler, caller.Runtime.Scheduler))
        {
            return Equals(lane.Node, caller.Node)
                ? caller
                : new SimulationExecutionSnapshot(caller.Runtime, lane.Node, SimulationLogicalExecutionId.None);
        }

        throw new SimulationApiException(
            category,
            apiName,
            "a custom TaskScheduler is not supported inside a simulation because work queued to it would run " +
            "outside Clockwork's controlled logical strand. Use TaskScheduler.Default, a SimulationTaskScheduler of " +
            "this simulation, or an overload without a scheduler.");
    }

    /// <summary>
    /// Runs <paramref name="task"/> on the calling logical thread, as the BCL runs a task inline. A task with attached
    /// children - one uninstrumented code constructed without <see cref="TaskCreationOptions.DenyChildAttach"/>, say -
    /// completes only once they do; the inline run waits for them with the controlled wait (see
    /// <see cref="TryExecuteTaskInline"/>), so the BCL's <c>RunSynchronously</c> finds the task complete and never
    /// blocks the logical thread on them.
    /// </summary>
    /// <param name="task">The task, in its created state.</param>
    public static void RunInline(Task task)
    {
        Task? outer = t_inlineTask;
        t_inlineTask = task;
        try
        {
            ExecuteAsCurrentStrand(static task => { task.RunSynchronously(Instance); return true; }, task);
        }
        finally
        {
            t_inlineTask = outer;
        }

        Debug.Assert(task.IsCompleted, "RunSynchronously returned before the task it ran inline completed.");
        RaceSynchronization.Signal(task);
    }

    /// <summary>
    /// Executes <paramref name="task"/> through <paramref name="execute"/> as the calling strand on the calling node. The
    /// BCL runs a task in the <see cref="ExecutionContext"/> captured when it was constructed, which carries its creator's
    /// strand and node; a task constructed through the controlled <c>Task</c> constructors re-applies the identity
    /// recorded here inside that context (see <see cref="RunAsExecutingTask"/>), so it runs as the strand which executes
    /// it, as on a real thread.
    /// </summary>
    /// <param name="execute">Executes the task: the scheduler's <c>TryExecuteTask</c>, or an inline run.</param>
    /// <param name="task">The task.</param>
    /// <returns>What <paramref name="execute"/> returns.</returns>
    internal static bool ExecuteAsCurrentStrand(Func<Task, bool> execute, Task task)
    {
        var outer = t_executingIdentity;
        t_executingIdentity = SimulationExecutionContext.Current is { } snapshot
            ? (snapshot, SimulationSynchronizationFlow.CurrentId)
            : null;
        try
        {
            return execute(task);
        }
        finally
        {
            t_executingIdentity = outer;
        }
    }

    /// <summary>
    /// Runs a controlled task's delegate under the identity <see cref="ExecuteAsCurrentStrand"/> recorded for it,
    /// consuming that identity so work the delegate executes in turn records its own.
    /// </summary>
    /// <param name="body">The task's delegate, bound to its state.</param>
    internal static void RunAsExecutingTask(Action body)
    {
        var identity = t_executingIdentity;
        if (identity is not { } executing)
        {
            body();
            return;
        }

        t_executingIdentity = null;
        try
        {
            SimulationTaskRuntime.RunInSimulationSnapshot(executing.Snapshot, executing.StrandId, body);
        }
        finally
        {
            t_executingIdentity = identity;
        }
    }

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        if (ReferenceEquals(task, t_inlineTask))
        {
            // The BCL would block this thread until the queued task ran, which only this thread can run it.
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                RunSynchronouslyApi,
                "the task could not run inline (the calling thread's stack is nearly exhausted), and waiting for it " +
                "to run elsewhere would block the simulation's logical thread.");
        }

        SimulationTaskRuntime.QueueWork(
            () =>
            {
                ExecuteAsCurrentStrand(TryExecuteTask, task);
                RaceSynchronization.Signal(task);
            },
            QueueTaskApi,
            flowExecutionContext: false);
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        if (taskWasPreviouslyQueued || !IsSimulationThread() || !TryExecuteTask(task))
        {
            return false;
        }

        // A task whose delegate has returned stays incomplete while it waits for attached children, which only other
        // controlled operations can complete. RunSynchronously would block this thread on them, so the coordinator runs
        // that work here instead, until the task completes.
        if (ReferenceEquals(task, t_inlineTask) && !task.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(task, RunSynchronouslyApi, CancellationToken.None);
        }

        return true;
    }

    /// <inheritdoc />
    protected override IEnumerable<Task> GetScheduledTasks() => [];

    private static bool IsSimulationThread() =>
        SimulationExecutionContext.Current is { Runtime.Scheduler.IsSimulationThread: true };
}
