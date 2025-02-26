using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace System.Distributed.DurableTasks;

internal static class DurableTaskMethodInvocation
{
    public static UntypedDurableTaskMethodInvocation<TStateMachine> Create<TStateMachine>(scoped ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => UntypedDurableTaskMethodInvocation<TStateMachine>.Create(ref stateMachine);
    public static DurableTaskMethodInvocation<TResult, TStateMachine> Create<TResult, TStateMachine>(scoped ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => DurableTaskMethodInvocation<TResult, TStateMachine>.Create(ref stateMachine);
}

internal abstract class UntypedDurableTaskMethodInvocation : DurableTask
{
    public abstract void SetResult();

    public abstract void SetException(Exception exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask"/> method.
/// </summary>
internal sealed class UntypedDurableTaskMethodInvocation<TStateMachine> : UntypedDurableTaskMethodInvocation, IAsyncStateMachine, IValueTaskSource<DurableTaskResponse>
    where TStateMachine : IAsyncStateMachine
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _completion = new();
    private DurableExecutionContext? _executionContext;

#pragma warning disable IDE0044 // Add readonly modifier
    private TStateMachine _stateMachine;
#pragma warning restore IDE0044 // Add readonly modifier

    private UntypedDurableTaskMethodInvocation(TStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    public static UntypedDurableTaskMethodInvocation<TStateMachine> Create(scoped ref TStateMachine stateMachine)
    {
        var result = new UntypedDurableTaskMethodInvocation<TStateMachine>(stateMachine);
        stateMachine.SetStateMachine(result);
        return result;
    }

    private void StartInvocation() => ((IAsyncStateMachine)this).MoveNext();
    void IAsyncStateMachine.MoveNext()
    {
        // TODO: is this the best & most efficient way to propagate the context? It seems like it would be costly to do this for every await point.
        DurableExecutionContext.SetCurrentContext(_executionContext, out var previousContext);
        try
        {
            _stateMachine.MoveNext();
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }
    }

    void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine.SetStateMachine(stateMachine);

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        _executionContext = executionContext;
        StartInvocation();
        return new(this, _completion.Version);
    }

    public override void SetResult() => _completion.SetResult(DurableTaskResponse.Completed);
    public override void SetException(Exception exception) => _completion.SetResult(DurableTaskResponse.FromException(exception));
    public DurableTaskResponse GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal abstract class DurableTaskMethodInvocation<TResult> : DurableTask<TResult>
{
    public abstract void SetResult(TResult result);
    public abstract void SetException(Exception exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation<TResult, TStateMachine> : DurableTaskMethodInvocation<TResult>, IAsyncStateMachine, IValueTaskSource<DurableTaskResponse>
    where TStateMachine : IAsyncStateMachine
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _completion = new();
    private DurableExecutionContext? _executionContext;

#pragma warning disable IDE0044 // Add readonly modifier
    private TStateMachine _stateMachine;
#pragma warning restore IDE0044 // Add readonly modifier

    private DurableTaskMethodInvocation(TStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    public static DurableTaskMethodInvocation<TResult, TStateMachine> Create(scoped ref TStateMachine stateMachine)
    {
        var result = new DurableTaskMethodInvocation<TResult, TStateMachine>(stateMachine);
        stateMachine.SetStateMachine(result);
        return result;
    }

    private void StartInvocation() => ((IAsyncStateMachine)this).MoveNext();
    void IAsyncStateMachine.MoveNext()
    {
        // TODO: is this the best & most efficient way to propagate the context? It seems like it would be costly to do this for every await point.
        // Maybe a cheaper alternative would be to use a thread-local in addition to the async-local? Possibly ask Toub about ExecutionContext APIs, etc...
        DurableExecutionContext.SetCurrentContext(_executionContext, out var previousContext);
        try
        {
            _stateMachine.MoveNext();
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }
    }

    void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine.SetStateMachine(stateMachine);

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        _executionContext = executionContext;
        StartInvocation();
        return new(this, _completion.Version);
    }

    public override void SetResult(TResult result) => _completion.SetResult(DurableTaskResponse.FromResult(result));
    public override void SetException(Exception exception) => _completion.SetException(exception);

    public DurableTaskResponse GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}
