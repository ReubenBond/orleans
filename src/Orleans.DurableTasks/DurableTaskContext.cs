using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Orleans.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

public class DurableTaskContext
{
    private static readonly AsyncLocal<DurableTaskContext?> _current = new();

    public static DurableTaskContext? CurrentContext => _current.Value;
    public static DurableTaskContext GetCurrentContextOrThrow()
    {
        if (_current.Value is not { } value)
        {
            ThrowMissingContext();
            return null!;
        }

        return value;
    }

    private readonly Serializer _serializer;

    public bool IsCancellationRequested => Status is DurableTaskStatus.Canceled;
    public bool IsCompleted => Status is DurableTaskStatus.Success or DurableTaskStatus.Faulted or DurableTaskStatus.Canceled;

    // Get state associated with a workflow
    public IDurableTaskState<T> GetState<T>(string key) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, T defaultValue) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, Func<T> createDefaultValue) => default!;

    internal void Enter(Serializer serializer, string id)
    {
        _current.Value = _current.Value switch
        {
            { } parent => parent.GetOrCreateChildNode(id, out _),
            _ => new DurableTaskContext(serializer) { Id = id },
        };
    }
    
    internal void Clear()
    {
        _current.Value = null;
    }

    private object? _result;

    public DurableTaskContext? Parent { get; init; }
    public required string Id { get; init; }
    public Dictionary<string, DurableTaskContext>? Children { get; private set; }

    internal DurableTaskContext(Serializer serializer)
    {
        _serializer = serializer;
    }

    public DurableTaskStatus Status { get; private set; }
    public ExceptionDispatchInfo? Exception => _result switch { DurableTaskStatus.Faulted => (ExceptionDispatchInfo)_result!, _ => null };
    public object? Result => _result switch { DurableTaskStatus.Success => _result, _ => null };

    internal void SetResult(object? result)
    {
        Debug.Assert(Status is DurableTaskStatus.NotStarted or DurableTaskStatus.InProgress);
        ClearChildren();
        Status = DurableTaskStatus.Success;
        _result = result;
    }

    internal void SetException(Exception exception)
    {
        Debug.Assert(Status is DurableTaskStatus.NotStarted or DurableTaskStatus.InProgress);
        ClearChildren();
        Status = DurableTaskStatus.Faulted;
        _result = ExceptionDispatchInfo.Capture(exception);
    }

    internal DurableTaskContext GetOrCreateChildNode(string childId, out bool exists)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out exists);
        childNode ??= new DurableTaskContext(_serializer) { Id = childId, Parent = this };
        return childNode;
    }

    internal DurableTaskContext CreateChildNode(string childId)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out var exists);
        if (exists)
        {
            throw new InvalidOperationException("Child node already exists");
        }

        return childNode = new DurableTaskContext(_serializer) { Id = childId, Parent = this };
    }

    internal void ClearChildren() => Children = null;

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissingContext() => throw new InvalidOperationException($"An ambient {nameof(DurableTaskContext)} is required but not present.");
}
