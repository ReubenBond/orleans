using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask"/>.
/// </summary>
public struct DurableTaskMethodBuilder
{
    private UntypedDurableTaskMethodInvocation _taskSource;

    public readonly DurableTask Task => _taskSource;

    public static DurableTaskMethodBuilder Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        _taskSource = DurableTaskMethodInvocation.Create(ref stateMachine);
    }

    public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public readonly void SetException(Exception exception)
    {
        _taskSource.SetException(exception);
    }

    public readonly void SetResult()
    {
        _taskSource.SetResult();
    }

    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }
}

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask{TResult}"/>.
/// </summary>
public struct DurableTaskMethodBuilder<TResult>
{
    private DurableTaskMethodInvocation<TResult> _taskSource;

    public readonly DurableTask<TResult> Task => _taskSource;

    public static DurableTaskMethodBuilder<TResult> Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        _taskSource = DurableTaskMethodInvocation.Create<TResult, TStateMachine>(ref stateMachine);
    }

    public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public readonly void SetException(Exception exception)
    {
        _taskSource.SetException(exception);
    }

    public readonly void SetResult(TResult result)
    {
        _taskSource.SetResult(result);
    }

    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }
}
