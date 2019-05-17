using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal class OrleansServer : ILifecycleParticipant<ISiloLifecycle>
#if NETCOREAPP30
        , IAsyncDisposable
#endif
    {
        private readonly IConnectionListenerFactory listenerfactory;
        private readonly EndpointOptions endpointOptions;
        private readonly ConnectionDelegate siloConnectionDelegate;
        private readonly ConnectionDelegate gatewayConnectionDelegate;
        private IConnectionListener siloListener;
        private IConnectionListener gatewayListener;

        public OrleansServer(
            IConnectionListenerFactory listenerFactory,
            IOptions<EndpointOptions> endpointOptions,
            IOptions<ConnectionOptions> connectionOptions,
            IServiceProvider serviceProvider)
        {
            this.listenerfactory = listenerFactory;
            this.endpointOptions = endpointOptions.Value;
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
                ?? (this.siloListener = this.listenerfactory.Create(endpointOptions.GetListeningSiloEndpoint().ToString(), this.siloConnectionDelegate));
            return listener.BindAsync();
        }

        private Task StopSiloListener(CancellationToken cancellation)
        {
            var listener = Interlocked.Exchange(ref this.siloListener, null);
            return listener?.UnbindAsync() ?? Task.CompletedTask;
        }

        private Task StartGatewayListener(CancellationToken cancellation)
        {
            var listener = this.gatewayListener
                ?? (this.gatewayListener = this.listenerfactory.Create(endpointOptions.GetListeningProxyEndpoint().ToString(), this.gatewayConnectionDelegate));
            return listener.BindAsync();
        }

        private Task StopGatewayListener(CancellationToken cancellation)
        {
            var listener = Interlocked.Exchange(ref this.gatewayListener, null);
            return listener?.UnbindAsync() ?? Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (this.siloListener is IConnectionListener silo)
            {
                await silo.UnbindAsync();
                await silo.StopAsync();
            }

            if (this.gatewayListener is IConnectionListener gateway)
            {
                await gateway.UnbindAsync();
                await gateway.StopAsync();
            }
        }
    }
}
