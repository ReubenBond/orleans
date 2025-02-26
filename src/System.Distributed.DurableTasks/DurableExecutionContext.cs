namespace System.Distributed.DurableTasks;

public abstract partial class DurableExecutionContext(TaskId id)
{
    private static readonly AsyncLocal<DurableExecutionContext?> Current = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public static DurableExecutionContext? CurrentContext => Current.Value;

    internal static void SetCurrentContext(DurableExecutionContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableExecutionContext? context, out DurableExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    public TaskId TaskId { get; } = id;

    protected internal CancellationToken CancellationToken => _cancellationTokenSource.Token;

    protected internal abstract ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract IScheduledTaskHandle GetChildTaskHandle(TaskId taskId);
    protected internal abstract TaskId CreateChildTaskId(string? name);

    // Note that blocking on cancellation of a task from within that task would result in a deadlock
    // Cancels the task if it is scheduled or running. If the task is not scheduled or running, this method does nothing.
    internal async Task CancelAsync(CancellationToken cancellationToken)
    {
        await _cancellationTokenSource.CancelAsync();
        await CancelAsyncCore(cancellationToken);
    }

    protected virtual Task CancelAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;
}
