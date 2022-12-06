using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Diagnostics;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
{
    public ValueTask<ScheduledTask> ScheduleAsync() => ScheduleAsyncCore(ScheduledTaskId.None, options: null);
    public ValueTask<ScheduledTask> ScheduleAsync(SchedulingOptions options) => ScheduleAsyncCore(ScheduledTaskId.None, options);
    public ValueTask<ScheduledTask> ScheduleAsync(ScheduledTaskId taskId) => ScheduleAsyncCore(taskId, options: null);
    public ValueTask<ScheduledTask> ScheduleAsync(ScheduledTaskId taskId, SchedulingOptions options) => ScheduleAsyncCore(taskId, options);

    protected abstract ValueTask<ScheduledTask> ScheduleAsyncCore(ScheduledTaskId taskId, SchedulingOptions? options);

    // Schedules the durable task with default options and awaits the scheduled task.
    // Equivalent to `await (await durableTask.ScheduleAsync())`
    public DurableTaskAwaiter GetAwaiter() => new (this);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask
{
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync() => ScheduleAsyncTypedCore(ScheduledTaskId.None, options: null);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(SchedulingOptions options) => ScheduleAsyncTypedCore(ScheduledTaskId.None, options);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(ScheduledTaskId taskId) => ScheduleAsyncTypedCore(taskId, options: null);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(ScheduledTaskId taskId, SchedulingOptions options) => ScheduleAsyncTypedCore(taskId, options);

    protected abstract ValueTask<ScheduledTask<TResult>> ScheduleAsyncTypedCore(ScheduledTaskId taskId, SchedulingOptions? options);

    // Schedules the durable task with default options and awaits the scheduled task.
    // Equivalent to `await (await durableTask.ScheduleAsync())`
    public new DurableTaskAwaiter<TResult> GetAwaiter() => new(this);
}

public static class DurableTaskExtensions
{
    public static async ValueTask<TResult> InvokeAsync<TResult>(this DurableTask<TResult> taskDefinition, ScheduledTaskId taskId)
    {
        return await await taskDefinition.ScheduleAsync(taskId).ConfigureAwait(false);
    }

    public static async ValueTask InvokeAsync(this DurableTask taskDefinition, ScheduledTaskId taskId)
    {
        await await taskDefinition.ScheduleAsync(taskId).ConfigureAwait(false);
    }

    // Return a "DurableTaskStepAwaitable<TResult>" which sets the appropriate context around the invocation.
    public static async ValueTask<TResult> AsStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
    {
        await Task.Delay(1).ConfigureAwait(false);
        // Check the current durable task context
        // If it does not exist, throw:
        //   * Steps can only exist within a durable execution context

        // Check to see if this step has been completed already.
        // If the step has been completed during the current RunId (the invocation, which should be incremented each time the task is started), throw:
        //   * This might be a loop or a duplicate step id. Give an informative error.
        // If the step was completed during a previous RunId, return the result from the previous invocation.

        // -- up until this point, this method should execute synchronously --
        // If the step has not completed, create a new nested durable execution context and invoke the task.
        // When the task completes, replace its entry with the completed result and persist the current state.

        // Return the result to the caller.
        return default;
    }
}

public class DurableTaskContext
{
    private static readonly AsyncLocal<DurableTaskStateNode?> _current = new();
    internal void Enter(string id)
    {
        _current.Value = _current.Value switch
        {
            { } parent => parent.GetOrCreateChildNode(id),
            _ => new DurableTaskStateNode { Id = id },
        };
    }
    
    internal void Clear()
    {
        _current.Value = null;
    }
}

public interface IScheduledTaskManager
{
    ValueTask<ScheduledTask> ScheduleAsync<TGrain>(TGrain grain, Func<TGrain, DurableTask> task, DateTimeOffset dueTime);
}

internal class DurableTaskStateNode
{
    private object? _result;

    public DurableTaskStateNode? Parent { get; init; }
    public required string Id { get; init; }
    public Dictionary<string, DurableTaskStateNode>? Children { get; private set; }

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

    internal DurableTaskStateNode GetOrCreateChildNode(string childId)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out _);
        if (childNode is null)
        {
            childNode = new DurableTaskStateNode { Id = childId, Parent = this };
        }

        return childNode;
    }

    internal DurableTaskStateNode CreateChildNode(string childId)
    {
        Children ??= new();
        ref var childNode = ref CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out var exists);
        if (exists)
        {
            throw new InvalidOperationException("Child node already exists");
        }

        return childNode = new DurableTaskStateNode { Id = childId, Parent = this };
    }

    internal void ClearChildren() => Children = null;
}

public enum DurableTaskStatus
{
    NotStarted,
    InProgress,
    Success,
    Faulted
}
