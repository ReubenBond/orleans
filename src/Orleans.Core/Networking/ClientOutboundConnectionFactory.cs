using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Networking.Transport;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        internal static readonly object ServicesKey = new object();
        private readonly ConnectionCommon connectionShared;
        private readonly ConnectionOptions connectionOptions;
        private readonly ClusterOptions clusterOptions;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly object initializationLock = new object();
        private volatile bool isInitialized;
        private ClientMessageCenter messageCenter;
        private ConnectionManager connectionManager;

        public ClientOutboundConnectionFactory(
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<ClusterOptions> clusterOptions,
            EndpointConfigurationProvider endpointConfigurationProvider,
            IEnumerable<IMessageTransportFactoryProvider> transportFactoryProviders,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper,
            IOptionsMonitor<TransportFactoryOptions> transportFactoryOptions)
            : base(endpointConfigurationProvider, transportFactoryProviders, transportFactoryOptions)
        {
            this.connectionOptions = connectionOptions.Value;
            this.connectionShared = connectionShared;
            this.clusterOptions = clusterOptions.Value;
            this.connectionPreambleHelper = connectionPreambleHelper;
        }

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport)
        {
            EnsureInitialized();

            return new ClientOutboundConnection(
                address,
                transport,
                this.messageCenter,
                this.connectionManager,
                this.connectionShared,
                this.connectionOptions,
                this.connectionPreambleHelper,
                this.clusterOptions);
        }

        protected override bool TryGetTransportFactory(EndpointInfo endpointInfo, out MessageTransportFactory transportFactory)
        {
            var gatewayEndpointOptions = new GatewayEndpointOptions();
            endpointInfo.Configuration.Bind(gatewayEndpointOptions);

            if (!gatewayEndpointOptions.IsProxy)
            {
                transportFactory = default;
                return false;
            }

            return base.TryGetTransportFactory(endpointInfo, out transportFactory);
        }

        private void EnsureInitialized()
        {
            if (!isInitialized)
            {
                lock (this.initializationLock)
                {
                    if (!isInitialized)
                    {
                        this.messageCenter = this.connectionShared.ServiceProvider.GetRequiredService<ClientMessageCenter>();
                        this.connectionManager = this.connectionShared.ServiceProvider.GetRequiredService<ConnectionManager>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}
