#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Networking.Transport;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionListener
    {
        private readonly ConnectionManager _connectionManager;
        private readonly ConnectionCommon _connectionShared;
        private readonly TransportListenerOptions _listenerOptions;
        private readonly IMessageTransportListenerProvider[] _listenerProviders;
        private readonly ConcurrentDictionary<Connection, object> _connections = new(ReferenceEqualsComparer.Default);
        private Task? _acceptLoopTask;

        protected ConnectionListener(
            IOptions<TransportListenerOptions> listenerOptions,
            IEnumerable<IMessageTransportListenerProvider> listenerProviders,
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared)
        {
            _listenerOptions = listenerOptions.Value;
            _listenerProviders = listenerProviders.ToArray();
            _connectionManager = connectionManager;
            ConnectionOptions = connectionOptions.Value;
            _connectionShared = connectionShared;
        }

        public abstract EndPoint Endpoint { get; }

        protected IServiceProvider ServiceProvider => _connectionShared.ServiceProvider;

        protected NetworkingTrace NetworkingTrace => _connectionShared.NetworkingTrace;

        protected ConnectionOptions ConnectionOptions { get; }

        protected abstract Connection CreateConnection(MessageTransport transport);

        protected virtual void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder) { }

        protected async Task BindAsync(CancellationToken cancellationToken)
        {
            MessageTransportListener? listener = null;
            foreach (var provider in _listenerProviders)
            {
                if (provider.TryGetMessageTransportListener(_listenerOptions, out listener))
                {
                    break;
                }
            }

            if (listener is null)
            {
                throw new OrleansConfigurationException($"None of the configured transport listeners were able to satisfy the demands.");
            }

            listener.LocalEndpoint = Endpoint;
            await listener.BindAsync(cancellationToken);
            this.listener = await this.listenerFactory.BindAsync(Endpoint);
        }

        protected void Start()
        {
            if (this.listener is null) throw new InvalidOperationException("Listener is not bound");
            _acceptLoopTask = RunAcceptLoop();
        }

        private async Task RunAcceptLoop()
        {
            await Task.Yield();
            try
            {
                while (true)
                {
                    var context = await this.listener.AcceptAsync();
                    if (context == null) break;

                    var connection = this.CreateConnection(context);
                    StartConnection(connection);
                }
            }
            catch (Exception exception)
            {
                NetworkingTrace.LogCritical(exception, "Exception in AcceptAsync");
            }
        }

        protected async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await listener.UnbindAsync(cancellationToken);

                if (_acceptLoopTask is not null)
                {
                    await _acceptLoopTask;
                }

                var closeTasks = new List<Task>();
                foreach (var kv in _connections)
                {
                    closeTasks.Add(kv.Key.CloseAsync(exception: null));
                }

                if (closeTasks.Count > 0)
                {
                    await Task.WhenAny(Task.WhenAll(closeTasks), cancellationToken.WhenCancelled());
                }

                await _connectionManager.Closed;
                await this.listener.DisposeAsync();
            }
            catch (Exception exception)
            {
                NetworkingTrace.LogWarning(exception, "Exception during shutdown");
            }
        }

        private void StartConnection(Connection connection)
        {
            _connections.TryAdd(connection, null);

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, connection) = ((ConnectionListener, Connection))state;
                _ = t.RunConnectionAsync(connection);
            }, (this, connection));
        }

        private async Task RunConnectionAsync(Connection connection)
        {
            using (BeginConnectionScope(connection))
            {
                try
                {
                    await connection.RunAsync();
                    NetworkingTrace.LogInformation("Connection {Connection} terminated", connection);
                }
                catch (Exception exception)
                {
                    NetworkingTrace.LogInformation(exception, "Connection {Connection} terminated with an exception", connection);
                }
                finally
                {
                    _connections.TryRemove(connection, out _);
                }
            }
        }

        private IDisposable BeginConnectionScope(Connection connection)
        {
            if (NetworkingTrace.IsEnabled(LogLevel.Critical))
            {
                return NetworkingTrace.BeginScope(new ConnectionLogScope(connection));
            }

            return null;
        }
    }
}