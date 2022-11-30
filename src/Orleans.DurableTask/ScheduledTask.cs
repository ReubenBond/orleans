using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace Orleans.Vesuvius;

public interface IStateMachineManager
{
    void RegisterStateMachine(string name, IStateMachine stateMachine);
    ValueTask SnapshotAsync();
    ValueTask WriteStateAsync();
}

public interface IStateMachineStorage
{
    MemoryPool<byte> MemoryPool { get; }
    void AppendEntry(LogEntry entry);
    void AppendEntries(ICollection<LogEntry> entry);
    ValueTask WaitForCommit(StateMachineVersion minVersion);
    void RequestSnapshot();
}

public interface IStateMachine
{
    string Name { get; }
    void OnInitialize(IStateMachineStorage storage);
    ValueTask OnRestoreSnapshotAsync(Snapshot snapshot);
    ValueTask OnReplayLogEntryAsync(LogEntry logEntry);
    Snapshot CreateSnapshot();
}

public readonly struct LogEntry
{
    public string StateMachineName { get; init; }
    public StateMachineVersion Version { get; init; }
    public ReadOnlySequence<byte> Data { get; init; }
}

public readonly struct Snapshot
{
    public string StateMachineName { get; init; }
    public StateMachineVersion Version { get; init; }
    public ReadOnlySequence<byte> Data { get; init; }
}

public readonly struct StateMachineVersion
{
    public StateMachineVersion(long version) { Value = version; }
    public long Value { get; init; }
}

/*public readonly struct StateMachineVersion : IComparable<StateMachineVersion>, IEquatable<StateMachineVersion>
{
    public static StateMachineVersion MinValue => long.MinValue;
    public static StateMachineVersion MaxValue => long.MaxValue;

    public StateMachineVersion(long version) { Value = version; }
    public long Value { get; init; }
    public static implicit operator long (StateMachineVersion id) => id.Value;
    public static implicit operator StateMachineVersion(long value) => new (value);
    public int CompareTo(StateMachineVersion other) => Value.CompareTo(other.Value);
    public bool Equals(StateMachineVersion other) => Value == other.Value;
    public override bool Equals(object? obj) => obj switch
    {
        StateMachineVersion other => Equals(other),
        _ => false
    };

    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(StateMachineVersion left, StateMachineVersion right) => left.Equals(right);
    public static bool operator !=(StateMachineVersion left, StateMachineVersion right) => !(left == right);
    public static bool operator <(StateMachineVersion left, StateMachineVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(StateMachineVersion left, StateMachineVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(StateMachineVersion left, StateMachineVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(StateMachineVersion left, StateMachineVersion right) => left.CompareTo(right) >= 0;
}
*/


public abstract class ScheduledTask
{
    internal abstract DurableTask DurableTask { get; }

    protected ScheduledTask(ScheduledTaskId taskId)
    {
        Id = taskId;
    }

    public ScheduledTaskId Id { get; }

    public ValueTask RescheduleAsync(DateTimeOffset dueTime)
    {
        return default;
    }

    public ValueTask CancelAsync()
    {
        return default;
    }

    public abstract Task AsTask();

    public ScheduledTaskAwaiter GetAwaiter() => new (this);

    protected internal abstract ValueTask AsUntypedValueTask();
}

public class ScheduledTask<TResult> : ScheduledTask, IValueTaskSource<TResult>, IValueTaskSource
{
    private readonly DurableTask<TResult> _durableTaskDefinition;
    private ManualResetValueTaskSourceCore<TResult> _taskSource;

    internal ScheduledTask(ScheduledTaskId taskId, DurableTask<TResult> durableTask) : base(taskId)
    {
        _durableTaskDefinition = durableTask;
    }

    internal override DurableTask<TResult> DurableTask => _durableTaskDefinition;

    public override async Task<TResult> AsTask() => await this;

    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this);

    protected internal override ValueTask AsUntypedValueTask() => new(this, _taskSource.Version);
    internal ValueTask<TResult> AsValueTask() => new(this, _taskSource.Version);

    TResult IValueTaskSource<TResult>.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource<TResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    internal void SetResult(TResult result) => _taskSource.SetResult(result);
    internal void SetException(Exception exception) => _taskSource.SetException(exception);
}

internal sealed class UntypedDurableTaskInvocation : ScheduledTask, IValueTaskSource
{
    private readonly DurableTask _durableTaskDefiniton;
    private ManualResetValueTaskSourceCore<VoidTaskResult> _taskSource;

    internal UntypedDurableTaskInvocation(ScheduledTaskId taskId, DurableTask durableTaskDefinition) : base(taskId)
    {
        _durableTaskDefiniton = durableTaskDefinition;
    }

    internal override DurableTask DurableTask => _durableTaskDefiniton;

    public new ScheduledTaskAwaiter GetAwaiter() => new(this);

    public override async Task AsTask() => await this;

    protected internal override ValueTask AsUntypedValueTask() => new(this, _taskSource.Version);

    void IValueTaskSource.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    internal void SetResult() => _taskSource.SetResult(default);
    internal void SetException(Exception exception) => _taskSource.SetException(exception);
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
    private readonly ValueTaskAwaiter<TResult> _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask<TResult> durableTaskInvocation)
    {
#pragma warning disable CA2012 // Use ValueTasks correctly
        _awaiter = durableTaskInvocation.AsValueTask().GetAwaiter();
#pragma warning restore CA2012 // Use ValueTasks correctly
    }

    public TResult GetResult() => _awaiter.GetResult();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}