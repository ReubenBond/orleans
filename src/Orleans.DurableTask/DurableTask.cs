using System.Runtime.CompilerServices;
using Orleans.Vesuvius.Remoting;
using Orleans.Runtime;

namespace Orleans.Vesuvius;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
{
    public ValueTask<ScheduledTask> ScheduleAsync() => ScheduleAsyncCore(new SchedulingOptions());
    public ValueTask<ScheduledTask> ScheduleAsync(SchedulingOptions options) => ScheduleAsyncCore(options);
    public ValueTask<ScheduledTask> ScheduleAsync(ScheduledTaskId id) => ScheduleAsyncCore(new SchedulingOptions());
    public ValueTask<ScheduledTask> ScheduleAsync(ScheduledTaskId id, SchedulingOptions options) => ScheduleAsyncCore(options);

    protected abstract ValueTask<ScheduledTask> ScheduleAsyncCore(SchedulingOptions options);

    // Schedules the durable task with default options and awaits the scheduled task.
    // Equivalent to `await (await durableTask.ScheduleAsync())`
    public DurableTaskAwaiter GetAwaiter() => new (this);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask
{
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync() => ScheduleAsyncTypedCore(new SchedulingOptions());
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(SchedulingOptions options) => ScheduleAsyncTypedCore(options);
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(ScheduledTaskId id) => ScheduleAsyncTypedCore(new SchedulingOptions());
    public new ValueTask<ScheduledTask<TResult>> ScheduleAsync(ScheduledTaskId id, SchedulingOptions options) => ScheduleAsyncTypedCore(options);

    protected override async ValueTask<ScheduledTask> ScheduleAsyncCore(SchedulingOptions options)
    {
        return await ScheduleAsyncTypedCore(options).ConfigureAwait(false);
    }

    protected abstract ValueTask<ScheduledTask<TResult>> ScheduleAsyncTypedCore(SchedulingOptions options);

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
