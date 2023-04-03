#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;
using Orleans.Messaging;

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
            IEnumerable<MessageTransportConnector> connectors,
            ILocalSiloDetails localSiloDetails,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeRequestMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connectors.Where(static connector => connector.Features.Get<ITransportProtocolFeature>()?.Protocol == TransportProtocol.Cluster))
        {
            _connectionOptions = connectionOptions.Value;
            _serviceProvider = serviceProvider;
            _localSiloDetails = localSiloDetails;
            _connectionShared = connectionShared;
            _probeRequestMonitor = probeRequestMonitor;
            _connectionPreambleHelper = connectionPreambleHelper;
        }

        public override async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            EnsureInitialized();

            /*
            var hasRefreshed = false;
            while (true)
            {
                var snapshot = _clusterMembership.CurrentSnapshot;
                var status = snapshot.GetSiloStatus(address);
                if (status.IsTerminating())
                {
                    throw new ConnectionAbortedException($"Denying connection to known-dead silo {address}");
                }

                if (status == SiloStatus.None)
                {
                    if (hasRefreshed)
                    {
                        throw new ConnectionAbortedException($"Unable to connect to unknown silo {address}");
                    }

                    await _clusterMembership.Refresh();
                    continue;
                }

                break;
            }
            */

            return await base.ConnectAsync(address, cancellationToken);
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

        protected override async IAsyncEnumerable<EndpointInfo> GetEndpointInfo(SiloAddress siloAddress, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            await Task.Yield();

            yield return new EndpointInfo(SiloConnectionListener.DefaultListenerName)
            {
                ["ep"] = siloAddress.Endpoint.ToString(),
            };

            /*
            ClusterMember? member;
            var didRefresh = false;
            var membershipSnapshot = _clusterMembership.CurrentSnapshot;
            while (!membershipSnapshot.Members.TryGetValue(siloAddress, out member) && !didRefresh)
            {
                // Allow for one refresh.
                // If silo identity encodes the membership version, then we can do this more intelligently by only refreshing if our current version is below
                // The silo's joining version.
                await _clusterMembership.Refresh();
                membershipSnapshot = _clusterMembership.CurrentSnapshot;
                didRefresh = true;
            }

            var status = membershipSnapshot.GetSiloStatus(siloAddress);
            if (status.IsTerminating())
            {
                throw new ConnectionAbortedException($"Denying connection to known-dead silo {siloAddress}");
            }

            if (member is not null)
            {
                foreach (var info in member.Endpoints)
                {
                    yield return info;
                }
            }
            */
        }

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
