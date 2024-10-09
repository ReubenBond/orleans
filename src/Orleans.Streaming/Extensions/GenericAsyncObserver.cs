using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Class used by the IAsyncObservable extension methods to allow observation via delegate.
    /// </summary>
    /// <typeparam name="T">The type of object produced by the observable.</typeparam>
    internal class GenericAsyncObserver<T> : IAsyncObserver<T>
    {
        private readonly Func<T, StreamSequenceToken, Task> onNextAsync;
        private readonly Func<Exception, Task> onErrorAsync;
        private readonly Func<Task> onCompletedAsync;

        public GenericAsyncObserver(Func<T, StreamSequenceToken, Task> onNextAsync, Func<Exception, Task> onErrorAsync, Func<Task> onCompletedAsync)
        {
            ArgumentNullException.ThrowIfNull(onNextAsync);
            ArgumentNullException.ThrowIfNull(onErrorAsync);
            ArgumentNullException.ThrowIfNull(onCompletedAsync);

            this.onNextAsync = onNextAsync;
            this.onErrorAsync = onErrorAsync;
            this.onCompletedAsync = onCompletedAsync;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            return onNextAsync(item, token);
        }

        public Task OnCompletedAsync()
        {
            return onCompletedAsync();
        }

        public Task OnErrorAsync(Exception ex)
        {
            return onErrorAsync(ex);
        }
    }
}
