using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks.Remoting;

namespace Orleans.DurableTasks;

public static class DurableTaskExtensions
{
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
    public static async ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> taskDefinition, string taskId, SchedulingOptions? options)
    {
        var typedTaskId = TaskId.Create(taskId);
        if (taskDefinition is not ISchedulableTask<TResult> schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        return await schedulableTask.ScheduleTypedAsync(typedTaskId, options);
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId) => ScheduleAsync(taskDefinition, taskId);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="options">The task scheduling options.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static async ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId, SchedulingOptions? options)
    {
        var typedTaskId = TaskId.Create(taskId);
        if (taskDefinition is not ISchedulableTask schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        return await schedulableTask.ScheduleUntypedAsync(typedTaskId, options);
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}" /> as a named step within the current workflow.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="stepId">The step identifier, which must be unique within the current context.</param>
    /// <returns>The result of invoking the task.</returns>
    public static ValueTask<TResult> AsStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
    {
        // Steps are only applicable nested within other durable tasks, so if we do not have an ambient context
        // then something has gone awry.
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // Create a new, nested task id for the step
        var taskId = currentContext.TaskId.Child(stepId);

        return currentContext.Runtime.EvaluateStepAsync(taskId, taskDefinition);
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask" /> as a named step within the current workflow.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="stepId">The step identifier, which must be unique within the current context.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    public static ValueTask AsStep(this DurableTask taskDefinition, string stepId)
    {
        // Steps are only applicable nested within other durable tasks, so if we do not have an ambient context
        // then something has gone awry.
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();
        var taskId = currentContext.TaskId.Child(stepId);
        var runtime = currentContext.Runtime;
        return runtime.EvaluateStepAsync(taskId, taskDefinition);
    }

    private static InvalidOperationException GetNonSchedulableTaskException() => new ("The provided task does not support scheduling. This may be because it is a local method or another non-serializable task type.");
}
