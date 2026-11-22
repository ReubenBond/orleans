using Orleans.Durable.Storage.RocksDb;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Bounded dedicated worker threads (<see cref="DedicatedThreads"/>) used by <see cref="RocksDbJournalIndex"/>
/// for blocking C++ RocksDB calls off the thread pool.
/// </summary>
public sealed class DedicatedThreadsTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>Work runs on the pool's named non-thread-pool worker threads and propagates return values and exceptions.</summary>
    [Fact]
    public async Task RunsWorkOnNamedDedicatedThreadsAndPropagatesResultsAndExceptions()
    {
        using var pool = new DedicatedThreads("test-index-worker", 2, ThreadPriority.Normal);

        var (isThreadPool, name) = await pool.RunAsync(
            static () => (Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name));
        Assert.False(isThreadPool);
        Assert.Equal("test-index-worker", name);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.RunAsync<int>(static () => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", error.Message);
    }

    /// <summary>A single-thread pool executes queued work items in submission order.</summary>
    [Fact]
    public async Task SingleThreadPoolExecutesWorkInSubmissionOrder()
    {
        using var pool = new DedicatedThreads("test-index-fifo", 1, ThreadPriority.Normal);
        var observed = new List<int>();
        var tasks = new Task<int>[8];
        for (var i = 0; i < tasks.Length; i++)
        {
            var value = i;
            tasks[i] = pool.RunAsync(() =>
            {
                observed.Add(value);
                return value;
            });
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], results);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], observed);
    }
}
