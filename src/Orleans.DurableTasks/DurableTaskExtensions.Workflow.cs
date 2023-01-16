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
    public static ValueTask<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> taskDefinition, string taskId)   
    {
        throw new NotImplementedException();
        /*
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // See if a child node exists for this context already.
        var childContext = currentContext.GetOrCreateChildNode(workflowId, taskDefinition, out var exists);
        if (exists)
        {
            // The child already existed. If it is complete, return the result here.
            var resultTask = childContext.AsValueTask<TResult>();
            if (resultTask.IsCompleted)
            {
                return resultTask;
            }
        }

        // The child task is either in-progress or not yet started. Either way,
        // use the (potentially in-progress) execution context to invoke it and attempt to complete it.
        return taskDefinition.InvokeAsync(childContext);
        */
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static ValueTask<ScheduledTask> ScheduleAsync(this DurableTask taskDefinition, string taskId)
    {
        throw new NotImplementedException();
        /*
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // See if a child node exists for this context already.
        var childContext = currentContext.GetOrCreateChildNode(workflowId, taskDefinition, out var exists);
        if (exists)
        {
            // The child already existed. If it is complete, return the result here.
            var resultTask = childContext.AsUntypedValueTask();
            if (resultTask.IsCompleted)
            {
                return resultTask;
            }
        }

        // The child task is either in-progress or not yet started. Either way,
        // use the (potentially in-progress) execution context to invoke it and attempt to complete it.
        return taskDefinition.InvokeAsync(childContext);
        */
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
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // See if a child node exists for this context already.
        var childContext = currentContext.GetOrCreateChildNode(stepId, taskDefinition, out var exists);
        if (exists)
        {
            // The child already existed. If it is complete, return the result here.
            var resultTask = childContext.AsValueTask<TResult>();
            if (resultTask.IsCompleted)
            {
                return resultTask;
            }
        }

        // The child task is either in-progress or not yet started. Either way,
        // use the (potentially in-progress) execution context to invoke it and attempt to complete it.
        return taskDefinition.InvokeAsync(childContext);
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask" /> as a named step within the current workflow.
    /// </summary>
    /// <param name="taskDefinition">The task.</param>
    /// <param name="stepId">The step identifier, which must be unique within the current context.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    public static ValueTask AsStep(this DurableTask taskDefinition, string stepId)
    {
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // See if a child node exists for this context already.
        var childContext = currentContext.GetOrCreateChildNode(stepId, taskDefinition, out var exists);
        if (exists)
        {
            // The child already existed. If it is complete, return the result here.
            var resultTask = childContext.AsUntypedValueTask();
            if (resultTask.IsCompleted)
            {
                return resultTask;
            }
        }

        // The child task is either in-progress or not yet started. Either way,
        // use the (potentially in-progress) execution context to invoke it and attempt to complete it.
        return taskDefinition.InvokeAsync(childContext);
    }
}
