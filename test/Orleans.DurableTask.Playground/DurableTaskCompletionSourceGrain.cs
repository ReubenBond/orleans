using Orleans.Journaling;

namespace Orleans.DurableTasks.Playground;

public class DurableTaskCompletionSourceGrain<T> : DurableGrain, IDurableTaskCompletionSourceGrain<T>
{
    private readonly DurableTaskCompletionSource<T> _state;

    public DurableTaskCompletionSourceGrain()
    {
        _state = GetOrCreateTaskCompletionSource<T>("state");
    }

    public async ValueTask<bool> TrySetResult(T value)
    {
        if (_state.TrySetResult(value))
        {
            await WriteStateAsync();
            return true;
        }

        return false;
    }

    public async ValueTask<bool> TrySetException(Exception exception)
    {
        if (_state.TrySetException(exception))
        {
            await WriteStateAsync();
            return true;
        }

        return false;
    }

    public async ValueTask<bool> TrySetCanceled()
    {
        if (_state.TrySetCanceled())
        {
            await WriteStateAsync();
            return true;
        }

        return false;
    }

    public async DurableTask<DurableTaskCompletionSourceState<T>> GetCompletionState()
    {
        // Wait for the result to complete, without throwing.
        var nonGenericTask = (Task)_state.Task;
        await nonGenericTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return _state.State;
    }

    public async DurableTask<T> GetResult() => await _state.Task;
    public ValueTask<DurableTaskCompletionSourceState<T>> GetState() => new(_state.State);
}
