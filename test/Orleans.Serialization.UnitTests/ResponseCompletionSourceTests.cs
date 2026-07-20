#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;
using Xunit;
#if ORLEANS_PROFILING
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Orleans.Serialization.Diagnostics;
#endif

#pragma warning disable xUnit1031 // These tests manually complete ValueTaskSource awaiters and must call GetResult.

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public class ResponseCompletionSourceTests
{
#if ORLEANS_PROFILING
    [Fact]
    public async Task TracedCompletionEmitsSignalAndContinuationAndClearsContext()
    {
        using var listener = new RpcEventListener();
        var source = ResponseCompletionSourcePool.Get<int>();
        source.SetRpcTraceContext(new RpcCallTraceContext(
            1, 2, 3, 11111, 1, 11111, 1, 2, 2, 0, 0));
        var awaiter = source.AsValueTask().GetAwaiter();
        var continuation = RegisterContinuation(awaiter);

        using var response = Response.FromResult(42);
        source.Complete(response);
        await WaitForContinuation(continuation.Task);
        Assert.Equal(42, awaiter.GetResult());

        Assert.Contains((byte)RpcCallPhase.CompletionSignaled, listener.Phases);
        Assert.Contains((byte)RpcCallPhase.ContinuationStart, listener.Phases);
        var phaseCount = listener.Phases.Count;

        var reused = ResponseCompletionSourcePool.Get<int>();
        var reusedAwaiter = reused.AsValueTask().GetAwaiter();
        var reusedContinuation = RegisterContinuation(reusedAwaiter);
        using var reusedResponse = Response.FromResult(43);
        reused.Complete(reusedResponse);
        await WaitForContinuation(reusedContinuation.Task);
        Assert.Equal(43, reusedAwaiter.GetResult());
        Assert.Equal(phaseCount, listener.Phases.Count);
    }
#endif

    [Fact]
    public async Task TypedCompletionRunsContinuationsAsynchronously()
    {
        var source = ResponseCompletionSourcePool.Get<int>();
        var awaiter = source.AsValueTask().GetAwaiter();
        var continuation = RegisterContinuation(awaiter);

        using var response = Response.FromResult(42);
        continuation.CompletionThreadId = Thread.CurrentThread.ManagedThreadId;
        source.Complete(response);

        Assert.NotEqual(continuation.CompletionThreadId, Volatile.Read(ref continuation.ContinuationThreadId));

        await WaitForContinuation(continuation.Task);
        Assert.Equal(42, awaiter.GetResult());
    }

    [Fact]
    public async Task UntypedCompletionRunsContinuationsAsynchronously()
    {
        var source = ResponseCompletionSourcePool.Get();
        var awaiter = source.AsValueTask().GetAwaiter();
        var continuation = RegisterContinuation(awaiter);
        var response = Response.FromResult(42);
        Response? result = null;

        try
        {
            continuation.CompletionThreadId = Thread.CurrentThread.ManagedThreadId;
            source.Complete(response);

            Assert.NotEqual(continuation.CompletionThreadId, Volatile.Read(ref continuation.ContinuationThreadId));

            await WaitForContinuation(continuation.Task);
            result = awaiter.GetResult();
            Assert.Same(response, result);
            Assert.Equal(42, result.GetResult<int>());
        }
        finally
        {
            (result ?? response).Dispose();
        }
    }

    private static ContinuationProbe RegisterContinuation<T>(ValueTaskAwaiter<T> awaiter)
    {
        var continuation = new ContinuationProbe();

        awaiter.OnCompleted(() =>
        {
            Volatile.Write(ref continuation.ContinuationThreadId, Thread.CurrentThread.ManagedThreadId);
            continuation.SetResult();
        });

        return continuation;
    }

    private static async Task WaitForContinuation(Task continuation)
    {
        var completed = await Task.WhenAny(continuation, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(continuation, completed);
        await continuation;
    }

    private sealed class ContinuationProbe
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompletionThreadId = -1;
        public int ContinuationThreadId = -1;
        public Task Task => _completion.Task;

        public void SetResult() => _completion.SetResult(true);
    }

#if ORLEANS_PROFILING
    private sealed class RpcEventListener : EventListener
    {
        public ConcurrentQueue<byte> Phases { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Orleans-RpcLatency")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "Phase" && eventData.Payload is { Count: > 8 })
            {
                Phases.Enqueue(Convert.ToByte(eventData.Payload[8]));
            }
        }
    }
#endif
}
