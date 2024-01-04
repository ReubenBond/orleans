using Orleans.Journaling;

namespace Orleans.DurableTasks.Playground;

[Alias("IDurableTaskCompletionSourceGrain`1")]
public interface IDurableTaskCompletionSourceGrain<T> : IGrain
{
    [Alias("TrySetResult")]
    ValueTask<bool> TrySetResult(T value);
    [Alias("TrySetException")]
    ValueTask<bool> TrySetException(Exception exception);
    [Alias("TrySetCanceled")]
    ValueTask<bool> TrySetCanceled();
    [Alias("GetResult")]
    DurableTask<T> GetResult();
    [Alias("GetState")]
    ValueTask<DurableTaskCompletionSourceState<T>> GetState();
}
