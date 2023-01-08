namespace Orleans.DurableTasks;

public static class DurableTaskExtensions
{
    public static async ValueTask<TResult> InvokeAsync<TResult>(this DurableTask<TResult> taskDefinition, TaskId taskId)
    {
        return await await taskDefinition.ScheduleAsync(taskId).ConfigureAwait(false);
    }

    public static async ValueTask InvokeAsync(this DurableTask taskDefinition, TaskId taskId)
    {
        await await taskDefinition.ScheduleAsync(taskId).ConfigureAwait(false);
    }

    // Return a "DurableTaskStepAwaitable<TResult>" which sets the appropriate context around the invocation.
    public static ValueTask<TResult> AsWorkflowStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
    {
        var currentContext = DurableTaskExecutionContext.GetCurrentContextOrThrow();

        // See if a child node exists for this context already.
        // If so, there are two cases:
        // - the task has completed, in which case return the result.
        // - the task is incomplete, in which case we will need to execute it.
        // If not, create a new child node and invoke the task.
        var childContext = currentContext.GetOrCreateChildNode(stepId, out var exists);
        if (exists)
        {
            if (childContext.IsCompleted)
            {
                if (childContext.Result is { } result)
                {
                    return new ValueTask<TResult>((TResult)result);
                }
                else if (childContext.Exception is { SourceException: { } exception } )
                {
                    return ValueTask.FromException<TResult>(exception);
                }
                else if (childContext.IsCancellationRequested)
                {
                    // Consider tracking a CancellationToken
                    return ValueTask.FromException<TResult>(new OperationCanceledException());
                }
            }
        }

        await Task.Delay(1).ConfigureAwait(false);
        // Check the current durable task context
        // If it does not exist, throw:
        //   * Steps can only exist within a durable execution context

        // Check to see if this step has been completed already.
        // If the step has been completed during the current RunId (the invocation, which should be incremented each time the task is started), throw:
        //   * This might be a loop or a duplicate step id. Give an informative error.
        // If the step was completed during a previous RunId, return the result from the previous invocation.

        // -- up until this point, this method should execute synchronously --
        // If the step has not completed, create a new nested durable execution context and invoke the task.
        // When the task completes, replace its entry with the completed result and persist the current state.

        // Return the result to the caller.
        return default!;
    }

    // See above
    public static ValueTask AsWorfklowStep(this DurableTask taskDefinition, string stepId)
    {
        return default;
    }
}
