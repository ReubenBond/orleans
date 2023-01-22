namespace Orleans.DurableTasks.Remoting;

public interface ISchedulableTask
{
    ValueTask<DurableTaskExecutionContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options);
}
