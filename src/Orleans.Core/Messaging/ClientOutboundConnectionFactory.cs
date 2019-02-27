using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : OutboundConnectionFactory
    {
        private readonly IServiceProvider serviceProvider;
        private ConnectionOptions connectionOptions;

        public ClientOutboundConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IConnectionFactory connectionFactory)
            : base(connectionFactory, serviceProvider)
        {
            this.connectionOptions = connectionOptions.Value;
            this.serviceProvider = serviceProvider;
        }

        protected override ConnectionDelegate GetOutboundConnectionDelegate()
        {
            // Configure the connection builder using the user-defined options.
            var connectionBuilder = new ConnectionBuilder(this.serviceProvider);
            this.connectionOptions.ConfigureConnectionBuilder(connectionBuilder);

            // Track connection lifetime for connectivity events.
            var messageCenter = this.serviceProvider.GetRequiredService<ClientMessageCenter>();
            connectionBuilder.Use(
                next =>
                {
                    return connection =>
                    {
                        messageCenter.OnGatewayConnectionOpen();
                        connection.GetLifetime().ConnectionClosed.Register(
                            () => messageCenter.OnGatewayConnectionClosed(),
                            useSynchronizationContext: false);
                        return next(connection);
                    };
                });

            connectionBuilder.UseOrleansOutboundClientConnectionHandler();
            return connectionBuilder.Build();
        }
    }
}
