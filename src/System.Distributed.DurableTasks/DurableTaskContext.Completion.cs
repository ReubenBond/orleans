using System.Diagnostics;

namespace System.Distributed.DurableTasks;

public abstract partial class DurableTaskContext
{
    private readonly TaskCompletionSource<DurableTaskResponse> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously); 

    internal Task<DurableTaskResponse> GetResponseAsync() => _tcs.Task;
    internal DurableTaskResultAwaitable<TResult> GetResultAsync<TResult>() => new(this);

    internal void SetResult(DurableTaskResponse response)
    {
        Debug.Assert(response.IsCompleted, "DurableTask completed with an invalid, non-terminal response.");
        _tcs.SetResult(response);
    }
}
