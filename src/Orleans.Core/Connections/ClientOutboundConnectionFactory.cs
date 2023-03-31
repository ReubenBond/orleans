#nullable enable
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Connections.Transport;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        public const string DefaultConnectorName = "gw";
        private readonly object _initializationLock = new();
        private readonly IGatewayListProvider _gatewayListProvider;
        private readonly ConnectionCommon _connectionShared;
        private readonly ConnectionOptions _connectionOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper;
        private volatile bool _isInitialized;
        private ClientMessageCenter? _messageCenter;
        private ConnectionManager? _connectionManager;

        public ClientOutboundConnectionFactory(
            IGatewayListProvider gatewayListProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<ClusterOptions> clusterOptions,
            IEnumerable<MessageTransportConnector> connectors,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connectors.Where(static connector => connector.Features.Get<ITransportProtocolFeature>()?.Protocol == TransportProtocol.Gateway))
        {
            _connectionOptions = connectionOptions.Value;
            _gatewayListProvider = gatewayListProvider;
            _connectionShared = connectionShared;
            _clusterOptions = clusterOptions.Value;
            _connectionPreambleHelper = connectionPreambleHelper;
        }

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport)
        {
            EnsureInitialized();

            return new ClientOutboundConnection(
                address,
                transport,
                _messageCenter,
                _connectionManager,
                _connectionShared,
                _connectionOptions,
                _connectionPreambleHelper,
                _clusterOptions);
        }

        protected override async IAsyncEnumerable<EndPointInfo> GetEndpointInfo(SiloAddress siloAddress, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var uri in await _gatewayListProvider.GetGateways())
            {
                var address = uri.ToGatewayAddress();
                if (address is null)
                {
                    continue;
                }

                if (address.Equals(siloAddress))
                {
                    // TODO: Enhance IGatewayProvider to support providing EndPointInfo objects
                    yield return new EndPointInfo()
                    {
                        Name = DefaultConnectorName,
                        ["ep"] = address.Endpoint.ToString(),
                    };
                }
            }
        }

        [MemberNotNull(nameof(_messageCenter), nameof(_connectionManager))]
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                lock (_initializationLock)
                {
                    if (!_isInitialized)
                    {
                        _messageCenter = _connectionShared.ServiceProvider.GetRequiredService<ClientMessageCenter>();
                        _connectionManager = _connectionShared.ServiceProvider.GetRequiredService<ConnectionManager>();
                        _isInitialized = true;
                    }
                }
            }

            Debug.Assert(_messageCenter is not null);
            Debug.Assert(_connectionManager is not null);
        }
    }
}
