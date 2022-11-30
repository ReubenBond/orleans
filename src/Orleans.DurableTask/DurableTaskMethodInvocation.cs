using System.Threading.Tasks.Sources;

namespace Orleans.Vesuvius;

/// <summary>
/// Represents a locally-executing <see cref="DurableTask"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation : DurableTask, IValueTaskSource, IDurableTaskMethodInvocation
{
    private ManualResetValueTaskSourceCore<VoidTaskResult> _taskSource = new();

    protected override ValueTask<ScheduledTask> ScheduleAsyncCore(ScheduledTaskId taskId, SchedulingOptions? options) => new(new UntypedDurableTaskInvocation(taskId, this));

    public ValueTask AsUntypedValueTask() => new ValueTask(this, _taskSource.Version);

    void IValueTaskSource.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    public void SetResult() => _taskSource.SetResult(default);
    public void SetException(Exception exception) => _taskSource.SetException(exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation<TResult> : DurableTask<TResult>, IValueTaskSource<TResult>, IValueTaskSource, IDurableTaskMethodInvocation
{
    private ManualResetValueTaskSourceCore<TResult> _taskSource = new();

    protected override ValueTask<ScheduledTask<TResult>> ScheduleAsyncTypedCore(ScheduledTaskId taskId, SchedulingOptions? options) => new(new ScheduledTask<TResult>(taskId, this));

    public ValueTask<TResult> AsValueTask() => new ValueTask<TResult>(this, _taskSource.Version);
    public ValueTask AsUntypedValueTask() => new ValueTask(this, _taskSource.Version);

    TResult IValueTaskSource<TResult>.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource<TResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    public void SetResult(TResult result) => _taskSource.SetResult(result);
    public void SetException(Exception exception) => _taskSource.SetException(exception);
}

/// <summary>
/// Support for converting awaiting the completion of a <see cref="DurableTaskMethodInvocation{TResult}"/> instance which has been cast to an untyped <see cref="DurableTask"/> instance.
/// </summary>
internal interface IDurableTaskMethodInvocation
{
    ValueTask AsUntypedValueTask();
}