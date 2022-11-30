using System.Runtime.CompilerServices;
using Orleans.Vesuvius.Remoting;
using Orleans.Runtime;

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

    protected override async ValueTask<ScheduledTask> ScheduleAsyncCore(ScheduledTaskId taskId, SchedulingOptions? options)
    {
        return await ScheduleAsyncTypedCore(taskId, options).ConfigureAwait(false);
    }

    protected abstract ValueTask<ScheduledTask<TResult>> ScheduleAsyncTypedCore(ScheduledTaskId taskId, SchedulingOptions? options);

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
}
