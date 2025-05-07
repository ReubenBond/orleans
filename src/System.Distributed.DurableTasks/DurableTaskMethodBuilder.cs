using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask"/>.
/// </summary>
public readonly struct DurableTaskMethodBuilder
{
    private readonly DurableTaskMethodInvocation _taskSource;
    public readonly DurableTask Task => _taskSource;

    public DurableTaskMethodBuilder()
    {
        _taskSource = new();
    }

    public static DurableTaskMethodBuilder Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly).
        _taskSource.SetStateMachine(stateMachine);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) => SetStateMachineCore(stateMachine);

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

    internal static void SetStateMachineCore(IAsyncStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);

        // SetStateMachine was originally needed in order to store the boxed state machine reference into
        // the boxed copy.  Now that a normal box is no longer used, SetStateMachine is also legacy.  We need not
        // do anything here, and thus assert to ensure we're not calling this from our own implementations.
        Debug.Fail("SetStateMachine should not be used.");
    }
}

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask{TResult}"/>.
/// </summary>
public readonly struct DurableTaskMethodBuilder<TResult>
{
    private readonly DurableTaskMethodInvocation<TResult> _taskSource;
    public readonly DurableTask<TResult> Task => _taskSource;

    public DurableTaskMethodBuilder()
    {
        _taskSource = new();
    }

    public static DurableTaskMethodBuilder<TResult> Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        _taskSource.SetStateMachine(stateMachine);
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) => DurableTaskMethodBuilder.SetStateMachineCore(stateMachine);

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
