using System.Diagnostics;

namespace System.Distributed.DurableTasks;

public abstract partial class DurableTaskContext
{
    private readonly TaskCompletionSource<DurableTaskResponse> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously); 

    internal Task<DurableTaskResponse> ResponseTask => _tcs.Task;

    internal void SetResult(DurableTaskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Debug.Assert(response.IsCompleted, "DurableTask completed with an invalid, non-terminal response.");
        _tcs.SetResult(response);
    }
}
