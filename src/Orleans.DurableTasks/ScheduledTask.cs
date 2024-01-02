using System.Runtime.CompilerServices;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask
{
    public abstract TaskId Id { get; }
    public abstract Task AsTask();
    public ScheduledTaskAwaiter GetAwaiter() => new(this);
    protected internal abstract ValueTask AsUntypedValueTask();
}

public abstract class ScheduledTask<TResult> : ScheduledTask
{
    public override async Task<TResult> AsTask() => await this;
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this);
    internal abstract ValueTask<Response> AsValueTask();
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
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this);
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
    public new ScheduledTaskAwaiter GetAwaiter() => new(this);
    protected internal override ValueTask AsUntypedValueTask() => _executionContext.AsUntypedValueTask();
}

public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _durableTaskInvocation;

    internal ScheduledTaskAwaiter(ScheduledTask durableTaskInvocation)
    {
#pragma warning disable CA2012 // Use ValueTasks correctly
        _durableTaskInvocation = durableTaskInvocation.AsUntypedValueTask().GetAwaiter();
#pragma warning restore CA2012 // Use ValueTasks correctly
    }

    public void GetResult() => _durableTaskInvocation.GetResult();
    public bool IsCompleted => _durableTaskInvocation.IsCompleted;
    public void OnCompleted(Action continuation) => _durableTaskInvocation.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _durableTaskInvocation.UnsafeOnCompleted(continuation);
}

public readonly struct ScheduledTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<Response> _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask<TResult> durableTaskInvocation)
    {
#pragma warning disable CA2012 // Use ValueTasks correctly
        _awaiter = durableTaskInvocation.AsValueTask().GetAwaiter();
#pragma warning restore CA2012 // Use ValueTasks correctly
    }

    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}