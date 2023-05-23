#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport.Sockets;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Connections.Transport;

/// <summary>
/// Represents a bi-directional communication channel between two hosts.
/// </summary>
public abstract class MessageTransport : IAsyncDisposable
{
    /// <summary>
    /// Gets the cancellation token which is canceled once the connection is closed.
    /// </summary>
    public virtual CancellationToken Closed { get; }

    /// <summary>
    /// Gets a value indicating whether this instance is valid.
    /// </summary>
    public virtual bool IsValid => !Closed.IsCancellationRequested;

    /// <summary>
    /// Gets the collection of features available on the transport.
    /// </summary>
    public abstract IFeatureCollection Features { get; }

    /// <summary>
    /// Submits a read request to the channel.
    /// </summary>
    /// <param name="request">The read request.</param>
    /// <returns><see langword="true"/> if the read request was accepted by the channel, <see langword="false"/> if it was rejected.</returns>
    public abstract bool ReadAsync(ReadRequest request);

    /// <summary>
    /// Submits a write request to the channel.
    /// </summary>
    /// <param name="request">The write request.</param>
    /// <returns><see langword="true"/> if the read request was accepted by the channel, <see langword="false"/> if it was rejected.</returns>
    public abstract bool WriteAsync(WriteRequest request);

    /// <summary>
    /// Closes the channel, optionally with a provided exception.
    /// </summary>
    /// <param name="closeException">The channel close exception, which is propagated to requests.</param>
    /// <returns>A <see cref="ValueTask"/> which completes once the channel has been closed.</returns>
    public abstract ValueTask CloseAsync(Exception? closeException);
    
    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

/// <summary>
/// Creates <see cref="MessageTransport"/> instances which are connected to a specified endpoint.
/// </summary>
public abstract class MessageTransportConnector : IAsyncDisposable
{
    /// <summary>
    /// Gets the endpoint name that this connector connects to.
    /// </summary>
    public abstract string EndpointName { get; }

    /// <summary>
    /// Gets the collection of features available on the transport factory.
    /// </summary>
    public abstract IFeatureCollection Features { get; }

    /// <summary>
    /// Gets a value indicating whether this connector is valid for use.
    /// </summary>
    public abstract bool IsValid { get; }
    
    /// <summary>
    /// Creates a <see cref="MessageTransport"/> connected to the specified <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connected message transport.</returns>
    public abstract ValueTask<MessageTransport> CreateAsync(EndpointInfo endpoint, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

/// <summary>
/// Middleware which operates on <see cref="MessageTransportConnector"/> instances.
/// </summary>
public interface IMessageTransportConnectorMiddleware
{
    /// <summary>
    /// Applies this middleware to the provided transport connector. 
    /// </summary>
    /// <param name="transport">The transport connector.</param>
    /// <returns>The transport factory with this middleware applied to it.</returns>
    MessageTransportConnector Apply(MessageTransportConnector transport);
}

/// <summary>
/// Middleware which operates on <see cref="MessageTransportListener"/> instances.
/// </summary>
public interface IMessageTransportListenerMiddleware
{
    /// <summary>
    /// Applies this middleware to the provided listener. 
    /// </summary>
    /// <param name="listener">The listener.</param>
    /// <returns>The listener with this middleware applied to it.</returns>
    MessageTransportListener Apply(MessageTransportListener listener);
}

public static class TcpMessageTransportBuilderExtensions
{
    public static IConnectorBuilder UseTcp(this IConnectorBuilder builder, Action<OptionsBuilder<TcpMessageTransportOptions>>? configureTransportOptions = default)
    {
        // Add a listener and a factory
        var options = builder.Services.AddOptions<TcpMessageTransportOptions>(builder.EndpointName);
        configureTransportOptions?.Invoke(options);

        builder.Services.AddSingletonNamedService<MessageTransportConnector>(
            builder.EndpointName,
            (sp, name) => new TcpMessageTransportConnector(
                name,
                sp.GetRequiredService<IOptionsMonitor<TcpMessageTransportOptions>>(),
                sp.GetRequiredService<ILoggerFactory>()));

        return builder;
    }
}

/// <summary>
/// Builder type for building a connector which can create a <see cref="MessageTransport"/> connected to a specified endpoint.
/// </summary>
public interface IConnectorBuilder
{
    /// <summary>
    /// Gets the endpoint name.
    /// </summary>
    public string EndpointName { get; }

    /// <summary>
    /// Gets the service collection.
    /// </summary>
    public IServiceCollection Services { get; }
}

public interface IClientTransportCollection
{
    IConnectorBuilder AddConnector(string endpointName);
}

internal class ClientTransportCollection : IClientTransportCollection
{
    private readonly IServiceCollection _services;
    public ClientTransportCollection(IServiceCollection services) => _services = services;

    public IConnectorBuilder AddConnector(string endpointName)
    {
        var result = new ConnectorBuilder(endpointName, _services);
        result.SetProtocol(TransportProtocol.Gateway);
        return result;
    }
}

public static class ConnectorBuilderMiddlewareExtensions
{
    public static IConnectorBuilder AddMiddleware(this IConnectorBuilder builder, IMessageTransportConnectorMiddleware middleware)
    {
        builder.Services.AddSingletonNamedService(builder.EndpointName, middleware);
        return builder;
    }

    public static IConnectorBuilder AddMiddleware(this IConnectorBuilder builder, Action<MessageTransportConnector> middleware) => builder.AddMiddleware(new ActionMessageTransportConnectorMiddleware(middleware));

    public static IConnectorBuilder AddMiddleware(this IConnectorBuilder builder, Func<MessageTransportConnector, MessageTransportConnector> middleware) => builder.AddMiddleware(new FuncMessageTransportConnectorMiddleware(middleware));

    public static IConnectorBuilder SetProtocol(this IConnectorBuilder builder, TransportProtocol protocol)
        => builder.AddMiddleware(connector => connector.Features.Set<ITransportProtocolFeature>(TransportProtocolFeature.Get(protocol)));

    private sealed class FuncMessageTransportConnectorMiddleware : IMessageTransportConnectorMiddleware
    {
        private readonly Func<MessageTransportConnector, MessageTransportConnector> _middleware;

        public FuncMessageTransportConnectorMiddleware(Func<MessageTransportConnector, MessageTransportConnector> middleware)
        {
            _middleware = middleware;
        }

        public MessageTransportConnector Apply(MessageTransportConnector listener) => _middleware(listener);
    }

    private sealed class ActionMessageTransportConnectorMiddleware : IMessageTransportConnectorMiddleware
    {
        private readonly Action<MessageTransportConnector> _middleware;

        public ActionMessageTransportConnectorMiddleware(Action<MessageTransportConnector> middleware)
        {
            _middleware = middleware;
        }

        public MessageTransportConnector Apply(MessageTransportConnector listener)
        {
            _middleware(listener);
            return listener;
        }
    }
}

internal class ConnectorBuilder : IConnectorBuilder
{
    public ConnectorBuilder(string endpointName, IServiceCollection services)
    {
        EndpointName = endpointName;
        Services = services;
        services.AddSingleton(GetConnectorFunc(endpointName));
    }

    public IServiceCollection Services { get; }

    public string EndpointName { get; }

    private static Func<IServiceProvider, MessageTransportConnector> GetConnectorFunc(string name)
    {
        return sp =>
        {
            var connector = sp.GetRequiredServiceByName<MessageTransportConnector>(name);
            var mw = sp.GetServicesByName<IMessageTransportConnectorMiddleware>(name);
            foreach (var middleware in mw)
            {
                connector = middleware.Apply(connector);
            }

            return connector;
        };
    }
}
