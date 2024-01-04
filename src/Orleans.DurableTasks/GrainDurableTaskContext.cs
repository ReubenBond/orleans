using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

internal sealed class GrainDurableTaskContext(TaskId taskId, IDurableTaskGrainRuntime runtime, IDurableTaskState state) : DurableTaskContext(taskId)
{
    internal IDurableTaskGrainRuntime Runtime { get; } = runtime;

    internal IDurableTaskState State { get; } = state;

    protected internal override ValueTask<DurableTaskContext> EvaluateAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken) =>
        Runtime.EvaluateAsync(taskId, taskDefinition, cancellationToken);

    protected internal override ValueTask<Response> InvokeAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken) =>
        Runtime.InvokeAsync(taskId, taskDefinition, cancellationToken);

    private int _nextChildId = 0;
    protected internal override TaskId CreateChildTaskId() => Id.Child((++_nextChildId).ToString());
}
