using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;

namespace Orleans.Runtime.Messaging
{
    public sealed class IOQueue : PipeScheduler
#if NETCORE
        , IThreadPoolWorkItem
#endif
    {
        private readonly object _workSync = new object();
        private readonly ConcurrentQueue<Work> _workItems = new ConcurrentQueue<Work>();
#if !NETCORE
        private static readonly WaitCallback WaitCallback = ctx => ((IOQueue)ctx).Execute();
#endif

        private bool _doingWork;

        public override void Schedule(Action<object> action, object state)
        {
            var work = new Work(action, state);

            _workItems.Enqueue(work);

            lock (_workSync)
            {
                if (!_doingWork)
                {
#if NETCORE
                    System.Threading.ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
#else
                    System.Threading.ThreadPool.UnsafeQueueUserWorkItem(WaitCallback, this);
#endif
                    _doingWork = true;
                }
            }
        }

#if NETCORE
        void IThreadPoolWorkItem.Execute()
#else
        private void Execute()
#endif
        {
            while (true)
            {
                while (_workItems.TryDequeue(out var item))
                {
                    item.Callback(item.State);
                }

                lock (_workSync)
                {
                    if (_workItems.IsEmpty)
                    {
                        _doingWork = false;
                        return;
                    }
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
