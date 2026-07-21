using System.Runtime.CompilerServices;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Orleans.Serialization.Invocation;

namespace Benchmarks.Rpc;

[MemoryDiagnoser]
public class ResponseCompletionBenchmarks
{
    private readonly ManualResetEventSlim _completed = new();
    private readonly Action _continuation;
    private ValueTaskAwaiter<int> _awaiter;
    private int _result;

    public ResponseCompletionBenchmarks()
    {
        _continuation = ConsumeResult;
    }

    [Params(0, 64)]
    public int ConsumerSpinWait { get; set; }

    [Benchmark]
    public int CompleteAndConsume()
    {
        _completed.Reset();
        var source = ResponseCompletionSourcePool.Get<int>();
        _awaiter = source.AsValueTask().GetAwaiter();
        _awaiter.UnsafeOnCompleted(_continuation);
        source.SetResult(42);
        _completed.Wait();
        return _result;
    }

    private void ConsumeResult()
    {
        if (ConsumerSpinWait > 0)
        {
            Thread.SpinWait(ConsumerSpinWait);
        }

        _result = _awaiter.GetResult();
        _completed.Set();
    }
}
