using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

public abstract partial class DurableTaskExecutionContext(TaskId taskId)
{
    private static readonly AsyncLocal<DurableTaskExecutionContext?> Current = new();

    public static DurableTaskExecutionContext? CurrentContext => Current.Value;
    public static DurableTaskExecutionContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskExecutionContext)} is required but not present.");
    public static void Reset() => Current.Value = null;
    public static void SetCurrentContext(DurableTaskExecutionContext? context) => Current.Value = context;
    public static void SetCurrentContext(DurableTaskExecutionContext? context, out DurableTaskExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    public TaskId TaskId { get; } = taskId;
    internal CancellationTokenSource CancellationTokenSource { get; } = new();
    public CancellationToken CancellationToken => CancellationTokenSource.Token;
    protected internal abstract ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
}

public static class DurableTaskInternal
{
    public static ValueTask<Response> InvokeAsync(DurableTask task, DurableTaskExecutionContext context) => task.InvokeAsync(context);
}

internal sealed class GrainDurableTaskExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime, IDurableTaskState state) : DurableTaskExecutionContext(taskId)
{
    internal IDurableTaskGrainRuntime Runtime { get; } = runtime;
    internal IDurableTaskState State { get; } = state;

    protected internal override ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken) =>
        Runtime.EvaluateStepAsync(taskId, taskDefinition, cancellationToken);
}
