using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Networking.Shared
{
    internal sealed class IOQueue : PipeScheduler
    {
        private readonly Task _runTask;
        private readonly ConcurrentQueue<Work> _workItems = new();
        private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };

        public IOQueue()
        {
            _runTask = Task.Run(Run);
        }

        public override void Schedule(Action<object> action, object state)
        {
            _workItems.Enqueue(new Work(action, state));
            _workSignal.Signal();
        }

        private async Task Run()
        {
            while (true)
            {
                await _workSignal.WaitAsync();

                while (_workItems.TryDequeue(out var item))
                {
                    item.Callback(item.State);
                }
            }
        }

        private readonly struct Work
        {
            public readonly Action<object> Callback;
            public readonly object State;

            public Work(Action<object> callback, object state)
            {
                Callback = callback;
                State = state;
            }
        }
    }
}
