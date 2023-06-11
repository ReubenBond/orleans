using System.Diagnostics.CodeAnalysis;
using LiteDB;
using Orleans.DurableTasks;
using Orleans.Serialization;

public sealed class LiteDbJobStorage : IJobStorage
{
    private readonly Serializer<JobTaskState> _serializer;
    private readonly DeepCopier<JobTaskState> _copier;

    private LiteDatabase _db;
    private Dictionary<TaskId, JobTaskState> _workingCopy = new();
    private HashSet<TaskId> _removed = new();

    public LiteDbJobStorage(Serializer<JobTaskState> serializer, DeepCopier<JobTaskState> copier)
    {
        _serializer = serializer;
        _copier = copier;
        _db = new LiteDatabase(@"jobs.db");
    }

    public IEnumerable<(TaskId Id, JobTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, pair.Value));

    public void AddOrUpdateTask(TaskId taskId, JobTaskState state) => _workingCopy[taskId] = _copier.Copy(state);
    public bool RemoveTask(TaskId taskId)
    {
        if (_workingCopy.Remove(taskId))
        {
            return _removed.Add(taskId);
        }
        return false;
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state)
    {
        if (_workingCopy.TryGetValue(taskId, out var internalState))
        {
            state = _copier.Copy(internalState);
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync()
    {
        var collection = _db.GetCollection<JobEntity>("jobs");

        _workingCopy = new Dictionary<TaskId, JobTaskState>();
        foreach (var entry in collection.FindAll())
        {
            var taskId = TaskId.Parse(entry.Id!);

            _workingCopy.Add(taskId, _serializer.Deserialize(entry.Payload));
        }
        
        return default;
    }

    public ValueTask WriteAsync()
    {
        var collection = _db.GetCollection<JobEntity>("jobs");
        foreach (var (id, task) in _workingCopy)
        {
            var entry = new JobEntity
            {
                Id = id.ToString(),
                Payload = _serializer.SerializeToArray(task),
            };

            collection.Upsert(entry);
        }

        foreach (var id in _removed)
        {
            collection.Delete(id.ToString());
        }

        _removed.Clear();
        return default;
    }

    private class JobEntity
    {
        [BsonField("_id")]
        public string? Id { get; set; }

        [BsonField("data")]
        public byte[]? Payload { get; set; }
    }
}
