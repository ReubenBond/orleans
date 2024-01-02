using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

public abstract partial class DurableTaskContext(TaskId taskId)
{
    private static readonly AsyncLocal<DurableTaskContext?> Current = new();

    public static DurableTaskContext? CurrentContext => Current.Value;
    public static DurableTaskContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskContext)} is required but not present.");
    public static void Reset() => Current.Value = null;
    public static void SetCurrentContext(DurableTaskContext? context) => Current.Value = context;
    public static void SetCurrentContext(DurableTaskContext? context, out DurableTaskContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    public TaskId TaskId { get; } = taskId;
    internal CancellationTokenSource CancellationTokenSource { get; } = new();
    public CancellationToken CancellationToken => CancellationTokenSource.Token;
    protected internal abstract ValueTask<DurableTaskContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
}

internal sealed class GrainDurableTaskExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime, IDurableTaskState state) : DurableTaskContext(taskId)
{
    internal IDurableTaskGrainRuntime Runtime { get; } = runtime;
    internal IDurableTaskState State { get; } = state;

    protected internal override ValueTask<DurableTaskContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken) =>
        Runtime.EvaluateAsync(taskId, taskDefinition, cancellationToken);
}

public static class DurableTaskRuntimeHelper
{
    /// <summary>
    /// Invokes a durable task.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="context">The task context.</param>
    /// <returns>The result of invocation.</returns>
    public static ValueTask<Response> InvokeAsync(DurableTask task, DurableTaskContext context) => task.InvokeAsync(context);
}
