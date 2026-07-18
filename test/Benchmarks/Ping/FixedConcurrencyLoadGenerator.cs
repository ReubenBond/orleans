using System.Diagnostics;

namespace Benchmarks.Ping;

public sealed class FixedConcurrencyLoadGenerator<TState>(
    int concurrency,
    Func<TState, ValueTask> issueRequest,
    Func<int, TState> getStateForWorker)
{
    private readonly int _concurrency = concurrency > 0
        ? concurrency
        : throw new ArgumentOutOfRangeException(nameof(concurrency));
    private readonly Func<TState, ValueTask> _issueRequest = issueRequest;
    private readonly Func<int, TState> _getStateForWorker = getStateForWorker;

    public async Task WarmupAsync(TimeSpan duration)
    {
        _ = await RunAsync(duration);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public async Task<FixedConcurrencyLoadResult> RunAsync(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var workers = new Task<long>[_concurrency];
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

        await Task.Delay(duration);
        await cancellation.CancelAsync();
        var completed = (await Task.WhenAll(workers)).Sum();
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        return new(
            completed,
            elapsed,
            GC.GetTotalAllocatedBytes(precise: false) - allocatedBytesBefore,
            GC.CollectionCount(0) - gen0CollectionsBefore,
            GC.CollectionCount(1) - gen1CollectionsBefore,
            GC.CollectionCount(2) - gen2CollectionsBefore);
    }

    private async Task<long> RunWorkerAsync(TState state, Task start, CancellationToken cancellationToken)
    {
        await start.ConfigureAwait(false);

        long completed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await _issueRequest(state).ConfigureAwait(false);
            completed++;
        }

        return completed;
    }
}

public readonly record struct FixedConcurrencyLoadResult(
    long Completed,
    TimeSpan Elapsed,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    public double Throughput => Completed / Elapsed.TotalSeconds;

    public double AllocatedBytesPerOperation => AllocatedBytes / (double)Completed;
}
