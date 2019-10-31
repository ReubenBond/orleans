using System;
using System.Threading;

namespace Orleans.Threading
{
    public interface IExecutor
    {
        /// <summary>
        /// Executes the given command at some time in the future. The command
        /// may execute in a new thread, in a pooled thread, or in the calling thread
        /// </summary>
        void QueueWorkItem(Action<object> callback, object state = null);
    }
}