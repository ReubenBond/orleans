using System.Threading.Tasks.Sources;

namespace Orleans.DurableTasks;

public sealed partial class DurableTaskExecutionContext : IValueTaskSource<object?>, IValueTaskSource
{
    // TODO: should we try harder to use a strongly-typed tcs here?
    // Questions around how serialization shoudl work
    private ManualResetValueTaskSourceCore<object?> _tcs = new()
    {
        RunContinuationsAsynchronously = true
    };

    private CancellationTokenSource _cancellationTokenSource = new();

    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    internal ValueTask AsUntypedValueTask() => new(this, _tcs.Version);
    internal ValueTask<object?> AsValueTask() => new(this, _tcs.Version);
    internal ValueTask<TResult> AsValueTask<TResult>() => new ConvertingValueTaskSource<TResult>(this).AsValueTask();
    internal void SetCanceled()
    {
        _cancellationTokenSource.Cancel();
        _tcs.SetException(new OperationCanceledException(_cancellationTokenSource.Token));
    }

    internal void SetException(Exception exception) => _tcs.SetException(exception);
    internal void SetResult(object? result) => _tcs.SetResult(result);

    object? IValueTaskSource<object?>.GetResult(short token) => _tcs.GetResult(token);
    void IValueTaskSource.GetResult(short token) => _tcs.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<object?>.GetStatus(short token) => _tcs.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _tcs.GetStatus(token);
    void IValueTaskSource<object?>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _tcs.OnCompleted(continuation, state, token, flags);

    private sealed class ConvertingValueTaskSource<TResult> : IValueTaskSource<TResult>
    {
        private readonly DurableTaskExecutionContext _context;
        public ConvertingValueTaskSource(DurableTaskExecutionContext context)
        {
            _context = context;
        }

        public ValueTask<TResult> AsValueTask() => new (this, _context._tcs.Version);
        public TResult GetResult(short token) => (TResult)_context._tcs.GetResult(token)!;
        public ValueTaskSourceStatus GetStatus(short token) => _context._tcs.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _context._tcs.OnCompleted(continuation, state, token, flags);
    }
}
