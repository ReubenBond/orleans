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
using Orleans.Connections.Transport.Security;
using Orleans.Connections.Transport.Sockets;
using Orleans.Hosting;
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
    public virtual EndPoint? LocalEndpoint { get; set; }

    /// <summary>
    /// Gets the <see cref="EndPoint"/> of the remote side of the channel, if available.
    /// </summary>
    public virtual EndPoint? RemoteEndpoint { get; set; }

    /// <summary>
    /// Gets a value indicating whether this instance is valid.
    /// </summary>
    public virtual bool IsValid => !Closed.IsCancellationRequested;

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

    /// <summary>
    /// Gets the collection of features available on the channel.
    /// </summary>
    public abstract IFeatureCollection Features { get; }
    
    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

/// <summary>
/// Provides <see cref="MessageTransportFactory"/> instances.
/// </summary>
public interface IMessageTransportFactoryProvider
{
    /// <summary>
    /// Gets an <see cref="MessageTransportFactory"/> instance which is suitable for connecting to the provided <see cref="EndPoint"/>.
    /// </summary>
    /// <param name="endpointInfo">The endpoint description.</param>
    /// <param name="factory">The factory.</param>
    /// <returns><see langword="true"/> if a suitable <see cref="MessageTransportFactory"/> could be provided; otherwise <see langword="false"/>.</returns>
    bool TryGetMessageTransportFactory(EndPointInfo endpointInfo, [NotNullWhen(true)] out MessageTransportFactory? factory);
}

internal sealed class OrleansMessageTransportFactoryProvider : IMessageTransportFactoryProvider
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;

    public OrleansMessageTransportFactoryProvider(ILoggerFactory loggerFactory, IOptionsMonitor<TlsOptions> tlsOptions)
    {
        _loggerFactory = loggerFactory;
        _tlsOptions = tlsOptions;
    }

    /// <inheritdoc/>
    public bool TryGetMessageTransportFactory(EndPointInfo endpointInfo, [NotNullWhen(true)] out MessageTransportFactory? factory)
    {
        var tlsOptions = _tlsOptions.CurrentValue;
        factory = new TcpMessageTransportFactory(_loggerFactory);

        if (tlsOptions.EnableTransportLayerSecurity)
        {
            var name = Options.DefaultName;
            factory = new TlsMessageTransportFactory(name, factory, _tlsOptions, _loggerFactory);
        }

        return true;
    }
}

internal sealed class TcpMessageTransportFactoryProvider : IMessageTransportFactoryProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public TcpMessageTransportFactoryProvider(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public bool TryGetMessageTransportFactory(EndPointInfo endpointInfo, [NotNullWhen(true)] out MessageTransportFactory? factory)
    {
        factory = new TcpMessageTransportFactory(_loggerFactory);
        return true;
    }
}

/// <summary>
/// Creates <see cref="MessageTransport"/> instances which are connected to a specified <see cref="EndPoint"/>.
/// </summary>
public abstract class MessageTransportFactory : IAsyncDisposable
{
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

/// <summary>
/// Provides <see cref="MessageTransportListener"/> instances.
/// </summary>
public interface IMessageTransportListenerProvider
{
    /// <summary>
    /// Creates a <see cref="MessageTransportListener"/> for the provided <paramref name="listenOptions"/>.
    /// </summary>
    /// <param name="listenOptions">The listener options.</param>
    /// <param name="listener">The listener.</param>
    /// <returns><see langword="true"/> if a suitable <see cref="MessageTransportListener"/> could be provided; otherwise <see langword="false"/>.</returns>
    bool TryGetMessageTransportListener(ServerMessageTransportBuilder listenOptions, [NotNullWhen(true)] out MessageTransportListener? listener);
}

internal sealed class OrleansMessageTransportListenerProvider : IMessageTransportListenerProvider
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;
    private readonly IServiceProvider _serviceProvider;

