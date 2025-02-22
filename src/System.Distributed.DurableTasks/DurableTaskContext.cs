namespace System.Distributed.DurableTasks;

public abstract partial class DurableTaskContext(TaskId id)
{
    private static readonly AsyncLocal<DurableTaskContext?> Current = new();
    public static DurableTaskContext? CurrentContext => Current.Value;
    public static DurableTaskContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskContext)} is required but not present.");

    internal static void SetCurrentContext(DurableTaskContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableTaskContext? context, out DurableTaskContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    public TaskId Id { get; } = id;
    internal CancellationToken CancellationToken => _cancellationTokenSource.Token;
    protected internal abstract ValueTask<DurableTaskResponse> RunChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract TaskId CreateChildTaskId(string? name);

    // Get all children of this task.
    //protected abstract IEnumerable<DurableTaskContext> GetChildren();

    // Waits for all children to terminate.
    //protected abstract ValueTask WaitForChildrenAsync(CancellationToken cancellationToken);

    // Note that blocking on cancellation of a task from within that task would result in a deadlock
    // Cancels the task if it is scheduled or running. If the task is not scheduled or running, this method does nothing.
    internal async Task CancelAsync()
    {
        //await SignalCancellationAsyncCore(cancellationToken);
        await _cancellationTokenSource.CancelAsync();
    }

    //protected abstract ValueTask SignalCancellationAsyncCore(CancellationToken cancellationToken);
}
