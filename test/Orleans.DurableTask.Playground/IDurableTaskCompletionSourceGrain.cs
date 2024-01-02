using Orleans.Journaling;

namespace Orleans.DurableTasks.Playground;

public interface IDurableTaskCompletionSourceGrain<T> : IGrain
{
    ValueTask<bool> TrySetResult(T value);
    ValueTask<bool> TrySetException(Exception exception);
    ValueTask<bool> TrySetCanceled();
    DurableTask<T> GetResult();
    ValueTask<DurableTaskCompletionSourceState<T>> GetState();
}
