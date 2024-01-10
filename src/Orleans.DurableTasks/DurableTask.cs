using System.Runtime.CompilerServices;
using Orleans.DurableTasks.Remoting;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
[GenerateSerializer, SerializerTransparent]
[Alias("DurableTask")]
public abstract partial class DurableTask
{
    public static DurableTask<T> FromResult<T>(T value) => new CompletedDurableTask<T>(value);

    public static DurableTask Run(Action func) => new DelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<T> func) => new DelegateDurableTask<T>(func);
    public static DurableTask Run(Func<ValueTask> func) => new AsyncDelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<ValueTask<T>> func) => new AsyncDelegateDurableTask<T>(func);
    public static DurableTask Run(Func<Task> func) => new AsyncTaskDelegateDurableTask(func);
    public static DurableTask<T> Run<T>(Func<Task<T>> func) => new AsyncTaskDelegateDurableTask<T>(func);

    /// <summary>
    /// Invokes the task with the provided context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <returns>The response.</returns>
    protected internal abstract ValueTask<Response> InvokeAsync(DurableTaskContext context);
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
[GenerateSerializer, SerializerTransparent]
[Alias("DurableTask`1")]
public abstract class DurableTask<TResult> : DurableTask
{
}

internal struct ConfiguredDurableTaskCore<TDurableTask>(TDurableTask task) where TDurableTask : DurableTask
{
    internal readonly TDurableTask Task = task;
    internal readonly DurableTaskContext? ParentContext = DurableTaskContext.CurrentContext;
    internal TaskId Id;
    internal SchedulingOptions? SchedulingOptions;

    internal ValueTask<Response> InvokeAsync()
    {
        if (ParentContext is { } parentContext)
        {
            if (Id.IsDefault)
            {
                // Allocate a child identifier for the task.
                Id = parentContext.CreateChildTaskId(null);
            }

            // Evaluates the task: if it is a local method, it will be executed immediately.
            // This will return once the task has completed.
            return parentContext.InvokeAsync(Id, Task, CancellationToken.None);
        }
        else if (Task is ISchedulableTask schedulableTask)
        {
            if (Id.IsDefault)
            {
                // Select a random identifier for the task.
                // The caller will need to query for the task to find its identifier.
                Id = TaskId.Create(Guid.NewGuid().ToString());
            }

            // Schedules the task and await completion.
            return ScheduleAndAwaitAsync(schedulableTask);
        }
        else
        {
            throw GetNonSchedulableTaskException();
        }
    }

    private async readonly ValueTask<Response> ScheduleAndAwaitAsync(ISchedulableTask schedulableTask)
    {
        var context = await schedulableTask.ScheduleAsync(Id, SchedulingOptions);
        return await context.AsValueTask();
    }

    internal void SetTaskIdCore(string name)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
        if (!Id.IsDefault)
        {
            throw new InvalidOperationException("Id already specified");
        }

        if (ParentContext is { } parentContext)
        {
            // Create an identifier relative to the parent context's identifier.
            Id = parentContext.CreateChildTaskId(name);
        }
        else
        {
            Id = TaskId.Create(name);
        }
    }

    private static InvalidOperationException GetNonSchedulableTaskException() => new (
        $"The provided task does not support scheduling and was not executed in the context of an existing {nameof(DurableTask)}. This may be because it is a local method or another non-serializable task type.");
}

public struct ConfiguredDurableTask(DurableTask task)
{
    private ConfiguredDurableTaskCore<DurableTask> _core = new(task);

    public DurableTaskAwaiter GetAwaiter() => new (_core.InvokeAsync());

    internal readonly DurableTask Task => _core.Task;
    internal TaskId Id { set => _core.Id = value; readonly get => _core.Id; }
    internal SchedulingOptions? SchedulingOptions { set => _core.SchedulingOptions = value; readonly get => _core.SchedulingOptions; }
    internal ConfiguredDurableTask WithId(string id)
    {
        _core.SetTaskIdCore(id);
        return this;
    }

    internal ConfiguredDurableTask WithSchedulingOptions(SchedulingOptions? options)
    {
        SchedulingOptions = options;
        return this;
    }

    public async readonly ValueTask<ScheduledTask> ScheduleAsync()
    {
        if (Task is not ISchedulableTask schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(Id, SchedulingOptions);
        return new ScheduledDurableTask(executionContext);
    }

    internal static InvalidOperationException GetNonSchedulableTaskException() => new ("The provided task does not support scheduling. This may be because it is a local method or another non-serializable task type.");
}

public struct ConfiguredDurableTask<TResult>(DurableTask<TResult> task)
{
    private ConfiguredDurableTaskCore<DurableTask<TResult>> _core = new(task);

    internal readonly DurableTask<TResult> Task => _core.Task;
    internal TaskId Id { set => _core.Id = value; readonly get => _core.Id; }
    internal SchedulingOptions? SchedulingOptions { set => _core.SchedulingOptions = value; readonly get => _core.SchedulingOptions; }
    internal ConfiguredDurableTask<TResult> WithId(string id)
    {
        _core.SetTaskIdCore(id);
        return this;
    }

    internal ConfiguredDurableTask<TResult> WithSchedulingOptions(SchedulingOptions? options)
    {
        SchedulingOptions = options;
        return this;
    }

    public async readonly ValueTask<ScheduledTask<TResult>> ScheduleAsync()
    {
        if (Task is not ISchedulableTask schedulableTask)
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(Id, SchedulingOptions);
        return new ScheduledDurableTask<TResult>(executionContext);
    }

    public DurableTaskAwaiter<TResult> GetAwaiter() => new (_core.InvokeAsync());
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context) => new(Response.FromResult(value));
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask<TResult>(Func<Task<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func());
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask(Func<Task> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func();
            return Response.Completed;
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult>(Func<ValueTask<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func());
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask(Func<ValueTask> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func();
            return Response.Completed;
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask<TResult>(Func<TResult> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return new(Response.FromResult(func()));
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask(Action func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            func();
            return new(Response.Completed);
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
        }
    }
}
