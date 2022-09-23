using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayConnectionListener : ConnectionListener, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        public const string DefaultListenerName = "DefaultGatewayListener";
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly MessageCenter messageCenter;
        private readonly ConnectionCommon connectionShared;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly ILogger<GatewayConnectionListener> logger;
        private readonly EndpointOptions endpointOptions;
        private readonly OverloadDetector overloadDetector;
        private readonly Gateway gateway;

        public GatewayConnectionListener(
            string name,
            IOptionsMonitor<TransportListenerOptions> transportListenerOptions,
            IEnumerable<IMessageTransportListenerProvider> listenerProviders,
            IOptions<ConnectionOptions> connectionOptions,
            OverloadDetector overloadDetector,
            ILocalSiloDetails localSiloDetails,
            IOptions<EndpointOptions> endpointOptions,
            MessageCenter messageCenter,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper,
            ILogger<GatewayConnectionListener> logger)
            : base(transportListenerOptions.Get(name), listenerProviders, connectionOptions, connectionManager, connectionShared)
        {
            this.overloadDetector = overloadDetector;
            this.gateway = messageCenter.Gateway;
            this.localSiloDetails = localSiloDetails;
            this.messageCenter = messageCenter;
            this.connectionShared = connectionShared;
            this.connectionPreambleHelper = connectionPreambleHelper;
            this.logger = logger;
            this.endpointOptions = endpointOptions.Value;
        }

        protected override Connection CreateConnection(MessageTransport transport)
        {
            return new GatewayInboundConnection(
                transport,
                this.gateway,
                this.overloadDetector,
                this.localSiloDetails,
                this.ConnectionOptions,
                this.messageCenter,
                this.connectionShared,
                this.connectionPreambleHelper);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (TransportListenerOptions.Endpoint is null) return;

            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.RuntimeInitialize - 1, this);
            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.Active, _ => Task.Run(Start));
        }

        Task ILifecycleObserver.OnStart(CancellationToken ct) => Task.Run(() => BindAsync(ct));
        Task ILifecycleObserver.OnStop(CancellationToken ct) => Task.Run(() => StopAsync(ct));
    }
}
