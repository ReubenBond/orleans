using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

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
            : base(connectionFactory)
        {
            this.connectionOptions = connectionOptions.Value;
            this.serviceProvider = serviceProvider;
        }

        protected override ConnectionDelegate GetOutboundConnectionDelegate()
        {
            // Configure the connection builder using the user-defined options.
            var connectionBuilder = new ConnectionBuilder(serviceProvider);
            connectionOptions.ConfigureConnectionBuilder(connectionBuilder);
            connectionBuilder.UseOrleansOutboundClientConnectionHandler();
            return connectionBuilder.Build();
        }
    }
}
