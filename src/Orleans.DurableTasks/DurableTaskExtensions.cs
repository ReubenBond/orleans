using System.Diagnostics.Contracts;

namespace Orleans.DurableTasks;

public static class DurableTaskExtensions
{

    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task).GetAwaiter();

    /// <summary>
    /// Sets the identifier for this task.
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

    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).GetAwaiter();

    /// <summary>
    /// Sets the identifier for this task.
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
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task) => ScheduleAsync(task, taskId: null, options: null);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> taskDefinition, string taskId) => ScheduleAsync(taskDefinition, taskId, options: null);

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
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition) => ScheduleAsync(taskDefinition, taskId: null, options: null);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId) => ScheduleAsync(taskDefinition, taskId, options: null);

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

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}" /> as a named step within the current workflow.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="stepId">The step identifier, which must be unique within the current context.</param>
    /// <returns>The result of invoking the task.</returns>
    public static async ValueTask<TResult> AsStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
    {
        // Steps are only applicable nested within other durable tasks, so if we do not have an ambient context
        // then something has gone awry.
        var parentContext = DurableTaskContext.GetCurrentContextOrThrow();

        // Create a new, nested task id for the step
        var taskId = parentContext.Id.Child(stepId);

        var executionContext = await parentContext.EvaluateAsync(taskId, taskDefinition, CancellationToken.None);
        return await executionContext.GetResultAsync<TResult>();
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask" /> as a named step within the current workflow.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="stepId">The step identifier, which must be unique within the current context.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    public static async ValueTask AsStep(this DurableTask taskDefinition, string stepId)
    {
        // Steps are only applicable nested within other durable tasks, so if we do not have an ambient context
        // then something has gone awry.
        var parentContext = DurableTaskContext.GetCurrentContextOrThrow();
        var taskId = parentContext.Id.Child(stepId);

        var executionContext = await parentContext.EvaluateAsync(taskId, taskDefinition, CancellationToken.None);
        await executionContext.AsUntypedValueTask();
    }
}
