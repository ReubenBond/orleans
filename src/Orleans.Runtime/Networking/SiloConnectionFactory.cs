using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionFactory : ConnectionFactory
    {
        internal static readonly object ServicesKey = new object();
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly ConnectionCommon connectionShared;
        private readonly ProbeRequestMonitor probeRequestMonitor;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly ConnectionOptions _connectionOptions;
        private readonly IServiceProvider serviceProvider;
        private readonly object initializationLock = new ();
        private bool isInitialized;
        private ConnectionManager connectionManager;
        private MessageCenter messageCenter;
        private ClusterMembershipService clusterMembership;

        public SiloConnectionFactory(
            IServiceProvider serviceProvider,
            IOptions<ConnectionOptions> connectionOptions,
            IOptionsMonitor<TransportFactoryOptions> transportFactoryOptions,
            IEnumerable<IMessageTransportFactoryProvider> transportFactoryProviders,
            ILocalSiloDetails localSiloDetails,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeRequestMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(transportFactoryProviders, transportFactoryOptions)
        {
            _connectionOptions = connectionOptions.Value;
            this.serviceProvider = serviceProvider;
            this.localSiloDetails = localSiloDetails;
            this.connectionShared = connectionShared;
            this.probeRequestMonitor = probeRequestMonitor;
            this.connectionPreambleHelper = connectionPreambleHelper;
        }

        public override async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            EnsureInitialized();

            var hasRefreshed = false;
            while (true)
            {
                var snapshot = clusterMembership.CurrentSnapshot;
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

                    await clusterMembership.Refresh();
                    continue;
                }

                break;
            }

            clusterMembership.CurrentSnapshot.

            return await base.ConnectAsync(address, cancellationToken);
        }

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport)
        {
            EnsureInitialized();

            return new SiloConnection(
                address,
                transport,
                this.messageCenter,
                this.localSiloDetails,
                this.connectionManager,
                _connectionOptions,
                this.connectionShared,
                this.probeRequestMonitor,
                this.connectionPreambleHelper);
        }

        private void EnsureInitialized()
        {
            if (!isInitialized)
            {
                lock (this.initializationLock)
                {
                    if (!isInitialized)
                    {
                        this.messageCenter = this.serviceProvider.GetRequiredService<MessageCenter>();
                        this.connectionManager = this.serviceProvider.GetRequiredService<ConnectionManager>();
                        this.clusterMembership = this.serviceProvider.GetRequiredService<ClusterMembershipService>();
                        this.isInitialized = true;
                    }
                }
            }
        }
    }
}
