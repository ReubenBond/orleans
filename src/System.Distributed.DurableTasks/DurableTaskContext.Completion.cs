using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace System.Distributed.DurableTasks;

public abstract partial class DurableTaskContext : IValueTaskSource<DurableTaskResponse>, IValueTaskSource
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _tcs = new()
    {
        RunContinuationsAsynchronously = true
    };

    internal ValueTask AsUntypedValueTask() => new(this, _tcs.Version);
    internal ValueTask<DurableTaskResponse> AsValueTask() => new(this, _tcs.Version);
    internal DurableTaskResultAwaitable<TResult> GetResultAsync<TResult>() => new(this);

    internal void SetResult(DurableTaskResponse response)
    {
        Debug.Assert(response.IsCompleted, "DurableTask completed with an invalid, non-terminal response.");
        _tcs.SetResult(response);
    }

    DurableTaskResponse IValueTaskSource<DurableTaskResponse>.GetResult(short token) => _tcs.GetResult(token);
    void IValueTaskSource.GetResult(short token) => _tcs.GetResult(token).ThrowIfExceptionResponse();
    ValueTaskSourceStatus IValueTaskSource<DurableTaskResponse>.GetStatus(short token) => _tcs.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _tcs.GetStatus(token);
    void IValueTaskSource<DurableTaskResponse>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
}
