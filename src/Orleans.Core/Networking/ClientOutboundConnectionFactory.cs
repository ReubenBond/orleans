#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Connections.Transport;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Net;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory : ConnectionFactory
    {
        private readonly object _initializationLock = new();
        private readonly ConnectionCommon _connectionShared;
        private readonly ConnectionOptions _connectionOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper;
        private volatile bool _isInitialized;
        private ClientMessageCenter? _messageCenter;
        private ConnectionManager? _connectionManager;

        public ClientOutboundConnectionFactory(
            IOptions<ConnectionOptions> connectionOptions,
            IOptions<ClusterOptions> clusterOptions,
            MessageTransportConnector connector,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connector)
        {
            _connectionOptions = connectionOptions.Value;
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

        protected override EndPoint GetEndPoint(SiloAddress address) => address.Endpoint;

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
