using System.Distributed.DurableTasks.Scheduling;
using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Serialization.Invocation;

namespace System.Distributed.DurableTasks;

[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
[GenerateSerializer, SerializerTransparent]
[Alias("DurableTask")]
public abstract partial class DurableTask
{
    public static DurableTask<TResult> FromResult<TResult>(TResult value) => new CompletedDurableTask<TResult>(value);

    public static DurableTask Run(Action<CancellationToken> func) => new DelegateDurableTask(func);
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, TResult> func) => new DelegateDurableTask<TResult>(func);
    public static DurableTask Run(Func<CancellationToken, ValueTask> func) => new AsyncDelegateDurableTask(func);
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, ValueTask<TResult>> func) => new AsyncDelegateDurableTask<TResult>(func);
    public static DurableTask Run(Func<CancellationToken, Task> func) => new AsyncTaskDelegateDurableTask(func);
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, Task<TResult>> func) => new AsyncTaskDelegateDurableTask<TResult>(func);

    public static DurableTask Run<TState>(Action<TState, CancellationToken> func, TState state) => new DelegateDurableTaskWithState<TState>(func, state);
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, TResult> func, TState state) => new DelegateDurableTaskWithState<TState, TResult>(func, state);
    public static DurableTask Run<TState>(Func<TState, CancellationToken, ValueTask> func, TState state) => new AsyncDelegateDurableTaskWithState<TState>(func, state);
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, ValueTask<TResult>> func, TState state) => new AsyncDelegateDurableTaskWithState<TState, TResult>(func, state);
    public static DurableTask Run<TState>(Func<TState, CancellationToken, Task> func, TState state) => new AsyncTaskDelegateDurableTaskWithState<TState>(func, state);
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> func, TState state) => new AsyncTaskDelegateDurableTaskWithState<TState, TResult>(func, state);

    /// <summary>
    /// Invokes the task with the provided context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <returns>The response.</returns>
    protected internal abstract ValueTask<Response> RunAsync(DurableTaskContext context);
}

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

    internal ValueTask<Response> RunAsync()
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
            return parentContext.RunAsync(Id, Task, CancellationToken.None);
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

    private readonly async ValueTask<Response> ScheduleAndAwaitAsync(ISchedulableTask schedulableTask)
    {
        var context = await schedulableTask.ScheduleAsync(Id, SchedulingOptions);
        return await context.AsValueTask();
    }

    internal void SetTaskIdCore(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Id.IsDefault)
        {
            throw new InvalidOperationException($"This task's {nameof(TaskId)} has already been specified.");
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

    private static InvalidOperationException GetNonSchedulableTaskException() => new(
        $"The provided task does not support scheduling and was not executed in the context of an existing {nameof(DurableTask)}. This may be because it is a local method or another non-serializable task type.");
}

public struct ConfiguredDurableTask(DurableTask task)
{
    private ConfiguredDurableTaskCore<DurableTask> _core = new(task);

    public DurableTaskAwaiter GetAwaiter() => new(_core.RunAsync());

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

    // Schedules a durable task without waiting for the task to complete
    public readonly async ValueTask<ScheduledTask> ScheduleAsync()
    {
        if (Task is not ISchedulableTask schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(Id, SchedulingOptions);
        return new ScheduledDurableTask(executionContext);
    }

    internal static InvalidOperationException GetNonSchedulableTaskException() => new("The provided task does not support scheduling. This may be because it is a local method or another non-serializable task type.");
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

    public readonly async ValueTask<ScheduledTask<TResult>> ScheduleAsync()
    {
        if (Task is not ISchedulableTask schedulableTask)
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(Id, SchedulingOptions);
        return new ScheduledDurableTask<TResult>(executionContext);
    }

    public DurableTaskAwaiter<TResult> GetAwaiter() => new(_core.RunAsync());
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> RunAsync(DurableTaskContext context) => new(Response.FromResult(value));
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask<TResult>(Func<CancellationToken, Task<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func(context.CancellationToken));
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
internal sealed class AsyncTaskDelegateDurableTask(Func<CancellationToken, Task> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(context.CancellationToken);
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
internal sealed class AsyncDelegateDurableTask<TResult>(Func<CancellationToken, ValueTask<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func(context.CancellationToken));
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
internal sealed class AsyncDelegateDurableTask(Func<CancellationToken, ValueTask> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(context.CancellationToken);
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
internal sealed class DelegateDurableTask<TResult>(Func<CancellationToken, TResult> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return new(Response.FromResult(func(context.CancellationToken)));
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
internal sealed class DelegateDurableTask(Action<CancellationToken> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            func(context.CancellationToken);
            return new(Response.Completed);
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
internal sealed class AsyncTaskDelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func(state, context.CancellationToken));
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
internal sealed class AsyncTaskDelegateDurableTaskWithState<TState>(Func<TState, CancellationToken, Task> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(state, context.CancellationToken);
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
internal sealed class AsyncDelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, ValueTask<TResult>> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return Response.FromResult(await func(state, context.CancellationToken));
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
internal sealed class AsyncDelegateDurableTaskWithState<TState>(Func<TState, CancellationToken, ValueTask> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(state, context.CancellationToken);
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
internal sealed class DelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, TResult> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return new(Response.FromResult(func(state, context.CancellationToken)));
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
internal sealed class DelegateDurableTaskWithState<TState>(Action<TState, CancellationToken> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<Response> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            func(state, context.CancellationToken);
            return new(Response.Completed);
        }
        catch (Exception exception)
        {
            return new(Response.FromException(exception));
        }
    }
}

