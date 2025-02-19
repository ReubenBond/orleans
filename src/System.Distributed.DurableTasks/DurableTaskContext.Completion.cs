using System.Diagnostics;
using System.Threading.Tasks.Sources;
using Orleans.Serialization.Invocation;

namespace System.Distributed.DurableTasks;

public abstract partial class DurableTaskContext : IValueTaskSource<Response>, IValueTaskSource
{
    private ManualResetValueTaskSourceCore<Response> _tcs = new()
    {
        RunContinuationsAsynchronously = true
    };

    internal ValueTask AsUntypedValueTask() => new(this, _tcs.Version);
    internal ValueTask<Response> AsValueTask() => new(this, _tcs.Version);
    internal DurableTaskResultAwaitable<TResult> GetResultAsync<TResult>() => new(this);

    internal void SetResult(Response response)
    {
        Debug.Assert(response.IsFinal, "DurableTask completed with an invalid, non-terminal response.");
        _tcs.SetResult(response);
    }

    Response IValueTaskSource<Response>.GetResult(short token) => _tcs.GetResult(token);
    void IValueTaskSource.GetResult(short token) => _tcs.GetResult(token).ThrowIfExceptionResponse();
    ValueTaskSourceStatus IValueTaskSource<Response>.GetStatus(short token) => _tcs.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _tcs.GetStatus(token);
    void IValueTaskSource<Response>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
}
