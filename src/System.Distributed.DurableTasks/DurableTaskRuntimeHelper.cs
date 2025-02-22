namespace System.Distributed.DurableTasks;

public static class DurableTaskRuntimeHelper
{
    /// <summary>
    /// Invokes a durable task.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="context">The task context.</param>
    /// <returns>The result of invocation.</returns>
    public static ValueTask<DurableTaskResponse> RunAsync(DurableTask task, DurableTaskContext context) => task.RunAsync(context);

    /// <summary>
    /// Sets the result of a durable task context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <param name="result">The result.</param>
    public static void SetResult(DurableTaskContext context, DurableTaskResponse result) => context.SetResult(result);

    public static void SetCurrentContext(DurableTaskContext? context) => DurableTaskContext.SetCurrentContext(context);
    public static void SetCurrentContext(DurableTaskContext? context, out DurableTaskContext? previous) => DurableTaskContext.SetCurrentContext(context, out previous);

    public static DurableTaskResponse Poll(DurableTaskContext context)
    {
        var task = context.ResponseTask;
        return task.Status switch
        {
            TaskStatus.RanToCompletion => task.Result,
            _ => DurableTaskResponse.Pending,
        };  
    }

    public static async ValueTask<DurableTaskResponse> WaitAsync(DurableTaskContext context, CancellationToken cancellationToken)
    {
        return await context.ResponseTask.WaitAsync(cancellationToken);
    }

    public static Task<DurableTaskResponse> GetCompletionTask(DurableTaskContext context) => context.ResponseTask;
}
