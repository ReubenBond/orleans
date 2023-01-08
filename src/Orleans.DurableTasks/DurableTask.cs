using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using System.Threading.Tasks.Sources;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(VoidDurableTaskRequest))]
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

public interface IDurableTaskState<T>
{
    public T Value { get; set; }
    public ValueTask WriteStateAsync();
}

public interface IScheduledTaskManager
{
    ValueTask<ScheduledTask> ScheduleAsync<TGrain>(TGrain grain, Func<TGrain, DurableTask> task, DateTimeOffset dueTime);
}
