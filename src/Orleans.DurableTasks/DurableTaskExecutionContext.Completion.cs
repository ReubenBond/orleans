using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Orleans.DurableTasks.Remoting;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

public sealed partial class DurableTaskExecutionContext : IValueTaskSource<Response>, IValueTaskSource
{
    private ManualResetValueTaskSourceCore<Response> _tcs = new()
    {
        RunContinuationsAsynchronously = true
    };

    internal ValueTask AsUntypedValueTask() => new(this, _tcs.Version);
    internal ValueTask<Response> AsValueTask() => new(this, _tcs.Version);
    internal DurableTaskResultAwaitable<TResult> GetResultAsync<TResult>() => new(this);

    internal void SetResponse(Response response)
    {
        Debug.Assert(response is not PendingResponse);
        _tcs.SetResult(response);
    }

    Response IValueTaskSource<Response>.GetResult(short token) => _tcs.GetResult(token);
    void IValueTaskSource.GetResult(short token) => _tcs.GetResult(token).ThrowIfExceptionResponse();
    ValueTaskSourceStatus IValueTaskSource<Response>.GetStatus(short token) => _tcs.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _tcs.GetStatus(token);
    void IValueTaskSource<Response>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
}

public readonly struct DurableTaskResultAwaitable<TResult>
{
    private readonly DurableTaskExecutionContext _executionContext;
    public DurableTaskResultAwaitable(DurableTaskExecutionContext executionContext) => _executionContext = executionContext;
    public DurableTaskResultAwaiter<TResult> GetAwaiter() => new(_executionContext.AsValueTask());
}

public readonly struct DurableTaskResultAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<Response> _awaiter;

    internal DurableTaskResultAwaiter(ValueTask<Response> responseTask)
    {
        _awaiter = responseTask.GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}
