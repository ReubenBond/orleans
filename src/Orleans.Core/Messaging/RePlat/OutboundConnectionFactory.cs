using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class OutboundConnectionFactory
    {
        private readonly IOutboundTransportFactory connectionFactory;
        private readonly Lazy<ConnectionDelegate> connectionDelegate;

        protected OutboundConnectionFactory(IOutboundTransportFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
            this.connectionDelegate = new Lazy<ConnectionDelegate>(() => this.GetOutboundConnectionDelegate(), isThreadSafe: false);
        }

        protected abstract ConnectionDelegate GetOutboundConnectionDelegate();

        public async Task<ConnectionInfo> Create(string endPoint)
        {
            var context = await this.connectionFactory.Connect(endPoint);
            var middlewareTask = this.connectionDelegate.Value(context);

            var connection = new ConnectionInfo(context);
            _ = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        await middlewareTask.ConfigureAwait(false);
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

            return connection;
        }
    }
}
