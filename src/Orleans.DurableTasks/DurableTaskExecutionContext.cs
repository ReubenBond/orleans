using System.Runtime.InteropServices;
using Orleans.Serialization;

namespace Orleans.DurableTasks;

public sealed partial class DurableTaskExecutionContext
{
    private static readonly AsyncLocal<DurableTaskExecutionContext?> Current = new();

    public static DurableTaskExecutionContext? CurrentContext => Current.Value;
    public static DurableTaskExecutionContext GetCurrentContextOrThrow() => Current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskExecutionContext)} is required but not present.");
    internal static void Reset() => Current.Value = null;
    internal static void SetCurrentContext(DurableTaskExecutionContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableTaskExecutionContext? context, out DurableTaskExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    private readonly Serializer _serializer;

    public bool IsCancellationRequested => AsValueTask().IsCanceled;
    public bool IsCompleted => AsValueTask().IsCompleted;

    // Get state associated with a workflow
    public IDurableTaskState<T> GetState<T>(string key) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, T defaultValue) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, Func<T> createDefaultValue) => default!;

    public DurableTaskExecutionContext? Parent { get; init; }
    public required TaskId Id { get; init; }

    // Child nodes. Each child is a workflow step (subworkflow)
    public Dictionary<string, DurableTaskExecutionContext>? Children { get; private set; }

    internal DurableTask Task { get; }

    internal DurableTaskExecutionContext(Serializer serializer, DurableTask task)
    {
        _serializer = serializer;
        Task = task;
    }

    internal DurableTaskExecutionContext GetOrCreateChildNode(string childId, DurableTask task, out bool exists)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out exists);
        childNode ??= new DurableTaskExecutionContext(_serializer, task) { Id = Id.CreateChild(childId), Parent = this };
        return childNode;
    }

    internal DurableTaskExecutionContext CreateChildNode(string childId, DurableTask task)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out var exists);
        if (exists)
        {
            throw new InvalidOperationException("Child node already exists");
        }

        return childNode = new DurableTaskExecutionContext(_serializer, task) { Id = Id.CreateChild(childId), Parent = this };
    }

    internal void ClearChildren() => Children = null;
}
