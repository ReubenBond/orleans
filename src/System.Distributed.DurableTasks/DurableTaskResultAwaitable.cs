using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.WireProtocol;

namespace System.Distributed.DurableTasks;

public readonly struct DurableTaskResultAwaitable<TResult>(DurableTaskContext executionContext)
{
    private readonly DurableTaskContext _executionContext = executionContext;

    public DurableTaskResultAwaiter<TResult> GetAwaiter() => new(_executionContext.AsValueTask());
}

public readonly struct DurableTaskResultAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<Response> _awaiter;

    internal DurableTaskResultAwaiter(ValueTask<Response> responseTask)
    {
        _awaiter = responseTask.GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}
