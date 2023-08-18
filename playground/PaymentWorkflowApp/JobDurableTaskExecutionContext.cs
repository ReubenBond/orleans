using Orleans.DurableTasks;

internal sealed class JobDurableTaskExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : DurableTaskExecutionContext(taskId)
{
    private readonly JobScheduler _jobScheduler = jobScheduler;
    public JobTaskState State { get; } = state;

    protected override ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
        => _jobScheduler.EvaluateStepAsync(taskId, taskDefinition, cancellationToken);
}
