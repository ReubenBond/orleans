using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Orleans.Serialization;

namespace Orleans.DurableTasks;

public abstract class DurableTaskExecutionContext
{
    private static readonly AsyncLocal<DurableTaskExecutionContext?> _current = new();

    public static DurableTaskExecutionContext? CurrentContext => _current.Value;
    public static DurableTaskExecutionContext GetCurrentContextOrThrow() => _current.Value ?? throw new InvalidOperationException($"An ambient {nameof(DurableTaskExecutionContext)} is required but not present.");

    private readonly Serializer _serializer;

    public bool IsCancellationRequested => Status is DurableTaskStatus.Canceled;
    public bool IsCompleted => Status is DurableTaskStatus.Success or DurableTaskStatus.Faulted or DurableTaskStatus.Canceled;

    // Get state associated with a workflow
    public IDurableTaskState<T> GetState<T>(string key) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, T defaultValue) => default!;
    public ValueTask<IDurableTaskState<T>> GetOrAddStateAsync<T>(string key, Func<T> createDefaultValue) => default!;

    /*
    internal void Enter(Serializer serializer, string id)
    {
        _current.Value = _current.Value switch
        {
            { } parent => parent.GetOrCreateChildNode(id, out _),
            _ => new DurableTaskContext(serializer) { Id = id },
        };
    }
    */
    
    internal void Clear()
    {
        _current.Value = null;
    }

    // Pointer to parent, if present
    // Why? 
    public DurableTaskExecutionContext? Parent { get; init; }
    public required string Id { get; init; }

    // Child nodes. Each child is a workflow step (subworkflow)
    public Dictionary<string, DurableTaskExecutionContext>? Children { get; private set; }

    // Result of this task.
    internal DurableTask Task { get; private set; }

    internal DurableTaskExecutionContext(Serializer serializer, DurableTask task)
    {
        _serializer = serializer;
        Task = task;
    }

    public DurableTaskStatus Status { get; private set; }
    public ExceptionDispatchInfo? Exception => _result switch { DurableTaskStatus.Faulted => (ExceptionDispatchInfo)_result!, _ => null };
    public object? Result => _result switch { DurableTaskStatus.Success => _result, _ => null };
    internal async ValueTask<TResult> GetCompletionTask<TResult>()
    {
        // TODO: We could make this more efficient with a special awaiter to convert from object to TResult.
        return (TResult)await _tcs.Task;
    }

    internal abstract void SetResult(object? result);
    internal abstract void SetException(Exception exception);
    internal abstract void SetCanceled(CancellationToken cancellationToken);

    internal DurableTaskExecutionContext GetOrCreateChildNode(string childId, DurableTask task, out bool exists)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out exists);
        childNode ??= new DurableTaskExecutionContext(_serializer, task) { Id = childId, Parent = this };
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

        return childNode = new VoidDurableTaskExecutionContext(_serializer, task) { Id = childId, Parent = this };
    }

    internal void ClearChildren() => Children = null;
}

public sealed class VoidDurableTaskExecutionContext : DurableTaskExecutionContext
{
    private readonly TaskCompletionSource _tcs = new (TaskCreationOptions.RunContinuationsAsynchronously);

    public VoidDurableTaskExecutionContext(Serializer serializer, DurableTask task) : base(serializer, task)
    {
    }

    internal override void SetCanceled(CancellationToken cancellationToken) => _tcs.SetCanceled(cancellationToken);
    internal override void SetException(Exception exception) => _tcs.SetException(exception);
    internal override void SetResult(object? result)
    {
        Debug.Assert(result is null);
        _tcs.SetResult();
    }
}

public sealed class DurableTaskExecutionContext<TResult> : DurableTaskExecutionContext
{
    private readonly TaskCompletionSource<TResult> _tcs = new (TaskCreationOptions.RunContinuationsAsynchronously);

    public DurableTaskExecutionContext(Serializer serializer, DurableTask task) : base(serializer, task)
    {
    }

    internal override void SetCanceled(CancellationToken cancellationToken) => _tcs.SetCanceled(cancellationToken);
    internal override void SetException(Exception exception) => _tcs.SetException(exception);
    internal override void SetResult(object? result)
    {
        Debug.Assert(result is null);
        _tcs.SetResult((TResult)result!);
    }
}

