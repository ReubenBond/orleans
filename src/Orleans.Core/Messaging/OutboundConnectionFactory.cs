using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal abstract class OutboundConnectionFactory
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly Lazy<ConnectionDelegate> connectionDelegate;
        private readonly IApplicationLifetime applicationLifetime;

        protected OutboundConnectionFactory(IConnectionFactory connectionFactory, IServiceProvider serviceProvider)
        {
            this.connectionFactory = connectionFactory;
            this.connectionDelegate = new Lazy<ConnectionDelegate>(() => this.GetOutboundConnectionDelegate(), isThreadSafe: true);
            this.applicationLifetime = serviceProvider.GetRequiredService<IApplicationLifetime>();
        }

        protected abstract ConnectionDelegate GetOutboundConnectionDelegate();

        public async Task Connect(
            string endPoint,
            Action<ConnectionContext> configureContext)
        {
            var connectionContext = await this.connectionFactory.Connect(endPoint).ConfigureAwait(false);
            var registration = this.applicationLifetime.ApplicationStopped.Register(
                () => connectionContext.Abort(),
                useSynchronizationContext: false);

            try
            {
                configureContext(connectionContext);
                var connectionTask = this.connectionDelegate.Value(connectionContext);
                connectionContext.GetLifetime().ConnectionClosed.Register(
                    () => (connectionContext as IDisposable)?.Dispose(),
                    useSynchronizationContext: false);
                await connectionTask.ConfigureAwait(false);
            }
            finally
            {
                // Remove the defunct connection.
                connectionContext.Abort();
                registration.Dispose();
            }
        }
    }
}
