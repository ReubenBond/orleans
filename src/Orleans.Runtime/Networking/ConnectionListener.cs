#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Connections.Transport;
using Orleans.Connections;
using Orleans.Runtime.Internal;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections.Transport.Sockets;
using Orleans.Connections.Transport.Security;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Messaging;
using System.Net;
using System.Collections.Immutable;
using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    public interface ISiloTransportCollection
    {
        IListenerBuilder AddListener(string endpointName);
        IConnectorBuilder AddConnector(string endpointName);
    }
}

namespace Orleans.Runtime.Messaging
{
    internal class SiloTransportCollection : ISiloTransportCollection
    {
        private readonly IServiceCollection _services;
        public SiloTransportCollection(IServiceCollection services) => _services = services;

        public IListenerBuilder AddListener(string endpointName)
        {
            var result = new ListenerBuilder(endpointName, _services);
            result.AddMiddleware(listener => listener.Features.Set<IEndpointNameFeature>(new EndpointNameFeature(endpointName)));
            return result;
        }

        public IConnectorBuilder AddConnector(string endpointName)
        {
            var result = new ConnectorBuilder(endpointName, _services);
            result.AddMiddleware(listener => listener.Features.Set<IEndpointNameFeature>(new EndpointNameFeature(endpointName)));
            return result;
        }
    }

    public static class SiloTransportConnectionExtensions
    {
        public static IListenerBuilder AddMiddleware(this IListenerBuilder builder, IMessageTransportListenerMiddleware middleware)
        {
            builder.Services.AddSingletonNamedService(builder.EndpointName, middleware);
            return builder;
        }

    public static IListenerBuilder AddMiddleware(this IListenerBuilder builder, Action<MessageTransportListener> middleware) => builder.AddMiddleware(new ActionMessageTransportListenerMiddleware(middleware));

    public static IListenerBuilder AddMiddleware(this IListenerBuilder builder, Func<MessageTransportListener, MessageTransportListener> middleware) => builder.AddMiddleware(new FuncMessageTransportListenerMiddleware(middleware));

        public static IListenerBuilder AddDefaultGatewayListener(this ISiloTransportCollection builder)
        {
            return builder
                .AddListener(GatewayConnectionListener.DefaultListenerName)
                .SetProtocol(TransportProtocol.Gateway);
        }

        public static IListenerBuilder AddDefaultSiloListener(this ISiloTransportCollection builder)
        {
            return builder
                .AddListener(SiloConnectionListener.DefaultListenerName)
                .SetProtocol(TransportProtocol.Cluster);
        }

        private sealed class FuncMessageTransportListenerMiddleware : IMessageTransportListenerMiddleware
        {
            private readonly Func<MessageTransportListener, MessageTransportListener> _middleware;

            public FuncMessageTransportListenerMiddleware(Func<MessageTransportListener, MessageTransportListener> middleware)
            {
                _middleware = middleware;
            }

            public MessageTransportListener Apply(MessageTransportListener listener) => _middleware(listener);
        }

        private sealed class ActionMessageTransportListenerMiddleware : IMessageTransportListenerMiddleware
        {
            private readonly Action<MessageTransportListener> _middleware;

            public ActionMessageTransportListenerMiddleware(Action<MessageTransportListener> middleware)
            {
                _middleware = middleware;
            }

            public MessageTransportListener Apply(MessageTransportListener listener)
            {
                _middleware(listener);
                return listener;
            }
        }
    }

    public interface IListenerBuilder
    {
        public string EndpointName { get; }
        public IServiceCollection Services { get; }
    }

    public class ListenerBuilder : IListenerBuilder
    {
        public ListenerBuilder(string endpointName, IServiceCollection services)
        {
            EndpointName = endpointName;
            Services = services;

            // Add a non-named registrations pointing to the named ones so that ConnectionFactory and ConnectionListener can find them.
            services.AddSingleton(GetListenerFunc(endpointName));
        }

        public IServiceCollection Services { get; }

        public string EndpointName { get; }

        private static Func<IServiceProvider, MessageTransportListener> GetListenerFunc(string name)
        {
            return sp =>
            {
                var listener = sp.GetRequiredServiceByName<MessageTransportListener>(name);
                var mw = sp.GetServicesByName<IMessageTransportListenerMiddleware>(name);
                foreach (var middleware in mw)
                {
                    listener = middleware.Apply(listener);
                }

                return listener;
            };
        }
    }

    public static class TcpMessageTransportBuilderExtensions
    {
        public static IListenerBuilder UseTcp(this IListenerBuilder builder, Action<OptionsBuilder<TcpMessageTransportListenerOptions>>? configureListenerOptions = default, Action<OptionsBuilder<TcpMessageTransportOptions>>? configureTransportOptions = default)
        {
            // Add a listener and a factory
            var listenerOptions = builder.Services.AddOptions<TcpMessageTransportListenerOptions>(builder.EndpointName);
            configureListenerOptions?.Invoke(listenerOptions);
            var options = builder.Services.AddOptions<TcpMessageTransportOptions>(builder.EndpointName);
            configureTransportOptions?.Invoke(options);

            builder.Services.AddSingletonNamedService<MessageTransportListener>(
                builder.EndpointName,
                (sp, name) => new TcpMessageTransportListener(
                    name,
                    sp.GetRequiredService<IOptionsMonitor<TcpMessageTransportOptions>>(),
                    sp.GetRequiredService<IOptionsMonitor<TcpMessageTransportListenerOptions>>(),
                    sp.GetRequiredService<ILoggerFactory>()));

            return builder;
        }
    }

