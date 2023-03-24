#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport.Security;
using Orleans.Connections.Transport.Sockets;
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
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="factory">The factory.</param>
    /// <returns><see langword="true"/> if a suitable <see cref="MessageTransportFactory"/> could be provided; otherwise <see langword="false"/>.</returns>
    bool TryGetMessageTransportFactory(EndPoint endpoint, [NotNullWhen(true)] out MessageTransportFactory? factory);
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
    public bool TryGetMessageTransportFactory(EndPoint endpoint, [NotNullWhen(true)] out MessageTransportFactory? factory)
    {
        var tlsOptions = _tlsOptions.CurrentValue;
        if (tlsOptions.EnableTransportLayerSecurity)
        {
            var name = Options.DefaultName;
            factory = new TlsMessageTransportFactory(name, _tlsOptions, _loggerFactory);
            return true;
        }
        else
        {
            factory = new TcpMessageTransportFactory(_loggerFactory);
            return true;
        }
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
    public abstract ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default);

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
    bool TryGetMessageTransportListener(TransportListenerOptions listenOptions, [NotNullWhen(true)] out MessageTransportListener? listener);
}

internal sealed class OrleansMessageTransportListenerProvider : IMessageTransportListenerProvider
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions;

    public OrleansMessageTransportListenerProvider(ILoggerFactory loggerFactory, IOptionsMonitor<TlsOptions> tlsOptions)
    {
        _loggerFactory = loggerFactory;
        _tlsOptions = tlsOptions;
    }

    public bool TryGetMessageTransportListener(TransportListenerOptions listenOptions, [NotNullWhen(true)] out MessageTransportListener? listener)
    {
        listener = default;
        var tlsOptions = _tlsOptions.CurrentValue;
        if (listenOptions.Endpoint is not IPEndPoint endpoint)
        {
            return false;
        }

        if (tlsOptions.EnableTransportLayerSecurity)
        {
            listener = new TlsMessageTransportListener(listenOptions, _tlsOptions, _loggerFactory);
        }
        else
        {
            listener = new TcpMessageTransportListener(listenOptions, _loggerFactory);
        }

        return listener is not null;
    }
}

public interface IMessageTransportBuilder
{
    public bool IsServer { get; }
    IServiceProvider ApplicationServices { get; }
    //IMessageTransportBuilder AddMiddleware(Func<MessageTransport, MessageTransport> middleware);
}

public sealed class ConnectionInitiatorFeature
{
    public ConnectionInitiatorFeature(bool isClient) => IsClient = isClient;
    public bool IsClient { get; }
}

public abstract class MessageTransportListener : IAsyncDisposable
{
    public abstract EndPoint LocalEndpoint { get; }
    public abstract ValueTask BindAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask UnbindAsync(CancellationToken cancellationToken = default);
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
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

* IMessageTransportListenerProvider
*   * GetMessageTransportListener() : MessageTransportListener 
*   * MessageTransportListener has EndpointInfo
*   * Name : string << ?
* ConnectionListener
*   * Resolve all IMessageTransportListenerProvider from ServiceProvider, bind all, accept from all, create connections from all accepted transports
* 
* Endpoint
*/  

public static class TransportFactoryClientBuilderExtensions
{
    public static IServiceCollection AddTcpTransportListener(this IServiceCollection services, string transportName, IPEndPoint endpoint, Action<OptionsBuilder<TransportListenerOptions>>? configureOptions = null)
    {
        var options = services.AddOptions<TransportListenerOptions>(transportName);
        options.Configure(options => options.Endpoint = endpoint);
        configureOptions?.Invoke(options);

        return services.AddSingletonNamedService<MessageTransportListener>(
            transportName,
            (sp, name) => new TcpMessageTransportListener(
                sp.GetRequiredService<IOptionsMonitor<TransportListenerOptions>>().Get(transportName),
                sp.GetRequiredService<ILoggerFactory>()));
    }

    public static IServiceCollection AddTcpTransportFactory(this IServiceCollection services)
    {
        return services.AddSingletonNamedService<MessageTransportFactory, TcpMessageTransportFactory>("tcp");
    }

    public static IServiceCollection AddTcpTransportFactory(this IServiceCollection services, string transportName, Action<OptionsBuilder<TransportFactoryOptions>> configureOptions)
    {
        var options = services.AddOptions<TransportFactoryOptions>(transportName);
        configureOptions?.Invoke(options);
        return services.AddSingletonNamedService<MessageTransportFactory, TcpMessageTransportFactory>("tcp");
    }

    public static IServiceCollection AddTransportFactoryMiddleware(this IServiceCollection services, string transportName, Action<OptionsBuilder<TransportFactoryOptions>> configureOptions)
    {
        var options = services.AddOptions<TransportFactoryOptions>(transportName);
        configureOptions?.Invoke(options);
        return services.AddSingletonNamedService<MessageTransportFactory, TcpMessageTransportFactory>(transportName);
    }

    public static IServiceCollection AddTlsTransportFactory(this IServiceCollection services, Action<OptionsBuilder<TlsOptions>> configureTlsOptions)
    {
        var transportName = "tls";
        return services.AddTlsTransportFactory(transportName, configureTlsOptions);
    }

