#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionFactory : ConnectionFactory
    {
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ConnectionCommon _connectionShared;
        private readonly ProbeRequestMonitor _probeRequestMonitor;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper;
        private readonly ConnectionOptions _connectionOptions;
        private readonly IServiceProvider _serviceProvider;
        private readonly object _initializationLock = new ();
        private bool _isInitialized;
        private ConnectionManager? _connectionManager;
        private MessageCenter? _messageCenter;
        private ClusterMembershipService? _clusterMembership;

        public SiloConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            MessageTransportConnector connector,
            ILocalSiloDetails localSiloDetails,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeRequestMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connector)
        {
            _connectionOptions = connectionOptions.Value;
            _serviceProvider = serviceProvider;
            _localSiloDetails = localSiloDetails;
            _connectionShared = connectionShared;
            _probeRequestMonitor = probeRequestMonitor;
            _connectionPreambleHelper = connectionPreambleHelper;
        }

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport)
        {
            EnsureInitialized();

            return new SiloConnection(
                address,
                transport,
                _messageCenter,
                _localSiloDetails,
                _connectionManager,
                _connectionOptions,
                _connectionShared,
                _probeRequestMonitor,
                _connectionPreambleHelper);
        }

        protected override EndPoint GetEndPoint(SiloAddress address) => address.Endpoint;

        [MemberNotNull(nameof(_messageCenter), nameof(_connectionManager), nameof(_clusterMembership))]
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                lock (_initializationLock)
                {
                    if (!_isInitialized)
                    {
                        _messageCenter = _serviceProvider.GetRequiredService<MessageCenter>();
                        _connectionManager = _serviceProvider.GetRequiredService<ConnectionManager>();
                        _clusterMembership = _serviceProvider.GetRequiredService<ClusterMembershipService>();
                        _isInitialized = true;
                    }
                }
            }

            Debug.Assert(_messageCenter is not null);
            Debug.Assert(_connectionManager is not null);
            Debug.Assert(_clusterMembership is not null);
        }
    }
}
