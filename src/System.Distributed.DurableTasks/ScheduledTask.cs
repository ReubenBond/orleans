using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask
{
    internal ScheduledTask() { }

    /// <summary>
    /// Gets the task identifier.
    /// </summary>
    public abstract TaskId Id { get; }

    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public ScheduledTaskAwaiter GetAwaiter() => new(this);

    /// <summary>
    /// Gets the status of the task.
    /// </summary>
    /// <returns>The task status.</returns>
    public virtual async Task<bool> IsCompletedAsync(CancellationToken cancellationToken)
    {
        var result = await PollAsyncCore(cancellationToken);
        return result.Status.IsCompleted();
    }

    /// <summary>
    /// Gets the status of the task.
    /// </summary>
    /// <returns>The task status.</returns>
    public virtual async Task<DurableTaskStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await PollAsyncCore(cancellationToken);
        return result.Status;
    }

    /// <summary>
    /// Gets a task representing the completion of the operation.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    public virtual async ValueTask WaitAsync(CancellationToken cancellationToken) => await WaitAsyncCore(cancellationToken);

    /// <summary>
    /// Attempts to cancel the operation.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token used to signal when the attempt to request cancellation should be abandoned.</param>
    /// <returns>A task representing the completion of the operation.</returns>
    public abstract ValueTask CancelAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a task representing the completion of the operation.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    protected internal abstract ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current status of the task without waiting for the task to complete.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    protected abstract ValueTask<DurableTaskResponse> PollAsyncCore(CancellationToken cancellationToken);
}

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask<TResult> : ScheduledTask
{
    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask{TResult}"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this);
}

internal sealed class ScheduledDurableTask<TResult> : ScheduledTask<TResult>
{
    private readonly IScheduledTaskHandle _handle;

    internal ScheduledDurableTask(IScheduledTaskHandle handle)
    {
        _handle = handle;
    }

    public override TaskId Id => _handle.TaskId;

    public override async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        await _handle.CancelAsync(cancellationToken);
    }

    protected override async ValueTask<DurableTaskResponse> PollAsyncCore(CancellationToken cancellationToken)
    {
        return await _handle.PollAsync(cancellationToken);
    }

    protected internal override async ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
    {
        return await _handle.WaitAsync(cancellationToken);
    }
}

internal sealed class ScheduledDurableTask : ScheduledTask
{
    private readonly IScheduledTaskHandle _handle;

    internal ScheduledDurableTask(IScheduledTaskHandle handle)
    {
        _handle = handle;
    }

    public override TaskId Id => _handle.TaskId;

    public override async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        await _handle.CancelAsync(cancellationToken);
    }

    protected override async ValueTask<DurableTaskResponse> PollAsyncCore(CancellationToken cancellationToken)
    {
        return await _handle.PollAsync(cancellationToken);
    }

    protected internal override async ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
    {
        return await _handle.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// An awaiter for <see cref="ScheduledTask"/>.
/// </summary>
public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask durableTaskInvocation) =>
        _awaiter = durableTaskInvocation.WaitAsync(CancellationToken.None).GetAwaiter();

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
    private readonly ValueTaskAwaiter<DurableTaskResponse> _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask<TResult> durableTaskInvocation) =>
        _awaiter = durableTaskInvocation.WaitAsyncCore(CancellationToken.None).GetAwaiter();

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
