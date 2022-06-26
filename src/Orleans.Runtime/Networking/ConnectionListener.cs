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
using Orleans.Connections.Transport;
using Orleans.Connections;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionListener
    {
        private readonly ConnectionManager _connectionManager;
        private readonly ConnectionCommon _connectionShared;
        private readonly IMessageTransportListenerProvider[] _listenerProviders;
        private readonly ConcurrentDictionary<Connection, object?> _connections = new(ReferenceEqualsComparer.Default);
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private MessageTransportListener? _listener;
        private Task? _acceptLoopTask;

        protected ConnectionListener(
            TransportListenerOptions listenerOptions,
            IEnumerable<IMessageTransportListenerProvider> listenerProviders,
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared)
        {
            TransportListenerOptions = listenerOptions;
            _listenerProviders = listenerProviders.Reverse().ToArray();
            _connectionManager = connectionManager;
            ConnectionOptions = connectionOptions.Value;
            _connectionShared = connectionShared;
        }

        protected TransportListenerOptions TransportListenerOptions { get; }

        protected IServiceProvider ServiceProvider => _connectionShared.ServiceProvider;

        protected ConnectionTrace TransportTrace => _connectionShared.ConnectionTrace;

        protected ConnectionOptions ConnectionOptions { get; }

        protected abstract Connection CreateConnection(MessageTransport transport);

        protected async Task BindAsync(CancellationToken cancellationToken)
        {
            foreach (var provider in _listenerProviders)
            {
                if (provider.TryGetMessageTransportListener(TransportListenerOptions, out _listener))
                {
                    break;
                }
            }

            if (_listener is null)
            {
                throw new OrleansConfigurationException($"None of the configured transport listeners were able to satisfy the demands.");
            }

            await _listener.BindAsync(cancellationToken);
        }

        protected void Start()
        {
            if (_listener is null) throw new InvalidOperationException($"Listener is not bound, call {nameof(BindAsync)} first");
            _acceptLoopTask = RunAcceptLoop();
        }

        private async Task RunAcceptLoop()
        {
            await Task.Yield();
            var listener = _listener!;
            try
            {
                while (true)
                {
                    var context = await listener.AcceptAsync(_shutdownCancellation.Token);
                    if (context == null) break;

                    var connection = CreateConnection(context);
                    StartConnection(connection);
                }
            }
            catch (Exception exception)
            {
                TransportTrace.LogCritical(exception, "Exception in AcceptAsync");
            }
        }

        protected async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_listener is null)
                {
                    return;
                }

                _shutdownCancellation.Cancel();
                await _listener.UnbindAsync(cancellationToken);

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
                await _listener.DisposeAsync();
            }
            catch (Exception exception)
            {
                TransportTrace.LogWarning(exception, "Exception during shutdown");
            }
        }

        private void StartConnection(Connection connection)
        {
            _connections.TryAdd(connection, null);

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, connection) = ((ConnectionListener, Connection))state!;
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
                    TransportTrace.LogInformation("Connection {Connection} terminated", connection);
                }
                catch (Exception exception)
                {
                    TransportTrace.LogInformation(exception, "Connection {Connection} terminated with an exception", connection);
                }
                finally
                {
                    _connections.TryRemove(connection, out _);
                }
            }
        }

        private IDisposable? BeginConnectionScope(Connection connection)
        {
            if (TransportTrace.IsEnabled(LogLevel.Critical))
            {
                return TransportTrace.BeginScope(new ConnectionLogScope(connection));
            }

            return null;
        }
    }
}