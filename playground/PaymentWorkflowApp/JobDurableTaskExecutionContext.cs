using Orleans.DurableTasks;

internal sealed class JobDurableTaskExecutionContext : DurableTaskExecutionContext
{
    private readonly JobScheduler _jobScheduler;
    public JobTaskState State { get; }

    public JobDurableTaskExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : base(taskId)
    {
        _jobScheduler = jobScheduler;
        State = state;
    }

    protected override ValueTask<DurableTaskExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition) => _jobScheduler.EvaluateStepAsync(taskId, taskDefinition);
}
