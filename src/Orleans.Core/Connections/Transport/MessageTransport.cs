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
using Orleans.Networking.Transport.Security;
using Orleans.Networking.Transport.Sockets;
using Orleans.Runtime;

namespace Orleans.Networking.Transport;

public abstract class MessageTransport : IAsyncDisposable
{
    public virtual CancellationToken Closed { get; }
    public virtual EndPoint? LocalEndpoint { get; set; }
    public virtual EndPoint? RemoteEndpoint { get; set; }
    public virtual bool IsValid => !Closed.IsCancellationRequested;
    public abstract bool ReadAsync(ReadRequest request);
    public abstract bool WriteAsync(WriteRequest request);
    public abstract ValueTask CloseAsync(Exception? closeException);
    public abstract IFeatureCollection Features { get; }
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

public interface IMessageTransportFactoryProvider
{
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

public abstract class MessageTransportFactory : IAsyncDisposable
{
    public abstract ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default);
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return default;
    }
}

public interface IMessageTransportListenerProvider
{
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
        var tlsOptions = _tlsOptions.CurrentValue;
        var endpoint = (IPEndPoint)listenOptions.Endpoint!;
        if (tlsOptions.EnableTransportLayerSecurity)
        {
            listener = new TlsMessageTransportListener(endpoint, _tlsOptions.CurrentValue, _loggerFactory);
            return true;
        }
        else
        {
            listener = new TcpMessageTransportListener(endpoint, _loggerFactory);
            return true;
        }
    }
}

public interface IMessageTransportBuilder
{
    public bool IsServer { get; }
    IServiceProvider ApplicationServices { get; }
    IMessageTransportBuilder AddMiddleware(Func<MessageTransport, MessageTransport> middleware);
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
                endpoint,
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
    private readonly IServiceProvider _applicationServices;
    private readonly List<Func<MessageTransport, MessageTransport>> _middleware = new();

    public TransportListenerOptions(IServiceProvider applicationServices)
    {
        _applicationServices = applicationServices;
        Configuration = new ConfigurationBuilder();
    }

    public string? TransportName { get; set; }
    public EndPoint? Endpoint { get; set; }
    public IConfigurationBuilder Configuration { get; }

    IServiceProvider IMessageTransportBuilder.ApplicationServices => _applicationServices;
    bool IMessageTransportBuilder.IsServer => true;
    public IMessageTransportBuilder AddMiddleware(Func<MessageTransport, MessageTransport> middleware)
    {
        _middleware.Add(middleware ?? throw new ArgumentNullException(nameof(middleware)));
        return this;
    }
}