    public static class ListenerBuilderProtocolExtensions
    {
        public static IListenerBuilder SetProtocol(this IListenerBuilder builder, TransportProtocol protocol)
            => builder.AddMiddleware(listener => listener.Features.Set<ITransportProtocolFeature>(TransportProtocolFeature.Get(protocol)));
    }

    public static class TlsListenerBuilderExtensions
    {
        public static IListenerBuilder UseTls(this IListenerBuilder builder, Action<OptionsBuilder<TlsOptions>> configureOptions)
        {
            // Add middleware
            builder.Services.AddSingletonNamedService<IMessageTransportListenerMiddleware>(builder.EndpointName, (sp, name) => ActivatorUtilities.CreateInstance<TlsMessageTransportListenerMiddleware>(sp, name));
            builder.Services.AddSingletonNamedService<IMessageTransportConnectorMiddleware>(builder.EndpointName, (sp, name) => ActivatorUtilities.CreateInstance<TlsMessageTransportConnectorMiddleware>(sp, name));
            var options = builder.Services.AddOptions<TlsOptions>(builder.EndpointName);
            configureOptions?.Invoke(options);

            return builder;
        }
    }

    public sealed class ListenerEndpointRegistry
    {
        private readonly List<EndpointInfo> _endpoints = new();

        public void AddEndpoints(EndpointInfo[] endpoints)
        {
            lock (_endpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    var index = -1;
                    for (var i = 0; i < _endpoints.Count; i++)
                    {
                        if (StringComparer.Ordinal.Equals(_endpoints[i].Name, endpoint.Name))
                        {
                            index = i;
                            break;
                        }
                    }

                    if (index >= 0)
                    {
                        _endpoints[index] = endpoint;
                    }
                    else
                    {
                        _endpoints.Add(endpoint);
                    }
                }
            }
        }

        public void RemoveEndpoints(EndpointInfo[] endpoints)
        {
            lock (_endpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    var index = -1;
                    for (var i = 0; i < _endpoints.Count; i++)
                    {
                        if (StringComparer.Ordinal.Equals(_endpoints[i].Name, endpoint.Name))
                        {
                            index = i;
                            break;
                        }
                    }

                    if (index >= 0)
                    {
                        _endpoints.RemoveAt(index);
                    }
                }
            }
        }

        public EndpointInfo[] GetEndpoints()
        {
            lock (_endpoints)
            {
                return _endpoints.ToArray();
            }
        }
    }

    internal abstract class ConnectionListener
    {
        private readonly ListenerEndpointRegistry _listenerEndpointRegistry;
        private readonly ConnectionManager _connectionManager;
        private readonly ConnectionCommon _connectionShared;
        private readonly MessageTransportListener[] _listeners;
        private readonly ConcurrentDictionary<Connection, object?> _connections = new(ReferenceEqualsComparer.Default);
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private Task? _acceptLoopTask;
        private EndpointInfo[] _endpoints = Array.Empty<EndpointInfo>();

        protected ConnectionListener(
            ListenerEndpointRegistry listenerEndpointRegistry,
            IEnumerable<MessageTransportListener> listeners,
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared)
        {

            // Get the listeners which are valid according to their configuration.
            _listeners = listeners.Where(static listener => listener.IsValid).ToArray();
            _listenerEndpointRegistry = listenerEndpointRegistry;
            _connectionManager = connectionManager;
            ConnectionOptions = connectionOptions.Value;
            _connectionShared = connectionShared;
        }

        protected bool HasListeners => _listeners is { Length: > 0 };

        protected IServiceProvider ServiceProvider => _connectionShared.ServiceProvider;

        protected ConnectionTrace TransportTrace => _connectionShared.ConnectionTrace;

        protected ConnectionOptions ConnectionOptions { get; }


        protected abstract Connection CreateConnection(MessageTransport transport);

        protected async Task BindAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task<EndpointInfo>>(_listeners.Length);
            foreach (var listener in _listeners)
            {
                tasks.Add(listener.BindAsync(cancellationToken).AsTask());
            }

            var endpoints = await Task.WhenAll(tasks).ConfigureAwait(false);
            _endpoints = endpoints;
            _listenerEndpointRegistry.AddEndpoints(endpoints);
        }

        protected void Start()
        {
            if (_endpoints is { Length: 0 })
            {
                _acceptLoopTask = Task.CompletedTask;
                return;
            }

            using var _ = new ExecutionContextSuppressor();
            var tasks = new List<Task>(_listeners.Length);
            foreach (var listener in _listeners)
            {
                tasks.Add(RunAcceptLoop(listener));
            }

            _acceptLoopTask = Task.WhenAll(tasks);
        }

        private async Task RunAcceptLoop(MessageTransportListener listener)
        {
            await Task.Yield();
            try
            {
                while (true)
                {
                    var context = await listener.AcceptAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                    if (context == null) break;

                    var connection = CreateConnection(context);
                    StartConnection(connection);
                }
            }
            catch (Exception exception)
            {
                TransportTrace.LogCritical(exception, $"Exception in AcceptAsync for listener {listener}");
            }
        }

        protected async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!HasListeners)
                {
                    return;
                }

                _listenerEndpointRegistry.RemoveEndpoints(_endpoints);

                _shutdownCancellation.Cancel();
                await Task.WhenAll(_listeners.Select(listener => listener.UnbindAsync(cancellationToken).AsTask())).ConfigureAwait(false);

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
                await Task.WhenAll(_listeners.Select(listener => listener.DisposeAsync().AsTask()));
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