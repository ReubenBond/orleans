using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloOutboundConnectionFactory : OutboundConnectionFactory
    {
        private readonly IServiceProvider serviceProvider;
        private ConnectionOptions connectionOptions;

        public SiloOutboundConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IConnectionFactory connectionFactory,
            ILogger<SiloOutboundConnectionFactory> log)
            : base(connectionFactory, log, serviceProvider)
        {
            this.connectionOptions = connectionOptions.Value;
            this.serviceProvider = serviceProvider;
        }

        protected override ConnectionDelegate GetOutboundConnectionDelegate()
        {
            // Configure the connection builder using the user-defined options.
            var connectionBuilder = new ConnectionBuilder(this.serviceProvider);
            this.connectionOptions.ConfigureConnectionBuilder(connectionBuilder);
            connectionBuilder.UseOrleansOutboundSiloConnectionHandler();
            return connectionBuilder.Build();
        }
    }
}
