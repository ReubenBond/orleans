namespace Orleans.Testing.Simulation;

/// <summary>
/// A deterministic task scheduler that queues tasks through a <see cref="SimulationSchedulerLane"/>
/// and executes them only when explicitly stepped.
/// </summary>
public sealed class SimulationTaskScheduler(SimulationSchedulerLane schedulerLane) : TaskScheduler
{
    /// <inheritdoc />
    protected override IEnumerable<Task>? GetScheduledTasks() => schedulerLane.GetItemsOfType<ScheduledTaskItem, Task>(item => item.Task);

    /// <summary>
    /// Gets the scheduled tasks in the queue.
    /// </summary>
    public IReadOnlyList<Task> Tasks => schedulerLane.GetItemsOfType<ScheduledTaskItem, Task>(item => item.Task);

    /// <inheritdoc />
    protected override void QueueTask(Task task) => schedulerLane.Enqueue(new ScheduledTaskItem(task, this));

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    /// <summary>
    /// Gets the underlying scheduler lane for this task scheduler.
    /// </summary>
    public object UnderlyingScheduler => schedulerLane;

    internal SimulationSchedulerLane Lane => schedulerLane;

    /// <summary>
    /// Checks if this scheduler shares the same underlying queue as a synchronization context.
    /// </summary>
    /// <param name="syncCtx">The synchronization context to compare with.</param>
    /// <returns>True if they share the same underlying queue.</returns>
    public bool IsSameScheduler(SynchronizationContext syncCtx) => syncCtx is SimulationSynchronizationContext simSyncCtx && simSyncCtx.UnderlyingScheduler.Equals(UnderlyingScheduler);

    private sealed class ScheduledTaskItem(Task task, SimulationTaskScheduler scheduler) : ScheduledItem
    {
        public Task Task => task;

        internal override string Kind => "task";

        internal override string Description => "Scheduled task";

        // Each queued task is an independently scheduled unit of work, so it runs as a fresh strand.
        internal override void Invoke() =>
            Runtime.Threading.SimulationSynchronizationFlow.RunAsNewStrand(
                () => Shims.System.Threading.Tasks.ControlledTaskScheduler.ExecuteAsCurrentStrand(scheduler.TryExecuteTask, task));
    }
}
