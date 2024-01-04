using Orleans.DurableTasks;
using Orleans.Serialization.Invocation;
namespace PaymentWorkflowApp;

internal sealed class JobDurableTaskExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : DurableTaskContext(taskId)
{
    private readonly JobScheduler _jobScheduler = jobScheduler;
    public JobTaskState State { get; } = state;

    protected override ValueTask<DurableTaskContext> EvaluateAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
        => _jobScheduler.EvaluateStepAsync(taskId, taskDefinition, cancellationToken);

    protected override ValueTask<Response> InvokeAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
        => _jobScheduler.InvokeAsync(taskId, taskDefinition, cancellationToken);

    private int _nextChildId = 1;
    protected override TaskId CreateChildTaskId() => TaskId.Create(_nextChildId++.ToString());
}
