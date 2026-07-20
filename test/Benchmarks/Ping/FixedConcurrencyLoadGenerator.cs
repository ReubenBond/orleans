using System.Diagnostics;
using System.Numerics;

namespace Benchmarks.Ping;

public sealed class FixedConcurrencyLoadGenerator<TState>(
    int concurrency,
    Func<TState, ValueTask> issueRequest,
    Func<int, TState> getStateForWorker,
    bool recordLatency = false)
{
    private readonly int _concurrency = concurrency > 0
        ? concurrency
        : throw new ArgumentOutOfRangeException(nameof(concurrency));
    private readonly Func<TState, ValueTask> _issueRequest = issueRequest;
    private readonly Func<int, TState> _getStateForWorker = getStateForWorker;
    private readonly bool _recordLatency = recordLatency;

    public async Task WarmupAsync(TimeSpan duration)
    {
        _ = await RunAsync(duration);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public async Task<FixedConcurrencyLoadResult> RunAsync(TimeSpan duration, Func<Task> concurrentAction = null)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var workers = new Task<WorkerResult>[_concurrency];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = RunWorkerAsync(_getStateForWorker(i), start.Task, cancellation.Token);
        }

        var allocatedBytesBefore = GC.GetTotalAllocatedBytes(precise: false);
        var gen0CollectionsBefore = GC.CollectionCount(0);
        var gen1CollectionsBefore = GC.CollectionCount(1);
        var gen2CollectionsBefore = GC.CollectionCount(2);
        var startTimestamp = Stopwatch.GetTimestamp();
        start.SetResult();

        var delay = Task.Delay(duration);
        Task concurrentTask;
        try
        {
            concurrentTask = concurrentAction?.Invoke() ?? Task.CompletedTask;
        }
        catch
        {
            await cancellation.CancelAsync();
            await Task.WhenAll(workers);
            throw;
        }

        await delay;
        await cancellation.CancelAsync();
        var workerResults = await Task.WhenAll(workers);
        await concurrentTask;
        var completed = workerResults.Sum(static result => result.Completed);
        var latency = new LatencyHistogram();
        foreach (var result in workerResults)
        {
            if (result.Latency is { } workerLatency)
            {
                latency.Merge(workerLatency);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        return new(
            completed,
            elapsed,
            GC.GetTotalAllocatedBytes(precise: false) - allocatedBytesBefore,
            GC.CollectionCount(0) - gen0CollectionsBefore,
            GC.CollectionCount(1) - gen1CollectionsBefore,
            GC.CollectionCount(2) - gen2CollectionsBefore,
            latency);
    }

    private async Task<WorkerResult> RunWorkerAsync(TState state, Task start, CancellationToken cancellationToken)
    {
        await start.ConfigureAwait(false);

        long completed = 0;
        if (!_recordLatency)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _issueRequest(state).ConfigureAwait(false);
                completed++;
            }

            return new(completed, null);
        }

        var latency = new LatencyHistogram();
        while (!cancellationToken.IsCancellationRequested)
        {
            var requestStart = Stopwatch.GetTimestamp();
            await _issueRequest(state).ConfigureAwait(false);
            latency.Record(Stopwatch.GetTimestamp() - requestStart);
            completed++;
        }
        return new(completed, latency);
    }

    private readonly record struct WorkerResult(long Completed, LatencyHistogram Latency);
}

public readonly record struct FixedConcurrencyLoadResult(
    long Completed,
    TimeSpan Elapsed,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    LatencyHistogram Latency)
{
    public double Throughput => Completed / Elapsed.TotalSeconds;

    public double AllocatedBytesPerOperation => AllocatedBytes / (double)Completed;
}

public sealed class LatencyHistogram
{
    private const int LinearBuckets = 32;
    private const int SubBuckets = 16;
    private readonly long[] _counts = new long[LinearBuckets + (64 * SubBuckets)];
    private long _totalTicks;

    public long Count { get; private set; }
    public long MaxTicks { get; private set; }
    public double MeanMicroseconds => Count == 0 ? 0 : TicksToMicroseconds(_totalTicks / (double)Count);
    public double MaxMicroseconds => TicksToMicroseconds(MaxTicks);

    public void Record(long ticks)
    {
        ticks = Math.Max(ticks, 0);
        _counts[GetBucketIndex((ulong)ticks)]++;
        Count++;
        _totalTicks += ticks;
        MaxTicks = Math.Max(MaxTicks, ticks);
    }

    public void Merge(LatencyHistogram other)
    {
        for (var i = 0; i < _counts.Length; i++)
        {
            _counts[i] += other._counts[i];
        }

        Count += other.Count;
        _totalTicks += other._totalTicks;
        MaxTicks = Math.Max(MaxTicks, other.MaxTicks);
    }

    public double GetPercentileMicroseconds(double percentile)
    {
        if (Count == 0)
        {
            return 0;
        }

        var target = Math.Max(1, (long)Math.Ceiling(Count * percentile / 100));
        long cumulative = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            cumulative += _counts[i];
            if (cumulative >= target)
            {
                return TicksToMicroseconds(Math.Min(GetBucketUpperBound(i), (ulong)MaxTicks));
            }
        }

        return MaxMicroseconds;
    }

    private static int GetBucketIndex(ulong ticks)
    {
        if (ticks < LinearBuckets)
        {
            return (int)ticks;
        }

        var exponent = BitOperations.Log2(ticks);
        var shift = exponent - 4;
        var subBucket = (int)((ticks - (1UL << exponent)) >> shift);
        return Math.Min(LinearBuckets + ((exponent - 5) * SubBuckets) + subBucket, LinearBuckets + (64 * SubBuckets) - 1);
    }

    private static ulong GetBucketUpperBound(int index)
    {
        if (index < LinearBuckets)
        {
            return (ulong)index;
        }

        var offset = index - LinearBuckets;
        var exponent = 5 + (offset / SubBuckets);
        var subBucket = offset % SubBuckets;
        var shift = exponent - 4;
        return (1UL << exponent) + ((ulong)(subBucket + 1) << shift) - 1;
    }

    private static double TicksToMicroseconds(double ticks) => ticks * 1_000_000 / Stopwatch.Frequency;
}
