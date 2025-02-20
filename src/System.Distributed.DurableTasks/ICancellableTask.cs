namespace System.Distributed.DurableTasks;

/// <summary>
/// Interface implemented by <see cref="DurableTask"/> and <see cref="DurableTask{TResult}"/> implementations allowing them to be canceled.
/// </summary>
public interface ICancellableTask
{
    /// <summary>
    /// Cancels a task.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A task which completes when cancellation has been acknowledged.</returns>
    ValueTask CancelAsync(TaskId taskId, CancellationToken cancellationToken);
}


