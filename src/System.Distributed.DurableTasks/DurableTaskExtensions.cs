using System.Diagnostics.Contracts;

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
    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task, TaskId.CreateRandom()).GetAwaiter();

    /// <summary>
    /// Gets an awaiter for the durable task. This schedules the task and awaits completion.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>An awaiter for the task.</returns>
    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task, TaskId.CreateRandom()).GetAwaiter();

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="taskId">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask WithId(this DurableTask task, TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return new ConfiguredDurableTask(task, taskId);
    }

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="taskId">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask WithId(this DurableTask task, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return new ConfiguredDurableTask(task, TaskId.Create(taskId));
    }

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="taskId">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return new ConfiguredDurableTask<TResult>(task, taskId);
    }

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="taskId">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return new ConfiguredDurableTask<TResult>(task, TaskId.Create(taskId));
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="task">The task.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, CancellationToken cancellationToken = default) => task.ScheduleAsyncCore(taskId: null, cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> taskDefinition, string taskId, CancellationToken cancellationToken = default) => taskDefinition.ScheduleAsyncCore(taskId, cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    internal static ValueTask<ScheduledTask<TResult>> ScheduleAsyncCore<TResult>(this DurableTask<TResult> taskDefinition, string? taskId, CancellationToken cancellationToken)
    {
        var configuredTask = new ConfiguredDurableTask<TResult>(taskDefinition, taskId is null ? TaskId.CreateRandom() : TaskId.Create(taskId));
        return configuredTask.ScheduleAsync(cancellationToken);
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, CancellationToken cancellationToken = default) => taskDefinition.ScheduleAsyncCore(taskId: null, cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId, CancellationToken cancellationToken = default) => taskDefinition.ScheduleAsyncCore(taskId, cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsyncCore(this DurableTask taskDefinition, string? taskId, CancellationToken cancellationToken)
    {
        var configuredTask = new ConfiguredDurableTask(taskDefinition, taskId is null ? TaskId.CreateRandom() : TaskId.Create(taskId));
        return configuredTask.ScheduleAsync(cancellationToken);
    }
}
