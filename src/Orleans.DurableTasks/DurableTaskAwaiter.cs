using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>
/// Provides an awaiter for <see cref="DurableTask"/> instances.
/// </summary>
public readonly struct DurableTaskAwaiter : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _awaiter;

    internal DurableTaskAwaiter(DurableTask durableTask)
    {
        _awaiter = durableTask.InvokeAsync(null!).GetAwaiter();
    }

    public void GetResult() => _awaiter.GetResult();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

/// <summary>
/// Provides an awaiter for <see cref="DurableTask{TResult}"/> instances.
/// </summary>
public readonly struct DurableTaskAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<TResult> _awaiter;

    internal DurableTaskAwaiter(DurableTask<TResult> durableTask)
    {
        _awaiter = durableTask.InvokeAsync(null!).GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}