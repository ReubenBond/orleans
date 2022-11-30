using System.Runtime.CompilerServices;
using Orleans.Vesuvius.Remoting;
using Orleans.Runtime;
using System.Runtime.InteropServices;

namespace Orleans.Vesuvius;

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

    public static async ValueTask<TResult> AsStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
    {
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
    }
}

public class DurableTaskState
{
}

internal class DurableTaskStateNode
{
    public DurableTaskStateNode? Parent { get; init; }
    public required string Id { get; init; }
    public Dictionary<string, DurableTaskStateNode>? Children { get; private set; }
    public DurableTaskStateNode CreateChildNode(string childId)
    {
        Children ??= new();
        ref var childNode = CollectionsMarshal.GetValueRefOrAddDefault(Children, childId, out var exists);
        if (exists)
        {
            throw new InvalidOperationException("Child node already exists");
        }

        return childNode = new DurableTaskStateNode { Id = childId, Parent = this };
    }

    public DurableTaskStatus Status { get; private set; }
    public object? Result { get; set; }
    public void ClearChildren() => Children = null;
}

public enum DurableTaskStatus
{
    NotStarted,
    InProgress,
    Success,
    Faulted
}
