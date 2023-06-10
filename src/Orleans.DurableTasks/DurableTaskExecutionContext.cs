using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

public abstract partial class DurableTaskExecutionContext
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

    public TaskId TaskId { get; }

    public DurableTaskExecutionContext(TaskId taskId)
    {
        TaskId = taskId;
    }

    protected internal abstract ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition);
}

public static class DurableTaskInternal
{
    public static ValueTask<Response> InvokeAsync(DurableTask task, DurableTaskExecutionContext context) => task.InvokeAsync(context);
}

internal sealed class GrainDurableTaskExecutionContext : DurableTaskExecutionContext
{
    internal IDurableTaskGrainRuntime Runtime { get; }
    internal DurableTaskState State { get; }

    public GrainDurableTaskExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime, DurableTaskState state) : base(taskId)
    {
        Runtime = runtime;
        State = state;
    }

    protected internal override ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition) =>
        Runtime.EvaluateStepAsync(taskId, taskDefinition);
}
