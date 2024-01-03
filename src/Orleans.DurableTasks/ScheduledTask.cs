using System.Runtime.CompilerServices;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask
{
    /// <summary>
    /// Gets the task identifier.
    /// </summary>
    public abstract TaskId Id { get; }

    /// <summary>
    /// Gets a task representing the completion of the operation.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    public abstract Task AsTask();

    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public ScheduledTaskAwaiter GetAwaiter() => new(this);

    protected internal abstract ValueTask AsUntypedValueTask();
}

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask<TResult> : ScheduledTask
{
    /// <summary>
    /// Gets a task representing the completion of the operation.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    public override async Task<TResult> AsTask() => await this;

    /// <summary>
    /// Gets a task representing the completion of the operation.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    internal abstract ValueTask<Response> AsValueTask();

    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask{TResult}"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this);
}

internal sealed class ScheduledDurableTask<TResult> : ScheduledTask<TResult>
{
    private readonly DurableTaskContext _executionContext;

    internal ScheduledDurableTask(DurableTaskContext executionContext)
    {
        _executionContext = executionContext;
    }

    public override TaskId Id => _executionContext.Id;
    public override async Task<TResult> AsTask() => await this;
    protected internal override ValueTask AsUntypedValueTask() => _executionContext.AsUntypedValueTask();
    internal override ValueTask<Response> AsValueTask() => _executionContext.AsValueTask();
}

internal sealed class ScheduledDurableTask : ScheduledTask
{
    private readonly DurableTaskContext _executionContext;

    internal ScheduledDurableTask(DurableTaskContext executionContext)
    {
        _executionContext = executionContext;
    }

    public override TaskId Id => _executionContext.Id;
    public override Task AsTask() => _executionContext.AsUntypedValueTask().AsTask();
    protected internal override ValueTask AsUntypedValueTask() => _executionContext.AsUntypedValueTask();
}

/// <summary>
/// An awaiter for <see cref="ScheduledTask"/>.
/// </summary>
public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask durableTaskInvocation)
    {
#pragma warning disable CA2012 // Use ValueTasks correctly
        _awaiter = durableTaskInvocation.AsUntypedValueTask().GetAwaiter();
#pragma warning restore CA2012 // Use ValueTasks correctly
    }

    /// <summary>
    /// Gets the result of the task.
    /// </summary>
    public void GetResult() => _awaiter.GetResult();

    /// <summary>
    /// Returns a value indicating whether the task has completed.
    /// </summary>
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

/// <summary>
/// An awaiter for <see cref="ScheduledTask{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">The underlying result type.</typeparam>
public readonly struct ScheduledTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<Response> _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask<TResult> durableTaskInvocation)
    {
#pragma warning disable CA2012 // Use ValueTasks correctly
        _awaiter = durableTaskInvocation.AsValueTask().GetAwaiter();
#pragma warning restore CA2012 // Use ValueTasks correctly
    }

    /// <summary>
    /// Gets the result of the task.
    /// </summary>
    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();

    /// <summary>
    /// Returns a value indicating whether the task has completed.
    /// </summary>
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}