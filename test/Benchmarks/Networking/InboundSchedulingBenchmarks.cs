using System.Collections.Concurrent;
using System.Threading;
using BenchmarkDotNet.Attributes;

namespace Benchmarks.Networking;

[MemoryDiagnoser]
public class InboundSchedulingBenchmarks
{
    private readonly CompletionState _completion = new();
    private WorkItem[] _workItems;
    private CoalescingWorkItemQueue<WorkItem> _queue;

    [Params(1, 4, 16, 64)]
    public int BatchCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _workItems = Enumerable.Range(0, BatchCount).Select(_ => new WorkItem(_completion)).ToArray();
        _queue = new(static item => item.Execute(), maxItemsPerTurn: 4);
    }

    [Benchmark(Baseline = true)]
    public void DirectThreadPool()
    {
        _completion.Reset(BatchCount);
        foreach (var item in _workItems)
        {
            ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: true);
        }

        _completion.Wait();
    }

    [Benchmark]
    public void Coalesced()
    {
        _completion.Reset(BatchCount);
        foreach (var item in _workItems)
        {
            _queue.Queue(item);
        }

        _completion.Wait();
        var timeout = Environment.TickCount64 + 10_000;
        while (_queue.IsScheduled && Environment.TickCount64 < timeout)
        {
            Thread.SpinWait(1);
        }

        if (_queue.IsScheduled)
        {
            throw new TimeoutException("The coalescing queue did not return to idle.");
        }
    }

    private sealed class CompletionState
    {
        private readonly ManualResetEventSlim _completed = new();
        private int _remaining;

        public void Reset(int count)
        {
            _remaining = count;
            _completed.Reset();
        }

        public void Complete()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _completed.Set();
            }
        }

        public void Wait() => _completed.Wait();
    }

    private sealed class WorkItem(CompletionState completion) : IThreadPoolWorkItem
    {
        public void Execute() => completion.Complete();
    }

    private sealed class CoalescingWorkItemQueue<T>(Action<T> execute, int maxItemsPerTurn) : IThreadPoolWorkItem
    {
        private readonly ConcurrentQueue<T> _items = new();
        private int _scheduled;

        public bool IsScheduled => Volatile.Read(ref _scheduled) != 0;

        public void Queue(T item)
        {
            _items.Enqueue(item);
            if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
            }
        }

        public void Execute()
        {
            for (var i = 0; i < maxItemsPerTurn && _items.TryDequeue(out var item); i++)
            {
                execute(item);
            }

            if (!_items.IsEmpty)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
                return;
            }

            Volatile.Write(ref _scheduled, 0);
            Thread.MemoryBarrier();
            if (!_items.IsEmpty && Interlocked.Exchange(ref _scheduled, 1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
            }
        }
    }
}
