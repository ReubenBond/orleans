using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask"/>.
/// </summary>
public struct DurableTaskMethodBuilder
{
    private UntypedDurableTaskMethodInvocation _taskSource;

    public DurableTask Task => _taskSource;

    public static DurableTaskMethodBuilder Create() => new ();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        _taskSource = DurableTaskMethodInvocation.Create(ref stateMachine);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public void SetException(Exception exception)
    {
        _taskSource.SetException(exception);
    }

    public void SetResult()
    {
        _taskSource.SetResult();
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
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

    public DurableTask<TResult> Task => _taskSource;

    public static DurableTaskMethodBuilder<TResult> Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        _taskSource = DurableTaskMethodInvocation.Create<TResult, TStateMachine>(ref stateMachine);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public void SetException(Exception exception)
    {
        _taskSource.SetException(exception);
    }

    public void SetResult(TResult result)
    {
        _taskSource.SetResult(result);
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }
}