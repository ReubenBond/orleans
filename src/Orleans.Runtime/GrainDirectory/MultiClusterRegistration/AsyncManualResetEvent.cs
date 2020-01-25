using System.Threading.Tasks;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// An async-compatible manual-reset event.
    /// </summary>
    /// <see href="https://github.com/StephenCleary/AsyncEx/blob/5ede2ebad24bb3696fd730de2d7e11cda92bf8dc/src/Nito.AsyncEx.Coordination/AsyncManualResetEvent.cs"/>
    internal sealed class AsyncManualResetEvent
    {
        private readonly object lockObj = new object();
        private TaskCompletionSource<object> completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Creates an async-compatible manual-reset event.
        /// </summary>
        /// <param name="set">Whether the manual-reset event is initially set or unset.</param>
        public AsyncManualResetEvent(bool set)
        {
            if (set)
            {
                completion.TrySetResult(null);
            }
        }

        /// <summary>
        /// Asynchronously waits for this event to be set.
        /// </summary>
        public Task WaitAsync()
        {
            lock (lockObj)
            {
                return completion.Task;
            }
        }

        /// <summary>
        /// Sets the event, atomically completing every task returned by <see cref="O:Nito.AsyncEx.AsyncManualResetEvent.WaitAsync"/>. If the event is already set, this method does nothing.
        /// </summary>
        public void Set()
        {
            lock (lockObj)
            {
                completion.TrySetResult(null);
            }
        }

        /// <summary>
        /// Resets the event. If the event is already reset, this method does nothing.
        /// </summary>
        public void Reset()
        {
            lock (lockObj)
            {
                if (completion.Task.IsCompleted)
                {
                    completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
    }
}