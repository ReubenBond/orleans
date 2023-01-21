using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Remoting;

// TODO: In designing this interface, perhaps we should model mutations in a finer-grained manner to facilitate efficient log-based storage approach.
// Eg: Separate AddRequest, Add/RemoveClient, SetResponse methods.
internal interface IDurableTaskGrainStorage
{
    IEnumerable<(TaskId Id, DurableTaskState State)> Tasks { get; }
    void AddOrUpdateTask(TaskId taskId, DurableTaskState state);
    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out DurableTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    
    ValueTask WriteAsync();
    ValueTask ReadAsync();
}

internal class VolatileDurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private Dictionary<TaskId, DurableTaskState> _workingCopy = new();
    private Dictionary<TaskId, DurableTaskState> _persistedCopy = new();
    private readonly DeepCopier<Dictionary<TaskId, DurableTaskState>> _storageCopier;
    private readonly DeepCopier<DurableTaskState> _stateCopier;

    public IEnumerable<(TaskId Id, DurableTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, pair.Value));

    public VolatileDurableTaskGrainStorage(DeepCopier<Dictionary<TaskId, DurableTaskState>> storageCopier, DeepCopier<DurableTaskState> stateCopier)
    {
        _storageCopier = storageCopier;
        _stateCopier = stateCopier;
    }

    public void AddOrUpdateTask(TaskId taskId, DurableTaskState state) => _workingCopy[taskId] = _stateCopier.Copy(state);
    public bool RemoveTask(TaskId taskId) => _workingCopy.Remove(taskId);
    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out DurableTaskState? state)
    {
        if (_workingCopy.TryGetValue(taskId, out var internalState))
        {
            state = _stateCopier.Copy(internalState);
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync()
    {
        _workingCopy = _storageCopier.Copy(_persistedCopy);
        return default;
    }

    public ValueTask WriteAsync()
    {
        _persistedCopy = _storageCopier.Copy(_workingCopy);
        return default;
    }
}
