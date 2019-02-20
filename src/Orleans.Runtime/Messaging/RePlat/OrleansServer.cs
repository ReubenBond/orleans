using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Messaging.RePlat;

namespace Orleans.Messaging.RePlat
{
    internal class OrleansServer : ILifecycleParticipant<ISiloLifecycle>
#if NETCOREAPP
        , IAsyncDisposable
#endif
    {
        private readonly IConnectionListenerFactory listenerfactory;
        private readonly EndpointOptions endPointOptions;
        private readonly ConnectionDelegate siloConnectionDelegate;
        private readonly ConnectionDelegate gatewayConnectionDelegate;
        private IConnectionListener siloListener;
        private IConnectionListener gatewayListener;

        public OrleansServer(
            IConnectionListenerFactory listenerFactory,
            IOptions<EndpointOptions> endPointOptions,
            IOptions<ConnectionOptions> connectionOptions,
            IServiceProvider serviceProvider)
        {
            this.listenerfactory = listenerFactory;
            this.endPointOptions = endPointOptions.Value;
            var connectionBuilderOptions = connectionOptions.Value;

            this.siloConnectionDelegate = GetSiloConnectionDelegate();
            this.gatewayConnectionDelegate = GetGatewayConnectionDelegate();

            ConnectionDelegate GetSiloConnectionDelegate()
            {
                var connectionBuilder = new ConnectionBuilder(serviceProvider);
                connectionBuilderOptions.ConfigureConnectionBuilder(connectionBuilder);
                connectionBuilder.UseOrleansSiloConnectionHandler();
                return connectionBuilder.Build();
            }

            ConnectionDelegate GetGatewayConnectionDelegate()
            {
                var connectionBuilder = new ConnectionBuilder(serviceProvider);
                connectionBuilderOptions.ConfigureConnectionBuilder(connectionBuilder);
                connectionBuilder.UseOrleansGatewayConnectionHandler();
                return connectionBuilder.Build();
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            // Start/stop listening for connections at different run levels.
            lifecycle.Subscribe("OrleansServer.Silo", ServiceLifecycleStage.AcceptSiloConnections, this.StartSiloListener, _ => Task.CompletedTask);
            lifecycle.Subscribe("OrleansServer.Silo", ServiceLifecycleStage.RuntimeInitialize, _ => Task.CompletedTask, this.StopSiloListener);

            lifecycle.Subscribe("OrleansServer.Gateway", ServiceLifecycleStage.AcceptGatewayConnections, this.StartGatewayListener, _ => Task.CompletedTask);
            lifecycle.Subscribe("OrleansServer.Gateway", ServiceLifecycleStage.BecomeActive, _ => Task.CompletedTask, this.StopGatewayListener);
        }

        private Task StartSiloListener(CancellationToken cancellation)
        {
            var listener = this.siloListener
                ?? (this.siloListener = this.listenerfactory.Create(endPointOptions.GetListeningSiloEndpoint().ToString(), this.siloConnectionDelegate));
            return listener.Bind();
        }

        private Task StopSiloListener(CancellationToken cancellation)
        {
            var listener = Interlocked.Exchange(ref this.siloListener, null);
            return listener?.Unbind() ?? Task.CompletedTask;
        }

        private Task StartGatewayListener(CancellationToken cancellation)
        {
            var listener = this.gatewayListener
                ?? (this.gatewayListener = this.listenerfactory.Create(endPointOptions.GetListeningProxyEndpoint().ToString(), this.gatewayConnectionDelegate));
            return listener.Bind();
        }

        private Task StopGatewayListener(CancellationToken cancellation)
        {
            var listener = Interlocked.Exchange(ref this.gatewayListener, null);
            return listener?.Unbind() ?? Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (this.siloListener is IConnectionListener silo)
            {
                await silo.Unbind();
                await silo.Stop();
            }

            if (this.gatewayListener is IConnectionListener gateway)
            {
                await gateway.Unbind();
                await gateway.Stop();
            }
        }
    }
}
