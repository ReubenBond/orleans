using System;
using System.Collections.Generic;
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

        public async Task Connect(
            string endPoint,
            Dictionary<object, object> additionalItems)
        {
            var context = await this.connectionFactory.Connect(endPoint);

            // Add the additional items before passing the contex to the handler.
            if (additionalItems != null)
            {
                foreach (var item in additionalItems)
                {
                    context.Items.Add(item.Key, item.Value);
                }
            }

            var handlerTask = this.connectionDelegate.Value(context);

            var connection = new ConnectionInfo(context);

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
