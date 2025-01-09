using System.Diagnostics.Contracts;
using System.Distributed.DurableTasks.Scheduling;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Extension methods for working with <see cref="DurableTask"/> and <see cref="DurableTask{TResult}"/> instances.
/// </summary>
public static class DurableTaskExtensions
{
    /// <summary>
    /// Gets an awaiter for the durable task. This schedules the task and awaits completion.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>An awaiter for the task.</returns>
    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task).GetAwaiter();

    /// <summary>
    /// Gets an awaiter for the durable task. This schedules the task and awaits completion.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>An awaiter for the task.</returns>
    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).GetAwaiter();

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask WithId(this DurableTask task, string id)
    {
        var result = new ConfiguredDurableTask(task);
        result.WithId(id);
        return result;
    }

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string id)
    {
        var result = new ConfiguredDurableTask<TResult>(task);
        result.WithId(id);
        return result;
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="task">The task.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task) => task.ScheduleAsync(taskId: null, options: null);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> taskDefinition, string taskId) => taskDefinition.ScheduleAsync(taskId, options: null);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="options">The task scheduling options.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> taskDefinition, string? taskId, SchedulingOptions? options = null)
    {
        var configuredTask = new ConfiguredDurableTask<TResult>(taskDefinition);
        if (taskId is not null)
        {
            configuredTask = configuredTask.WithId(taskId);
        }

        if (options is not null)
        {
            configuredTask = configuredTask.WithSchedulingOptions(options);
        }

        return configuredTask.ScheduleAsync();
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition) => taskDefinition.ScheduleAsync(taskId: null, options: null);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId) => taskDefinition.ScheduleAsync(taskId, options: null);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="options">The task scheduling options.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string? taskId, SchedulingOptions? options)
    {
        var configuredTask = new ConfiguredDurableTask(taskDefinition);
        if (taskId is not null)
        {
            configuredTask = configuredTask.WithId(taskId);
        }

        if (options is not null)
        {
            configuredTask = configuredTask.WithSchedulingOptions(options);
        }

        return configuredTask.ScheduleAsync();
    }
}
