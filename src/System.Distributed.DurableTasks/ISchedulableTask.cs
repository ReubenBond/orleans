using System.Distributed.DurableTasks.Scheduling;
using Orleans.DurableTasks;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Interface implemented by <see cref="DurableTask"/> and <see cref="DurableTask{TResult}"/> implementations allowing them to be scheduled.
/// </summary>
public interface ISchedulableTask
{
    /// <summary>
    /// Schedules the task, returning a <see cref="DurableTaskContext"/> representing the scheduled task.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="options">The scheduling options.</param>
    /// <returns>A context representing the scheduled task.</returns>
    ValueTask<DurableTaskContext> ScheduleAsync(TaskId taskId, SchedulingOptions? options);
}
