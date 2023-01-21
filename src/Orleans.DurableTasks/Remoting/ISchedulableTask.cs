namespace Orleans.DurableTasks.Remoting;

public interface ISchedulableTask
{
    ValueTask<ScheduledTask> ScheduleUntypedAsync(TaskId taskId, SchedulingOptions? options);
}

public interface ISchedulableTask<TResult> : ISchedulableTask
{
    ValueTask<ScheduledTask<TResult>> ScheduleTypedAsync(TaskId taskId, SchedulingOptions? options);
}

