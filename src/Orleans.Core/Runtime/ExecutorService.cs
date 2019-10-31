using System;
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
            return this.serviceProvider.GetService<IExecutor>() ?? ActivatorUtilities.CreateInstance<ThreadPoolExecutor>(this.serviceProvider, options);
        }
    }
}
