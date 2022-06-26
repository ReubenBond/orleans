using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Networking.Transport;

namespace Orleans.Runtime.Messaging;

internal abstract class ConnectionFactory
{
    private readonly EndpointConfigurationProvider _endpointProvider;
    private readonly IMessageTransportFactoryProvider[] _transportFactoryProviders;
    private readonly IOptionsMonitor<TransportFactoryOptions> _transportFactoryOptions;

    protected ConnectionFactory(
        EndpointConfigurationProvider endpointConfigurationProvider,
        IEnumerable<IMessageTransportFactoryProvider> transportFactoryProviders,
        IOptionsMonitor<TransportFactoryOptions> transportFactoryOptions)
    {
        _endpointProvider = endpointConfigurationProvider;
        _transportFactoryProviders = transportFactoryProviders.ToArray();
        _transportFactoryOptions = transportFactoryOptions;
    }

    protected abstract Connection CreateConnection(SiloAddress address, MessageTransport context);

    public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
    {
        GetTransportFactory(address, out var endpointConfiguration, out var transportFactory);

        // Connect to the endpoint.
        var transport = await transportFactory.CreateAsync(endpointConfiguration, cancellationToken);

        var options = _transportFactoryOptions.Get(endpointConfiguration.EndpointName);
        foreach (var middleware in options.Middleware)
        {
            transport = middleware(transport);
        }

        // Create a connection object to represent the connection.
        var connection = CreateConnection(address, transport);
        return connection;
    }

    protected virtual void GetTransportFactory(SiloAddress address, out MessageTransportFactory transportFactory)
    {
        // Find an appropriate endpoint for the peer.
        var endpoints = _endpointProvider.GetEndpoints(address).ToList();

        if (endpoints is { Count: > 0 })
        {
            throw new KeyNotFoundException($"Could not find an endpoint for peer {address}");
        }

        // Get the first endpoint which a registered transport factory claims to support.
        endpointConfiguration = null;
        transportFactory = null;
        foreach (var endpoint in endpoints)
        {
            if (TryGetTransportFactory(endpoint, out transportFactory))
            {
                endpointConfiguration = endpoint;
                break;
            }
        }

        if (endpointConfiguration is null)
        {
            throw new KeyNotFoundException($"Could not find an endpoint for peer {address}");
        }
    }

    protected virtual bool TryGetTransportFactory(EndpointInfo endpoint, out MessageTransportFactory transportFactory)
    {
        foreach (var provider in _transportFactoryProviders)
        {
            if (provider.TryGetMessageTransportFactory(endpoint, out transportFactory))
            {
                return true;
            }
        }

        transportFactory = null;
        return false;
    }
}
