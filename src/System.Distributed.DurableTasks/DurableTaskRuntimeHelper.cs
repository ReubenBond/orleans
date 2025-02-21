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

    public static async ValueTask<DurableTaskResponse> GetResponseAsync(DurableTaskContext context, CancellationToken cancellationToken)
    {
        return await context.GetResponseAsync().WaitAsync(cancellationToken);
    }
}
