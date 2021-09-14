using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Orleans.Internal;

namespace Orleans.Runtime
{
    internal abstract class WorkerPool<T>
    {
        private readonly ConcurrentQueue<T> _workItems = new();
        private readonly SingleWaiterAutoResetEvent[] _waiters;
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private readonly Task[] _workerTasks;
        private int _nextWaiter;

        protected WorkerPool(int numWorkers = 0)
        {
            if (numWorkers == 0)
            {
                numWorkers = Environment.ProcessorCount;
            }

            _waiters = new SingleWaiterAutoResetEvent[numWorkers];
            _workerTasks = new Task[numWorkers];
        }

        public void Start()
        {
            // Don't capture the current ExecutionContext
            var restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                for (var i = 0; i < _waiters.Length; i++)
                {
                    var waitHandle = new SingleWaiterAutoResetEvent { RunContinuationsAsynchronously = true };
                    _waiters[i] = waitHandle;
                    _workerTasks[i] = RunAsync(waitHandle, _shutdownCancellation.Token);
                }
            }
            finally
            {
                // Restore the current ExecutionContext
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }

        public void Enqueue(T workItem)
        {
            _workItems.Enqueue(workItem);
            var waiter = _nextWaiter = (_nextWaiter + 1) % _waiters.Length;
            _waiters[waiter].Signal();
        }

        protected bool TryDequeue(out T workItem) => _workItems.TryDequeue(out workItem);

        protected abstract Task RunAsync(SingleWaiterAutoResetEvent workSignal, CancellationToken shutdownToken);

        public async Task StopAsync(CancellationToken cancellation)
        {
            _shutdownCancellation.Cancel();
            foreach (var waiter in _waiters)
            {
                waiter.Signal();
            }

            var cancellationTask = cancellation.WhenCancelled();
            await Task.WhenAny(Task.WhenAll(_workerTasks), cancellationTask);
        }
    }
}