    public OrleansMessageTransportListenerProvider(ILoggerFactory loggerFactory, IOptionsMonitor<TlsOptions> tlsOptions, IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory;
        _tlsOptions = tlsOptions;
        _serviceProvider = serviceProvider;
    }

    public bool TryGetMessageTransportListener(ServerMessageTransportBuilder listenOptions, [NotNullWhen(true)] out MessageTransportListener? listener)
    {
        listener = default;
        if (listenOptions.ListenEndpoint is not IPEndPoint)
        {
            return false;
        }

        listener = new TcpMessageTransportListener(listenOptions, _serviceProvider, _loggerFactory);

        var tlsOptions = _tlsOptions.Get(listenOptions.EndpointName);
        if (tlsOptions.EnableTransportLayerSecurity)
        {
            // Wrap the listener in a TLS listener.
            listener = new TlsMessageTransportListener(listenOptions.EndpointName, listener, _tlsOptions, _loggerFactory);
        }

        return true;
    }
}

public interface IMessageTransportMiddleware
{
    /// <summary>
    /// Applies this middleware to the provided transport. 
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <returns>The transport with this middleware applied to it.</returns>
    MessageTransport Apply(MessageTransport transport);
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

/// <summary>
/// Middleware which adds TLS to all <see cref="MessageTransport"/> instances created by a <see cref="MessageTransportListener"/>.
/// </summary>
public sealed class TlsMessageTransportListenerMiddleware : IMessageTransportListenerMiddleware
{
    private readonly string _endpointName;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;
    private readonly ILoggerFactory _loggerFactory;

    public TlsMessageTransportListenerMiddleware(string endpointName, IOptionsMonitor<ServerMessageTransportBuilder> listenerOptions, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        _endpointName = endpointName;
        _tlsOptions = tlsOptions;
        _loggerFactory = loggerFactory;
    }

    public MessageTransportListener Apply(MessageTransportListener input) => new TlsMessageTransportListener(_endpointName, input, _tlsOptions, _loggerFactory);
}

public sealed class TlsMessageTransportMiddleware : IMessageTransportMiddleware
{
    private readonly string _endpointName;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;
    private readonly ILoggerFactory _loggerFactory;

    public TlsMessageTransportMiddleware(string endpointName, IOptionsMonitor<TlsOptions> tlsOptions, ILoggerFactory loggerFactory)
    {
        _endpointName = endpointName;
        _tlsOptions = tlsOptions;
        _loggerFactory = loggerFactory;
    }

    public MessageTransport Apply(MessageTransport input) => new ClientTlsMessageTransport(input, _tlsOptions.Get(_endpointName), _loggerFactory.CreateLogger<TlsMessageTransport>());
}

internal sealed class TcpMessageTransportListenerProvider : IMessageTransportListenerProvider
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public TcpMessageTransportListenerProvider(ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
    }

    public bool TryGetMessageTransportListener(ServerMessageTransportBuilder listenOptions, [NotNullWhen(true)] out MessageTransportListener? listener)
    {
        listener = default;
        if (listenOptions.ListenEndpoint is not IPEndPoint)
        {
            return false;
        }

        listener = new TcpMessageTransportListener(listenOptions, _serviceProvider, _loggerFactory);

        return true;
    }
}

public interface IClientMessageTransportBuilder
{
    /// <summary>
    /// Gets the name of this endpoint.
    /// </summary>
    string EndpointName { get; }

    /// <summary>
    /// Gets the service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Adds middleware to this endpoint.
    /// </summary>
    /// <param name="middleware">The middleware decorator delegate.</param>
    /// <returns>This instance.</returns>
    IClientMessageTransportBuilder AddClientMiddleware(Func<IServiceProvider, MessageTransport, MessageTransport> middleware);
}

public interface IServerMessageTransportBuilder
{
    /// <summary>
    /// Gets the name of this endpoint.
    /// </summary>
    string EndpointName { get; }

    /// <summary>
    /// Gets the service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Adds middleware to this endpoint.
    /// </summary>
    /// <param name="middleware">The middleware decorator delegate.</param>
    /// <returns>This instance.</returns>
    IServerMessageTransportBuilder AddServerMiddleware(Func<IServiceProvider, MessageTransportListener, MessageTransportListener> middleware);
}

