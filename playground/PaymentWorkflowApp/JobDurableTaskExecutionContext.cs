using Orleans.DurableTasks;
namespace PaymentWorkflowApp;

internal sealed class JobDurableTaskExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : DurableTaskContext(taskId)
{
    private readonly JobScheduler _jobScheduler = jobScheduler;
    public JobTaskState State { get; } = state;

    protected override ValueTask<DurableTaskContext> EvaluateAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
        => _jobScheduler.EvaluateStepAsync(taskId, taskDefinition, cancellationToken);
}
