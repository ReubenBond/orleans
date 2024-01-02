using System.Runtime.CompilerServices;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

/// <summary>
/// Provides an awaiter for <see cref="DurableTask"/> instances.
/// </summary>
public readonly struct DurableTaskAwaiter : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<Response> _awaiter;

    internal DurableTaskAwaiter(ValueTask<Response> invokedTask)
    {
        _awaiter = invokedTask.GetAwaiter();
    }

    public void GetResult() => _awaiter.GetResult().ThrowIfExceptionResponse();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

/// <summary>
/// Provides an awaiter for <see cref="DurableTask{TResult}"/> instances.
/// </summary>
public readonly struct DurableTaskAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<Response> _awaiter;

    internal DurableTaskAwaiter(ValueTask<Response> invokedTask)
    {
        _awaiter = invokedTask.GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}