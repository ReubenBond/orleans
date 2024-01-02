namespace Orleans.DurableTasks.Remoting;

public interface ISchedulableTask
{
    ValueTask<DurableTaskContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options);
}
