using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Networking.Transport;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        public const string DefaultListenerName = "DefaultSiloListener";
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly MessageCenter messageCenter;
        private readonly EndpointOptions endpointOptions;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionCommon connectionShared;
        private readonly ProbeRequestMonitor probeRequestMonitor;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;

        public SiloConnectionListener(
            string name,
            IOptionsMonitor<TransportListenerOptions> transportListenerOptions,
            IEnumerable<IMessageTransportListenerProvider> listenerProviders,
            IOptions<ConnectionOptions> connectionOptions,
            MessageCenter messageCenter,
            IOptions<EndpointOptions> endpointOptions,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeRequestMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(transportListenerOptions.Get(name), listenerProviders, connectionOptions, connectionManager, connectionShared)
        {
            this.messageCenter = messageCenter;
            this.localSiloDetails = localSiloDetails;
            this.connectionManager = connectionManager;
            this.connectionShared = connectionShared;
            this.probeRequestMonitor = probeRequestMonitor;
            this.connectionPreambleHelper = connectionPreambleHelper;
            this.endpointOptions = endpointOptions.Value;
        }

        protected override Connection CreateConnection(MessageTransport transport)
        {
            return new SiloConnection(
                default(SiloAddress),
                transport,
                this.messageCenter,
                this.localSiloDetails,
                this.connectionManager,
                this.ConnectionOptions,
                this.connectionShared,
                this.probeRequestMonitor,
                this.connectionPreambleHelper);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (TransportListenerOptions.Endpoint is null) return;

            lifecycle.Subscribe(nameof(SiloConnectionListener), ServiceLifecycleStage.RuntimeInitialize - 1, this);
        }

        Task ILifecycleObserver.OnStart(CancellationToken ct) => Task.Run(async () =>
        {
            await BindAsync(ct);

            // Start accepting connections immediately.
            Start();
        });

        Task ILifecycleObserver.OnStop(CancellationToken ct) => Task.Run(() => StopAsync(ct));
    }
}
