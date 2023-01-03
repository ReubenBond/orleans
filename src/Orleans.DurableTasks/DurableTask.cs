using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using System.Threading.Tasks.Sources;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
{
    public ValueTask<ScheduledTask> ScheduleAsync() => ScheduleAsyncCore(TaskId.None, options: null);
    public ValueTask<ScheduledTask> ScheduleAsync(SchedulingOptions options) => ScheduleAsyncCore(TaskId.None, options);
    public ValueTask<ScheduledTask> ScheduleAsync(TaskId taskId) => ScheduleAsyncCore(taskId, options: null);
    public ValueTask<ScheduledTask> ScheduleAsync(TaskId taskId, SchedulingOptions options) => ScheduleAsyncCore(taskId, options);
    public ValueTask<ScheduledTask> ScheduleAsync(TaskId taskId, DateTimeOffset dueTime) => ScheduleAsyncCore(taskId, new SchedulingOptions { DueTime = dueTime });

    protected abstract ValueTask<ScheduledTask> ScheduleAsyncCore(TaskId taskId, SchedulingOptions? options);

    // Schedules the durable task with default options and awaits the scheduled task.
    // Equivalent to `await (await durableTask.ScheduleAsync())`
    public DurableTaskAwaiter GetAwaiter() => new (this);

    public static DurableTask<T> FromResult<T>(T value) => new CompletedDurableTask<T>(value);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask
{
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync() => ScheduleAsyncTypedCore(TaskId.None, options: null);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(SchedulingOptions options) => ScheduleAsyncTypedCore(TaskId.None, options);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(TaskId taskId) => ScheduleAsyncTypedCore(taskId, options: null);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(TaskId taskId, SchedulingOptions options) => ScheduleAsyncTypedCore(taskId, options);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(TaskId taskId, DateTimeOffset dueTime) => ScheduleAsyncTypedCore(taskId, new SchedulingOptions { DueTime = dueTime });

    protected abstract ValueTask<ScheduledTask<TResult>> ScheduleAsyncTypedCore(TaskId taskId, SchedulingOptions? options);

    // Schedules the durable task with default options and awaits the scheduled task.
    // Equivalent to `await (await durableTask.ScheduleAsync())`
    public new DurableTaskAwaiter<TResult> GetAwaiter() => new(this);
}

internal interface ICompletedDurableTask
{
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult> : DurableTask<TResult>, ICompletedDurableTask
{
    public CompletedDurableTask(TResult value) => Result = value;

    public TResult Result { get; }

    protected override ValueTask<ScheduledTask> ScheduleAsyncCore(TaskId taskId, SchedulingOptions? options) => new(new ScheduledTask<TResult>(taskId, options, this));
    protected override ValueTask<ScheduledTask<TResult>> ScheduleAsyncTypedCore(TaskId taskId, SchedulingOptions? options)
    {
        // If inside a durable execution context, use the runtime to schedule a 
        return new(new ScheduledTask<TResult>(taskId, options, this));
    }

    public ValueTask<TResult> AsValueTask() => new ValueTask<TResult>(Result);
}

public static class DurableTaskExtensions
{
    public static async ValueTask<TResult> InvokeAsync<TResult>(this DurableTask<TResult> taskDefinition, TaskId taskId)
    {
        return await await taskDefinition.ScheduleAsync(taskId).ConfigureAwait(false);
    }

    public static async ValueTask InvokeAsync(this DurableTask taskDefinition, TaskId taskId)
    {
        await await taskDefinition.ScheduleAsync(taskId).ConfigureAwait(false);
    }

    // Return a "DurableTaskStepAwaitable<TResult>" which sets the appropriate context around the invocation.
    public static async ValueTask<TResult> AsWorkflowStep<TResult>(this DurableTask<TResult> taskDefinition, string stepId)
    {
        var currentContext = DurableTaskContext.GetCurrentContextOrThrow();

        // See if a child node exists for this context already.
        // If so, there are two cases:
        // - the task has completed, in which case return the result.
        // - the task is incomplete, in which case we will need to execute it.
        // If not, create a new child node and invoke the task.
        var childContext = currentContext.GetOrCreateChildNode(stepId, out var exists);
        if (exists)
        {
            if (childContext.IsCompleted)
            {
            }
        }

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
        return default!;
    }

    // See above
    public static ValueTask AsWorfklowStep(this DurableTask taskDefinition, string stepId)
    {
        return default;
    }
}

public interface IDurableTaskState<T>
{
    public T Value { get; set; }
    public ValueTask WriteStateAsync();
}

public interface IScheduledTaskManager
{
    ValueTask<ScheduledTask> ScheduleAsync<TGrain>(TGrain grain, Func<TGrain, DurableTask> task, DateTimeOffset dueTime);
}
