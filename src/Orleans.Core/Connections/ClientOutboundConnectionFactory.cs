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
using Orleans.Connections.Transport.Sockets;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        public const string DefaultConnectorName = "gw";
        private readonly object _initializationLock = new();
        private readonly IGatewayMembershipService _gatewayMembershipService;
        private readonly ConnectionCommon _connectionShared;
        private readonly ConnectionOptions _connectionOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper;
        private volatile bool _isInitialized;
        private ClientMessageCenter? _messageCenter;
        private ConnectionManager? _connectionManager;

        public ClientOutboundConnectionFactory(
            IGatewayMembershipService gatewayMembershipService,
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<ClusterOptions> clusterOptions,
            IEnumerable<MessageTransportConnector> connectors,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connectors.Where(static connector => connector.Features.Get<ITransportProtocolFeature>()?.Protocol == TransportProtocol.Gateway))
        {
            _connectionOptions = connectionOptions.Value;
            _gatewayMembershipService = gatewayMembershipService;
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

        protected override async IAsyncEnumerable<EndpointInfo> GetEndpointInfo(SiloAddress siloAddress, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            // Handle the development clustering scenario, where an endpoint IP address is hard-coded with a generation of 0.
            // This could be made into an abstraction to support non-TCP/IP cases.
            if (siloAddress.Generation == 0)
            {
                yield return new EndpointInfo(DefaultConnectorName)
                {
                    [TcpMessageTransportConnector.EndpointAddressPropertyName] = siloAddress.Endpoint.ToString(),
                };

                yield break;
            }

            GatewayMember? member;
            var didRefresh = false;
            var membershipSnapshot = _gatewayMembershipService.CurrentSnapshot;
            while (!membershipSnapshot.Gateways.TryGetValue(siloAddress, out member) && !didRefresh)
            {
                // Allow for one refresh in the event that the silo is not found in the current membership table.
                // If silo identity encoded the membership version, then we could do this more intelligently by only refreshing if our current version is below
                // the silo's joining version.
                await _gatewayMembershipService.Refresh(membershipSnapshot.Version.Successor());
                membershipSnapshot = _gatewayMembershipService.CurrentSnapshot;
                didRefresh = true;
            }

            if (member is not null)
            {
                foreach (var info in member.Endpoints)
                {
                    yield return info;
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
