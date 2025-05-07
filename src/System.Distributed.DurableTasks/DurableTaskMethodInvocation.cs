using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Represents a locally-executing <see cref="DurableTask"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation : DurableTask, IAsyncStateMachine, IValueTaskSource<DurableTaskResponse>
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _completion;
    private DurableExecutionContext? _executionContext;
    private IAsyncStateMachine? _stateMachine;

    private void StartInvocation() => MoveNext();
    public void MoveNext()
    {
        Debug.Assert(_stateMachine is not null);

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

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        _executionContext = executionContext;
        StartInvocation();
        return new(this, _completion.Version);
    }

    public void SetResult() => _completion.SetResult(DurableTaskResponse.Completed);
    public void SetException(Exception exception) => _completion.SetResult(DurableTaskResponse.FromException(exception));
    public void SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine = stateMachine;

    public DurableTaskResponse GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation<TResult> : DurableTask<TResult>, IAsyncStateMachine, IValueTaskSource<DurableTaskResponse>
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _completion;
    private DurableExecutionContext? _executionContext;
    private IAsyncStateMachine? _stateMachine;

    private void StartInvocation() => MoveNext();
    public void MoveNext()
    {
        Debug.Assert(_stateMachine is not null);

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

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        _executionContext = executionContext;
        StartInvocation();
        return new(this, _completion.Version);
    }

    public void SetResult(TResult result) => _completion.SetResult(DurableTaskResponse.FromResult(result));
    public void SetException(Exception exception) => _completion.SetException(exception);
    public void SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine = stateMachine;

    public DurableTaskResponse GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}
