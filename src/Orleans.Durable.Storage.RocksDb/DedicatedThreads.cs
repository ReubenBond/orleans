using System.Collections.Concurrent;

namespace Orleans.Durable.Storage.RocksDb;

/// <summary>
/// Dedicated threads which run blocking work items in order of submission and complete their tasks.
/// </summary>
/// <remarks>
/// <para>
/// journals.md §8.3: RocksDB blocks on file I/O, so only dedicated threads call it, and callers await completions.
/// </para>
/// <para>
/// The threads may serve every caller in the process, so they start without capturing their creator's execution
/// context, and each work item runs in the context of the caller which submitted it, as the thread pool runs its work
/// (conventions §12).
/// </para>
/// </remarks>
internal sealed class DedicatedThreads : IDisposable
{
    // Each entry runs its work item, of whatever result type, through a static callback the compiler caches per type, so
    // queueing allocates no delegate.
    private readonly BlockingCollection<(Action<object> Execute, object Item)> _queue = [];
    private readonly Thread[] _threads;

    public DedicatedThreads(string name, int count, ThreadPriority priority)
    {
        _threads = new Thread[count];
        for (var index = 0; index < count; index++)
        {
            _threads[index] = new Thread(Run) { IsBackground = true, Name = name, Priority = priority };
            _threads[index].UnsafeStart();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on one of the threads.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var item = new WorkItem<T>(work);
        _queue.Add((static queued => ((WorkItem<T>)queued).Execute(), item));
        return item.Task;
    }

    /// <summary>
    /// Stops the threads once they finish the work already submitted.
    /// </summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var thread in _threads)
        {
            if (thread != Thread.CurrentThread)
            {
                thread.Join();
            }
        }

        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var (execute, item) in _queue.GetConsumingEnumerable())
        {
            execute(item);
        }
    }

    private sealed class WorkItem<T>(Func<T> work)
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ExecutionContext? _context = ExecutionContext.Capture();

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (_context is null)
            {
                Invoke();
            }
            else
            {
                ExecutionContext.Run(_context, static item => ((WorkItem<T>)item!).Invoke(), this);
            }
        }

        private void Invoke()
        {
            try
            {
                _ = _completion.TrySetResult(work());
            }
            catch (Exception exception)
            {
                _ = _completion.TrySetException(exception);
            }
        }
    }
}
