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

    public ScheduledTaskId Id { get; }

    public ValueTask RescheduleAsync(DateTimeOffset dueTime)
    {
        return default;
    }

    public ValueTask CancelAsync()
    {
        return default;
    }

    public async Task AsTask()
    {
        await this;
    }

    public DurableTaskInvocationAwaiter GetAwaiter() => new (this);

    protected internal abstract ValueTask AsUntypedValueTask();
}

public class ScheduledTask<TResult> : ScheduledTask, IValueTaskSource<TResult>, IValueTaskSource
{
    private readonly DurableTask<TResult> _durableTaskDefinition;
    private ManualResetValueTaskSourceCore<TResult> _taskSource;

    internal ScheduledTask(DurableTask<TResult> durableTask)
    {
        _durableTaskDefinition = durableTask;
    }

    internal override DurableTask<TResult> DurableTask => _durableTaskDefinition;

    public new async Task<TResult> AsTask()
    {
        return await this;
    }

    public new DurableTaskInvocationAwaiter<TResult> GetAwaiter() => new (this);

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

    internal UntypedDurableTaskInvocation(DurableTask durableTaskDefinition)
    {
        _durableTaskDefiniton = durableTaskDefinition;
    }

    internal override DurableTask DurableTask => _durableTaskDefiniton;

    public new async Task AsTask()
    {
        await this;
    }

    public new DurableTaskInvocationAwaiter GetAwaiter() => new (this);

    protected internal override ValueTask AsUntypedValueTask() => new(this, _taskSource.Version);

    void IValueTaskSource.GetResult(short token) => _taskSource.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _taskSource.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _taskSource.OnCompleted(continuation, state, token, flags);

    internal void SetResult() => _taskSource.SetResult(default);
    internal void SetException(Exception exception) => _taskSource.SetException(exception);
}

public readonly struct DurableTaskInvocationAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _durableTaskInvocation;

    internal DurableTaskInvocationAwaiter(ScheduledTask durableTaskInvocation)
    {
        _durableTaskInvocation = durableTaskInvocation.AsUntypedValueTask().GetAwaiter();
    }

    public void GetResult() => _durableTaskInvocation.GetResult();
    public bool IsCompleted => _durableTaskInvocation.IsCompleted;
    public void OnCompleted(Action continuation) => _durableTaskInvocation.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _durableTaskInvocation.UnsafeOnCompleted(continuation);
}

public readonly struct DurableTaskInvocationAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<TResult> _awaiter;

    internal DurableTaskInvocationAwaiter(ScheduledTask<TResult> durableTaskInvocation)
    {
        _awaiter = durableTaskInvocation.AsValueTask().GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}