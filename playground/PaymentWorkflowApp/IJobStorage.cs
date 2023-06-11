using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;

public interface IJobStorage
{
    IEnumerable<(TaskId Id, JobTaskState State)> Tasks { get; }
    void AddOrUpdateTask(TaskId taskId, JobTaskState state);
    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    
    ValueTask WriteAsync();
    ValueTask ReadAsync();
}
