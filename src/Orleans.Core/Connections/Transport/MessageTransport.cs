#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
    /// Gets the endpoint of the local side of the channel, if available.
    /// </summary>
    // TODO: REMOVE if not necessary
    // TODO: REMOVE if not necessary
    // TODO: REMOVE if not necessary
    // TODO: REMOVE if not necessary
    public virtual EndPoint? LocalEndpoint { get; set; }

    /// <summary>
    /// Gets the <see cref="EndPoint"/> of the remote side of the channel, if available.
    /// </summary>
    // TODO: REMOVE if not necessary
    // TODO: REMOVE if not necessary
    // TODO: REMOVE if not necessary
    // TODO: REMOVE if not necessary
    public virtual EndPoint? RemoteEndpoint { get; set; }

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
/// Creates <see cref="MessageTransport"/> instances which are connected to a specified <see cref="EndPoint"/>.
/// </summary>
public abstract class MessageTransportConnector : IAsyncDisposable
{
    /// <summary>
    /// Gets the collection of features available on the transport factory.
    /// </summary>
    public abstract IFeatureCollection Features { get; }
    
    /// <summary>
    /// Creates a <see cref="MessageTransport"/> connected to the specified <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connected message transport.</returns>
    public abstract ValueTask<MessageTransport> CreateAsync(EndPointInfo endpoint, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

internal interface IEndPointNameFeature
{
    public string EndPointName { get; }
}

internal class EndPointNameFeature : IEndPointNameFeature
{
    [SetsRequiredMembers]
    public EndPointNameFeature(string endPointName)
    {
        EndPointName = endPointName;
    }

    public required string EndPointName { get; init; }
}

/// <summary>
/// Middleware which operates on <see cref="MessageTransportConnector"/>.
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
        var options = builder.Services.AddOptions<TcpMessageTransportOptions>(builder.EndPointName);
        configureTransportOptions?.Invoke(options);

        builder.Services.AddSingletonNamedService<MessageTransportConnector, TcpMessageTransportConnector>(builder.EndPointName);

        // Add a non-named registrations pointing to the named ones
        builder.Services.AddSingleton(sp => sp.GetRequiredServiceByName<MessageTransportConnector>(builder.EndPointName));

        return builder;
    }
}

public interface IConnectorBuilder
{
    public string EndPointName { get; }
    public IServiceCollection Services { get; }
}

public interface IClientTransportCollection
{
    IConnectorBuilder AddTransport(string endPointName);
}

internal class ClientTransportCollection : IClientTransportCollection
{
    private readonly IServiceCollection _services;
    public ClientTransportCollection(IServiceCollection services) => _services = services;

    public IConnectorBuilder AddTransport(string endPointName)
    {
        var result = new ConnectorBuilder(endPointName, _services);
        result.AddMiddleware(factory =>
        {
            factory.Features.Set<ITransportProtocolFeature>(TransportProtocolFeature.ClientToGateway);
            factory.Features.Set<IEndPointNameFeature>(new EndPointNameFeature(endPointName));
        });
        return result;
    }
}

public static class ConnectorBuilderMiddlewareExtensions
{
    public static IConnectorBuilder AddMiddleware(this IConnectorBuilder builder, IMessageTransportConnectorMiddleware middleware)
    {
        builder.Services.AddSingletonNamedService(builder.EndPointName, middleware);
        return builder;
    }

    public static IConnectorBuilder AddMiddleware(this IConnectorBuilder builder, Action<MessageTransportConnector> middleware) => builder.AddMiddleware(new ActionMessageTransportConnectorMiddleware(middleware));

    public static IConnectorBuilder AddMiddleware(this IConnectorBuilder builder, Func<MessageTransportConnector, MessageTransportConnector> middleware) => builder.AddMiddleware(new FuncMessageTransportConnectorMiddleware(middleware));

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
    public ConnectorBuilder(string endPointName, IServiceCollection services)
    {
        EndPointName = endPointName;
        Services = services;
    }

    public IServiceCollection Services { get; }

    public string EndPointName { get; }
}
