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
        switch (durableTask)
        {
            case DurableTaskMethodInvocation methodInvocation:
                _awaiter = methodInvocation.AsUntypedValueTask().GetAwaiter();
                break;
            case IDurableTaskMethodInvocation methodInvocation:
                // This handles the cases where a DurableTask<T> method is cast to an untyped DurableTask.
                _awaiter = methodInvocation.AsUntypedValueTask().GetAwaiter();
                break;
            case ICompletedDurableTask:
                // This handles the cases where a CompletedDurableTask<T> method is cast to an untyped DurableTask.
                _awaiter = default(ValueTask).GetAwaiter();
                break;
            default:
                _awaiter = ScheduleAndAwaitAsync(durableTask).GetAwaiter();
                break;
        }
    }

    private static async ValueTask ScheduleAndAwaitAsync(DurableTask durableTask)
    {
        var durableTaskInvocation = await durableTask.ScheduleAsync();
        await durableTaskInvocation;
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
        switch (durableTask)
        {
            case DurableTaskMethodInvocation<TResult> methodInvocation:
                _awaiter = methodInvocation.AsValueTask().GetAwaiter();
                break;
            case CompletedDurableTask<TResult> completedTask:
                _awaiter = new ValueTask<TResult>(completedTask.Result).GetAwaiter();
                break;
            default:
                _awaiter = ScheduleAndAwaitAsync(durableTask).GetAwaiter();
                break;
        }
    }

    private static async ValueTask<TResult> ScheduleAndAwaitAsync(DurableTask<TResult> durableTask)
    {
        var durableTaskInvocation = await durableTask.ScheduleAsync();
        return await durableTaskInvocation;
    }

    public TResult GetResult() => _awaiter.GetResult();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}