    public static IServiceCollection AddTlsTransportFactory(this IServiceCollection services, string transportName, Action<OptionsBuilder<TlsOptions>> configureTlsOptions)
    {
        var options = services.AddOptions<TlsOptions>(transportName);
        configureTlsOptions?.Invoke(options);
        return services.AddSingletonNamedService<MessageTransportFactory>(transportName, static (sp, name) => new TlsMessageTransportFactory(
            name,
            sp.GetRequiredService<IOptionsMonitor<TlsOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));
    }

    // Transport names:
    /*
    Example transport names:
     > tcp << added by default?
     > tls << reads thumbprint from remote EndpointInfo.Configuration, but how does it identify itself? Need to configure mTLS
     > quic
     > mem << in-memory transport, primarily for testing
     > unix << unix domain sockets
     > http2 << bi-directional HTTP2 streams
     > ws << web sockets over HTTP/2?

     */
    public static IServiceCollection AddTransportFactory<TFactory>(this IServiceCollection services, string transportName, Action<OptionsBuilder<TransportFactoryOptions>> configureOptions) where TFactory : MessageTransportFactory
    {
        configureOptions?.Invoke(services.AddOptions<TransportFactoryOptions>(transportName));
        services.AddSingletonNamedService<MessageTransportFactory, TFactory>(transportName);
        return services;
    }
}

public class TransportFactoryOptions : IMessageTransportBuilder
{
    private readonly List<Func<MessageTransport, MessageTransport>> _middleware = new();
    private readonly IServiceProvider _applicationServices;

    public TransportFactoryOptions(IServiceProvider applicationServices)
    {
        _applicationServices = applicationServices;
    }

    internal List<Func<MessageTransport, MessageTransport>> Middleware => _middleware;

    bool IMessageTransportBuilder.IsServer => false;
    IServiceProvider IMessageTransportBuilder.ApplicationServices => _applicationServices;
    public IMessageTransportBuilder AddMiddleware(Func<MessageTransport, MessageTransport> middleware)
    {
        _middleware.Add(middleware ?? throw new ArgumentNullException(nameof(middleware)));
        return this;
    }
}

public class TransportListenerOptions : IMessageTransportBuilder
{
    //private readonly List<Func<MessageTransport, MessageTransport>> _middleware = new();
    private readonly IServiceProvider _applicationServices;

    public TransportListenerOptions(IServiceProvider serviceProvider)
    {
        _applicationServices = serviceProvider;
    }

    public string? TransportName { get; set; }
    public EndPoint? Endpoint { get; set; }
    public IConfigurationBuilder Configuration { get; } = new ConfigurationBuilder();

    IServiceProvider IMessageTransportBuilder.ApplicationServices => _applicationServices;
    bool IMessageTransportBuilder.IsServer => true;
    /*public IMessageTransportBuilder AddMiddleware(Func<MessageTransport, MessageTransport> middleware)
    {
        _middleware.Add(middleware ?? throw new ArgumentNullException(nameof(middleware)));
        return this;
    }

    internal MessageTransport ApplyMiddleware(MessageTransport transport)
    {
        if (_middleware is { Count: > 0 })
        {
            var middleware = new List<Func<MessageTransport, MessageTransport>>(_middleware);
            middleware.Reverse();
            foreach (var middlewareDelegate in middleware)
            {
                transport = middlewareDelegate(transport);
            }
        }

        return transport;
    }
    */
}

internal abstract class ServiceProviderOptionsFactory<T> : OptionsFactory<T> where T : class
{
    private readonly IServiceProvider _serviceProvider;

    public ServiceProviderOptionsFactory(IServiceProvider serviceProvider, IEnumerable<IConfigureOptions<T>> setups, IEnumerable<IPostConfigureOptions<T>> postConfigures) : base(setups, postConfigures)
    {
        _serviceProvider = serviceProvider;
    }

    protected override T CreateInstance(string name) => CreateInstanceInner(name, _serviceProvider);

    public abstract T CreateInstanceInner(string name, IServiceProvider serviceProvider);
}

internal sealed class TransportListenerOptionsFactory : ServiceProviderOptionsFactory<TransportListenerOptions>
{
    public TransportListenerOptionsFactory(IServiceProvider serviceProvider, IEnumerable<IConfigureOptions<TransportListenerOptions>> setups, IEnumerable<IPostConfigureOptions<TransportListenerOptions>> postConfigures) : base(serviceProvider, setups, postConfigures)
    {
    }

    public override TransportListenerOptions CreateInstanceInner(string name, IServiceProvider serviceProvider) => new TransportListenerOptions(serviceProvider);
}

internal sealed class TransportFactoryOptionsFactory : ServiceProviderOptionsFactory<TransportFactoryOptions>
{
    public TransportFactoryOptionsFactory(IServiceProvider serviceProvider, IEnumerable<IConfigureOptions<TransportFactoryOptions>> setups, IEnumerable<IPostConfigureOptions<TransportFactoryOptions>> postConfigures) : base(serviceProvider, setups, postConfigures)
    {
    }

    public override TransportFactoryOptions CreateInstanceInner(string name, IServiceProvider serviceProvider) => new TransportFactoryOptions(serviceProvider);
}
