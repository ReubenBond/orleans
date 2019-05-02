using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Threading;

namespace Orleans.Runtime
{
    internal class ExecutorService
    {
        private readonly IServiceProvider serviceProvider;

        public ExecutorService(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public IExecutor GetExecutor(ThreadPoolExecutorOptions options)
        {
            return SharedThreadPoolExecutor.Instance;
        }
    }

    internal sealed class SharedThreadPoolExecutor : IExecutor
    {
        public static SharedThreadPoolExecutor Instance { get; } = new SharedThreadPoolExecutor();

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(callback, state);
        }
    }
}
