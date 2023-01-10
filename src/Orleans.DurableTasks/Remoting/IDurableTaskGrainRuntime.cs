using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Remoting;

public interface IDurableTaskGrainExtension : IGrainExtension
{
    // Called by DurableTaskRequest.Invoke to ensure that a task is scheduled
    ValueTask<Response> ScheduleOrPollAsync(IDurableTaskRequest request);

    // Called when a remotely scheduled request completes
    ValueTask OnResponse(TaskId taskId, Response response);
}

public interface IDurableTaskGrainRuntime : IDurableTaskGrainExtension
{
    // Similar to `ScheduleOrPollAsync`, except that:
    // It is intended for local `DurableTask` methods (steps) versus remotely issued requests
    // The DurableTaskRequest is not 
    // It blocks until the response has been completed.
    ValueTask<Response> InvokeAsync(IDurableTaskRequest request);
}

/*
 * Grain activates
 * Grain enumerates stored pending tasks and re-invokes any which are not completed.
 *   * Some tasks will not be directly invokable, since they represent local methods on a grain (not remote requests to the grain)
     * Those tasks do not need to be invoked.
 */
