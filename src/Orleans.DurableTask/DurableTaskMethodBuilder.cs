using System.Runtime.CompilerServices;

namespace Orleans.Vesuvius;

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask"/>.
/// </summary>
public readonly struct DurableTaskMethodBuilder
{
    private readonly DurableTaskMethodInvocation _taskSource;

    private DurableTaskMethodBuilder(DurableTaskMethodInvocation taskSource)
    {
        _taskSource = taskSource;
    }

    public DurableTask Task => _taskSource;

    public static DurableTaskMethodBuilder Create() => new DurableTaskMethodBuilder(new DurableTaskMethodInvocation());

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        Console.WriteLine($"Start {stateMachine}");
        stateMachine.MoveNext();
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
        Console.WriteLine($"Set state machine {stateMachine}");
    }

    public void SetException(Exception exception)
    {
        Console.WriteLine($"Set exception {exception}");
        _taskSource.SetException(exception);
    }

    public void SetResult()
    {
        Console.WriteLine($"Set result");
        _taskSource.SetResult();
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        Console.WriteLine($"AwaitOnCompleted {awaiter} ({awaiter.GetType()}) {stateMachine} ({stateMachine.GetType()}");
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        Console.WriteLine($"AwaitUnsafeOnCompleted {awaiter} ({awaiter.GetType()}) {stateMachine} ({stateMachine.GetType()}");
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }
}

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask{TResult}"/>.
/// </summary>
public readonly struct DurableTaskMethodBuilder<TResult>
{
    private readonly DurableTaskMethodInvocation<TResult> _taskSource;

    private DurableTaskMethodBuilder(DurableTaskMethodInvocation<TResult> taskSource) : this()
    {
        _taskSource = taskSource;
    }

    public DurableTask<TResult> Task => _taskSource;

    public static DurableTaskMethodBuilder<TResult> Create() => new DurableTaskMethodBuilder<TResult>(new DurableTaskMethodInvocation<TResult>());

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        Console.WriteLine($"Start {stateMachine}");
        stateMachine.MoveNext();
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
        Console.WriteLine($"Set state machine {stateMachine}");
    }

    public void SetException(Exception exception)
    {
        Console.WriteLine($"Set exception {exception}");
        _taskSource.SetException(exception);
    }

    public void SetResult(TResult result)
    {
        Console.WriteLine($"Set result {result}");
        _taskSource.SetResult(result);
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        Console.WriteLine($"AwaitOnCompleted {awaiter} ({awaiter.GetType()}) {stateMachine} ({stateMachine.GetType()}");
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        Console.WriteLine($"AwaitUnsafeOnCompleted {awaiter} ({awaiter.GetType()}) {stateMachine} ({stateMachine.GetType()}");
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }
}