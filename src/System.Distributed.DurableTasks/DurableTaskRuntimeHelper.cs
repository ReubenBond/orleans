
namespace System.Distributed.DurableTasks;

public static class DurableTaskRuntimeHelper
{
    /// <summary>
    /// Invokes a durable task.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="context">The task context.</param>
    /// <returns>The result of invocation.</returns>
    public static ValueTask<DurableTaskResponse> RunAsync(DurableTask task, DurableExecutionContext context) => task.RunAsync(context);

    public static CancellationToken GetCancellationToken(DurableExecutionContext context) => context.CancellationToken;

    /*
    /// <summary>
    /// Sets the result of a durable task context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <param name="result">The result.</param>
    public static void SetResult(DurableExecutionContext context, DurableTaskResponse result) => context.SetResult(result);
    */

    public static void SetCurrentContext(DurableExecutionContext? context) => DurableExecutionContext.SetCurrentContext(context);
    public static void SetCurrentContext(DurableExecutionContext? context, out DurableExecutionContext? previous) => DurableExecutionContext.SetCurrentContext(context, out previous);

    /*
    public static DurableTaskResponse Poll(DurableExecutionContext context)
    {
        var task = context.ResponseTask;
        return task.Status switch
        {
            TaskStatus.RanToCompletion => task.Result,
            _ => DurableTaskResponse.Pending,
        };  
    }

    public static async ValueTask<DurableTaskResponse> WaitAsync(DurableExecutionContext context, CancellationToken cancellationToken)
    {
        return await context.ResponseTask.WaitAsync(cancellationToken);
    }

    public static Task<DurableTaskResponse> GetCompletionTask(DurableExecutionContext context) => context.ResponseTask;
    */

    public static Task CancelAsync(DurableExecutionContext context, CancellationToken cancellationToken)
    {
        return context.CancelAsync(cancellationToken);
    }
}
