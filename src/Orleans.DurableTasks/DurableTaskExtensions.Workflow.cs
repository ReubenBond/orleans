using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

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
        if (taskDefinition is not ISchedulableTask schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(typedTaskId, options);
        return new ScheduledTask<TResult>(executionContext);
    }

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
    public static async ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId, SchedulingOptions? options)
    {
        var typedTaskId = TaskId.Create(taskId);
        if (taskDefinition is not ISchedulableTask schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(typedTaskId, options);
        return new UntypedScheduledTask(executionContext);
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
        var parentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // Create a new, nested task id for the step
        var taskId = parentContext.TaskId.Child(stepId);

        var executionContext = await parentContext.Runtime.EvaluateStepAsync(taskId, taskDefinition);
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
        var parentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();
        var taskId = parentContext.TaskId.Child(stepId);

        var executionContext = await parentContext.Runtime.EvaluateStepAsync(taskId, taskDefinition);
        await executionContext.AsUntypedValueTask();
    }

    private static InvalidOperationException GetNonSchedulableTaskException() => new ("The provided task does not support scheduling. This may be because it is a local method or another non-serializable task type.");
}
