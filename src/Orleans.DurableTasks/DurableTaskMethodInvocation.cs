using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

internal static class DurableTaskMethodInvocation
{
    public static UntypedDurableTaskMethodInvocation<TStateMachine> Create<TStateMachine>(scoped ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => UntypedDurableTaskMethodInvocation<TStateMachine>.Create(ref stateMachine);
    public static DurableTaskMethodInvocation<TResult, TStateMachine> Create<TResult, TStateMachine>(scoped ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => DurableTaskMethodInvocation<TResult, TStateMachine>.Create(ref stateMachine);
}

internal abstract class UntypedDurableTaskMethodInvocation : DurableTask, IDurableTaskMethodInvocation
{
    public abstract void SetResult();

    public abstract void SetException(Exception exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask"/> method.
/// </summary>
internal sealed class UntypedDurableTaskMethodInvocation<TStateMachine> : UntypedDurableTaskMethodInvocation, IAsyncStateMachine
    where TStateMachine : IAsyncStateMachine
{
    private DurableTaskExecutionContext? _executionContext;

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
        DurableTaskExecutionContext.SetCurrentContext(_executionContext, out var previousContext);
        try
        {
            _stateMachine.MoveNext();
        }
        finally
        {
            DurableTaskExecutionContext.SetCurrentContext(previousContext);
        }
    }

    void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine.SetStateMachine(stateMachine);

    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) 
    {
        _executionContext = executionContext;
        StartInvocation();
        return executionContext.AsUntypedValueTask();
    }

    public override void SetResult() => _executionContext!.SetResult(null);

    public override void SetException(Exception exception) => _executionContext!.SetException(exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal abstract class DurableTaskMethodInvocation<TResult> : DurableTask<TResult>, IDurableTaskMethodInvocation
{
    public abstract void SetResult(TResult result);

    public abstract void SetException(Exception exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation<TResult, TStateMachine> : DurableTaskMethodInvocation<TResult>, IAsyncStateMachine
    where TStateMachine : IAsyncStateMachine
{
    private DurableTaskExecutionContext? _executionContext;

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
        DurableTaskExecutionContext.SetCurrentContext(_executionContext, out var previousContext);
        try
        {
            _stateMachine.MoveNext();
        }
        finally
        {
            DurableTaskExecutionContext.SetCurrentContext(previousContext);
        }
    }

    void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine.SetStateMachine(stateMachine);

    protected internal override ValueTask<TResult> InvokeAsyncTypedCore(DurableTaskExecutionContext executionContext)
    {
        _executionContext = executionContext;
        StartInvocation();
        return executionContext.AsValueTask<TResult>();
    }

    protected internal override ValueTask InvokeAsyncUntypedCore(DurableTaskExecutionContext executionContext) 
    {
        _executionContext = executionContext;
        StartInvocation();
        return executionContext.AsUntypedValueTask();
    }

    public override void SetResult(TResult result) => _executionContext!.SetResult(result);

    public override void SetException(Exception exception) => _executionContext!.SetException(exception);
}

/// <summary>
/// Support for converting awaiting the completion of a <see cref="DurableTaskMethodInvocation{TResult}"/> instance which has been cast to an untyped <see cref="DurableTask"/> instance.
/// </summary>
internal interface IDurableTaskMethodInvocation
{
}