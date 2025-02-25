namespace System.Distributed.DurableTasks;

public abstract partial class DurableExecutionContext(TaskId id)
{
    private static readonly AsyncLocal<DurableExecutionContext?> Current = new();
    public static DurableExecutionContext? CurrentContext => Current.Value;
    public static DurableExecutionContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableExecutionContext)} is required but not present.");

    internal static void SetCurrentContext(DurableExecutionContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableExecutionContext? context, out DurableExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    public TaskId TaskId { get; } = id;
    internal CancellationToken CancellationToken => _cancellationTokenSource.Token;
    protected internal abstract ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract IScheduledTaskHandle GetChildTaskHandle(TaskId taskId);
    protected internal abstract TaskId CreateChildTaskId(string? name);

    // Get all children of this task.
    //protected abstract IEnumerable<DurableTaskContext> GetChildren();

    // Waits for all children to terminate.
    //protected abstract ValueTask WaitForChildrenAsync(CancellationToken cancellationToken);

    // Note that blocking on cancellation of a task from within that task would result in a deadlock
    // Cancels the task if it is scheduled or running. If the task is not scheduled or running, this method does nothing.
    internal async Task CancelAsync(CancellationToken cancellationToken)
    {
        await _cancellationTokenSource.CancelAsync();
        await CancelAsyncCore(cancellationToken);
    }

    protected virtual Task CancelAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;
}
