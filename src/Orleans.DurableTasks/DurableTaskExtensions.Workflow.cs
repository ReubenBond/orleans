namespace Orleans.DurableTasks;

public static class DurableTaskExtensions
{
    // Return a "DurableTaskStepAwaitable<TResult>" which sets the appropriate context around the invocation.
    public static ValueTask<TResult> AsWorkflow<TResult>(this DurableTask<TResult> taskDefinition, string workflowId)   
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

    public static ValueTask AsWorfklow(this DurableTask taskDefinition, string workflowId)
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

    // Return a "DurableTaskStepAwaitable<TResult>" which sets the appropriate context around the invocation.
    public static ValueTask<TResult> AsWorkflowStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
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

    // See above
    public static ValueTask AsWorfklowStep(this DurableTask taskDefinition, string stepId)
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
