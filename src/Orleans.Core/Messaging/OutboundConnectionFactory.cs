using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal abstract class OutboundConnectionFactory
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly ILogger log;
        private readonly Lazy<ConnectionDelegate> connectionDelegate;
        private readonly IApplicationLifetime applicationLifetime;

        protected OutboundConnectionFactory(IConnectionFactory connectionFactory, ILogger log, IServiceProvider serviceProvider)
        {
            this.connectionFactory = connectionFactory;
            this.log = log;
            this.connectionDelegate = new Lazy<ConnectionDelegate>(() => this.GetOutboundConnectionDelegate(), isThreadSafe: true);
            this.applicationLifetime = serviceProvider.GetRequiredService<IApplicationLifetime>();
        }

        protected abstract ConnectionDelegate GetOutboundConnectionDelegate();

        public async Task Connect(
            string endPoint,
            Action<ConnectionContext> configureContext)
        {
            ConnectionContext connectionContext = default;
            CancellationTokenRegistration appLifetimeRegistration = default;

            try
            {
                this.log.LogInformation("Connecting to endpoint {EndPoint}", endPoint);

                // Initiate the connection
                connectionContext = await this.connectionFactory.Connect(endPoint).ConfigureAwait(false);
                appLifetimeRegistration = this.applicationLifetime.ApplicationStopped.Register(
                    () => connectionContext.Abort(),
                    useSynchronizationContext: false);

                // Configure and handle the connection
                configureContext(connectionContext);
                var connectionTask = this.connectionDelegate.Value(connectionContext);
                connectionContext.GetLifetime().ConnectionClosed.Register(
                    () => (connectionContext as IDisposable)?.Dispose(),
                    useSynchronizationContext: false);

                // Wait for the connection to complete
                await connectionTask.ConfigureAwait(false);
            }
            finally
            {
                this.log.LogInformation("Connection to endpoint {EndPoint} terminated", endPoint);

                // Clean up the defunct connection
                connectionContext?.Abort();
                appLifetimeRegistration.Dispose();
            }
        }
    }
}
