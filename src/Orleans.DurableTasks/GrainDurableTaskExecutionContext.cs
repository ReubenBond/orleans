using Orleans.DurableTasks.Remoting;

namespace Orleans.DurableTasks;

internal sealed class GrainDurableTaskExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime, IDurableTaskState state) : DurableTaskContext(taskId)
{
    internal IDurableTaskGrainRuntime Runtime { get; } = runtime;

    internal IDurableTaskState State { get; } = state;

    protected internal override ValueTask<DurableTaskContext> EvaluateAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken) =>
        Runtime.EvaluateAsync(taskId, taskDefinition, cancellationToken);
}
