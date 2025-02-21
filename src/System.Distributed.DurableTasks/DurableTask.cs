using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System.Distributed.DurableTasks;

[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
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
    protected internal abstract ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context);
}

[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask
{
}

internal struct ConfiguredDurableTaskCore<TDurableTask> where TDurableTask : DurableTask
{
    internal readonly TDurableTask Task;
    internal readonly DurableTaskContext? ParentContext = DurableTaskContext.CurrentContext;
    internal TaskId Id;

    public ConfiguredDurableTaskCore(TDurableTask task, TaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        Task = task;
        Id = taskId;
    }

    // TODO: move id logic to ctor or a Create method
    internal ValueTask<DurableTaskResponse> RunAsync(CancellationToken cancellationToken)
    {
        if (ParentContext is { } parentContext)
        {
            if (Id.IsDefault)
            {
                // Allocate a child identifier for the task.
                Id = parentContext.CreateChildTaskId(null);
            }
            else if (!Id.IsChildOf(parentContext.Id))
            {
                // Make the id a child id, since it's running in the context of a parent task.
                Id = parentContext.CreateChildTaskId(Id.ToString());
            }

            // Evaluates the task: if it is a local method, it will be executed immediately.
            // This will return once the task has completed.
            return parentContext.RunChildTaskAsync(Id, Task, cancellationToken);
        }
        else if (Task is ISchedulableTask schedulableTask)
        {
            if (Id.IsDefault)
            {
                // Select a random identifier for the task.
                // The caller will need to query for the task to find its identifier.
                Id = TaskId.CreateRandom();
            }

            // Schedules the task and await completion.
            return ScheduleAndWaitAsync(Id, schedulableTask, cancellationToken);

            static async ValueTask<DurableTaskResponse> ScheduleAndWaitAsync(TaskId taskId, ISchedulableTask schedulableTask, CancellationToken cancellationToken)
            {
                var context = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
                return await context.GetResponseAsync().WaitAsync(cancellationToken);
            }
        }
        else
        {
            throw GetNonSchedulableTaskException();
        }
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

public struct ConfiguredDurableTask(DurableTask task, TaskId taskId)
{
    private ConfiguredDurableTaskCore<DurableTask> _core = new(task, taskId);

    public DurableTaskAwaiter GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    internal readonly DurableTask Task => _core.Task;
    internal TaskId Id { set => _core.Id = value; readonly get => _core.Id; }
    internal ConfiguredDurableTask WithId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _core.SetTaskIdCore(id);
        return this;
    }

    // Schedules a durable task without waiting for the task to complete
    public readonly async ValueTask<ScheduledTask> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        if (Task is not ISchedulableTask schedulableTask)
        {
            throw GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(Id, cancellationToken);
        return new ScheduledDurableTask(executionContext);
    }

    // Cancels a durable task without waiting for the task to complete
    public readonly async ValueTask<bool> CancelAsync(CancellationToken cancellationToken)
    {
        if (Task is not ICancellableTask cancellableTask)
        {
            return false;
        }

        await cancellableTask.CancelAsync(Id, cancellationToken);
        return true;
    }

    internal static InvalidOperationException GetNonSchedulableTaskException() => new("The provided task does not support scheduling. This may be because it is a local method or another non-serializable task type.");
}

public struct ConfiguredDurableTask<TResult>(DurableTask<TResult> task, TaskId taskId)
{
    private ConfiguredDurableTaskCore<DurableTask<TResult>> _core = new(task, taskId);

    internal readonly DurableTask<TResult> Task => _core.Task;
    internal TaskId Id { set => _core.Id = value; readonly get => _core.Id; }
    internal ConfiguredDurableTask<TResult> WithId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _core.SetTaskIdCore(id);
        return this;
    }

    public readonly async ValueTask<ScheduledTask<TResult>> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        if (Task is not ISchedulableTask schedulableTask)
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        var executionContext = await schedulableTask.ScheduleAsync(Id, cancellationToken);
        return new ScheduledDurableTask<TResult>(executionContext);
    }

    public DurableTaskAwaiter<TResult> GetAwaiter() => new(_core.RunAsync(CancellationToken.None));
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) => new(DurableTaskResponse.FromResult(value));
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask<TResult>(Func<CancellationToken, Task<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return DurableTaskResponse.FromResult(await func(context.CancellationToken));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask(Func<CancellationToken, Task> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(context.CancellationToken);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask<TResult>(Func<CancellationToken, ValueTask<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return DurableTaskResponse.FromResult(await func(context.CancellationToken));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTask(Func<CancellationToken, ValueTask> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(context.CancellationToken);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask<TResult>(Func<CancellationToken, TResult> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return new(DurableTaskResponse.FromResult(func(context.CancellationToken)));
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask(Action<CancellationToken> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            func(context.CancellationToken);
            return new(DurableTaskResponse.Completed);
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return DurableTaskResponse.FromResult(await func(state, context.CancellationToken));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTaskWithState<TState>(Func<TState, CancellationToken, Task> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(state, context.CancellationToken);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, ValueTask<TResult>> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return DurableTaskResponse.FromResult(await func(state, context.CancellationToken));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTaskWithState<TState>(Func<TState, CancellationToken, ValueTask> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            await func(state, context.CancellationToken);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, TResult> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            return new(DurableTaskResponse.FromResult(func(state, context.CancellationToken)));
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTaskWithState<TState>(Action<TState, CancellationToken> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        try
        {
            DurableTaskContext.SetCurrentContext(context);
            func(state, context.CancellationToken);
            return new(DurableTaskResponse.Completed);
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
    }
}

public enum DurableTaskResponseStatus
{
    None,
    Pending,
    Subscribed,
    Success,
    Error,
}

internal static class ResponseKindExtensions
{
    public static bool IsCompleted(this DurableTaskResponseStatus value) => value is DurableTaskResponseStatus.Success or DurableTaskResponseStatus.Error;
}

/// <summary>
/// Represents the result of a method invocation.
/// </summary>
public abstract class DurableTaskResponse
{
    // Internal constructor to prevent external inheritance.
    internal DurableTaskResponse()
    {
    }

    /// <summary>
    /// Creates a new response representing an exception.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>A new response.</returns>
    public static ExceptionDurableTaskResponse FromException(Exception exception) => new(exception);

    /// <summary>
    /// Creates a new response object which has been fulfilled with the provided value.
    /// </summary>
    /// <typeparam name="TResult">The underlying result type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>A new response.</returns>
    public static DurableTaskResponse<TResult> FromResult<TResult>(TResult value) => new(value);

    /// <summary>
    /// Gets a completed response with no value.
    /// </summary>
    public static SuccessDurableTaskResponse Completed => SuccessDurableTaskResponse.Instance;

    /// <summary>
    /// Gets a pending response.
    /// </summary>
    public static PendingDurableTaskResponse Pending => PendingDurableTaskResponse.Instance;

    /// <summary>
    /// Gets a subscribed response.
    /// </summary>
    public static SubscribedDurableTaskResponse Subscribed => SubscribedDurableTaskResponse.Instance;

    /// <summary>
    /// Gets a value indicating whether the response represents a completed task.
    /// </summary>
    public bool IsCompleted => Status.IsCompleted();

    /// <summary>
    /// Gets the response status.
    /// </summary>
    public abstract DurableTaskResponseStatus Status { get; }

    /// <summary>
    /// Gets the result value.
    /// </summary>
    /// <remarks>
    /// If the response represents an exception, this property will throw the exception.
    /// If the response represents an incomplete task, this property will throw an exception.
    /// </remarks>
    public abstract object? Result { get; }

    /// <summary>
    /// Gets the static type of the result value, or <see langword="null"/> if this response does not have a result value.
    /// </summary>
    public virtual Type? ResultType => null;

    /// <summary>
    /// Gets the exception or <see langword="null" /> if the response does not represent an exception.
    /// </summary>
    public abstract Exception? Exception { get; }

    /// <summary>
    /// Gets the result value with the specified type.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <returns>The result value.</returns>
    public abstract T GetResult<T>();
}

/// <summary>
/// Represents a successfully completed task.
/// </summary>
public sealed class SuccessDurableTaskResponse : DurableTaskResponse
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static SuccessDurableTaskResponse Instance { get; } = new SuccessDurableTaskResponse();

    /// <inheritdoc/>
    public override object? Result => null;

    /// <inheritdoc/>
    public override Exception? Exception => null;

    public override DurableTaskResponseStatus Status => DurableTaskResponseStatus.Success;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override string ToString() => "[Success]";
}

/// <summary>
/// A <see cref="DurableTaskResponse"/> which represents an exception, a broken promise.
/// </summary>
public sealed class ExceptionDurableTaskResponse : DurableTaskResponse
{
    public ExceptionDurableTaskResponse(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <inheritdoc/>
    public override object? Result
    {
        get
        {
            ExceptionDispatchInfo.Capture(Exception!).Throw();
            return null;
        }
    }

    /// <inheritdoc/>
    public override Exception Exception { get; }

    /// <inheritdoc/>
    public override DurableTaskResponseStatus Status => DurableTaskResponseStatus.Error;

    /// <inheritdoc/>
    public override T GetResult<T>()
    {
        ExceptionDispatchInfo.Capture(Exception!).Throw();
        return default;
    }

    /// <inheritdoc/>
    public override string ToString() => $"[Error: {Exception?.ToString()}]";
}

/// <summary>
/// A <see cref="DurableTaskResponse"/> which represents a typed value.
/// </summary>
/// <typeparam name="TResult">The underlying result type.</typeparam>
public sealed class DurableTaskResponse<TResult>(TResult result) : DurableTaskResponse
{
    public TResult TypedResult { get => result; }

    public override Exception? Exception => null;

    public override object? Result => result;

    public override DurableTaskResponseStatus Status => DurableTaskResponseStatus.Success;

    public override Type ResultType => typeof(TResult);

    public override T GetResult<T>()
    {
        if (typeof(TResult).IsValueType && typeof(T).IsValueType && typeof(T) == typeof(TResult))
            return Unsafe.As<TResult, T>(ref result!);

        return (T)(object)result!;
    }

    public override string ToString() => $"[Success: '{result?.ToString()}']";
}

/// <summary>
/// Represents a pending result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> invocation.
/// </summary>
public sealed class PendingDurableTaskResponse : DurableTaskResponse
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static PendingDurableTaskResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object? Result => throw new InvalidOperationException("The task has not completed yet.");

    /// <inheritdoc/>
    public override Exception? Exception => null;

    /// <inheritdoc/>
    public override DurableTaskResponseStatus Status => DurableTaskResponseStatus.Pending;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override string ToString() => "[Pending]";
}

/// <summary>
/// Represents an intermediary response indicating that the caller is subscribed to the task and the task has not completed yet.
/// </summary>
public sealed class SubscribedDurableTaskResponse : DurableTaskResponse
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static SubscribedDurableTaskResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object? Result => throw new InvalidOperationException("The task has not completed yet.");

    /// <inheritdoc/>
    public override Exception? Exception => null;

    /// <inheritdoc/>
    public override DurableTaskResponseStatus Status => DurableTaskResponseStatus.Subscribed;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override string ToString() => "[Subscribed]";
}

internal static class ResponseExtensions
{
    public static void ThrowIfExceptionResponse(this DurableTaskResponse response)
    {
        if (response.Exception is { } exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
