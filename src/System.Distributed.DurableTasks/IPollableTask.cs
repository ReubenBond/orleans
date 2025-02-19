using Orleans.Serialization.Invocation;

namespace System.Distributed.DurableTasks;

/// <summary>
/// A task which can be polled for completion.
/// </summary>
public interface IPollableTask
{
    /// <summary>
    /// Polls the task to determine whether it has completed, returning the result if it has completed, or a non-final result (<see cref="Response.IsFinal"/> returns <see langword="false"/>) if it has not.
    /// </summary>
    /// <returns>The current task result.</returns>
    ValueTask<Response> PollAsync();
}