/// <summary>
/// Describes a message endpoint.
/// </summary>
[GenerateSerializer]
public sealed class EndPointInfo
{
    /// <summary>
    /// Gets the name of the endpoint.
    /// </summary>
    [Id(0)]
    public required string EndpointName { get; init; }

    /// <summary>
    /// Gets the configuration for this endpoint.
    /// </summary>
    [Id(1)]
    public Dictionary<string, string> Configuration { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public static class TransportFactoryClientBuilderExtensions
{
    public static IServiceCollection AddTcpTransportListener(this IServiceCollection services, string endpointName, IPEndPoint endpoint, Action<OptionsBuilder<ServerMessageTransportBuilder>>? configureOptions = null)
    {
        var options = services.AddOptions<ServerMessageTransportBuilder>(endpointName);
        options.Configure(options => options.ListenEndpoint = endpoint);
        configureOptions?.Invoke(options);

        return services.AddSingletonNamedService<MessageTransportListener>(
            endpointName,
            (sp, name) => new TcpMessageTransportListener(
                sp.GetRequiredService<IOptionsMonitor<ServerMessageTransportBuilder>>().Get(endpointName),
                sp,
                sp.GetRequiredService<ILoggerFactory>()));
    }



    public static IServiceCollection AddTcpTransportFactory(this IServiceCollection services)
    {
        return services.AddSingletonNamedService<MessageTransportFactory, TcpMessageTransportFactory>("tcp");
    }

    public static IServiceCollection AddTcpTransportFactory(this IServiceCollection services, string endpointName, Action<OptionsBuilder<TransportFactoryOptions>> configureOptions)
    {
        var options = services.AddOptions<TransportFactoryOptions>(endpointName);
        configureOptions?.Invoke(options);
        return services.AddSingletonNamedService<MessageTransportFactory, TcpMessageTransportFactory>("tcp");
    }

    public static IServiceCollection AddTransportFactoryMiddleware(this IServiceCollection services, string endpointName, Action<OptionsBuilder<TransportFactoryOptions>> configureOptions)
    {
        var options = services.AddOptions<TransportFactoryOptions>(endpointName);
        configureOptions?.Invoke(options);
        return services.AddSingletonNamedService<MessageTransportFactory, TcpMessageTransportFactory>(endpointName);
    }

    public static IServiceCollection AddTlsTransportFactory(this IServiceCollection services, Action<OptionsBuilder<TlsOptions>> configureTlsOptions)
    {
        var transportName = "tcp+tls";
        return services.AddTlsTransportFactory(transportName, configureTlsOptions);
    }

    public static IServiceCollection AddTlsTransportFactory(this IServiceCollection services, string endpointName, Action<OptionsBuilder<TlsOptions>> configureTlsOptions)
    {
        var options = services.AddOptions<TlsOptions>(endpointName);
        configureTlsOptions?.Invoke(options);
        return services.AddSingletonNamedService<MessageTransportFactory>(endpointName, static (sp, name) => new TlsMessageTransportFactory(
            name,
            new TcpMessageTransportFactory(sp.GetRequiredService<ILoggerFactory>()),
            sp.GetRequiredService<IOptionsMonitor<TlsOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));
    }


    public static IServiceCollection AddMessageTransports(this IClientBuilder clientBuilder) => clientBuilder.Services;

    public static void Configure(IClientBuilder siloBuilder)
    {
    }

    public static IServiceCollection AddTransportFactory<TFactory>(this IServiceCollection services, string endpointName, Action<OptionsBuilder<TransportFactoryOptions>> configureOptions) where TFactory : MessageTransportFactory
    {
        configureOptions?.Invoke(services.AddOptions<TransportFactoryOptions>(endpointName));
        services.AddSingletonNamedService<MessageTransportFactory, TFactory>(endpointName);
        return services;
    }
}

public class TransportFactoryOptions : IClientMessageTransportBuilder
{
    private readonly List<Func<IServiceProvider, MessageTransport, MessageTransport>> _transportMiddleware = new();

    public TransportFactoryOptions(string endpointName, IServiceCollection services)
    {
        EndpointName = endpointName;
        Services = services;
    }

    public string EndpointName { get; }
    public IServiceCollection Services { get; }

    internal MessageTransport ApplyMiddleware(IServiceProvider serviceProvider, MessageTransport transport)
    {
        if (_transportMiddleware is { Count: > 0 })
        {
            var middleware = new List<Func<IServiceProvider, MessageTransport, MessageTransport>>(_transportMiddleware);
            middleware.Reverse();
            foreach (var middlewareDelegate in middleware)
            {
                transport = middlewareDelegate(serviceProvider, transport);
            }
        }

        return transport;
    }

    public IClientMessageTransportBuilder AddClientMiddleware(Func<IServiceProvider, MessageTransport, MessageTransport> middleware)
    {
        _transportMiddleware.Add(middleware ?? throw new ArgumentNullException(nameof(middleware)));
        return this;
    }
}

public class ServerMessageTransportBuilder : IServerMessageTransportBuilder
{
    private readonly List<Func<IServiceProvider, MessageTransportListener, MessageTransportListener>> _listenerMiddleware = new();

    public ServerMessageTransportBuilder(string endpointName, IServiceCollection services)
    {
        EndpointName = endpointName;
        Services = services;
    }

    public IServiceCollection Services { get; }

    public string EndpointName { get; }

    public EndPoint? ListenEndpoint { get; set; }

    public IServerMessageTransportBuilder AddServerMiddleware(Func<IServiceProvider, MessageTransportListener, MessageTransportListener> middleware)
    {
        _listenerMiddleware.Add(middleware ?? throw new ArgumentNullException(nameof(middleware)));
        return this;
    }

    internal MessageTransportListener ApplyMiddleware(IServiceProvider serviceProvider, MessageTransportListener transport)
    {
        if (_listenerMiddleware is { Count: > 0 })
        {
            var middleware = new List<Func<IServiceProvider, MessageTransportListener, MessageTransportListener>>(_listenerMiddleware);
            middleware.Reverse();
            foreach (var middlewareDelegate in middleware)
            {
                transport = middlewareDelegate(serviceProvider, transport);
            }
        }

        return transport;
    }
}

/*

TransportName is a property of a listener which is propagated to the clients (which initiate connections) via an EndpointInfo object in order to indicate which registered transport provider to use.
EndpointInfo is serialized into MembershipTable / GatewayProvider and has
  * TransportName
  * Endpoint (note: how should these be serialized as strings?) << might be best to use URIs as endpoints for this reason and rely on providers to convert them into IPEndpoint/UnixDomainEndpoint/FileHandleEndpoint/etc
     * using a URI instead also lets us get rid of TransportName, since it can be encoded using the URI scheme instead. Configuration like TLS certificate thumbprints can be encoded as query parameters
     * Do we use "gw.tcp" to indicate a Gateway endpoint?
  * Configuration (an IConfiguration, serialized as a JSON object? Alternatively serialized as a Dictionary<string, string>)

* TLS: client options should be given the 
*

* EndpointInfo
  * Properties: Dictionary<string, string>
* SiloEndpointInfo // The base information object returned by MembershipTable, GatewayListProvider, and static config
*   * Id : SiloAddress
*   * Endpoints : List<EndpointInfo>
*   * Properties : Dictionary<string, string>
* EndpointInfoProvider
*   * GetEndpointInfoAsync(SiloAddress) : ValueTask<EndpointInfo> // For silos in remote clusters
* IEndpointInfoProvider
*   * TryGetEndpointInfoAsync(SiloAddress) : ValueTask<EndpointInfo?> // EndpointInfoProvider returns first non-null result from a collection of providers
*      * Alternatively: Merge EndpointInfo values together or return a collection of EndpointInfo values (eg, with different properties, such as one if connecting to a local cluster node, one if connecting via a metacluster gateway
    * Cluster bootstrap and development/static clustering: provide an implementation of this which returns the seeds / primary based on config.
* SiloMessageTransportProvider
*   * CreateMessageTransportAsync(EndpointInfo) : ValueTask<MessageTransport>
* ConnectionFactory:
*   * ConnectAsync(SiloAddress) << lookup MembershipTableEntry and connect? How do we bootstrap clusters? Assume TCP?

* IServerMessageTransportBuilder
*   * GetMessageTransportListener() : MessageTransportListener 
*   * MessageTransportListener has EndpointInfo
*   * Name : string << ?
* ConnectionListener
*   * Resolve all IMessageTransportListenerProvider from ServiceProvider, bind all, accept from all, create connections from all accepted transports
* 
* Endpoint
*/
    /*

     TransportType = a name used to identify a configured transport. Multiple endpoints can exist with the same transport type (eg, different ports, different TLS certificates, etc)
     Configure transports separately from endpoints. Transports are configured per transport type, and endpoint configuration specifies a transport type.
     Client-side needs a TLS certificate resolver: given an EndpointInfo, return a TLS certificate to use for that endpoint.
     Server side can use the same TLS certificate resolver.
        ITlsCertificateSelector { bool TryGetCertificate(EndpointInfo, ConnectionDirection (* client or server *) , out X509Certificate2) } 

      IClientBuilder.AddMessageTransport(EndpointName, TransportFactory, Action<IMessageTransportFactoryBuilder>)
      ISiloBuilder.AddMessageTransport(EndpointName, TransportFactory, TransportListenerFactory)

      I*Builder.ConfigureTransportLayerSecurity(Func<EndpointInfo, ValueTask<X509Certificate2?>> certificateSelector);
      ISiloBuilder.AddEndpoint(EndpointName, Func<TransportListenerOptions>);

// "silo" = endpoint name for silo to silo communication
// "gw" = endpoint name for gateway communication
// Future: "gg" = endpoint name for geo cluster gateway (typically HTTPS)

// Default middleware: add TLS if there is a TlsOptions matching the EndpointName => take into account the remote EndPointInfo

ISiloBuilder.Configure<EndpointOptions>(Action<EndpointOptions> configureOptions)
    EndpointOptions.ConfigureSiloEndpoint(Action<IServerMessageTransportBuilder> configureSiloEndpoint)
    IMTLB.ConfigureTls(Action<TlsOptions> configureTls) => configures TlsOptions with "silo" EP name

    EndpointOptions.ConfigureProxyEndpoint(Action<IServerMessageTransportBuilder> configureGatewayEndpoint)
    IMTLB.ConfigureTls(Action<TlsOptions> configureTls) => configures TlsOptions with "silo" EP name

IClientBuilder.Configure<EndpointOptions>(Action<EndpointOptions> configureOptions)
    EndpointOptions.ConfigureClientEndpoint(Action<IClientMessageTransportBuilder> configureClientEndpoint)
    IMTB.ConfigureTls(Action<TlsOptions> configureTls) => configures TlsOptions with "gw" EP name

ISiloBuilder.ConfigureServerTransport(string endpointName, Action<IServerMessageTransportBuilder> configureEndpoint) // Listen on an endpoint named `endpointName`
    // Adds an IMessageTransportListenerProvider registration to IServiceCollection with no name, which will retrieve the IMessageTransportListenerProvider with the name `endpointName` and if it is not found, it will throw a configuration exception ("Call ListenTcp, etc to configure a listener").
    // ISMTB.ListenTcp(IPEndPoint) // Adds an IMessageTransportListenerProvider registration to IServiceCollection for name `endpointName`
    // ISMTB.ListenTcp(IPEndPoint) // Adds an IMessageTransportListenerProvider registration to IServiceCollection for name `endpointName`
    // ISMTB.ConfgigureTls(Action<TlsOptions>) // 1. Configures TlsOptions with name `endpointName`. 2. Adds a middleware to wrap the MessageTransportListener with one which injects TLS
ISiloBuilder.ConfigureClientTransport(string endpointName, Action<IClientMessageTransportBuilder> configureEndpoint) // How to connect to an endpoint named `endpointName`, if one is encountered
    // ISMTB.UseTcp() // Adds an IMessageTransportFactoryProvider registration to IServiceCollection for name `endpointName`
    // ISMTB.ConfgigureTls(Action<TlsOptions>) // 1. Configures TlsOptions with name `endpointName`. 2. Adds a middleware to wrap the MessageTransportFactory with one which injects TLS
IClientBuilder.ConfigureClientTransport(string endpointName, Action<IClientMessageTransportBuilder> configureEndpoint)

IClientBuilder.ConfigureServerEndpoint(string endpointName, Action<IMessageTransportListenerBuilder> configureEndpoint)
    IMTLB.ConfigureTls(Action<TlsOptions> configureTls) => configures TlsOptions with endpointName EP name

IServiceBuilder.AddTcpMessageTransport();
IServiceBuilder.AddTlsMessageTransport();

    ISiloBuilder.ConfigureTransports(Action<ITransportBuilder> transportBuilder)
    ITransportBuilder.EndPoints : IEndpointCollection
    IEndPointCollection.Clear()
    IEndPointCollection.Add(Action<IEndPointBuilder>)
    IEndPointBuilder.ListenTcp(IPEndPoint)
    IEndPointBuilder.ConfigureTls(Action<TlsOptions>)

    ISiloBuilder.ConfigureEndpoints(Action<IEndpointBuilder> endpointBuilder)
    endpointBuilder.ListenTcp(IPEndpoint)
    endpointBuilder.ListenTcpTls(IPEndpoint)
    endpointBuilder.AddTcpListener(name, endpoint)
    endpointBuilder.AddListener<TListener>(name)

    We want TCP to be the default, upgrading to TLS gradually (eg, one host adds TLS, others gradually add it) instead of all at once.
    We want to be able to swap TCP for something else
    We need multiple endpoints (gateway vs silo, for example, possibly HTTPS for geo-distributed cluster gateways)
     * Clients (incl silos) need to choose the appropriate endpoint for them.
     * Maybe a "flags" field in the config can satisfy this, being one of gw|silo|geogw

    Choose transport like this:
      * Get silo endpoints, filter to the required role: gw vs silo
      * Iterate down filtered list of transports until we find one for which `IMessageTransportFactoryProvider` returns 'true' and the `MessageTransportFactory` returns a non-null `MessageTransport`
   
    EndpointInfo => how o configure it
     */

    // Transport names:
    /*
    Example transport names:
     > tcp << added by default?
     > tcp+tls << reads thumbprint from remote EndpointInfo.Configuration, but how does it identify itself? Need to configure mTLS
     > quic
     > mem << in-memory transport, primarily for testing
     > unix << unix domain sockets
     > http2 << bi-directional HTTP2 streams
     > ws << web sockets over HTTP/2?
     */

        // Membership table:
        //  * SiloAddress => List<EndpointInfo> (ordered by preference)
        // EndpointInfo =>
        // {
        //   TransportName: string // used to look up a configured transport?
        //   Endpoint: string // serialized endpoint
        //   Configuration: Dictionary<string, string> // serialized configuration
        // }

        // Create listeners: for each configured endpoint on silo, use listener provider to create a listener.
        // After bind, ask listeners for their configuration (we do it this way so that binding to unknown ports works, or asking some coordination service to provision a port, resolve DNS, etc)
        // Publish listener configuration in table.
        // Open Q: do we allow dynamically changing this configuration? Eg for cert roll-over? Seems like "no" is a good answer for now.

        // Create connections: get all gateways from gw membership provider, enumerate each one's endpoint infos in order, try creating connections for each, stopping when a connection is successfully created.
        // siloBuilder.AddMessageTransports().AddTransportFactory<
