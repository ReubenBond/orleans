using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Orleans.DurableTasks.Remoting;

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

    public bool IsCancellationRequested => AsValueTask().IsCanceled;
    public bool IsCompleted => AsValueTask().IsCompleted;

    // Get state associated with a workflow
    public IDurableTaskState<T> GetState<T>(string key) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, T defaultValue) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, Func<T> createDefaultValue) => default!;

    public DurableTaskExecutionContext? Parent { get; init; }
    public required TaskId Id { get; init; }
    internal DurableTaskState State { get; }

    // Child nodes. Each child is a workflow step (subworkflow)
    public Dictionary<TaskId, DurableTaskExecutionContext>? Children { get; private set; }

    [SetsRequiredMembers]
    internal DurableTaskExecutionContext(TaskId taskId, DurableTaskState state)
    {
        Id = taskId;
        State = state;
    }

    internal DurableTaskExecutionContext GetOrCreateChildNode(string childId, DurableTaskState state, out bool exists) => GetOrCreateChildNode(Id.CreateChild(childId), state, out exists);
    internal DurableTaskExecutionContext GetOrCreateChildNode(TaskId childId, DurableTaskState state, out bool exists)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out exists);
        childNode ??= new DurableTaskExecutionContext(childId, state) { Parent = this };
        return childNode;
    }

    internal DurableTaskExecutionContext CreateChildNode(string childId, DurableTaskState state) => CreateChildNode(Id.CreateChild(childId), state);
    internal DurableTaskExecutionContext CreateChildNode(TaskId childId, DurableTaskState state)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out var exists);
        if (exists)
        {
            throw new InvalidOperationException("Child node already exists");
        }

        return childNode = new DurableTaskExecutionContext(childId, state) { Parent = this };
    }

    internal void ClearChildren() => Children = null;
}
