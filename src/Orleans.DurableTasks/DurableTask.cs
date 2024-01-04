using System.Diagnostics.Contracts;
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

    /// <summary>
    /// Invokes the task with the provided context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <returns>The response.</returns>
    protected internal abstract ValueTask<Response> InvokeAsync(DurableTaskContext context);

    public new DurableTaskAwaiter GetAwaiter() => new ConfiguredDurableTask(this).GetAwaiter();

    /// <summary>
    /// Sets the identifier for this task.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public ConfiguredDurableTask WithId(string id)
    {
        var result = new ConfiguredDurableTask(this);
        result.WithId(id);
        return result;
    }
}

[InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
[GenerateSerializer, SerializerTransparent]
[Alias("DurableTask`1")]
public abstract class DurableTask<TResult> : DurableTask
{
    public new DurableTaskAwaiter<TResult> GetAwaiter() => new ConfiguredDurableTask<TResult>(this).GetAwaiter();

    /// <summary>
    /// Sets the identifier for this task.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public new ConfiguredDurableTask<TResult> WithId(string id)
    {
        var result = new ConfiguredDurableTask<TResult>(this);
        result.WithId(id);
        return result;
    }
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
                Id = parentContext.CreateChildTaskId();
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

    internal void SetTaskIdCore(string id)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(id);
        if (!Id.IsDefault)
        {
            throw new InvalidOperationException("Id already specified");
        }

        if (ParentContext is { } parentContext)
        {
            Id = parentContext.Id.Child(id);
        }
        else
        {
            Id = TaskId.Create(id);
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

    public DurableTaskAwaiter<TResult> GetAwaiter() => new (_core.InvokeAsync());
}

internal interface ICompletedDurableTask
{
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>, ICompletedDurableTask
{
    protected internal override ValueTask<Response> InvokeAsync(DurableTaskContext context) => new(Response.FromResult(value));
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult>(Func<ValueTask<TResult>> func) : DurableTask<TResult>
{
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
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask(Func<ValueTask> func) : DurableTask
{
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
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask(Action func) : DurableTask
{
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
