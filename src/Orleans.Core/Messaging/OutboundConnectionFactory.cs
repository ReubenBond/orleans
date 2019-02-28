using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class OutboundConnectionFactory
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly Lazy<ConnectionDelegate> connectionDelegate;

        protected OutboundConnectionFactory(IConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
            this.connectionDelegate = new Lazy<ConnectionDelegate>(() => this.GetOutboundConnectionDelegate(), isThreadSafe: false);
        }

        protected abstract ConnectionDelegate GetOutboundConnectionDelegate();

        public async Task Connect(
            string endPoint,
            Action<ConnectionContext> configureContext)
        {
            var context = await this.connectionFactory.Connect(endPoint);
            configureContext(context);
            var handlerTask = this.connectionDelegate.Value(context);
            
            _ = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        await handlerTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        // Remove the defunct connection.
                        context.Abort();
                    }
                }
                catch
                {
                    // Ignore all exceptions.
                }
            });
        }
    }
}
